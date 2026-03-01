using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

internal static class ApkDownloadService
{
    public record ApkVersionInfo(string Version, string VersionCode, string ApkId);

    private const string PackageName = "com.everywear.game5";
    private const string AppSlug = "merge-mansion-puzzles-story";

    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Tries multiple download strategies in order.
    /// Returns (version, filePath) on success.
    /// </summary>
    public static async Task<(string version, string filePath)> DownloadLatestAsync(
        string basePath,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        ApplyBrowserHeaders(client);

        // ── Strategy 1: warm up cookies by visiting the page, then download ──
        onStatus?.Invoke("Connecting to APKPure...");
        string? version = null;
        try
        {
            version = await WarmUpAndScrapeVersionAsync(client, ct);
        }
        catch { /* Cloudflare may block — continue anyway */ }

        // Try download URLs in order (APK and XAPK variants, .com and .net domains)
        // Prefer XAPK (full split bundle ~195 MB) over APK (base only ~22 MB)
        string[] downloadUrls =
        [
            $"https://d.apkpure.net/b/XAPK/{PackageName}?version=latest",
            $"https://d.apkpure.com/b/XAPK/{PackageName}?version=latest",
            $"https://d.apkpure.net/b/APK/{PackageName}?version=latest",
            $"https://d.apkpure.com/b/APK/{PackageName}?version=latest",
        ];

        HttpResponseMessage? response = null;
        string? usedUrl = null;

        foreach (var url in downloadUrls)
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke(version != null
                ? $"Trying download v{version}..."
                : "Trying download...");

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri($"https://apkpure.com/{AppSlug}/{PackageName}");

                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    resp.Dispose();
                    continue;
                }

                // Check we got a binary, not an HTML Cloudflare page
                var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (ct2.Contains("text/html"))
                {
                    resp.Dispose();
                    continue;
                }

