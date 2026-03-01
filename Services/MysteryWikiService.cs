using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Wiki API operations for Mystery Pass features:
/// page existence checks, template fetching/comparison, markup generation, publishing.
/// </summary>
public static class MysteryWikiService
{
    private const string BaseApiUrl = "https://merge-mansion.fandom.com/api.php";
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MergeMansionWikiTools/1.0");
        return client;
    }

    private static readonly string StatusCachePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "mystery_wiki_status_cache.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── Status cache persistence ──────────────────────────────────

    public static MysteryWikiStatusCache LoadStatusCache()
    {
        try
        {
            if (File.Exists(StatusCachePath))
            {
                var json = File.ReadAllText(StatusCachePath);
                return JsonSerializer.Deserialize<MysteryWikiStatusCache>(json, JsonOpts)
                       ?? new MysteryWikiStatusCache();
            }
        }
        catch { /* Return empty */ }
        return new MysteryWikiStatusCache();
    }

    public static void SaveStatusCache(MysteryWikiStatusCache cache)
    {
        try
        {
            var json = JsonSerializer.Serialize(cache, JsonOpts);
            File.WriteAllText(StatusCachePath, json);
        }
        catch { /* Silently fail */ }
    }

    /// <summary>
    /// Applies cached true values to mysteries. Returns false/null entries untouched
    /// so they get re-checked. Only confirmed-true statuses are trusted from cache.
    /// </summary>
    private static void ApplyCache(IReadOnlyList<MysteryEvent> mysteries, MysteryWikiStatusCache cache)
    {
        foreach (var m in mysteries)
        {
            if (!cache.Entries.TryGetValue(m.ProgressionEventId, out var cached)) continue;

            if (cached.EventPageExists)
            {
                m.WikiStatus.EventPageExists = true;
                m.WikiStatus.SuggestedPageTitle = cached.SuggestedPageTitle;
            }
            if (cached.EventPageContentMatches)
                m.WikiStatus.EventPageContentMatches = true;
            if (cached.EventItemPageExists)
                m.WikiStatus.EventItemPageExists = true;
            if (cached.EventItemPageContentMatches)
                m.WikiStatus.EventItemPageContentMatches = true;
            if (cached.RewardTemplateMatches)
            {
                m.WikiStatus.RewardTemplateMatches = true;
                m.WikiStatus.MatchingVariant = cached.MatchingVariant;
                if (cached.RewardContentMatches)
                    m.WikiStatus.RewardContentMatches = true;
            }
        }
    }

    /// <summary>
    /// Updates cache with current mystery statuses. Only stores confirmed-true values.
    /// </summary>
    private static void UpdateCache(IReadOnlyList<MysteryEvent> mysteries, MysteryWikiStatusCache cache)
    {
        foreach (var m in mysteries)
        {
            var entry = new CachedMysteryStatus();
            bool hasData = false;

            if (m.WikiStatus.EventPageExists == true)
            {
                entry.EventPageExists = true;
                entry.SuggestedPageTitle = m.WikiStatus.SuggestedPageTitle;
                hasData = true;
            }
            if (m.WikiStatus.EventPageContentMatches == true)
            {
                entry.EventPageContentMatches = true;
                hasData = true;
            }
            if (m.WikiStatus.EventItemPageExists == true)
            {
                entry.EventItemPageExists = true;
                hasData = true;
            }
            if (m.WikiStatus.EventItemPageContentMatches == true)
            {
                entry.EventItemPageContentMatches = true;
                hasData = true;
            }
            if (m.WikiStatus.RewardTemplateMatches == true)
            {
                entry.RewardTemplateMatches = true;
                entry.MatchingVariant = m.WikiStatus.MatchingVariant;
                if (m.WikiStatus.RewardContentMatches == true)
                    entry.RewardContentMatches = true;
                hasData = true;
            }

            if (hasData)
                cache.Entries[m.ProgressionEventId] = entry;
        }
    }

    // ── Page existence checks ─────────────────────────────────────

    /// <summary>
    /// Batch checks whether wiki pages exist. Returns title → exists map.
    /// Uses unauthenticated read-only API (max 50 titles per request).
    /// </summary>
    public static async Task<Dictionary<string, bool>> CheckPagesExistAsync(IEnumerable<string> titles)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var titleList = titles.ToList();

        // Process in batches of 50 (MediaWiki API limit)
        for (int i = 0; i < titleList.Count; i += 50)
        {
            var batch = titleList.Skip(i).Take(50);
            var joined = string.Join("|", batch);
            var url = $"{BaseApiUrl}?action=query&titles={Uri.EscapeDataString(joined)}&format=json";
            AppLogger.Info($"CheckPagesExist batch: {url}");

            var json = await Http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");

            foreach (var page in pages.EnumerateObject())
            {
                var title = page.Value.GetProperty("title").GetString() ?? "";
                bool missing = page.Value.TryGetProperty("missing", out _);
                result[title] = !missing;
            }
        }

        return result;
    }

    /// <summary>
    /// Checks wiki status for a single mystery event: event page, item page, reward template.
    /// </summary>
    public static async Task<WikiPageStatus> CheckMysteryStatusAsync(
        MysteryEvent mystery, DataService? ds)
    {
        var status = new WikiPageStatus();
        var pageName = mystery.Name;
        status.SuggestedPageTitle = pageName;

        // Check for name collisions with chain/item names
        if (ds != null)
        {
            bool collision = ds.ChainNames.Values.Any(n =>
                string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
            if (!collision)
                collision = ds.ItemNames.Values.Any(n =>
                    string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));

            if (collision && mystery.StartDate.HasValue)
                status.SuggestedPageTitle = $"{pageName} (Mystery {mystery.StartDate.Value.Year})";
        }

        var titlesToCheck = new List<string> { status.SuggestedPageTitle };
        if (!string.IsNullOrEmpty(mystery.EventItemName))
            titlesToCheck.Add(mystery.EventItemName);

        var existMap = await CheckPagesExistAsync(titlesToCheck);

        status.EventPageExists = existMap.GetValueOrDefault(status.SuggestedPageTitle, false);
        if (!string.IsNullOrEmpty(mystery.EventItemName))
            status.EventItemPageExists = existMap.GetValueOrDefault(mystery.EventItemName, false);

        return status;
    }

    // ── Fetch existing reward templates ───────────────────────────

    /// <summary>
    /// Lists all Template:Mystery_Pass/Rewards* pages and fetches their content.
    /// Returns variant title → wikitext content.
    /// </summary>
    public static async Task<Dictionary<string, string>> FetchRewardTemplatesAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // List all pages with prefix Mystery_Pass/Rewards in Template namespace (ns=10)
        var listUrl = $"{BaseApiUrl}?action=query&list=allpages" +
                      "&apprefix=Mystery_Pass/Rewards&apnamespace=10&aplimit=100&format=json";

        var listJson = await Http.GetStringAsync(listUrl);
        var listDoc = JsonDocument.Parse(listJson);

        var allPages = listDoc.RootElement
            .GetProperty("query")
            .GetProperty("allpages");

        var templateTitles = new List<string>();
        foreach (var p in allPages.EnumerateArray())
        {
            var title = p.GetProperty("title").GetString();
            if (!string.IsNullOrEmpty(title))
                templateTitles.Add(title);
        }

        // Fetch content for each template (batch of 50)
        for (int i = 0; i < templateTitles.Count; i += 50)
        {
            var batch = templateTitles.Skip(i).Take(50);
            var joined = string.Join("|", batch);
            var url = $"{BaseApiUrl}?action=query&titles={Uri.EscapeDataString(joined)}" +
                      "&prop=revisions&rvprop=content&rvslots=main&format=json";

            var json = await Http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");

            foreach (var page in pages.EnumerateObject())
            {
                if (!page.Value.TryGetProperty("revisions", out var revisions)) continue;
                var title = page.Value.GetProperty("title").GetString() ?? "";
                var content = revisions[0]
                    .GetProperty("slots").GetProperty("main")
                    .GetProperty("*").GetString() ?? "";
                result[title] = content;
            }
        }

        return result;
    }

    // ── Template comparison ───────────────────────────────────────

    /// <summary>
    /// Compares a mystery's rewards with existing wiki templates.
    /// Returns (matches, variantNumber) if a match is found.
    /// </summary>
    public static async Task<(bool XpMatches, bool ContentMatches, string? Variant)> CompareWithExistingTemplatesAsync(
        MysteryEvent mystery, MysteryItemMapping? mapping)
    {
        var templates = await FetchRewardTemplatesAsync();
        return CompareWithTemplates(mystery, templates);
    }

    /// <summary>
    /// Compares a mystery against pre-fetched templates. Does not make any API calls.
    /// First determines mystery type, then only checks compatible templates:
    /// Pet mysteries → Pet templates (/Pet, /Pet/2, ...).
    /// Standard mysteries → Standard templates (/Rewards, /2, /3, ...).
    /// Returns (XpMatches, ContentMatches, Variant).
    /// </summary>
    public static (bool XpMatches, bool ContentMatches, string? Variant) CompareWithTemplates(
        MysteryEvent mystery, Dictionary<string, string> templates)
    {
        bool isPet = mystery.MysteryType == MysteryType.Pet;

        // Step 1: Extract variant info and filter to only compatible templates
        var compatible = new List<(string Variant, string Content)>();
        foreach (var (title, content) in templates)
        {
            var idx = title.IndexOf("/Rewards", StringComparison.OrdinalIgnoreCase);
            string? variant = null;
            if (idx >= 0)
            {
                var after = title[(idx + "/Rewards".Length)..].TrimStart('/');
                if (!string.IsNullOrEmpty(after))
                    variant = after;
            }

            bool isPetTemplate = variant != null
                && variant.StartsWith("Pet", StringComparison.OrdinalIgnoreCase);

            if (isPet == isPetTemplate)
                compatible.Add((variant ?? "", content));
        }

        // Step 2: Check all compatible templates — prefer full content match over XP-only
        string? firstXpVariant = null;
        foreach (var (variant, content) in compatible)
        {
            var (xpMatch, contentMatch) = CompareRewardsWithTemplate(mystery, content);
            if (!xpMatch) continue;

            if (contentMatch)
                return (true, true, string.IsNullOrEmpty(variant) ? null : variant);

            // Remember first XP match as fallback (formatting-only mismatch)
            firstXpVariant ??= variant;
        }

        if (firstXpVariant != null)
            return (true, false, string.IsNullOrEmpty(firstXpVariant) ? null : firstXpVariant);

        return (false, false, null);
    }

    /// <summary>
    /// Determines the next available variant name for a new reward template.
    /// Standard: Rewards → Rewards/2 → Rewards/3 → ...
    /// Pet: Rewards/Pet → Rewards/Pet/2 → Rewards/Pet/3 → ...
    /// </summary>
    public static async Task<string> GetNextVariantNameAsync(bool isPet)
    {
        var templates = await FetchRewardTemplatesAsync();
        var prefix = isPet ? "Pet" : "";

        int maxNum = isPet ? 0 : 0; // base variant counts as 0
        foreach (var title in templates.Keys)
        {
            var idx = title.IndexOf("/Rewards", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var after = title[(idx + "/Rewards".Length)..].TrimStart('/');

            if (isPet)
            {
                if (!after.StartsWith("Pet", StringComparison.OrdinalIgnoreCase)) continue;
                var petAfter = after.Length > 3 ? after[3..].TrimStart('/') : "";
                if (string.IsNullOrEmpty(petAfter))
                    maxNum = Math.Max(maxNum, 1); // "Pet" = variant 1
                else if (int.TryParse(petAfter, out var n))
                    maxNum = Math.Max(maxNum, n);
            }
            else
            {
                // Skip Pet variants
                if (after.StartsWith("Pet", StringComparison.OrdinalIgnoreCase)) continue;
                // Skip named variants (e.g. "Secrets of Serenity")
                if (string.IsNullOrEmpty(after))
                    maxNum = Math.Max(maxNum, 1); // base = variant 1
                else if (int.TryParse(after, out var n))
                    maxNum = Math.Max(maxNum, n);
            }
        }

        int next = maxNum + 1;
        if (isPet)
            return next == 1 ? "Pet" : $"Pet/{next}";
        else
            return next == 1 ? "" : $"{next}";
    }

    /// <summary>
    /// Returns (xpMatches, contentMatches).
    /// xpMatches = XP progression is identical (right template variant found).
    /// contentMatches = all reward cells match for levels 1–50
    ///   (level 0 and PremiumLevel rows are hardcoded and skipped).
    /// </summary>
    private static (bool XpMatches, bool ContentMatches) CompareRewardsWithTemplate(
        MysteryEvent mystery, string templateContent)
    {
        // Strip HTML comments before comparison
        templateContent = Regex.Replace(templateContent, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Parse wiki template into rows: each row = list of cell values
        var wikiRows = ParseTemplateRows(templateContent);

        // ── Phase 1: XP pre-filter (levels 1+) ──
        var tier = mystery.FreeTier.Count > 0 ? mystery.FreeTier
            : mystery.SilverTier.Count > 0 ? mystery.SilverTier
            : mystery.GoldTier;

        if (tier.Count == 0 || wikiRows.Count == 0) return (false, false);

        // Filter to data rows only (skip level 0 and PremiumLevel)
        var dataRows = wikiRows
            .Where(r => r.Count >= 5
                && !r[0].Contains("PremiumLevel")
                && r[0].Trim() != "0")
            .ToList();

        if (dataRows.Count != tier.Count - 1) return (false, false); // levels 1..N

        for (int i = 0; i < dataRows.Count; i++)
        {
            // XP is in cell [1]: "{{Pass XP}} 40" → extract number
            var xpMatch = Regex.Match(dataRows[i][1], @"\{\{Pass XP\}\}\s*(\d+)");
            if (!xpMatch.Success) return (false, false);
            if (int.Parse(xpMatch.Groups[1].Value) != tier[i + 1].XpRequired)
                return (false, false);
        }

        // ── Phase 2: Reward content comparison (cells 2,3,4 = F2P, Silver, Gold) ──
        var generated = GenerateRewardTemplate(mystery, null);
        var genRows = ParseTemplateRows(generated);
        var genDataRows = genRows
            .Where(r => r.Count >= 5
                && !r[0].Contains("PremiumLevel")
                && r[0].Trim() != "0")
            .ToList();

        if (genDataRows.Count != dataRows.Count) return (true, false);

        for (int i = 0; i < dataRows.Count; i++)
        {
            // Compare XP + F2P + Silver + Gold (cells 1-4)
            for (int c = 1; c <= 4; c++)
            {
                if (c >= dataRows[i].Count || c >= genDataRows[i].Count)
                    return (true, false);

                var wikiCell = NormalizeCell(dataRows[i][c]);
                var genCell = NormalizeCell(genDataRows[i][c]);
                if (wikiCell != genCell)
                    return (true, false);
            }
        }

        return (true, true);
    }

    /// <summary>
    /// Parses a wiki table into rows of cells.
    /// Each row starts with |- and contains | cell values.
    /// </summary>
    private static List<List<string>> ParseTemplateRows(string content)
    {
        var rows = new List<List<string>>();

        // Normalize line endings
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");

        var rowMatches = Regex.Matches(content, @"\|\-\s*\n((?:\|(?!\-|\})[^\n]*\n?)+)");
        foreach (Match row in rowMatches)
        {
            var cells = new List<string>();
            // Each line starting with | is a cell (but not |- or |})
            foreach (var line in row.Groups[1].Value.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("|") && !trimmed.StartsWith("|-") && !trimmed.StartsWith("|}"))
                    cells.Add(trimmed[1..].Trim()); // strip leading "| "
            }
            if (cells.Count > 0)
                rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// Normalizes a single cell value for comparison:
    /// trims, normalizes dashes and whitespace.
    /// </summary>
    private static string NormalizeCell(string cell)
    {
        cell = cell.Trim();
        cell = cell.Replace('\u2013', '\u2014'); // en dash → em dash
        cell = Regex.Replace(cell, @"\s+", " ");  // collapse whitespace
        return cell;
    }

    /// <summary>
    /// Normalizes a reward template for content comparison:
    /// strips comments, normalizes dashes/whitespace, trims lines.
    /// </summary>
    private static string NormalizeTemplateForComparison(string content)
    {
        // Strip HTML comments and any surrounding whitespace they leave behind
        content = Regex.Replace(content, @"\s*<!--.*?-->\s*", " ", RegexOptions.Singleline);

        // Normalize line endings
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");

        // Normalize dashes: en dash (–) → em dash (—) to match wiki convention
        content = content.Replace('\u2013', '\u2014'); // en dash → em dash

        // Normalize wiki table cell spacing: "|X" → "| X" (some wiki edits omit the space)
        content = Regex.Replace(content, @"^\|\s*(?=[^-}|!])", "| ", RegexOptions.Multiline);

        // Trim each line and collapse blank lines
        content = string.Join("\n", content.Split('\n').Select(l => l.TrimEnd()));
        content = Regex.Replace(content, @"\n{3,}", "\n\n");

        return content.Trim();
    }

    // ── Reward template generation ────────────────────────────────

    private const string PassXpHeader =
        "{{#Invoke:Utils|Icon|name=Silver Pass Ticket|suppressLevel=true|size=32}}";
    private const string GoldPassHeader =
        "{{#Invoke:Utils|Icon|name=Gold Pass Ticket|suppressLevel=true|size=32}}";
    private const string InventorySlotIcon =
        "{{#Invoke:Utils|Icon|name=InventorySlot|suppressLevel=true|size=24}}";

    /// <summary>
    /// Generates a wiki reward table matching the established wiki format.
    /// Level 0 is hardcoded (identical across all mysteries).
    /// </summary>
    public static string GenerateRewardTemplate(MysteryEvent mystery, MysteryItemMapping? mapping)
    {
        var sb = new StringBuilder();
        var isPet = mystery.MysteryType == MysteryType.Pet;

        // Pre-assign decoration numbers (silver 1,2 → gold 3,4,5 for Standard)
        int decoNum = 1;
        foreach (var level in mystery.SilverTier)
            foreach (var r in level.Rewards)
                if (r.Type == MysteryRewardType.Decoration)
                    r.ItemLevel = decoNum++;
        foreach (var level in mystery.GoldTier)
            foreach (var r in level.Rewards)
                if (r.Type == MysteryRewardType.Decoration)
                    r.ItemLevel = decoNum++;

        // Header
        sb.AppendLine("{| class=\"article-table\"");
        sb.AppendLine("! Level");
        sb.AppendLine("! {{Pass XP}}Points Needed");
        sb.AppendLine("! F2P  Reward");
        sb.AppendLine($"! {PassXpHeader} Silver Pass Reward");
        sb.AppendLine($"! {GoldPassHeader} Gold Pass Reward");

        // Level 0 — hardcoded (same for all mysteries)
        sb.AppendLine("|-");
        sb.AppendLine("| 0");
        sb.AppendLine("| {{Pass XP}} \u2014");
        sb.AppendLine("| {{Energy}} 10");
        sb.AppendLine($"| 5 {{{{Gems}}}}/day <br> 3 {InventorySlotIcon} [[Inventory]] slots");
        sb.AppendLine($"| 5 {{{{Gems}}}}/day <br> 3 {InventorySlotIcon} [[Inventory]] slots");

        // Levels 1+
        int maxLevels = Math.Max(mystery.FreeTier.Count,
            Math.Max(mystery.SilverTier.Count, mystery.GoldTier.Count));

        for (int i = 1; i < maxLevels; i++)
        {
            var free = i < mystery.FreeTier.Count ? mystery.FreeTier[i] : null;
            var silver = i < mystery.SilverTier.Count ? mystery.SilverTier[i] : null;
            var gold = i < mystery.GoldTier.Count ? mystery.GoldTier[i] : null;

            int xp = free?.XpRequired ?? silver?.XpRequired ?? gold?.XpRequired ?? 0;

            sb.AppendLine("|-");
            sb.AppendLine($"| {i}");
            sb.AppendLine($"| {{{{Pass XP}}}} {xp}");
            sb.AppendLine($"| {FormatRewards(free?.Rewards)}");
            sb.AppendLine($"| {FormatRewards(silver?.Rewards, "silver")}");
            sb.AppendLine($"| {FormatRewards(gold?.Rewards, "gold")}");
        }

        // Premium levels — hardcoded (same for all mysteries)
        int[] premiumXp = [2000, 3000, 3000, 4000, 5000];
        for (int p = 0; p < 5; p++)
        {
            sb.AppendLine("|-");
            sb.AppendLine($"| {{{{PremiumLevel|{p + 1}}}}}");
            sb.AppendLine($"| {{{{Pass XP}}}} {premiumXp[p]}");
            sb.AppendLine("| {{Dash}}");
            sb.AppendLine($"| colspan = 2 style = \"text-align: center\" | {{{{Item/Group|Challenge Chest|{p + 1}|iconLevel=1}}}}");
        }

        sb.AppendLine("|}");
        return sb.ToString();
    }

    private static string FormatRewards(List<MysteryReward>? rewards, string tier = "free")
    {
        if (rewards == null || rewards.Count == 0) return "?";

        var parts = new List<string>();
        foreach (var r in rewards)
        {
            // Skip perks (level 0 is hardcoded, other perks are not in table)
            if (r.Type == MysteryRewardType.Perk) continue;

            var formatted = FormatSingleReward(r, tier);
            if (!string.IsNullOrEmpty(formatted))
                parts.Add(formatted);
        }

        return parts.Count > 0 ? string.Join(" <br> ", parts) : "?";
    }

    private static string FormatSingleReward(MysteryReward reward, string tier)
    {
        return reward.Type switch
        {
            MysteryRewardType.Coins => $"{{{{Coins}}}} {reward.Amount}",
            MysteryRewardType.Diamonds => $"{{{{Gems}}}} {reward.Amount}",
            MysteryRewardType.Energy => $"{{{{Energy}}}} {reward.Amount}",
            MysteryRewardType.Experience => $"{{{{XP}}}} {reward.Amount}",
            MysteryRewardType.Item => FormatItemReward(reward),
            MysteryRewardType.Decoration => FormatDecorationReward(reward, tier),
            MysteryRewardType.CardPack => FormatCardPack(reward),
            MysteryRewardType.Pet => $"{{{{Decoration|silver|0|text={{{{{{pet}}}}}}}}}}",
            MysteryRewardType.InformantTip => FormatInformantTip(reward),
            _ => ""
        };
    }

    // Items that use {{Item/nolevel|Name|Level}} (no chain grouping)
    private static readonly HashSet<string> NoLevelItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Missing Evidence", "Clues Envelope", "Unlimited Energy"
    };

    // Items that use plain {{Item|Name|Level}} (unique items, not part of a chain group)
    private static readonly HashSet<string> PlainItemItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Brown Chest", "Fancy Blue Chest"
    };

    private static string FormatItemReward(MysteryReward reward)
    {
        var key = reward.ItemKey ?? "";

        // Hourglass with duration override (TimeSkipBoosterSingle)
        if (key.StartsWith("TimeSkipBoosterSingle") && reward.DurationMs.HasValue)
        {
            var duration = FormatDuration(reward.DurationMs.Value);
            return $"{duration} {{{{Item/Group|Hourglass|1}}}}";
        }

        // Time Skip Booster (non-single, no duration override)
        if (key.StartsWith("TimeSkipBooster_"))
        {
            var level = key.Contains("_02") ? 2 : 1;
            return $"{{{{Item/Group|Time Skip Booster|{level}}}}}";
        }

        // Unlimited Energy → {{Item/nolevel|Unlimited Energy|N}}
        if (key.StartsWith("InfiniteEnergy"))
        {
            var level = key.Contains("Mid") ? 2 : key.Contains("Big") ? 3 : 1;
            return $"{{{{Item/nolevel|Unlimited Energy|{level}}}}}";
        }

        var name = reward.ItemDisplayName ?? reward.ItemKey ?? "Unknown";

        // Items that use {{Item/nolevel|Name|Level}}
        if (NoLevelItems.Contains(name))
        {
            if (reward.ItemLevel.HasValue && reward.ItemLevel.Value > 0)
                return $"{{{{Item/nolevel|{name}|{reward.ItemLevel.Value}}}}}";
            return $"{{{{Item/nolevel|{name}}}}}";
        }

        // Items that use plain {{Item|Name|Level}}
        if (PlainItemItems.Contains(name))
        {
            if (reward.ItemLevel.HasValue && reward.ItemLevel.Value > 0)
                return reward.Amount > 1
                    ? $"{{{{Item|{name}|{reward.ItemLevel.Value}}}}} x{reward.Amount}"
                    : $"{{{{Item|{name}|{reward.ItemLevel.Value}}}}}";
            return $"{{{{Item|{name}}}}}";
        }

        // Default: leveled → Item/Group, unleveled → Item/nolevel
        if (reward.ItemLevel.HasValue && reward.ItemLevel.Value > 0)
            return reward.Amount > 1
                ? $"{{{{Item/Group|{name}|{reward.ItemLevel.Value}}}}} x{reward.Amount}"
                : $"{{{{Item/Group|{name}|{reward.ItemLevel.Value}}}}}";

        return reward.Amount > 1
            ? $"{{{{Item/nolevel|{name}}}}} x{reward.Amount}"
            : $"{{{{Item/nolevel|{name}}}}}";
    }

    private static string FormatDecorationReward(MysteryReward reward, string tier)
    {
        // ItemLevel holds the pre-assigned sequential number (1-5)
        var num = reward.ItemLevel ?? 0;
        return $"{{{{Decoration|{tier}|{num}}}}}";
    }

    private static string FormatCardPack(MysteryReward reward)
    {
        // TCE_CardPackBasic_NStars_01 → {{Item/nolevel|Clues Envelope|N}}
        var level = 1;
        if (reward.CardPackId != null)
        {
            var match = Regex.Match(reward.CardPackId, @"(\d)Stars");
            if (match.Success) level = int.Parse(match.Groups[1].Value);
        }
        return $"{{{{Item/nolevel|Clues Envelope|{level}}}}}";
    }

    private static string FormatInformantTip(MysteryReward reward)
    {
        // TCE_WildCardBasic_01 → Missing Evidence L1, TCE_WildCardSpecial_01 → L2
        var level = reward.InformantTipCardId?.Contains("Special") == true ? 2 : 1;
        return $"{{{{Item/nolevel|Missing Evidence|{level}}}}}";
    }

    private static string FormatDuration(long ms)
    {
        var totalMinutes = ms / 60000;
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        if (hours > 0 && minutes > 0)
            return $"{hours} h {minutes} m";
        if (hours > 0)
            return $"{hours} h";
        return $"{minutes} m";
    }

    // ── Event page generation ─────────────────────────────────────

    /// <summary>
    /// Generates the event page wikitext following the established wiki format.
    /// Two variants: Standard (5 decorations) and Pet (2 decorations + pet).
    /// </summary>
    public static string GenerateEventPage(MysteryEvent mystery, string? rewardVariant)
    {
        var sb = new StringBuilder();
        var itemName = mystery.EventItemName ?? "Unknown";
        var startDate = FormatStartDate(mystery.StartDate);
        var isPet = mystery.MysteryType == MysteryType.Pet;

        // Header: all 3 vardefines on one line + intro
        var suggestedTitle = mystery.WikiStatus.SuggestedPageTitle;
        bool eventHasDisambig = suggestedTitle != null && suggestedTitle != mystery.Name;
        var eventDisplayName = eventHasDisambig ? mystery.Name : "{{PAGENAME}}";

        // Event item: if name has parenthetical (from wiki mapping), strip for display
        var itemDisplayName = StripParenthetical(itemName);
        bool itemHasDisambig = itemDisplayName != itemName;

        sb.Append($"{{{{#vardefine:EventItem|{itemName}}}}}");
        sb.Append(itemHasDisambig
            ? $"{{{{#vardefine:EventItemDisplayName|{itemDisplayName}}}}}"
            : "{{#vardefine:EventItemDisplayName|{{#var:EventItem}}}}");
        sb.AppendLine($"{{{{#vardefine:EventDisplayName|{eventDisplayName}}}}}");

        sb.AppendLine($"{{{{Mystery Pass/Intro|startingDate={startDate}}}}}");
        sb.AppendLine();

        // Event Mechanics
        sb.AppendLine("== Event Mechanics ==");
        sb.AppendLine("{{Mystery Pass/Event Mechanics}}");
        sb.AppendLine();

        // Item Descriptions
        sb.AppendLine("== Item Descriptions ==");
        sb.AppendLine("{{Mystery Pass/ItemDesc}}");
        sb.AppendLine();

        // Statistics
        sb.AppendLine("== Statistics ==");
        sb.AppendLine("{{Mystery Pass/Event Item}}");
        sb.AppendLine();

        // Rewards
        sb.AppendLine("== Rewards ==");
        if (!string.IsNullOrEmpty(rewardVariant))
        {
            if (isPet && !string.IsNullOrEmpty(mystery.PetName))
                sb.AppendLine($"{{{{Mystery Pass/Rewards/{rewardVariant}|pet={mystery.PetName}}}}}");
            else
                sb.AppendLine($"{{{{Mystery Pass/Rewards/{rewardVariant}}}}}");
        }
        else
        {
            // No matching variant — use page name as variant (wiki convention)
            sb.AppendLine($"{{{{Mystery Pass/Rewards/{{{{PAGENAME}}}}}}}}");
        }
        sb.AppendLine();

        // Dialogue (empty tabber skeleton)
        sb.AppendLine("== Dialogue ==");
        sb.AppendLine("<tabber>");
        sb.AppendLine("|-|Event Intro=");
        sb.AppendLine();
        sb.AppendLine("|-|Getting Event Item L4=");
        sb.AppendLine();
        if (isPet)
        {
            sb.AppendLine($"|-|Getting {mystery.PetName ?? "Pet"}=");
            sb.AppendLine();
            sb.AppendLine("|-|Decoration Level 1=");
            sb.AppendLine();
            sb.AppendLine("|-|Decoration Level 2=");
            sb.AppendLine();
        }
        else
        {
            for (int i = 1; i <= 5; i++)
            {
                sb.AppendLine($"|-|Decoration Level {i}=");
                sb.AppendLine();
            }
        }
        sb.AppendLine("|-|Event Outro=");
        sb.AppendLine();
        sb.AppendLine("</tabber>");
        sb.AppendLine();

        // Gallery
        sb.AppendLine("== Gallery ==");
        sb.AppendLine(isPet ? "{{Mystery Pass/Gallery/Pet}}" : "{{Mystery Pass/Gallery}}");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the event item page wikitext following the established wiki format.
    /// Uses Lua module invocations and standardized templates matching existing wiki pages.
    /// </summary>
    public static string GenerateEventItemPage(MysteryEvent mystery, DataService? ds = null, WikiMappingCache? wikiMapping = null)
    {
        var sb = new StringBuilder();
        var eventName = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
        var year = mystery.StartDate?.Year.ToString() ?? "YYYY";

        // EventName + EventDisplayName on one line
        bool hasDisambiguation = eventName != mystery.Name;
        sb.Append($"{{{{#vardefine:EventName|{eventName}}}}}");
        if (hasDisambiguation)
            sb.AppendLine($"{{{{#vardefine:EventDisplayName|{mystery.Name}}}}}");
        else
            sb.AppendLine("{{#vardefine:EventDisplayName|{{#var:EventName}}}}");

        // Infobox Items — aligned = style
        sb.AppendLine("{{Infobox Items");
        sb.AppendLine("| image1 = ");
        sb.AppendLine("{{#tag:gallery|");
        sb.AppendLine("{{ItemNameToFilename|{{PAGENAME}}|1}} {{!}} Level 1");
        sb.AppendLine("{{ItemNameToFilename|{{PAGENAME}}|{{#Invoke:Items|GetItemMaxLevelFromChainName}}}} {{!}} Level {{#Invoke:Items|GetItemMaxLevelFromChainName}}");
        sb.AppendLine("}}");
        sb.AppendLine("| type   = Drop Item");
        sb.AppendLine("| source = Merging Items during {{Item/nolevel|{{#var:EventName}}}} Event");
        sb.AppendLine("}}");

        // Intro text
        sb.AppendLine($"{{{{Item/Group|{{{{PAGENAME}}}}|4}}}} is an item in '''''Merge Mansion'''''.  It is used in the {{{{Item/nolevel|{{{{#var:EventName}}}}}}}} [[Events|Event]] of {year}.");
        sb.AppendLine();

        // Gameplay notes
        sb.AppendLine("* {{Item/nolevel|{{PAGENAME}}|1}}  can spawn from any merge action which takes place on the normal board and also on any Story Event boards like other [[Events#Progression_Events|Mystery Pass events]].");
        sb.AppendLine("* {{Item/nolevel|{{PAGENAME}}|1}}  can be merged up to level 4, which then gives the max points of 20.");
        sb.AppendLine("* Similar to {{XP}}[[XP]] {{PAGENAME}} can be collected by tapping.");
        sb.AppendLine("* It is advisable to leave 2 empty spots whilst merging, as the priority order for drops whilst merging goes to:");
        sb.AppendLine("# {{Item/nolevel|{{PAGENAME}}|1}}");
        sb.AppendLine("# Double Bubbles");
        sb.AppendLine("# {{XP}} XP");
        sb.AppendLine();
        sb.AppendLine("Therefore, to maximise the {{PAGENAME}} drops and XP, it is best to keep 2 free spots for them to drop.");
        sb.AppendLine();

        // Descriptions
        sb.AppendLine("== Descriptions ==");
        for (int i = 1; i <= 4; i++)
        {
            sb.AppendLine($"{{{{Item/Icon|{{{{PAGENAME}}}}|{i}}}}} {{{{#Invoke:Items|GetItemDescFromChainName|{i}}}}}");
            sb.AppendLine();
        }

        // Statistics — Merge Stages
        sb.AppendLine("== Statistics ==");
        sb.AppendLine("=== Merge Stages ===");

        // Use WikiTableGenerator if chain data is available
        ParsedChain? eventItemChain = null;
        if (ds != null && !string.IsNullOrEmpty(mystery.EventItemType))
        {
            eventItemChain = ds.Chains.FirstOrDefault(c =>
                c.Items.Any(i => i.ItemType == mystery.EventItemType));
        }

        if (eventItemChain != null)
        {
            var generator = new WikiTableGenerator(ds!, wikiMapping);
            sb.Append(generator.Generate(eventItemChain, mystery.EventItemName ?? "{{PAGENAME}}", false));
        }
        else
        {
            // Fallback: hardcoded table for when chain data is unavailable
            sb.AppendLine("{| class=\"article-table\"");
            sb.AppendLine("|+ <u>{{PAGENAME}}</u>");
            sb.AppendLine("! Lvl");
            sb.AppendLine("! Image");
            sb.AppendLine("! Item");
            sb.AppendLine("! [[Coins|Sells for]]");
            sb.AppendLine("! Drops");

            var sellPrices = new[] { 1, 2, 4, 6 };
            for (int i = 1; i <= 4; i++)
            {
                sb.AppendLine("|-");
                sb.AppendLine($"| {i}");
                sb.AppendLine($"| style=\"text-align:center;\" |{{{{Item/Icon|{{{{PAGENAME}}}}|{i}}}}}");
                sb.AppendLine($"| <u>{{{{#Invoke:Items|GetItemNameFromChainName|{i}}}}}</u>");
                sb.AppendLine($"| {{{{Coins}}}}{sellPrices[i - 1]}");
                sb.AppendLine("| {{Dash}}");
            }
            sb.AppendLine("|}");
        }
        sb.AppendLine();

        // Double Bubbles
        sb.AppendLine("=== [[Double Bubble]]s ===");
        sb.AppendLine("{{#Invoke:Items|GetItemBubbleTableFromChainName}}");

        return sb.ToString();
    }

    /// <summary>
    /// Strips trailing parenthetical suffix from a name.
    /// E.g., "Festive Feast (Mystery Item 2025)" → "Festive Feast"
    /// </summary>
    private static string StripParenthetical(string name)
    {
        var idx = name.LastIndexOf('(');
        if (idx > 0)
            return name[..idx].TrimEnd();
        return name;
    }

    /// <summary>
    /// Formats date as "Month Dth, Year" (e.g., "January 30th, 2026").
    /// </summary>
    private static string FormatStartDate(DateTime? date)
    {
        if (date == null) return "Unknown";
        var d = date.Value;
        var suffix = (d.Day % 10 == 1 && d.Day != 11) ? "st"
            : (d.Day % 10 == 2 && d.Day != 12) ? "nd"
            : (d.Day % 10 == 3 && d.Day != 13) ? "rd"
            : "th";
        var month = d.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
        return $"{month} {d.Day}{suffix}, {d.Year}";
    }

    // ── Event page content comparison ─────────────────────────────

    /// <summary>
    /// Batch-fetches wikitext content for multiple pages.
    /// Returns title → wikitext content map.
    /// </summary>
    public static async Task<Dictionary<string, string>> FetchPagesContentAsync(IEnumerable<string> titles)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var titleList = titles.ToList();

        for (int i = 0; i < titleList.Count; i += 50)
        {
            var batch = titleList.Skip(i).Take(50);
            var joined = string.Join("|", batch);
            var url = $"{BaseApiUrl}?action=query&titles={Uri.EscapeDataString(joined)}" +
                      "&prop=revisions&rvprop=content&rvslots=main&format=json";

            var json = await Http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");

            foreach (var page in pages.EnumerateObject())
            {
                if (!page.Value.TryGetProperty("revisions", out var revisions)) continue;
                var title = page.Value.GetProperty("title").GetString() ?? "";
                var content = revisions[0]
                    .GetProperty("slots").GetProperty("main")
                    .GetProperty("*").GetString() ?? "";
                result[title] = content;
            }
        }

        return result;
    }

    /// <summary>
    /// Compares generated event page content with wiki page content.
    /// Strips Dialogue section and normalizes formatting before comparison.
    /// </summary>
    public static bool CompareEventPageContent(string generated, string wikiContent)
    {
        generated = RemoveDialogueSection(generated);
        wikiContent = RemoveDialogueSection(wikiContent);

        generated = NormalizeWikiContent(generated);
        wikiContent = NormalizeWikiContent(wikiContent);

        return generated == wikiContent;
    }

    /// <summary>
    /// Checks if the event item page references the correct event name.
    /// The page is mostly static — the only variable part is {{#vardefine:EventName|...}}.
    /// </summary>
    public static bool CompareEventItemPageContent(MysteryEvent mystery, string wikiContent)
    {
        // Check that {{#vardefine:EventName|MYSTERY_NAME}} is present with correct name
        var match = Regex.Match(wikiContent, @"\{\{#vardefine:EventName\|([^}]+)\}\}");
        if (!match.Success) return false;

        var wikiEventName = match.Groups[1].Value.Trim();
        return string.Equals(wikiEventName, mystery.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the == Dialogue == section (including all its content)
    /// up to the next == heading == or end of string.
    /// </summary>
    private static string RemoveDialogueSection(string content)
    {
        return Regex.Replace(content,
            @"={2,}\s*Dialogue\s*={2,}.*?(?=\n={2,}\s*[^=]|\z)",
            "", RegexOptions.Singleline);
    }

    /// <summary>
    /// Normalizes wiki content for comparison:
    /// - Unifies line endings
    /// - Normalizes heading whitespace (==Heading== → == Heading ==)
    /// - Removes [[Category:...]] tags
    /// - Trims lines and collapses blank lines
    /// </summary>
    private static string NormalizeWikiContent(string content)
    {
        // Normalize line endings
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");

        // Normalize heading whitespace: ==Heading== → == Heading ==
        content = Regex.Replace(content, @"(={2,})\s*([^=\n]+?)\s*(={2,})", "$1 $2 $3");

        // Remove categories
        content = Regex.Replace(content, @"\[\[Category:[^\]]*\]\]\s*", "");

        // Trim each line
        content = string.Join("\n", content.Split('\n').Select(l => l.TrimEnd()));

        // Collapse multiple blank lines to single
        content = Regex.Replace(content, @"\n{3,}", "\n\n");

        return content.Trim();
    }

    // ── Batch wiki status check ───────────────────────────────────

    /// <summary>
    /// Checks wiki page existence for all mysteries in a single batch operation.
    /// Uses persistent cache: confirmed-true values are skipped, false/null are re-checked.
    /// </summary>
    public static async Task CheckAllMysteryStatusAsync(
        IReadOnlyList<MysteryEvent> mysteries, DataService? ds)
    {
        using var _t = AppLogger.Timed($"CheckAllMysteryStatusAsync ({mysteries.Count} mysteries)");

        // Load cache and apply confirmed-true values
        var cache = LoadStatusCache();
        ApplyCache(mysteries, cache);
        AppLogger.Info($"Cache loaded: {cache.Entries.Count} entries");

        // Resolve suggested page titles (needed even for cached entries)
        // Step 1: Detect same-name mystery events
        var nameGroups = mysteries
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        foreach (var m in mysteries)
        {
            if (!string.IsNullOrEmpty(m.WikiStatus.SuggestedPageTitle)) continue;

            var pageName = m.Name;
            var suggestedTitle = pageName;

            // Same-name mystery → always disambiguate with year
            if (nameGroups.Contains(m) && m.StartDate.HasValue)
            {
                suggestedTitle = $"{pageName} (Mystery {m.StartDate.Value.Year})";
            }
            else if (ds != null)
            {
                // Collision with chain/item names
                bool collision = ds.ChainNames.Values.Any(n =>
                    string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
                if (!collision)
                    collision = ds.ItemNames.Values.Any(n =>
                        string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
                if (collision && m.StartDate.HasValue)
                    suggestedTitle = $"{pageName} (Mystery {m.StartDate.Value.Year})";
            }

            m.WikiStatus.SuggestedPageTitle = suggestedTitle;
        }

        // ── Page existence: only check pages not yet confirmed ────
        var titleToMystery = new Dictionary<string, List<(MysteryEvent Mystery, string Type)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var m in mysteries)
        {
            // Event page — skip if already confirmed
            if (m.WikiStatus.EventPageExists != true)
            {
                var title = m.WikiStatus.SuggestedPageTitle ?? m.Name;
                if (!titleToMystery.ContainsKey(title))
                    titleToMystery[title] = new();
                titleToMystery[title].Add((m, "EventPage"));
            }

            // Item page — skip if already confirmed (or no item name yet)
            if (m.WikiStatus.EventItemPageExists != true && !string.IsNullOrEmpty(m.EventItemName))
            {
                if (!titleToMystery.ContainsKey(m.EventItemName))
                    titleToMystery[m.EventItemName] = new();
                titleToMystery[m.EventItemName].Add((m, "ItemPage"));
            }
        }

        AppLogger.Info($"PageExistence: {titleToMystery.Count} titles to check");
        if (titleToMystery.Count > 0)
        {
            Dictionary<string, bool> existMap;
            using (var _tp = AppLogger.Timed("CheckPagesExistAsync"))
                existMap = await CheckPagesExistAsync(titleToMystery.Keys);

            foreach (var (title, entries) in titleToMystery)
            {
                bool exists = existMap.GetValueOrDefault(title, false);
                foreach (var (mystery, type) in entries)
                {
                    if (type == "EventPage")
                        mystery.WikiStatus.EventPageExists = exists;
                    else
                        mystery.WikiStatus.EventItemPageExists = exists;
                }
            }
        }

        // ── Template comparison: only check mysteries not yet fully confirmed ──
        var needsTemplateCheck = mysteries
            .Where(m => m.WikiStatus.RewardTemplateMatches != true
                     || m.WikiStatus.RewardContentMatches != true)
            .ToList();

        AppLogger.Info($"TemplateCheck: {needsTemplateCheck.Count} mysteries need check");
        if (needsTemplateCheck.Count > 0)
        {
            try
            {
                Dictionary<string, string> templates;
                using (var _tr = AppLogger.Timed("FetchRewardTemplatesAsync"))
                    templates = await FetchRewardTemplatesAsync();
                foreach (var m in needsTemplateCheck)
                {
                    var (xpMatch, contentMatch, variant) = CompareWithTemplates(m, templates);
                    m.WikiStatus.RewardTemplateMatches = xpMatch;
                    m.WikiStatus.RewardContentMatches = contentMatch;
                    m.WikiStatus.MatchingVariant = variant;
                }
            }
            catch
            {
                // Template comparison is optional — continue without it
            }
        }

        // ── Event page content comparison: fetch + compare (excluding Dialogue) ──
        var needsPageContentCheck = mysteries
            .Where(m => m.WikiStatus.EventPageExists == true
                     && m.WikiStatus.EventPageContentMatches != true)
            .ToList();

        AppLogger.Info($"EventPageContentCheck: {needsPageContentCheck.Count} mysteries need check");
        if (needsPageContentCheck.Count > 0)
        {
            try
            {
                var pageTitles = needsPageContentCheck
                    .Select(m => m.WikiStatus.SuggestedPageTitle ?? m.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Dictionary<string, string> pageContents;
                using (var _te = AppLogger.Timed("FetchEventPagesContentAsync"))
                    pageContents = await FetchPagesContentAsync(pageTitles);

                foreach (var m in needsPageContentCheck)
                {
                    var title = m.WikiStatus.SuggestedPageTitle ?? m.Name;
                    if (!pageContents.TryGetValue(title, out var wikiContent)) continue;

                    var generated = GenerateEventPage(m, m.WikiStatus.MatchingVariant);
                    m.WikiStatus.EventPageContentMatches =
                        CompareEventPageContent(generated, wikiContent);
                }
            }
            catch
            {
                // Page content comparison is optional — continue without it
            }
        }

        // ── Event item page content comparison: check EventName vardefine ──
        var needsItemContentCheck = mysteries
            .Where(m => m.WikiStatus.EventItemPageExists == true
                     && m.WikiStatus.EventItemPageContentMatches != true
                     && !string.IsNullOrEmpty(m.EventItemName))
            .ToList();

        AppLogger.Info($"ItemPageContentCheck: {needsItemContentCheck.Count} mysteries need check");
        if (needsItemContentCheck.Count > 0)
        {
            try
            {
                var itemTitles = needsItemContentCheck
                    .Select(m => m.EventItemName!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Dictionary<string, string> itemContents;
                using (var _ti = AppLogger.Timed("FetchItemPagesContentAsync"))
                    itemContents = await FetchPagesContentAsync(itemTitles);

                foreach (var m in needsItemContentCheck)
                {
                    if (!itemContents.TryGetValue(m.EventItemName!, out var wikiContent)) continue;
                    m.WikiStatus.EventItemPageContentMatches =
                        CompareEventItemPageContent(m, wikiContent);
                }
            }
            catch
            {
                // Item page content comparison is optional — continue without it
            }
        }

        // Save updated cache
        UpdateCache(mysteries, cache);
        SaveStatusCache(cache);
    }

    // ── Wiki publishing ───────────────────────────────────────────

    /// <summary>
    /// Publishes wikitext to a page using authenticated bot credentials.
    /// </summary>
    public static async Task<string> PublishPageAsync(
        string username, string password,
        string pageTitle, string content, string editSummary)
    {
        using var client = await WikiMappingService.CreateAuthenticatedClientAsync(username, password);

        // Get CSRF token
        var csrfJson = await client.GetStringAsync(
            $"{BaseApiUrl}?action=query&meta=tokens&format=json");
        var csrfToken = JsonDocument.Parse(csrfJson).RootElement
            .GetProperty("query").GetProperty("tokens")
            .GetProperty("csrftoken").GetString()!;

        // Edit page
        var editResp = await client.PostAsync(BaseApiUrl, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["action"] = "edit",
                ["title"] = pageTitle,
                ["text"] = content,
                ["token"] = csrfToken,
                ["summary"] = editSummary,
                ["bot"] = "1",
                ["format"] = "json",
            }));

        var editRaw = await editResp.Content.ReadAsStringAsync();
        var editDoc = JsonDocument.Parse(editRaw);

        if (editDoc.RootElement.TryGetProperty("error", out var error))
            throw new Exception($"Wiki edit failed: {error.GetProperty("info").GetString()}");

        if (editDoc.RootElement.TryGetProperty("edit", out var edit))
        {
            var editResult = edit.TryGetProperty("result", out var r) ? r.GetString() : "?";
            return $"Edit result: {editResult}";
        }

        return "Unknown response";
    }
}
