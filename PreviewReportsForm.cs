using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace bug_reporter;

[DesignerCategory("")]
public class PreviewReportsForm : Form
{
    private const int EmLineScroll = 0x00B6;
    private const int SbHorz = 0;
    private const int SbVert = 1;
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    private static Color Bg       => RecorderForm.BgColor;
    private static Color Surface  => RecorderForm.SurfaceColor;
    private static Color Surface2 => RecorderForm.Surface2Color;
    private static Color Tx       => RecorderForm.TextColor;
    private static Color Tx2      => RecorderForm.Text2Color;
    private static Color Blue     => RecorderForm.BlueColor;
    private static Color Green    => RecorderForm.GreenColor;
    private static Color Red      => RecorderForm.RedColor;

    private readonly string _videoFolder;
    private readonly List<ReportRecord> _records = new();
    private readonly ListBox _recordsList;
    private readonly VideoView _videoBox;
    private readonly Label _titleValue;
    private readonly Label _fileValue;
    private readonly TextBox _descriptionBox;
    private readonly Panel _sourcesTabsBar;
    private readonly Panel _sourcesContentPanel;
    private readonly List<Button> _sourceTabButtons = new();
    private readonly Dictionary<string, Control> _sourceTabViews = new();
    private readonly Button _playPauseButton;
    private readonly Button _stopButton;
    private readonly Label _timeLabel;
    private readonly TrackBar _seekBar;
    private readonly Label _loadingLabel;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly System.Windows.Forms.Timer _positionTimer;
    private string? _currentVideoPath;
    private long _durationMs;
    private bool _isPlaying;
    private bool _isSeekDragging;
    private bool _seekUpdateInternal;
    private bool _resumeAfterSeek;

