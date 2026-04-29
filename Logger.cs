using System.Collections.Generic;

namespace bug_reporter;

public class Logger
{
    private static Logger? _instance;
    private readonly List<string> _logs = new List<string>();
    private event Action<string>? _onLogAdded;
    private readonly string _logFilePath;

    private Logger()
    {
        _logFilePath = ResolveLogFilePath();
        try
        {
            File.AppendAllText(_logFilePath, $"=== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch
        {
            // Ignore logger initialization write failures.
        }
    }

    private static string ResolveLogFilePath()
    {
        string appDir = AppContext.BaseDirectory;
        string exeLogPath = Path.Combine(appDir, "bug-reporter.log");

        try
        {
            Directory.CreateDirectory(appDir);
            using (FileStream stream = new FileStream(exeLogPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
            }
            return exeLogPath;
        }
        catch
        {
            string appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder");
            Directory.CreateDirectory(appDataFolder);
            return Path.Combine(appDataFolder, "bug-reporter.log");
        }
    }

    public static Logger Instance
    {
        get
        {
            _instance ??= new Logger();
            return _instance;
        }
    }

    public void Subscribe(Action<string> onLogAdded)
    {
        _onLogAdded += onLogAdded;
    }

    public void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string logMessage = $"[{timestamp}] {message}";
        
        lock (_logs)
        {
            _logs.Add(logMessage);
            
            // Keep only last 1000 logs
            if (_logs.Count > 1000)
            {
                _logs.RemoveAt(0);
            }
        }

        Console.WriteLine(logMessage);
        _onLogAdded?.Invoke(logMessage);
        try { File.AppendAllText(_logFilePath, logMessage + Environment.NewLine); } catch { }
    }

    public List<string> GetLogs()
    {
        lock (_logs)
        {
            return new List<string>(_logs);
        }
    }

    public void Clear()
    {
        lock (_logs)
        {
            _logs.Clear();
        }
    }
}
