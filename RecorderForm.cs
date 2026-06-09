using System.Windows.Forms;
using System.Drawing;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
    private Button? _changeKeyButton;
    private Button? _changeSaveClipKeyButton;
    private ComboBox? _profileComboBox;
    private bool _suppressProfileChange;
    private Button? _micToggleButton;
    private ComboBox? _micDeviceComboBox;
    private Button? _recordModeButton;

    private bool _formShownOnce = false;

    private sealed record MicDeviceItem(string Id, string Name) { public override string ToString() => Name; }

    private const long MaxApiVideoBytes = 4 * 1024 * 1024;
    private const int MaxApiContextFileBytes = 512 * 1024; // 512 KB per context file

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

    private static Panel MkDiv() =>
        new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Surface2Color };

    // ── SetupUI ───────────────────────────────────────────────────────────────
    private void SetupUI()
    {
        // 1. Title bar – Dock.Top (added last so it appears at the top)
        Panel titleBar = new Panel { Height = 42, Dock = DockStyle.Top, BackColor = SurfaceColor };
        titleBar.MouseDown += TitleBar_MouseDown;

        var tbIcon = MkLabel("⏺", 13, false, RedColor);   tbIcon.Location = new Point(16, 10);

        titleBar.Controls.AddRange(new Control[] { tbIcon });

        // 2. Status area – Dock.Top
        Panel statusPanel = new Panel { Height = 80, Dock = DockStyle.Top, BackColor = BgColor };

        _statusDot         = MkLabel("●", 22, false, GreenColor);  _statusDot.Location         = new Point(24, 16);
        _statusLabel       = MkLabel("IDLE", 20, true, TextColor); _statusLabel.Location       = new Point(62, 18);
        _instructionsLabel = MkLabel("Press F2 to start/stop recording  ·  Press 3 to save last 10s", 9, false, Text2Color); _instructionsLabel.Location = new Point(64, 56);

        statusPanel.Controls.AddRange(new Control[] { _statusDot, _statusLabel, _instructionsLabel });

        // 3. Config strip – Dock.Top, three rows with breathing room
        Panel cfgPanel = new Panel { Height = 200, Dock = DockStyle.Top, BackColor = SurfaceColor };

        var monLbl = MkLabel("MONITOR", 7.5f, true); monLbl.Location = new Point(20, 8);
        _selectedMonitorLabel = monLbl;
        _monitorComboBox = new ComboBox
        {
            Size = new Size(186, 26), Location = new Point(20, 24),
            DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 9),
            BackColor = Surface2Color, ForeColor = TextColor, FlatStyle = FlatStyle.Flat
        };
        PopulateMonitors();
        _monitorComboBox.SelectedIndexChanged += MonitorComboBox_SelectedIndexChanged;

        _recordingKeyLabel = MkLabel("RECORD KEY", 7.5f, true); _recordingKeyLabel.Location = new Point(224, 8);
        _changeKeyButton   = MkBtn("F2", Surface2Color, 76, 26); _changeKeyButton.Location  = new Point(224, 24); _changeKeyButton.ForeColor  = OrangeColor; _changeKeyButton.Click += ChangeKeyButton_Click;

        _saveClipKeyLabel        = MkLabel("CLIP KEY", 7.5f, true); _saveClipKeyLabel.Location        = new Point(316, 8);
        _changeSaveClipKeyButton = MkBtn("3", Surface2Color, 76, 26); _changeSaveClipKeyButton.Location = new Point(316, 24); _changeSaveClipKeyButton.ForeColor = BlueColor; _changeSaveClipKeyButton.Click += ChangeSaveClipKeyButton_Click;

        _recordingFpsLabel = MkLabel("FPS", 7.5f, true); _recordingFpsLabel.Location = new Point(408, 8);
        _recordingFpsInput = new NumericUpDown { Minimum = 5, Maximum = 60, Value = 30, Size = new Size(66, 26), Location = new Point(408, 24), Font = new Font("Segoe UI", 9), BackColor = Surface2Color, ForeColor = TextColor, BorderStyle = BorderStyle.None };
        _recordingFpsInput.ValueChanged += RecordingFpsInput_ValueChanged;

        _retrospectiveDurationLabel = MkLabel("CLIP DURATION (s)", 7.5f, true); _retrospectiveDurationLabel.Location = new Point(490, 8);
        _retrospectiveDurationInput = new NumericUpDown { Minimum = 5, Maximum = 120, Value = 15, Size = new Size(66, 26), Location = new Point(490, 24), Font = new Font("Segoe UI", 9), BackColor = Surface2Color, ForeColor = TextColor, BorderStyle = BorderStyle.None };
        _retrospectiveDurationInput.ValueChanged += RetrospectiveDurationInput_ValueChanged;

        var recordModeLbl = MkLabel("RECORD MODE", 7.5f, true); recordModeLbl.Location = new Point(572, 8);
        _recordModeButton = MkBtn("TOGGLE", Surface2Color, 80, 26); _recordModeButton.Location = new Point(572, 24); _recordModeButton.ForeColor = OrangeColor; _recordModeButton.Click += RecordModeButton_Click;

        // Row 2 – output resolution and encoding quality, properly spaced below row 1
        var resolutionLbl = MkLabel("OUTPUT RESOLUTION", 7.5f, true); resolutionLbl.Location = new Point(20, 57);
        _outputResolutionComboBox = new ComboBox
        {
            Size = new Size(172, 24), Location = new Point(20, 72),
            DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 8.5f),
            BackColor = Surface2Color, ForeColor = TextColor, FlatStyle = FlatStyle.Flat
        };
        _outputResolutionComboBox.Items.AddRange(new object[]
        {
            "Native", "2160p", "1440p", "1080p", "720p", "480p"
        });
        _outputResolutionComboBox.SelectedIndexChanged += OutputResolutionComboBox_SelectedIndexChanged;

        var qualityLbl = MkLabel("ENCODING", 7.5f, true); qualityLbl.Location = new Point(208, 57);
        _encodingQualityComboBox = new ComboBox
        {
            Size = new Size(122, 24), Location = new Point(208, 72),
            DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 8.5f),
            BackColor = Surface2Color, ForeColor = TextColor, FlatStyle = FlatStyle.Flat
        };
        _encodingQualityComboBox.Items.AddRange(new object[]
        {
            "Fast", "Balanced", "Quality"
        });
        _encodingQualityComboBox.SelectedIndexChanged += EncodingQualityComboBox_SelectedIndexChanged;

        // Row 3 – profile selector
        var profileLbl = MkLabel("PROFILE", 7.5f, true); profileLbl.Location = new Point(20, 104);
        _profileComboBox = new ComboBox
        {
            Size = new Size(180, 26), Location = new Point(20, 119),
            DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 9),
            BackColor = Surface2Color, ForeColor = TextColor, FlatStyle = FlatStyle.Flat
        };
        _profileComboBox.SelectedIndexChanged += ProfileComboBox_SelectedIndexChanged;

        var newProfileBtn = MkBtn("+ New", Surface2Color, 64, 26); newProfileBtn.Location = new Point(208, 119); newProfileBtn.ForeColor = GreenColor; newProfileBtn.Click += NewProfileButton_Click;
        var deleteProfileBtn = MkBtn("Delete", Surface2Color, 64, 26); deleteProfileBtn.Location = new Point(280, 119); deleteProfileBtn.ForeColor = RedColor; deleteProfileBtn.Click += DeleteProfileButton_Click;

        // Row 4 – microphone
        var micLbl = MkLabel("MICROPHONE", 7.5f, true); micLbl.Location = new Point(20, 152);
        _micToggleButton = MkBtn("MIC: OFF", Surface2Color, 100, 26); _micToggleButton.Location = new Point(20, 167); _micToggleButton.Click += MicToggleButton_Click;
        _micDeviceComboBox = new ComboBox
        {
            Size = new Size(230, 26), Location = new Point(128, 167),
            DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 8.5f),
            BackColor = Surface2Color, ForeColor = TextColor, FlatStyle = FlatStyle.Flat,
            Visible = false
        };
        _micDeviceComboBox.SelectedIndexChanged += MicDeviceComboBox_SelectedIndexChanged;

        cfgPanel.Controls.AddRange(new Control[]
        {
            monLbl, _monitorComboBox, _recordingKeyLabel, _changeKeyButton, _saveClipKeyLabel, _changeSaveClipKeyButton,
            _recordingFpsLabel, _recordingFpsInput, _retrospectiveDurationLabel, _retrospectiveDurationInput,
            recordModeLbl, _recordModeButton,
            resolutionLbl, _outputResolutionComboBox, qualityLbl, _encodingQualityComboBox,
            profileLbl, _profileComboBox, newProfileBtn, deleteProfileBtn,
            micLbl, _micToggleButton, _micDeviceComboBox
        });

        // 4. Actions – Dock.Top
        Panel actPanel = new Panel { Height = 64, Dock = DockStyle.Top, BackColor = BgColor };

        _startButton = MkBtn("▶  Start", GreenColor, 130, 40); _startButton.Location = new Point(16, 12); _startButton.Click += StartButton_Click;
        _stopButton  = MkBtn("■  Stop",  RedColor,   130, 40); _stopButton.Location  = new Point(16, 12); _stopButton.Visible = false; _stopButton.Click += StopButton_Click;
        _saveClipButton   = MkBtn("◉  Save Clip",   PurpleColor, 130, 40); _saveClipButton.Location   = new Point(154, 12); _saveClipButton.Click += SaveClipButton_Click;
        _openFolderButton = MkBtn("📁  Folder",      Surface2Color, 112, 40); _openFolderButton.Location = new Point(292, 12); _openFolderButton.ForeColor = Text2Color; _openFolderButton.Click += OpenFolderButton_Click;
        var trayBtn = MkBtn("⎕  Minimize to Tray", Surface2Color, 170, 40); trayBtn.Location = new Point(412, 12); trayBtn.ForeColor = Text2Color; trayBtn.Click += (_, _) => MinimizeToTray();
        var settingsBtn = MkBtn("⚙", Surface2Color, 40, 40); settingsBtn.Location = new Point(590, 12); settingsBtn.ForeColor = Text2Color; settingsBtn.Click += ContextSettingsButton_Click;

        actPanel.Controls.AddRange(new Control[] { _startButton, _stopButton, _saveClipButton, _openFolderButton, trayBtn, settingsBtn });

        // 5. Log area – Dock.Fill so it grows/shrinks with the window
        Panel logOuter = new Panel { Dock = DockStyle.Fill, BackColor = BgColor };

        // Header row inside logOuter (added last → appears at top via Dock.Top)
        Panel logHeader = new Panel { Height = 32, Dock = DockStyle.Top, BackColor = BgColor, Padding = new Padding(20, 10, 0, 0) };
        var logLbl = MkLabel("LOGS", 7.5f, true); logLbl.Dock = DockStyle.Left;
        logHeader.Controls.Add(logLbl);

        // Content fills remaining space; Padding provides margins around the text box
        Panel logContent = new Panel { Dock = DockStyle.Fill, BackColor = BgColor, Padding = new Padding(20, 0, 20, 16) };
        var logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.None,
            Font = new Font("Consolas", 8.5f),
            BackColor = SurfaceColor, ForeColor = Color.FromArgb(134, 239, 172),
            BorderStyle = BorderStyle.None
        };
        EnableHiddenScrollbarScrolling(logBox);
        logContent.Controls.Add(logBox);

        // logContent added first (back/Fill), logHeader added last (front/Top)
        logOuter.Controls.Add(logContent);
        logOuter.Controls.Add(logHeader);

        // Assemble in REVERSE visual order: last added = topmost with Dock.Top
        Controls.AddRange(new Control[] { logOuter, MkDiv(), actPanel, MkDiv(), cfgPanel, MkDiv(), statusPanel, titleBar });

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

        RefreshProfileComboBox();
        LoadSettingsToUi();
        RestartKeyboardListener();
        UpdateUI(false);
    }

    private void LoadSettingsToUi()
    {
        int recordingFps                  = _settings!.GetRecordingFps();
        string outputResolution           = _settings.GetOutputResolutionPreset();
        string encodingQuality            = _settings.GetEncodingQualityPreset();
        int retrospectiveDurationSeconds  = _settings.GetRetrospectiveDurationSeconds();
        int savedKeyCode                  = _settings.GetRecordingKeyCode();
        string savedKeyName               = _settings.GetRecordingKeyName();
        int savedClipKeyCode              = _settings.GetSaveClipKeyCode();
        string savedClipKeyName           = _settings.GetSaveClipKeyName();

        _keyboardListener!.RecordingKeyCode = savedKeyCode;
        _keyboardListener.SaveClipKeyCode   = savedClipKeyCode;
        _recorder!.SetRecordingFps(recordingFps);
        _recorder.SetOutputResolutionPreset(outputResolution);
        _recorder.SetEncodingQualityPreset(encodingQuality);
        _recorder.SetRetrospectiveDurationSeconds(retrospectiveDurationSeconds);

        string savedOutputFolder = _settings.GetOutputFolder();
        if (!string.IsNullOrWhiteSpace(savedOutputFolder))
            _recorder.SetOutputFolder(savedOutputFolder);

        _recordingFpsInput!.Value          = recordingFps;
        _retrospectiveDurationInput!.Value = retrospectiveDurationSeconds;
        _outputResolutionComboBox!.SelectedItem = outputResolution;
        _encodingQualityComboBox!.SelectedItem  = encodingQuality;
        UpdateRetrospectiveUi(savedKeyName, savedClipKeyName, retrospectiveDurationSeconds);

        // Mic
        string micDeviceId = _settings.GetMicDeviceId();
        bool micEnabled    = _settings.GetMicEnabled();
        _recorder.SetMicDeviceId(micDeviceId);
        _recorder.SetMicEnabled(micEnabled);
        LoadMicDevices(micDeviceId);
        UpdateMicToggle(micEnabled);

        // Recording mode
        string recordingMode = _settings.GetRecordingMode();
        _keyboardListener!.HoldMode = recordingMode == "Hold";
        UpdateRecordModeButton(recordingMode);
    }

    private void LoadMicDevices(string selectedDeviceId = "")
    {
        _micDeviceComboBox!.SelectedIndexChanged -= MicDeviceComboBox_SelectedIndexChanged;
        _micDeviceComboBox.Items.Clear();
        _micDeviceComboBox.Items.Add(new MicDeviceItem("", "Default Microphone"));
        foreach (var (id, name) in ScreenRecorder.GetMicrophoneDevices())
            _micDeviceComboBox.Items.Add(new MicDeviceItem(id, name));
        var toSelect = _micDeviceComboBox.Items.Cast<MicDeviceItem>()
            .FirstOrDefault(m => m.Id == selectedDeviceId);
        _micDeviceComboBox.SelectedItem = toSelect ?? _micDeviceComboBox.Items[0];
        _micDeviceComboBox.SelectedIndexChanged += MicDeviceComboBox_SelectedIndexChanged;
    }

    private void UpdateMicToggle(bool enabled)
    {
        _micToggleButton!.Text      = enabled ? "MIC: ON" : "MIC: OFF";
        _micToggleButton.ForeColor  = enabled ? GreenColor : Text2Color;
        _micDeviceComboBox!.Visible = enabled;
    }

    private void UpdateRecordModeButton(string mode)
    {
        _recordModeButton!.Text      = mode == "Hold" ? "HOLD" : "TOGGLE";
        _recordModeButton.ForeColor  = mode == "Hold" ? BlueColor : OrangeColor;
    }

    // ── Mic handlers ──────────────────────────────────────────────────────────
    private void RecordModeButton_Click(object? sender, EventArgs e)
    {
        string current = _settings!.GetRecordingMode();
        string newMode = current == "Hold" ? "Toggle" : "Hold";
        _settings.SetRecordingMode(newMode);
        _keyboardListener!.HoldMode = newMode == "Hold";
        UpdateRecordModeButton(newMode);
        Logger.Instance.Log($"Recording mode set to: {newMode}");
    }

    private void MicToggleButton_Click(object? sender, EventArgs e)
    {
        bool newState = !_settings!.GetMicEnabled();
        _settings.SetMicEnabled(newState);
        _recorder!.SetMicEnabled(newState);
        if (newState) LoadMicDevices(_settings.GetMicDeviceId());
        UpdateMicToggle(newState);
        Logger.Instance.Log($"Microphone recording {(newState ? "enabled" : "disabled")}.");
    }

    private void MicDeviceComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_micDeviceComboBox?.SelectedItem is MicDeviceItem item)
        {
            _settings!.SetMicDeviceId(item.Id);
            _recorder!.SetMicDeviceId(item.Id);
            Logger.Instance.Log($"Microphone device set to: {item.Name}");
        }
    }

    private void RefreshProfileComboBox()
    {
        _suppressProfileChange = true;
        try
        {
            _profileComboBox!.Items.Clear();
            foreach (string name in _settings!.GetProfileNames())
                _profileComboBox.Items.Add(name);
            _profileComboBox.SelectedItem = _settings.ActiveProfileName;
        }
        finally
        {
            _suppressProfileChange = false;
        }
    }

    // ── Profile handlers ──────────────────────────────────────────────────────
    private void ProfileComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressProfileChange) return;
        if (_profileComboBox?.SelectedItem is string profileName && _settings != null)
        {
            if (!string.Equals(profileName, _settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
            {
                _settings.SwitchProfile(profileName);
                LoadSettingsToUi();
                Logger.Instance.Log($"Switched to profile: {profileName}");
            }
        }
    }

    private void NewProfileButton_Click(object? sender, EventArgs e)
    {
        string? name = PromptForInput(this, "New Profile", "Enter a name for the new profile:");
        if (string.IsNullOrWhiteSpace(name)) return;

        // Sanitize: strip invalid filename chars
        char[] invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        _settings!.CreateProfile(name);
        _settings.SwitchProfile(name);
        RefreshProfileComboBox();
        LoadSettingsToUi();
        Logger.Instance.Log($"Created and switched to profile: {name}");
    }

    private void DeleteProfileButton_Click(object? sender, EventArgs e)
    {
        string current = _settings?.ActiveProfileName ?? "Default";
        if (string.Equals(current, "Default", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("The Default profile cannot be deleted.", "Delete Profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show($"Delete profile \"{current}\"?", "Delete Profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _settings!.DeleteProfile(current);
        RefreshProfileComboBox();
        LoadSettingsToUi();
        Logger.Instance.Log($"Deleted profile: {current}");
    }

    private static string? PromptForInput(IWin32Window owner, string title, string prompt)
    {
        using var form = new Form
        {
            Text = title,
            Size = new Size(360, 150),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false,
            BackColor = BgColor
        };
        var lbl = MkLabel(prompt);                lbl.Location = new Point(12, 14);
        var tb  = new TextBox                     { Location = new Point(12, 36), Width = 322, BackColor = Surface2Color, ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle };
        var ok  = MkBtn("OK",     BlueColor,  80, 30); ok.Location     = new Point(158, 72); ok.DialogResult     = DialogResult.OK;
        var cancel = MkBtn("Cancel", SurfaceColor, 80, 30); cancel.Location = new Point(246, 72); cancel.DialogResult = DialogResult.Cancel;
        form.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
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
            Logger.Instance.Log($"Found monitor {i + 1}: {screens[i].DeviceName} bounds {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");
            _monitorComboBox.Items.Add(new MonitorItem(screens[i], name));
        }
        _monitorComboBox.SelectedIndex = 0;
    }

    private void MonitorComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_monitorComboBox!.SelectedItem is MonitorItem item)
        {
            Logger.Instance.Log($"Monitor selected: {item.DisplayName} (index {_monitorComboBox.SelectedIndex}) - DeviceName: {item.Screen.DeviceName}");
            _recorder!.SetSelectedScreen(item.Screen);
            _ = FlashMonitorBorderAsync(item.Screen);
        }
    }

    private async Task FlashMonitorBorderAsync(Screen screen)
    {
        Rectangle bounds = GetPhysicalBounds(screen);
        Logger.Instance.Log($"Flashing monitor: {screen.DeviceName} at bounds {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");
        var borderForm = new MonitorBorderFlash(bounds, BlueColor);

        try
        {
            for (int i = 0; i < 3; i++)
            {
                borderForm.Show();
                await Task.Delay(250);
                borderForm.Hide();
                await Task.Delay(150);
            }
        }
        finally
        {
            borderForm.Close();
            borderForm.Dispose();
        }
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
        using var dlg = new ContextSettingsDialog(
            currentFolder,
            currentFiles,
            _settings.GetFeedbackApiEndpoint(),
            _settings.GetFeedbackApiKey());
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.SetOutputFolder(dlg.OutputFolder);
            _settings.SetContextFilePaths(dlg.ContextFilePaths);
            _settings.SetFeedbackApiEndpoint(dlg.ApiEndpoint);
            _settings.SetFeedbackApiKey(dlg.ApiKey);
            if (!string.IsNullOrWhiteSpace(dlg.OutputFolder))
                _recorder!.SetOutputFolder(dlg.OutputFolder);
            Logger.Instance.Log($"Settings saved. Output folder: {dlg.OutputFolder}. Context files: {dlg.ContextFilePaths.Count}. API endpoint configured: {!string.IsNullOrWhiteSpace(dlg.ApiEndpoint)}");
        }
        RestartKeyboardListener();
    }

    private void StartButton_Click(object? sender, EventArgs e)    { if (_recorder?.StartRecording() == true) UpdateUI(true);  }
    private void StopButton_Click(object? sender, EventArgs e)     { if (_recorder?.StopRecording()  == true) UpdateUI(false); }
    private void SaveClipButton_Click(object? sender, EventArgs e) => _recorder?.SaveRecentClip();

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
        if (_settings != null && _keyboardListener != null)
            _keyboardListener.HoldMode = _settings.GetRecordingMode() == "Hold";
    }

    // ── Recorder events ───────────────────────────────────────────────────────
    private void Recorder_RecordingProcessingStarted(string expectedVideoPath, TaskCompletionSource<string> tcs)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Recorder_RecordingProcessingStarted(expectedVideoPath, tcs)); return; }
        _keyboardListener?.StopListening();
        Task<string> videoTask = tcs.Task;
        List<string> contextFiles = _settings?.GetContextFilePaths() ?? new List<string>();
        JsonObject contextSnapshot = CaptureContextSnapshot(contextFiles);
        ShowWindow();
        using var dlg = new FeedbackReportDialog(expectedVideoPath, videoTask, contextFiles);
        DialogResult result = dlg.ShowDialog(this);
        if (result == DialogResult.OK)
        {
            string title = dlg.ReportTitle;
            string description = dlg.ReportDescription;
            string severity = dlg.ReportSeverity;
            double trimStartSeconds = dlg.TrimStartSeconds;
            double trimEndSeconds = dlg.TrimEndSeconds;
            bool hasTrimSelection = dlg.HasTrimSelection;
            _ = Task.Run(() => FinalizeSubmittedReportAsync(videoTask, title, description, severity, contextSnapshot, contextFiles, hasTrimSelection, trimStartSeconds, trimEndSeconds));
        }
        else
        {
            _ = Task.Run(() => DiscardUnsubmittedReportAsync(videoTask));
        }
        if (!IsDisposed) RestartKeyboardListener();
    }

    private async Task FinalizeSubmittedReportAsync(
        Task<string> videoTask,
        string title,
        string description,
        string severity,
        JsonObject contextSnapshot,
        List<string> contextFiles,
        bool hasTrimSelection,
        double trimStartSeconds,
        double trimEndSeconds)
    {
        try
        {
            string videoPath = await videoTask.ConfigureAwait(false);
            string finalizedInputPath = ApplyTrimIfRequested(videoPath, hasTrimSelection, trimStartSeconds, trimEndSeconds);
            string safeTitle = title?.Trim() ?? string.Empty;
            string safeDescription = string.IsNullOrWhiteSpace(description) ? "No description provided." : description.Trim();
            string normalizedSeverity = string.IsNullOrWhiteSpace(severity) ? "medium" : severity.Trim().ToLowerInvariant();
            string finalPath = MoveReportToOutputFolder(finalizedInputPath, safeTitle);

            string? videoPathForUpload = finalPath;
            if (File.Exists(finalPath))
            {
                long sizeBytes = new FileInfo(finalPath).Length;
                if (sizeBytes > MaxApiVideoBytes)
                {
                    videoPathForUpload = null;
                    Logger.Instance.Log($"API submission: video exceeds 4MB ({sizeBytes} bytes). Submitting report without video.");
                }
            }

            ApiSubmitResult submitResult = await SubmitReportToApiWithRetryAsync(
                safeTitle,
                safeDescription,
                normalizedSeverity,
                contextFiles,
                videoPathForUpload).ConfigureAwait(false);

            LogApiResponsePayload(submitResult);

            WriteReportSidecarJson(finalPath, safeTitle, safeDescription, normalizedSeverity, contextSnapshot, submitResult);

            if (submitResult.Success)
            {
                Logger.Instance.Log($"Report submitted successfully to API ({submitResult.StatusCode}). File: {finalPath}");
            }
            else
            {
                Logger.Instance.Log($"Report submission failed. Status={submitResult.StatusCode}, Error={submitResult.ErrorMessage}");
                ShowSubmissionMessage($"Report submission failed. {submitResult.ErrorMessage}", "Submission Failed", MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Report submission finalization failed: {ex.Message}");
            ShowSubmissionMessage($"Report submission failed: {ex.Message}", "Submission Failed", MessageBoxIcon.Error);
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

    private string MoveReportToOutputFolder(string videoPath, string title)
    {
        string safeTitle  = SanitizeFileNameSegment(title ?? string.Empty);
        string timestamp  = ExtractRecordingTimestamp(videoPath);
        string extension  = Path.GetExtension(videoPath);
        string baseName   = string.IsNullOrWhiteSpace(safeTitle) ? $"[{timestamp}]" : $"{safeTitle} [{timestamp}]";

        // Use configured output folder (or fall back to where the video already lives)
        string outputFolder = _settings?.GetOutputFolder() ?? "";
        if (string.IsNullOrWhiteSpace(outputFolder))
            outputFolder = Path.GetDirectoryName(videoPath) ?? Environment.CurrentDirectory;
        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        string renamedPath = GetUniqueFilePath(outputFolder, baseName, extension);
        if (!string.Equals(videoPath, renamedPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(videoPath, renamedPath, overwrite: false);
        }

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
        return sanitized;
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

    private static void WriteReportSidecarJson(
        string videoPath,
        string title,
        string description,
        string severity,
        JsonObject contextSnapshot,
        ApiSubmitResult submitResult)
    {
        string jsonPath = Path.ChangeExtension(videoPath, ".json");
        var root = new JsonObject
        {
            ["Title"] = title ?? string.Empty,
            ["Description"] = description ?? string.Empty,
            ["Severity"] = severity ?? "medium",
            ["Context"] = contextSnapshot,
            ["submittedToApi"] = submitResult.Success,
            ["statusCode"] = submitResult.StatusCode,
            ["response"] = submitResult.ResponseBody,
            ["error"] = submitResult.ErrorMessage,
            ["submittedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };

        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
    }

    private JsonObject CaptureContextSnapshot(List<string> contextFiles)
    {
        // Return only flat key-value metadata for indexing; context files are sent separately
        return new JsonObject
        {
            ["platform"] = Environment.Is64BitOperatingSystem ? "win64" : "win32",
            ["os_version"] = Environment.OSVersion.VersionString,
            ["machine_name"] = Environment.MachineName,
            ["captured_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };
    }

    private async Task<ApiSubmitResult> SubmitReportToApiWithRetryAsync(
        string title,
        string description,
        string severity,
        List<string> contextFiles,
        string? videoPath)
    {
        if (_settings == null)
        {
            return new ApiSubmitResult(false, null, string.Empty, "Internal settings are not initialized.");
        }

        string endpoint = _settings.GetFeedbackApiEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ApiSubmitResult(false, null, string.Empty, "API endpoint is not configured. Open Settings and set the endpoint URL.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
        {
            return new ApiSubmitResult(false, null, string.Empty, "API endpoint URL is invalid.");
        }

        string apiKey = _settings.GetFeedbackApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ApiSubmitResult(false, null, string.Empty, "API key is not configured. Open Settings and set X-API-Key.");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var form = new MultipartFormDataContent();
                string buildId = _settings.GetFeedbackApiBuildId();
                string buildVersion = _settings.GetFeedbackApiBuildVersion();

                form.Add(new StringContent(description, Encoding.UTF8), "description");
                if (!string.IsNullOrWhiteSpace(title)) form.Add(new StringContent(title, Encoding.UTF8), "title");
                if (!string.IsNullOrWhiteSpace(buildId)) form.Add(new StringContent(buildId, Encoding.UTF8), "buildId");
                if (!string.IsNullOrWhiteSpace(buildVersion)) form.Add(new StringContent(buildVersion, Encoding.UTF8), "buildVersion");
                form.Add(new StringContent(severity, Encoding.UTF8), "severity");

                // Attach context files as separate form fields
                Logger.Instance.Log($"Attaching {contextFiles.Count} context files...");
                foreach (string contextFilePath in contextFiles)
                {
                    if (string.IsNullOrWhiteSpace(contextFilePath))
                    {
                        Logger.Instance.Log("Skipped blank context file path.");
                        continue;
                    }

                    if (!File.Exists(contextFilePath))
                    {
                        Logger.Instance.Log($"Context file not found: {contextFilePath}");
                        continue;
                    }

                    try
                    {
                        string fileName = Path.GetFileName(contextFilePath);
                        string ext = Path.GetExtension(contextFilePath).ToLowerInvariant();
                        string mimeType = ext switch
                        {
                            ".json" => "application/json",
                            ".log" => "text/plain",
                            ".txt" => "text/plain",
                            ".csv" => "text/csv",
                            ".xml" => "application/xml",
                            _ => "application/octet-stream"
                        };

                        byte[] fileBytes;
                        using (var fs = new FileStream(contextFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            long fileSize = fs.Length;
                            bool isTextFile = mimeType.StartsWith("text/", StringComparison.Ordinal);
                            if (fileSize > MaxApiContextFileBytes)
                            {
                                if (isTextFile)
                                {
                                    // Send the tail — most recent content is most relevant for bug reports
                                    byte[] tail = new byte[MaxApiContextFileBytes];
                                    fs.Seek(-MaxApiContextFileBytes, SeekOrigin.End);
                                    fs.ReadExactly(tail, 0, MaxApiContextFileBytes);
                                    byte[] header = Encoding.UTF8.GetBytes($"[Truncated: showing last {MaxApiContextFileBytes / 1024} KB of {fileSize / 1024} KB]\n");
                                    fileBytes = [.. header, .. tail];
                                    Logger.Instance.Log($"Context file {fileName} truncated from {fileSize} to ~{MaxApiContextFileBytes} bytes (tail).");
                                }
                                else
                                {
                                    Logger.Instance.Log($"Skipped context file {fileName}: {fileSize} bytes exceeds {MaxApiContextFileBytes} byte limit for non-text files.");
                                    continue;
                                }
                            }
                            else
                            {
                                using var ms = new MemoryStream((int)fileSize);
                                fs.CopyTo(ms);
                                fileBytes = ms.ToArray();
                            }
                        }

                        var fileContent = new ByteArrayContent(fileBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                        // Use filename as field name to keep each file unique and avoid conflicts with context.json
                        string fieldName = fileName;
                        form.Add(fileContent, fieldName, fileName);
                        Logger.Instance.Log($"Attached context file: {fileName} ({fileBytes.Length} bytes, field: {fieldName}, mime: {mimeType})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.Log($"Failed to attach context file {contextFilePath}: {ex.Message}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
                {
                    var stream = File.OpenRead(videoPath);
                    var videoContent = new StreamContent(stream);
                    videoContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                    form.Add(videoContent, "recording", Path.GetFileName(videoPath));
                }

                using HttpResponseMessage response = await client.PostAsync(endpointUri, form).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new ApiSubmitResult(false, (int)response.StatusCode, body, "Unauthorized (401). Check API key configuration.");
                }

                if ((int)response.StatusCode >= 500)
                {
                    if (attempt < 3)
                    {
                        int backoffSeconds = 1 << (attempt - 1);
                        Logger.Instance.Log($"API submission attempt {attempt} failed with {(int)response.StatusCode}. Retrying in {backoffSeconds}s.");
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds)).ConfigureAwait(false);
                        continue;
                    }

                    return new ApiSubmitResult(false, (int)response.StatusCode, body, $"Server error {(int)response.StatusCode}.");
                }

                if (response.IsSuccessStatusCode)
                {
                    return new ApiSubmitResult(true, (int)response.StatusCode, body, string.Empty);
                }

                return new ApiSubmitResult(false, (int)response.StatusCode, body, $"HTTP {(int)response.StatusCode}.");
            }
            catch (Exception ex)
            {
                if (attempt >= 3)
                {
                    return new ApiSubmitResult(false, null, string.Empty, $"Network error: {ex.Message}");
                }

                int backoffSeconds = 1 << (attempt - 1);
                Logger.Instance.Log($"API submission attempt {attempt} failed with network error. Retrying in {backoffSeconds}s.");
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds)).ConfigureAwait(false);
            }
        }

        return new ApiSubmitResult(false, null, string.Empty, "Submission failed after retries.");
    }

    private void ShowSubmissionMessage(string message, string caption, MessageBoxIcon icon)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowSubmissionMessage(message, caption, icon));
            return;
        }

        MessageBox.Show(this, message, caption, MessageBoxButtons.OK, icon);
    }

    private static void LogApiResponsePayload(ApiSubmitResult submitResult)
    {
        string statusText = submitResult.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "(no status)";
        string payload = string.IsNullOrWhiteSpace(submitResult.ResponseBody)
            ? "<empty response body>"
            : submitResult.ResponseBody;

        string logLine = $"API response payload (status {statusText}): {payload}";
        Logger.Instance.Log(logLine);
        Console.WriteLine(logLine);
    }

    private sealed record ApiSubmitResult(bool Success, int? StatusCode, string ResponseBody, string ErrorMessage);

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
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Bug Reporter")
            : configured;
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        else
            MessageBox.Show("Videos folder does not exist yet.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void MinimizeToTray() { Hide(); WindowState = FormWindowState.Minimized; _notifyIcon!.Visible = true; }
    private void ShowWindow()     { Show(); WindowState = FormWindowState.Normal;    BringToFront(); _notifyIcon!.Visible = false; }

    protected override void OnShown(EventArgs e) { base.OnShown(e); _formShownOnce = true; }

    private void RecorderForm_Resize(object? sender, EventArgs e)
    {
        if (_formShownOnce && WindowState == FormWindowState.Minimized) { Hide(); _notifyIcon!.Visible = true; }
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

public class MonitorBorderFlash : Form
{
    private readonly Color _borderColor;
    private const int BorderWidth = 5;

    public MonitorBorderFlash(Rectangle bounds, Color borderColor)
    {
        _borderColor = borderColor;

        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;
        Location = new Point(bounds.X, bounds.Y);
        Size = bounds.Size;
        TopMost = true;
        ShowInTaskbar = false;
        ControlBox = false;
        DoubleBuffered = true;
        Opacity = 1.0;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Invalidate();
        Refresh();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.Black);
        using (var pen = new Pen(_borderColor, BorderWidth))
        {
            int offset = BorderWidth / 2;
            e.Graphics.DrawRectangle(pen, offset, offset, Width - BorderWidth, Height - BorderWidth);
        }
    }
}