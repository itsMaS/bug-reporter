using System.Text.Json;

namespace bug_reporter;

public class SettingsManager
{
    private readonly string _configPath;
    private Dictionary<string, object> _settings;

    public SettingsManager()
    {
        string appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenRecorder"
        );

        if (!Directory.Exists(appDataFolder))
        {
            Directory.CreateDirectory(appDataFolder);
        }

        _configPath = Path.Combine(appDataFolder, "settings.json");
        _settings = LoadSettings();
    }

    private Dictionary<string, object> LoadSettings()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                string json = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                return loaded ?? GetDefaultSettings();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading settings: {ex.Message}");
        }

        return GetDefaultSettings();
    }

    private Dictionary<string, object> GetDefaultSettings()
    {
        return new Dictionary<string, object>
        {
            { "RecordingKeyCode", 0x7A }, // F11
            { "RecordingKeyName", "F11" },
            { "RecordingFps", 30 },
            { "OutputResolutionPreset", "1080p" },
            { "EncodingQualityPreset", "Balanced" },
            { "SaveClipKeyCode", 0x79 }, // F10
            { "SaveClipKeyName", "F10" },
            { "RetrospectiveDurationSeconds", 15 },
            { "OutputFolder", "" },
            { "ContextFilePaths", new List<string>() },
            { "RcloneRemoteName", "gdrive" },
            { "RcloneDriveFolder", "BugReporter" },
            { "AutoUploadToGoogleDrive", true }
        };
    }

    public void SaveSettings()
    {
        try
        {
            string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving settings: {ex.Message}");
        }
    }

    public int GetRecordingKeyCode()
    {
        return GetIntSetting("RecordingKeyCode", 0x7A);
    }

    public string GetRecordingKeyName()
    {
        return GetStringSetting("RecordingKeyName", "F11");
    }

    public void SetRecordingKey(int keyCode, string keyName)
    {
        _settings["RecordingKeyCode"] = keyCode;
        _settings["RecordingKeyName"] = keyName;
        SaveSettings();
    }

    public int GetRecordingFps()
    {
        return Math.Clamp(GetIntSetting("RecordingFps", 30), 5, 60);
    }

    public void SetRecordingFps(int fps)
    {
        _settings["RecordingFps"] = Math.Clamp(fps, 5, 60);
        SaveSettings();
    }

    public string GetOutputResolutionPreset()
    {
        string value = GetStringSetting("OutputResolutionPreset", "1080p");
        string[] allowed = { "Native", "2160p", "1440p", "1080p", "720p", "480p" };
        return Array.Exists(allowed, option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase)) ? value : "1080p";
    }

    public void SetOutputResolutionPreset(string preset)
    {
        _settings["OutputResolutionPreset"] = string.IsNullOrWhiteSpace(preset) ? "1080p" : preset;
        SaveSettings();
    }

    public string GetEncodingQualityPreset()
    {
        string value = GetStringSetting("EncodingQualityPreset", "Balanced");
        string[] allowed = { "Fast", "Balanced", "Quality" };
        return Array.Exists(allowed, option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase)) ? value : "Balanced";
    }

    public void SetEncodingQualityPreset(string preset)
    {
        _settings["EncodingQualityPreset"] = string.IsNullOrWhiteSpace(preset) ? "Balanced" : preset;
        SaveSettings();
    }

    public int GetSaveClipKeyCode()
    {
        return GetIntSetting("SaveClipKeyCode", 0x79);
    }

    public string GetSaveClipKeyName()
    {
        return GetStringSetting("SaveClipKeyName", "F10");
    }

    public void SetSaveClipKey(int keyCode, string keyName)
    {
        _settings["SaveClipKeyCode"] = keyCode;
        _settings["SaveClipKeyName"] = keyName;
        SaveSettings();
    }

    public int GetRetrospectiveDurationSeconds()
    {
        return Math.Clamp(GetIntSetting("RetrospectiveDurationSeconds", 15), 5, 120);
    }

    public void SetRetrospectiveDurationSeconds(int seconds)
    {
        _settings["RetrospectiveDurationSeconds"] = Math.Clamp(seconds, 5, 120);
        SaveSettings();
    }

    public string GetOutputFolder()
    {
        return GetStringSetting("OutputFolder", "");
    }

    public void SetOutputFolder(string folder)
    {
        _settings["OutputFolder"] = folder ?? "";
        SaveSettings();
    }

    public List<string> GetContextFilePaths()
    {
        if (!_settings.TryGetValue("ContextFilePaths", out object? value) || value == null)
            return new List<string>();

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                string? s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            }
            return list;
        }

        if (value is List<string> direct) return direct;
        return new List<string>();
    }

    public void SetContextFilePaths(List<string> paths)
    {
        _settings["ContextFilePaths"] = paths ?? new List<string>();
        SaveSettings();
    }

    public string GetRcloneRemoteName()
    {
        string value = GetStringSetting("RcloneRemoteName", "gdrive").Trim();
        return string.IsNullOrWhiteSpace(value) ? "gdrive" : value;
    }

    public void SetRcloneRemoteName(string remoteName)
    {
        string value = (remoteName ?? string.Empty).Trim();
        _settings["RcloneRemoteName"] = string.IsNullOrWhiteSpace(value) ? "gdrive" : value;
        SaveSettings();
    }

    public string GetRcloneDriveFolder()
    {
        string value = GetStringSetting("RcloneDriveFolder", "BugReporter").Trim();
        return value.Replace('\\', '/').Trim('/');
    }

    public void SetRcloneDriveFolder(string driveFolder)
    {
        string value = (driveFolder ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        _settings["RcloneDriveFolder"] = string.IsNullOrWhiteSpace(value) ? "BugReporter" : value;
        SaveSettings();
    }

    public bool GetAutoUploadToGoogleDrive()
    {
        if (!_settings.TryGetValue("AutoUploadToGoogleDrive", out object? value) || value == null)
            return true;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.True) return true;
            if (element.ValueKind == JsonValueKind.False) return false;
            if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out bool parsedString)) return parsedString;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int parsedInt)) return parsedInt != 0;
        }

        if (value is bool boolValue) return boolValue;
        if (bool.TryParse(value.ToString(), out bool parsed)) return parsed;
        if (int.TryParse(value.ToString(), out int parsedIntFallback)) return parsedIntFallback != 0;
        return true;
    }

    public void SetAutoUploadToGoogleDrive(bool enabled)
    {
        _settings["AutoUploadToGoogleDrive"] = enabled;
        SaveSettings();
    }

    private int GetIntSetting(string key, int defaultValue)
    {
        if (!_settings.TryGetValue(key, out object? value) || value == null)
        {
            return defaultValue;
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int numericValue))
            {
                return numericValue;
            }

            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out int parsedValue))
            {
                return parsedValue;
            }
        }

        if (value is int intValue)
        {
            return intValue;
        }

        return int.TryParse(value.ToString(), out int fallbackValue) ? fallbackValue : defaultValue;
    }

    private string GetStringSetting(string key, string defaultValue)
    {
        if (!_settings.TryGetValue(key, out object? value) || value == null)
        {
            return defaultValue;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? defaultValue;
        }

        return value.ToString() ?? defaultValue;
    }
}
