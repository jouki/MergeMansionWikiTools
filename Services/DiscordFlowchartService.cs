using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Publishes area flowchart SVGs to a Discord thread.
/// Matches existing messages by content ("## AreaName") so any instance can update.
/// Enforces wiki ordering — deletes and recreates messages when order changes.
/// </summary>
internal static class DiscordFlowchartService
{
    private const string BaseUrl = "https://discord.com/api/v10";
    internal const string DefaultThreadId = "1485372779002986667";
    private static readonly HttpClient _http = new();

    // ── Wiki area ordering (fetched at runtime from Module:Datatable/Areas/Mapping) ──

    public record WikiAreaMapping(double OrderingIndex, double Right = 0, double Bot = 0);

    private static Dictionary<string, WikiAreaMapping>? _wikiMapping;
    private static readonly SemaphoreSlim _mappingLock = new(1, 1);

    /// <summary>
    /// Fetches and caches the area mapping from wiki. Safe to call multiple times.
    /// </summary>
    public static async Task EnsureMappingLoadedAsync()
    {
        if (_wikiMapping != null) return;
        await _mappingLock.WaitAsync();
        try
        {
            if (_wikiMapping != null) return;
            _wikiMapping = await FetchWikiMappingAsync();
        }
        finally { _mappingLock.Release(); }
    }

    /// <summary>Forces a re-fetch (e.g., after user edits wiki).</summary>
    public static async Task RefreshMappingAsync()
    {
        await _mappingLock.WaitAsync();
        try { _wikiMapping = await FetchWikiMappingAsync(); }
        finally { _mappingLock.Release(); }
    }

