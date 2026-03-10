using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

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
        // 1. Create ZIP
        progress?.Report("Creating ZIP archive…");
        var now = DateTimeOffset.Now;
        var zipName = $"dump_{now:dd_MM_yyyy_HH_mm}.zip";
        var zipPath = Path.Combine(Path.GetTempPath(), zipName);

        try
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(dumpDir, zipPath, CompressionLevel.Optimal, false);

            var zipSize = new FileInfo(zipPath).Length;
            progress?.Report($"ZIP created: {zipName} ({zipSize / (1024 * 1024.0):F1} MB)");

            // Discord file size limit: 25 MB for regular bots
            if (zipSize > 25 * 1024 * 1024)
            {
                progress?.Report("ERROR: ZIP exceeds 25 MB Discord limit.");
                return false;
            }

            // 2. Build message
            var messageText = $"New dumps from {now:dd.MM.yyyy HH:mm}{now:zzz}.\n" +
                              $"The data itself was created at {createdAt}";

            // 3. Upload via multipart form
            progress?.Report("Uploading to Discord…");

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(messageText), "content");

            var fileBytes = await File.ReadAllBytesAsync(zipPath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(fileContent, "files[0]", zipName);

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BaseUrl}/channels/{channelId}/messages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
            request.Content = form;

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                progress?.Report("Published successfully.");
                AppLogger.Info($"Discord dump published: {zipName} to channel {channelId}");

                // Forward to additional channels
                foreach (var fwdId in ForwardChannelIds)
                {
                    try
                    {
                        progress?.Report($"Forwarding to channel {fwdId}…");
                        using var fwdForm = new MultipartFormDataContent();
                        fwdForm.Add(new StringContent(messageText), "content");
                        var fwdFileContent = new ByteArrayContent(fileBytes);
                        fwdFileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                        fwdForm.Add(fwdFileContent, "files[0]", zipName);

                        var fwdReq = new HttpRequestMessage(HttpMethod.Post,
                            $"{BaseUrl}/channels/{fwdId}/messages");
                        fwdReq.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                        fwdReq.Content = fwdForm;

                        var fwdRes = await _http.SendAsync(fwdReq);
                        if (fwdRes.IsSuccessStatusCode)
                            progress?.Report($"Forwarded to {fwdId}.");
                        else
                            progress?.Report($"Forward to {fwdId} failed: {fwdRes.StatusCode}");
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"Forward to {fwdId} error: {ex.Message}");
                    }
                }

                return true;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                progress?.Report($"Discord API error: {response.StatusCode} — {body}");
                AppLogger.Warn($"Discord publish failed: {response.StatusCode} — {body}");
                return false;
            }
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
    private static DateTimeOffset? ParseCreatedAtFromMessage(string content)
    {
        const string marker = "The data itself was created at ";
        var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var timestamp = content[(idx + marker.Length)..].Trim();
        // Remove any trailing text after the timestamp
        var newlineIdx = timestamp.IndexOf('\n');
        if (newlineIdx >= 0) timestamp = timestamp[..newlineIdx].Trim();

        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var result))
            return result;

        return null;
    }
}
