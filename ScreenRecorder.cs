using OpenCvSharp;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NAudio.Wave;

namespace bug_reporter;

public class ScreenRecorder : IDisposable
{
    public event Action<string, TaskCompletionSource<string>>? RecordingProcessingStarted;

    private readonly object _stateLock = new();
    private Thread? _recordingThread;
    private bool _isRecording = false;
    private bool _isStopping = false;
    private volatile bool _shouldStop = false;
    private string _videosFolder;
    private int _recordingFps = 30;
    private Screen? _selectedScreen;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveFileWriter? _waveFileWriter;
    private readonly object _retrospectiveBufferLock = new();
    private readonly Queue<BufferedFrame> _recentFrames = new();
    private readonly Queue<BufferedAudioChunk> _recentAudioChunks = new();
    private int _retrospectiveDurationSeconds = 15;
    private Thread? _retrospectiveThread;
    private volatile bool _retrospectiveEnabled = true;
    private WasapiLoopbackCapture? _retrospectiveLoopbackCapture;
    private WaveFormat? _retrospectiveWaveFormat;
    private string _outputResolutionPreset = "1080p";
    private string _encodingQualityPreset = "Balanced";
    private int _retrospectiveCaptureFailureCount = 0;
    private long _lastRetrospectiveFailureLogTick = 0;

    private const int RetrospectiveFailureLogIntervalMs = 2000;
    private const int RetrospectiveFailureBackoffMs = 120;

    private const int EnumCurrentSettings = -1;
    private const int Srccopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    public ScreenRecorder()
    {
        Logger.Instance.Log("Recorder pipeline: v2 (timing+color+loopback mux)");

        _videosFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "ScreenRecordings"
        );

        // Create videos folder if it doesn't exist
        if (!Directory.Exists(_videosFolder))
        {
            Directory.CreateDirectory(_videosFolder);
        }
        
        // Default to primary screen
        _selectedScreen = Screen.PrimaryScreen;