    private static async Task<Dictionary<string, WikiAreaMapping>> FetchWikiMappingAsync()
    {
        var result = new Dictionary<string, WikiAreaMapping>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Fandom blocks ?action=raw (403), use MediaWiki API instead
            var apiUrl = "https://merge-mansion.fandom.com/api.php"
                + "?action=query&titles=Module:Datatable/Areas/Mapping"
                + "&prop=revisions&rvprop=content&rvslots=main&format=json";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var json = await _http.GetStringAsync(apiUrl, cts.Token);
            using var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            string lua = "";
            foreach (var page in pages.EnumerateObject())
            {
                lua = page.Value.GetProperty("revisions")[0]
                    .GetProperty("slots").GetProperty("main")
                    .GetProperty("*").GetString() ?? "";
                break;
            }

            // Parse: ["Area Name"] = {orderingIndex = 51, right = -14, bot = 6},
            foreach (Match m in Regex.Matches(lua, @"\[""([^""]+)""\]\s*=\s*\{([^}]*)\}"))
            {
                var props = m.Groups[2].Value;
                var idxMatch = Regex.Match(props, @"orderingIndex\s*=\s*([\d.]+)");
                if (!idxMatch.Success) continue;

                var idx = double.Parse(idxMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                double right = 0, bot = 0;

                var rm = Regex.Match(props, @"right\s*=\s*(-?[\d.]+)");
                if (rm.Success) right = double.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture);
                var bm = Regex.Match(props, @"bot\s*=\s*(-?[\d.]+)");
                if (bm.Success) bot = double.Parse(bm.Groups[1].Value, CultureInfo.InvariantCulture);

                var mapping = new WikiAreaMapping(idx, right, bot);
                var name = m.Groups[1].Value;
                result[name] = mapping;
                var norm = Normalize(name);
                if (!result.ContainsKey(norm))
                    result[norm] = mapping;
            }

            AppLogger.Info($"Wiki area mapping loaded: {result.Count / 2} areas");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to fetch wiki area mapping: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Normalizes a display name for ordering lookup:
    /// strips apostrophes, "The " prefix, "-" → " ".
    /// </summary>
    private static string Normalize(string name)
    {
        var n = name.Replace("'", "").Replace("\u2019", "").Replace("-", " ");
        if (n.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            n = n[4..];
        return n.Trim();
    }

    /// <summary>
    /// Returns the wiki ordering index for an area.
    /// Unknown areas return 999 (sorted to end).
    /// </summary>
    public static double GetOrderingIndex(string displayName)
    {
        if (_wikiMapping is { Count: > 0 })
        {
            if (_wikiMapping.TryGetValue(displayName, out var m)) return m.OrderingIndex;
            if (_wikiMapping.TryGetValue(Normalize(displayName), out m)) return m.OrderingIndex;
        }
        return 999;
    }

    /// <summary>
    /// Returns wiki image crop offsets (right %, bottom px) for an area.
    /// </summary>
    public static (double right, double bot) GetImageOffset(string displayName)
    {
        if (_wikiMapping is { Count: > 0 })
        {
            if (_wikiMapping.TryGetValue(displayName, out var m)) return (m.Right, m.Bot);
            if (_wikiMapping.TryGetValue(Normalize(displayName), out m)) return (m.Right, m.Bot);
        }
        return (0, 0);
    }

    // ── Content format ────────────────────────────────────────────────

    private static string FormatContent(string areaDisplayName) => $"## {areaDisplayName}";

    private static string? ParseAreaName(string messageContent)
    {
        if (!messageContent.StartsWith("## ")) return null;
        var name = messageContent[3..].Trim();
        var nl = name.IndexOf('\n');
        if (nl >= 0) name = name[..nl].Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    // ── Fetch existing messages (ordered by creation time) ────────────

    private static async Task<List<(string areaName, string messageId)>> FetchExistingMessagesOrderedAsync(
        string botToken, string threadId)
    {
        var raw = new List<(string areaName, string messageId, ulong snowflake)>();
        string? beforeId = null;

        for (int page = 0; page < 10; page++)
        {
            var url = $"{BaseUrl}/channels/{threadId}/messages?limit=100";
            if (beforeId != null) url += $"&before={beforeId}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) break;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var messages = doc.RootElement;

            if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
                break;

            foreach (var msg in messages.EnumerateArray())
            {
                var content = msg.GetProperty("content").GetString() ?? "";
                var id = msg.GetProperty("id").GetString() ?? "";

                var areaName = ParseAreaName(content);
                if (areaName != null && ulong.TryParse(id, out var snowflake))
                    raw.Add((areaName, id, snowflake));

                beforeId = id;
            }

            if (messages.GetArrayLength() < 100) break;
        }

        // Sort by snowflake ascending = chronological = display order in thread
        raw.Sort((a, b) => a.snowflake.CompareTo(b.snowflake));
        return raw.Select(r => (r.areaName, r.messageId)).ToList();
    }

    // ── Publish all ──────────────────────────────────────────────────

    /// <summary>
    /// Publishes flowcharts to a Discord thread with correct wiki ordering.
    /// Re-uses existing message slots: edits their content/attachment to match
    /// the desired order. Creates new messages only for extras, deletes leftovers.
    /// </summary>
    public static async Task<(int created, int updated, int deleted, int failed)> PublishAllAsync(
        string botToken,
        string threadId,
        List<(LuaArea area, string svg)> flowcharts,
        IProgress<string>? progress = null)
    {
        await EnsureMappingLoadedAsync();
        // Discord auto-archives inactive threads — every API write returns 50083 "Thread is archived".
        // Unarchive once at the start of the batch (idempotent — no-op if already active).
        await EnsureThreadUnarchivedAsync(botToken, threadId, progress);
        progress?.Report("Reading existing messages…");
        var existingOrdered = await FetchExistingMessagesOrderedAsync(botToken, threadId);

        var sorted = flowcharts
            .OrderBy(f => GetOrderingIndex(f.area.DisplayName))
            .ToList();

        int created = 0, updated = 0, deleted = 0, failed = 0;
        int total = Math.Max(sorted.Count, existingOrdered.Count);

        for (int i = 0; i < total; i++)
        {
            bool hasSlot = i < existingOrdered.Count;
            bool hasArea = i < sorted.Count;

            if (hasArea && hasSlot)
            {
                // Edit existing message to show the correct area at this position
                var (area, svg) = sorted[i];
                var msgId = existingOrdered[i].messageId;
                progress?.Report($"({i + 1}/{sorted.Count}) {area.DisplayName}…");

                var svgBytes = Encoding.UTF8.GetBytes(svg);
                var fileName = SafeFileName(area.DisplayName);
                var content = FormatContent(area.DisplayName);
                if (await EditMessageAsync(botToken, threadId, msgId, content, svgBytes, fileName))
                    updated++;
                else
                    failed++;
            }
            else if (hasArea)
            {
                // More areas than existing messages — create new
                var (area, svg) = sorted[i];
                progress?.Report($"({i + 1}/{sorted.Count}) {area.DisplayName} (new)…");
                if (await SendArea(botToken, threadId, area.DisplayName, svg))
                    created++;
                else
                    failed++;
            }
            else if (hasSlot)
            {
                // More existing messages than areas — delete leftover
                progress?.Report($"Deleting leftover message…");
                if (await DeleteMessageAsync(botToken, threadId, existingOrdered[i].messageId))
                    deleted++;
            }

            if (i < total - 1) await Task.Delay(2000);
        }

        return (created, updated, deleted, failed);
    }

    // ── Publish one area ─────────────────────────────────────────────

    /// <summary>
    /// Publishes a single area to Discord. Checks ordering against wiki
    /// and fixes affected messages if needed (requires generateSvg callback).
    /// </summary>
    public static async Task<(bool success, bool wasUpdate, int reordered)> PublishOneAsync(
        string botToken,
        string threadId,
        string areaDisplayName,
        string svg,
        Func<string, Task<string?>>? generateSvg = null,
        IProgress<string>? progress = null)
    {
        await EnsureMappingLoadedAsync();
        await EnsureThreadUnarchivedAsync(botToken, threadId, progress);
        progress?.Report("Reading existing messages…");
        var existing = await FetchExistingMessagesOrderedAsync(botToken, threadId);

        var currentNames = existing.Select(e => e.areaName).ToList();
        bool isNew = !currentNames.Any(n => n.Equals(areaDisplayName, StringComparison.OrdinalIgnoreCase));

        // Build desired order from all area names (existing + new if applicable)
        var allNames = new List<string>(currentNames);
        if (isNew) allNames.Add(areaDisplayName);
        var desiredOrder = allNames
            .OrderBy(n => GetOrderingIndex(n))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Check if existing messages already match desired order
        bool orderingOk = existing.Select(e => e.areaName)
            .SequenceEqual(desiredOrder.Take(existing.Count), StringComparer.OrdinalIgnoreCase);

        // ── Fast path: ordering correct ──
        if (orderingOk)
        {
            if (!isNew)
            {
                var targetId = existing.First(e =>
                    e.areaName.Equals(areaDisplayName, StringComparison.OrdinalIgnoreCase)).messageId;
                progress?.Report($"Updating {areaDisplayName}…");
                var ok = await EditMessageAsync(botToken, threadId, targetId,
                    FormatContent(areaDisplayName),
                    Encoding.UTF8.GetBytes(svg),
                    SafeFileName(areaDisplayName));
                return (ok, true, 0);
            }
            else
            {
                // New area appends at end (existing order is correct prefix)
                progress?.Report($"Creating {areaDisplayName}…");
                var ok = await SendArea(botToken, threadId, areaDisplayName, svg);
                return (ok, false, 0);
            }
        }

        // ── Ordering wrong — fall back to simple update if no SVG generator ──
        if (generateSvg == null)
        {
            if (!isNew)
            {
                var targetId = existing.First(e =>
                    e.areaName.Equals(areaDisplayName, StringComparison.OrdinalIgnoreCase)).messageId;
                progress?.Report($"Updating {areaDisplayName}…");
                var ok = await EditMessageAsync(botToken, threadId, targetId,
                    FormatContent(areaDisplayName),
                    Encoding.UTF8.GetBytes(svg),
                    SafeFileName(areaDisplayName));
                return (ok, true, 0);
            }
            else
            {
                progress?.Report($"Creating {areaDisplayName}…");
                var ok = await SendArea(botToken, threadId, areaDisplayName, svg);
                return (ok, false, 0);
            }
        }

        // ── Reordering with SVG generation ──

        // If new area, create a message to have enough slots
        if (isNew)
        {
            progress?.Report($"Creating message for {areaDisplayName}…");
            var newId = await CreateMessageAsync(botToken, threadId,
                FormatContent(areaDisplayName),
                Encoding.UTF8.GetBytes(svg),
                SafeFileName(areaDisplayName));
            if (newId == null) return (false, false, 0);
            existing.Add((areaDisplayName, newId));
            await Task.Delay(2000);
        }

        // Phase 1: Collect SVGs for all slots that need editing
        var edits = new List<(int slot, string name, byte[] svgBytes, string fileName)>();
        bool allResolved = true;

        for (int i = 0; i < desiredOrder.Count && i < existing.Count; i++)
        {
            var desired = desiredOrder[i];
            var current = existing[i].areaName;
            bool isTarget = desired.Equals(areaDisplayName, StringComparison.OrdinalIgnoreCase);
            bool slotCorrect = current.Equals(desired, StringComparison.OrdinalIgnoreCase);

            if (slotCorrect && !isTarget) continue;

            string? slotSvg;
            if (isTarget)
                slotSvg = svg;
            else
            {
                progress?.Report($"Generating {desired}…");
                slotSvg = await generateSvg(desired);
            }

            if (slotSvg == null)
            {
                allResolved = false;
                break;
            }

            edits.Add((i, desired, Encoding.UTF8.GetBytes(slotSvg), SafeFileName(desired)));
        }

        // If we couldn't resolve all SVGs, just update the target in its current position
        if (!allResolved)
        {
            int targetIdx = isNew
                ? existing.Count - 1
                : existing.FindIndex(e => e.areaName.Equals(areaDisplayName, StringComparison.OrdinalIgnoreCase));
            if (targetIdx >= 0)
            {
                progress?.Report($"Updating {areaDisplayName} (ordering fix skipped)…");
                await EditMessageAsync(botToken, threadId, existing[targetIdx].messageId,
                    FormatContent(areaDisplayName),
                    Encoding.UTF8.GetBytes(svg),
                    SafeFileName(areaDisplayName));
            }
            return (targetIdx >= 0, !isNew, 0);
        }

        // Phase 2: Apply all edits
        int reordered = 0;
        for (int j = 0; j < edits.Count; j++)
        {
            var (slot, name, svgBytes, fileName) = edits[j];
            bool isTarget = name.Equals(areaDisplayName, StringComparison.OrdinalIgnoreCase);
            bool slotChanged = !existing[slot].areaName.Equals(name, StringComparison.OrdinalIgnoreCase);

            progress?.Report(slotChanged
                ? $"Fixing order: {name} → position {slot + 1}…"
                : $"Updating {name}…");

            await EditMessageAsync(botToken, threadId, existing[slot].messageId,
                FormatContent(name), svgBytes, fileName);

            if (!isTarget && slotChanged) reordered++;

            if (j < edits.Count - 1) await Task.Delay(2000);
        }

        return (true, !isNew, reordered);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string SafeFileName(string displayName) =>
        string.Join("_", displayName.Split(Path.GetInvalidFileNameChars())) + ".html";

    private static async Task<bool> SendArea(
        string botToken, string threadId, string areaDisplayName, string svg)
    {
        var svgBytes = Encoding.UTF8.GetBytes(svg);
        var fileName = SafeFileName(areaDisplayName);
        var content = FormatContent(areaDisplayName);
        return await CreateMessageAsync(botToken, threadId, content, svgBytes, fileName) != null;
    }

    // ── Discord API ──────────────────────────────────────────────────

    /// <summary>
    /// Unarchives the thread if Discord auto-archived it. Idempotent — sending PATCH with
    /// archived=false on an already-active thread is a no-op. Without this, every API write
    /// to an archived thread fails with HTTP 400 / code 50083 ("Thread is archived"), which
    /// the retry logic would then loop on indefinitely.
    /// </summary>
    private static async Task EnsureThreadUnarchivedAsync(
        string botToken, string threadId, IProgress<string>? progress = null)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { archived = false, locked = false });
            HttpRequestMessage MakeRequest()
            {
                var req = new HttpRequestMessage(HttpMethod.Patch, $"{BaseUrl}/channels/{threadId}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                return req;
            }

            var response = await SendWithRetryAsync(MakeRequest(), MakeRequest);
            if (response.IsSuccessStatusCode)
            {
                AppLogger.Debug($"[Discord] Thread {threadId} unarchived (or already active)");
                return;
            }
            // Non-success — log body but don't throw; the subsequent edit/create call will surface
            // the real error if unarchive truly failed (e.g. missing permission).
            var body = await response.Content.ReadAsStringAsync();
            AppLogger.Warn($"[Discord] Thread unarchive returned {response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Discord] Thread unarchive error: {ex.Message}");
        }
    }

    // ── Concurrency cap (no time-based throttle) ──
    // Discord does its own rate limiting via 429 responses. We just cap concurrent in-flight
    // requests so we don't open arbitrary sockets, and let SendWithRetryAsync honor whatever
    // retry_after Discord tells us (precisely from body, not the rounded-up header).
    private static readonly SemaphoreSlim _rateLimitSlots = new(5, 5);

    private static async Task AcquireRateSlotAsync() => await _rateLimitSlots.WaitAsync();
    private static void ReleaseRateSlot() => _rateLimitSlots.Release();

    /// <summary>
    /// Sends an HTTP request with automatic retry on 429 (rate limit) + comprehensive
    /// per-request logging (status, X-RateLimit-* headers, body on error). Throttled to
    /// 5 starts per second via the global slot semaphore.
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request, Func<HttpRequestMessage> cloneRequest, int maxRetries = 3)
    {
        await AcquireRateSlotAsync();
        HttpResponseMessage response;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var method = request.Method.Method;
            var path = request.RequestUri?.AbsolutePath ?? "?";
            response = await _http.SendAsync(request);
            sw.Stop();
            LogDiscordResponse(method, path, response, sw.ElapsedMilliseconds);

            for (int attempt = 0; attempt < maxRetries && (int)response.StatusCode == 429; attempt++)
            {
                // PREFER body's retry_after (fractional seconds, e.g. 0.338) over the Retry-After
                // header which Discord rounds up to whole seconds (e.g. 1). Saves ~0.5-1s per retry.
                double waitSeconds = 1.0;
                bool fromBody = false;
                try
                {
                    var body = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("retry_after", out var ra))
                    {
                        waitSeconds = ra.GetDouble();
                        fromBody = true;
                    }
                }
                catch { /* fall through to header */ }

                if (!fromBody && response.Headers.TryGetValues("Retry-After", out var retryValues) &&
                    double.TryParse(retryValues.FirstOrDefault(), out var headerWait))
                    waitSeconds = headerWait;

                // Tiny 50ms cushion only — Discord's retry_after already includes its own buffer.
                AppLogger.Info($"[Discord] {method} {path}: 429 — waiting {waitSeconds:F3}s {(fromBody ? "(body)" : "(header)")} (attempt {attempt + 1}/{maxRetries})");
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds) + TimeSpan.FromMilliseconds(50));

