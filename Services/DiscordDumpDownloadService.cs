using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using MergeMansionWikiTools.Models;
using SharpSevenZip;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Downloads dump archives from a Discord thread, extracts them, and manages local dump inventory.
/// </summary>
internal static class DiscordDumpDownloadService
{
    private const string BaseUrl = "https://discord.com/api/v10";
    // Bot token goes per-request (HttpRequestMessage.Headers.Authorization) — never on the shared client.
    private static readonly HttpClient _http = HttpClients.Discord;

    // ── Data model ──

    internal record DiscordDumpInfo(
        string MessageId,
        DateTimeOffset MessageTimestamp,
        DateTimeOffset? DataCreatedAt,
        string? AttachmentUrl,
        string? AttachmentFilename,
        long AttachmentSize);

    // ── Public API ──

    /// <summary>
    /// Fetches ALL dump messages (with .7z attachments) from the Discord channel.
    /// Handles both forum channels (each dump = a thread) and regular channels/threads.
    /// Returns newest first.
    /// </summary>
    public static async Task<List<DiscordDumpInfo>> FetchAllDumpMessagesAsync(
        string botToken, string channelId, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Detect channel type
        progress?.Report("Detecting channel type…");
        var isForum = await IsForumChannelAsync(botToken, channelId, ct);

        // 2. Fetch based on type
        var results = isForum
            ? await FetchFromForumThreadsAsync(botToken, channelId, progress, ct)
            : await FetchFromChannelMessagesAsync(botToken, channelId, progress, ct);

        // Sort newest first by DataCreatedAt (fallback to MessageTimestamp)
        results.Sort((a, b) =>
        {
            var dateA = a.DataCreatedAt ?? a.MessageTimestamp;
            var dateB = b.DataCreatedAt ?? b.MessageTimestamp;
            return dateB.CompareTo(dateA);
        });

        progress?.Report($"Found {results.Count} dumps.");
        return results;
    }

    private static async Task<bool> IsForumChannelAsync(
        string botToken, string channelId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/channels/{channelId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return false;

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetInt32();
        return type is 15 or 16; // forum or media channel
    }

    /// <summary>
    /// Forum channel: list all threads (archived + active), get first message from each.
    /// </summary>
    private static async Task<List<DiscordDumpInfo>> FetchFromForumThreadsAsync(
        string botToken, string channelId, IProgress<string>? progress,
        CancellationToken ct)
    {
        var threadIds = new List<(string id, DateTimeOffset timestamp)>();

        // Fetch archived threads (paginated)
        string? beforeTimestamp = null;
        for (int page = 0; page < 20; page++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Fetching forum threads… (page {page + 1}, {threadIds.Count} found)");

            var url = $"{BaseUrl}/channels/{channelId}/threads/archived/public?limit=100";
            if (beforeTimestamp != null) url += $"&before={beforeTimestamp}";

            var threads = await FetchJsonAsync(botToken, url, ct);
            if (threads == null) break;

            if (!threads.Value.TryGetProperty("threads", out var threadsArr) ||
                threadsArr.GetArrayLength() == 0)
                break;

            foreach (var thread in threadsArr.EnumerateArray())
            {
                var id = thread.GetProperty("id").GetString();
                var archiveTs = thread.TryGetProperty("thread_metadata", out var meta) &&
                                meta.TryGetProperty("archive_timestamp", out var ats)
                    ? ats.GetString()
                    : null;

                DateTimeOffset ts = default;
                if (archiveTs != null)
                    DateTimeOffset.TryParse(archiveTs, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out ts);

                if (id != null)
                    threadIds.Add((id, ts));
                if (archiveTs != null)
                    beforeTimestamp = archiveTs;
            }

            if (!threads.Value.TryGetProperty("has_more", out var hm) || !hm.GetBoolean())
                break;
        }

        // Also fetch active threads (not paginated — returns all at once from guild,
        // but we can filter by parent_id)
        // For simplicity, also try fetching recent messages from the forum channel itself
        // (Discord returns thread-creation system messages with the starter message content)
        progress?.Report($"Checking active threads…");
        var activeUrl = $"{BaseUrl}/guilds/@me/threads/active"; // This won't work without guild ID
        // Alternative: just fetch channel messages — forum channels return thread starters
        var channelMessages = await FetchFromChannelMessagesAsync(botToken, channelId, null, ct);
        // Merge thread IDs from channel messages
        foreach (var cm in channelMessages)
            threadIds.Add((cm.MessageId, cm.MessageTimestamp));

        // Deduplicate thread IDs
        var seen = new HashSet<string>();
        var uniqueThreadIds = new List<(string id, DateTimeOffset timestamp)>();
        foreach (var t in threadIds)
        {
            if (seen.Add(t.id))
                uniqueThreadIds.Add(t);
        }

        // Fetch first message from each thread
        var results = new List<DiscordDumpInfo>();
        for (int i = 0; i < uniqueThreadIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (threadId, _) = uniqueThreadIds[i];
            progress?.Report($"Reading thread {i + 1}/{uniqueThreadIds.Count}…");

            // Get first message in thread (starter message)
            var msgUrl = $"{BaseUrl}/channels/{threadId}/messages?limit=1&order=asc";
            // Discord doesn't support order param; use after=0 trick to get oldest
            msgUrl = $"{BaseUrl}/channels/{threadId}/messages?limit=5";

            var msgDoc = await FetchJsonAsync(botToken, msgUrl, ct);
            if (msgDoc == null || msgDoc.Value.ValueKind != JsonValueKind.Array) continue;

            // Check all messages in the thread for .7z attachments (usually first message)
            foreach (var msg in msgDoc.Value.EnumerateArray())
            {
                var info = TryParseDumpMessage(msg);
                if (info != null)
                {
                    results.Add(info);
                    break; // one dump per thread
                }
            }

            // Small delay to avoid rate limits
            if (i % 5 == 4)
                await Task.Delay(500, ct);
        }

        return results;
    }

    /// <summary>
    /// Regular channel/thread: paginate through messages.
    /// </summary>
    private static async Task<List<DiscordDumpInfo>> FetchFromChannelMessagesAsync(
        string botToken, string channelId, IProgress<string>? progress,
        CancellationToken ct)
    {
        var results = new List<DiscordDumpInfo>();
        string? beforeId = null;

        for (int page = 0; page < 50; page++)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{BaseUrl}/channels/{channelId}/messages?limit=100";
            if (beforeId != null) url += $"&before={beforeId}";

            progress?.Report($"Fetching messages… (page {page + 1}, {results.Count} found)");

            var doc = await FetchJsonAsync(botToken, url, ct);
            if (doc == null || doc.Value.ValueKind != JsonValueKind.Array ||
                doc.Value.GetArrayLength() == 0)
                break;

            foreach (var msg in doc.Value.EnumerateArray())
            {
                beforeId = msg.GetProperty("id").GetString();

                var info = TryParseDumpMessage(msg);
                if (info != null)
                    results.Add(info);
            }

            if (doc.Value.GetArrayLength() < 100) break;
        }

        return results;
    }

