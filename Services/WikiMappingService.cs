using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public class WikiMappingEntry
{
    /// <summary>All fields from the Lua mapping entry. Values: string, double, bool, or null (explicit nil).</summary>
    public Dictionary<string, object?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Convenience typed accessors for commonly used fields
    public string? ChainName => GetString("chainName");
    public string? Name => GetString("name");
    public int? Level => GetDouble("level") is double d ? (int)d : null;
    public double? SortingLevel => GetDouble("sortingLevel");
    public bool Fueled => GetBool("fueled");
    public bool IgnoreInTask => GetBool("ignoreInTask");
    public bool IsAlias => GetBool("isAlias");

    /// <summary>Whether a field was explicitly set to nil in the mapping (e.g., fuels = nil).</summary>
    public bool IsNil(string field) => Fields.TryGetValue(field, out var v) && v == null;

    /// <summary>Whether a field exists in the mapping (any value including nil).</summary>
    public bool Has(string field) => Fields.ContainsKey(field);

    private string? GetString(string field) => Fields.TryGetValue(field, out var v) && v is string s ? s : null;
    private double? GetDouble(string field) => Fields.TryGetValue(field, out var v) && v is double d ? d : null;
    private bool GetBool(string field) => Fields.TryGetValue(field, out var v) && v is true;
}

public class WikiMappingCache
{
    public DateTime FetchedAt { get; set; }
    public Dictionary<string, WikiMappingEntry> Mappings { get; set; } = new();
}

