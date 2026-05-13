using System.Text.Json;
using System.Security.Cryptography;

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
            { "FeedbackApiEndpoint", "" },
            { "FeedbackApiBuildId", "" },
            { "FeedbackApiBuildVersion", "" },
            { "FeedbackApiKeyEncrypted", "" }
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

    public string GetFeedbackApiEndpoint()
    {
        return GetStringSetting("FeedbackApiEndpoint", "").Trim();
    }

    public void SetFeedbackApiEndpoint(string endpoint)
    {
        _settings["FeedbackApiEndpoint"] = (endpoint ?? string.Empty).Trim();
        SaveSettings();
    }

    public string GetFeedbackApiBuildId()
    {
        return GetStringSetting("FeedbackApiBuildId", "").Trim();
    }

    public void SetFeedbackApiBuildId(string buildId)
    {
        _settings["FeedbackApiBuildId"] = (buildId ?? string.Empty).Trim();
        SaveSettings();
    }

    public string GetFeedbackApiBuildVersion()
    {
        return GetStringSetting("FeedbackApiBuildVersion", "").Trim();
    }

    public void SetFeedbackApiBuildVersion(string buildVersion)
    {
        _settings["FeedbackApiBuildVersion"] = (buildVersion ?? string.Empty).Trim();
        SaveSettings();
    }

    public string GetFeedbackApiKey()
    {
        string encryptedValue = GetStringSetting("FeedbackApiKeyEncrypted", "");
        return DecryptSecret(encryptedValue);
    }

    public void SetFeedbackApiKey(string apiKey)
    {
        _settings["FeedbackApiKeyEncrypted"] = EncryptSecret((apiKey ?? string.Empty).Trim());
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

    private static string EncryptSecret(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return string.Empty;

        try
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DecryptSecret(string encryptedValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
            return string.Empty;

        try
        {
            byte[] encrypted = Convert.FromBase64String(encryptedValue);
            byte[] data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(data);
        }
        catch
        {
            return string.Empty;
        }
    }
}