        StartRetrospectiveCapture();
    }

    public void SetOutputFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;
        _videosFolder = folder;
        if (!Directory.Exists(_videosFolder))
            Directory.CreateDirectory(_videosFolder);
    }

    public string GetOutputFolder() => _videosFolder;

    public void SetSelectedScreen(Screen screen)
    {
        _selectedScreen = screen;
    }

    public Screen? GetSelectedScreen()
    {
        return _selectedScreen;
    }

    public bool IsRecording
    {
        get
        {
            lock (_stateLock)
            {
                return _isRecording;
            }
        }
    }

    public bool IsStopping
    {
        get
        {
            lock (_stateLock)
            {
                return _isStopping;
            }
        }
    }

    public bool StartRecording()
    {
        lock (_stateLock)
        {
            if (_isRecording || _isStopping)
            {
                Logger.Instance.Log("Start ignored: recorder is already running or stopping.");
                return false;
            }

            Logger.Instance.Log("StartRecording called");
            _isRecording = true;
            _isStopping = false;
            _shouldStop = false;

            _recordingThread = new Thread(RecordingLoop)
            {
                IsBackground = true,
                Name = "ScreenRecorderLoop"
            };
            _recordingThread.Start();
        }

        Logger.Instance.Log("Recording thread started.");
        return true;
    }

    public bool StopRecording()
    {
        lock (_stateLock)
        {
            if (!_isRecording)
            {
                Logger.Instance.Log("Stop ignored: recorder is not running.");
                return false;
            }

            Logger.Instance.Log("StopRecording called");
            _shouldStop = true;
            _isRecording = false;
            _isStopping = true;
        }

        Logger.Instance.Log("Stop signal sent.");
        return true;
    }

    public int RetrospectiveDurationSeconds => Volatile.Read(ref _retrospectiveDurationSeconds);

    public int RecordingFps => Volatile.Read(ref _recordingFps);

    public void SetRecordingFps(int fps)
    {
        int normalizedFps = Math.Clamp(fps, 5, 60);
        Volatile.Write(ref _recordingFps, normalizedFps);
        Logger.Instance.Log($"Recording FPS updated to {normalizedFps}.");
    }

    public void SetRetrospectiveDurationSeconds(int seconds)
    {
        int normalizedSeconds = Math.Clamp(seconds, 5, 120);
        Volatile.Write(ref _retrospectiveDurationSeconds, normalizedSeconds);
        Logger.Instance.Log($"Retrospective duration updated to {normalizedSeconds} seconds.");
    }

    public void SetOutputResolutionPreset(string preset)
    {
        _outputResolutionPreset = NormalizeResolutionPreset(preset);
        Logger.Instance.Log($"Output resolution preset set to {_outputResolutionPreset}.");
    }

    public void SetEncodingQualityPreset(string preset)
    {
        _encodingQualityPreset = NormalizeQualityPreset(preset);
        Logger.Instance.Log($"Encoding quality preset set to {_encodingQualityPreset}.");
    }

    public bool SaveLast15Seconds()
    {
        return SaveRecentClip();
    }

    public bool SaveRecentClip()
    {
        return SaveRetrospectiveClip(TimeSpan.FromSeconds(RetrospectiveDurationSeconds));
    }

    private bool SaveRetrospectiveClip(TimeSpan duration)
    {
        DateTime snapshotTime = DateTime.UtcNow;
        DateTime cutoff = snapshotTime - duration;
        BufferedFrame[] frames;
        BufferedAudioChunk[] audioChunks;
        WaveFormat? waveFormatCopy;

        lock (_retrospectiveBufferLock)
        {
            frames = _recentFrames.Where(frame => frame.Timestamp >= cutoff).ToArray();
            audioChunks = _recentAudioChunks.Where(chunk => chunk.Timestamp >= cutoff).ToArray();
            waveFormatCopy = CloneWaveFormat(_retrospectiveWaveFormat);
        }

        if (frames.Length == 0)
        {
            Logger.Instance.Log("Retrospective save ignored: retrospective buffer is still empty.");
            return false;
        }

        Logger.Instance.Log($"Saving retrospective clip with {frames.Length} buffered frames.");
        _ = Task.Run(() => PersistRetrospectiveClip(frames, audioChunks, waveFormatCopy));
        return true;
    }

    private void StartRetrospectiveCapture()
    {
        StartRetrospectiveAudioCapture();

        _retrospectiveThread = new Thread(RetrospectiveCaptureLoop)
        {
            IsBackground = true,
            Name = "RetrospectiveCaptureLoop"
        };
        _retrospectiveThread.Start();

        Logger.Instance.Log("Retrospective buffer started.");
    }

    private void RetrospectiveCaptureLoop()
    {
        while (_retrospectiveEnabled)
        {
            int clipFps = Math.Max(5, RecordingFps);
            TimeSpan frameInterval = TimeSpan.FromMilliseconds(1000.0 / clipFps);
            Stopwatch iterationTimer = Stopwatch.StartNew();

            try
            {
                Screen screen = _selectedScreen ?? Screen.PrimaryScreen!;
                Rectangle captureBounds = GetCaptureBounds(screen);
                OpenCvSharp.Size outputSize = GetCaptureOutputSize(captureBounds.Width, captureBounds.Height);
                using Bitmap bitmap = CaptureScreen(captureBounds.Width, captureBounds.Height, captureBounds.X, captureBounds.Y);
                byte[] imageBytes = EncodeBufferedFrame(bitmap, outputSize);
                DateTime capturedAt = DateTime.UtcNow;

                lock (_retrospectiveBufferLock)
                {
                    _recentFrames.Enqueue(new BufferedFrame(capturedAt, imageBytes));
                    TrimRetrospectiveBufferUnsafe(capturedAt);
                }

                int recoveredFailures = Interlocked.Exchange(ref _retrospectiveCaptureFailureCount, 0);
                if (recoveredFailures > 0)
                {
                    Logger.Instance.Log($"Retrospective capture recovered after {recoveredFailures} consecutive failures.");
                }
            }
            catch (Exception ex)
            {
                int failures = Interlocked.Increment(ref _retrospectiveCaptureFailureCount);
                long nowTick = Environment.TickCount64;
                long previousLogTick = Interlocked.Read(ref _lastRetrospectiveFailureLogTick);
                bool shouldLog = failures == 1 || (nowTick - previousLogTick) >= RetrospectiveFailureLogIntervalMs;
                if (shouldLog)
                {
                    Interlocked.Exchange(ref _lastRetrospectiveFailureLogTick, nowTick);
                    Logger.Instance.Log($"Retrospective frame capture failing (x{failures}): {ex.Message}");
                }

                Thread.Sleep(RetrospectiveFailureBackoffMs);
            }

            double remainingMilliseconds = frameInterval.TotalMilliseconds - iterationTimer.Elapsed.TotalMilliseconds;
            if (remainingMilliseconds > 1)
            {
                Thread.Sleep((int)remainingMilliseconds);
            }
        }
    }

    private byte[] EncodeBufferedFrame(Bitmap bitmap, OpenCvSharp.Size outputSize)
    {
        using Mat frame = BitmapToMatFast(bitmap);
        using Mat frameToEncode = ResizeFrameIfNeeded(frame, outputSize);

        Cv2.ImEncode(
            ".jpg",
            frameToEncode,
            out byte[] encoded,
            new[] { (int)ImwriteFlags.JpegQuality, 85 }
        );

        return encoded;
    }

    private void StartRetrospectiveAudioCapture()
    {
        try
        {
            _retrospectiveLoopbackCapture = new WasapiLoopbackCapture();
            _retrospectiveWaveFormat = CloneWaveFormat(_retrospectiveLoopbackCapture.WaveFormat);

            _retrospectiveLoopbackCapture.DataAvailable += (s, e) =>
            {
                byte[] audioBytes = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, audioBytes, 0, e.BytesRecorded);
                DateTime capturedAt = DateTime.UtcNow;

                lock (_retrospectiveBufferLock)
                {
                    _recentAudioChunks.Enqueue(new BufferedAudioChunk(capturedAt, audioBytes));
                    TrimRetrospectiveBufferUnsafe(capturedAt);
                }
            };

            _retrospectiveLoopbackCapture.StartRecording();
            Logger.Instance.Log("Retrospective audio buffer started.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Retrospective audio capture failed: {ex.Message}");
        }
    }

    private void TrimRetrospectiveBufferUnsafe(DateTime referenceTime)
    {
        DateTime cutoff = referenceTime - TimeSpan.FromSeconds(RetrospectiveDurationSeconds + 2);

        while (_recentFrames.Count > 0 && _recentFrames.Peek().Timestamp < cutoff)
        {
            _recentFrames.Dequeue();
        }

        while (_recentAudioChunks.Count > 0 && _recentAudioChunks.Peek().Timestamp < cutoff)
        {
            _recentAudioChunks.Dequeue();
        }
    }

    private WaveFormat? CloneWaveFormat(WaveFormat? waveFormat)
    {
        if (waveFormat == null)
        {
            return null;
        }

        return WaveFormat.CreateCustomFormat(
            waveFormat.Encoding,
            waveFormat.SampleRate,
            waveFormat.Channels,
            waveFormat.AverageBytesPerSecond,
            waveFormat.BlockAlign,
            waveFormat.BitsPerSample
        );
    }

    private void PersistRetrospectiveClip(BufferedFrame[] frames, BufferedAudioChunk[] audioChunks, WaveFormat? waveFormat)
    {
        int clipFps = Math.Max(5, RecordingFps);
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        string rawVideoPath = Path.Combine(_videosFolder, $"instant_replay_{timestamp}_raw.avi");
        string audioPath = Path.Combine(_videosFolder, $"instant_replay_{timestamp}.wav");
        string finalVideoPath = Path.Combine(_videosFolder, $"instant_replay_{timestamp}.mp4");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyRecordingProcessingStarted(finalVideoPath, tcs);

        try
        {
            bool wroteVideo = WriteRetrospectiveVideo(frames, rawVideoPath, clipFps);
            bool wroteAudio = WriteRetrospectiveAudio(audioChunks, waveFormat, audioPath);

            if (!wroteVideo)
            {
                Logger.Instance.Log("Retrospective save failed: no video data was written.");
                tcs.TrySetException(new Exception("No video data was written."));
                return;
            }

            bool savedOutput = false;

            if (wroteAudio)
            {
                savedOutput = MergeAudioVideo(rawVideoPath, audioPath, finalVideoPath);
            }
            else
            {
                savedOutput = ConvertVideoToMp4(rawVideoPath, finalVideoPath, clipFps);
                if (savedOutput && File.Exists(rawVideoPath))
                {
                    File.Delete(rawVideoPath);
                }
            }

            if (savedOutput)
            {
                if (!wroteAudio && File.Exists(audioPath))
                {
                    File.Delete(audioPath);
                }

                Logger.Instance.Log($"Retrospective clip saved to: {finalVideoPath}");
                tcs.TrySetResult(finalVideoPath);
                return;
            }

            Logger.Instance.Log("Retrospective clip save failed during final encoding. Temporary files were kept.");
            tcs.TrySetException(new Exception("Processing failed."));
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Error saving retrospective clip: {ex.Message}");
            tcs.TrySetException(ex);
        }
    }

    private bool WriteRetrospectiveVideo(BufferedFrame[] frames, string rawVideoPath, int fps)
    {
        if (frames.Length == 0)
        {
            return false;
        }

        using MemoryStream firstFrameStream = new MemoryStream(frames[0].ImageBytes);
        using Bitmap firstBitmap = new Bitmap(firstFrameStream);
        using VideoWriter videoWriter = new VideoWriter(
            rawVideoPath,
            FourCC.MJPG,
            fps,
            new OpenCvSharp.Size(firstBitmap.Width, firstBitmap.Height)
        );

        if (!videoWriter.IsOpened())
        {
            Logger.Instance.Log("Retrospective video writer failed to open.");
            return false;
        }

        DateTime firstTimestamp = frames[0].Timestamp;
        int writtenFrames = 0;

        foreach (BufferedFrame frame in frames)
        {
            using MemoryStream frameStream = new MemoryStream(frame.ImageBytes);
            using Bitmap bitmap = new Bitmap(frameStream);
            using Mat mat = BitmapToMatFast(bitmap);

            int targetWrittenFrames = (int)Math.Floor((frame.Timestamp - firstTimestamp).TotalSeconds * fps) + 1;
            int framesToWrite = Math.Max(1, targetWrittenFrames - writtenFrames);

            for (int i = 0; i < framesToWrite; i++)
            {
                videoWriter.Write(mat);
                writtenFrames++;
            }
        }

        // Store the actual video duration for audio sync
        TimeSpan videoDuration = frames.Length > 0 ? frames[frames.Length - 1].Timestamp - frames[0].Timestamp : TimeSpan.Zero;
        Logger.Instance.Log($"Retrospective video duration: {videoDuration.TotalSeconds:F2} seconds ({frames.Length} frames at {fps} fps).");

        return true;
    }

    private bool WriteRetrospectiveAudio(BufferedAudioChunk[] audioChunks, WaveFormat? waveFormat, string audioPath)
    {
        if (waveFormat == null || audioChunks.Length == 0)
        {
            return false;
        }

        using WaveFileWriter writer = new WaveFileWriter(audioPath, waveFormat);
        foreach (BufferedAudioChunk audioChunk in audioChunks)
        {
            writer.Write(audioChunk.AudioBytes, 0, audioChunk.AudioBytes.Length);
        }

        return true;
    }

    private void RecordingLoop()
    {
        VideoWriter? videoWriter = null;
        string? rawVideoPath = null;
        string? audioPath = null;
        string? finalVideoPath = null;
        bool processingStartedFired = false;
        bool completedFired = false;
        TaskCompletionSource<string>? tcs = null;
        int darkFrameSamples = 0;
        int sampledFrames = 0;

        try
        {
            Logger.Instance.Log("RecordingLoop started");
            
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            rawVideoPath = Path.Combine(_videosFolder, $"screen_recording_{timestamp}_raw.avi");
            audioPath = Path.Combine(_videosFolder, $"screen_recording_{timestamp}.wav");
            finalVideoPath = Path.Combine(_videosFolder, $"screen_recording_{timestamp}.mp4");

            Logger.Instance.Log($"Video path: {finalVideoPath}");

            // Get selected screen bounds
            if (_selectedScreen == null)
            {
                Logger.Instance.Log("No screen selected, using primary screen");
                _selectedScreen = Screen.PrimaryScreen;
            }

            Screen screen = _selectedScreen ?? Screen.PrimaryScreen!;
            Rectangle captureBounds = GetCaptureBounds(screen);
            int screenWidth = captureBounds.Width;
            int screenHeight = captureBounds.Height;
            int screenX = captureBounds.X;
            int screenY = captureBounds.Y;
            OpenCvSharp.Size captureOutputSize = GetCaptureOutputSize(screenWidth, screenHeight);

            Logger.Instance.Log($"Screen dimensions: {screenWidth}x{screenHeight} at ({screenX}, {screenY})");
            Logger.Instance.Log($"Capture output dimensions: {captureOutputSize.Width}x{captureOutputSize.Height}");
            Logger.Instance.Log($"Encoding settings: resolution={_outputResolutionPreset}, quality={_encodingQualityPreset}, fps={RecordingFps}");

            // Create video writer for AVI
            Logger.Instance.Log("Creating video writer with MJPEG codec");
            videoWriter = new VideoWriter(
                rawVideoPath,
                FourCC.MJPG,
                RecordingFps,
                captureOutputSize
            );

            if (!videoWriter.IsOpened())
            {
                Logger.Instance.Log("ERROR: Failed to open video writer");
                return;
            }

            Logger.Instance.Log($"Video writer opened successfully");
            Logger.Instance.Log($"Recording to: {rawVideoPath}");

            // Start audio recording on a separate thread
            StartAudioRecording(audioPath);

            var frameTimer = Stopwatch.StartNew();
            int capturedFrames = 0;
            int writtenFrames = 0;
            int errors = 0;

            Logger.Instance.Log("Starting main recording loop");

            while (!_shouldStop)
            {
                try
                {
                    using Bitmap bitmap = CaptureScreen(screenWidth, screenHeight, screenX, screenY);
                    using Mat frame = BitmapToMatFast(bitmap);
                    using Mat frameToWrite = ResizeFrameIfNeeded(frame, captureOutputSize);
                    capturedFrames++;

                    if (capturedFrames % 60 == 0)
                    {
                        sampledFrames++;
                        if (IsLikelyBlackFrame(frameToWrite))
                        {
                            darkFrameSamples++;
                            Logger.Instance.Log($"Capture diagnostic: sampled frame {capturedFrames} appears nearly black.");
                        }
                    }

                    // Keep timeline accurate: write enough frames to match elapsed time.
                    int targetWrittenFrames = (int)Math.Floor(frameTimer.Elapsed.TotalSeconds * RecordingFps);
                    int framesToWrite = targetWrittenFrames - writtenFrames;
                    if (framesToWrite <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    if (framesToWrite > 5)
                    {
                        framesToWrite = 5;
                    }

                    for (int i = 0; i < framesToWrite; i++)
                    {
                        videoWriter.Write(frameToWrite);
                        writtenFrames++;
                    }

                    if (writtenFrames % 120 == 0)
                    {
                        Logger.Instance.Log($"Captured {capturedFrames} frames, wrote {writtenFrames} frames.");
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    Logger.Instance.Log($"ERROR: Capture loop failed: {ex.Message}");
                    if (errors > 10)
                    {
                        Logger.Instance.Log("Too many errors, stopping recording");
                        break;
                    }
                }
            }

            frameTimer.Stop();
            Logger.Instance.Log($"Recording loop ended. Captured {capturedFrames}, wrote {writtenFrames}.");
            if (sampledFrames > 0 && darkFrameSamples == sampledFrames)
            {
                Logger.Instance.Log("Capture diagnostic: all sampled frames were nearly black. Check desktop/session permissions, GPU acceleration, or protected content.");
            }
            Logger.Instance.Log("Releasing video writer...");

            videoWriter.Release();
            videoWriter.Dispose();
            videoWriter = null;

            // Stop audio recording
            Logger.Instance.Log("Stopping audio recording...");
            StopAudioRecording();

            // Notify UI that processing is starting so dialog can open immediately
            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            NotifyRecordingProcessingStarted(finalVideoPath!, tcs);
            processingStartedFired = true;

            // Merge audio and video
            if (File.Exists(rawVideoPath) && File.Exists(audioPath))
            {
                Logger.Instance.Log("Merging audio and video...");
                bool muxed = MergeAudioVideo(rawVideoPath, audioPath, finalVideoPath);
                if (muxed)
                {
                    Logger.Instance.Log($"Video saved to: {finalVideoPath}");
                    tcs.TrySetResult(finalVideoPath!);
                    completedFired = true;
                }
                else
                {
                    Logger.Instance.Log("Mux failed. Falling back to raw AVI file.");
                    if (File.Exists(rawVideoPath))
                    {
                        tcs.TrySetResult(rawVideoPath!);
                        completedFired = true;
                    }
                    else
                    {
                        Logger.Instance.Log("Raw AVI file also missing. Recording completely failed.");
                        tcs.TrySetException(new Exception("Processing failed."));
                        completedFired = true;
                    }
                }
            }
            else
            {
                Logger.Instance.Log("Audio/video sources missing. Skipping mux.");
                if (!string.IsNullOrWhiteSpace(rawVideoPath) && File.Exists(rawVideoPath))
                {
                    Logger.Instance.Log("Falling back to raw AVI file.");
                    tcs.TrySetResult(rawVideoPath!);
                    completedFired = true;
                }
                else
                {
                    Logger.Instance.Log("Raw AVI file missing. Recording completely failed.");
                    tcs.TrySetException(new Exception("Processing failed."));
                    completedFired = true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"CRITICAL ERROR in RecordingLoop: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            if (processingStartedFired && !completedFired)
            {
                tcs?.TrySetException(new Exception("Processing failed."));
            }

            try
            {
                videoWriter?.Dispose();
            }
            catch
            {
            }

            lock (_stateLock)
            {
                _isRecording = false;
                _isStopping = false;
                _recordingThread = null;
                _shouldStop = false;
            }

            Logger.Instance.Log("RecordingLoop exited.");
        }
    }

    private void StartAudioRecording(string audioPath)
    {
        try
        {
            _loopbackCapture = new WasapiLoopbackCapture();
            _waveFileWriter = new WaveFileWriter(audioPath, _loopbackCapture.WaveFormat);

            _loopbackCapture.DataAvailable += (s, e) =>
            {
                if (_waveFileWriter != null)
                {
                    _waveFileWriter.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            _loopbackCapture.StartRecording();
            Logger.Instance.Log("System audio (loopback) recording started.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Audio recording failed: {ex.Message}");
        }
    }

    private void StopAudioRecording()
    {
        try
        {
            _loopbackCapture?.StopRecording();
            _loopbackCapture?.Dispose();
            _loopbackCapture = null;
            _waveFileWriter?.Dispose();
            _waveFileWriter = null;
            Logger.Instance.Log("Audio recording stopped.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Error stopping audio recording: {ex.Message}");
        }
    }

    private bool MergeAudioVideo(string rawVideoPath, string audioPath, string outputPath)
    {
        try
        {
            if (!File.Exists(rawVideoPath) || !File.Exists(audioPath))
            {
                Logger.Instance.Log($"Mux inputs missing. Raw exists={File.Exists(rawVideoPath)}, Audio exists={File.Exists(audioPath)}.");
                Logger.Instance.Log($"Raw path: {rawVideoPath}");
                Logger.Instance.Log($"Audio path: {audioPath}");
                return false;
            }

            // Use FFmpeg to merge audio and video
            string ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                Logger.Instance.Log("FFmpeg not found. Unable to produce single-file output.");
                return false;
            }

            string ffmpegRawInputPath = rawVideoPath;
            string ffmpegAudioInputPath = audioPath;
            string? stagedRawPath = null;
            string? stagedAudioPath = null;

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "bug-reporter-ffmpeg");
                Directory.CreateDirectory(tempDir);

                stagedRawPath = Path.Combine(tempDir, $"mux_{Guid.NewGuid():N}_raw.avi");
                stagedAudioPath = Path.Combine(tempDir, $"mux_{Guid.NewGuid():N}.wav");

                File.Copy(rawVideoPath, stagedRawPath, true);
                File.Copy(audioPath, stagedAudioPath, true);

                ffmpegRawInputPath = stagedRawPath;
                ffmpegAudioInputPath = stagedAudioPath;
            }
            catch (Exception ex)
            {
                Logger.Instance.Log($"Mux staging copy failed, using original files: {ex.Message}");
            }

            // Validate input files before attempting mux
            bool inputsValid = ValidateInputFiles(ffmpegRawInputPath, ffmpegAudioInputPath);
            if (!inputsValid)
            {
                Logger.Instance.Log("Input validation failed. AVI or WAV file may be invalid or empty.");
                return false;
            }

            // Try preferred modern encoding first, then fallback for older FFmpeg builds.
            string filter = BuildEncodeFilter();
            (string x264Preset, int crf, int mpegQ) = GetEncodingParams();
            string[] ffmpegArguments =
            {
                $"-y -i \"{ffmpegRawInputPath}\" -i \"{ffmpegAudioInputPath}\" -vf \"{filter}\" -c:v libx264 -preset {x264Preset} -crf {crf} -color_range pc -r {RecordingFps} -c:a aac -b:a 128k -movflags +faststart -shortest \"{outputPath}\"",
                $"-y -i \"{ffmpegRawInputPath}\" -i \"{ffmpegAudioInputPath}\" -vf \"{filter}\" -c:v libx264 -preset veryfast -crf 24 -r {RecordingFps} -c:a aac -b:a 128k -movflags +faststart -shortest \"{outputPath}\"",
                $"-y -i \"{ffmpegRawInputPath}\" -i \"{ffmpegAudioInputPath}\" -vf \"{filter}\" -c:v mpeg4 -q:v {mpegQ} -r {RecordingFps} -c:a aac -shortest \"{outputPath}\""
            };

            bool muxSucceeded = false;
            foreach (string arguments in ffmpegArguments)
            {
                if (RunFfmpeg(ffmpegPath, arguments))
                {
                    muxSucceeded = true;
                    break;
                }
            }

            if (!muxSucceeded)
            {
                if (!string.IsNullOrWhiteSpace(stagedRawPath) && File.Exists(stagedRawPath))
                    File.Delete(stagedRawPath);
                if (!string.IsNullOrWhiteSpace(stagedAudioPath) && File.Exists(stagedAudioPath))
                    File.Delete(stagedAudioPath);
                return false;
            }

            // Check output file size and validate streams
            if (!File.Exists(outputPath))
            {
                Logger.Instance.Log("Mux output file was not created by FFmpeg.");
                return false;
            }

            long outputSize = new FileInfo(outputPath).Length;
            if (outputSize < 1000) // MP4 header is at least 1KB
            {
                Logger.Instance.Log($"Mux output file is suspiciously small ({outputSize} bytes). Likely invalid.");
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                return false;
            }

            if (!OutputHasMediaStreams(outputPath))
            {
                Logger.Instance.Log("Mux output validation failed: no video/audio streams detected in output file.");
                Logger.Instance.Log($"Output file size: {outputSize} bytes. FFmpeg may have produced an empty container.");
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                if (!string.IsNullOrWhiteSpace(stagedRawPath) && File.Exists(stagedRawPath))
                    File.Delete(stagedRawPath);
                if (!string.IsNullOrWhiteSpace(stagedAudioPath) && File.Exists(stagedAudioPath))
                    File.Delete(stagedAudioPath);
                return false;
            }

            // Delete temporary files only on successful mux
            if (File.Exists(rawVideoPath))
                File.Delete(rawVideoPath);
            if (File.Exists(audioPath))
                File.Delete(audioPath);
            if (!string.IsNullOrWhiteSpace(stagedRawPath) && File.Exists(stagedRawPath))
                File.Delete(stagedRawPath);
            if (!string.IsNullOrWhiteSpace(stagedAudioPath) && File.Exists(stagedAudioPath))
                File.Delete(stagedAudioPath);

            Logger.Instance.Log("Audio and video merged successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Error merging audio and video: {ex.Message}");
            return false;
        }
    }

    private bool ValidateInputFiles(string aviPath, string wavPath)
    {
        try
        {
            // Check AVI file size
            long aviSize = new FileInfo(aviPath).Length;
            if (aviSize < 1000)
            {
                Logger.Instance.Log($"Input AVI file is suspiciously small ({aviSize} bytes). Likely no frames captured.");
                return false;
            }
            Logger.Instance.Log($"Input AVI file size: {aviSize} bytes. Valid.");

            // Check WAV file size
            long wavSize = new FileInfo(wavPath).Length;
            
            // If WAV is nearly empty (just header, no audio data), create a silent replacement
            if (wavSize < 100)
            {
                Logger.Instance.Log($"Input WAV file is suspiciously small ({wavSize} bytes). Creating silent audio fallback.");
                CreateSilentWavFile(wavPath);
                return true; // Continue with silent audio
            }
            
            Logger.Instance.Log($"Input WAV file size: {wavSize} bytes. Valid.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Error validating input files: {ex.Message}");
            return false;
        }
    }

    private void CreateSilentWavFile(string wavPath)
    {
        try
        {
            // Create silent audio matching retrospective buffer duration (default 5-15 seconds)
            var waveFormat = new WaveFormat(44100, 16, 1); // 44.1kHz, 16-bit, mono
            using var waveFileWriter = new WaveFileWriter(wavPath, waveFormat);
            
            // Write silence for the retrospective buffer duration
            // Default is 5-15 seconds; creating 15 seconds to be safe
            int silenceDurationSeconds = Math.Max(5, RetrospectiveDurationSeconds);
            byte[] silence = new byte[waveFormat.AverageBytesPerSecond * silenceDurationSeconds];
            waveFileWriter.Write(silence, 0, silence.Length);
            
            Logger.Instance.Log($"Created silent audio fallback ({silence.Length} bytes, {silenceDurationSeconds} seconds).");
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Error creating silent WAV file: {ex.Message}");
        }
    }

    private bool OutputHasMediaStreams(string outputPath)
    {
        try
        {
            string ffprobePath = FindFFprobe();
            if (string.IsNullOrWhiteSpace(ffprobePath))
            {
                Logger.Instance.Log("FFprobe not found. Skipping mux stream validation.");
                return true;
            }

            var psi = new ProcessStartInfo(ffprobePath,
                $"-v error -show_entries stream=codec_type -of csv=p=0 \"{outputPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                Logger.Instance.Log("FFprobe process failed to start for mux validation.");
                return false;
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Logger.Instance.Log($"FFprobe stream validation failed with exit code {process.ExitCode}: {error}");
                return false;
            }

            string normalized = output.ToLowerInvariant();
            return normalized.Contains("video") || normalized.Contains("audio");
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Mux output validation error: {ex.Message}");
            return false;
        }
    }

    private bool ConvertVideoToMp4(string rawVideoPath, string outputPath, int fps)
    {
        try
        {
            string ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                Logger.Instance.Log("FFmpeg not found. Unable to transcode retrospective clip.");
                return false;
            }

            string[] ffmpegArguments =
            {
                $"-y -i \"{rawVideoPath}\" -vf \"{BuildEncodeFilter()}\" -c:v libx264 -preset {GetEncodingParams().x264Preset} -crf {GetEncodingParams().crf} -color_range pc -r {fps} -movflags +faststart \"{outputPath}\"",
                $"-y -i \"{rawVideoPath}\" -vf \"{BuildEncodeFilter()}\" -c:v libx264 -preset veryfast -crf 24 -r {fps} -movflags +faststart \"{outputPath}\"",
                $"-y -i \"{rawVideoPath}\" -vf \"{BuildEncodeFilter()}\" -c:v mpeg4 -q:v {GetEncodingParams().mpegQ} -r {fps} \"{outputPath}\""
            };

            foreach (string arguments in ffmpegArguments)
            {
                if (RunFfmpeg(ffmpegPath, arguments))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Error converting video to MP4: {ex.Message}");
        }

        return false;
    }

    private bool RunFfmpeg(string ffmpegPath, string arguments)
    {
        // Always log the command for diagnostics
        Logger.Instance.Log($"[FFmpeg] Running: {ffmpegPath} {arguments}");

        var processInfo = new ProcessStartInfo(ffmpegPath, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        if (process == null)
        {
            Logger.Instance.Log("FFmpeg process failed to start.");
            return false;
        }

        // Read output asynchronously to prevent deadlock from buffer overflow
        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());
        
        process.WaitForExit();

        string ffmpegOutput = stdoutTask.Result;
        string ffmpegError = stderrTask.Result;

        // Log stderr if significant (but don't log massive encoding progress output)
        if (!string.IsNullOrEmpty(ffmpegError))
        {
            // Only log first 500 chars of stderr to avoid bloating logs with progress output
            string errorSnippet = ffmpegError.Length > 500 ? ffmpegError.Substring(0, 500) + "..." : ffmpegError;
            Logger.Instance.Log($"[FFmpeg stderr] {errorSnippet}");
        }

        if (process.ExitCode != 0)
        {
            Logger.Instance.Log($"FFmpeg command failed with exit code {process.ExitCode}");
            return false;
        }

        Logger.Instance.Log("FFmpeg command completed successfully.");
        return true;
    }

    private void NotifyRecordingProcessingStarted(string expectedPath, TaskCompletionSource<string> tcs)
    {
        try
        {
            RecordingProcessingStarted?.Invoke(expectedPath, tcs);
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"RecordingProcessingStarted handler failed: {ex.Message}");
            tcs.TrySetException(ex);
        }
    }

    private string FindFFmpeg()
    {
        string appDir = AppContext.BaseDirectory;
        // Check common FFmpeg locations
        string[] ffmpegPaths = new[]
        {
            Path.Combine(appDir, "ffmpeg.exe"),
            Path.Combine(appDir, "tools", "ffmpeg.exe"),
            Path.Combine(appDir, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(appDir, "tools", "ffmpeg", "bin", "ffmpeg.exe"),
            "ffmpeg.exe",
            "C:\\Program Files\\ffmpeg\\bin\\ffmpeg.exe",
            "C:\\Program Files (x86)\\ffmpeg\\bin\\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg\\bin\\ffmpeg.exe")
        };

        foreach (var path in ffmpegPaths)
        {
            if (File.Exists(path))
                return path;
        }

        // Try to find in PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var dir in pathEnv.Split(';'))
            {
                var ffmpegPath = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                    return ffmpegPath;
            }
        }

        // Try resolving through shell PATH resolution using where.exe
        try
        {
            var processInfo = new ProcessStartInfo("where.exe", "ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process != null)
            {
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    string firstPath = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                    if (File.Exists(firstPath))
                    {
                        return firstPath;
                    }
                }
            }
        }
        catch
        {
            // Ignore and fall through to empty result.
        }

        return string.Empty;
    }

    private string FindFFprobe()
    {
        string appDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(appDir, "ffprobe.exe"),
            Path.Combine(appDir, "tools", "ffprobe.exe"),
            Path.Combine(appDir, "ffmpeg", "bin", "ffprobe.exe"),
            Path.Combine(appDir, "tools", "ffmpeg", "bin", "ffprobe.exe"),
            "ffprobe.exe",
            "C:\\Program Files\\ffmpeg\\bin\\ffprobe.exe",
            "C:\\Program Files (x86)\\ffmpeg\\bin\\ffprobe.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg\\bin\\ffprobe.exe")
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string directory in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory.Trim(), "ffprobe.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return string.Empty;
    }

    private static bool IsLikelyBlackFrame(Mat frame)
    {
        if (frame.Empty())
            return true;

        Scalar mean = Cv2.Mean(frame);
        double avgBrightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
        return avgBrightness < 3.0;
    }

    private Bitmap CaptureScreen(int width, int height, int x, int y)
    {
        Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        IntPtr hdcDest = graphics.GetHdc();
        IntPtr hdcSrc = IntPtr.Zero;

        try
        {
            // Use BitBlt + CAPTUREBLT for better compatibility with fullscreen/layered windows.
            hdcSrc = GetDC(IntPtr.Zero);
            bool copied = hdcSrc != IntPtr.Zero && BitBlt(
                hdcDest,
                0,
                0,
                width,
                height,
                hdcSrc,
                x,
                y,
                Srccopy | CaptureBlt
            );

            // Fallback path if BitBlt fails.
            if (!copied)
            {
                graphics.ReleaseHdc(hdcDest);
                hdcDest = IntPtr.Zero;
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
            }
        }
        finally
        {
            if (hdcSrc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, hdcSrc);
            }

            if (hdcDest != IntPtr.Zero)
            {
                graphics.ReleaseHdc(hdcDest);
            }
        }

        return bitmap;
    }

    private Rectangle GetCaptureBounds(Screen screen)
    {
        if (TryGetPhysicalScreenBounds(screen, out Rectangle physicalBounds))
        {
            return physicalBounds;
        }

        return screen.Bounds;
    }

    private bool TryGetPhysicalScreenBounds(Screen screen, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;

        try
        {
            DevMode devMode = new DevMode
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (short)Marshal.SizeOf<DevMode>()
            };

            if (!EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref devMode))
            {
                return false;
            }

            if (devMode.dmPelsWidth <= 0 || devMode.dmPelsHeight <= 0)
            {
                return false;
            }

            bounds = new Rectangle(devMode.dmPositionX, devMode.dmPositionY, devMode.dmPelsWidth, devMode.dmPelsHeight);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Could not query physical monitor bounds for {screen.DeviceName}: {ex.Message}");
            return false;
        }
    }

    private Mat BitmapToMatFast(Bitmap bitmap)
    {
        try
        {
            // Lock the bitmap bits directly
            System.Drawing.Imaging.BitmapData bmpData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb
            );

            try
            {
                using Mat bgraMat = new Mat(bitmap.Height, bitmap.Width, MatType.CV_8UC4, bmpData.Scan0, bmpData.Stride);
                Mat bgrMat = new Mat();
                Cv2.CvtColor(bgraMat, bgrMat, ColorConversionCodes.BGRA2BGR);
                return bgrMat;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Error converting bitmap to mat: {ex.Message}");
            return new Mat(bitmap.Height, bitmap.Width, MatType.CV_8UC3);
        }
    }

    private Mat ManualRGBAtoBGR(Mat rgbaMat)
    {
        // This is no longer used - kept for compatibility
        return rgbaMat;
    }

    private Mat BitmapToMat(Bitmap bitmap)
    {
        // Create a copy of the bitmap
        Bitmap bmp = new Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.DrawImageUnscaled(bitmap, 0, 0);
        }

        // Lock the bitmap bits
        System.Drawing.Imaging.BitmapData bmpData = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb
        );

        try
        {
            // Create Mat from the bitmap data (BGR format for OpenCV)
            Mat mat = new Mat(bmp.Height, bmp.Width, MatType.CV_8UC3, bmpData.Scan0);
            
            // Convert RGB to BGR for OpenCV
            Mat bgrMat = new Mat();
            Cv2.CvtColor(mat, bgrMat, ColorConversionCodes.RGB2BGR);
            
            return bgrMat;
        }
        finally
        {
            bmp.UnlockBits(bmpData);
            bmp.Dispose();
        }
    }

    private static string NormalizeResolutionPreset(string preset)
    {
        string normalized = (preset ?? string.Empty).Trim();
        return normalized switch
        {
            "Native" => "Native",
            "2160p" => "2160p",
            "1440p" => "1440p",
            "1080p" => "1080p",
            "720p" => "720p",
            "480p" => "480p",
            _ => "1080p"
        };
    }

    private static string NormalizeQualityPreset(string preset)
    {
        string normalized = (preset ?? string.Empty).Trim();
        return normalized switch
        {
            "Fast" => "Fast",
            "Balanced" => "Balanced",
            "Quality" => "Quality",
            _ => "Balanced"
        };
    }

    private (string x264Preset, int crf, int mpegQ) GetEncodingParams()
    {
        return NormalizeQualityPreset(_encodingQualityPreset) switch
        {
            "Fast" => ("ultrafast", 30, 8),
            "Quality" => ("medium", 20, 2),
            _ => ("veryfast", 24, 4)
        };
    }

    private string BuildEncodeFilter()
    {
        string resolution = NormalizeResolutionPreset(_outputResolutionPreset);
        return resolution switch
        {
            "2160p" => "scale=3840:2160:force_original_aspect_ratio=decrease,format=yuv420p",
            "1440p" => "scale=2560:1440:force_original_aspect_ratio=decrease,format=yuv420p",
            "1080p" => "scale=1920:1080:force_original_aspect_ratio=decrease,format=yuv420p",
            "720p" => "scale=1280:720:force_original_aspect_ratio=decrease,format=yuv420p",
            "480p" => "scale=854:480:force_original_aspect_ratio=decrease,format=yuv420p",
            _ => "format=yuv420p"
        };
    }

    private OpenCvSharp.Size GetCaptureOutputSize(int sourceWidth, int sourceHeight)
    {
        string resolution = NormalizeResolutionPreset(_outputResolutionPreset);

        if (resolution == "Native")
        {
            return new OpenCvSharp.Size(sourceWidth, sourceHeight);
        }

        (int maxWidth, int maxHeight) = resolution switch
        {
            "2160p" => (3840, 2160),
            "1440p" => (2560, 1440),
            "1080p" => (1920, 1080),
            "720p" => (1280, 720),
            "480p" => (854, 480),
            _ => (sourceWidth, sourceHeight)
        };

        double scale = Math.Min(1.0, Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight));
        int targetWidth = Math.Max(2, ((int)Math.Round(sourceWidth * scale)) & ~1);
        int targetHeight = Math.Max(2, ((int)Math.Round(sourceHeight * scale)) & ~1);

        return new OpenCvSharp.Size(targetWidth, targetHeight);
    }

    private static Mat ResizeFrameIfNeeded(Mat frame, OpenCvSharp.Size targetSize)
    {
        if (frame.Width == targetSize.Width && frame.Height == targetSize.Height)
        {
            return frame.Clone();
        }

        Mat resized = new Mat();
        Cv2.Resize(frame, resized, targetSize, 0, 0, InterpolationFlags.Area);
        return resized;
    }

    public void Dispose()
    {
        StopRecording();
        _retrospectiveEnabled = false;

        try
        {
            _retrospectiveLoopbackCapture?.StopRecording();
        }
        catch
        {
        }
        
        _loopbackCapture?.Dispose();
        _waveFileWriter?.Dispose();
        _retrospectiveLoopbackCapture?.Dispose();

        Thread? recorderThread;
        lock (_stateLock)
        {
            recorderThread = _recordingThread;
        }

        if (recorderThread != null && recorderThread != Thread.CurrentThread)
        {
            recorderThread.Join(3000);
        }

        if (_retrospectiveThread != null && _retrospectiveThread != Thread.CurrentThread)
        {
            _retrospectiveThread.Join(3000);
        }
    }

    private sealed class BufferedFrame
    {
        public BufferedFrame(DateTime timestamp, byte[] imageBytes)
        {
            Timestamp = timestamp;
            ImageBytes = imageBytes;
        }

        public DateTime Timestamp { get; }

        public byte[] ImageBytes { get; }
    }

    private sealed class BufferedAudioChunk
    {
        public BufferedAudioChunk(DateTime timestamp, byte[] audioBytes)
        {
            Timestamp = timestamp;
            AudioBytes = audioBytes;
        }

        public DateTime Timestamp { get; }

        public byte[] AudioBytes { get; }
    }
}