public static class WikiMappingService
{
    private static readonly string CachePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "wiki_mapping_cache.json");

    /// <summary>
    /// Throws a user-friendly exception for wiki API errors.
    /// Recognizes known server-side errors and provides actionable messages.
    /// </summary>
    internal static Exception WikiEditException(string info)
    {
        if (info.Contains("DBConnectionError", StringComparison.OrdinalIgnoreCase)
            || info.Contains("DBQueryError", StringComparison.OrdinalIgnoreCase))
            return new Exception("Fandom wiki server is temporarily unavailable (database error). Please try again in a few minutes.");

        if (info.Contains("ratelimited", StringComparison.OrdinalIgnoreCase))
            return new Exception("Wiki rate limit reached. Please wait a moment and try again.");

        if (info.Contains("readonly", StringComparison.OrdinalIgnoreCase))
            return new Exception("Fandom wiki is currently in read-only mode (maintenance). Please try again later.");

        if (info.Contains("badtoken", StringComparison.OrdinalIgnoreCase))
            return new Exception("Wiki session expired. Please restart the app and try again.");

        if (info.Contains("protectedpage", StringComparison.OrdinalIgnoreCase)
            || info.Contains("cascadeprotected", StringComparison.OrdinalIgnoreCase))
            return new Exception("This wiki page is protected and cannot be edited by the bot account.");

        return new Exception($"Wiki edit failed: {info}");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MergeMansionWikiTools/1.0");
        return client;
    }

    private const string ApiUrl =
        "https://merge-mansion.fandom.com/api.php?action=query" +
        "&titles=Module:Datatable/Items/Mapping" +
        "&prop=revisions&rvprop=content&rvslots=main&format=json";

    public static WikiMappingCache Load()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                return JsonSerializer.Deserialize<WikiMappingCache>(json, JsonOpts)
                       ?? new WikiMappingCache();
            }
        }
        catch (Exception ex) { AppLogger.Error("WikiMappingService.Load failed", ex); }

        return new WikiMappingCache();
    }

    public static void Save(WikiMappingCache cache)
    {
        try
        {
            var json = JsonSerializer.Serialize(cache, JsonOpts);
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex) { AppLogger.Error("WikiMappingService.Save failed", ex); }
    }

    public static bool NeedsRefresh(WikiMappingCache cache)
    {
        if (cache.Mappings.Count == 0) return true;
        return (DateTime.UtcNow - cache.FetchedAt).TotalHours >= 24;
    }

    /// <summary>
    /// Fetches the Lua module from Fandom MediaWiki API and parses it into a cache.
    /// </summary>
    public static async Task<WikiMappingCache> FetchFromWikiAsync()
    {
        using var _t = AppLogger.Timed("WikiMapping.FetchFromWikiAsync");
        var response = await Http.GetStringAsync(ApiUrl);
        var doc = JsonDocument.Parse(response);

        // Navigate: query → pages → {pageId} → revisions[0] → slots → main → *
        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        string luaContent = "";

        foreach (var page in pages.EnumerateObject())
        {
            var revisions = page.Value.GetProperty("revisions");
            var firstRev = revisions[0];
            luaContent = firstRev.GetProperty("slots")
                                 .GetProperty("main")
                                 .GetProperty("*")
                                 .GetString() ?? "";
            break;
        }

        var mappings = ParseLua(luaContent);

        var cache = new WikiMappingCache
        {
            FetchedAt = DateTime.UtcNow,
            Mappings = mappings
        };

        Save(cache);
        return cache;
    }

    /// <summary>
    /// Parses the Lua table entries into a dictionary.
    /// Format: ["ItemType"] = {chainName = "Name", level = 1, fueled = true, ...}
    /// </summary>
    private static Dictionary<string, WikiMappingEntry> ParseLua(string lua)
    {
        var result = new Dictionary<string, WikiMappingEntry>(StringComparer.OrdinalIgnoreCase);

        // Match top-level entry keys: ["key"] = { at line start (with optional indent)
        var keyRegex = new Regex(@"^\s*\[""([^""]+)""\]\s*=\s*\{", RegexOptions.Compiled | RegexOptions.Multiline);

        foreach (Match m in keyRegex.Matches(lua))
        {
            var key = m.Groups[1].Value;
            var bodyStart = m.Index + m.Length;
            var body = ExtractBalancedBraceContent(lua, bodyStart);
            if (body == null) continue;

            var entry = new WikiMappingEntry();
            ParseTopLevelFields(body, entry.Fields);

            result.TryAdd(key, entry);
        }

        return result;
    }

    /// <summary>Parses only top-level fields from a Lua table body, skipping nested table content.</summary>
    private static void ParseTopLevelFields(string body, Dictionary<string, object?> fields)
    {
        // Strip nested tables from body before parsing fields
        var flat = StripNestedTables(body);

        var fieldRegex = new Regex(
            @"(\w+)\s*=\s*(?:""([^""]*)""|(-?[\d.]+)|(\w+))",
            RegexOptions.Compiled);

        foreach (Match f in fieldRegex.Matches(flat))
        {
            var fieldName = f.Groups[1].Value;
            if (f.Groups[2].Success) // string value
                fields[fieldName] = f.Groups[2].Value;
            else if (f.Groups[3].Success) // number value
                fields[fieldName] = double.TryParse(f.Groups[3].Value,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
            else if (f.Groups[4].Success) // keyword: true, false, nil
            {
                var kw = f.Groups[4].Value;
                if (kw == "true") fields[fieldName] = true;
                else if (kw == "false") fields[fieldName] = false;
                else if (kw == "nil") fields[fieldName] = null;
            }
        }
    }

    /// <summary>Removes all nested {...} blocks from a Lua table body, preserving top-level fields.</summary>
    private static string StripNestedTables(string body)
    {
        var sb = new System.Text.StringBuilder(body.Length);
        int depth = 0;
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '{') { depth++; continue; }
            if (body[i] == '}') { depth--; continue; }
            if (depth == 0) sb.Append(body[i]);
        }
        return sb.ToString();
    }

    /// <summary>Extracts content from the position right after an opening '{' to its matching '}'.</summary>
    private static string? ExtractBalancedBraceContent(string text, int start)
    {
        int depth = 1;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') { depth--; if (depth == 0) return text[start..i]; }
        }
        return null;
    }

    // ── Wiki Edit API ────────────────────────────────────────────────

    private const string BaseApiUrl = "https://merge-mansion.fandom.com/api.php";

    /// <summary>
    /// Creates an authenticated HttpClient logged in with the given bot password credentials.
    /// All edits go through the user's own bot — permissions are enforced by Fandom, not the app.
    /// </summary>
    internal static async Task<HttpClient> CreateAuthenticatedClientAsync(string username, string password)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler);
        // Fandom/Cloudflare returns 403 "error code: 1010" (a non-JSON body that breaks
        // JsonDocument.Parse, e.g. on uploads) when the request has no/default User-Agent.
        // The shared GET client sets one; the authenticated client must too.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MergeMansionWikiTools/1.0");

        var tokenJson = await client.GetStringAsync(
            $"{BaseApiUrl}?action=query&meta=tokens&type=login&format=json");
        var loginToken = JsonDocument.Parse(tokenJson).RootElement
            .GetProperty("query").GetProperty("tokens")
            .GetProperty("logintoken").GetString()!;

        var loginContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "login",
            ["lgname"] = username,
            ["lgpassword"] = password,
            ["lgtoken"] = loginToken,
            ["format"] = "json",
        });
        var resp = await client.PostAsync(BaseApiUrl, loginContent);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var result = doc.RootElement.GetProperty("login").GetProperty("result").GetString();

        if (result != "Success")
        {
            client.Dispose();
            throw new Exception($"Wiki login failed: {result}");
        }

        return client;
    }

    /// <summary>
    /// Verifies a user's Fandom account via bot password (action=login).
    /// Returns the confirmed Fandom username on success, null on failure.
    /// </summary>
    public static async Task<string?> VerifyLoginAsync(string username, string password)
    {
        try
        {
            using var client = await CreateAuthenticatedClientAsync(username, password);

            // Query the logged-in user info to get the real username
            var infoJson = await client.GetStringAsync(
                $"{BaseApiUrl}?action=query&meta=userinfo&format=json");
            var infoDoc = JsonDocument.Parse(infoJson);
            var uiName = infoDoc.RootElement
                .GetProperty("query").GetProperty("userinfo")
                .GetProperty("name").GetString();

            return uiName ?? username;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Test edit: appends a timestamped comment to the given page using the user's own bot.
    /// </summary>
    public static async Task<string> TestEditPageAsync(
        string username, string password, string page)
    {
        using var client = await CreateAuthenticatedClientAsync(username, password);

        var csrfJson = await client.GetStringAsync(
            $"{BaseApiUrl}?action=query&meta=tokens&format=json");
        var csrfToken = JsonDocument.Parse(csrfJson).RootElement
            .GetProperty("query").GetProperty("tokens")
            .GetProperty("csrftoken").GetString()!;

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
        var editResp = await client.PostAsync(BaseApiUrl, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["action"] = "edit",
                ["title"] = page,
                ["appendtext"] = $"\n/* Test edit at {timestamp} via MergeMansionWikiTools */",
                ["token"] = csrfToken,
                ["summary"] = $"Test edit (via MergeMansionWikiTools)",
                ["bot"] = "1",
                ["format"] = "json",
            }));
        var editRaw = await editResp.Content.ReadAsStringAsync();
        var editDoc = JsonDocument.Parse(editRaw);

        if (editDoc.RootElement.TryGetProperty("error", out var error))
            return $"Edit failed: {error.GetProperty("info").GetString()}";

        if (editDoc.RootElement.TryGetProperty("edit", out var edit))
        {
            var editResult = edit.TryGetProperty("result", out var r) ? r.GetString() : "?";
            return $"Edit result: {editResult}";
        }

        return $"Unexpected response: {(editRaw.Length > 200 ? editRaw[..200] : editRaw)}";
    }

    /// <summary>
    /// Resolves the wiki filename for a chain by invoking Module:Utils FormatFileName.
    /// Returns e.g. "MagicFlower01.png" for chain "Magic Flower" at level 1.
    /// When suppressLevel is true, returns e.g. "MagicFlower.png" (no level suffix).
    /// </summary>
    public static async Task<string?> ResolveWikiFilenameAsync(string chainName, bool suppressLevel = false)
    {
        var suppressArg = suppressLevel ? "|true" : "";
        var text = Uri.EscapeDataString($"{{{{#invoke:Utils|FormatFileName|{chainName}|1{suppressArg}}}}}");
        var url = $"{BaseApiUrl}?action=expandtemplates&text={text}&prop=wikitext&format=json";
        var json = await Http.GetStringAsync(url);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("expandtemplates")
                              .GetProperty("wikitext").GetString();
    }

    /// <summary>
    /// Gets a CSRF token from the wiki for authenticated edits/uploads.
    /// </summary>
    internal static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var json = await client.GetStringAsync($"{BaseApiUrl}?action=query&meta=tokens&format=json");
        return JsonDocument.Parse(json).RootElement
            .GetProperty("query").GetProperty("tokens")
            .GetProperty("csrftoken").GetString()!;
    }

    /// <summary>
    /// Uploads a file to the wiki using an authenticated client and CSRF token.
    /// </summary>
    public static async Task<string> UploadFileAsync(
        HttpClient client, string csrfToken,
        string filename, byte[] fileData, string? description = null,
        bool ignoreWarnings = true)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("upload"), "action");
        content.Add(new StringContent(filename), "filename");
        content.Add(new StringContent(csrfToken), "token");
        content.Add(new StringContent("1"), "bot");
        if (ignoreWarnings)
            content.Add(new StringContent("1"), "ignorewarnings");
        content.Add(new StringContent("json"), "format");
        if (description != null)
            content.Add(new StringContent(description), "text");
        content.Add(new StringContent("Uploaded via MergeMansionWikiTools"), "comment");
        content.Add(new ByteArrayContent(fileData), "file", filename);

        var resp = await client.PostAsync(BaseApiUrl, content);
        var raw = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new Exception($"Upload failed: {error.GetProperty("info").GetString()}");

        if (doc.RootElement.TryGetProperty("upload", out var upload))
        {
            // With ignoreWarnings, API may still return warnings alongside a successful result
            if (!ignoreWarnings && upload.TryGetProperty("warnings", out var warnings))
                throw new WikiUploadWarningException(warnings.ToString());

            if (upload.TryGetProperty("result", out var resultProp))
                return resultProp.GetString() ?? "Unknown";
        }

        return "Unknown";
    }

    internal class WikiUploadWarningException : Exception
    {
        public WikiUploadWarningException(string warnings) : base(warnings) { }
    }

    /// <summary>
    /// Pushes chain name updates to the wiki module using the user's own bot credentials.
    /// Permissions are enforced by Fandom — the app does not restrict any actions.
    /// </summary>
    public static async Task PushChainNameToWikiAsync(
        string username, string password,
        IReadOnlyList<ParsedItem> items, string chainName)
    {
        using var client = await CreateAuthenticatedClientAsync(username, password);

        // 1. Fetch current Lua content
        var luaJson = await client.GetStringAsync(ApiUrl);
        var luaDoc = JsonDocument.Parse(luaJson);
        var pages = luaDoc.RootElement.GetProperty("query").GetProperty("pages");
        string currentLua = "";
        foreach (var page in pages.EnumerateObject())
        {
            currentLua = page.Value.GetProperty("revisions")[0]
                .GetProperty("slots").GetProperty("main")
                .GetProperty("*").GetString() ?? "";
            break;
        }

        // 2. Version check + modify Lua
        ThrowIfNewerVersion(currentLua);
        var updatedLua = EnsureMmwtVersionHeader(UpdateLuaEntry(currentLua, items, chainName));

        // 3. Get CSRF token
        var csrfJson = await client.GetStringAsync(
            $"{BaseApiUrl}?action=query&meta=tokens&format=json");
        var csrfToken = JsonDocument.Parse(csrfJson).RootElement
            .GetProperty("query").GetProperty("tokens")
            .GetProperty("csrftoken").GetString()!;

        // 4. Edit page
        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = "Module:Datatable/Items/Mapping",
            ["text"] = updatedLua,
            ["token"] = csrfToken,
            ["summary"] = $"Update chainName → \"{chainName}\" (via MergeMansionWikiTools)",
            ["bot"] = "1",
            ["format"] = "json",
        });
        var editResp = await client.PostAsync(BaseApiUrl, editContent);
        var editDoc = JsonDocument.Parse(await editResp.Content.ReadAsStringAsync());

        if (editDoc.RootElement.TryGetProperty("error", out var error))
            throw WikiEditException(error.GetProperty("info").GetString());
    }

    /// <summary>
    /// Updates or adds chainName (and optionally level) entries in the Lua module source.
    /// </summary>
    private static string UpdateLuaEntry(
        string lua, IReadOnlyList<ParsedItem> items, string chainName, int? level = null,
        bool? isAlias = null)
    {
        var escapedName = chainName.Replace("\"", "\\\"");

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.ItemType)) continue;

            var escapedType = Regex.Escape(item.ItemType);
            var entryRegex = new Regex(
                @"(\[""" + escapedType + @"""\]\s*=\s*\{)([^}]*)(})",
                RegexOptions.None);

            if (entryRegex.IsMatch(lua))
            {
                // Entry exists — update or add chainName field
                lua = entryRegex.Replace(lua, m =>
                {
                    var prefix = m.Groups[1].Value;
                    var body = m.Groups[2].Value;
                    var suffix = m.Groups[3].Value;

                    // Update chainName
                    var chainNameRegex = new Regex(@"chainName\s*=\s*""[^""]*""");
                    if (chainNameRegex.IsMatch(body))
                        body = chainNameRegex.Replace(body, $"chainName = \"{escapedName}\"");
                    else
                    {
                        body = body.TrimStart();
                        body = $"chainName = \"{escapedName}\", " + body;
                    }

                    // Update level if specified
                    if (level.HasValue)
                    {
                        var levelRegex = new Regex(@"level\s*=\s*\d+");
                        if (levelRegex.IsMatch(body))
                            body = levelRegex.Replace(body, $"level = {level.Value}");
                        else
                        {
                            // Add level after chainName
                            var insertIdx = body.IndexOf("chainName");
                            if (insertIdx >= 0)
                            {
                                // Find end of chainName value
                                var afterChain = body.IndexOf('"', body.IndexOf('"', insertIdx + 12) + 1);
                                if (afterChain >= 0)
                                    body = body.Insert(afterChain + 1, $", level = {level.Value}");
                            }
                        }
                    }

                    // Handle isAlias (only when explicitly set)
                    if (isAlias.HasValue)
                    {
                        var isAliasRegex = new Regex(@",?\s*isAlias\s*=\s*(true|false)");
                        if (isAlias.Value)
                        {
                            if (isAliasRegex.IsMatch(body))
                                body = isAliasRegex.Replace(body, ", isAlias = true");
                            else
                                body += ", isAlias = true";
                        }
                        else
                        {
                            // Remove isAlias field if present
                            body = isAliasRegex.Replace(body, "");
                        }
                    }

                    return prefix + body + suffix;
                });
            }
            else
            {
                // Entry doesn't exist — add before closing "}"
                var fields = $"chainName = \"{escapedName}\"";
                if (level.HasValue)
                    fields += $", level = {level.Value}";
                if (isAlias == true)
                    fields += ", isAlias = true";
                var newEntry = $"\t[\"{item.ItemType}\"] = {{{fields}}},\n";
                var lastBrace = lua.LastIndexOf('}');
                if (lastBrace >= 0)
                    lua = lua.Insert(lastBrace, newEntry);
            }
        }

        return lua;
    }

    // ── Merge Alias Operations ─────────────────────────────────────────

    /// <summary>
    /// Updates Lua entries for a merge-alias operation:
    /// - Primary chain items: sets chainName, removes isAlias if present
    /// - Alias chain items: sets chainName + isAlias = true
    /// </summary>
    private static string UpdateLuaEntryAlias(
        string lua, ParsedChain primaryChain, IReadOnlyList<ParsedChain> aliasChains, string mergedName)
    {
        var escapedName = mergedName.Replace("\"", "\\\"");

        // Helper: update or add entry for a single item
        string ProcessItem(string currentLua, ParsedItem item, bool isAlias)
        {
            if (string.IsNullOrEmpty(item.ItemType)) return currentLua;

            var escapedType = Regex.Escape(item.ItemType);
            var entryRegex = new Regex(
                @"(\[""" + escapedType + @"""\]\s*=\s*\{)([^}]*)(})",
                RegexOptions.None);

            if (entryRegex.IsMatch(currentLua))
            {
                currentLua = entryRegex.Replace(currentLua, m =>
                {
                    var prefix = m.Groups[1].Value;
                    var body = m.Groups[2].Value;
                    var suffix = m.Groups[3].Value;

                    // Update/add chainName
                    var chainNameRegex = new Regex(@"chainName\s*=\s*""[^""]*""");
                    if (chainNameRegex.IsMatch(body))
                        body = chainNameRegex.Replace(body, $"chainName = \"{escapedName}\"");
                    else
                    {
                        body = body.TrimStart();
                        body = $"chainName = \"{escapedName}\", " + body;
                    }

                    // Handle isAlias
                    var isAliasRegex = new Regex(@",?\s*isAlias\s*=\s*(true|false)");
                    if (isAlias)
                    {
                        if (isAliasRegex.IsMatch(body))
                            body = isAliasRegex.Replace(body, ", isAlias = true");
                        else
                            body += ", isAlias = true";
                    }
                    else
                    {
                        // Remove isAlias field if present
                        body = isAliasRegex.Replace(body, "");
                    }

                    return prefix + body + suffix;
                });
            }
            else
            {
                // Entry doesn't exist — add before closing "}"
                var fields = $"chainName = \"{escapedName}\"";
                if (isAlias)
                    fields += ", isAlias = true";
                var newEntry = $"\t[\"{item.ItemType}\"] = {{{fields}}},\n";
                var lastBrace = currentLua.LastIndexOf('}');
                if (lastBrace >= 0)
                    currentLua = currentLua.Insert(lastBrace, newEntry);
            }

            return currentLua;
        }

        // Primary chain items: chainName only, no isAlias
        foreach (var item in primaryChain.Items)
            lua = ProcessItem(lua, item, isAlias: false);

        // Alias chain items: chainName + isAlias = true
        foreach (var chain in aliasChains)
            foreach (var item in chain.Items)
                lua = ProcessItem(lua, item, isAlias: true);

        return lua;
    }

    /// <summary>
    /// Pushes a merge-alias operation to the wiki: sets chainName on all items,
    /// marks alias chain items with isAlias = true.
    /// </summary>
    public static async Task PushMergeAliasAsync(
        string username, string password,
        ParsedChain primaryChain,
        IReadOnlyList<ParsedChain> aliasChains,
        string mergedName)
    {
        using var client = await CreateAuthenticatedClientAsync(username, password);

        // 1. Fetch current Lua
        var luaJson = await client.GetStringAsync(ApiUrl);
        var luaDoc = JsonDocument.Parse(luaJson);
        var pages = luaDoc.RootElement.GetProperty("query").GetProperty("pages");
        string currentLua = "";
        foreach (var page in pages.EnumerateObject())
        {
            currentLua = page.Value.GetProperty("revisions")[0]
                .GetProperty("slots").GetProperty("main")
                .GetProperty("*").GetString() ?? "";
            break;
        }

        // 2. Version check + modify Lua
        ThrowIfNewerVersion(currentLua);
        var updatedLua = EnsureMmwtVersionHeader(UpdateLuaEntryAlias(currentLua, primaryChain, aliasChains, mergedName));

        // 3. Get CSRF token + edit
        var csrfToken = await GetCsrfTokenAsync(client);

        var allKeys = new List<string> { primaryChain.ConfigKey };
        allKeys.AddRange(aliasChains.Select(c => c.ConfigKey));
        var summary = $"Merge chains: {string.Join(" + ", allKeys)} → \"{mergedName}\" (via MergeMansionWikiTools)";

        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = "Module:Datatable/Items/Mapping",
            ["text"] = updatedLua,
            ["token"] = csrfToken,
            ["summary"] = summary,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var editResp = await client.PostAsync(BaseApiUrl, editContent);
        var editDoc = JsonDocument.Parse(await editResp.Content.ReadAsStringAsync());

        if (editDoc.RootElement.TryGetProperty("error", out var error))
            throw WikiEditException(error.GetProperty("info").GetString());
    }

    /// <summary>
    /// Adds alias chains to an existing chain name without modifying existing entries.
    /// Only writes chainName + isAlias = true for items in the alias chains.
    /// </summary>
    public static async Task PushAddAliasesAsync(
        string username, string password,
        IReadOnlyList<ParsedChain> aliasChains,
        string existingChainName)
    {
        using var client = await CreateAuthenticatedClientAsync(username, password);

        // 1. Fetch current Lua
        var luaJson = await client.GetStringAsync(ApiUrl);
        var luaDoc = JsonDocument.Parse(luaJson);
        var pages = luaDoc.RootElement.GetProperty("query").GetProperty("pages");
        string currentLua = "";
        foreach (var page in pages.EnumerateObject())
        {
            currentLua = page.Value.GetProperty("revisions")[0]
                .GetProperty("slots").GetProperty("main")
                .GetProperty("*").GetString() ?? "";
            break;
        }

        // 2. Version check + add alias entries
        ThrowIfNewerVersion(currentLua);
        var escapedName = existingChainName.Replace("\"", "\\\"");
        var lua = currentLua;

        foreach (var chain in aliasChains)
        {
            foreach (var item in chain.Items)
            {
                if (string.IsNullOrEmpty(item.ItemType)) continue;

                var escapedType = Regex.Escape(item.ItemType);
                var entryRegex = new Regex(
                    @"(\[""" + escapedType + @"""\]\s*=\s*\{)([^}]*)(})",
                    RegexOptions.None);

                if (entryRegex.IsMatch(lua))
                {
                    lua = entryRegex.Replace(lua, m =>
                    {
                        var prefix = m.Groups[1].Value;
                        var body = m.Groups[2].Value;
                        var suffix = m.Groups[3].Value;

                        // Update/add chainName
                        var chainNameRegex = new Regex(@"chainName\s*=\s*""[^""]*""");
                        if (chainNameRegex.IsMatch(body))
                            body = chainNameRegex.Replace(body, $"chainName = \"{escapedName}\"");
                        else
                        {
                            body = body.TrimStart();
                            body = $"chainName = \"{escapedName}\", " + body;
                        }

                        // Ensure isAlias = true
                        var isAliasRegex = new Regex(@",?\s*isAlias\s*=\s*(true|false)");
                        if (isAliasRegex.IsMatch(body))
                            body = isAliasRegex.Replace(body, ", isAlias = true");
                        else
                            body += ", isAlias = true";

                        return prefix + body + suffix;
                    });
                }
                else
                {
                    var newEntry = $"\t[\"{item.ItemType}\"] = {{chainName = \"{escapedName}\", isAlias = true}},\n";
                    var lastBrace = lua.LastIndexOf('}');
                    if (lastBrace >= 0)
                        lua = lua.Insert(lastBrace, newEntry);
                }
            }
        }

        // 3. Get CSRF token + edit
        lua = EnsureMmwtVersionHeader(lua);
        var csrfToken = await GetCsrfTokenAsync(client);

        var chainKeys = aliasChains.Select(c => c.ConfigKey);
        var summary = $"Add aliases: {string.Join(" + ", chainKeys)} → \"{existingChainName}\" (via MergeMansionWikiTools)";

        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = "Module:Datatable/Items/Mapping",
            ["text"] = lua,
            ["token"] = csrfToken,
            ["summary"] = summary,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var editResp = await client.PostAsync(BaseApiUrl, editContent);
        var editDoc = JsonDocument.Parse(await editResp.Content.ReadAsStringAsync());

        if (editDoc.RootElement.TryGetProperty("error", out var err))
            throw WikiEditException(err.GetProperty("info").GetString());
    }

    // ── Item Move & Level Operations ──────────────────────────────────

    /// <summary>
    /// Pushes item mapping changes (chainName + optional level) to the wiki Lua module.
    /// </summary>
    public static async Task PushItemMappingsAsync(
        string username, string password,
        IReadOnlyList<ParsedItem> items, string chainName, int? level = null,
        bool? isAlias = null)
    {
        using var client = await CreateAuthenticatedClientAsync(username, password);

        // 1. Fetch current Lua content
        var luaJson = await client.GetStringAsync(ApiUrl);
        var luaDoc = JsonDocument.Parse(luaJson);
        var pages = luaDoc.RootElement.GetProperty("query").GetProperty("pages");
        string currentLua = "";
        foreach (var page in pages.EnumerateObject())
        {
            currentLua = page.Value.GetProperty("revisions")[0]
                .GetProperty("slots").GetProperty("main")
                .GetProperty("*").GetString() ?? "";
            break;
        }

        // 2. Version check + modify Lua
        ThrowIfNewerVersion(currentLua);
        var updatedLua = EnsureMmwtVersionHeader(UpdateLuaEntry(currentLua, items, chainName, level, isAlias));

        // 3. Get CSRF token
        var csrfToken = await GetCsrfTokenAsync(client);

        // 4. Build edit summary
        string summary;
        if (level.HasValue && items.Count == 1)
            summary = $"Set level {level.Value} for {items[0].ItemType} → \"{chainName}\" (via MergeMansionWikiTools)";
        else
            summary = $"Move items → \"{chainName}\" (via MergeMansionWikiTools)";
        if (isAlias.HasValue)
            summary += isAlias.Value ? " [alias]" : " [alias removed]";

        // 5. Edit page
        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = "Module:Datatable/Items/Mapping",
            ["text"] = updatedLua,
            ["token"] = csrfToken,
            ["summary"] = summary,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var editResp = await client.PostAsync(BaseApiUrl, editContent);
        var editDoc = JsonDocument.Parse(await editResp.Content.ReadAsStringAsync());

        if (editDoc.RootElement.TryGetProperty("error", out var error))
            throw WikiEditException(error.GetProperty("info").GetString());
    }

    /// <summary>
    /// Pushes an item name change to the wiki Lua module.
    /// Updates the "name" field in Module:Datatable/Items/Mapping.
    /// </summary>
    public static async Task PushItemNameAsync(
        string username, string password, string itemType, string newName)
    {
        using var client = await CreateAuthenticatedClientAsync(username, password);

        // 1. Fetch current Lua content
        var luaJson = await client.GetStringAsync(ApiUrl);
        var luaDoc = JsonDocument.Parse(luaJson);
        var pages = luaDoc.RootElement.GetProperty("query").GetProperty("pages");
        string currentLua = "";
        foreach (var page in pages.EnumerateObject())
        {
            currentLua = page.Value.GetProperty("revisions")[0]
                .GetProperty("slots").GetProperty("main")
                .GetProperty("*").GetString() ?? "";
            break;
        }

        // 2. Version check + modify Lua
        ThrowIfNewerVersion(currentLua);
        var updatedLua = EnsureMmwtVersionHeader(UpdateLuaItemName(currentLua, itemType, newName));

        // 3. Get CSRF token
        var csrfToken = await GetCsrfTokenAsync(client);

        // 4. Edit page
        var summary = $"Rename {itemType} → \"{newName}\" (via MergeMansionWikiTools)";
        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = "Module:Datatable/Items/Mapping",
            ["text"] = updatedLua,
            ["token"] = csrfToken,
            ["summary"] = summary,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var editResp = await client.PostAsync(BaseApiUrl, editContent);
        var editDoc = JsonDocument.Parse(await editResp.Content.ReadAsStringAsync());

        if (editDoc.RootElement.TryGetProperty("error", out var error))
            throw WikiEditException(error.GetProperty("info").GetString());
    }

    /// <summary>
    /// Updates or adds the "name" field for a single item in the Lua module source.
    /// </summary>
    private static string UpdateLuaItemName(string lua, string itemType, string newName)
    {
        var escapedType = Regex.Escape(itemType);
        var escapedName = newName.Replace("\"", "\\\"");
        var entryRegex = new Regex(
            @"(\[""" + escapedType + @"""\]\s*=\s*\{)([^}]*)(})",
            RegexOptions.None);

        if (entryRegex.IsMatch(lua))
        {
            lua = entryRegex.Replace(lua, m =>
            {
                var prefix = m.Groups[1].Value;
                var body = m.Groups[2].Value;
                var suffix = m.Groups[3].Value;

                // Update or add name field
                var nameRegex = new Regex(@"name\s*=\s*""[^""]*""");
                if (nameRegex.IsMatch(body))
                    body = nameRegex.Replace(body, $"name = \"{escapedName}\"");
                else
                {
                    // Insert after chainName if it exists, otherwise at start
                    var chainNameMatch = Regex.Match(body, @"chainName\s*=\s*""[^""]*""");
                    if (chainNameMatch.Success)
                    {
                        var insertAt = chainNameMatch.Index + chainNameMatch.Length;
                        body = body.Insert(insertAt, $", name = \"{escapedName}\"");
                    }
                    else
                        body = $"name = \"{escapedName}\", " + body.TrimStart();
                }

                return prefix + body + suffix;
            });
        }
        else
        {
            // Entry doesn't exist — add with just name field
            var newEntry = $"\t[\"{itemType}\"] = {{name = \"{escapedName}\"}},\n";
            var lastBrace = lua.LastIndexOf('}');
            if (lastBrace >= 0)
                lua = lua.Insert(lastBrace, newEntry);
        }

        return lua;
    }

    /// <summary>
    /// Pushes multiple item name changes to the wiki in a single edit.
    /// </summary>
    public static async Task PushItemNamesBatchAsync(
        string username, string password, IReadOnlyList<(string ItemType, string NewName)> renames)
    {
        if (renames.Count == 0) return;

        using var client = await CreateAuthenticatedClientAsync(username, password);

        // 1. Fetch current Lua content
        var luaJson = await client.GetStringAsync(ApiUrl);
        var luaDoc = JsonDocument.Parse(luaJson);
        var pages = luaDoc.RootElement.GetProperty("query").GetProperty("pages");
        string currentLua = "";
        foreach (var page in pages.EnumerateObject())
        {
            currentLua = page.Value.GetProperty("revisions")[0]
                .GetProperty("slots").GetProperty("main")
                .GetProperty("*").GetString() ?? "";
            break;
        }

        // 2. Version check + apply all renames
        ThrowIfNewerVersion(currentLua);
        var updatedLua = currentLua;
        foreach (var (itemType, newName) in renames)
            updatedLua = UpdateLuaItemName(updatedLua, itemType, newName);
        updatedLua = EnsureMmwtVersionHeader(updatedLua);

        // 3. Get CSRF token
        var csrfToken = await GetCsrfTokenAsync(client);

        // 4. Edit page
        var names = string.Join(", ", renames.Select(r => r.ItemType));
        var summary = $"Batch rename {renames.Count} items: {names} (via MergeMansionWikiTools)";
        if (summary.Length > 500) summary = $"Batch rename {renames.Count} items (via MergeMansionWikiTools)";
        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = "Module:Datatable/Items/Mapping",
            ["text"] = updatedLua,
            ["token"] = csrfToken,
            ["summary"] = summary,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var editResp = await client.PostAsync(BaseApiUrl, editContent);
        var editDoc = JsonDocument.Parse(await editResp.Content.ReadAsStringAsync());

        if (editDoc.RootElement.TryGetProperty("error", out var error))
            throw WikiEditException(error.GetProperty("info").GetString());
    }

    /// <summary>
    /// Checks which chain images exist on the wiki for given levels.
    /// Returns level → image URL (null if image doesn't exist).
    /// Uses the original PNG URL (not thumburl) to preserve transparency.
    /// </summary>
    public static async Task<Dictionary<int, string?>> CheckChainImagesAsync(string chainName, int[] levels)
    {
        var fullName = await ResolveWikiFilenameAsync(chainName);
        if (string.IsNullOrEmpty(fullName))
            return levels.ToDictionary(l => l, _ => (string?)null);

        // Strip trailing "01.png" or "01" to get base name
        string baseName;
        if (fullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && fullName.Length > 6)
            baseName = fullName[..^6];
        else if (fullName.Length > 2)
            baseName = fullName[..^2];
        else
            baseName = fullName;

        // Build file titles
        var titles = levels.Select(l => $"File:{baseName}{l:D2}.png");
        var titlesStr = string.Join("|", titles);

        // Use iiprop=url without iiurlwidth — returns the original PNG (preserves transparency)
        var url = $"{BaseApiUrl}?action=query&titles={Uri.EscapeDataString(titlesStr)}" +
                  "&prop=imageinfo&iiprop=url&format=json";
        var json = await Http.GetStringAsync(url);
        var doc = JsonDocument.Parse(json);

        var result = new Dictionary<int, string?>();
        var pagesEl = doc.RootElement.GetProperty("query").GetProperty("pages");

        foreach (var level in levels)
        {
            var fileName = $"{baseName}{level:D2}.png";
            string? imageUrl = null;
            foreach (var page in pagesEl.EnumerateObject())
            {
                var pageTitle = page.Value.GetProperty("title").GetString() ?? "";
                if (pageTitle.Equals($"File:{fileName}", StringComparison.OrdinalIgnoreCase)
                    && page.Value.TryGetProperty("imageinfo", out var ii))
                {
                    imageUrl = ii[0].GetProperty("url").GetString();
                    break;
                }
            }
            result[level] = imageUrl;
        }
        return result;
    }

    /// <summary>
    /// Checks whether a single wiki page exists (unauthenticated read-only API).
    /// </summary>
    public static async Task<bool> CheckPageExistsAsync(string pageTitle)
    {
        var url = $"{BaseApiUrl}?action=query" +
                  $"&titles={Uri.EscapeDataString(pageTitle)}" +
                  "&format=json";
        var json = await Http.GetStringAsync(url);
        var doc = JsonDocument.Parse(json);

        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("missing", out _))
                return false;
        }
        return true;
    }

    public enum WikiSectionFetchStatus { Ok, PageMissing, SectionMissing, NetworkError }
    public record WikiSectionFetchResult(WikiSectionFetchStatus Status, string? Content);

    /// <summary>
    /// Fetches a single top-level section (== Heading ==) from a wiki page by title.
    /// Status indicates outcome — content is non-null only when Status == Ok.
    /// </summary>
    public static async Task<WikiSectionFetchResult> FetchPageSectionAsync(string pageTitle, string sectionTitle)
    {
        try
        {
            var pageExists = await CheckPageExistsAsync(pageTitle);
            if (!pageExists) return new WikiSectionFetchResult(WikiSectionFetchStatus.PageMissing, null);

            var wikitext = await FetchPageWikitextAsync(Http, pageTitle);
            if (string.IsNullOrEmpty(wikitext))
                return new WikiSectionFetchResult(WikiSectionFetchStatus.PageMissing, null);

            var section = ExtractTopLevelSection(wikitext, sectionTitle);
            return section != null
                ? new WikiSectionFetchResult(WikiSectionFetchStatus.Ok, section)
                : new WikiSectionFetchResult(WikiSectionFetchStatus.SectionMissing, null);
        }
        catch
        {
            return new WikiSectionFetchResult(WikiSectionFetchStatus.NetworkError, null);
        }
    }

    /// <summary>
    /// Extracts content of a top-level section (== Heading ==) from wikitext. Stops at the next
    /// top-level (level-2) heading. Subsections (===, ====) stay inside. Returns null if not found.
    /// </summary>
    private static string? ExtractTopLevelSection(string wikitext, string sectionTitle)
    {
        var lines = wikitext.Replace("\r\n", "\n").Split('\n');
        bool inSection = false;
        var content = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            int leading = 0;
            while (leading < trimmed.Length && trimmed[leading] == '=') leading++;
            int trailing = 0;
            while (trailing < trimmed.Length && trimmed[trimmed.Length - 1 - trailing] == '=') trailing++;
            bool isTopLevelHeading = leading == 2 && trailing == 2 && trimmed.Length > 4;

            if (isTopLevelHeading)
            {
                var title = trimmed.Substring(2, trimmed.Length - 4).Trim();
                if (inSection) break;  // hit next top-level section
                if (string.Equals(title, sectionTitle, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    continue;
                }
            }
            if (inSection) content.AppendLine(lines[i]);
        }
        return inSection ? content.ToString().TrimEnd() : null;
    }

    /// <summary>
    /// Moves a wiki page from one title to another.
    /// On cantmove-noredirect, retries without noredirect.
    /// On articleexists, throws WikiArticleExistsException.
    /// </summary>
    public static async Task<string> MoveWikiPageAsync(
        HttpClient client, string csrfToken,
        string from, string to, string reason)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "move",
            ["from"] = from,
            ["to"] = to,
            ["reason"] = reason,
            ["noredirect"] = "1",
            ["movetalk"] = "1",
            ["token"] = csrfToken,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var resp = await client.PostAsync(BaseApiUrl, content);
        var raw = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var code = error.GetProperty("code").GetString() ?? "";
            if (code == "cantmove-noredirect")
            {
                // Retry without noredirect
                var retryContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["action"] = "move",
                    ["from"] = from,
                    ["to"] = to,
                    ["reason"] = reason,
                    ["movetalk"] = "1",
                    ["token"] = csrfToken,
                    ["bot"] = "1",
                    ["format"] = "json",
                });
                var retryResp = await client.PostAsync(BaseApiUrl, retryContent);
                var retryRaw = await retryResp.Content.ReadAsStringAsync();
                var retryDoc = JsonDocument.Parse(retryRaw);

                if (retryDoc.RootElement.TryGetProperty("error", out var retryError))
                    throw new Exception($"Move failed: {retryError.GetProperty("info").GetString()}");
                return "Success (with redirect)";
            }
            if (code == "articleexists")
                throw new WikiArticleExistsException($"Target page \"{to}\" already exists");
            throw new Exception($"Move failed: {error.GetProperty("info").GetString()}");
        }
        return "Success";
    }

    internal class WikiArticleExistsException : Exception
    {
        public WikiArticleExistsException(string message) : base(message) { }
    }

    /// <summary>
    /// Updates the content of a moved page: adds/updates DISPLAYTITLE, Infobox title1, Item/Group displayName.
    /// Handles {{#var:DisplayTitle}} pattern and existing DISPLAYTITLE.
    /// Only applies when the new name has a parenthetical suffix.
    /// Returns true if the page was edited.
    /// </summary>
    public static async Task<bool> UpdateMovedPageContentAsync(
        HttpClient client, string csrfToken,
        string pageTitle, string displayName)
    {
        var wikitext = await FetchPageWikitextAsync(client, pageTitle);
        if (string.IsNullOrEmpty(wikitext)) return false;

        var original = wikitext;

        // 1. DISPLAYTITLE + optional #vardefine:DisplayTitle
        bool usesDisplayTitleVar = wikitext.Contains("{{#var:DisplayTitle}}", StringComparison.OrdinalIgnoreCase);
        var displayTitleValue = usesDisplayTitleVar ? "{{#var:DisplayTitle}}" : displayName;
        var varDefinePart = usesDisplayTitleVar
            ? $"{{{{#vardefine:DisplayTitle|{displayName}}}}}" : null;

        if (Regex.IsMatch(wikitext, @"\{\{DISPLAYTITLE:", RegexOptions.IgnoreCase))
        {
            // Update existing DISPLAYTITLE value
            wikitext = Regex.Replace(wikitext,
                @"\{\{DISPLAYTITLE:[^}]*\}\}",
                $"{{{{DISPLAYTITLE:{displayTitleValue}}}}}",
                RegexOptions.IgnoreCase);

            // If we need vardefine and it doesn't exist yet, insert before DISPLAYTITLE
            if (varDefinePart != null &&
                !Regex.IsMatch(wikitext, @"\{\{#vardefine:DisplayTitle\|", RegexOptions.IgnoreCase))
            {
                wikitext = Regex.Replace(wikitext,
                    @"(\{\{DISPLAYTITLE:)",
                    varDefinePart + "$1",
                    RegexOptions.IgnoreCase);
            }
        }
        else
        {
            // Insert new DISPLAYTITLE
            var displayTitleTag = $"{{{{DISPLAYTITLE:{displayTitleValue}}}}}";
            var toInsert = varDefinePart != null
                ? varDefinePart + displayTitleTag
                : displayTitleTag;

            // Try to place on first line if it contains {{#vardefine
            var firstLineEnd = wikitext.IndexOf('\n');
            if (firstLineEnd > 0)
            {
                var firstLine = wikitext[..firstLineEnd];
                if (firstLine.Contains("{{#vardefine", StringComparison.OrdinalIgnoreCase))
                {
                    // Insert before <!-- comment if present, otherwise at end of line
                    var commentIdx = firstLine.IndexOf("<!--");
                    if (commentIdx >= 0)
                        wikitext = firstLine[..commentIdx] + toInsert + firstLine[commentIdx..] + wikitext[firstLineEnd..];
                    else
                        wikitext = firstLine + toInsert + wikitext[firstLineEnd..];
                }
                else
                {
                    wikitext = toInsert + "\n" + wikitext;
                }
            }
            else
            {
                wikitext = toInsert + "\n" + wikitext;
            }
        }

        // 2. After {{Infobox Items line → insert | title1 = displayName
        var infoboxRegex = new Regex(@"(\{\{Infobox Items[^\n]*\n)", RegexOptions.IgnoreCase);
        if (infoboxRegex.IsMatch(wikitext) &&
            !Regex.IsMatch(wikitext, @"\|\s*title1\s*=", RegexOptions.IgnoreCase))
        {
            wikitext = infoboxRegex.Replace(wikitext,
                $"$1| title1 = {displayName}\n", 1);
        }

        // 3. {{Item/Group|{{PAGENAME}}|N}} → {{Item/Group|{{PAGENAME}}|N|displayName=displayName}}
        wikitext = Regex.Replace(wikitext,
            @"\{\{Item/Group\|\{\{PAGENAME\}\}\|(\d+)\}\}",
            $"{{{{Item/Group|{{{{PAGENAME}}}}|$1|displayName={displayName}}}}}");

        if (wikitext == original) return false;

        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = pageTitle,
            ["text"] = wikitext,
            ["token"] = csrfToken,
            ["summary"] = "Update page after move: add DISPLAYTITLE + title1 (via MergeMansionWikiTools)",
            ["bot"] = "1",
            ["format"] = "json",
        });
        var resp = await client.PostAsync(BaseApiUrl, editContent);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new Exception($"Edit failed on \"{pageTitle}\": {error.GetProperty("info").GetString()}");

        return true;
    }

    /// <summary>
    /// Moves a wiki file (image) from one title to another.
    /// </summary>
    public static async Task<string> MoveFileAsync(
        HttpClient client, string csrfToken,
        string from, string to, string reason)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "move",
            ["from"] = from,
            ["to"] = to,
            ["reason"] = reason,
            ["noredirect"] = "1",
            ["movetalk"] = "1",
            ["token"] = csrfToken,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var resp = await client.PostAsync(BaseApiUrl, content);
        var raw = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var code = error.GetProperty("code").GetString() ?? "";
            // If noredirect not allowed, retry without it
            if (code == "cantmove-noredirect")
                return await MoveFileWithRedirectAsync(client, csrfToken, from, to, reason);
            throw new Exception($"Move failed: {error.GetProperty("info").GetString()}");
        }
        return "Success";
    }

    private static async Task<string> MoveFileWithRedirectAsync(
        HttpClient client, string csrfToken,
        string from, string to, string reason)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "move",
            ["from"] = from,
            ["to"] = to,
            ["reason"] = reason,
            ["movetalk"] = "1",
            ["token"] = csrfToken,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var resp = await client.PostAsync(BaseApiUrl, content);
        var raw = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new Exception($"Move failed: {error.GetProperty("info").GetString()}");
        return "Success (with redirect)";
    }

    /// <summary>
    /// Moves all chain images from old name to new name on the wiki.
    /// On articleexists, overwrites by downloading source and re-uploading to target.
    /// Returns (moved, overwritten, skipped).
    /// </summary>
    public static async Task<(int moved, int overwritten, int skipped)> MoveChainImagesAsync(
        HttpClient client, string csrfToken,
        string oldChainName, string newChainName, int[] levels,
        Dictionary<int, string?>? sourceImageUrls = null,
        Action<int, int>? onProgress = null)
    {
        var oldFull = await ResolveWikiFilenameAsync(oldChainName);
        var newFull = await ResolveWikiFilenameAsync(newChainName);

        if (string.IsNullOrEmpty(oldFull) || string.IsNullOrEmpty(newFull))
            return (0, 0, levels.Length);

        // Get base names
        string GetBase(string f) => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && f.Length > 6
            ? f[..^6] : (f.Length > 2 ? f[..^2] : f);
        var oldBase = GetBase(oldFull);
        var newBase = GetBase(newFull);

        // Check which old images exist (use provided URLs if available)
        var existingImages = sourceImageUrls ?? await CheckChainImagesAsync(oldChainName, levels);

        int moved = 0, overwritten = 0, skipped = 0;
        int total = levels.Length;

        foreach (var level in levels)
        {
            if (existingImages.TryGetValue(level, out var imageUrl) && imageUrl != null)
            {
                var from = $"File:{oldBase}{level:D2}.png";
                var to = $"File:{newBase}{level:D2}.png";
                var reason = $"Rename chain images: \"{oldChainName}\" → \"{newChainName}\" (via MergeMansionWikiTools)";

                try
                {
                    await MoveFileAsync(client, csrfToken, from, to, reason);
                    moved++;
                }
                catch (Exception ex)
                {
                    // On articleexists: download source image → upload to target (overwrite)
                    if (ex.Message.Contains("already exists") || ex.Message.Contains("articleexists"))
                    {
                        try
                        {
                            var bytes = await Http.GetByteArrayAsync(imageUrl);
                            var targetFilename = $"{newBase}{level:D2}.png";
                            await UploadFileAsync(client, csrfToken, targetFilename, bytes,
                                ignoreWarnings: true);
                            overwritten++;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
            else
            {
                skipped++;
            }
            onProgress?.Invoke(moved + overwritten + skipped, total);
        }

        return (moved, overwritten, skipped);
    }

    // ── Backlinks & Reference Updates ─────────────────────────────────

    /// <summary>
    /// Fetches all pages that link to the given page title (main namespace only).
    /// Handles continuation for >500 results.
    /// </summary>
    public static async Task<List<string>> GetBacklinksAsync(string pageTitle, CancellationToken ct = default)
    {
        var results = new List<string>();
        string? blcontinue = null;

        do
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{BaseApiUrl}?action=query&list=backlinks" +
                      $"&bltitle={Uri.EscapeDataString(pageTitle)}" +
                      "&bllimit=500&blnamespace=0&format=json";
            if (blcontinue != null)
                url += $"&blcontinue={Uri.EscapeDataString(blcontinue)}";

            var json = await Http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("query", out var query)
                && query.TryGetProperty("backlinks", out var bl))
            {
                foreach (var item in bl.EnumerateArray())
                {
                    var title = item.GetProperty("title").GetString();
                    if (title != null)
                        results.Add(title);
                }
            }

            blcontinue = null;
            if (doc.RootElement.TryGetProperty("continue", out var cont)
                && cont.TryGetProperty("blcontinue", out var blc))
            {
                blcontinue = blc.GetString();
            }
        } while (blcontinue != null);

        return results;
    }

    /// <summary>
    /// Fetches the raw wikitext content of a page.
    /// </summary>
    public static async Task<string> FetchPageWikitextAsync(HttpClient client, string pageTitle)
    {
        var url = $"{BaseApiUrl}?action=query" +
                  $"&titles={Uri.EscapeDataString(pageTitle)}" +
                  "&prop=revisions&rvprop=content&rvslots=main&format=json";
        var json = await client.GetStringAsync(url);
        var doc = JsonDocument.Parse(json);

        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("revisions", out var revs) && revs.GetArrayLength() > 0)
            {
                return revs[0].GetProperty("slots").GetProperty("main")
                    .GetProperty("*").GetString() ?? "";
            }
        }
        return "";
    }

    /// <summary>
    /// Updates all references from oldName to newName on a wiki page.
    /// Returns true if the page was actually edited (wikitext changed).
    /// </summary>
    public static async Task<bool> UpdatePageReferencesAsync(
        HttpClient client, string csrfToken,
        string pageTitle, string oldName, string newName)
    {
        var wikitext = await FetchPageWikitextAsync(client, pageTitle);
        if (string.IsNullOrEmpty(wikitext)) return false;

        var updated = ReplaceChainReferences(wikitext, oldName, newName);
        if (updated == wikitext) return false;

        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = pageTitle,
            ["text"] = updated,
            ["token"] = csrfToken,
            ["summary"] = $"Update references: \"{oldName}\" → \"{newName}\" (via MergeMansionWikiTools)",
            ["bot"] = "1",
            ["format"] = "json",
        });
        var resp = await client.PostAsync(BaseApiUrl, editContent);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new Exception($"Edit failed on \"{pageTitle}\": {error.GetProperty("info").GetString()}");

        return true;
    }

    /// <summary>
    /// Replaces all wiki references from oldName to newName in wikitext.
    /// Handles: [[wikilinks]], {{Item|}}, {{Item/nolevel|}}, {{Item/Group|}} templates.
    /// </summary>
    internal static string ReplaceChainReferences(string wikitext, string oldName, string newName)
    {
        var escapedOld = Regex.Escape(oldName);
        bool newHasSuffix = newName.LastIndexOf('(') > 0;
        var displayName = newHasSuffix ? StripParenthetical(newName) : newName;

        // (a) [[Old Name]] → [[New Name]] or [[New Name|Display]] if suffix
        if (newHasSuffix)
        {
            wikitext = Regex.Replace(wikitext,
                @"\[\[" + escapedOld + @"\]\]",
                $"[[{newName}|{displayName}]]");
        }
        else
        {
            wikitext = Regex.Replace(wikitext,
                @"\[\[" + escapedOld + @"\]\]",
                $"[[{newName}]]");
        }

        // (b) [[Old Name|existing text]] → [[New Name|existing text]]
        wikitext = Regex.Replace(wikitext,
            @"\[\[" + escapedOld + @"\|([^\]]*)\]\]",
            $"[[{newName}|$1]]");

        // (c) {{Item|Old Name| → {{Item|New Name|
        wikitext = Regex.Replace(wikitext,
            @"\{\{Item\|" + escapedOld + @"\|",
            $"{{{{Item|{newName}|");

        // (d) {{Item/nolevel|Old Name}} or {{Item/nolevel|Old Name|...}}
        wikitext = Regex.Replace(wikitext,
            @"\{\{Item/nolevel\|" + escapedOld + @"([|}])",
            $"{{{{Item/nolevel|{newName}$1");

        // (e) {{Item/Group|Old Name| → {{Item/Group|New Name|
        wikitext = Regex.Replace(wikitext,
            @"\{\{Item/Group\|" + escapedOld + @"\|",
            $"{{{{Item/Group|{newName}|");

        // (f) {{#vardefine:...|Old Name}} → {{#vardefine:...|New Name}}
        wikitext = Regex.Replace(wikitext,
            @"(\{\{#vardefine:[^|]*\|)" + escapedOld + @"(\}\})",
            $"$1{newName}$2");

        // (g) When suffix + page uses DropDisplayName:
        //     - Update existing {{#vardefine:DropDisplayName|...}} → displayName
        //     - Or create {{#vardefine:DropDisplayName|displayName}} if page uses {{#var:DropDisplayName}}
        if (newHasSuffix)
        {
            bool hasVarDefine = Regex.IsMatch(wikitext,
                @"\{\{#vardefine:DropDisplayName\|", RegexOptions.IgnoreCase);
            bool hasVarUsage = wikitext.Contains("{{#var:DropDisplayName}}",
                StringComparison.OrdinalIgnoreCase);

            if (hasVarDefine)
            {
                // Update existing vardefine value
                wikitext = Regex.Replace(wikitext,
                    @"(\{\{#vardefine:DropDisplayName\|)[^}]*(\}\})",
                    $"$1{displayName}$2",
                    RegexOptions.IgnoreCase);
            }
            else if (hasVarUsage)
            {
                // Page uses {{#var:DropDisplayName}} but has no vardefine — create one
                // Insert after {{#vardefine:DropName|...}} if present, otherwise prepend
                var dropNameDefine = Regex.Match(wikitext,
                    @"\{\{#vardefine:DropName\|[^}]*\}\}", RegexOptions.IgnoreCase);
                var newDefine = $"{{{{#vardefine:DropDisplayName|{displayName}}}}}";
                if (dropNameDefine.Success)
                {
                    var insertAt = dropNameDefine.Index + dropNameDefine.Length;
                    wikitext = wikitext.Insert(insertAt, newDefine);
                }
                else
                {
                    wikitext = newDefine + wikitext;
                }
            }
        }

        return wikitext;
    }

    /// <summary>
    /// Counts how many references to oldName exist in the wikitext.
    /// Uses the same patterns as ReplaceChainReferences.
    /// </summary>
    internal static int CountChainReferences(string wikitext, string oldName)
    {
        var escaped = Regex.Escape(oldName);
        int count = 0;
        count += Regex.Matches(wikitext, @"\[\[" + escaped + @"(\]\]|\|)").Count;     // [[Name]] or [[Name|...]]
        count += Regex.Matches(wikitext, @"\{\{Item\|" + escaped + @"\|").Count;       // {{Item|Name|
        count += Regex.Matches(wikitext, @"\{\{Item/nolevel\|" + escaped + @"[|}]").Count; // {{Item/nolevel|Name
        count += Regex.Matches(wikitext, @"\{\{Item/Group\|" + escaped + @"\|").Count; // {{Item/Group|Name|
        count += Regex.Matches(wikitext, @"\{\{#vardefine:[^|]*\|" + escaped + @"\}\}").Count; // {{#vardefine:...|Name}}
        return count;
    }

    private static string StripParenthetical(string name)
    {
        var idx = name.LastIndexOf('(');
        if (idx > 0)
            return name[..idx].TrimEnd();
        return name;
    }

    // ── Module Date Checking ─────────────────────────────────────────

    /// <summary>
    /// Reads the first line of a wiki module looking for "-- createdAt: ..." comment.
    /// Returns the date string, or null if the module doesn't exist or has no createdAt.
    /// </summary>
    public static async Task<string?> FetchModuleCreatedAtAsync(string moduleTitle)
    {
        try
        {
            var url = $"{BaseApiUrl}?action=query" +
                      $"&titles={Uri.EscapeDataString(moduleTitle)}" +
                      "&prop=revisions&rvprop=content&rvslots=main&format=json";
            var json = await Http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);

            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            foreach (var page in pages.EnumerateObject())
            {
                if (page.Value.TryGetProperty("missing", out _)) return null;
                if (page.Value.TryGetProperty("revisions", out var revs) && revs.GetArrayLength() > 0)
                {
                    var content = revs[0].GetProperty("slots").GetProperty("main")
                        .GetProperty("*").GetString() ?? "";
                    // Parse first line: "-- createdAt: 2025-05-01T12:00:00Z"
                    var firstLine = content.Split('\n', 2)[0];
                    if (firstLine.StartsWith("-- createdAt: "))
                        return firstLine["-- createdAt: ".Length..].Trim();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Fetches the full content of a wiki module (unauthenticated read).
    /// Returns null if the module doesn't exist.
    /// </summary>
    public static async Task<string?> FetchModuleContentAsync(string moduleTitle)
    {
        try
        {
            var url = $"{BaseApiUrl}?action=query" +
                      $"&titles={Uri.EscapeDataString(moduleTitle)}" +
                      "&prop=revisions&rvprop=content&rvslots=main&format=json";
            var json = await Http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);

            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            foreach (var page in pages.EnumerateObject())
            {
                if (page.Value.TryGetProperty("missing", out _)) return null;
                if (page.Value.TryGetProperty("revisions", out var revs) && revs.GetArrayLength() > 0)
                {
                    return revs[0].GetProperty("slots").GetProperty("main")
                        .GetProperty("*").GetString();
                }
            }
        }
        catch (Exception ex) { AppLogger.Error($"FetchModuleContentAsync({moduleTitle}) failed", ex); }
        return null;
    }

    /// <summary>
    /// Extracts the createdAt date from a module content's first line comment.
    /// </summary>
    public static string? ExtractCreatedAtFromContent(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var firstLine = content.Split('\n', 2)[0];
        if (firstLine.StartsWith("-- createdAt: "))
            return firstLine["-- createdAt: ".Length..].Trim();
        return null;
    }

    /// <summary>
    /// Extracts the mmwtVersion from a module content's header comments.
    /// </summary>
    public static string? ExtractMmwtVersionFromContent(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("-- mmwtVersion: "))
                return trimmed["-- mmwtVersion: ".Length..].Trim();
            if (!trimmed.StartsWith("--"))
                break; // only check header comments
        }
        return null;
    }

    /// <summary>
    /// Compares two version strings (e.g. "v0.15.0", "v0.16.0").
    /// Returns negative if v1 &lt; v2, 0 if equal, positive if v1 &gt; v2.
    /// </summary>
    public static int CompareVersions(string v1, string v2)
    {
        var a = Version.Parse(v1.TrimStart('v'));
        var b = Version.Parse(v2.TrimStart('v'));
        return a.CompareTo(b);
    }

    /// <summary>
    /// Throws if the wiki module was uploaded by a newer MMWT version.
    /// No-op if the wiki has no version tag.
    /// </summary>
    private static void ThrowIfNewerVersion(string lua)
    {
        var wikiVersion = ExtractMmwtVersionFromContent(lua);
        if (wikiVersion == null) return;

        try
        {
            if (CompareVersions(AppVersion.Version, wikiVersion) < 0)
                throw new InvalidOperationException(
                    $"Wiki module was updated by a newer MMWT version ({wikiVersion}). Update your app ({AppVersion.Version}) first.");
        }
        catch (FormatException) { } // malformed version → allow
    }

    /// <summary>
    /// Ensures the Lua content has a current mmwtVersion header comment.
    /// Adds or updates the "-- mmwtVersion:" line.
    /// </summary>
    private static string EnsureMmwtVersionHeader(string lua)
    {
        var versionTag = $"-- mmwtVersion: {AppVersion.Version}";
        var existing = ExtractMmwtVersionFromContent(lua);
        if (existing != null)
        {
            // Replace existing version line
            var regex = new Regex(@"-- mmwtVersion: .*");
            return regex.Replace(lua, versionTag, 1);
        }

        // Add as first line (or after createdAt if present)
        return versionTag + "\n" + lua;
    }

    /// <summary>
    /// Extracts top-level Lua table keys from a specific p.XXX = {...} section.
    /// Pattern matches: \n\t["key"] entries (one-tab indentation = top-level).
    /// If tableName is provided, only keys from that section are extracted.
    /// </summary>
    public static HashSet<string> ExtractLuaTableKeys(string content, string? tableName = null)
    {
        var keys = new HashSet<string>();
        var section = GetTableSection(content, tableName);
        if (section == null) return keys;

        foreach (Match m in Regex.Matches(section, @"\n\t\[""([^""]+)""\]"))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    /// <summary>
    /// Extracts top-level Lua table entries as key → full entry line (single-line entries).
    /// Used for detecting modified values, not just added/removed keys.
    /// </summary>
    public static Dictionary<string, string> ExtractLuaTableEntries(string content, string tableName)
    {
        var entries = new Dictionary<string, string>();
        var section = GetTableSection(content, tableName);
        if (section == null) return entries;

        foreach (Match m in Regex.Matches(section, @"\n\t\[""([^""]+)""\] = (\{[^\n]+\})"))
            entries.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return entries;
    }

    /// <summary>
    /// Extracts top-level Lua area entries as key → full multi-line block text.
    /// Area entries span multiple lines with nested braces (tasks, requirements, etc.).
    /// Uses brace-depth tracking to capture complete blocks.
    /// </summary>
    public static Dictionary<string, string> ExtractLuaAreaEntries(string content, string tableName = "areas")
    {
        var entries = new Dictionary<string, string>();
        var section = GetTableSection(content, tableName);
        if (section == null) return entries;

        // Find each top-level entry: \n\t["key"] = {
        var entryPattern = new Regex(@"\n\t\[""([^""]+)""\]\s*=\s*\{");
        var matches = entryPattern.Matches(section);

        for (int m = 0; m < matches.Count; m++)
        {
            var key = matches[m].Groups[1].Value;
            // Start after the opening brace of the entry
            var braceStart = matches[m].Index + matches[m].Length - 1; // points to '{'
            var blockStart = braceStart;

            // Track brace depth (starting at 1 for the opening brace we found)
            int depth = 1;
            bool inString = false;
            int i = braceStart + 1;

            while (i < section.Length && depth > 0)
            {
                char c = section[i];
                if (c == '"' && (i == 0 || section[i - 1] != '\\'))
                    inString = !inString;
                if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                i++;
            }

            if (depth == 0)
            {
                // Capture the full block including outer braces
                var block = section[blockStart..i];
                entries.TryAdd(key, block);
            }
        }

        return entries;
    }

    private static string? GetTableSection(string content, string? tableName)
    {
        if (tableName == null) return content;

        var marker = "p." + tableName + " = {";
        var startIdx = content.IndexOf(marker);
        if (startIdx < 0) return null;
        startIdx += marker.Length;

        var endIdx = content.IndexOf("\n}", startIdx);
        if (endIdx < 0) endIdx = content.Length;
        return content[startIdx..endIdx];
    }

    // ── Area Module Publishing ────────────────────────────────────────

    /// <summary>
    /// Queries which Module:Datatable/Areas/N subpages exist on the wiki.
    /// Returns a sorted list of integer indices (e.g. [1, 2, 3]).
    /// </summary>
    public static async Task<List<int>> QueryExistingAreaModulesAsync()
    {
        var results = new List<int>();
        string? apcontinue = null;

        do
        {
            var url = $"{BaseApiUrl}?action=query&list=allpages" +
                      "&apprefix=Datatable/Areas/&apnamespace=828&aplimit=100&format=json";
            if (apcontinue != null)
                url += $"&apcontinue={Uri.EscapeDataString(apcontinue)}";

            var json = await Http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("query", out var query)
                && query.TryGetProperty("allpages", out var pages))
            {
                foreach (var page in pages.EnumerateArray())
                {
                    var title = page.GetProperty("title").GetString() ?? "";
                    // title = "Module:Datatable/Areas/1" etc.
                    var lastSlash = title.LastIndexOf('/');
                    if (lastSlash >= 0 && int.TryParse(title[(lastSlash + 1)..], out var idx))
                        results.Add(idx);
                }
            }

            apcontinue = null;
            if (doc.RootElement.TryGetProperty("continue", out var cont)
                && cont.TryGetProperty("apcontinue", out var apc))
            {
                apcontinue = apc.GetString();
            }
        } while (apcontinue != null);

        results.Sort();
        return results;
    }

    /// <summary>
    /// Edits (creates or overwrites) a Lua module page on the wiki.
    /// Returns the edit result string ("Success" / "nochange").
    /// </summary>
    public static async Task<string> EditModuleAsync(
        HttpClient client, string csrfToken,
        string title, string content, string summary)
    {
        AppLogger.Info($"EditModule: {title}");
        using var _t = AppLogger.Timed($"EditModuleAsync({title})");
        var editContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = title,
            ["text"] = content,
            ["token"] = csrfToken,
            ["summary"] = summary,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var resp = await client.PostAsync(BaseApiUrl, editContent);
        var raw = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new Exception($"Edit failed on \"{title}\": {error.GetProperty("info").GetString()}");

        if (doc.RootElement.TryGetProperty("edit", out var edit)
            && edit.TryGetProperty("result", out var result))
            return result.GetString() ?? "Unknown";

        return "Unknown";
    }

    /// <summary>
    /// Generates the arbiter Lua module that requires and combines area data chunks.
    /// </summary>
    public static string GenerateAreasArbiterLua(int chunkCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("local p = {}");

        for (int i = 1; i <= chunkCount; i++)
            sb.AppendLine($"local areas_{i} = require('Module:Datatable/Areas/{i}')");

        sb.AppendLine();
        sb.AppendLine("p.areas = {");
        for (int i = 1; i <= chunkCount; i++)
        {
            var comma = i < chunkCount ? "," : "";
            sb.AppendLine($"\tareas{i} = areas_{i}.areas{comma}");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("return p");

        return sb.ToString();
    }

    /// <summary>
    /// Fetches ordering indices from Module:Datatable/Areas/Mapping.
    /// Returns a dictionary of area name → orderingIndex.
    /// </summary>
    public static async Task<Dictionary<string, double>> FetchAreaOrderingAsync()
    {
        var result = new Dictionary<string, double>();
        try
        {
            var content = await FetchModuleContentAsync("Module:Datatable/Areas/Mapping");
            if (content == null) return result;

            // Match only NON-COMMENTED rows (line-anchored regex, must start with optional whitespace then `[`).
            // Commented rows like `--["Name"] = {orderingIndex = N}` would be matched without the `^[ \t]*\[`
            // anchor (a previous bug); they are now filtered out so existing in-prep markers don't poison the dict.
            foreach (Match m in Regex.Matches(content,
                @"^[ \t]*\[""([^""]+)""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*([0-9.]+)",
                RegexOptions.Multiline))
            {
                if (double.TryParse(m.Groups[2].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var idx))
                    result.TryAdd(m.Groups[1].Value, idx);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("FetchAreaOrderingAsync failed", ex);
        }
        return result;
    }

    /// <summary>
    /// Updates the wiki "Modules" page to include/update Areas submodule links.
    /// Preserves existing non-numeric submodule lines and adds numeric chunk links with area ranges.
    /// </summary>
    public static async Task UpdateAreasModulesPageAsync(
        HttpClient client, string csrfToken, int chunkCount, List<(string Label, string Lua)> chunks)
    {
        // Fetch current wikitext of the "Modules" page
        var url = $"{BaseApiUrl}?action=query" +
                  $"&titles={Uri.EscapeDataString("Modules")}" +
                  "&prop=revisions&rvprop=content&rvslots=main&format=json";
        var json = await Http.GetStringAsync(url);
        var doc = JsonDocument.Parse(json);

        string? wikitext = null;
        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("missing", out _)) return;
            if (page.Value.TryGetProperty("revisions", out var revs) && revs.GetArrayLength() > 0)
            {
                wikitext = revs[0].GetProperty("slots").GetProperty("main")
                    .GetProperty("*").GetString();
            }
        }

        if (string.IsNullOrEmpty(wikitext)) return;

        // Find the Areas section
        var lines = wikitext.Split('\n').ToList();
        var areasLineIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('*') &&
                (trimmed.Contains("[[Module:Datatable/Areas|") ||
                 trimmed.Contains("[[Module:Datatable/Areas]]")))
            {
                areasLineIdx = i;
                break;
            }
        }

        if (areasLineIdx < 0) return; // Areas section not found

        // Collect existing sub-entries under the Areas line (** lines)
        var subStart = areasLineIdx + 1;
        var subEnd = subStart;
        while (subEnd < lines.Count && lines[subEnd].TrimStart().StartsWith("**"))
            subEnd++;

        // Preserve non-numeric submodule lines
        var preserved = new List<string>();
        for (int i = subStart; i < subEnd; i++)
        {
            if (!Regex.IsMatch(lines[i], @"\[\[Module:Datatable/Areas/\d+\|"))
                preserved.Add(lines[i]);
        }

        // Build new submodule lines: preserved + numeric chunks with area ranges
        var newSubLines = new List<string>();
        newSubLines.AddRange(preserved);
        for (int i = 1; i <= chunkCount; i++)
        {
            var line = $"** '''[[Module:Datatable/Areas/{i}|Datatable/Areas/{i}]]'''";
            if (i <= chunks.Count && !string.IsNullOrEmpty(chunks[i - 1].Label))
                line += $" — Areas {chunks[i - 1].Label}";
            newSubLines.Add(line);
        }

        // Replace the old sub-entries
        lines.RemoveRange(subStart, subEnd - subStart);
        lines.InsertRange(subStart, newSubLines);

        var newWikitext = string.Join('\n', lines);
        if (newWikitext == wikitext) return; // no changes

        await EditModuleAsync(client, csrfToken, "Modules", newWikitext,
            $"Update Areas submodule links ({chunkCount} chunks) (via MergeMansionWikiTools)");
    }

    // ── Item Module Publishing ────────────────────────────────────────

    /// <summary>
    /// Queries which Module:Datatable/Items/N subpages exist on the wiki.
    /// Returns a sorted list of integer indices (e.g. [1, 2]).
    /// Filters out non-numeric suffixes (e.g. "Mapping").
    /// </summary>
    public static async Task<List<int>> QueryExistingItemModulesAsync()
    {
        var results = new List<int>();
        string? apcontinue = null;

        do
        {
            var url = $"{BaseApiUrl}?action=query&list=allpages" +
                      "&apprefix=Datatable/Items/&apnamespace=828&aplimit=100&format=json";
            if (apcontinue != null)
                url += $"&apcontinue={Uri.EscapeDataString(apcontinue)}";

            var json = await Http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("query", out var query)
                && query.TryGetProperty("allpages", out var pages))
            {
                foreach (var page in pages.EnumerateArray())
                {
                    var title = page.GetProperty("title").GetString() ?? "";
                    // title = "Module:Datatable/Items/1" etc.
                    var lastSlash = title.LastIndexOf('/');
                    if (lastSlash >= 0 && int.TryParse(title[(lastSlash + 1)..], out var idx))
                        results.Add(idx);
                }
            }

            apcontinue = null;
            if (doc.RootElement.TryGetProperty("continue", out var cont)
                && cont.TryGetProperty("apcontinue", out var apc))
            {
                apcontinue = apc.GetString();
            }
        } while (apcontinue != null);

        results.Sort();
        return results;
    }

    /// <summary>
    /// Generates the arbiter Lua module for Items that requires and flat-merges item data chunks
    /// and includes p.chainNames directly.
    /// Unlike Areas (which uses nested tables), Items uses a FLAT merge because Module:Items
    /// iterates with "for id, item in pairs(itemsData.items)".
    /// </summary>
    public static string GenerateItemsArbiterLua(
        int chunkCount, string chainNamesBlock,
        string? archivedFlagsBlock = null, string? createdAt = null)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(createdAt))
            sb.AppendLine($"-- createdAt: {createdAt}");
        sb.AppendLine("local str = require('Module:Strings')");
        sb.AppendLine("local p = {}");

        for (int i = 1; i <= chunkCount; i++)
            sb.AppendLine($"local items_{i} = require('Module:Datatable/Items/{i}')");

        sb.AppendLine();
        sb.AppendLine("p.items = items_1.items");
        for (int i = 2; i <= chunkCount; i++)
            sb.AppendLine($"for k, v in pairs(items_{i}.items) do p.items[k] = v end");

        sb.AppendLine();
        sb.AppendLine(chainNamesBlock);

        // Optional p.archived flat marker map — emitted only when archive workflow ran for this update.
        // When omitted (e.g. legacy callers), older Module:Items code falls back gracefully on `nil`.
        if (!string.IsNullOrEmpty(archivedFlagsBlock))
        {
            sb.AppendLine();
            sb.AppendLine(archivedFlagsBlock);
        }

        sb.AppendLine();
        sb.AppendLine("return p");

        return sb.ToString();
    }

    /// <summary>
    /// Updates the wiki "Modules" page to include/update Items submodule links.
    /// Preserves existing non-numeric submodules (e.g. Mapping) and adds numeric chunk links.
    /// </summary>
    public static async Task UpdateModulesPageAsync(
        HttpClient client, string csrfToken, int chunkCount, int firstEventChunkIndex = 0)
    {
        // Fetch current wikitext of the "Modules" page
        var content = await FetchModuleContentAsync("Modules");
        if (content == null) return; // page doesn't exist, skip

        // Note: FetchModuleContentAsync uses ns=Module by default via titles=,
        // but "Modules" is in main namespace — need direct fetch
        var url = $"{BaseApiUrl}?action=query" +
                  $"&titles={Uri.EscapeDataString("Modules")}" +
                  "&prop=revisions&rvprop=content&rvslots=main&format=json";
        var json = await Http.GetStringAsync(url);
        var doc = JsonDocument.Parse(json);

        string? wikitext = null;
        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("missing", out _)) return;
            if (page.Value.TryGetProperty("revisions", out var revs) && revs.GetArrayLength() > 0)
            {
                wikitext = revs[0].GetProperty("slots").GetProperty("main")
                    .GetProperty("*").GetString();
            }
        }

        if (string.IsNullOrEmpty(wikitext)) return;

        // Find the Items section and update submodule lines
        var lines = wikitext.Split('\n').ToList();
        var itemsLineIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('*') &&
                (trimmed.Contains("[[Module:Datatable/Items|") ||
                 trimmed.Contains("[[Module:Datatable/Items]]")))
            {
                itemsLineIdx = i;
                break;
            }
        }

        if (itemsLineIdx < 0) return; // Items section not found

        // Collect existing sub-entries under the Items line (** lines)
        var subStart = itemsLineIdx + 1;
        var subEnd = subStart;
        while (subEnd < lines.Count && lines[subEnd].TrimStart().StartsWith("**"))
            subEnd++;

        // Preserve non-numeric submodule lines (e.g. Mapping, Archive). Numeric chunks are
        // regenerated below — non-numeric ones are kept verbatim with whatever description the
        // wiki maintainer wrote.
        var preserved = new List<string>();
        var hasArchiveLink = false;
        for (int i = subStart; i < subEnd; i++)
        {
            if (Regex.IsMatch(lines[i], @"\[\[Module:Datatable/Items/\d+\|")) continue;
            preserved.Add(lines[i]);
            if (lines[i].Contains("[[Module:Datatable/Items/Archive", StringComparison.Ordinal))
                hasArchiveLink = true;
        }

        // Ensure the Archive submodule link is present. Inserted once on first run; subsequent runs
        // detect the existing link and skip (idempotent).
        if (!hasArchiveLink)
        {
            preserved.Add("** '''[[Module:Datatable/Items/Archive|Datatable/Items/Archive]]''' — Last-known data of items removed from current dump + #missing# chain shadows (lazy-loaded fallback)");
            AppLogger.Debug("[UpdateModulesPage] Added missing Datatable/Items/Archive link to preserved list");
        }

        // Build new submodule lines: preserved + numeric chunks (with main/event annotations)
        var newSubLines = new List<string>();
        newSubLines.AddRange(preserved);
        for (int i = 1; i <= chunkCount; i++)
        {
            var line = $"** '''[[Module:Datatable/Items/{i}|Datatable/Items/{i}]]'''";
            if (firstEventChunkIndex > 0)
            {
                if (i == 1)
                    line += " — Main items";
                else if (i == firstEventChunkIndex)
                    line += " — Event items onwards";
            }
            newSubLines.Add(line);
        }

        // Replace the old sub-entries
        lines.RemoveRange(subStart, subEnd - subStart);
        lines.InsertRange(subStart, newSubLines);

        var newWikitext = string.Join('\n', lines);
        if (newWikitext == wikitext) return; // no changes

        await EditModuleAsync(client, csrfToken, "Modules", newWikitext,
            $"Update Items submodule links ({chunkCount} chunks) (via MergeMansionWikiTools)");
    }
}
