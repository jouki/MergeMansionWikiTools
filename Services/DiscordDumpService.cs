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
    // Bot token goes per-request (HttpRequestMessage.Headers.Authorization) — never on the shared client.
    private static readonly HttpClient _http = HttpClients.Discord;

    // ── Public API ──

    /// <summary>Newest dump post on the channel: message coordinates + parsed metadata.</summary>
    public record LastPublishedInfo(
        string MessageId,
        string ChannelId,
        DateTimeOffset? CreatedAt,      // data timestamp from "The data itself was created at …"
        string? MmwtVersion,            // from "MMWT Version: …" (null on pre-v0.23.38 posts)
        string Content);                // raw message content (for in-place content updates)

    public enum PublishMode
    {
        None,             // nothing to do (same data + same/older app version)
        NewPost,          // data newer than the last published dump → post a new message
        UpdateExisting,   // same data, newer MMWT → replace archive + MMWT line on the existing post
    }

    /// <summary>
    /// Reads the most recent message in the channel and extracts the "created at" timestamp
    /// from the second line (format: "The data itself was created at {ISO8601}").
    /// Returns null if no messages found or parsing fails.
    /// </summary>
    public static async Task<DateTimeOffset?> GetLastPublishedDateAsync(string botToken, string channelId)
        => (await GetLastPublishedInfoAsync(botToken, channelId))?.CreatedAt;

    /// <summary>
    /// Like <see cref="GetLastPublishedDateAsync"/> but returns the full info about the newest
    /// dump post (message id/channel, data timestamp, MMWT version, raw content) — needed for
    /// the update-in-place publish mode.
    /// </summary>
    public static async Task<LastPublishedInfo?> GetLastPublishedInfoAsync(string botToken, string channelId)
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

        // Search recent messages (newest first) for the "created at" pattern
        foreach (var msg in messages.EnumerateArray())
        {
            var content = msg.GetProperty("content").GetString();
            if (string.IsNullOrEmpty(content)) continue;

            var parsed = ParseCreatedAtFromMessage(content);
            if (!parsed.HasValue) continue;

            return new LastPublishedInfo(
                msg.GetProperty("id").GetString() ?? "",
                msg.GetProperty("channel_id").GetString() ?? "",
                parsed,
                ParseMmwtVersionFromMessage(content),
                content);
        }

        return null;
    }

    /// <summary>
    /// Decides what the Publish button should do for a dump with the given CreatedAt:
    /// newer data → NewPost; same data but newer running MMWT version than the one recorded
    /// on the post (missing line counts as older) → UpdateExisting; anything else → None.
    /// </summary>
    public static PublishMode DecidePublishMode(
        string? dumpCreatedAt, LastPublishedInfo? lastPublished, string currentAppVersion)
    {
        if (string.IsNullOrEmpty(dumpCreatedAt)) return PublishMode.None;
        if (lastPublished?.CreatedAt == null) return PublishMode.NewPost;

        if (!DateTimeOffset.TryParse(dumpCreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dumpDate))
            return PublishMode.None;

        if (dumpDate > lastPublished.CreatedAt.Value) return PublishMode.NewPost;
        if (dumpDate < lastPublished.CreatedAt.Value) return PublishMode.None;

        // Same data timestamp — offer an in-place update when the app is newer than the
        // version recorded on the post ("MMWT Version:" missing = old post = always older).
        var published = lastPublished.MmwtVersion;
        if (string.IsNullOrEmpty(published)) return PublishMode.UpdateExisting;
        return ApkDownloadService.CompareVersions(
                   currentAppVersion.TrimStart('v', 'V'), published.TrimStart('v', 'V')) > 0
            ? PublishMode.UpdateExisting
            : PublishMode.None;
    }

    /// <summary>
    /// Returns the post content with the "MMWT Version:" line value replaced (or the line
    /// appended when the post predates it). Everything else is kept verbatim.
    /// </summary>
    public static string BuildUpdatedContent(string originalContent, string newMmwtVersion)
    {
        var rx = new System.Text.RegularExpressions.Regex(@"(MMWT Version:).*");
        if (rx.IsMatch(originalContent))
            return rx.Replace(originalContent, $"$1 {newMmwtVersion}", 1);
        return originalContent.TrimEnd('\n', '\r') + $"\nMMWT Version: {newMmwtVersion}";
    }

    /// <summary>
    /// <summary>
    /// Additional channel IDs to forward the dump message to (same message + attachment).
    /// </summary>
    private static readonly string[] ForwardChannelIds = ["783385526387998743"];

    /// <summary>
    /// Creates the dump 7z archive (LZMA2 ultra, incl. Phone Raw Files, with the
    /// too-large retry without Experimental/). Returns null when the archive can't fit
    /// the 25 MB Discord limit. Caller owns (and must delete) the returned temp file.
    /// </summary>
    private static (string ZipPath, string ZipName)? BuildArchive(
        string dumpDir, DateTimeOffset now, IProgress<string>? progress)
    {
        progress?.Report("Creating 7z archive…");
        var archiveName = $"dump_{now:dd_MM_yyyy_HH_mm}.7z";
        var zipPath = Path.Combine(Path.GetTempPath(), archiveName);

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

                    // C: newest config archive by embedded CreatedAt (= the one the dumper actually
                    //    uses). Pulled files share the same mtime, so OrderBy(mtime) was non-deterministic.
                    var cDir = Path.Combine(dataDir, "C");
                    if (Directory.Exists(cDir))
                    {
                        var newestConfig = DumperService.SelectNewestConfigArchive(cDir)
                            ?? Directory.GetFiles(cDir).FirstOrDefault();
                        if (newestConfig != null)
                        {
                            var destDir = Path.Combine(phoneDir, "C");
                            Directory.CreateDirectory(destDir);
                            File.Copy(newestConfig, Path.Combine(destDir, Path.GetFileName(newestConfig)));
                            hasAny = true;
                        }
                    }

                    // P: ALL patch files — the dump unions every snapshot (multi-snapshot AB discovery),
                    //    so a faithful re-dump needs them all, not just the newest one.
                    var pDir = Path.Combine(dataDir, "P");
                    if (Directory.Exists(pDir))
                    {
                        var patchFiles = Directory.GetFiles(pDir);
                        if (patchFiles.Length > 0)
                        {
                            var destDir = Path.Combine(phoneDir, "P");
                            Directory.CreateDirectory(destDir);
                            foreach (var pf in patchFiles)
                                File.Copy(pf, Path.Combine(destDir, Path.GetFileName(pf)));
                            hasAny = true;
                        }
                    }

                    // L: language (single file).
                    var lDir = Path.Combine(dataDir, "L");
                    if (Directory.Exists(lDir))
                    {
                        var newestLang = Directory.GetFiles(lDir)
                            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                            .FirstOrDefault();
                        if (newestLang != null)
                        {
                            var destDir = Path.Combine(phoneDir, "L");
                            Directory.CreateDirectory(destDir);
                            File.Copy(newestLang, Path.Combine(destDir, Path.GetFileName(newestLang)));
                            hasAny = true;
                        }
                    }

                    // Extra metadata files (at the root of Phone Raw Files): AB memberships + game version.
                    foreach (var extra in new[] { AbGroupsService.LastSessionFileName, PhoneDetectionService.GameVersionFileName, PhoneDetectionService.UnityVersionFileName })
                    {
                        var src = Path.Combine(dataDir, extra);
                        if (File.Exists(src))
                        {
                            Directory.CreateDirectory(phoneDir);
                            File.Copy(src, Path.Combine(phoneDir, extra), overwrite: true);
                            hasAny = true;
                        }
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
            // Source for compression = combined (dump + Phone Raw Files) when available.
            // Kept on disk until after the size-retry so Phone Raw Files survive the retry too.
            var sourceDir = combinedDir ?? dumpDir;
            compressor.CompressDirectory(sourceDir, zipPath);

            var archiveSize = new FileInfo(zipPath).Length;
            progress?.Report($"7z created: {archiveName} ({archiveSize / (1024 * 1024.0):F1} MB)");

            // Discord file size limit depends on server boost tier (8 MB for tier 0)
            if (archiveSize > 8 * 1024 * 1024)
            {
                // Retry without Experimental/ subfolder — but keep Phone Raw Files (re-dump source).
                progress?.Report("7z too large — retrying without Experimental/ folder…");
                File.Delete(zipPath);

                // Copy files to temp dir excluding Experimental/ (relative to sourceDir, so the
                // Phone Raw Files folder in combinedDir is preserved).
                var tempDir = Path.Combine(Path.GetTempPath(), $"dump_filtered_{now:HHmmss}");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
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

            if (combinedDir != null)
                try { Directory.Delete(combinedDir, true); } catch { }

            if (archiveSize > 25 * 1024 * 1024)
            {
                progress?.Report("ERROR: Archive still exceeds 25 MB Discord limit.");
                try { File.Delete(zipPath); } catch { }
                return null;
            }

        return (zipPath, archiveName);
    }

    /// <summary>
    /// Creates a ZIP from the dump folder and uploads it to the Discord channel with a message.
    /// Also forwards to additional channels.
    /// </summary>
    public static async Task<bool> PublishDumpAsync(
        string botToken, string channelId, string dumpDir, string createdAt,
        IProgress<string>? progress = null)
    {
        var now = DateTimeOffset.Now;
        var archive = BuildArchive(dumpDir, now, progress);
        if (archive == null) return false;
        var (zipPath, zipName) = archive.Value;

        try
        {
            // 2. Build message
            var messageText = $"New dumps from {now:dd.MM.yyyy HH:mm}{now:zzz}.\n" +
                              $"The data itself was created at {createdAt}";

            // Game Version (game client version, captured from the phone's Unity Analytics at pull time).
            var gv = PhoneDetectionService.ReadPulledGameVersion();
            if (!string.IsNullOrEmpty(gv))
                messageText += $"\nGame Version: {gv}";
            messageText += $"\nMMWT Version: {MergeMansionWikiTools.Models.AppVersion.Version}";

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
    /// Update-in-place publish: same data timestamp already on Discord, but the running MMWT
    /// is newer — PATCHes the existing post: replaces the attached archive (attachments=[] +
    /// new files[0]) and swaps the "MMWT Version:" line in the content. The bot must be the
    /// author of the message (dump posts are). Forwards are snapshots and are NOT re-sent.
    /// </summary>
    public static async Task<bool> UpdateDumpMessageAsync(
        string botToken, LastPublishedInfo target, string dumpDir,
        IProgress<string>? progress = null)
    {
        var now = DateTimeOffset.Now;
        var archive = BuildArchive(dumpDir, now, progress);
        if (archive == null) return false;
        var (zipPath, zipName) = archive.Value;

        try
        {
            var newContent = BuildUpdatedContent(target.Content,
                MergeMansionWikiTools.Models.AppVersion.Version);

            progress?.Report("Updating existing Discord post…");
            var fileBytes = await File.ReadAllBytesAsync(zipPath);

            using var form = new MultipartFormDataContent();
            // payload_json with attachments=[] drops the old archive; files[0] attaches the new one
            var payload = JsonSerializer.Serialize(new
            {
                content = newContent,
                attachments = Array.Empty<object>()
            });
            form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");

            var fc = new ByteArrayContent(fileBytes);
            fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fc, "files[0]", zipName);

            var request = new HttpRequestMessage(HttpMethod.Patch,
                $"{BaseUrl}/channels/{target.ChannelId}/messages/{target.MessageId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
            request.Content = form;

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                progress?.Report($"Discord API error: {response.StatusCode} — {body}");
                AppLogger.Warn($"Discord dump update failed: {response.StatusCode} — {body}");
                return false;
            }

            progress?.Report("Existing post updated (archive + MMWT version).");
            AppLogger.Info($"Discord dump post {target.MessageId} updated with {zipName}");

            // Forwards are immutable snapshots — editing the original doesn't propagate.
            // Delete the stale forward(s) for this data and re-forward the updated original.
            await RefreshForwardsAsync(botToken, target.MessageId, target.ChannelId,
                target.CreatedAt, progress);
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

    /// <summary>
    /// Parses "Game Version: {version}" from a Discord dump message (added v0.23.38).
    /// Returns null for older messages that don't carry the line (backward compatible).
    /// </summary>
    internal static string? ParseGameVersionFromMessage(string content)
    {
        const string marker = "Game Version:";
        var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var v = content[(idx + marker.Length)..].Trim();
        var newlineIdx = v.IndexOf('\n');
        if (newlineIdx >= 0) v = v[..newlineIdx].Trim();

        // Strip invisible/zero-width chars + un-escape Discord editor escaping ("26\.05\.01")
        v = new string(v.Where(c => c <= 127).ToArray()).Trim();
        v = v.Replace("\\.", ".").Replace("\\:", ":");
        v = v.TrimEnd('.', ',', ';', ' ');

        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>
    /// Parses "MMWT Version: {version}" from a Discord dump message (the app version that
    /// produced the dump). Returns null for posts that predate the line.
    /// </summary>
    internal static string? ParseMmwtVersionFromMessage(string content)
    {
        const string marker = "MMWT Version:";
        var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var v = content[(idx + marker.Length)..].Trim();
        var newlineIdx = v.IndexOf('\n');
        if (newlineIdx >= 0) v = v[..newlineIdx].Trim();

        // Strip invisible/zero-width chars + un-escape Discord editor escaping ("v0\.23\.52")
        v = new string(v.Where(c => c <= 127).ToArray()).Trim();
        v = v.Replace("\\.", ".").Replace("\\:", ":");
        v = v.TrimEnd('.', ',', ';', ' ');

        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>
    /// True when a forwarded message's snapshot content describes the same dump data
    /// (identical "created at" timestamp). Used to locate the stale forward to replace:
    /// Discord forwards are immutable snapshots, so an edited original does NOT propagate —
    /// the only way to refresh a forward is delete + re-forward.
    /// </summary>
    internal static bool ForwardSnapshotMatchesData(string? snapshotContent, DateTimeOffset targetCreatedAt)
    {
        if (string.IsNullOrEmpty(snapshotContent)) return false;
        var parsed = ParseCreatedAtFromMessage(snapshotContent);
        return parsed.HasValue && parsed.Value == targetCreatedAt;
    }

    /// <summary>
    /// Builds the re-upload note posted alongside a refreshed forward. Embeds the data timestamp
    /// via the same "The data itself was created at …" phrase the parser recognizes, so the NEXT
    /// update can find and delete this note too. Null when no timestamp is known.
    /// </summary>
    internal static string? BuildForwardNote(DateTimeOffset? dataCreatedAt)
        => dataCreatedAt.HasValue
            ? "🔄 Updated re-upload of the original dump post — refreshed to fix missing/incomplete data.\n" +
              $"(The data itself was created at {dataCreatedAt.Value:yyyy-MM-ddTHH:mm:ss.fffzzz})"
            : null;

    /// <summary>
    /// Refreshes the forwarded copies after an in-place update of the original: deletes each
    /// forward channel's stale forward AND stale note for this data timestamp, then posts a fresh
    /// note + forwards the (now-updated) original so the snapshot captures the new archive.
    /// Discord forbids content ON a forward (error 160011) — the note is a separate message,
    /// exactly how the Discord client sends the "optional message" when forwarding. Non-fatal per channel.
    /// </summary>
    private static async Task RefreshForwardsAsync(
        string botToken, string sourceMessageId, string sourceChannelId,
        DateTimeOffset? dataCreatedAt, IProgress<string>? progress)
    {
        if (dataCreatedAt.HasValue)
        {
            foreach (var fwdId in ForwardChannelIds)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get,
                        $"{BaseUrl}/channels/{fwdId}/messages?limit=50");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                    var res = await _http.SendAsync(req);
                    if (!res.IsSuccessStatusCode) continue;

                    var json = await res.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                    foreach (var msg in doc.RootElement.EnumerateArray())
                    {
                        // Match either a forward (its snapshot content) OR our own note (its content).
                        string? matchContent = null;
                        if (msg.TryGetProperty("message_snapshots", out var snaps) &&
                            snaps.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var snap in snaps.EnumerateArray())
                                if (snap.TryGetProperty("message", out var sm) &&
                                    sm.TryGetProperty("content", out var c))
                                { matchContent = c.GetString(); break; }
                        }
                        else if (msg.TryGetProperty("content", out var oc))
                        {
                            matchContent = oc.GetString();
                        }

                        if (!ForwardSnapshotMatchesData(matchContent, dataCreatedAt.Value)) continue;

                        var msgId = msg.GetProperty("id").GetString();
                        var delReq = new HttpRequestMessage(HttpMethod.Delete,
                            $"{BaseUrl}/channels/{fwdId}/messages/{msgId}");
                        delReq.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                        var delRes = await _http.SendAsync(delReq);
                        if (delRes.IsSuccessStatusCode)
                            progress?.Report($"Removed stale forward/note in {fwdId}.");
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"Forward refresh (delete) in {fwdId} error: {ex.Message}");
                }
            }
        }

        // Post note (separate message) + forward the updated original.
        await ForwardToChannelsAsync(botToken, sourceMessageId, sourceChannelId, progress,
            note: BuildForwardNote(dataCreatedAt));
    }

    private static async Task ForwardToChannelsAsync(
        string botToken, string messageId, string sourceChannelId,
        IProgress<string>? progress, string? note = null)
    {
        foreach (var fwdId in ForwardChannelIds)
        {
            try
            {
                // Note first, as its OWN message — Discord forbids content on a forward (error
                // 160011). This mirrors the client: the "optional message" when forwarding is a
                // separate message, not content embedded in the forward. Best-effort.
                if (!string.IsNullOrEmpty(note))
                {
                    var notePayload = JsonSerializer.Serialize(new { content = note });
                    var noteReq = new HttpRequestMessage(HttpMethod.Post,
                        $"{BaseUrl}/channels/{fwdId}/messages");
                    noteReq.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                    noteReq.Content = new StringContent(notePayload, Encoding.UTF8, "application/json");
                    var noteRes = await _http.SendAsync(noteReq);
                    if (!noteRes.IsSuccessStatusCode)
                        progress?.Report($"Note to {fwdId} failed: {noteRes.StatusCode}");
                }

                progress?.Report($"Forwarding to channel {fwdId}…");

                // Discord forward: message_reference with type 1 (FORWARD), no content.
                var payload = JsonSerializer.Serialize(new
                {
                    message_reference = new { type = 1, channel_id = sourceChannelId, message_id = messageId }
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