                response = resp;
                usedUrl = url;
                break;
            }
            catch
            {
                // try next URL
            }
        }

        if (response == null)
            throw new InvalidOperationException(
                "All download URLs returned 403 (Cloudflare). " +
                "APKPure blocks automated downloads — try again later or download manually from apkpure.com.");

        using (response)
        {
            return await SaveResponseToFileAsync(response, basePath, version, onStatus, ct);
        }
    }

    private static async Task<(string version, string filePath)> SaveResponseToFileAsync(
        HttpResponseMessage response, string basePath, string? version,
        Action<string>? onStatus, CancellationToken ct)
    {
        // ── Extract version from response headers if not scraped ──
        var cdFileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        if (version == null && cdFileName != null)
        {
            var m = Regex.Match(cdFileName, @"_v?([\d]+\.[\d]+\.[\d]+)");
            if (m.Success) version = m.Groups[1].Value;
        }

        if (version == null && response.RequestMessage?.RequestUri is { } finalUri)
        {
            var m = Regex.Match(finalUri.ToString(), @"([\d]+\.[\d]+\.[\d]+)");
            if (m.Success) version = m.Groups[1].Value;
        }

        version ??= "latest";

        // ── Create version folder & save file ──
        var versionDir = Path.Combine(basePath, version);
        Directory.CreateDirectory(versionDir);

        var fileName = cdFileName ?? $"merge-mansion-{version}.apk";
        fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
        var filePath = Path.Combine(versionDir, fileName);

        var totalBytes = response.Content.Headers.ContentLength;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = File.Create(filePath);

        var buffer = new byte[81920];
        long downloaded = 0;
        long speedBytes = 0;
        var speedTimer = Stopwatch.StartNew();
        double lastSpeedMbps = 0;
        int bytesRead;

        while (true)
        {
            // Per-read timeout: abort if no data received for 30 seconds (stall detection)
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"Download stalled at {downloaded / 1024.0 / 1024.0:F1} MB — no data received for 30 seconds.");
            }

            if (bytesRead == 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            speedBytes += bytesRead;

            // Calculate speed every ~1 second
            var elapsed = speedTimer.Elapsed.TotalSeconds;
            if (elapsed >= 1.0)
            {
                lastSpeedMbps = speedBytes / 1024.0 / 1024.0 / elapsed;
                speedBytes = 0;
                speedTimer.Restart();
            }

            var speedStr = lastSpeedMbps > 0 ? $" ({lastSpeedMbps:F1} MB/s)" : "";

            if (totalBytes is > 0)
            {
                var pct = (double)downloaded / totalBytes.Value * 100;
                var mb = downloaded / 1024.0 / 1024.0;
                var totalMb = totalBytes.Value / 1024.0 / 1024.0;
                onStatus?.Invoke($"Downloading v{version}... {mb:F1} / {totalMb:F1} MB ({pct:F0}%){speedStr}");
            }
            else
            {
                var mb = downloaded / 1024.0 / 1024.0;
                onStatus?.Invoke($"Downloading v{version}... {mb:F1} MB{speedStr}");
            }
        }

        return (version, filePath);
    }

    /// <summary>
    /// Visits the APKPure page to collect cookies (may help bypass CDN checks)
    /// and tries to scrape the version number from the HTML.
    /// </summary>
    private static async Task<string?> WarmUpAndScrapeVersionAsync(HttpClient client, CancellationToken ct)
    {
        var pageUrl = $"https://apkpure.com/{AppSlug}/{PackageName}";
        var html = await client.GetStringAsync(pageUrl, ct);

        string[] patterns =
        [
            @"""softwareVersion""\s*:\s*""([\d.]+)""",
            @"<span[^>]*class=""ver-info""[^>]*>([\d.]+)</span>",
            @"Latest\s+Version[^<]*<[^>]+>([\d.]+)",
            @"""versionName""\s*:\s*""([\d.]+)""",
        ];

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Fetches the list of available versions from APKPure's version history page.
    /// Uses curl to bypass Cloudflare TLS fingerprinting that blocks .NET HttpClient.
    /// </summary>
    public static async Task<List<ApkVersionInfo>> FetchAvailableVersionsAsync(CancellationToken ct = default)
    {
        var url = $"https://apkpure.com/{AppSlug}/{PackageName}/versions";
        var html = await FetchWithCurlAsync(url, ct);

        var results = new List<ApkVersionInfo>();
        var seen = new HashSet<string>();

        // Match each <a> or <div> tag that contains all three data attributes (they may be on separate lines)
        foreach (Match tag in Regex.Matches(html, @"<(?:a|div)\b[^>]*data-dt-version=""[^""]*""[^>]*>", RegexOptions.Singleline))
        {
            var tagHtml = tag.Value;
            var mVer = Regex.Match(tagHtml, @"data-dt-version=""([^""]+)""");
            var mCode = Regex.Match(tagHtml, @"data-dt-versioncode=""([^""]+)""");
            var mApk = Regex.Match(tagHtml, @"data-dt-apkid=""([^""]+)""");

            if (!mVer.Success || !mCode.Success || !mApk.Success) continue;

            var ver = mVer.Groups[1].Value;
            // Deduplicate (latest appears twice: ver-top header + first list item)
            if (seen.Add(ver))
                results.Add(new ApkVersionInfo(ver, mCode.Groups[1].Value, mApk.Groups[1].Value));
        }

        return results;
    }

    /// <summary>
    /// Downloads a specific version using curl to bypass Cloudflare.
    /// ApkId is used as direct URL path (e.g. "b/XAPK/Y29t...").
    /// </summary>
    public static async Task<(string version, string filePath)> DownloadVersionAsync(
        string basePath, ApkVersionInfo version,
        Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var versionDir = Path.Combine(basePath, version.Version);
        Directory.CreateDirectory(versionDir);

        // Same CDN pattern as DownloadLatestAsync, but with versionCode
        string[] downloadUrls =
        [
            $"https://d.apkpure.net/b/XAPK/{PackageName}?versionCode={version.VersionCode}",
            $"https://d.apkpure.com/b/XAPK/{PackageName}?versionCode={version.VersionCode}",
            $"https://d.apkpure.net/b/APK/{PackageName}?versionCode={version.VersionCode}",
            $"https://d.apkpure.com/b/APK/{PackageName}?versionCode={version.VersionCode}",
        ];

        foreach (var url in downloadUrls)
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke($"Downloading v{version.Version}...");

            var tempFile = Path.Combine(versionDir, $"download_{version.Version}.tmp");

            try
            {
                var result = await DownloadWithCurlAsync(url, tempFile, version.Version, onStatus, ct);
                if (result != null) return result.Value;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        throw new InvalidOperationException(
            $"All download URLs for v{version.Version} failed. " +
            "Try again later or download manually from apkpure.com.");
    }

    /// <summary>
    /// Downloads a file using curl, monitors progress by polling file size.
    /// Returns null if curl fails (to try next URL), or (version, filePath) on success.
    /// Includes app-level stall detection: kills curl if file hasn't grown in 30s.
    /// </summary>
    private static async Task<(string version, string filePath)?> DownloadWithCurlAsync(
        string url, string tempFile, string version,
        Action<string>? onStatus, CancellationToken ct)
    {
        const int maxAttempts = 3;
        const int stallTimeoutSeconds = 30;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Use -C - to resume partial downloads on retry
            var useResume = attempt > 1 && File.Exists(tempFile) && new FileInfo(tempFile).Length > 0;

            var psi = new ProcessStartInfo
            {
                FileName = "curl",
                ArgumentList =
                {
                    "-L", "-f",                     // follow redirects, fail on HTTP errors
                    "-A", BrowserUserAgent,
                    "-e", $"https://apkpure.com/{AppSlug}/{PackageName}",
                    "-o", tempFile,
                    "-w", "%{http_code}",           // write HTTP status to stdout
                    "--connect-timeout", "30",
                    "--max-time", "900",            // 15 min max
                    "-D", "-"                       // dump headers to stdout (for filename)
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (useResume)
                psi.ArgumentList.Add("-C");
            if (useResume)
                psi.ArgumentList.Add("-");

            psi.ArgumentList.Add(url);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start curl.");

            using var registration = ct.Register(() => { try { proc.Kill(); } catch { } });

            bool stalledAndKilled = false;

            // Monitor file size for progress + speed + stall detection
            var progressTask = Task.Run(async () =>
            {
                long prevSize = 0;
                long lastChangedSize = 0;
                var speedTimer = Stopwatch.StartNew();
                var stallTimer = Stopwatch.StartNew();
                double lastSpeedMbps = 0;

                while (!proc.HasExited)
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            var size = new FileInfo(tempFile).Length;
                            var mb = size / 1024.0 / 1024.0;

                            // Track stall: file size unchanged
                            if (size > lastChangedSize)
                            {
                                lastChangedSize = size;
                                stallTimer.Restart();
                            }
                            else if (stallTimer.Elapsed.TotalSeconds >= stallTimeoutSeconds)
                            {
                                // File hasn't grown in 30s — kill curl, will retry
                                stalledAndKilled = true;
                                try { proc.Kill(); } catch { }
                                break;
                            }

                            // Speed calculation every ~1 second
                            var elapsed = speedTimer.Elapsed.TotalSeconds;
                            if (elapsed >= 1.0)
                            {
                                lastSpeedMbps = (size - prevSize) / 1024.0 / 1024.0 / elapsed;
                                prevSize = size;
                                speedTimer.Restart();
                            }

                            var speedStr = lastSpeedMbps > 0 ? $" ({lastSpeedMbps:F1} MB/s)" : "";
                            var stallStr = lastSpeedMbps <= 0 && stallTimer.Elapsed.TotalSeconds >= 5
                                ? " (stalled...)" : "";
                            var attemptStr = attempt > 1 ? $" [retry {attempt}/{maxAttempts}]" : "";
                            onStatus?.Invoke($"Downloading v{version}... {mb:F1} MB{speedStr}{stallStr}{attemptStr}");
                        }
                    }
                    catch { }
                    await Task.Delay(500, CancellationToken.None);
                }
            }, CancellationToken.None);

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            await progressTask;

            // If we killed curl due to stall, retry (keep temp file for resume)
            if (stalledAndKilled)
            {
                var stalledMb = File.Exists(tempFile) ? new FileInfo(tempFile).Length / 1024.0 / 1024.0 : 0;
                onStatus?.Invoke($"Download stalled at {stalledMb:F1} MB — retrying ({attempt}/{maxAttempts})...");
                await Task.Delay(2000, ct); // brief pause before retry
                continue;
            }

            if (proc.ExitCode != 0 || !File.Exists(tempFile) || new FileInfo(tempFile).Length < 10000)
            {
                // Non-stall failure — don't retry, try next URL
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                return null;
            }

            // Success — determine final filename from Content-Disposition or use default
            var cdMatch = Regex.Match(stdout, @"filename=""?([^""\r\n]+)""?");
            var fileName = cdMatch.Success
                ? string.Join("_", cdMatch.Groups[1].Value.Trim().Split(Path.GetInvalidFileNameChars()))
                : $"merge-mansion-{version}.xapk";

            var finalPath = Path.Combine(Path.GetDirectoryName(tempFile)!, fileName);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tempFile, finalPath);

            return (version, finalPath);
        }

        // All retry attempts exhausted due to stalls — clean up and return null to try next URL
        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        return null;
    }

    /// <summary>
    /// Fetches a URL using curl subprocess to bypass Cloudflare TLS fingerprinting.
    /// </summary>
    private static async Task<string> FetchWithCurlAsync(string url, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "curl",
            ArgumentList = { "-sL", "-A", BrowserUserAgent, url },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start curl.");

        using var registration = ct.Register(() => { try { proc.Kill(); } catch { } });

        var html = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"curl exited with code {proc.ExitCode}: {err.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(html) || html.Length < 1000)
            throw new InvalidOperationException($"curl returned unexpected response ({html.Length} bytes).");

        return html;
    }

    private static void ApplyBrowserHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
        client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua",
            "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
    }
}
