using System.Windows.Forms;
using System.Drawing;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bug_reporter;

[DesignerCategory("")]
public partial class RecorderForm : Form
{
    private const int EmLineScroll = 0x00B6;
    private const int EnumCurrentSettings = -1;
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
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

    // ── Theme ─────────────────────────────────────────────────────────────────
    internal static readonly Color BgColor       = Color.FromArgb(28,  28,  30);
    internal static readonly Color SurfaceColor  = Color.FromArgb(44,  44,  46);
    internal static readonly Color Surface2Color = Color.FromArgb(58,  58,  60);
    internal static readonly Color GreenColor    = Color.FromArgb(48,  209, 88);
    internal static readonly Color RedColor      = Color.FromArgb(255, 69,  58);
    internal static readonly Color OrangeColor   = Color.FromArgb(255, 159, 10);
    internal static readonly Color BlueColor     = Color.FromArgb(10,  132, 255);
    internal static readonly Color PurpleColor   = Color.FromArgb(191, 90,  242);
    internal static readonly Color TextColor     = Color.FromArgb(235, 235, 245);
    internal static readonly Color Text2Color    = Color.FromArgb(142, 142, 147);

    // ── Fields ────────────────────────────────────────────────────────────────
    private ScreenRecorder? _recorder;
    private KeyboardListener? _keyboardListener;
    private SettingsManager? _settings;
    private NotifyIcon? _notifyIcon;
    private PreviewReportsForm? _previewForm;

    private Label? _statusLabel;
    private Label? _statusDot;
    private Label? _recordingKeyLabel;
    private Label? _saveClipKeyLabel;
    private Label? _retrospectiveDurationLabel;
    private Label? _recordingFpsLabel;
    private Label? _selectedMonitorLabel;
    private Label? _instructionsLabel;
    private ComboBox? _monitorComboBox;
    private NumericUpDown? _recordingFpsInput;
    private NumericUpDown? _retrospectiveDurationInput;
    private ComboBox? _outputResolutionComboBox;
    private ComboBox? _encodingQualityComboBox;
    private Button? _startButton;
    private Button? _stopButton;
    private Button? _saveClipButton;
    private Button? _openFolderButton;
    private Button? _previewButton;
    private Button? _changeKeyButton;
    private Button? _changeSaveClipKeyButton;

    public RecorderForm()
    {
        InitializeComponent();
        SetupUI();
        SetupNotifyIcon();
        SetupRecorder();
    }

