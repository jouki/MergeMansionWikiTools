using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SharpSevenZip;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Publishes dump ZIP files to a Discord channel/thread via Bot API.
/// Reads last message to determine if a new dump is available.
/// </summary>
internal static class DiscordDumpService
{
    private const string BaseUrl = "https://discord.com/api/v10";
    private static readonly HttpClient _http = new();

    // ── Public API ──

    /// <summary>
    /// Reads the most recent message in the channel and extracts the "created at" timestamp
    /// from the second line (format: "The data itself was created at {ISO8601}").
    /// Returns null if no messages found or parsing fails.
    /// </summary>
    public static async Task<DateTimeOffset?> GetLastPublishedDateAsync(string botToken, string channelId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}/channels/{channelId}/messages?limit=5");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            AppLogger.Warn($"Discord API: {response.StatusCode} reading messages");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var messages = doc.RootElement;

        if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            return null;

        // Search recent messages for the "created at" pattern
        foreach (var msg in messages.EnumerateArray())
        {
            var content = msg.GetProperty("content").GetString();
            if (string.IsNullOrEmpty(content)) continue;

            var parsed = ParseCreatedAtFromMessage(content);
            if (parsed.HasValue) return parsed;
        }

        return null;
    }

    /// <summary>
    /// <summary>
    /// Additional channel IDs to forward the dump message to (same message + attachment).
    /// </summary>
    private static readonly string[] ForwardChannelIds = ["783385526387998743"];

    /// <summary>
    /// Creates a ZIP from the dump folder and uploads it to the Discord channel with a message.
    /// Also forwards to additional channels.
    /// </summary>
    public static async Task<bool> PublishDumpAsync(
        string botToken, string channelId, string dumpDir, string createdAt,
        IProgress<string>? progress = null)
    {
        // 1. Create 7z archive with LZMA2 ultra compression
        progress?.Report("Creating 7z archive…");
        var now = DateTimeOffset.Now;
        var archiveName = $"dump_{now:dd_MM_yyyy_HH_mm}.7z";
        var zipPath = Path.Combine(Path.GetTempPath(), archiveName);
        var zipName = archiveName;

        try
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);

            // Include Phone Raw Files (C/P/L — newest file from each) if available
            string? combinedDir = null;
            try
            {
                var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_DATA");
                if (Directory.Exists(dataDir))
                {
                    var hasAny = false;
                    var phoneDir = Path.Combine(Path.GetTempPath(), $"phone_raw_{now:HHmmss}",
                        "Phone Raw Files");

                    foreach (var sub in new[] { "C", "P", "L" })
                    {
                        var srcDir = Path.Combine(dataDir, sub);
                        if (!Directory.Exists(srcDir)) continue;

                        var newestFile = Directory.GetFiles(srcDir)
                            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                            .FirstOrDefault();
                        if (newestFile == null) continue;

                        var destDir = Path.Combine(phoneDir, sub);
                        Directory.CreateDirectory(destDir);
                        File.Copy(newestFile, Path.Combine(destDir, Path.GetFileName(newestFile)));
                        hasAny = true;
                    }

                    if (hasAny)
                    {
                        combinedDir = Path.Combine(Path.GetTempPath(), $"dump_combined_{now:HHmmss}");
                        CopyDirectory(dumpDir, combinedDir);
                        CopyDirectory(Path.GetDirectoryName(phoneDir)!, combinedDir);
                        progress?.Report("Including Phone Raw Files in archive…");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Phone Raw Files collection failed (non-critical): {ex.Message}");
                combinedDir = null;
            }

            var compressor = new SharpSevenZip.SharpSevenZipCompressor
            {
                ArchiveFormat = SharpSevenZip.OutArchiveFormat.SevenZip,
                CompressionLevel = SharpSevenZip.CompressionLevel.Ultra,
                CompressionMethod = SharpSevenZip.CompressionMethod.Lzma2,
                DirectoryStructure = true,
                PreserveDirectoryRoot = false
            };
            compressor.CompressDirectory(combinedDir ?? dumpDir, zipPath);
            if (combinedDir != null)
                try { Directory.Delete(combinedDir, true); } catch { }

            var archiveSize = new FileInfo(zipPath).Length;
            progress?.Report($"7z created: {archiveName} ({archiveSize / (1024 * 1024.0):F1} MB)");

            // Discord file size limit depends on server boost tier (8 MB for tier 0)
            if (archiveSize > 8 * 1024 * 1024)
            {
                // Retry without Experimental/ subfolder
                progress?.Report("7z too large — retrying without Experimental/ folder…");
                File.Delete(zipPath);

                // Copy files to temp dir excluding Experimental/
                var tempDir = Path.Combine(Path.GetTempPath(), $"dump_filtered_{now:HHmmss}");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                foreach (var file in Directory.EnumerateFiles(dumpDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dumpDir, file).Replace('\\', '/');
                    if (rel.StartsWith("Experimental/", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dest = Path.Combine(tempDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest);
                }
                compressor.CompressDirectory(tempDir, zipPath);
                try { Directory.Delete(tempDir, true); } catch { }
                archiveSize = new FileInfo(zipPath).Length;
                progress?.Report($"7z without Experimental: {archiveSize / (1024 * 1024.0):F1} MB");
            }

            if (archiveSize > 25 * 1024 * 1024)
            {
                progress?.Report("ERROR: Archive still exceeds 25 MB Discord limit.");
                return false;
            }

            // 2. Build message
            var messageText = $"New dumps from {now:dd.MM.yyyy HH:mm}{now:zzz}.\n" +
                              $"The data itself was created at {createdAt}";

            // 3. Resolve channel type and upload
            progress?.Report("Uploading to Discord…");
            var fileBytes = await File.ReadAllBytesAsync(zipPath);

            string? postedMessageId = null;
            string? postedChannelId = null;

            var chReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/channels/{channelId}");
            chReq.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
            var chRes = await _http.SendAsync(chReq);
            var isForum = false;
            if (chRes.IsSuccessStatusCode)
            {
                var chJson = await chRes.Content.ReadAsStringAsync();
                using var chDoc = JsonDocument.Parse(chJson);
                var chType = chDoc.RootElement.GetProperty("type").GetInt32();
                isForum = chType is 15 or 16; // forum or media channel
            }

            if (isForum)
            {
                // Forum/media channel → create thread with initial message + file
                progress?.Report("Forum channel detected — creating thread…");
                using var threadPayload = new MultipartFormDataContent();
                threadPayload.Add(new StringContent(messageText), "message[content]");
                threadPayload.Add(new StringContent($"Dump {now:dd.MM.yyyy HH:mm}"), "name");

                var fc = new ByteArrayContent(fileBytes);
                fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                threadPayload.Add(fc, "message[files[0]]", zipName);

                var threadReq = new HttpRequestMessage(HttpMethod.Post,
                    $"{BaseUrl}/channels/{channelId}/threads");
                threadReq.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                threadReq.Content = threadPayload;

                var threadRes = await _http.SendAsync(threadReq);
                if (!threadRes.IsSuccessStatusCode)
                {
                    var body = await threadRes.Content.ReadAsStringAsync();
                    progress?.Report($"Discord API error: {threadRes.StatusCode} — {body}");
                    AppLogger.Warn($"Discord forum thread failed: {threadRes.StatusCode} — {body}");
                    return false;
                }

                // Extract message ID from the thread's initial message
                var resJson = await threadRes.Content.ReadAsStringAsync();
                using var resDoc = JsonDocument.Parse(resJson);
                var root = resDoc.RootElement;
                // Forum thread response includes the thread channel; the initial message is in "message"
                if (root.TryGetProperty("message", out var msgEl) &&
                    msgEl.TryGetProperty("id", out var msgId) &&
                    msgEl.TryGetProperty("channel_id", out var msgChId))
                {
                    postedMessageId = msgId.GetString();
                    postedChannelId = msgChId.GetString();
                }
                // Fallback: thread id itself might contain last_message_id
                else if (root.TryGetProperty("last_message_id", out var lmId))
                {
                    postedMessageId = lmId.GetString();
                    postedChannelId = root.GetProperty("id").GetString();
                }

                progress?.Report("Forum thread created successfully.");
                AppLogger.Info($"Discord forum thread created in channel {channelId}");
            }
            else
            {
                // Regular text channel / thread → POST message with file
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(messageText), "content");

                var fc = new ByteArrayContent(fileBytes);
                fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(fc, "files[0]", zipName);

                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{BaseUrl}/channels/{channelId}/messages");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                request.Content = form;

                var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    progress?.Report($"Discord API error: {response.StatusCode} — {body}");
                    AppLogger.Warn($"Discord publish failed: {response.StatusCode} — {body}");
                    return false;
                }

                var resJson = await response.Content.ReadAsStringAsync();
                using var resDoc = JsonDocument.Parse(resJson);
                postedMessageId = resDoc.RootElement.GetProperty("id").GetString();
                postedChannelId = resDoc.RootElement.GetProperty("channel_id").GetString();

                progress?.Report("Published successfully.");
                AppLogger.Info($"Discord dump published: {zipName} to channel {channelId}");
            }

            // 4. Forward to additional channels via Discord forward API
            if (postedMessageId != null && postedChannelId != null)
                await ForwardToChannelsAsync(botToken, postedMessageId, postedChannelId, progress);

            return true;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }

    /// <summary>
    /// Reads CreatedAt from a dump JSON file (root-level property).
    /// </summary>
    public static string? ReadCreatedAtFromDump(string dumpDir)
    {
        // Try chain_item_odds.json first (most commonly present), then others
        var candidates = new[] { "chain_item_odds.json", "areas.json", "events.json" };
        foreach (var file in candidates)
        {
            var path = Path.Combine(dumpDir, file);
            if (!File.Exists(path)) continue;

            try
            {
                using var stream = File.OpenRead(path);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("CreatedAt", out var ca))
                    return ca.GetString();
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Compares dump CreatedAt against last published date.
    /// Returns true if dump is newer (should publish).
    /// </summary>
    public static bool IsDumpNewer(string? dumpCreatedAt, DateTimeOffset? lastPublished)
    {
        if (string.IsNullOrEmpty(dumpCreatedAt)) return false;
        if (lastPublished == null) return true; // no previous publish → always newer

        if (DateTimeOffset.TryParse(dumpCreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dumpDate))
        {
            return dumpDate > lastPublished.Value;
        }

        return false;
    }

    // ── Private helpers ──

    /// <summary>
    /// Parses "The data itself was created at {timestamp}" from a Discord message.
    /// </summary>
    internal static DateTimeOffset? ParseCreatedAtFromMessage(string content)
    {
        const string marker = "The data itself was created at ";
        var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var timestamp = content[(idx + marker.Length)..].Trim();
        // Remove any trailing text after the timestamp
        var newlineIdx = timestamp.IndexOf('\n');
        if (newlineIdx >= 0) timestamp = timestamp[..newlineIdx].Trim();

        // Strip invisible/zero-width Unicode characters that Discord editor may insert
        timestamp = new string(timestamp.Where(c => c <= 127).ToArray()).Trim();
        // Discord escapes colons/dots in edited messages: "13\:11\:45" → "13:11:45"
        timestamp = timestamp.Replace("\\:", ":").Replace("\\.", ".");
        // Strip trailing punctuation (e.g. "...347." → "...347")
        timestamp = timestamp.TrimEnd('.', ',', ';', ' ');

        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var result))
            return result;

        // Regex fallback: extract ISO8601-like pattern (stops at digits/letters, not trailing dots)
        var m = System.Text.RegularExpressions.Regex.Match(timestamp,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[+-]\d{2}:?\d{2}|Z)?");
        if (m.Success && DateTimeOffset.TryParse(m.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out result))
            return result;

        AppLogger.Warn($"ParseCreatedAtFromMessage: failed to parse [{timestamp}] " +
                        $"(len={timestamp.Length}, hex={Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(timestamp))})");
        return null;
    }

    private static async Task ForwardToChannelsAsync(
        string botToken, string messageId, string sourceChannelId,
        IProgress<string>? progress)
    {
        foreach (var fwdId in ForwardChannelIds)
        {
            try
            {
                progress?.Report($"Forwarding to channel {fwdId}…");

                // Discord forward: message_reference with type 1 (FORWARD)
                var payload = JsonSerializer.Serialize(new
                {
                    message_reference = new
                    {
                        type = 1, // FORWARD
                        channel_id = sourceChannelId,
                        message_id = messageId
                    }
                });

                var fwdReq = new HttpRequestMessage(HttpMethod.Post,
                    $"{BaseUrl}/channels/{fwdId}/messages");
                fwdReq.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                fwdReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                var fwdRes = await _http.SendAsync(fwdReq);
                if (fwdRes.IsSuccessStatusCode)
                    progress?.Report($"Forwarded to {fwdId}.");
                else
                {
                    var body = await fwdRes.Content.ReadAsStringAsync();
                    progress?.Report($"Forward to {fwdId} failed: {fwdRes.StatusCode} — {body}");
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"Forward to {fwdId} error: {ex.Message}");
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest))
                File.Copy(file, dest);
        }
    }
}
