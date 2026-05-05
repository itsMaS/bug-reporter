using System.Diagnostics;
using System.Text.Json;

namespace bug_reporter;

internal static class RcloneManager
{
    private const int ProcessTimeoutMs = 60 * 60 * 1000;

    public static string GetConfigPath()
    {
        string appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenRecorder"
        );

        if (!Directory.Exists(appDataFolder))
            Directory.CreateDirectory(appDataFolder);

        return Path.Combine(appDataFolder, "rclone.conf");
    }

    public static string FindRclonePath()
    {
        string appDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(appDir, "rclone.exe"),
            Path.Combine(appDir, "tools", "rclone.exe"),
            Path.Combine(appDir, "rclone", "rclone.exe"),
            Path.Combine(appDir, "tools", "rclone", "rclone.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "rclone", "rclone.exe"),
            "rclone.exe"
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string directory in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string probe = Path.Combine(directory.Trim(), "rclone.exe");
                if (File.Exists(probe))
                    return probe;
            }
        }

        return string.Empty;
    }

    public static bool IsAvailable(out string rclonePath)
    {
        rclonePath = FindRclonePath();
        return !string.IsNullOrWhiteSpace(rclonePath);
    }

    public static bool IsRemoteConfigured(string remoteName, out string error)
    {
        if (!IsAvailable(out string rclonePath))
        {
            error = "rclone executable was not found.";
            return false;
        }

        string normalizedRemote = (remoteName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedRemote))
        {
            error = "Remote name is empty.";
            return false;
        }

        string configPath = GetConfigPath();
        string args = $"listremotes --config \"{configPath}\"";
        if (!RunProcess(rclonePath, args, out string stdOut, out string stdErr))
        {
            error = string.IsNullOrWhiteSpace(stdErr) ? "Could not enumerate rclone remotes." : stdErr;
            return false;
        }

        bool exists = stdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.TrimEnd(':'), normalizedRemote, StringComparison.OrdinalIgnoreCase));

        error = exists ? string.Empty : $"Remote '{normalizedRemote}' is not configured in {configPath}.";
        return exists;
    }

    public static bool OpenAuthenticationConsole(string remoteName, out string error)
    {
        if (!IsAvailable(out string rclonePath))
        {
            error = "rclone executable was not found.";
            return false;
        }

        string normalizedRemote = string.IsNullOrWhiteSpace(remoteName) ? "gdrive" : remoteName.Trim();
        string configPath = GetConfigPath();
        bool remoteExists = IsRemoteConfigured(normalizedRemote, out _);
        string command;
        if (remoteExists)
        {
            // Reconnect with least-privilege scope for app-managed uploads only.
            command =
                $"echo Re-authenticating remote '{normalizedRemote}' with restricted Drive scope... & " +
                $"\"{rclonePath}\" config reconnect \"{normalizedRemote}:\" --config \"{configPath}\" --drive-scope drive.file";
        }
        else
        {
            command =
                $"echo Configure Google Drive remote (suggested name: {normalizedRemote}) & " +
                $"\"{rclonePath}\" config --config \"{configPath}\"";
        }

        var psi = new ProcessStartInfo("cmd.exe", $"/k {command}")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(rclonePath) ?? Environment.CurrentDirectory
        };

        try
        {
            Process.Start(psi);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool CopyMp4FolderToRemote(string localFolder, string remoteName, string remoteFolder, out string stdOut, out string stdErr)
    {
        stdOut = string.Empty;
        stdErr = string.Empty;

        if (!IsAvailable(out string rclonePath))
        {
            stdErr = "rclone executable was not found.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(localFolder) || !Directory.Exists(localFolder))
        {
            stdErr = "Local video folder does not exist.";
            return false;
        }

        string normalizedRemote = string.IsNullOrWhiteSpace(remoteName) ? "gdrive" : remoteName.Trim();
        string normalizedRemoteFolder = (remoteFolder ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        string remoteTarget = string.IsNullOrWhiteSpace(normalizedRemoteFolder)
            ? $"{normalizedRemote}:"
            : $"{normalizedRemote}:{normalizedRemoteFolder}";

        string configPath = GetConfigPath();
        string args =
            $"copy \"{localFolder}\" \"{remoteTarget}\" --include \"*.mp4\" --include \"*.json\" --config \"{configPath}\" --checkers 4 --transfers 4 --fast-list --create-empty-src-dirs";

        return RunProcess(rclonePath, args, out stdOut, out stdErr);
    }

    public static bool CopyJsonFolderToRemote(string localFolder, string remoteName, string remoteFolder, out string stdErr)
    {
        stdErr = string.Empty;

        if (!IsAvailable(out string rclonePath))
        {
            stdErr = "rclone executable was not found.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(localFolder) || !Directory.Exists(localFolder))
        {
            stdErr = "Local folder does not exist.";
            return false;
        }

        string normalizedRemote = string.IsNullOrWhiteSpace(remoteName) ? "gdrive" : remoteName.Trim();
        string normalizedRemoteFolder = (remoteFolder ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        string remoteTarget = string.IsNullOrWhiteSpace(normalizedRemoteFolder)
            ? $"{normalizedRemote}:"
            : $"{normalizedRemote}:{normalizedRemoteFolder}";

        string configPath = GetConfigPath();
        string args =
            $"copy \"{localFolder}\" \"{remoteTarget}\" --include \"*.json\" --config \"{configPath}\" --checkers 4 --transfers 4 --fast-list";

        return RunProcess(rclonePath, args, out _, out stdErr);
    }

    public static bool CopyFileToRemote(string localFilePath, string remoteName, string remoteFolder, out string stdOut, out string stdErr)
    {
        stdOut = string.Empty;
        stdErr = string.Empty;

        if (!IsAvailable(out string rclonePath))
        {
            stdErr = "rclone executable was not found.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            stdErr = "Local file does not exist.";
            return false;
        }

        string normalizedRemote = string.IsNullOrWhiteSpace(remoteName) ? "gdrive" : remoteName.Trim();
        string normalizedRemoteFolder = (remoteFolder ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        string remoteParent = string.IsNullOrWhiteSpace(normalizedRemoteFolder)
            ? $"{normalizedRemote}:"
            : $"{normalizedRemote}:{normalizedRemoteFolder}";
        string remoteTarget = $"{remoteParent}/{Path.GetFileName(localFilePath)}";

        string configPath = GetConfigPath();
        string args =
            $"copyto \"{localFilePath}\" \"{remoteTarget}\" --config \"{configPath}\" --checkers 4 --transfers 4 --fast-list";

        return RunProcess(rclonePath, args, out stdOut, out stdErr);
    }

    public static bool TryGetRemoteFileLink(string remoteName, string remoteFolder, string fileName, out string link, out string error)
    {
        link = string.Empty;
        error = string.Empty;

        if (!IsAvailable(out string rclonePath))
        {
            error = "rclone executable was not found.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            error = "File name is empty.";
            return false;
        }

        string normalizedRemote = string.IsNullOrWhiteSpace(remoteName) ? "gdrive" : remoteName.Trim();
        string normalizedRemoteFolder = (remoteFolder ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        string folderTarget = string.IsNullOrWhiteSpace(normalizedRemoteFolder)
            ? $"{normalizedRemote}:"
            : $"{normalizedRemote}:{normalizedRemoteFolder}";

        string configPath = GetConfigPath();
        string args = $"lsjson \"{folderTarget}\" --files-only --config \"{configPath}\"";
        if (!RunProcess(rclonePath, args, out string stdOut, out string stdErr))
        {
            error = string.IsNullOrWhiteSpace(stdErr) ? "rclone lsjson command failed." : stdErr.Trim();
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdOut);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "rclone lsjson did not return an array.";
                return false;
            }

            string? id = null;
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string name = item.TryGetProperty("Name", out JsonElement n) ? (n.GetString() ?? string.Empty) : string.Empty;
                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (item.TryGetProperty("ID", out JsonElement idNode))
                {
                    id = idNode.GetString();
                }

                break;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                error = "Could not resolve remote file ID.";
                return false;
            }

            link = $"https://drive.google.com/file/d/{id}/view";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not parse rclone lsjson output: {ex.Message}";
            return false;
        }
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

        using var process = Process.Start(psi);
        if (process == null)
        {
            stdOut = string.Empty;
            stdErr = "Process failed to start.";
            return false;
        }

        stdOut = process.StandardOutput.ReadToEnd();
        stdErr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(ProcessTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            stdErr = string.IsNullOrWhiteSpace(stdErr) ? "Process timed out." : stdErr;
            return false;
        }

        return process.ExitCode == 0;
    }
}