    /// <summary>
    /// Parses a single Discord message into DiscordDumpInfo if it has a dump archive attachment (.7z or .zip).
    /// </summary>
    private static DiscordDumpInfo? TryParseDumpMessage(JsonElement msg)
    {
        if (!msg.TryGetProperty("attachments", out var attachments) ||
            attachments.GetArrayLength() == 0)
            return null;

        string? attachUrl = null;
        string? attachName = null;
        long attachSize = 0;

        foreach (var att in attachments.EnumerateArray())
        {
            var filename = att.GetProperty("filename").GetString() ?? "";
            if (filename.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                attachUrl = att.GetProperty("url").GetString();
                attachName = filename;
                attachSize = att.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                break;
            }
        }

        if (attachUrl == null) return null;

        var timestampStr = msg.GetProperty("timestamp").GetString();
        DateTimeOffset.TryParse(timestampStr, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var msgTimestamp);

        var content = msg.GetProperty("content").GetString() ?? "";
        var dataCreatedAt = DiscordDumpService.ParseCreatedAtFromMessage(content);

        var id = msg.GetProperty("id").GetString() ?? "";

        if (dataCreatedAt == null)
        {
            // Log full message content for diagnostics — find out why parsing failed
            var contentHex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(content));
            AppLogger.Warn($"[DiscordDump] No CreatedAt parsed for msg {id} ({msgTimestamp:O}), " +
                           $"file={attachName}, content=[{content}], hex=[{contentHex}]");
        }