    private void InitializeComponent()
    {
        Text = "Bug Reporter";
        ClientSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        MinimumSize = new Size(820, 640);
        BackColor = BgColor;
        FormClosing += RecorderForm_FormClosing;
        Resize += RecorderForm_Resize;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    internal static Button MkBtn(string text, Color bg, int w, int h)
    {
        var b = new Button
        {
            Text = text, Size = new Size(w, h),
            BackColor = bg, ForeColor = TextColor,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    internal static Label MkLabel(string text, float sz = 9f, bool bold = false, Color? col = null) =>
        new Label
        {
            Text = text,
            Font = new Font("Segoe UI", sz, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = col ?? Text2Color,
            BackColor = Color.Transparent,
            AutoSize = true
        };

    private static Panel MkDiv(int y) =>
        new Panel { Location = new Point(0, y), Size = new Size(800, 1), BackColor = Surface2Color };

    // ── SetupUI ───────────────────────────────────────────────────────────────
    private void SetupUI()
    {
        // 1. Title bar (0-41)
        Panel titleBar = new Panel { Location = new Point(0, 0), Size = new Size(800, 42), BackColor = SurfaceColor };
        titleBar.MouseDown += TitleBar_MouseDown;

        var tbIcon = MkLabel("⏺", 13, false, RedColor);   tbIcon.Location = new Point(16, 10);
        var tbName = MkLabel("Bug Reporter", 11, true, TextColor); tbName.Location = new Point(42, 11);
        tbName.MouseDown += TitleBar_MouseDown;

        titleBar.Controls.AddRange(new Control[] { tbIcon, tbName });

        // 2. Status area (42-121)
        Panel statusPanel = new Panel { Location = new Point(0, 42), Size = new Size(800, 80), BackColor = BgColor };

        _statusDot         = MkLabel("●", 22, false, GreenColor);                                        _statusDot.Location         = new Point(24, 16);
        _statusLabel       = MkLabel("IDLE", 20, true, TextColor);                                       _statusLabel.Location       = new Point(62, 18);
        _instructionsLabel = MkLabel("Press F11 to start/stop recording  ·  Press F10 to save last 15s", 9, false, Text2Color); _instructionsLabel.Location = new Point(64, 56);

        statusPanel.Controls.AddRange(new Control[] { _statusDot, _statusLabel, _instructionsLabel });

        // 3. Config strip (123-209)
        Panel cfgPanel = new Panel { Location = new Point(0, 123), Size = new Size(800, 86), BackColor = SurfaceColor };

        var monLbl = MkLabel("MONITOR", 7.5f, true); monLbl.Location = new Point(20, 7);
        _selectedMonitorLabel = monLbl;
        _monitorComboBox = new ComboBox
        {
            Size = new Size(186, 26), Location = new Point(20, 24),
            DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 9),
            BackColor = Surface2Color, ForeColor = TextColor, FlatStyle = FlatStyle.Flat
        };
        PopulateMonitors();
        _monitorComboBox.SelectedIndexChanged += MonitorComboBox_SelectedIndexChanged;

        _recordingKeyLabel = MkLabel("RECORD KEY", 7.5f, true); _recordingKeyLabel.Location = new Point(224, 7);
        _changeKeyButton   = MkBtn("F11", Surface2Color, 76, 26); _changeKeyButton.Location  = new Point(224, 24); _changeKeyButton.ForeColor  = OrangeColor; _changeKeyButton.Click += ChangeKeyButton_Click;

        _saveClipKeyLabel        = MkLabel("CLIP KEY", 7.5f, true); _saveClipKeyLabel.Location        = new Point(316, 7);
        _changeSaveClipKeyButton = MkBtn("F10", Surface2Color, 76, 26); _changeSaveClipKeyButton.Location = new Point(316, 24); _changeSaveClipKeyButton.ForeColor = BlueColor; _changeSaveClipKeyButton.Click += ChangeSaveClipKeyButton_Click;

        _recordingFpsLabel = MkLabel("FPS", 7.5f, true); _recordingFpsLabel.Location = new Point(408, 7);
        _recordingFpsInput = new NumericUpDown { Minimum = 5, Maximum = 60, Value = 30, Size = new Size(66, 26), Location = new Point(408, 24), Font = new Font("Segoe UI", 9), BackColor = Surface2Color, ForeColor = TextColor, BorderStyle = BorderStyle.None };
        _recordingFpsInput.ValueChanged += RecordingFpsInput_ValueChanged;

        _retrospectiveDurationLabel = MkLabel("CLIP DURATION (s)", 7.5f, true); _retrospectiveDurationLabel.Location = new Point(490, 7);
        _retrospectiveDurationInput = new NumericUpDown { Minimum = 5, Maximum = 120, Value = 15, Size = new Size(66, 26), Location = new Point(490, 24), Font = new Font("Segoe UI", 9), BackColor = Surface2Color, ForeColor = TextColor, BorderStyle = BorderStyle.None };
        _retrospectiveDurationInput.ValueChanged += RetrospectiveDurationInput_ValueChanged;

        var settingsBtn = MkBtn("⚙  Context & Paths", Surface2Color, 148, 26); settingsBtn.Location = new Point(632, 24); settingsBtn.ForeColor = Text2Color; settingsBtn.Click += ContextSettingsButton_Click;
        var settingsLbl = MkLabel("SETTINGS", 7.5f, true); settingsLbl.Location = new Point(632, 7);

        var resolutionLbl = MkLabel("OUTPUT RESOLUTION", 7.5f, true); resolutionLbl.Location = new Point(20, 54);
        _outputResolutionComboBox = new ComboBox
        {
            Size = new Size(172, 24), Location = new Point(20, 58),
            DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 8.5f),
            BackColor = Surface2Color, ForeColor = TextColor, FlatStyle = FlatStyle.Flat
        };
        _outputResolutionComboBox.Items.AddRange(new object[]
        {
            "Native", "2160p", "1440p", "1080p", "720p", "480p"
        });
        _outputResolutionComboBox.SelectedIndexChanged += OutputResolutionComboBox_SelectedIndexChanged;

        var qualityLbl = MkLabel("ENCODING", 7.5f, true); qualityLbl.Location = new Point(208, 54);
        _encodingQualityComboBox = new ComboBox
        {
            Size = new Size(122, 24), Location = new Point(208, 58),
            DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 8.5f),
            BackColor = Surface2Color, ForeColor = TextColor, FlatStyle = FlatStyle.Flat
        };
        _encodingQualityComboBox.Items.AddRange(new object[]
        {
            "Fast", "Balanced", "Quality"
        });
        _encodingQualityComboBox.SelectedIndexChanged += EncodingQualityComboBox_SelectedIndexChanged;

        cfgPanel.Controls.AddRange(new Control[]
        {
            monLbl, _monitorComboBox, _recordingKeyLabel, _changeKeyButton, _saveClipKeyLabel, _changeSaveClipKeyButton,
            _recordingFpsLabel, _recordingFpsInput, _retrospectiveDurationLabel, _retrospectiveDurationInput,
            settingsLbl, settingsBtn, resolutionLbl, _outputResolutionComboBox, qualityLbl, _encodingQualityComboBox
        });

        // 4. Actions (210-273)
        Panel actPanel = new Panel { Location = new Point(0, 210), Size = new Size(800, 64), BackColor = BgColor };

        _startButton = MkBtn("▶  Start", GreenColor, 130, 40); _startButton.Location = new Point(16, 12); _startButton.Click += StartButton_Click;
        _stopButton  = MkBtn("■  Stop",  RedColor,   130, 40); _stopButton.Location  = new Point(16, 12); _stopButton.Visible = false; _stopButton.Click += StopButton_Click;
        _saveClipButton   = MkBtn("◉  Save Clip",   PurpleColor, 122, 40); _saveClipButton.Location   = new Point(154, 12); _saveClipButton.Click += SaveClipButton_Click;
        _openFolderButton = MkBtn("📁  Folder",      Surface2Color, 112, 40); _openFolderButton.Location = new Point(284, 12); _openFolderButton.ForeColor = Text2Color; _openFolderButton.Click += OpenFolderButton_Click;
        _previewButton    = MkBtn("▶  Preview",      Surface2Color, 118, 40); _previewButton.Location    = new Point(404, 12); _previewButton.ForeColor = Text2Color; _previewButton.Click += PreviewButton_Click;
        var trayBtn = MkBtn("⎕  Minimize to Tray", Surface2Color, 170, 40); trayBtn.Location = new Point(530, 12); trayBtn.ForeColor = Text2Color; trayBtn.Click += (_, _) => MinimizeToTray();

        actPanel.Controls.AddRange(new Control[] { _startButton, _stopButton, _saveClipButton, _openFolderButton, _previewButton, trayBtn });

        // 5. Log area (275-599)
        Panel logPanel = new Panel { Location = new Point(0, 275), Size = new Size(800, 325), BackColor = BgColor };
        var logLbl = MkLabel("LOGS", 7.5f, true); logLbl.Location = new Point(20, 12);

        var logBox = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.None,
            Font = new Font("Consolas", 8.5f),
            Location = new Point(20, 32), Size = new Size(760, 278),
            BackColor = SurfaceColor, ForeColor = Color.FromArgb(134, 239, 172),
            BorderStyle = BorderStyle.None
        };
        EnableHiddenScrollbarScrolling(logBox);

        logPanel.Controls.AddRange(new Control[] { logLbl, logBox });

        // Assemble
        Controls.AddRange(new Control[] { titleBar, statusPanel, MkDiv(122), cfgPanel, MkDiv(209), actPanel, MkDiv(274), logPanel });

        Logger.Instance.Subscribe(msg =>
        {
            if (logBox.InvokeRequired) logBox.Invoke(() => AppendLog(logBox, msg));
            else AppendLog(logBox, msg);
        });
        Logger.Instance.Log("Application started");
    }