                sw.Restart();
                response = await _http.SendAsync(cloneRequest());
                sw.Stop();
                LogDiscordResponse(method, path, response, sw.ElapsedMilliseconds, retryAttempt: attempt + 1);
            }
        }
        finally
        {
            ReleaseRateSlot();
        }

        return response;
    }

    /// <summary>Logs Discord response — status, rate-limit headers, and body excerpt on error.</summary>
    private static void LogDiscordResponse(string method, string path,
        HttpResponseMessage response, long elapsedMs, int? retryAttempt = null)
    {
        string Header(string name) =>
            response.Headers.TryGetValues(name, out var vals) ? (vals.FirstOrDefault() ?? "-") : "-";

        var bucket = Header("X-RateLimit-Bucket");
        var rlLimit = Header("X-RateLimit-Limit");
        var rlRemaining = Header("X-RateLimit-Remaining");
        var rlReset = Header("X-RateLimit-Reset-After");
        var rlScope = Header("X-RateLimit-Scope");
        var retryTag = retryAttempt.HasValue ? $" retry#{retryAttempt}" : "";

        var bodyExcerpt = "";
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                bodyExcerpt = body.Length > 200 ? body.Substring(0, 200) + "…" : body;
                bodyExcerpt = $" body={bodyExcerpt}";
            }
            catch { }
        }

        AppLogger.Debug(
            $"[Discord] {method} {path} -> {(int)response.StatusCode} {response.StatusCode}{retryTag} " +
            $"({elapsedMs}ms) RL[remaining={rlRemaining}/{rlLimit} reset={rlReset}s bucket={bucket} scope={rlScope}]{bodyExcerpt}");
    }

    private static async Task<string?> CreateMessageAsync(
        string botToken, string threadId,
        string content, byte[] fileBytes, string fileName)
    {
        try
        {
            HttpRequestMessage MakeRequest()
            {
                var form = new MultipartFormDataContent();
                var payload = JsonSerializer.Serialize(new { content });
                form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");

                var fc = new ByteArrayContent(fileBytes);
                fc.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
                form.Add(fc, "files[0]", fileName);

                var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{BaseUrl}/channels/{threadId}/messages");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                req.Content = form;
                return req;
            }

            var response = await SendWithRetryAsync(MakeRequest(), MakeRequest);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                AppLogger.Warn($"Discord flowchart create failed: {response.StatusCode} — {body}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("id").GetString();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Discord flowchart create error: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> EditMessageAsync(
        string botToken, string threadId, string messageId,
        string content, byte[] fileBytes, string fileName)
    {
        try
        {
            HttpRequestMessage MakeRequest()
            {
                var form = new MultipartFormDataContent();
                var payload = JsonSerializer.Serialize(new
                {
                    content,
                    attachments = new[] { new { id = 0, filename = fileName } }
                });
                form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");

                var fc = new ByteArrayContent(fileBytes);
                fc.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
                form.Add(fc, "files[0]", fileName);

                var req = new HttpRequestMessage(HttpMethod.Patch,
                    $"{BaseUrl}/channels/{threadId}/messages/{messageId}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                req.Content = form;
                return req;
            }

            var response = await SendWithRetryAsync(MakeRequest(), MakeRequest);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                AppLogger.Warn($"Discord flowchart edit failed: {response.StatusCode} — {body}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Discord flowchart edit error: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> DeleteMessageAsync(
        string botToken, string threadId, string messageId)
    {
        try
        {
            HttpRequestMessage MakeRequest()
            {
                var req = new HttpRequestMessage(HttpMethod.Delete,
                    $"{BaseUrl}/channels/{threadId}/messages/{messageId}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                return req;
            }

            var response = await SendWithRetryAsync(MakeRequest(), MakeRequest);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                AppLogger.Warn($"Discord flowchart delete failed: {response.StatusCode} — {body}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Discord flowchart delete error: {ex.Message}");
            return false;
        }
    }
}
