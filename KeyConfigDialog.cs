using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;
using System.ComponentModel;

namespace bug_reporter;

[DesignerCategory("")]
public class KeyConfigDialog : Form
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Virtual key codes for all keys
    private static readonly Dictionary<int, string> VirtualKeyNames = new()
    {
        { 0x70, "F1" }, { 0x71, "F2" }, { 0x72, "F3" }, { 0x73, "F4" }, { 0x74, "F5" },
        { 0x75, "F6" }, { 0x76, "F7" }, { 0x77, "F8" }, { 0x78, "F9" }, { 0x79, "F10" },
        { 0x7A, "F11" }, { 0x7B, "F12" },
        { 0x30, "0" }, { 0x31, "1" }, { 0x32, "2" }, { 0x33, "3" }, { 0x34, "4" },
        { 0x35, "5" }, { 0x36, "6" }, { 0x37, "7" }, { 0x38, "8" }, { 0x39, "9" },
        { 0x41, "A" }, { 0x42, "B" }, { 0x43, "C" }, { 0x44, "D" }, { 0x45, "E" },
        { 0x46, "F" }, { 0x47, "G" }, { 0x48, "H" }, { 0x49, "I" }, { 0x4A, "J" },
        { 0x4B, "K" }, { 0x4C, "L" }, { 0x4D, "M" }, { 0x4E, "N" }, { 0x4F, "O" },
        { 0x50, "P" }, { 0x51, "Q" }, { 0x52, "R" }, { 0x53, "S" }, { 0x54, "T" },
        { 0x55, "U" }, { 0x56, "V" }, { 0x57, "W" }, { 0x58, "X" }, { 0x59, "Y" },
        { 0x5A, "Z" },
        { 0x20, "Space" }, { 0x0D, "Enter" }, { 0x1B, "Escape" },
        { 0xA0, "LShift" }, { 0xA1, "RShift" }, { 0xA2, "LCtrl" }, { 0xA3, "RCtrl" },
        { 0xA4, "LAlt" }, { 0xA5, "RAlt" }
    };

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedKeyCode { get; set; }
    
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedKeyName { get; set; } = "";

    private Label? _instructionLabel;
    private Label? _detectedKeyLabel;
    private Button? _cancelButton;
    private Thread? _keyDetectionThread;
    private bool _isDetecting = false;
    private readonly string _dialogTitle;
    private readonly string _promptText;

    public KeyConfigDialog(string dialogTitle = "Configure Recording Key", string promptText = "Press any key to set as recording key...")
    {
        _dialogTitle = dialogTitle;
        _promptText = promptText;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.Text = _dialogTitle;
        this.Size = new Size(400, 250);
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ControlBox = true;

        // Instruction label
        _instructionLabel = new Label
        {
            Text = _promptText,
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 20),
            ForeColor = Color.DarkBlue
        };

        // Detected key label
        _detectedKeyLabel = new Label
        {
            Text = "Waiting for key press...",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 70),
            ForeColor = Color.Green
        };

        // Info label
        Label infoLabel = new Label
        {
            Text = "Supported keys: F1-F12, A-Z, 0-9, Space, Enter, Shift, Ctrl, Alt",
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            Location = new Point(20, 120),
            ForeColor = Color.Gray,
            MaximumSize = new Size(360, 0)
        };

        // Cancel button
        _cancelButton = new Button
        {
            Text = "Cancel",
            Size = new Size(100, 40),
            Location = new Point(150, 170),
            BackColor = Color.Gray,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        _cancelButton.Click += (s, e) =>
        {
            _isDetecting = false;
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        };

        this.Controls.Add(_instructionLabel);
        this.Controls.Add(_detectedKeyLabel);
        this.Controls.Add(infoLabel);
        this.Controls.Add(_cancelButton);

        this.FormClosing += KeyConfigDialog_FormClosing;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        StartKeyDetection();
    }

    private void StartKeyDetection()
    {
        _isDetecting = true;
        _keyDetectionThread = new Thread(KeyDetectionLoop)
        {
            IsBackground = true
        };
        _keyDetectionThread.Start();
    }

    private void KeyDetectionLoop()
    {
        int[] keyCodesToCheck = VirtualKeyNames.Keys.ToArray();
        int? lastDetectedKey = null;

        while (_isDetecting)
        {
            foreach (int keyCode in keyCodesToCheck)
            {
                short keyState = GetAsyncKeyState(keyCode);
                bool isPressed = (keyState & 0x8000) != 0;

                if (isPressed && lastDetectedKey != keyCode)
                {
                    lastDetectedKey = keyCode;

                    if (VirtualKeyNames.TryGetValue(keyCode, out string? keyName))
                    {
                        SelectedKeyCode = keyCode;
                        SelectedKeyName = keyName;

                        this.Invoke(() =>
                        {
                            _detectedKeyLabel!.Text = $"Key Detected: {keyName}";
                            _detectedKeyLabel.ForeColor = Color.Green;
                        });

                        // Wait a bit before confirming
                        Thread.Sleep(500);
                        _isDetecting = false;

                        this.Invoke(() =>
                        {
                            this.DialogResult = DialogResult.OK;
                            this.Close();
                        });
                        return;
                    }
                }
                else if (!isPressed)
                {
                    lastDetectedKey = null;
                }
            }

            Thread.Sleep(50);
        }
    }

    private void KeyConfigDialog_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _isDetecting = false;
        _keyDetectionThread?.Join(1000);
    }
}