    private static void AppendLog(TextBox b, string m)
    {
        const int maxLogLines = 2000;

        b.AppendText(m + Environment.NewLine);

        if (b.Lines.Length > maxLogLines)
        {
            string[] retained = b.Lines.Skip(b.Lines.Length - maxLogLines).ToArray();
            b.Lines = retained;
        }

        b.SelectionStart = b.Text.Length;
        b.ScrollToCaret();
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

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
    }

    // ── Notify icon ───────────────────────────────────────────────────────────
    private void SetupNotifyIcon()
    {
        _notifyIcon = new NotifyIcon { Icon = SystemIcons.Application, Visible = false, Text = "Bug Reporter" };
        var ctx = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show"); showItem.Click += (_, _) => ShowWindow(); ctx.Items.Add(showItem);
        ctx.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Exit"); exitItem.Click += (_, _) => { _keyboardListener?.StopListening(); _recorder?.Dispose(); Application.Exit(); }; ctx.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = ctx;
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    // ── Recorder setup ────────────────────────────────────────────────────────
    private void SetupRecorder()
    {
        _settings = new SettingsManager();
        _recorder = new ScreenRecorder();
        _recorder.RecordingProcessingStarted += Recorder_RecordingProcessingStarted;
        _keyboardListener = new KeyboardListener();

        int savedKeyCode   = _settings.GetRecordingKeyCode();
        string savedKeyName = _settings.GetRecordingKeyName();
        int recordingFps   = _settings.GetRecordingFps();
        string outputResolution = _settings.GetOutputResolutionPreset();
        string encodingQuality = _settings.GetEncodingQualityPreset();
        int savedClipKeyCode = _settings.GetSaveClipKeyCode();
        string savedClipKeyName = _settings.GetSaveClipKeyName();
        int retrospectiveDurationSeconds = _settings.GetRetrospectiveDurationSeconds();

        _keyboardListener.RecordingKeyCode = savedKeyCode;
        _keyboardListener.SaveClipKeyCode  = savedClipKeyCode;
        _recorder.SetRecordingFps(recordingFps);
        _recorder.SetOutputResolutionPreset(outputResolution);
        _recorder.SetEncodingQualityPreset(encodingQuality);
        _recorder.SetRetrospectiveDurationSeconds(retrospectiveDurationSeconds);

        string savedOutputFolder = _settings.GetOutputFolder();
        if (!string.IsNullOrWhiteSpace(savedOutputFolder))
            _recorder.SetOutputFolder(savedOutputFolder);

        _recordingFpsInput!.Value          = recordingFps;
        _retrospectiveDurationInput!.Value = retrospectiveDurationSeconds;
        _outputResolutionComboBox!.SelectedItem = outputResolution;
        _encodingQualityComboBox!.SelectedItem = encodingQuality;
        UpdateRetrospectiveUi(savedKeyName, savedClipKeyName, retrospectiveDurationSeconds);

        RestartKeyboardListener();
        UpdateUI(false);
    }

    // ── Monitors ──────────────────────────────────────────────────────────────
    private void PopulateMonitors()
    {
        _monitorComboBox!.Items.Clear();
        Screen[] screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            Rectangle bounds = GetPhysicalBounds(screens[i]);
            string name = $"Monitor {i + 1} ({bounds.Width}×{bounds.Height})";
            if (screens[i].Primary) name += " [Primary]";
            _monitorComboBox.Items.Add(new MonitorItem(screens[i], name));
        }
        _monitorComboBox.SelectedIndex = 0;
    }

