using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
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
    private readonly SettingsManager? _settings;
    private readonly List<ReportRecord> _records = new();
    private readonly Panel _titleBar;
    private readonly Label _folderLabel;
    private readonly Panel _leftPanel;
    private readonly Panel _rightPanel;
    private readonly Button _authDriveButton;
    private readonly Button _exportButton;
    private readonly Button _refreshButton;
    private readonly Panel _controlsPanel;
    private readonly Panel _metaPanel;
    private readonly ListBox _recordsList;
    private readonly VideoView _videoBox;
    private readonly Label _titleValue;
    private readonly Label _fileValue;
    private readonly LinkLabel _linkValue;
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

    public PreviewReportsForm(string videoFolder, SettingsManager? settings = null)
    {
        _videoFolder = videoFolder;
        _settings = settings;

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

        _titleBar = new Panel { Location = new Point(0, 0), Size = new Size(formWidth, titleHeight), BackColor = Surface };
        _titleBar.MouseDown += TitleBar_MouseDown;
        var titleLbl = RecorderForm.MkLabel("Preview Reports", 11, true, Tx); titleLbl.Location = new Point(16, 11);
        titleLbl.MouseDown += TitleBar_MouseDown;
        _folderLabel = RecorderForm.MkLabel(_videoFolder, 8, false, Tx2); _folderLabel.Location = new Point(170, 13); _folderLabel.MaximumSize = new Size(formWidth - 230, 18);

        _titleBar.Controls.AddRange(new Control[] { titleLbl, _folderLabel });

        _leftPanel = new Panel { Location = new Point(0, titleHeight), Size = new Size(leftWidth, contentHeight), BackColor = Surface };
        var leftTitle = RecorderForm.MkLabel("RECORDINGS", 8, true, Tx2); leftTitle.Location = new Point(14, 10);
        int listWidth = leftWidth - 28;
        int refreshButtonY = contentHeight - 46;
        int exportButtonY = refreshButtonY - 44;
        int authButtonY = exportButtonY - 44;
        int listHeight = authButtonY - 38;
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
        _authDriveButton = RecorderForm.MkBtn("Authenticate Drive", Surface2, listWidth, 36);
        _authDriveButton.Location = new Point(14, authButtonY);
        _authDriveButton.ForeColor = Tx2;
        _authDriveButton.Click += AuthenticateDrive_Click;
        _exportButton = RecorderForm.MkBtn("Export CSV", Green, listWidth, 36);
        _exportButton.Location = new Point(14, exportButtonY);
        _exportButton.Click += async (_, _) => await ExportRecordsCsvAsync();
        _refreshButton = RecorderForm.MkBtn("Refresh", Blue, listWidth, 36);
        _refreshButton.Location = new Point(14, refreshButtonY);
        _refreshButton.Click += async (_, _) => await RefreshRecordsAsync();
        _leftPanel.Controls.AddRange(new Control[] { leftTitle, _recordsList, _authDriveButton, _exportButton, _refreshButton });

        _rightPanel = new Panel { Location = new Point(leftWidth, titleHeight), Size = new Size(rightWidth, contentHeight), BackColor = Bg };

        _videoBox = new VideoView
        {
            Location = new Point(0, 0), Size = new Size(rightWidth, videoHeight),
            BackColor = Color.FromArgb(16, 16, 18)
        };

        _controlsPanel = new Panel { Location = new Point(0, videoHeight), Size = new Size(rightWidth, controlsHeight), BackColor = Surface };
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
        _controlsPanel.Controls.AddRange(new Control[] { _playPauseButton, _stopButton, openBtn, _seekBar, _timeLabel });

        _metaPanel = new Panel { Location = new Point(0, metaPanelY), Size = new Size(rightWidth, metaPanelHeight), BackColor = Bg };
        var tLbl = RecorderForm.MkLabel("TITLE", 8, true, Tx2); tLbl.Location = new Point(16, 10);
        _titleValue = RecorderForm.MkLabel("-", 10, true, Tx); _titleValue.Location = new Point(16, 28); _titleValue.MaximumSize = new Size(rightWidth - 30, 20);
        var fLbl = RecorderForm.MkLabel("FILE", 8, true, Tx2); fLbl.Location = new Point(16, 56);
        _fileValue = RecorderForm.MkLabel("-", 8, false, Tx2); _fileValue.Location = new Point(16, 74); _fileValue.MaximumSize = new Size(rightWidth - 30, 18);
        var lLbl = RecorderForm.MkLabel("LINK", 8, true, Tx2); lLbl.Location = new Point(16, 94);
        _linkValue = new LinkLabel
        {
            Text = "-",
            Location = new Point(16, 112),
            Size = new Size(rightWidth - 32, 18),
            Font = new Font("Segoe UI", 8),
            LinkBehavior = LinkBehavior.HoverUnderline,
            AutoEllipsis = true,
            BackColor = Bg,
            LinkColor = Blue,
            ActiveLinkColor = Green,
            VisitedLinkColor = Blue
        };
        _linkValue.Click += ReportLink_Click;

        var dLbl = RecorderForm.MkLabel("DESCRIPTION", 8, true, Tx2); dLbl.Location = new Point(16, 136);

        _sourcesTabsBar = new Panel { Location = new Point(16, 154), Size = new Size(rightWidth - 32, 30), BackColor = Bg };
        _sourcesContentPanel = new Panel { Location = new Point(16, 186), Size = new Size(rightWidth - 32, Math.Max(100, metaPanelHeight - 194)), BackColor = Bg };

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

        _metaPanel.Controls.AddRange(new Control[] { tLbl, _titleValue, fLbl, _fileValue, lLbl, _linkValue, dLbl, _sourcesTabsBar, _sourcesContentPanel });

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
        _rightPanel.Controls.Add(_loadingLabel);

        _rightPanel.Controls.AddRange(new Control[] { _videoBox, _controlsPanel, _metaPanel });

        Controls.AddRange(new Control[] { _titleBar, _leftPanel, _rightPanel });

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
        Resize += (_, _) => ApplyResponsiveLayout();
        Shown += async (_, _) =>
        {
            ApplyResponsiveLayout();
            await RefreshRecordsAsync();
        };
    }

    private void ApplyResponsiveLayout()
    {
        int formWidth = ClientSize.Width;
        int formHeight = ClientSize.Height;
        int titleHeight = 42;
        int leftWidth = 330;
        int contentHeight = Math.Max(220, formHeight - titleHeight);
        int rightWidth = Math.Max(420, formWidth - leftWidth);
        int videoHeight = (int)(contentHeight * 0.46);
        int controlsHeight = 52;
        int metaPanelY = videoHeight + controlsHeight;
        int metaPanelHeight = Math.Max(120, contentHeight - metaPanelY);

        _titleBar.Location = new Point(0, 0);
        _titleBar.Size = new Size(formWidth, titleHeight);
        _folderLabel.MaximumSize = new Size(Math.Max(120, formWidth - 230), 18);

        _leftPanel.Location = new Point(0, titleHeight);
        _leftPanel.Size = new Size(leftWidth, contentHeight);

        _rightPanel.Location = new Point(leftWidth, titleHeight);
        _rightPanel.Size = new Size(rightWidth, contentHeight);

        int listWidth = leftWidth - 28;
        int refreshButtonY = contentHeight - 46;
        int exportButtonY = refreshButtonY - 44;
        int authButtonY = exportButtonY - 44;
        int listHeight = Math.Max(120, authButtonY - 38);

        _recordsList.Location = new Point(14, 30);
        _recordsList.Size = new Size(listWidth, listHeight);
        _authDriveButton.Location = new Point(14, authButtonY);
        _authDriveButton.Size = new Size(listWidth, 36);
        _exportButton.Location = new Point(14, exportButtonY);
        _exportButton.Size = new Size(listWidth, 36);
        _refreshButton.Location = new Point(14, refreshButtonY);
        _refreshButton.Size = new Size(listWidth, 36);

        _videoBox.Location = new Point(0, 0);
        _videoBox.Size = new Size(rightWidth, videoHeight);

        _controlsPanel.Location = new Point(0, videoHeight);
        _controlsPanel.Size = new Size(rightWidth, controlsHeight);
        _seekBar.Location = new Point(292, 12);
        _seekBar.Size = new Size(Math.Max(120, rightWidth - 392), 26);
        _timeLabel.Location = new Point(Math.Max(300, rightWidth - 90), 18);

        _metaPanel.Location = new Point(0, metaPanelY);
        _metaPanel.Size = new Size(rightWidth, metaPanelHeight);
        _titleValue.MaximumSize = new Size(rightWidth - 30, 20);
        _fileValue.MaximumSize = new Size(rightWidth - 30, 18);
        _linkValue.Size = new Size(rightWidth - 32, 18);
        _sourcesTabsBar.Size = new Size(rightWidth - 32, 30);
        _sourcesContentPanel.Size = new Size(rightWidth - 32, Math.Max(100, metaPanelHeight - 194));

        _loadingLabel.Size = new Size(rightWidth, contentHeight);
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

    private async Task ExportRecordsCsvAsync()
    {
        if (!Directory.Exists(_videoFolder) || !Directory.EnumerateFiles(_videoFolder, "*.json", SearchOption.TopDirectoryOnly).Any())
        {
            MessageBox.Show(this, "No JSON report files found to export.", "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var saveDialog = new SaveFileDialog
        {
            Title = "Export reports as CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"reports-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            InitialDirectory = Directory.Exists(_videoFolder) ? _videoFolder : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (saveDialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(saveDialog.FileName))
            return;

        _loadingLabel.Text = "Exporting CSV...";
        _loadingLabel.Visible = true;

        try
        {
            int exportedCount = await Task.Run(() => WriteRecordsCsv(saveDialog.FileName)).ConfigureAwait(true);
            Logger.Instance.Log($"Preview: exported CSV with {exportedCount} JSON records to {saveDialog.FileName}");
            MessageBox.Show(this, "CSV export complete.", "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Preview: CSV export failed: {ex.Message}");
            MessageBox.Show(this, "CSV export failed. Check log for details.", "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _loadingLabel.Text = "Loading reports...";
            _loadingLabel.Visible = false;
        }
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
                Link = metadata.Link,
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
        _linkValue.Text = string.IsNullOrWhiteSpace(record.Link) ? "-" : record.Link;
        _linkValue.Enabled = Uri.TryCreate(record.Link, UriKind.Absolute, out _);
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

    private int WriteRecordsCsv(string csvPath)
    {
        var exportRecords = LoadJsonExportRecords()
            .OrderByDescending(record => record.LastWriteTime)
            .ToList();

        if (exportRecords.Count == 0)
            throw new InvalidOperationException("No JSON report records were found in the folder.");

        var headers = new List<string>
        {
            "Title",
            "Description",
            "Leave empty",
            "Leave Empty",
            "Link",
            "Leave empty",
            "Leave empty",
            "Leave empty",
            "Scene name",
            "Level area",
            "Version"
        };

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(EscapeCsvCell)));

        foreach (var record in exportRecords)
        {
            var cells = new List<string>
            {
                record.Title,
                record.Description,
                string.Empty,
                string.Empty,
                record.Link,
                string.Empty,
                string.Empty,
                string.Empty,
                record.SceneName,
                record.LevelArea,
                record.Version
            };

            sb.AppendLine(string.Join(",", cells.Select(EscapeCsvCell)));
        }

        File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
        return exportRecords.Count;
    }

    private List<ExportJsonRecord> LoadJsonExportRecords()
    {
        var records = new List<ExportJsonRecord>();

        foreach (string jsonPath in Directory.EnumerateFiles(_videoFolder, "*.json", SearchOption.TopDirectoryOnly))
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                JsonElement root = doc.RootElement;

                string title = TryGetCaseInsensitive(root, "Title", out JsonElement t)
                    ? (t.GetString() ?? string.Empty)
                    : string.Empty;
                string description = TryGetCaseInsensitive(root, "Description", out JsonElement d)
                    ? (d.GetString() ?? string.Empty)
                    : string.Empty;
                string link = TryGetCaseInsensitive(root, "link", out JsonElement l)
                    ? (l.GetString() ?? string.Empty)
                    : string.Empty;

                string sceneName = string.Empty;
                string levelArea = string.Empty;
                string version = string.Empty;

                if (TryGetCaseInsensitive(root, "Context", out JsonElement ctx) && ctx.ValueKind == JsonValueKind.Object)
                {
                    ExtractContextFields(ctx, ref sceneName, ref levelArea, ref version);
                }

                records.Add(new ExportJsonRecord
                {
                    Title = title,
                    Description = description,
                    Link = link,
                    SceneName = sceneName,
                    LevelArea = levelArea,
                    Version = version,
                    LastWriteTime = File.GetLastWriteTime(jsonPath)
                });
            }
            catch (Exception ex)
            {
                Logger.Instance.Log($"Preview CSV export: skipped invalid JSON file {jsonPath}: {ex.Message}");
            }
            finally
            {
                doc?.Dispose();
            }
        }

        return records;
    }

    private static void ExtractContextFields(JsonElement contextObj, ref string sceneName, ref string levelArea, ref string version)
    {
        foreach (JsonProperty p in contextObj.EnumerateObject())
        {
            if (TryAssignNamedField(p.Name, p.Value.ToString(), ref sceneName, ref levelArea, ref version))
                continue;

            if (p.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty child in p.Value.EnumerateObject())
                    TryAssignNamedField(child.Name, child.Value.ToString(), ref sceneName, ref levelArea, ref version);
            }
            else if (p.Value.ValueKind == JsonValueKind.String)
            {
                string normalized = NormalizePreviewText(p.Value.GetString() ?? string.Empty);
                string[] lines = normalized.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    string[] kv = line.Split('\t', 2, StringSplitOptions.TrimEntries);
                    if (kv.Length == 2)
                        TryAssignNamedField(kv[0], kv[1], ref sceneName, ref levelArea, ref version);
                }
            }
        }
    }

    private static bool TryAssignNamedField(string key, string value, ref string sceneName, ref string levelArea, ref string version)
    {
        if (string.Equals(key, "Scene name", StringComparison.OrdinalIgnoreCase))
        {
            sceneName = value ?? string.Empty;
            return true;
        }

        if (string.Equals(key, "Level area", StringComparison.OrdinalIgnoreCase))
        {
            levelArea = value ?? string.Empty;
            return true;
        }

        if (string.Equals(key, "Version", StringComparison.OrdinalIgnoreCase))
        {
            version = value ?? string.Empty;
            return true;
        }

        return false;
    }

    private static string EscapeCsvCell(string value)
    {
        string text = (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        if (text.Contains('"'))
            text = text.Replace("\"", "\"\"");
        return $"\"{text}\"";
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

    private void AuthenticateDrive_Click(object? sender, EventArgs e)
    {
        string remoteName = _settings?.GetRcloneRemoteName() ?? "gdrive";

        if (!RcloneManager.OpenAuthenticationConsole(remoteName, out string error))
        {
            Logger.Instance.Log($"Preview: failed to open rclone authentication flow: {error}");
            MessageBox.Show(this, "Could not launch rclone authentication. Ensure rclone.exe is bundled in tools/rclone.", "Authenticate Drive", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string configPath = RcloneManager.GetConfigPath();
        string info =
            $"A command window was opened for rclone setup.\n\n" +
            $"If remote '{remoteName}' exists, it will reconnect with restricted Drive scope (drive.file).\n" +
            $"If it does not exist, create a Google Drive remote with that name.\n" +
            $"Config file: {configPath}\n\n" +
            "After authentication completes, new reports will auto-upload if enabled.";

        MessageBox.Show(this, info, "Authenticate Drive", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenFile_Click(object? sender, EventArgs e)
    {
        int idx = _recordsList.SelectedIndex;
        if (idx < 0 || idx >= _records.Count) return;
        try { Process.Start(new ProcessStartInfo(_records[idx].Path) { UseShellExecute = true }); } catch { }
    }

    private void ReportLink_Click(object? sender, EventArgs e)
    {
        int idx = _recordsList.SelectedIndex;
        if (idx < 0 || idx >= _records.Count) return;

        string link = _records[idx].Link;
        if (!Uri.TryCreate(link, UriKind.Absolute, out _))
            return;

        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); } catch { }
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
        ReportMetadata? sidecar = TryReadMetadataSidecar(videoPath);
        if (sidecar != null)
            return sidecar;

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
            string link = string.Empty;
            var sources = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(comment))
            {
                try
                {
                    using JsonDocument commentJson = JsonDocument.Parse(comment);
                    JsonElement root = commentJson.RootElement;
                    if (string.IsNullOrWhiteSpace(title) && root.TryGetProperty("Title", out JsonElement t)) title = t.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(description) && root.TryGetProperty("Description", out JsonElement d)) description = d.GetString() ?? string.Empty;
                    link = TryGetCaseInsensitive(root, "link", out JsonElement l) ? (l.GetString() ?? string.Empty) : string.Empty;
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
                Link = link,
                Sources = sources
            };
        }
        catch
        {
            return new ReportMetadata();
        }
    }

    private static ReportMetadata? TryReadMetadataSidecar(string videoPath)
    {
        string jsonPath = Path.ChangeExtension(videoPath, ".json");
        if (!File.Exists(jsonPath))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement root = doc.RootElement;
            var sources = new Dictionary<string, string>();

            if (TryGetCaseInsensitive(root, "Context", out JsonElement ctx) && ctx.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in ctx.EnumerateObject())
                {
                    string val = p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        ? JsonSerializer.Serialize(p.Value, new JsonSerializerOptions { WriteIndented = true })
                        : p.Value.ToString();
                    sources[p.Name] = val;
                }
            }

            string title = TryGetCaseInsensitive(root, "Title", out JsonElement t) ? (t.GetString() ?? string.Empty) : string.Empty;
            string description = TryGetCaseInsensitive(root, "Description", out JsonElement d) ? (d.GetString() ?? string.Empty) : string.Empty;
            string link = TryGetCaseInsensitive(root, "link", out JsonElement l) ? (l.GetString() ?? string.Empty) : string.Empty;

            return new ReportMetadata
            {
                Title = title,
                Description = description,
                Link = link,
                Sources = sources
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
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
        public string Link { get; set; } = string.Empty;
        public Dictionary<string, string> Sources { get; set; } = new();
    }

    private sealed class ReportMetadata
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Link { get; set; } = string.Empty;
        public Dictionary<string, string> Sources { get; set; } = new();
    }

    private sealed class ExportJsonRecord
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Link { get; set; } = string.Empty;
        public string SceneName { get; set; } = string.Empty;
        public string LevelArea { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime LastWriteTime { get; set; }
    }
}
