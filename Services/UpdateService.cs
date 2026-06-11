using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace MergeMansionWikiTools.Services;

public class ReleaseInfo
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string? AssetUrl { get; set; }
    public string? AssetName { get; set; }
    public long AssetSize { get; set; }
    public DateTime PublishedAt { get; set; }
}

public static class UpdateService
{
    private const string RepoOwner = "jouki";
    private const string RepoName = "MergeMansionWikiTools";
    private const string ApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>Fail-fast timeout for the GitHub API release check (was the dedicated client's
    /// Timeout) — the startup update check must not hang for the shared client's full 100 s.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>GitHub API request with the headers the old dedicated client carried
    /// (version-specific UA + Accept). Per-request headers take precedence over the shared
    /// client's default UA.</summary>
    private static HttpRequestMessage NewApiRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd($"{RepoName}/{Models.AppVersion.Version}");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        return req;
    }

    /// <summary>
    /// Checks GitHub Releases for a newer version.
    /// Returns release info if a newer version is available, null otherwise.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(CheckTimeout);
            using var req = NewApiRequest(ApiUrl);
            using var resp = await HttpClients.Default.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Skip pre-releases
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                return null;
            // Skip drafts
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                return null;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            if (!IsNewerVersion(tagName, Models.AppVersion.Version))
                return null;

            var release = new ReleaseInfo
            {
                TagName = tagName,
                Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? tagName : tagName,
                Body = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                HtmlUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "",
                PublishedAt = root.TryGetProperty("published_at", out var pub)
                    ? pub.GetDateTime() : DateTime.MinValue
            };

            // Find ZIP asset
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.GetProperty("name").GetString() ?? "";
                    if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        release.AssetUrl = asset.GetProperty("browser_download_url").GetString();
                        release.AssetName = assetName;
                        if (asset.TryGetProperty("size", out var size))
                            release.AssetSize = size.GetInt64();
                        break;
                    }
                }
            }

            return release;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the release ZIP, backs up current files, extracts new files, and launches restart script.
    /// </summary>
    public static async Task<bool> DownloadAndApplyAsync(ReleaseInfo release, IProgress<(double percent, string status)>? progress = null)
    {
        if (string.IsNullOrEmpty(release.AssetUrl))
            throw new InvalidOperationException("No download URL available.");

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "MergeMansionWikiTools_update");
        var zipPath = Path.Combine(tempDir, release.AssetName ?? "update.zip");
        var extractDir = Path.Combine(tempDir, "extract");
        var backupDir = Path.Combine(appDir, "_backup");

        // Clean temp
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir);

        try
        {
            // 1. Download ZIP
            progress?.Report((0, "Downloading..."));
            // LongDownload (15 min): HttpClient.Timeout applies to reading the streamed body too —
            // the old dedicated client's 15 s would cut off any release ZIP on a slower connection.
            using (var response = await HttpClients.LongDownload.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? release.AssetSize;
                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (totalBytes > 0)
                        progress?.Report(((double)downloaded / totalBytes * 70, $"Downloading... {downloaded / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB"));
                }
            }

            // 2. Extract ZIP
            progress?.Report((70, "Extracting..."));
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            // If ZIP contains a single subfolder, use that as the source
            var extractedEntries = Directory.GetFileSystemEntries(extractDir);
            var sourceDir = extractDir;
            if (extractedEntries.Length == 1 && Directory.Exists(extractedEntries[0]))
                sourceDir = extractedEntries[0];

            // 3. Backup current files
            progress?.Report((80, "Backing up current version..."));
            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, true);
            Directory.CreateDirectory(backupDir);

            foreach (var file in Directory.GetFiles(appDir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith("_backup", StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName == "updater.bat") continue;
                try
                {
                    File.Copy(file, Path.Combine(backupDir, fileName), true);
                }
                catch { /* skip locked files like log */ }
            }

            // 4. Create updater script
            progress?.Report((90, "Preparing restart..."));
            var exeName = Path.GetFileName(Environment.ProcessPath ?? "MergeMansionWikiTools.exe");
            var scriptPath = Path.Combine(tempDir, "updater.bat");
            var script = $"""
                @echo off
                title MergeMansionWikiTools Updater
                echo Waiting for application to close...
                timeout /t 2 /nobreak >nul
                :waitloop
                tasklist /FI "IMAGENAME eq {exeName}" 2>NUL | find /I "{exeName}" >NUL
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >nul
                    goto waitloop
                )
                echo Applying update...
                xcopy /E /Y /Q "{sourceDir}\*" "{appDir}"
                if errorlevel 1 (
                    echo Update failed! Rolling back...
                    xcopy /E /Y /Q "{backupDir}\*" "{appDir}"
                    echo Rollback complete.
                    pause
                    exit /b 1
                )
                echo Update complete. Starting application...
                start "" "{Path.Combine(appDir, exeName)}"
                exit
                """;
            await File.WriteAllTextAsync(scriptPath, script);

            // 5. Launch updater and exit
            progress?.Report((95, "Restarting..."));
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            });

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Update failed: {ex.Message}");
            // Clean up temp on failure
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            throw;
        }
    }

    /// <summary>Compares two version strings like "v0.15.0" and returns true if remote is newer.</summary>
    internal static bool IsNewerVersion(string remoteTag, string localTag)
    {
        static Version? Parse(string tag)
        {
            var s = tag.TrimStart('v', 'V');
            return Version.TryParse(s, out var v) ? v : null;
        }
        var remote = Parse(remoteTag);
        var local = Parse(localTag);
        if (remote == null || local == null) return false;
        return remote > local;
    }

    /// <summary>Formats bytes into human-readable string.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }
}
