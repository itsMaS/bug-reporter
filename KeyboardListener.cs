using System.Runtime.InteropServices;

namespace bug_reporter;

public class KeyboardListener : IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private int _recordingKeyCode = 0x7A; // Default: F11
    private int _saveClipKeyCode = 0x79; // Default: F10
    private bool _isKeyPressed = false;
    private bool _isSaveKeyPressed = false;
    private bool _isRecordingToggled = false;
    private Thread? _listenerThread;
    private bool _isListening = false;
    private Action? _onKeyPressed;
    private Action? _onKeyReleased;
    private Action? _onSaveClipRequested;

    public int RecordingKeyCode
    {
        get => _recordingKeyCode;
        set => _recordingKeyCode = value;
    }

    public int SaveClipKeyCode
    {
        get => _saveClipKeyCode;
        set => _saveClipKeyCode = value;
    }

    public void StartListening(Action onPressed, Action onReleased, Action onSaveClipRequested)
    {
        if (_isListening)
            return;

        _onKeyPressed = onPressed;
        _onKeyReleased = onReleased;
        _onSaveClipRequested = onSaveClipRequested;
        _isListening = true;

        _listenerThread = new Thread(ListenerLoop)
        {
            IsBackground = true,
            Name = "KeyboardListener"
        };
        _listenerThread.Start();

        Console.WriteLine("Keyboard listener started.");
    }

    public void StopListening()
    {
        _isListening = false;
        _listenerThread?.Join(2000);
    }

    private void ListenerLoop()
    {
        while (_isListening)
        {
            try
            {
                short recordingKeyState = GetAsyncKeyState(_recordingKeyCode);
                bool isPressed = (recordingKeyState & 0x8000) != 0;

                if (isPressed && !_isKeyPressed)
                {
                    // Key was just pressed; toggle recording state.
                    _isKeyPressed = true;

                    if (_isRecordingToggled)
                    {
                        _isRecordingToggled = false;
                        Logger.Instance.Log("Recording key pressed - stopping recording");
                        SafeInvoke(_onKeyReleased, "onReleased");
                    }
                    else
                    {
                        _isRecordingToggled = true;
                        Logger.Instance.Log("Recording key pressed - starting recording");
                        SafeInvoke(_onKeyPressed, "onPressed");
                    }
                }
                else if (!isPressed && _isKeyPressed)
                {
                    // Key was just released.
                    _isKeyPressed = false;
                }

                short saveKeyState = GetAsyncKeyState(_saveClipKeyCode);
                bool isSavePressed = (saveKeyState & 0x8000) != 0;

                if (isSavePressed && !_isSaveKeyPressed)
                {
                    _isSaveKeyPressed = true;
                    Logger.Instance.Log("Retrospective save key pressed - saving buffered clip");
                    SafeInvoke(_onSaveClipRequested, "onSaveClipRequested");
                }
                else if (!isSavePressed && _isSaveKeyPressed)
                {
                    _isSaveKeyPressed = false;
                }

                Thread.Sleep(50); // Check every 50ms
            }
            catch (Exception ex)
            {
                Logger.Instance.Log($"Keyboard listener error: {ex.Message}");
            }
        }
    }

    private static void SafeInvoke(Action? action, string callbackName)
    {
        if (action == null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Instance.Log($"Keyboard callback error ({callbackName}): {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopListening();
    }
}