    private void MonitorComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_monitorComboBox!.SelectedItem is MonitorItem item) _recorder!.SetSelectedScreen(item.Screen);
    }

    // ── Button handlers ───────────────────────────────────────────────────────
    private void ChangeKeyButton_Click(object? sender, EventArgs e)
    {
        _keyboardListener?.StopListening();
        using var dlg = new KeyConfigDialog("Configure Recording Key", "Press any key to set as recording key...");
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings!.SetRecordingKey(dlg.SelectedKeyCode, dlg.SelectedKeyName);
            _keyboardListener!.RecordingKeyCode = dlg.SelectedKeyCode;
            UpdateRetrospectiveUi(dlg.SelectedKeyName, _settings.GetSaveClipKeyName(), _settings.GetRetrospectiveDurationSeconds());
        }
        RestartKeyboardListener();
    }

    private void ChangeSaveClipKeyButton_Click(object? sender, EventArgs e)
    {
        _keyboardListener?.StopListening();
        using var dlg = new KeyConfigDialog("Configure Clip Key", "Press any key to set as the save-clip key...");
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings!.SetSaveClipKey(dlg.SelectedKeyCode, dlg.SelectedKeyName);
            _keyboardListener!.SaveClipKeyCode = dlg.SelectedKeyCode;
            UpdateRetrospectiveUi(_settings.GetRecordingKeyName(), dlg.SelectedKeyName, _settings.GetRetrospectiveDurationSeconds());
        }
        RestartKeyboardListener();
    }

    private void ContextSettingsButton_Click(object? sender, EventArgs e)
    {
        _keyboardListener?.StopListening();
        string currentFolder  = _settings!.GetOutputFolder();
        var    currentFiles   = _settings.GetContextFilePaths();
        using var dlg = new ContextSettingsDialog(currentFolder, currentFiles);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.SetOutputFolder(dlg.OutputFolder);
            _settings.SetContextFilePaths(dlg.ContextFilePaths);
            if (!string.IsNullOrWhiteSpace(dlg.OutputFolder))
                _recorder!.SetOutputFolder(dlg.OutputFolder);
            Logger.Instance.Log($"Settings saved. Output folder: {dlg.OutputFolder}. Context files: {dlg.ContextFilePaths.Count}");
        }
        RestartKeyboardListener();
    }

    private void StartButton_Click(object? sender, EventArgs e)    { if (_recorder?.StartRecording() == true) UpdateUI(true);  }
    private void StopButton_Click(object? sender, EventArgs e)     { if (_recorder?.StopRecording()  == true) UpdateUI(false); }
    private void SaveClipButton_Click(object? sender, EventArgs e) => _recorder?.SaveRecentClip();
    private void PreviewButton_Click(object? sender, EventArgs e)
    {
        string configured = _settings?.GetOutputFolder() ?? "";
        string folder = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecordings")
            : configured;

        if (_previewForm == null || _previewForm.IsDisposed)
        {
            _previewForm = new PreviewReportsForm(folder, _settings);
            _previewForm.FormClosed += (_, _) => _previewForm = null;
            _previewForm.Show(this);
            return;
        }

        if (_previewForm.WindowState == FormWindowState.Minimized)
        {
            _previewForm.WindowState = FormWindowState.Normal;
        }

        _previewForm.BringToFront();
        _previewForm.Activate();
    }

    private void RetrospectiveDurationInput_ValueChanged(object? sender, EventArgs e)
    {
        int s = (int)_retrospectiveDurationInput!.Value;
        _settings!.SetRetrospectiveDurationSeconds(s);
        _recorder!.SetRetrospectiveDurationSeconds(s);
        UpdateRetrospectiveUi(_settings.GetRecordingKeyName(), _settings.GetSaveClipKeyName(), s);
    }

    private void RecordingFpsInput_ValueChanged(object? sender, EventArgs e)
    {
        int fps = (int)_recordingFpsInput!.Value;
        _settings!.SetRecordingFps(fps);
        _recorder!.SetRecordingFps(fps);
    }

    private void OutputResolutionComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        string preset = _outputResolutionComboBox?.SelectedItem?.ToString() ?? "1080p";
        _settings!.SetOutputResolutionPreset(preset);
        _recorder!.SetOutputResolutionPreset(preset);
    }

    private void EncodingQualityComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        string preset = _encodingQualityComboBox?.SelectedItem?.ToString() ?? "Balanced";
        _settings!.SetEncodingQualityPreset(preset);
        _recorder!.SetEncodingQualityPreset(preset);
    }

    // ── Keyboard listener ─────────────────────────────────────────────────────
    private void RestartKeyboardListener()
    {
        _keyboardListener?.StartListening(
            onPressed:           () => { if (_recorder?.StartRecording() == true) UpdateUI(true);  },
            onReleased:          () => { if (_recorder?.StopRecording()  == true) UpdateUI(false); },
            onSaveClipRequested: () => _recorder?.SaveRecentClip()
        );
    }

    // ── Recorder events ───────────────────────────────────────────────────────
    private void Recorder_RecordingProcessingStarted(string expectedVideoPath, TaskCompletionSource<string> tcs)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Recorder_RecordingProcessingStarted(expectedVideoPath, tcs)); return; }
        _keyboardListener?.StopListening();
        Task<string> videoTask = tcs.Task;
        List<string> contextFiles = _settings?.GetContextFilePaths() ?? new List<string>();
        ShowWindow();
        using var dlg = new FeedbackReportDialog(expectedVideoPath, videoTask, contextFiles);
        DialogResult result = dlg.ShowDialog(this);
        if (result == DialogResult.OK)
        {
            string title = dlg.ReportTitle;
            string description = dlg.ReportDescription;
            double trimStartSeconds = dlg.TrimStartSeconds;
            double trimEndSeconds = dlg.TrimEndSeconds;
            bool hasTrimSelection = dlg.HasTrimSelection;
            _ = Task.Run(() => FinalizeSubmittedReportAsync(videoTask, title, description, hasTrimSelection, trimStartSeconds, trimEndSeconds));
        }
        else
        {
            _ = Task.Run(() => DiscardUnsubmittedReportAsync(videoTask));
        }
        if (!IsDisposed) RestartKeyboardListener();
    }

    private async Task FinalizeSubmittedReportAsync(Task<string> videoTask, string title, string description, bool hasTrimSelection, double trimStartSeconds, double trimEndSeconds)
    {
        try
        {
            string videoPath = await videoTask.ConfigureAwait(false);
            string finalizedInputPath = ApplyTrimIfRequested(videoPath, hasTrimSelection, trimStartSeconds, trimEndSeconds);
            string finalPath = ApplyReportMetadataAndRename(finalizedInputPath, title, description);
            Logger.Instance.Log($"Report submission finalized: {finalPath}");
            _ = Task.Run(() => SyncReportToGoogleDrive(finalPath, "report finalization"));
            RefreshPreviewIfOpen();
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Report submission finalization failed: {ex.Message}");
        }
    }

    private string ApplyTrimIfRequested(string videoPath, bool hasTrimSelection, double trimStartSeconds, double trimEndSeconds)
    {
        if (!hasTrimSelection)
        {
            return videoPath;
        }

        if (trimEndSeconds <= trimStartSeconds + 0.03)
        {
            Logger.Instance.Log("Trim skipped due to invalid range.");
            return videoPath;
        }

        if (!string.Equals(Path.GetExtension(videoPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Instance.Log("Trim skipped: only MP4 trimming is supported.");
            return videoPath;
        }

        string ffmpegPath = FindFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            Logger.Instance.Log("FFmpeg not found. Trim skipped.");
            return videoPath;
        }

        string tempTrimPath = Path.Combine(Path.GetDirectoryName(videoPath) ?? Path.GetTempPath(), $"trim_{Guid.NewGuid():N}.mp4");
        string start = trimStartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string end = trimEndSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string args = $"-y -i \"{videoPath}\" -ss {start} -to {end} -c:v libx264 -preset veryfast -crf 22 -c:a aac -b:a 128k \"{tempTrimPath}\"";

        if (!RunProcess(ffmpegPath, args, out string ffmpegError))
        {
            Logger.Instance.Log($"Trim failed. Using original recording: {ffmpegError}");
            if (File.Exists(tempTrimPath)) File.Delete(tempTrimPath);
            return videoPath;
        }

        if (File.Exists(videoPath)) File.Delete(videoPath);
        File.Move(tempTrimPath, videoPath, overwrite: false);
        Logger.Instance.Log($"Applied trim range: {trimStartSeconds:0.###}s - {trimEndSeconds:0.###}s");
        return videoPath;
    }

    private async Task DiscardUnsubmittedReportAsync(Task<string> videoTask)
    {
        try
        {
            string videoPath = await videoTask.ConfigureAwait(false);
            if (File.Exists(videoPath))
            {
                File.Delete(videoPath);
                Logger.Instance.Log($"Report canceled. Discarded video: {videoPath}");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Report cancel cleanup failed: {ex.Message}");
        }
    }

    private string ApplyReportMetadataAndRename(string videoPath, string title, string description)
    {
        string safeTitle  = SanitizeFileNameSegment(string.IsNullOrWhiteSpace(title) ? "Untitled Report" : title);
        string timestamp  = ExtractRecordingTimestamp(videoPath);
        string extension  = Path.GetExtension(videoPath);

        // Use configured output folder (or fall back to where the video already lives)
        string outputFolder = _settings?.GetOutputFolder() ?? "";
        if (string.IsNullOrWhiteSpace(outputFolder))
            outputFolder = Path.GetDirectoryName(videoPath) ?? Environment.CurrentDirectory;
        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        string renamedPath = GetUniqueFilePath(outputFolder, $"{safeTitle} [{timestamp}]", extension);

        // Build context JSON from configured context files
        var contextData = new Dictionary<string, object>();
        foreach (var filePath in (_settings?.GetContextFilePaths() ?? new List<string>()))
        {
            if (!File.Exists(filePath)) continue;
            try
            {
                string raw = ReadFileSafe(filePath);
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".json")
                {
                    try
                    {
                        var parsed = System.Text.Json.JsonDocument.Parse(raw);
                        contextData[Path.GetFileName(filePath)] = parsed.RootElement.Clone();
                    }
                    catch { contextData[Path.GetFileName(filePath)] = raw; }
                }
                else
                {
                    contextData[Path.GetFileName(filePath)] = raw;
                }
            }
            catch (Exception ex) { Logger.Instance.Log($"Could not read context file {filePath}: {ex.Message}"); }
        }

        File.Move(videoPath, renamedPath, overwrite: false);
        WriteReportSidecarJson(renamedPath, title, description, contextData, link: string.Empty);
        return renamedPath;
    }

    private static string ReadFileSafe(string path, int maxBytes = 65536)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] buf = new byte[Math.Min(maxBytes, fs.Length)];
        int read = fs.Read(buf, 0, buf.Length);
        return System.Text.Encoding.UTF8.GetString(buf, 0, read);
    }

    private static string SanitizeFileNameSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Untitled Report" : sanitized;
    }

    private static string GetUniqueFilePath(string directory, string baseName, string extension)
    {
        string candidate = Path.Combine(directory, $"{baseName}{extension}");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        int suffix = 2;
        while (true)
        {
            candidate = Path.Combine(directory, $"{baseName} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static string ExtractRecordingTimestamp(string videoPath)
    {
        string fileName = Path.GetFileNameWithoutExtension(videoPath);
        int underscoreIndex = fileName.LastIndexOf('_');
        string timestampPart = underscoreIndex >= 0 ? fileName[(underscoreIndex + 1)..] : fileName;

        string[] formats =
        {
            "yyyy-MM-dd_HH-mm-ss",
            "yyyy-MM-dd_HH-mm-ss-fff"
        };

        if (DateTime.TryParseExact(timestampPart, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return parsed.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        }

        DateTime createdAt = File.GetCreationTime(videoPath);
        return createdAt.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
    }

    private static string EscapeFfmpegMetadata(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    private static void WriteReportSidecarJson(string videoPath, string title, string description, Dictionary<string, object> contextData, string link)
    {
        string jsonPath = Path.ChangeExtension(videoPath, ".json");
        var root = new JsonObject
        {
            ["Title"] = title ?? string.Empty,
            ["Description"] = description ?? string.Empty,
            ["Context"] = JsonSerializer.SerializeToNode(contextData) ?? new JsonObject(),
            ["link"] = link ?? string.Empty
        };

        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
    }

    private static void UpsertReportLinkField(string videoPath, string link)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(link))
            return;

        string jsonPath = Path.ChangeExtension(videoPath, ".json");
        JsonObject root;

        try
        {
            if (File.Exists(jsonPath))
            {
                string existing = File.ReadAllText(jsonPath);
                root = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject
                {
                    ["Title"] = Path.GetFileNameWithoutExtension(videoPath),
                    ["Description"] = string.Empty,
                    ["Context"] = new JsonObject()
                };
            }
        }
        catch
        {
            root = new JsonObject
            {
                ["Title"] = Path.GetFileNameWithoutExtension(videoPath),
                ["Description"] = string.Empty,
                ["Context"] = new JsonObject()
            };
        }

        root["link"] = link;
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
    }

    private void SyncReportToGoogleDrive(string videoPath, string reason)
    {
        try
        {
            if (_settings == null)
                return;

            if (!_settings.GetAutoUploadToGoogleDrive())
                return;

            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            {
                Logger.Instance.Log($"Drive sync skipped ({reason}): report file missing: {videoPath}");
                return;
            }

            string remoteName = _settings.GetRcloneRemoteName();
            if (!RcloneManager.IsRemoteConfigured(remoteName, out string remoteError))
            {
                Logger.Instance.Log($"Drive sync skipped ({reason}): {remoteError}");
                return;
            }

            string driveFolder = _settings.GetRcloneDriveFolder();
            if (!RcloneManager.CopyFileToRemote(videoPath, remoteName, driveFolder, out string stdOut, out string stdErr))
            {
                string trimmedError = string.IsNullOrWhiteSpace(stdErr) ? "unknown error" : stdErr.Trim();
                Logger.Instance.Log($"Drive sync failed ({reason}): {trimmedError}");
                return;
            }

            string fileName = Path.GetFileName(videoPath);
            if (!RcloneManager.TryGetRemoteFileLink(remoteName, driveFolder, fileName, out string link, out string linkError))
            {
                Logger.Instance.Log($"Drive link skipped for {fileName}: {linkError}");
                return;
            }

            UpsertReportLinkField(videoPath, link);

            string jsonPath = Path.ChangeExtension(videoPath, ".json");
            if (!File.Exists(jsonPath))
            {
                Logger.Instance.Log($"Drive JSON sync skipped ({reason}): sidecar missing for {fileName}");
            }
            else if (!RcloneManager.CopyFileToRemote(jsonPath, remoteName, driveFolder, out _, out string jsonErr))
            {
                string trimmedJsonError = string.IsNullOrWhiteSpace(jsonErr) ? "unknown error" : jsonErr.Trim();
                Logger.Instance.Log($"Drive JSON re-upload failed ({reason}): {trimmedJsonError}");
            }

            if (!string.IsNullOrWhiteSpace(stdOut))
            {
                string snippet = string.Join(Environment.NewLine, stdOut
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Take(3));
                if (!string.IsNullOrWhiteSpace(snippet))
                    Logger.Instance.Log($"Drive sync completed ({reason}): {snippet}");
                else
                    Logger.Instance.Log($"Drive sync completed ({reason}).");
            }
            else
            {
                Logger.Instance.Log($"Drive sync completed ({reason}).");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Drive sync failed ({reason}): {ex.Message}");
        }
    }

    private void RefreshPreviewIfOpen()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshPreviewIfOpen);
            return;
        }

        if (_previewForm == null || _previewForm.IsDisposed) return;
        _previewForm.RefreshReports();
    }

    private static string EscapeFfmpegMetadataValue(string value)
    {
        // FFmpeg metadata file format: escape = ; # \ and flatten control chars/newlines.
        string cleaned = new string((value ?? string.Empty)
            .Where(ch => ch == '\t' || ch >= ' ')
            .ToArray());

        return cleaned
            .Replace("\\", "\\\\")
            .Replace("=", "\\=")
            .Replace(";", "\\;")
            .Replace("#", "\\#")
            .Replace("\r\n", " ")
            .Replace("\n", " ")
            .Replace("\r", " ");
    }

    private static string FindFfmpegPath()
    {
        string appDir = AppContext.BaseDirectory;
        string[] ffmpegPaths =
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

        foreach (string path in ffmpegPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string directory in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    private static bool RunProcess(string fileName, string arguments, out string stdErr)
    {
        var processInfo = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(processInfo);
        if (process == null)
        {
            stdErr = "Process failed to start.";
            return false;
        }

        stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    // ── UI state ──────────────────────────────────────────────────────────────
    private void UpdateRetrospectiveUi(string recordingKeyName, string saveClipKeyName, int durationSeconds)
    {
        _changeKeyButton!.Text         = recordingKeyName;
        _changeSaveClipKeyButton!.Text = saveClipKeyName;
        _saveClipButton!.Text          = $"◉  Save Last {durationSeconds}s";
        _instructionsLabel!.Text       = $"Press {recordingKeyName} to start/stop recording  ·  Press {saveClipKeyName} to save last {durationSeconds}s";
    }

    private void UpdateUI(bool isRecording)
    {
        if (InvokeRequired) { Invoke(() => UpdateUI(isRecording)); return; }

        if (isRecording)
        {
            _statusLabel!.Text    = "RECORDING";
            _statusDot!.ForeColor = RedColor;
            _startButton!.Visible = false;
            _stopButton!.Visible  = true;
            _changeKeyButton!.Enabled            = false;
            _changeSaveClipKeyButton!.Enabled    = false;
            _recordingFpsInput!.Enabled          = false;
            _retrospectiveDurationInput!.Enabled = false;
            _outputResolutionComboBox!.Enabled   = false;
            _encodingQualityComboBox!.Enabled    = false;
        }
        else
        {
            _statusLabel!.Text    = "IDLE";
            _statusDot!.ForeColor = GreenColor;
            _startButton!.Visible = true;
            _stopButton!.Visible  = false;
            _changeKeyButton!.Enabled            = true;
            _changeSaveClipKeyButton!.Enabled    = true;
            _recordingFpsInput!.Enabled          = true;
            _retrospectiveDurationInput!.Enabled = true;
            _outputResolutionComboBox!.Enabled   = true;
            _encodingQualityComboBox!.Enabled    = true;
        }
    }

    private static Rectangle GetPhysicalBounds(Screen screen)
    {
        try
        {
            DevMode devMode = new DevMode
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (short)Marshal.SizeOf<DevMode>()
            };

            if (EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref devMode)
                && devMode.dmPelsWidth > 0 && devMode.dmPelsHeight > 0)
            {
                return new Rectangle(devMode.dmPositionX, devMode.dmPositionY, devMode.dmPelsWidth, devMode.dmPelsHeight);
            }
        }
        catch
        {
            // Fall back to logical bounds below.
        }

        return screen.Bounds;
    }

    // ── Misc ──────────────────────────────────────────────────────────────────
    private void OpenFolderButton_Click(object? sender, EventArgs e)
    {
        string configured = _settings?.GetOutputFolder() ?? "";
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecordings")
            : configured;
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        else
            MessageBox.Show("Videos folder does not exist yet.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void MinimizeToTray() { Hide(); WindowState = FormWindowState.Minimized; _notifyIcon!.Visible = true; }
    private void ShowWindow()     { Show(); WindowState = FormWindowState.Normal;    BringToFront(); _notifyIcon!.Visible = false; }

    private void RecorderForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized) { Hide(); _notifyIcon!.Visible = true; }
    }

    private void RecorderForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _keyboardListener?.StopListening();
        if (_recorder != null)
        {
            _recorder.RecordingProcessingStarted -= Recorder_RecordingProcessingStarted;
        }
        _recorder?.Dispose();
        _notifyIcon?.Dispose();
    }
}

public class MonitorItem
{
    public Screen Screen { get; set; }
    public string DisplayName { get; set; }
    public MonitorItem(Screen screen, string displayName) { Screen = screen; DisplayName = displayName; }
    public override string ToString() => DisplayName;
}