using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace bug_reporter;

[DesignerCategory("")]
public class FeedbackReportDialog : Form
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WsExNoActivate = 0x08000000;
    private const int EmLineScroll = 0x00B6;

    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] lpKeyState);

    // Theme aliases
    private static Color Bg       => RecorderForm.BgColor;
    private static Color Surface  => RecorderForm.SurfaceColor;
    private static Color Surface2 => RecorderForm.Surface2Color;
    private static Color Tx       => RecorderForm.TextColor;
    private static Color Tx2      => RecorderForm.Text2Color;
    private static Color Blue     => RecorderForm.BlueColor;
    private static Color Green    => RecorderForm.GreenColor;
    private static Color Red      => RecorderForm.RedColor;

    private readonly Task<string> _videoTask;
    private string? _videoPath;

    private readonly VideoView _videoBox;
    private readonly Label _processingLabel;
    private readonly Button _playPauseButton;
    private readonly Button _stopPlayButton;
    private readonly Button _openButton;
    private readonly Label _timeLabel;
    private readonly TrackBar _seekBar;
    private readonly Label _trimStartLabel;
    private readonly Label _trimEndLabel;
    private readonly Panel _trimStartHandle;
    private readonly Panel _trimEndHandle;
    private readonly TextBox _titleTextBox;
    private readonly TextBox _descriptionTextBox;
    private readonly Panel _dataTabsBar;
    private readonly Panel _dataContentPanel;
    private readonly List<Button> _dataTabButtons = new();
    private readonly Dictionary<string, Control> _dataTabViews = new();
    private const int MaxPreviewChars = 12000;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly System.Windows.Forms.Timer _positionTimer;
    private long _durationMs;
    private bool _isPlaying;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _keyboardHookProc;
    private bool _globalCaptureActive;
    private Label? _captureStateLabel;
    private System.Windows.Forms.Timer? _processingAnimationTimer;
    private int _processingAnimationPhase;
    private long _trimStartMs;
    private long _trimEndMs;
    private bool _dragTrimStart;
    private bool _dragTrimEnd;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public string ReportTitle => _titleTextBox.Text.Trim();
    public string ReportDescription => _descriptionTextBox.Text.Trim();
    public double TrimStartSeconds => _seekBar.Enabled ? _trimStartMs / 1000.0 : 0;
    public double TrimEndSeconds => _seekBar.Enabled ? _trimEndMs / 1000.0 : 0;
    public bool HasTrimSelection => _seekBar.Enabled && (_trimStartMs > 0 || _trimEndMs < _seekBar.Maximum);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate;
            return cp;
        }
    }

    public FeedbackReportDialog(string expectedVideoPath, Task<string> videoTask, List<string> contextFilePaths)
    {
        _videoTask = videoTask;

        const int dialogWidth = 845;
        const int dialogHeight = 864;

        Text = "Feedback Report";
        ClientSize = new Size(dialogWidth, dialogHeight);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = true;
        MinimumSize = new Size(dialogWidth, dialogHeight);
        BackColor = Bg;
        TopMost = true;

        // ── Title bar ─────────────────────────────────────────────────────────
        Panel titleBar = new Panel { Location = new Point(0, 0), Size = new Size(dialogWidth, 42), BackColor = Surface };
        titleBar.MouseDown += TitleBar_MouseDown;
        var tbIcon = RecorderForm.MkLabel("⏺", 13, false, Red);             tbIcon.Location = new Point(16, 10);
        var tbText = RecorderForm.MkLabel("Feedback Report", 11, true, Tx); tbText.Location = new Point(42, 10);
        tbText.MouseDown += TitleBar_MouseDown;
        titleBar.Controls.AddRange(new Control[] { tbIcon, tbText });

        // ── Path info bar (42-69) ─────────────────────────────────────────────
        Panel infoBar = new Panel { Location = new Point(0, 42), Size = new Size(dialogWidth, 26), BackColor = Surface };
        var pathLbl = RecorderForm.MkLabel(expectedVideoPath, 8, false, Tx2);
        pathLbl.Location = new Point(16, 5); pathLbl.MaximumSize = new Size(dialogWidth - 30, 18);
        infoBar.Controls.Add(pathLbl);

        // ── Video area (68-339, 272px tall) ──────────────────────────────────
        _videoBox = new VideoView
        {
            Location = new Point(0, 68), Size = new Size(dialogWidth, 272),
            BackColor = Color.FromArgb(16, 16, 18)
        };

        _processingLabel = new Label
        {
            Text = "Processing recording…",
            Font = new Font("Segoe UI", 14), ForeColor = Tx2,
            BackColor = Color.FromArgb(16, 16, 18),
            Size = new Size(dialogWidth, 272), Location = new Point(0, 68),
            TextAlign = ContentAlignment.MiddleCenter
        };

        // ── Playback controls (340-387, 48px) ─────────────────────────────────
        Panel ctrlBar = new Panel { Location = new Point(0, 340), Size = new Size(dialogWidth, 48), BackColor = Surface };

        _playPauseButton = RecorderForm.MkBtn("Play",           Blue,     80, 32); _playPauseButton.Location = new Point(16, 8);  _playPauseButton.Enabled = false; _playPauseButton.Click += PlayPauseButton_Click;
        _stopPlayButton  = RecorderForm.MkBtn("Stop",           Surface2, 72, 32); _stopPlayButton.Location  = new Point(104, 8); _stopPlayButton.Enabled  = false; _stopPlayButton.Click  += StopButton_Click;
        _openButton      = RecorderForm.MkBtn("Open in Player", Surface2, 116, 32); _openButton.Location = new Point(184, 8); _openButton.ForeColor = Tx2; _openButton.Enabled = false; _openButton.Click += (_, _) => OpenVideo();

        _captureStateLabel = RecorderForm.MkLabel("Typing Capture: OFF", 8, true, Tx2);
        _captureStateLabel.Location = new Point(316, 16);
        _trimStartLabel = RecorderForm.MkLabel("Start 0:00", 8, false, Tx2); _trimStartLabel.Location = new Point(472, 16);
        _trimEndLabel = RecorderForm.MkLabel("End 0:00", 8, false, Tx2); _trimEndLabel.Location = new Point(560, 16);
        _timeLabel = RecorderForm.MkLabel("0:00 / 0:00", 9, false, Tx2); _timeLabel.Location = new Point(dialogWidth - 90, 16);
        ctrlBar.Controls.AddRange(new Control[] { _playPauseButton, _stopPlayButton, _openButton, _captureStateLabel, _trimStartLabel, _trimEndLabel, _timeLabel });

        // ── Seek bar (388-413, 26px) ──────────────────────────────────────────
        _seekBar = new TrackBar
        {
            Location = new Point(0, 388), Size = new Size(dialogWidth, 26),
            Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None,
            TabStop = false, Enabled = false, BackColor = Surface
        };
        _seekBar.Scroll += SeekBar_Scroll;

        _trimStartHandle = new Panel { Size = new Size(8, 18), BackColor = Color.FromArgb(90, 200, 120), Cursor = Cursors.SizeWE };
        _trimEndHandle = new Panel { Size = new Size(8, 18), BackColor = Color.FromArgb(255, 120, 120), Cursor = Cursors.SizeWE };
        _trimStartHandle.MouseDown += TrimStartHandle_MouseDown;
        _trimStartHandle.MouseMove += TrimStartHandle_MouseMove;
        _trimStartHandle.MouseUp += TrimStartHandle_MouseUp;
        _trimEndHandle.MouseDown += TrimEndHandle_MouseDown;
        _trimEndHandle.MouseMove += TrimEndHandle_MouseMove;
        _trimEndHandle.MouseUp += TrimEndHandle_MouseUp;

        // ── Fields panel (414-803, 390px) ─────────────────────────────────────
        Panel fieldsPanel = new Panel { Location = new Point(0, 414), Size = new Size(dialogWidth, 390), BackColor = Bg };

        var titleLbl = RecorderForm.MkLabel("TITLE", 7.5f, true); titleLbl.Location = new Point(20, 16);
        _titleTextBox = new TextBox
        {
            Location = new Point(20, 34), Size = new Size(dialogWidth - 40, 30),
            Font = new Font("Segoe UI", 10), BackColor = Surface, ForeColor = Tx, BorderStyle = BorderStyle.None
        };

        var tabLbl = RecorderForm.MkLabel("DATA SOURCES", 7.5f, true); tabLbl.Location = new Point(20, 74);
        _dataTabsBar = new Panel
        {
            Location = new Point(20, 90),
            Size = new Size(dialogWidth - 40, 30),
            BackColor = Bg
        };
        _dataContentPanel = new Panel
        {
            Location = new Point(20, 122),
            Size = new Size(dialogWidth - 40, 254),
            BackColor = Bg
        };

        var descHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Bg };
        _descriptionTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9),
            BackColor = Surface,
            ForeColor = Tx,
            BorderStyle = BorderStyle.None,
            Multiline = true,
            ScrollBars = ScrollBars.None
        };
        EnableHiddenScrollbarScrolling(_descriptionTextBox);
        descHost.Controls.Add(_descriptionTextBox);
        AddDataTab("description", "Description", descHost);
        AddContextPreviewTabs(contextFilePaths);
        ShowDataTab("description");

        fieldsPanel.Controls.AddRange(new Control[] { titleLbl, _titleTextBox, tabLbl, _dataTabsBar, _dataContentPanel });

        // ── Bottom bar (804-863, 60px) ─────────────────────────────────────────
        Panel bottomBar = new Panel { Location = new Point(0, 804), Size = new Size(dialogWidth, 60), BackColor = Surface };

        var submitBtn = RecorderForm.MkBtn("Submit", Green, 120, 34); submitBtn.Location = new Point(dialogWidth - 16 - 120 - 12 - 120, 13); submitBtn.DialogResult = DialogResult.OK;
        var cancelBtn = RecorderForm.MkBtn("Cancel", Red,   120, 34); cancelBtn.Location = new Point(dialogWidth - 16 - 120, 13); cancelBtn.DialogResult = DialogResult.Cancel;
        bottomBar.Controls.AddRange(new Control[] { submitBtn, cancelBtn });

        AcceptButton = submitBtn;
        CancelButton = cancelBtn;

        // Assemble — processingLabel added after videoBox so it sits on top
        Controls.AddRange(new Control[] { titleBar, infoBar, _videoBox, ctrlBar, _seekBar, fieldsPanel, bottomBar, _trimStartHandle, _trimEndHandle });
        Controls.Add(_processingLabel);

        Core.Initialize();
        _libVlc = new LibVLC($"--plugin-path={Path.Combine(AppContext.BaseDirectory, "plugins")}");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.EndReached += (_, _) =>
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                _isPlaying = false;
                _playPauseButton.Text = "Play";
                SeekTo(GetTrimEndMs());
            }));
        };
        _videoBox.MediaPlayer = _mediaPlayer;

        _positionTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _positionTimer.Tick += (_, _) => SyncUiToPlayer();
        _positionTimer.Start();

        Shown       += FeedbackReportDialog_Shown;
        FormClosing += FeedbackReportDialog_FormClosing;

        StartProcessingAnimation();
    }

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void FeedbackReportDialog_Shown(object? sender, EventArgs e)
    {
        // Description is the primary input target for background typing capture.
        ShowDataTab("description");
        _descriptionTextBox.Focus();
        _descriptionTextBox.SelectionStart = _descriptionTextBox.TextLength;
        _descriptionTextBox.SelectionLength = 0;
        StartGlobalKeyboardCapture();

        _videoTask.ContinueWith(t =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                if (t.IsCompletedSuccessfully) OnVideoReady(t.Result);
                else OnProcessingFailed();
            });
        }, TaskScheduler.Default);
    }

    private void FeedbackReportDialog_FormClosing(object? sender, FormClosingEventArgs e)
    {
        StopPlayback();
        _positionTimer.Stop();
        StopProcessingAnimation();
        StopGlobalKeyboardCapture();
        _mediaPlayer.Media?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private void OnVideoReady(string videoPath)
    {
        _videoPath = videoPath;
        StopProcessingAnimation();
        _processingLabel.Visible = false;
        
        // Initialize video asynchronously to prevent UI blocking
        _ = Task.Run(() => InitCaptureAsync());
    }

    private void InitCaptureAsync()
    {
        try
        {
            _mediaPlayer.Media?.Dispose();
            var media = new Media(_libVlc, new Uri(_videoPath!));
            // Parse media off the UI thread to prevent blocking
            media.Parse(MediaParseOptions.ParseLocal);
            _durationMs = Math.Max(0, media.Duration);
            
            // Switch back to UI thread for updates
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                try
                {
                    _mediaPlayer.Media = media;
                    _mediaPlayer.Play();
                    _mediaPlayer.SetPause(true);

                    _seekBar.Maximum = (int)Math.Max(1, Math.Min(int.MaxValue - 1, _durationMs));
                    _trimStartMs = 0;
                    _trimEndMs = _seekBar.Maximum;
                    UpdateTrimLabels();
                    UpdateTrimHandlePositions();
                    SeekTo(0);
                    
                    _playPauseButton.Enabled = true;
                    _stopPlayButton.Enabled  = true;
                    _openButton.Enabled      = true;
                    _seekBar.Enabled         = true;
                }
                catch (Exception ex) { Logger.Instance.Log($"FeedbackDialog: failed to set media: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Logger.Instance.Log($"FeedbackDialog: parse init failed: {ex.Message}"); }
    }

    private void OnProcessingFailed()
    {
        StopProcessingAnimation();
        _processingLabel.Text      = "Processing failed.";
        _processingLabel.ForeColor = RecorderForm.RedColor;
    }

    private void StartProcessingAnimation()
    {
        _processingAnimationPhase = 0;
        _processingLabel.Text = "Processing recording";

        _processingAnimationTimer = new System.Windows.Forms.Timer { Interval = 260 };
        _processingAnimationTimer.Tick += (_, _) =>
        {
            if (_processingLabel.IsDisposed) return;
            _processingAnimationPhase = (_processingAnimationPhase + 1) % 4;
            _processingLabel.Text = "Processing recording" + new string('.', _processingAnimationPhase);
        };
        _processingAnimationTimer.Start();
    }

    private void StopProcessingAnimation()
    {
        _processingAnimationTimer?.Stop();
        _processingAnimationTimer?.Dispose();
        _processingAnimationTimer = null;
    }

    // ── Capture / playback ────────────────────────────────────────────────────
    private void InitCapture()
    {
        try
        {
            _mediaPlayer.Media?.Dispose();
            var media = new Media(_libVlc, new Uri(_videoPath!));
            media.Parse(MediaParseOptions.ParseLocal);
            _durationMs = Math.Max(0, media.Duration);
            _mediaPlayer.Media = media;
            _mediaPlayer.Play();
            _mediaPlayer.SetPause(true);

            _seekBar.Maximum = (int)Math.Max(1, Math.Min(int.MaxValue - 1, _durationMs));
            _trimStartMs = 0;
            _trimEndMs = _seekBar.Maximum;
            UpdateTrimLabels();
            UpdateTrimHandlePositions();
            SeekTo(0);
        }
        catch (Exception ex) { Logger.Instance.Log($"FeedbackDialog: capture init failed: {ex.Message}"); }
    }

    private void PlayPauseButton_Click(object? sender, EventArgs e) { if (_isPlaying) PausePlayback(); else StartPlayback(); }
    private void StopButton_Click(object? sender, EventArgs e)      { StopPlayback(); SeekTo(GetTrimStartMs()); }

    private void StartPlayback()
    {
        if (_mediaPlayer.Media == null) return;
        long trimStart = GetTrimStartMs();
        long trimEnd = GetTrimEndMs();
        long current = Math.Max(0, _mediaPlayer.Time);
        if (current < trimStart || current > trimEnd) SeekTo(trimStart);
        _mediaPlayer.Play();
        _isPlaying = true;
        _playPauseButton.Text = "Pause";
    }

    private void PausePlayback()
    {
        if (_mediaPlayer.Media != null)
        {
            _mediaPlayer.SetPause(true);
        }

        _isPlaying = false;
        _playPauseButton.Text = "Play";
    }

    private void StopPlayback() => PausePlayback();

    private void SyncUiToPlayer()
    {
        if (_mediaPlayer.Media == null) return;

        long reportedLength = Math.Max(0, _mediaPlayer.Length);
        if (reportedLength > 0 && reportedLength != _durationMs)
        {
            int oldMax = _seekBar.Maximum;
            bool trimEndWasAtEnd = Math.Abs(_trimEndMs - oldMax) <= 2;
            _durationMs = reportedLength;
            _seekBar.Maximum = (int)Math.Max(1, Math.Min(int.MaxValue - 1, _durationMs));
            _trimStartMs = Math.Max(0, Math.Min(_trimStartMs, _seekBar.Maximum));
            if (trimEndWasAtEnd || _trimEndMs > _seekBar.Maximum)
            {
                _trimEndMs = _seekBar.Maximum;
            }
            _trimEndMs = Math.Max(_trimStartMs, Math.Min(_trimEndMs, _seekBar.Maximum));
            UpdateTrimLabels();
            UpdateTrimHandlePositions();
        }

        long current = Math.Max(0, _mediaPlayer.Time);
        long trimEnd = GetTrimEndMs();
        if (_isPlaying && current >= trimEnd)
        {
            PausePlayback();
            SeekTo(trimEnd);
            return;
        }

        _seekBar.Value = Math.Max(_seekBar.Minimum, Math.Min(_seekBar.Maximum, (int)Math.Min(int.MaxValue - 1, current)));
        UpdateTimeLabel(current);
        UpdateTrimHandlePositions();
    }

    private void SeekBar_Scroll(object? sender, EventArgs e)
    {
        int target = Math.Max((int)GetTrimStartMs(), Math.Min((int)GetTrimEndMs(), _seekBar.Value));
        if (target != _seekBar.Value)
        {
            _seekBar.Value = target;
        }

        SeekTo(target);
    }

    private void SeekTo(long fi)
    {
        if (_mediaPlayer.Media == null) return;
        int clamped = Math.Max(_seekBar.Minimum, Math.Min(_seekBar.Maximum, (int)Math.Min(int.MaxValue - 1, Math.Max(0, fi))));
        _mediaPlayer.Time = clamped;
        _seekBar.Value = clamped;
        UpdateTimeLabel(clamped);
        UpdateTrimHandlePositions();
    }

    private long GetTrimStartMs() => _seekBar.Enabled ? _trimStartMs : 0;
    private long GetTrimEndMs() => _seekBar.Enabled ? _trimEndMs : _seekBar.Maximum;

    private void UpdateTrimLabels()
    {
        _trimStartLabel.Text = $"Start {FmtMs(_trimStartMs)}";
        _trimEndLabel.Text = $"End {FmtMs(_trimEndMs)}";
    }

    private void UpdateTimeLabel(long currentMs)
    {
        _timeLabel.Text = $"{FmtMs(currentMs)} / {FmtMs(_durationMs)}";
    }

    private void UpdateTrimHandlePositions()
    {
        int max = Math.Max(1, _seekBar.Maximum - _seekBar.Minimum);
        int trackWidth = Math.Max(1, _seekBar.Width - 1);
        int startX = _seekBar.Left + (int)Math.Round(((_trimStartMs - _seekBar.Minimum) / (double)max) * trackWidth) - (_trimStartHandle.Width / 2);
        int endX = _seekBar.Left + (int)Math.Round(((_trimEndMs - _seekBar.Minimum) / (double)max) * trackWidth) - (_trimEndHandle.Width / 2);
        int y = _seekBar.Top + 4;

        _trimStartHandle.Location = new Point(startX, y);
        _trimEndHandle.Location = new Point(endX, y);
        _trimStartHandle.BringToFront();
        _trimEndHandle.BringToFront();
    }

    private long MouseToSeekMs(Control source, int mouseX)
    {
        Point screenPoint = source.PointToScreen(new Point(mouseX, 0));
        Point seekPoint = _seekBar.PointToClient(screenPoint);
        int x = Math.Max(0, Math.Min(_seekBar.Width - 1, seekPoint.X));
        double ratio = x / (double)Math.Max(1, _seekBar.Width - 1);
        return _seekBar.Minimum + (long)Math.Round(ratio * (_seekBar.Maximum - _seekBar.Minimum));
    }

    private void TrimStartHandle_MouseDown(object? sender, MouseEventArgs e)
    {
        if (!_seekBar.Enabled || e.Button != MouseButtons.Left) return;
        _dragTrimStart = true;
        _trimStartHandle.Capture = true;
    }

    private void TrimStartHandle_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragTrimStart) return;
        long target = MouseToSeekMs(_trimStartHandle, e.X);
        _trimStartMs = Math.Max(0, Math.Min(target, _trimEndMs));
        UpdateTrimLabels();
        UpdateTrimHandlePositions();
        if (_seekBar.Value < _trimStartMs)
        {
            SeekTo(_trimStartMs);
        }
    }

    private void TrimStartHandle_MouseUp(object? sender, MouseEventArgs e)
    {
        _dragTrimStart = false;
        _trimStartHandle.Capture = false;
    }

    private void TrimEndHandle_MouseDown(object? sender, MouseEventArgs e)
    {
        if (!_seekBar.Enabled || e.Button != MouseButtons.Left) return;
        _dragTrimEnd = true;
        _trimEndHandle.Capture = true;
    }

    private void TrimEndHandle_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragTrimEnd) return;
        long target = MouseToSeekMs(_trimEndHandle, e.X);
        _trimEndMs = Math.Max(_trimStartMs, Math.Min(target, _seekBar.Maximum));
        UpdateTrimLabels();
        UpdateTrimHandlePositions();
        if (_seekBar.Value > _trimEndMs)
        {
            SeekTo(_trimEndMs);
        }
    }

    private void TrimEndHandle_MouseUp(object? sender, MouseEventArgs e)
    {
        _dragTrimEnd = false;
        _trimEndHandle.Capture = false;
    }

    private static string FmtMs(long ms)
    {
        long totalSeconds = Math.Max(0, ms / 1000);
        return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
    }

    private void StartGlobalKeyboardCapture()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
            return;

        _globalCaptureActive = true;
        _keyboardHookProc = KeyboardHookCallback;
        _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, IntPtr.Zero, 0);
        _captureStateLabel!.Text = _keyboardHookHandle != IntPtr.Zero
            ? "Typing Capture: ON (Enter releases)"
            : "Typing Capture: FAILED";
    }

    private void StopGlobalKeyboardCapture()
    {
        _globalCaptureActive = false;
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }

        _captureStateLabel!.Text = "Typing Capture: OFF";
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _globalCaptureActive)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == WmKeyDown || msg == WmSysKeyDown;
            bool isKeyUp = msg == WmKeyUp || msg == WmSysKeyUp;

            if (isKeyDown)
            {
                var hook = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                Keys key = (Keys)hook.vkCode;

                if (key == Keys.Enter)
                {
                    BeginInvoke(new Action(SubmitFromGlobalCapture));
                    return (IntPtr)1;
                }

                if (key == Keys.Back)
                {
                    BeginInvoke(new Action(DeletePreviousCharacter));
                    return (IntPtr)1;
                }

                if (key == Keys.Tab)
                {
                    BeginInvoke(new Action(() => InsertAtDescriptionCaret("    ")));
                    return (IntPtr)1;
                }

                if (key == Keys.Escape)
                {
                    BeginInvoke(new Action(StopGlobalKeyboardCapture));
                    return (IntPtr)1;
                }

                string text = ConvertKeyToText(hook.vkCode, hook.scanCode);
                if (!string.IsNullOrEmpty(text))
                {
                    BeginInvoke(new Action(() => InsertAtDescriptionCaret(text)));
                    return (IntPtr)1;
                }

                return (IntPtr)1;
            }

            if (isKeyUp)
            {
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private static string ConvertKeyToText(uint vkCode, uint scanCode)
    {
        byte[] state = new byte[256];
        if (!GetKeyboardState(state))
            return string.Empty;

        var sb = new StringBuilder(8);
        int rc = ToUnicode(vkCode, scanCode, state, sb, sb.Capacity, 0);
        if (rc > 0)
            return sb.ToString();

        return string.Empty;
    }

    private void InsertAtDescriptionCaret(string text)
    {
        _descriptionTextBox.Focus();
        int selStart = _descriptionTextBox.SelectionStart;
        int selLength = _descriptionTextBox.SelectionLength;
        string source = _descriptionTextBox.Text;
        _descriptionTextBox.Text = source.Remove(selStart, selLength).Insert(selStart, text);
        _descriptionTextBox.SelectionStart = selStart + text.Length;
        _descriptionTextBox.SelectionLength = 0;
    }

    private void DeletePreviousCharacter()
    {
        _descriptionTextBox.Focus();
        int selStart = _descriptionTextBox.SelectionStart;
        int selLength = _descriptionTextBox.SelectionLength;

        if (selLength > 0)
        {
            string source = _descriptionTextBox.Text;
            _descriptionTextBox.Text = source.Remove(selStart, selLength);
            _descriptionTextBox.SelectionStart = selStart;
            _descriptionTextBox.SelectionLength = 0;
            return;
        }

        if (selStart <= 0)
            return;

        string current = _descriptionTextBox.Text;
        _descriptionTextBox.Text = current.Remove(selStart - 1, 1);
        _descriptionTextBox.SelectionStart = selStart - 1;
        _descriptionTextBox.SelectionLength = 0;
    }

    private void SubmitFromGlobalCapture()
    {
        StopGlobalKeyboardCapture();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddDataTab(string key, string title, Control content)
    {
        int index = _dataTabButtons.Count;
        var btn = RecorderForm.MkBtn(title, Surface, 146, 26);
        btn.Location = new Point(index * 150, 2);
        btn.ForeColor = Tx2;
        btn.Tag = key;
        btn.Click += (_, _) => ShowDataTab(key);
        _dataTabsBar.Controls.Add(btn);
        _dataTabButtons.Add(btn);

        content.Visible = false;
        _dataContentPanel.Controls.Add(content);
        _dataTabViews[key] = content;
    }

    private void ShowDataTab(string key)
    {
        foreach (var kv in _dataTabViews)
            kv.Value.Visible = string.Equals(kv.Key, key, StringComparison.Ordinal);

        foreach (Button b in _dataTabButtons)
        {
            bool selected = string.Equals((string?)b.Tag, key, StringComparison.Ordinal);
            b.BackColor = selected ? Surface2 : Surface;
            b.ForeColor = selected ? Tx : Tx2;
        }
    }

    private void AddContextPreviewTabs(List<string> contextFilePaths)
    {
        foreach (string path in contextFilePaths)
        {
            string fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName)) continue;

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Bg };
            var preview = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 8.5f),
                BackColor = Surface,
                ForeColor = Tx,
                BorderStyle = BorderStyle.None,
                Multiline = true,
                ScrollBars = ScrollBars.None,
                ReadOnly = true,
                WordWrap = true,
                Text = NormalizePreviewText(GetPreviewText(path))
            };
            EnableHiddenScrollbarScrolling(preview);
            host.Controls.Add(preview);
            string tabName = fileName.Length > 20 ? fileName[..20] + "..." : fileName;
            AddDataTab(path, tabName, host);
        }
    }

    private static string NormalizePreviewText(string text)
    {
        return (text ?? string.Empty)
            .Replace("\\r\\n", "\r\n")
            .Replace("\\n", "\r\n")
            .Replace("\\r", "\r\n");
    }

    private static void EnableHiddenScrollbarScrolling(TextBox box)
    {
        box.MouseWheel += (_, e) =>
        {
            int lines = e.Delta > 0 ? -3 : 3;
            SendMessage(box.Handle, EmLineScroll, 0, lines);
        };

        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.PageDown)
            {
                SendMessage(box.Handle, EmLineScroll, 0, 18);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.PageUp)
            {
                SendMessage(box.Handle, EmLineScroll, 0, -18);
                e.Handled = true;
            }
        };
    }

    private static string GetPreviewText(string path)
    {
        try
        {
            if (!File.Exists(path)) return $"File not found:\r\n{path}";

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string text = reader.ReadToEnd();
            if (text.Length > MaxPreviewChars)
            {
                return text[..MaxPreviewChars] + "\r\n\r\n... (truncated)";
            }

            return text;
        }
        catch (Exception ex)
        {
            return $"Could not preview file:\r\n{path}\r\n\r\n{ex.Message}";
        }
    }

    private void OpenVideo()
    {
        if (_videoPath == null) return;
        try { Process.Start(new ProcessStartInfo(_videoPath) { UseShellExecute = true }); } catch { }
    }
}