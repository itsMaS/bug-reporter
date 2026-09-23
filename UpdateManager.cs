using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace bug_reporter;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string ReleaseNotes,
    string ReleasePageUrl,
    string DownloadUrl,
    long AssetSize)
{
    public string VersionString => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
}

/// <summary>
/// Checks GitHub Releases for a newer version and applies it.
/// The repository must be public (anonymous API access) and every release must ship
/// a single asset named <see cref="AssetName"/> with a tag of the form vX.Y.Z that
/// matches the &lt;Version&gt; in bug-reporter.csproj.
/// </summary>
public static class UpdateManager
{
    public const string RepoOwner = "itsMaS";
    public const string RepoName = "bug-reporter";
    public const string AssetName = "bug-reporter-win.zip";
    public const string ExeName = "bug-reporter.exe";

    private const string LatestReleaseUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    public static Version CurrentVersion { get; } = ReadCurrentVersion();
    public static string CurrentVersionString => $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    private static Version ReadCurrentVersion()
    {
        Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"bug-reporter/{CurrentVersionString}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Parses "v1.4.0" or "1.4.0" into a three-part <see cref="Version"/>.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        if (!Version.TryParse(s, out Version? parsed)) return false;
        version = new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>
    /// Queries the latest GitHub release. Returns null when the running build is already
    /// the newest. Throws on network, API, or parse failures.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using HttpClient client = CreateClient(TimeSpan.FromSeconds(20));
        using HttpResponseMessage response = await client.GetAsync(LatestReleaseUrl, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("No public release found. The repository may be private or have no published release.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub API rate limit reached. Try again later.");
        response.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseTag(tag, out Version latest))
            throw new InvalidOperationException($"Release tag '{tag}' is not in vX.Y.Z form.");

        string notes = root.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String
            ? body.GetString() ?? string.Empty
            : string.Empty;
        string page = root.TryGetProperty("html_url", out JsonElement html) ? html.GetString() ?? string.Empty : string.Empty;

        string? downloadUrl = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                assetSize = asset.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0;
                break;
            }
        }

        if (latest <= CurrentVersion) return null;
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException($"Release {tag} does not contain {AssetName}.");

        return new UpdateInfo(latest, tag, notes, page, downloadUrl, assetSize);
    }

    /// <summary>
    /// Downloads the release zip, verifies its size against the GitHub asset, extracts it,
    /// and launches a detached script that waits for this process to exit, copies the new
    /// files over the install folder, and relaunches the app. The caller must exit the
    /// application immediately after this returns.
    /// </summary>
    public static async Task DownloadAndStageAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        string workDir = Path.Combine(Path.GetTempPath(), "bug-reporter-update");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);

        string zipPath = Path.Combine(workDir, AssetName);
        string extractDir = Path.Combine(workDir, "new");

        Logger.Instance.Log($"Downloading {update.Tag} from {update.DownloadUrl}");
        using (HttpClient client = CreateClient(TimeSpan.FromMinutes(30)))
        using (HttpResponseMessage response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.AssetSize;

            await using Stream source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            byte[] buffer = new byte[1 << 16];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total > 0) progress?.Report((double)received / total);
            }
        }

        long actualSize = new FileInfo(zipPath).Length;
        if (update.AssetSize > 0 && actualSize != update.AssetSize)
            throw new InvalidOperationException($"Download is {actualSize:N0} bytes but the release asset is {update.AssetSize:N0} bytes. The download was incomplete.");

        Logger.Instance.Log($"Download verified ({actualSize:N0} bytes). Extracting.");
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        File.Delete(zipPath);

        if (!File.Exists(Path.Combine(extractDir, ExeName)))
            throw new InvalidOperationException($"The downloaded package does not contain {ExeName}.");

        string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string exePath = Environment.ProcessPath ?? Path.Combine(installDir, ExeName);
        string scriptPath = Path.Combine(workDir, "apply-update.cmd");
        string logPath = Path.Combine(workDir, "apply-update.log");

        string script = BuildApplyScript(Environment.ProcessId, extractDir, installDir, exePath, logPath);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Logger.Instance.Log($"Staged {update.Tag}. Handing off to {scriptPath} and exiting.");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string BuildApplyScript(int pid, string sourceDir, string installDir, string exePath, string logPath)
    {
        // robocopy exit codes below 8 are success variants. /E keeps files the new
        // package does not ship (the runtime log), so this is an overlay, not a mirror.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"APP_PID={pid}\"");
        sb.AppendLine($"set \"SRC={sourceDir}\"");
        sb.AppendLine($"set \"DST={installDir}\"");
        sb.AppendLine($"set \"EXE={exePath}\"");
        sb.AppendLine($"set \"LOG={logPath}\"");
        sb.AppendLine("echo [%date% %time%] Waiting for process %APP_PID% to exit > \"%LOG%\"");
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"PID eq %APP_PID%\" 2>nul | find \"%APP_PID%\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine("  goto wait");
        sb.AppendLine(")");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("echo [%date% %time%] Copying \"%SRC%\" to \"%DST%\" >> \"%LOG%\"");
        sb.AppendLine("robocopy \"%SRC%\" \"%DST%\" /E /R:15 /W:1 /NFL /NDL /NJH /NP >> \"%LOG%\" 2>&1");
        sb.AppendLine("set \"RC=%errorlevel%\"");
        sb.AppendLine("if %RC% GEQ 8 (");
        sb.AppendLine("  echo [%date% %time%] robocopy failed with exit code %RC% >> \"%LOG%\"");
        sb.AppendLine("  start \"\" \"%EXE%\"");
        sb.AppendLine("  exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine("echo [%date% %time%] Update applied, relaunching >> \"%LOG%\"");
        sb.AppendLine("start \"\" \"%EXE%\"");
        sb.AppendLine("rmdir /s /q \"%SRC%\" >> \"%LOG%\" 2>&1");
        sb.AppendLine("endlocal");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }
}