    public PreviewReportsForm(string videoFolder)
    {
        _videoFolder = videoFolder;

        Rectangle screenBounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        int formWidth = screenBounds.Width;
        int formHeight = screenBounds.Height;
        int titleHeight = 42;
        int leftWidth = 330;
        int rightWidth = formWidth - leftWidth;
        int contentHeight = formHeight - titleHeight;
        int videoHeight = (int)(contentHeight * 0.46);
        int controlsHeight = 52;
        int metaPanelY = videoHeight + controlsHeight;
        int metaPanelHeight = contentHeight - metaPanelY;

        Text = "Preview Reports";
        ClientSize = new Size(formWidth, formHeight);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        MinimumSize = new Size(1100, 760);
        WindowState = FormWindowState.Maximized;
        BackColor = Bg;

        Panel titleBar = new Panel { Location = new Point(0, 0), Size = new Size(formWidth, titleHeight), BackColor = Surface };
        titleBar.MouseDown += TitleBar_MouseDown;
        var titleLbl = RecorderForm.MkLabel("Preview Reports", 11, true, Tx); titleLbl.Location = new Point(16, 11);
        titleLbl.MouseDown += TitleBar_MouseDown;
        var folderLbl = RecorderForm.MkLabel(_videoFolder, 8, false, Tx2); folderLbl.Location = new Point(170, 13); folderLbl.MaximumSize = new Size(formWidth - 230, 18);

        var closeBtn = RecorderForm.MkBtn("✕", Color.Transparent, 44, 42);
        closeBtn.Location = new Point(formWidth - 44, 0);
        closeBtn.ForeColor = Tx2;
        closeBtn.FlatAppearance.MouseOverBackColor = Red;
        closeBtn.Click += (_, _) => Close();

        titleBar.Controls.AddRange(new Control[] { titleLbl, folderLbl, closeBtn });

        Panel leftPanel = new Panel { Location = new Point(0, titleHeight), Size = new Size(leftWidth, contentHeight), BackColor = Surface };
        var leftTitle = RecorderForm.MkLabel("RECORDINGS", 8, true, Tx2); leftTitle.Location = new Point(14, 10);
        int listWidth = leftWidth - 28;
        int refreshButtonY = contentHeight - 46;
        int listHeight = refreshButtonY - 38;
        _recordsList = new ListBox
        {
            Location = new Point(14, 30), Size = new Size(listWidth, listHeight),
            Font = new Font("Segoe UI", 9),
            BackColor = Surface2,
            ForeColor = Tx,
            BorderStyle = BorderStyle.None
        };
        _recordsList.HandleCreated += (_, _) =>
        {
            ShowScrollBar(_recordsList.Handle, SbVert, false);
            ShowScrollBar(_recordsList.Handle, SbHorz, false);
        };
        _recordsList.SelectedIndexChanged += RecordsList_SelectedIndexChanged;
        var refreshBtn = RecorderForm.MkBtn("Refresh", Blue, listWidth, 36);
        refreshBtn.Location = new Point(14, refreshButtonY);
        refreshBtn.Click += async (_, _) => await RefreshRecordsAsync();
        leftPanel.Controls.AddRange(new Control[] { leftTitle, _recordsList, refreshBtn });

        Panel rightPanel = new Panel { Location = new Point(leftWidth, titleHeight), Size = new Size(rightWidth, contentHeight), BackColor = Bg };

        _videoBox = new VideoView
        {
            Location = new Point(0, 0), Size = new Size(rightWidth, videoHeight),
            BackColor = Color.FromArgb(16, 16, 18)
        };

        Panel controls = new Panel { Location = new Point(0, videoHeight), Size = new Size(rightWidth, controlsHeight), BackColor = Surface };
        _playPauseButton = RecorderForm.MkBtn("Play", Blue, 80, 32); _playPauseButton.Location = new Point(16, 10); _playPauseButton.Click += PlayPause_Click;
        _stopButton = RecorderForm.MkBtn("Stop", Surface2, 72, 32); _stopButton.Location = new Point(104, 10); _stopButton.Click += Stop_Click;
        var openBtn = RecorderForm.MkBtn("Open File", Surface2, 100, 32); openBtn.Location = new Point(184, 10); openBtn.ForeColor = Tx2; openBtn.Click += OpenFile_Click;

        _seekBar = new TrackBar
        {
            Location = new Point(292, 12),
            Size = new Size(Math.Max(120, rightWidth - 392), 26),
            Minimum = 0,
            Maximum = 1000,
            TickStyle = TickStyle.None,
            BackColor = Surface
        };
        _seekBar.MouseDown += SeekBar_MouseDown;
        _seekBar.MouseMove += SeekBar_MouseMove;
        _seekBar.MouseUp += SeekBar_MouseUp;
        _seekBar.ValueChanged += SeekBar_ValueChanged;

        _timeLabel = RecorderForm.MkLabel("0:00 / 0:00", 9, false, Tx2); _timeLabel.Location = new Point(rightWidth - 90, 18);
        controls.Controls.AddRange(new Control[] { _playPauseButton, _stopButton, openBtn, _seekBar, _timeLabel });

        Panel metaPanel = new Panel { Location = new Point(0, metaPanelY), Size = new Size(rightWidth, metaPanelHeight), BackColor = Bg };
        var tLbl = RecorderForm.MkLabel("TITLE", 8, true, Tx2); tLbl.Location = new Point(16, 10);
        _titleValue = RecorderForm.MkLabel("-", 10, true, Tx); _titleValue.Location = new Point(16, 28); _titleValue.MaximumSize = new Size(rightWidth - 30, 20);
        var fLbl = RecorderForm.MkLabel("FILE", 8, true, Tx2); fLbl.Location = new Point(16, 56);
        _fileValue = RecorderForm.MkLabel("-", 8, false, Tx2); _fileValue.Location = new Point(16, 74); _fileValue.MaximumSize = new Size(rightWidth - 30, 18);
        var dLbl = RecorderForm.MkLabel("DESCRIPTION", 8, true, Tx2); dLbl.Location = new Point(16, 98);

        _sourcesTabsBar = new Panel { Location = new Point(16, 116), Size = new Size(rightWidth - 32, 30), BackColor = Bg };
        _sourcesContentPanel = new Panel { Location = new Point(16, 148), Size = new Size(rightWidth - 32, Math.Max(100, metaPanelHeight - 156)), BackColor = Bg };

        var descHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Bg };
        _descriptionBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.None,
            BorderStyle = BorderStyle.None,
            BackColor = Surface,
            ForeColor = Tx,
            Font = new Font("Segoe UI", 9)
        };
        EnableHiddenScrollbarScrolling(_descriptionBox);
        descHost.Controls.Add(_descriptionBox);
        AddSourceTab("description", "Description", descHost);
        ShowSourceTab("description");

        metaPanel.Controls.AddRange(new Control[] { tLbl, _titleValue, fLbl, _fileValue, dLbl, _sourcesTabsBar, _sourcesContentPanel });

        _loadingLabel = new Label
        {
            Text = "Loading reports...",
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            ForeColor = Tx2,
            BackColor = Bg,
            Size = new Size(rightWidth, contentHeight),
            Location = new Point(0, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false
        };
        rightPanel.Controls.Add(_loadingLabel);

        rightPanel.Controls.AddRange(new Control[] { _videoBox, controls, metaPanel });

        Controls.AddRange(new Control[] { titleBar, leftPanel, rightPanel });

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
                SeekToMs(_durationMs);
            }));
        };
        _videoBox.MediaPlayer = _mediaPlayer;

        _positionTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _positionTimer.Tick += (_, _) => SyncUiToPlayer();
        _positionTimer.Start();

        FormClosing += PreviewReportsForm_FormClosing;
        Shown += async (_, _) => await RefreshRecordsAsync();
    }

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
    }

    private void AddSourceTab(string key, string title, Control content)
    {
        int index = _sourceTabButtons.Count;
        var btn = RecorderForm.MkBtn(title, Surface, 146, 26);
        btn.Location = new Point(index * 150, 2);
        btn.ForeColor = Tx2;
        btn.Tag = key;
        btn.Click += (_, _) => ShowSourceTab(key);
        _sourcesTabsBar.Controls.Add(btn);
        _sourceTabButtons.Add(btn);

        content.Visible = false;
        _sourcesContentPanel.Controls.Add(content);
        _sourceTabViews[key] = content;
    }

    private void ShowSourceTab(string key)
    {
        foreach (var kv in _sourceTabViews)
            kv.Value.Visible = string.Equals(kv.Key, key, StringComparison.Ordinal);

        foreach (Button b in _sourceTabButtons)
        {
            bool selected = string.Equals((string?)b.Tag, key, StringComparison.Ordinal);
            b.BackColor = selected ? Surface2 : Surface;
            b.ForeColor = selected ? Tx : Tx2;
        }
    }

    private async Task RefreshRecordsAsync()
    {
        _loadingLabel.Visible = true;
        _recordsList.Enabled = false;

        var loaded = await Task.Run(() => ScanRecords()).ConfigureAwait(true);

        _records.Clear();
        _recordsList.Items.Clear();
        foreach (var rec in loaded)
        {
            _records.Add(rec);
            _recordsList.Items.Add($"{File.GetLastWriteTime(rec.Path):MM-dd HH:mm}  {rec.Title}");
        }

        if (_records.Count > 0)
            _recordsList.SelectedIndex = 0;

        _recordsList.Enabled = true;
        _loadingLabel.Visible = false;
    }

    public async void RefreshReports()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshReports);
            return;
        }

        try
        {
            await RefreshRecordsAsync();
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Preview refresh failed: {ex.Message}");
        }
    }

    private List<ReportRecord> ScanRecords()
    {
        var result = new List<ReportRecord>();

        if (!Directory.Exists(_videoFolder))
        {
            Logger.Instance.Log($"Preview: folder not found: {_videoFolder}");
            return result;
        }

        IEnumerable<string> files = Directory.EnumerateFiles(_videoFolder, "*.mp4", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTime);

        foreach (string file in files)
        {
            ReportMetadata metadata = ReadMetadata(file);
            var rec = new ReportRecord
            {
                Path = file,
                FileName = Path.GetFileName(file),
                Title = string.IsNullOrWhiteSpace(metadata.Title) ? Path.GetFileNameWithoutExtension(file) : metadata.Title,
                Description = metadata.Description,
                Sources = metadata.Sources
            };
            result.Add(rec);
        }
        return result;
    }

    private void RecordsList_SelectedIndexChanged(object? sender, EventArgs e)
    {
        int idx = _recordsList.SelectedIndex;
        if (idx < 0 || idx >= _records.Count) return;
        BindRecord(_records[idx]);
    }

    private void BindRecord(ReportRecord record)
    {
        _titleValue.Text = record.Title;
        _fileValue.Text = record.Path;
        _descriptionBox.Text = record.Description ?? string.Empty;

        // Keep the first tab (description), clear all dynamic source tabs.
        for (int i = _sourceTabButtons.Count - 1; i >= 1; i--)
        {
            _sourcesTabsBar.Controls.Remove(_sourceTabButtons[i]);
            _sourceTabButtons[i].Dispose();
            _sourceTabButtons.RemoveAt(i);
        }

        var extraKeys = _sourceTabViews.Keys.Where(k => !string.Equals(k, "description", StringComparison.Ordinal)).ToList();
        foreach (string key in extraKeys)
        {
            var view = _sourceTabViews[key];
            _sourcesContentPanel.Controls.Remove(view);
            view.Dispose();
            _sourceTabViews.Remove(key);
        }

        foreach (var kv in record.Sources)
        {
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Bg };
            var txt = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.None,
                WordWrap = true,
                BorderStyle = BorderStyle.None,
                BackColor = Surface,
                ForeColor = Tx,
                Font = new Font("Consolas", 8.5f),
                Text = NormalizePreviewText(kv.Value)
            };
            EnableHiddenScrollbarScrolling(txt);
            host.Controls.Add(txt);
            string tabName = kv.Key.Length > 18 ? kv.Key[..18] + "..." : kv.Key;
            AddSourceTab(kv.Key, tabName, host);
        }

        ShowSourceTab("description");

        InitCapture(record.Path);
    }

    private void InitCapture(string path)
    {
        StopPlayback();
        _currentVideoPath = path;
        _durationMs = 0;

        try
        {
            _mediaPlayer.Media?.Dispose();
            var media = new Media(_libVlc, new Uri(path));
            media.Parse(MediaParseOptions.ParseLocal);
            _durationMs = Math.Max(0, media.Duration);
            _mediaPlayer.Media = media;
            _mediaPlayer.Play();
            _mediaPlayer.SetPause(true);

            UpdateSeekBarRange();
            SeekToMs(0);
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Preview: capture init failed: {ex.Message}");
        }
    }

    private void PlayPause_Click(object? sender, EventArgs e)
    {
        if (_isPlaying) PausePlayback(); else StartPlayback();
    }

    private void Stop_Click(object? sender, EventArgs e)
    {
        StopPlayback();
        SeekToMs(0);
    }

    private void StartPlayback()
    {
        if (_mediaPlayer.Media == null) return;
        if (_durationMs > 0 && _mediaPlayer.Time >= _durationMs)
        {
            SeekToMs(0);
        }

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
            _durationMs = reportedLength;
            UpdateSeekBarRange();
        }

        if (_isSeekDragging) return;

        long current = Math.Max(0, _mediaPlayer.Time);
        UpdateSeekBarValue(current);
        UpdateTimeLabel(current);
    }

    private void UpdateTimeLabel(long currentMs)
    {
        _timeLabel.Text = $"{FmtMs(currentMs)} / {FmtMs(_durationMs)}";
    }

    private void UpdateSeekBarRange()
    {
        _seekUpdateInternal = true;
        _seekBar.Minimum = 0;
        _seekBar.Maximum = (int)Math.Max(1, Math.Min(int.MaxValue - 1, _durationMs));
        _seekBar.Value = Math.Max(_seekBar.Minimum, Math.Min(_seekBar.Maximum, _seekBar.Value));
        _seekUpdateInternal = false;
    }

    private void UpdateSeekBarValue(long currentMs)
    {
        if (_isSeekDragging) return;
        int clamped = Math.Max(_seekBar.Minimum, Math.Min(_seekBar.Maximum, (int)Math.Min(int.MaxValue - 1, Math.Max(0, currentMs))));
        if (_seekBar.Value == clamped) return;
        _seekUpdateInternal = true;
        _seekBar.Value = clamped;
        _seekUpdateInternal = false;
    }

    private void SeekBar_MouseDown(object? sender, MouseEventArgs e)
    {
        _isSeekDragging = true;
        _resumeAfterSeek = _isPlaying;
        if (_isPlaying)
        {
            PausePlayback();
        }

        SeekBar_SetFromMouseX(e.X);
    }

    private void SeekBar_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_isSeekDragging || e.Button != MouseButtons.Left) return;
        SeekBar_SetFromMouseX(e.X);
    }

    private void SeekBar_MouseUp(object? sender, MouseEventArgs e)
    {
        SeekBar_SetFromMouseX(e.X);
        _isSeekDragging = false;
        if (_resumeAfterSeek)
        {
            StartPlayback();
        }

        _resumeAfterSeek = false;
    }

    private void SeekBar_ValueChanged(object? sender, EventArgs e)
    {
        if (_seekUpdateInternal) return;
        if (_isSeekDragging)
        {
            SeekToMs(_seekBar.Value);
        }
    }

    private void SeekToMs(long targetMs)
    {
        if (_mediaPlayer.Media == null) return;
        long max = Math.Max(0, _durationMs);
        long clamped = Math.Max(0, Math.Min(max, targetMs));
        _mediaPlayer.Time = clamped;
        UpdateSeekBarValue(clamped);
        UpdateTimeLabel(clamped);
    }

    private void SeekBar_SetFromMouseX(int mouseX)
    {
        int width = Math.Max(1, _seekBar.ClientSize.Width - 1);
        double ratio = Math.Max(0, Math.Min(1, mouseX / (double)width));
        int target = _seekBar.Minimum + (int)Math.Round(ratio * (_seekBar.Maximum - _seekBar.Minimum));

        if (_seekBar.Value == target)
        {
            SeekToMs(target);
            return;
        }

        _seekBar.Value = target;
    }

    private static string FmtMs(long ms)
    {
        long totalSeconds = Math.Max(0, ms / 1000);
        return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
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

    private void OpenFile_Click(object? sender, EventArgs e)
    {
        int idx = _recordsList.SelectedIndex;
        if (idx < 0 || idx >= _records.Count) return;
        try { Process.Start(new ProcessStartInfo(_records[idx].Path) { UseShellExecute = true }); } catch { }
    }

    private void PreviewReportsForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _positionTimer.Stop();
        StopPlayback();
        _mediaPlayer.Media?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private static ReportMetadata ReadMetadata(string videoPath)
    {
        string ffprobe = FindFfprobePath();
        if (string.IsNullOrWhiteSpace(ffprobe))
            return new ReportMetadata();

        string args = $"-v quiet -print_format json -show_format \"{videoPath}\"";
        if (!RunProcess(ffprobe, args, out string stdout, out _))
            return new ReportMetadata();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("format", out JsonElement format)) return new ReportMetadata();
            if (!format.TryGetProperty("tags", out JsonElement tags) || tags.ValueKind != JsonValueKind.Object) return new ReportMetadata();

            string title = GetTag(tags, "title");
            string description = GetTag(tags, "description");
            string comment = GetTag(tags, "comment");
            var sources = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(comment))
            {
                try
                {
                    using JsonDocument commentJson = JsonDocument.Parse(comment);
                    JsonElement root = commentJson.RootElement;
                    if (string.IsNullOrWhiteSpace(title) && root.TryGetProperty("Title", out JsonElement t)) title = t.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(description) && root.TryGetProperty("Description", out JsonElement d)) description = d.GetString() ?? string.Empty;
                    if (root.TryGetProperty("Context", out JsonElement ctx) && ctx.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty p in ctx.EnumerateObject())
                        {
                            string val = p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                                ? JsonSerializer.Serialize(p.Value, new JsonSerializerOptions { WriteIndented = true })
                                : p.Value.ToString();
                            sources[p.Name] = val;
                        }
                    }
                }
                catch
                {
                    sources["comment"] = comment;
                }
            }

            return new ReportMetadata
            {
                Title = title,
                Description = description,
                Sources = sources
            };
        }
        catch
        {
            return new ReportMetadata();
        }
    }

    private static string GetTag(JsonElement tags, string key)
    {
        foreach (JsonProperty p in tags.EnumerateObject())
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string FindFfprobePath()
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

        foreach (string c in candidates)
            if (File.Exists(c)) return c;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string probe = Path.Combine(dir.Trim(), "ffprobe.exe");
                if (File.Exists(probe)) return probe;
            }
        }

        return string.Empty;
    }

    private static bool RunProcess(string fileName, string arguments, out string stdOut, out string stdErr)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null)
        {
            stdOut = string.Empty;
            stdErr = "Process failed to start";
            return false;
        }

        stdOut = p.StandardOutput.ReadToEnd();
        stdErr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private sealed class ReportRecord
    {
        public string Path { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public Dictionary<string, string> Sources { get; set; } = new();
    }

    private sealed class ReportMetadata
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, string> Sources { get; set; } = new();
    }
}