        return new DiscordDumpInfo(id, msgTimestamp, dataCreatedAt, attachUrl, attachName, attachSize);
    }

    /// <summary>
    /// Sends a GET request with bot auth, handles rate limiting, returns parsed JSON or null.
    /// </summary>
    private static async Task<JsonElement?> FetchJsonAsync(
        string botToken, string url, CancellationToken ct)
    {
        for (int retry = 0; retry < 3; retry++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

            var res = await _http.SendAsync(req, ct);

            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var retryAfter = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                await Task.Delay(retryAfter, ct);
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                AppLogger.Warn($"Discord API {res.StatusCode} for {url}");
                return null;
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        return null;
    }

    /// <summary>
    /// Downloads a Discord attachment to a local file with progress reporting.
    /// </summary>
    public static async Task DownloadAttachmentAsync(
        string url, string outputPath, IProgress<(long downloaded, long? total)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // LongDownload (15 min): HttpClient.Timeout applies to reading the streamed body too —
        // dump archives are hundreds of MB and would get cut off by the 120 s Discord client.
        using var response = await HttpClients.LongDownload.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        long downloaded = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            progress?.Report((downloaded, totalBytes));
        }
    }

    /// <summary>
    /// Extracts a .7z or .zip archive to the specified directory.
    /// </summary>
    public static async Task ExtractArchiveAsync(string archivePath, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        await Task.Run(() =>
        {
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, outputDir);
            }
            else
            {
                using var extractor = new SharpSevenZipExtractor(archivePath);
                extractor.ExtractArchive(outputDir);
            }
        });
    }

    /// <summary>
    /// Scans all Dump* folders across all versions in the workspace and collects CreatedAt timestamps.
    /// Used to detect which Discord dumps have already been downloaded (✔ indicator).
    /// </summary>
    public static HashSet<DateTimeOffset> ScanLocalCreatedAtTimestamps(string basePath)
    {
        var timestamps = new HashSet<DateTimeOffset>();
        if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
            return timestamps;

        foreach (var versionDir in Directory.GetDirectories(basePath))
        {
            foreach (var dumpDir in Directory.GetDirectories(versionDir, "Dump*"))
            {
                var createdAtStr = DiscordDumpService.ReadCreatedAtFromDump(dumpDir);
                if (createdAtStr != null &&
                    DateTimeOffset.TryParse(createdAtStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var ts))
                {
                    timestamps.Add(ts);
                }
            }
        }

        return timestamps;
    }

    /// <summary>
    /// Returns the next available dump folder name for a version directory.
    /// Known version: "Dump" → "Dump 2" → "Dump 3"
    /// Unknown Version: "Dump {yyyy-MM-dd}"
    /// </summary>
    public static string GetNextDumpFolderName(string versionDir, DateTimeOffset? dataCreatedAt,
        bool isUnknownVersion)
    {
        if (isUnknownVersion)
        {
            var dateStr = dataCreatedAt?.ToString("yyyy-MM-dd") ?? "Unknown";
            return $"Dump {dateStr}";
        }

        if (!Directory.Exists(Path.Combine(versionDir, "Dump")))
            return "Dump";

        for (int i = 2; i < 1000; i++)
        {
            if (!Directory.Exists(Path.Combine(versionDir, $"Dump {i}")))
                return $"Dump {i}";
        }

        return $"Dump {DateTime.Now:yyyyMMdd_HHmmss}";
    }

    /// <summary>
    /// Checks if a dump with the same CreatedAt timestamp already exists locally.
    /// Returns the folder name if found, null otherwise.
    /// </summary>
    public static string? FindExistingDumpByCreatedAt(string versionDir, DateTimeOffset dataCreatedAt)
    {
        if (!Directory.Exists(versionDir)) return null;

        foreach (var dir in Directory.GetDirectories(versionDir, "Dump*"))
        {
            var existingStr = DiscordDumpService.ReadCreatedAtFromDump(dir);
            if (existingStr != null &&
                DateTimeOffset.TryParse(existingStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var existing) &&
                existing == dataCreatedAt)
            {
                return Path.GetFileName(dir);
            }
        }

        return null;
    }

    /// <summary>
    /// Scans a version directory for all Dump* folders, returning folder name + CreatedAt.
    /// </summary>
    public static List<(string folderName, string? createdAt)> ScanDumpFolders(string versionDir)
    {
        var result = new List<(string, string?)>();
        if (!Directory.Exists(versionDir)) return result;

        // "Dump" first, then "Dump 2", "Dump 3", etc.
        var dumpDir = Path.Combine(versionDir, "Dump");
        if (Directory.Exists(dumpDir))
            result.Add(("Dump", DiscordDumpService.ReadCreatedAtFromDump(dumpDir)));

        for (int i = 2; i < 100; i++)
        {
            var dir = Path.Combine(versionDir, $"Dump {i}");
            if (!Directory.Exists(dir)) break;
            result.Add(($"Dump {i}", DiscordDumpService.ReadCreatedAtFromDump(dir)));
        }

        return result;
    }
}
