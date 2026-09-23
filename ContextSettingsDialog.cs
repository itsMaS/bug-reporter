using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace bug_reporter;

[DesignerCategory("")]
public class ContextSettingsDialog : Form
{
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    private static Color Bg      => RecorderForm.BgColor;
    private static Color Surface => RecorderForm.SurfaceColor;
    private static Color Surface2 => RecorderForm.Surface2Color;
    private static Color Tx      => RecorderForm.TextColor;
    private static Color Tx2     => RecorderForm.Text2Color;
    private static Color Blue    => RecorderForm.BlueColor;
    private static Color Green   => RecorderForm.GreenColor;
    private static Color Red     => RecorderForm.RedColor;

    private readonly TextBox _outputFolderBox;
    private readonly TextBox _apiEndpointBox;
    private readonly TextBox _apiKeyBox;
    private readonly ListBox _filesList;

    public string OutputFolder => _outputFolderBox.Text.Trim();
    public string ApiEndpoint => _apiEndpointBox.Text.Trim();
    public string ApiKey => _apiKeyBox.Text.Trim();

    public List<string> ContextFilePaths
    {
        get
        {
            var list = new List<string>();
            foreach (var item in _filesList.Items) list.Add(item.ToString()!);
            return list;
        }
    }

    public ContextSettingsDialog(
        string currentOutputFolder,
        List<string> currentContextFiles,
        string currentApiEndpoint,
        string currentApiKey,
        Action? onCheckForUpdates = null)
    {
        Text = "Settings";
        ClientSize = new Size(680, 570);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = true;
        BackColor = Bg;

        // ── Title bar ──────────────────────────────────────────────────────────
        Panel titleBar = new Panel { Location = new Point(0, 0), Size = new Size(680, 42), BackColor = Surface };
        titleBar.MouseDown += TitleBar_MouseDown;
        var tbText = RecorderForm.MkLabel("Settings", 11, true, Tx); tbText.Location = new Point(20, 11);
        tbText.MouseDown += TitleBar_MouseDown;
        titleBar.Controls.AddRange(new Control[] { tbText });

        // ── Output folder section (43-132) ────────────────────────────────────
        Panel folderPanel = new Panel { Location = new Point(0, 43), Size = new Size(680, 90), BackColor = Bg };
        var folderLbl = RecorderForm.MkLabel("OUTPUT FOLDER", 7.5f, true); folderLbl.Location = new Point(20, 12);
        var folderHint = RecorderForm.MkLabel("Where submitted report videos will be saved", 8f, false, Tx2); folderHint.Location = new Point(20, 28);

        string defaultFolder = string.IsNullOrWhiteSpace(currentOutputFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Bug Reporter")
            : currentOutputFolder;

        _outputFolderBox = new TextBox
        {
            Location = new Point(20, 52), Size = new Size(538, 26),
            Font = new Font("Segoe UI", 9), BackColor = Surface2, ForeColor = Tx,
            BorderStyle = BorderStyle.None, ReadOnly = true, Text = defaultFolder
        };

        var browseBtn = RecorderForm.MkBtn("Browse…", Surface2, 104, 26);
        browseBtn.Location = new Point(566, 52); browseBtn.ForeColor = Tx;
        browseBtn.Click += BrowseFolder_Click;

        folderPanel.Controls.AddRange(new Control[] { folderLbl, folderHint, _outputFolderBox, browseBtn });

        // ── Divider ───────────────────────────────────────────────────────────
        Panel div1 = new Panel { Location = new Point(0, 133), Size = new Size(680, 1), BackColor = Surface2 };

        // ── API section (134-309) ─────────────────────────────────────────────
        Panel apiPanel = new Panel { Location = new Point(0, 134), Size = new Size(680, 176), BackColor = Bg };
        var apiLbl = RecorderForm.MkLabel("API SUBMISSION", 7.5f, true); apiLbl.Location = new Point(20, 12);
        var apiHint = RecorderForm.MkLabel("Reports are submitted directly to this endpoint as multipart/form-data", 8f, false, Tx2);
        apiHint.Location = new Point(20, 28); apiHint.MaximumSize = new Size(640, 18);

        var endpointLbl = RecorderForm.MkLabel("ENDPOINT URL", 7.5f, true); endpointLbl.Location = new Point(20, 52);
        _apiEndpointBox = new TextBox
        {
            Location = new Point(20, 68), Size = new Size(640, 26),
            Font = new Font("Segoe UI", 9), BackColor = Surface2, ForeColor = Tx,
            BorderStyle = BorderStyle.None, Text = currentApiEndpoint ?? string.Empty
        };

        var keyLbl = RecorderForm.MkLabel("X-API-KEY", 7.5f, true); keyLbl.Location = new Point(20, 100);
        _apiKeyBox = new TextBox
        {
            Location = new Point(20, 116), Size = new Size(640, 26),
            Font = new Font("Segoe UI", 9), BackColor = Surface2, ForeColor = Tx,
            BorderStyle = BorderStyle.None, UseSystemPasswordChar = true,
            Text = currentApiKey ?? string.Empty
        };

        apiPanel.Controls.AddRange(new Control[] { apiLbl, apiHint, endpointLbl, _apiEndpointBox, keyLbl, _apiKeyBox });

        // ── Divider ───────────────────────────────────────────────────────────
        Panel divApi = new Panel { Location = new Point(0, 310), Size = new Size(680, 1), BackColor = Surface2 };

        // ── Context files section (311-506) ───────────────────────────────────
        Panel filesPanel = new Panel { Location = new Point(0, 311), Size = new Size(680, 196), BackColor = Bg };
        var filesLbl = RecorderForm.MkLabel("CONTEXT FILES", 7.5f, true); filesLbl.Location = new Point(20, 12);
        var filesHint = RecorderForm.MkLabel("Contents are captured when the report dialog opens and sent in the context JSON field", 8f, false, Tx2);
        filesHint.Location = new Point(20, 28); filesHint.MaximumSize = new Size(640, 18);

        _filesList = new ListBox
        {
            Location = new Point(20, 52), Size = new Size(640, 96),
            Font = new Font("Segoe UI", 9), BackColor = Surface, ForeColor = Tx,
            BorderStyle = BorderStyle.None, SelectionMode = SelectionMode.One
        };
        _filesList.DoubleClick += FilesList_DoubleClick;
        foreach (var path in currentContextFiles) _filesList.Items.Add(path);

        var addBtn    = RecorderForm.MkBtn("+ Add File",      Blue, 108, 30); addBtn.Location    = new Point(20,  156); addBtn.Click += AddFile_Click;
        var removeBtn = RecorderForm.MkBtn("Remove Selected", Red,  140, 30); removeBtn.Location = new Point(136, 156); removeBtn.Click += RemoveFile_Click;

        filesPanel.Controls.AddRange(new Control[] { filesLbl, filesHint, _filesList, addBtn, removeBtn });

        // ── Divider ───────────────────────────────────────────────────────────
        Panel div2 = new Panel { Location = new Point(0, 507), Size = new Size(680, 1), BackColor = Surface2 };

        // ── Bottom bar (508-569) ──────────────────────────────────────────────
        Panel bottomBar = new Panel { Location = new Point(0, 508), Size = new Size(680, 62), BackColor = Surface };
        var saveBtn   = RecorderForm.MkBtn("Save",   Green, 110, 40); saveBtn.Location   = new Point(450, 11); saveBtn.DialogResult   = DialogResult.OK;
        var cancelBtn = RecorderForm.MkBtn("Cancel", Red,   110, 40); cancelBtn.Location = new Point(568, 11); cancelBtn.DialogResult = DialogResult.Cancel;
        var versionLbl = RecorderForm.MkLabel(UpdateManager.CurrentVersionString, 8f, false, Tx2); versionLbl.Location = new Point(20, 23);
        var updateBtn = RecorderForm.MkBtn("Check for updates", Surface2, 150, 30); updateBtn.Location = new Point(88, 16); updateBtn.ForeColor = Tx2;
        updateBtn.Click += (_, _) => onCheckForUpdates?.Invoke();
        bottomBar.Controls.AddRange(new Control[] { versionLbl, updateBtn, saveBtn, cancelBtn });

        AcceptButton = saveBtn;
        CancelButton = cancelBtn;

        Controls.AddRange(new Control[] { titleBar, folderPanel, div1, apiPanel, divApi, filesPanel, div2, bottomBar });
    }

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
    }

    private void BrowseFolder_Click(object? sender, EventArgs e)
    {
        try
        {
            Logger.Instance.Log("Context settings: opening folder picker.");
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select output folder for report videos",
                SelectedPath = _outputFolderBox.Text
            };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _outputFolderBox.Text = dlg.SelectedPath;
                Logger.Instance.Log($"Context settings: output folder selected: {dlg.SelectedPath}");
            }
            else
            {
                Logger.Instance.Log("Context settings: folder picker canceled.");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Context settings: folder picker failed: {ex.Message}");
            MessageBox.Show($"Could not open folder picker. {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddFile_Click(object? sender, EventArgs e)
    {
        try
        {
            Logger.Instance.Log("Context settings: opening add-file picker.");
            using var dlg = new OpenFileDialog
            {
                Title = "Select context file",
                Filter = "All files (*.*)|*.*|JSON files (*.json)|*.json|Log files (*.log)|*.log|Text files (*.txt)|*.txt",
                FilterIndex = 1,
                Multiselect = true,
                CheckFileExists = true,
                DereferenceLinks = true
            };

            DialogResult result = dlg.ShowDialog();
            if (result == DialogResult.OK)
            {
                int addedCount = 0;
                foreach (var file in dlg.FileNames)
                {
                    if (!_filesList.Items.Contains(file))
                    {
                        _filesList.Items.Add(file);
                        addedCount++;
                    }
                }

                Logger.Instance.Log($"Context settings: add-file completed. Selected={dlg.FileNames.Length}, Added={addedCount}");
            }
            else
            {
                Logger.Instance.Log("Context settings: add-file picker canceled.");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Context settings: add-file picker failed: {ex.Message}");
            MessageBox.Show($"Could not open file picker. {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RemoveFile_Click(object? sender, EventArgs e)
    {
        if (_filesList.SelectedIndex >= 0)
            _filesList.Items.RemoveAt(_filesList.SelectedIndex);
    }

    private void FilesList_DoubleClick(object? sender, EventArgs e)
    {
        if (_filesList.SelectedItem is string filePath && File.Exists(filePath))
        {
            try
            {
                string folderPath = Path.GetDirectoryName(filePath) ?? filePath;
                if (Directory.Exists(folderPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = folderPath,
                        UseShellExecute = true
                    });
                    Logger.Instance.Log($"Opened folder for context file: {folderPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Log($"Context settings: failed to open folder: {ex.Message}");
                MessageBox.Show($"Could not open folder. {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
