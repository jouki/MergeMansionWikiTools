using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
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

    public static void ClearStatusCache()
    {
        try
        {
            if (File.Exists(StatusCachePath))
                File.Delete(StatusCachePath);
        }
        catch { }
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
    private static void ApplyCache(IReadOnlyList<MysteryEvent> mysteries, MysteryWikiStatusCache cache,
        bool hasDialogueService = false)
    {
        foreach (var m in mysteries)
        {
            if (!cache.Entries.TryGetValue(m.ProgressionEventId, out var cached)) continue;

            if (cached.EventPageExists)
            {
                m.WikiStatus.EventPageExists = true;
                m.WikiStatus.SuggestedPageTitle = cached.SuggestedPageTitle;
            }
            m.WikiStatus.EventPageContentMatches = cached.EventPageContentMatches;
            if (cached.EventItemPageExists)
                m.WikiStatus.EventItemPageExists = true;
            m.WikiStatus.EventItemPageContentMatches = cached.EventItemPageContentMatches;
            if (cached.RewardTemplateMatches)
            {
                m.WikiStatus.RewardTemplateMatches = true;
                m.WikiStatus.MatchingVariant = cached.MatchingVariant;
                if (cached.RewardContentMatches)
                    m.WikiStatus.RewardContentMatches = true;
            }
            m.WikiStatus.ImagesTotalExpected = cached.ImagesTotalExpected;
            m.WikiStatus.ImagesExistOnWiki = cached.ImagesExistOnWiki;
        }
    }

    /// <summary>
    /// Updates cache with current mystery statuses. Only stores confirmed-true values.
    /// </summary>
    private static void UpdateCache(IReadOnlyList<MysteryEvent> mysteries, MysteryWikiStatusCache cache)
    {
        foreach (var m in mysteries)
        {
            var entry = new CachedMysteryStatus
            {
                EventPageExists = m.WikiStatus.EventPageExists == true,
                EventPageContentMatches = m.WikiStatus.EventPageContentMatches,
                EventItemPageExists = m.WikiStatus.EventItemPageExists == true,
                EventItemPageContentMatches = m.WikiStatus.EventItemPageContentMatches,
                RewardTemplateMatches = m.WikiStatus.RewardTemplateMatches == true,
                RewardContentMatches = m.WikiStatus.RewardContentMatches == true,
                MatchingVariant = m.WikiStatus.MatchingVariant,
                SuggestedPageTitle = m.WikiStatus.SuggestedPageTitle,
                ImagesTotalExpected = m.WikiStatus.ImagesTotalExpected,
                ImagesExistOnWiki = m.WikiStatus.ImagesExistOnWiki
            };

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
                // Store both space and underscore variants (MediaWiki treats them as equivalent)
                result[title] = !missing;
                result[title.Replace(' ', '_')] = !missing;
                result[title.Replace('_', ' ')] = !missing;
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
    /// Backwards-compatible overload — generates empty dialogue skeleton and template Gallery.
    /// </summary>
    public static string GenerateEventPage(MysteryEvent mystery, string? rewardVariant)
    {
        return GenerateEventPageWithDialogues(mystery, rewardVariant, null);
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
        sb.AppendLine("== Descriptions == ");
        for (int i = 1; i <= 4; i++)
        {
            sb.AppendLine($"{{{{Item/Icon|{{{{PAGENAME}}}}|{i}}}}} {{{{#Invoke:Items|GetItemDescFromChainName|{i}}}}}");
            sb.AppendLine();
        }

        // Statistics — Merge Stages
        sb.AppendLine("== Statistics == ");
        sb.AppendLine("=== Merge Stages === ");

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
        sb.AppendLine("=== [[Double Bubble]]s === ");
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
    public static bool CompareEventPageContent(string generated, string wikiContent,
        bool stripDialogues = true)
    {
        if (stripDialogues)
        {
            // No dialogues available → ignore dialogue section differences
            generated = RemoveDialogueSection(generated);
            wikiContent = RemoveDialogueSection(wikiContent);
        }
        // When dialogues ARE available, compare including dialogue section

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
        IReadOnlyList<MysteryEvent> mysteries, DataService? ds,
        DialogueService? dialogueService = null)
    {
        using var _t = AppLogger.Timed($"CheckAllMysteryStatusAsync ({mysteries.Count} mysteries)");

        // Load cache and apply confirmed-true values
        var cache = LoadStatusCache();
        ApplyCache(mysteries, cache, hasDialogueService: dialogueService != null);
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
            // Always re-compute disambiguation (don't trust cached SuggestedPageTitle)
            m.WikiStatus.SuggestedPageTitle = null;

            var pageName = m.Name;
            var suggestedTitle = pageName;

            // Same-name mystery → always disambiguate with year
            if (nameGroups.Contains(m) && m.StartDate.HasValue)
            {
                suggestedTitle = $"{pageName} (Mystery {m.StartDate.Value.Year})";
            }
            else
            {
                bool collision = false;

                // Collision with chain/item names from DataService
                if (ds != null)
                {
                    collision = ds.ChainNames.Values.Any(n =>
                        string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
                    if (!collision)
                        collision = ds.ItemNames.Values.Any(n =>
                            string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
                }

                // Collision with event item names from OTHER mysteries
                if (!collision)
                {
                    collision = mysteries.Any(other =>
                        other != m &&
                        string.Equals(other.EventItemName, pageName, StringComparison.OrdinalIgnoreCase));
                }

                // Collision with mystery names matching other mysteries' event item names
                if (!collision)
                {
                    collision = mysteries.Any(other =>
                        other != m &&
                        string.Equals(other.Name, m.EventItemName, StringComparison.OrdinalIgnoreCase));
                }

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

        // ── Event page content comparison: fetch + compare ──
        // When dialogueService is available, ALWAYS re-check (cache may be from no-dialogue comparison)
        var needsPageContentCheck = mysteries
            .Where(m => m.WikiStatus.EventPageExists == true
                     && (m.WikiStatus.EventPageContentMatches != true || dialogueService != null))
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

                    // Generate WITH dialogues if available → compare INCLUDING dialogue section
                    var generated = GenerateEventPageWithDialogues(
                        m, m.WikiStatus.MatchingVariant, dialogueService);
                    bool hasDialogues = dialogueService != null &&
                        dialogueService.HasDialogues(m.ProgressionEventId);
                    m.WikiStatus.EventPageContentMatches =
                        CompareEventPageContent(generated, wikiContent, stripDialogues: !hasDialogues);
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

        // ── Images existence check ──
        try
        {
            var imageFileNames = new List<string>();
            var mysteryImageMap = new Dictionary<string, List<string>>();

            foreach (var m in mysteries)
            {
                var pageNameUsc = m.Name.Replace(' ', '_');
                var isPetM = m.MysteryType == MysteryType.Pet;
                int decoCount = isPetM ? 3 : 5;

                var expectedImages = new List<string>
                {
                    $"{pageNameUsc}.png", // wallpaper
                    FormatFileName(m.Name, 1), // badge
                    $"{pageNameUsc}_Icon.png" // icon
                };
                for (int d = isPetM ? 0 : 1; d <= decoCount + (isPetM ? -1 : 0); d++)
                    expectedImages.Add(FormatFileName(m.Name + "Decoration", d));

                m.WikiStatus.ImagesTotalExpected = expectedImages.Count;
                mysteryImageMap[m.ProgressionEventId] = expectedImages;
                imageFileNames.AddRange(expectedImages.Select(f => $"File:{f}"));
            }

            if (imageFileNames.Count > 0)
            {
                var imgExistMap = await CheckPagesExistAsync(imageFileNames);
                foreach (var m in mysteries)
                {
                    if (!mysteryImageMap.TryGetValue(m.ProgressionEventId, out var imgs)) continue;
                    m.WikiStatus.ImagesExistOnWiki = imgs.Count(f =>
                        imgExistMap.GetValueOrDefault($"File:{f}", false));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Images check failed: {ex.Message}");
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

    // ── Diff computation ──────────────────────────────────────────

    /// <summary>
    /// Fetches raw wikitext content for a single page.
    /// Returns null if the page doesn't exist.
    /// </summary>
    public static async Task<string?> FetchPageContentAsync(string title)
    {
        var result = await FetchPagesContentAsync(new[] { title });
        return result.TryGetValue(title, out var content) ? content : null;
    }

    /// <summary>
    /// Normalizes content for diff comparison:
    /// - Strips trailing whitespace per line
    /// - Collapses multiple blank lines to max 1
    /// - Removes HTML comments
    /// - Normalizes indentation (strip leading whitespace)
    /// </summary>
    public static string NormalizeDiffContent(string content)
    {
        // Strip HTML comments
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Normalize line endings
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");

        // Normalize heading whitespace: ==Heading== → == Heading ==
        content = Regex.Replace(content, @"(={2,})\s*([^=\n]+?)\s*(={2,})", "$1 $2 $3");

        // Normalize tabber lines: |-|Tab= or |-| Tab = → |-| Tab =
        content = Regex.Replace(content, @"\|-\|\s*(.+?)\s*=\s*$", "|-| $1 =", RegexOptions.Multiline);

        // Normalize wiki table cell spacing: "|X" → "| X"
        content = Regex.Replace(content, @"^\|\s*(?=[^-}|!])", "| ", RegexOptions.Multiline);

        // Trim each line (trailing whitespace + leading indentation)
        var lines = content.Split('\n').Select(l => l.Trim());
        content = string.Join("\n", lines);

        // Collapse multiple blank lines to single
        content = Regex.Replace(content, @"\n{3,}", "\n\n");

        return content.Trim();
    }

    /// <summary>
    /// Computes line-level diff between wiki (current) and generated (new) content.
    /// Uses longest common subsequence (LCS) algorithm for optimal diff.
    /// </summary>
    public static List<DiffLine> ComputeLineDiffs(string wikiContent, string generatedContent)
    {
        var wikiNorm = NormalizeDiffContent(wikiContent);
        var genNorm = NormalizeDiffContent(generatedContent);

        var wikiLines = wikiNorm.Split('\n');
        var genLines = genNorm.Split('\n');

        // LCS table
        int m = wikiLines.Length, n = genLines.Length;
        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = wikiLines[i - 1] == genLines[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        // Backtrack to produce diff
        var result = new List<DiffLine>();
        int wi = m, gi = n;
        var stack = new Stack<DiffLine>();

        while (wi > 0 || gi > 0)
        {
            if (wi > 0 && gi > 0 && wikiLines[wi - 1] == genLines[gi - 1])
            {
                stack.Push(new DiffLine { Type = DiffLineType.Match, Text = wikiLines[wi - 1] });
                wi--; gi--;
            }
            else if (gi > 0 && (wi == 0 || dp[wi, gi - 1] >= dp[wi - 1, gi]))
            {
                stack.Push(new DiffLine { Type = DiffLineType.Added, Text = genLines[gi - 1] });
                gi--;
            }
            else
            {
                stack.Push(new DiffLine { Type = DiffLineType.Removed, Text = wikiLines[wi - 1] });
                wi--;
            }
        }

        while (stack.Count > 0)
            result.Add(stack.Pop());

        return result;
    }

    /// <summary>
    /// Fetches wiki content and computes diff for the given scope.
    /// Returns (wikiContent, generatedContent, diffs).
    /// </summary>
    public static async Task<(string? WikiContent, string GeneratedContent, List<DiffLine> Diffs)>
        ComputeDiffAsync(MysteryEvent mystery, MysteryDiffScope scope,
            DataService? ds, WikiMappingCache? wikiMapping, MysteryItemMapping? mapping,
            DialogueService? dialogueService)
    {
        string pageTitle;
        string generated;

        switch (scope)
        {
            case MysteryDiffScope.Rewards:
                // Find the matching template variant
                var variant = mystery.WikiStatus.MatchingVariant;
                if (string.IsNullOrEmpty(variant))
                {
                    generated = GenerateRewardTemplate(mystery, mapping);
                    // Try to find a template page to diff against
                    var templates = await FetchRewardTemplatesAsync();
                    var (_, _, foundVariant) = CompareWithTemplates(mystery, templates);
                    variant = foundVariant;
                }
                generated = GenerateRewardTemplate(mystery, mapping);

                if (!string.IsNullOrEmpty(variant))
                    pageTitle = $"Template:Mystery Pass/Rewards/{variant}";
                else
                    pageTitle = $"Template:Mystery Pass/Rewards/{mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name}";

                break;

            case MysteryDiffScope.EventPage:
                pageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
                generated = GenerateEventPageWithDialogues(mystery, mystery.WikiStatus.MatchingVariant, dialogueService);
                break;

            case MysteryDiffScope.EventItemPage:
                pageTitle = mystery.EventItemName ?? mystery.Name;
                generated = GenerateEventItemPage(mystery, ds, wikiMapping);
                break;

            default:
                return (null, "", new List<DiffLine>());
        }

        var wikiContent = await FetchPageContentAsync(pageTitle);
        if (wikiContent == null)
            return (null, generated, new List<DiffLine>());

        var diffs = ComputeLineDiffs(wikiContent, generated);
        return (wikiContent, generated, diffs);
    }

    // ── Event page generation with dialogues ─────────────────────

    // ── Pet display name mapping (loaded from Pets.json) ──────

    private static Dictionary<string, string>? _petDisplayNames;

    /// <summary>
    /// Loads pet display names from Dump/Experimental/Pets.json.
    /// Maps PetId (=ConfigKey) → SelectionHeader (display name).
    /// </summary>
    public static void LoadPetDisplayNames(string? basePath, string? apkVersion)
    {
        _petDisplayNames = null;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(apkVersion)) return;

        var petsPath = Path.Combine(basePath, apkVersion, "Dump", "Experimental", "Pets.json");
        if (!File.Exists(petsPath)) return;

        try
        {
            var json = File.ReadAllText(petsPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            var root = doc.RootElement;
            if (root.TryGetProperty("Data", out var dataEl))
                root = dataEl;

            _petDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Data can be array directly or object with array property
            System.Text.Json.JsonElement petsArray;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                petsArray = root;
            else if (root.TryGetProperty("Pets", out var pa))
                petsArray = pa;
            else
                return;

            foreach (var pet in petsArray.EnumerateArray())
            {
                var petId = pet.TryGetProperty("PetId", out var pid) ? pid.GetString() : null;
                var header = pet.TryGetProperty("SelectionHeader", out var sh) ? sh.GetString() : null;
                if (!string.IsNullOrEmpty(petId) && !string.IsNullOrEmpty(header))
                    _petDisplayNames[petId] = header;
            }

            AppLogger.Info($"Loaded {_petDisplayNames.Count} pet display names from Pets.json");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to load Pets.json: {ex.Message}");
        }
    }

    /// <summary>Returns true if Pets.json display names are loaded.</summary>
    public static bool HasPetDisplayNames => _petDisplayNames != null && _petDisplayNames.Count > 0;

    /// <summary>
    /// Gets pet display name from PetId/ConfigKey. Uses Pets.json data if loaded.
    /// </summary>
    public static string FormatPetDisplayName(string? configKey)
    {
        if (string.IsNullOrEmpty(configKey)) return "Pet";
        if (_petDisplayNames != null && _petDisplayNames.TryGetValue(configKey, out var displayName))
            return displayName;
        return configKey; // fallback: ConfigKey as-is
    }

    /// <summary>
    /// Generates event page wikitext with actual dialogues from DialogueService
    /// instead of empty tabber skeleton.
    /// </summary>
    public static string GenerateEventPageWithDialogues(
        MysteryEvent mystery, string? rewardVariant, DialogueService? dialogueService)
    {
        var sb = new StringBuilder();
        var itemName = mystery.EventItemName ?? "Unknown";
        var startDate = FormatStartDate(mystery.StartDate);
        var isPet = mystery.MysteryType == MysteryType.Pet;

        // Header: vardefines + intro
        var suggestedTitle = mystery.WikiStatus.SuggestedPageTitle;
        bool eventHasDisambig = suggestedTitle != null && suggestedTitle != mystery.Name;
        var eventDisplayName = eventHasDisambig ? mystery.Name : "{{PAGENAME}}";

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
        sb.AppendLine("== Event Mechanics == ");
        sb.AppendLine("{{Mystery Pass/Event Mechanics}}");
        sb.AppendLine();

        // Item Descriptions
        sb.AppendLine("== Item Descriptions == ");
        sb.AppendLine("{{Mystery Pass/ItemDesc}}");
        sb.AppendLine();

        // Statistics
        sb.AppendLine("== Statistics == ");
        sb.AppendLine("{{Mystery Pass/Event Item}}");
        sb.AppendLine();

        // Rewards
        sb.AppendLine("== Rewards == ");
        if (!string.IsNullOrEmpty(rewardVariant))
        {
            if (isPet && !string.IsNullOrEmpty(mystery.PetName))
            {
                var petDisplay = FormatPetDisplayName(mystery.PetName);
                sb.AppendLine($"{{{{Mystery Pass/Rewards/{rewardVariant}|pet={petDisplay}}}}}");
            }
            else
                sb.AppendLine($"{{{{Mystery Pass/Rewards/{rewardVariant}}}}}");
        }
        else
        {
            sb.AppendLine($"{{{{Mystery Pass/Rewards/{{{{PAGENAME}}}}}}}}");
        }
        sb.AppendLine();

        // Dialogue — use actual dialogues if available
        sb.AppendLine("== Dialogue == ");
        List<DialogueGroup>? dialogueGroups = null;
        if (dialogueService != null && dialogueService.HasDialogues(mystery.ProgressionEventId))
        {
            var petDisplayName = !string.IsNullOrEmpty(mystery.PetName)
                ? FormatPetDisplayName(mystery.PetName) : null;
            dialogueGroups = dialogueService.GetMysteryDialogues(
                mystery.ProgressionEventId, mystery.MysteryType, petDisplayName);
        }

        if (dialogueGroups != null && dialogueGroups.Count > 0)
        {
            sb.Append(DialogueService.FormatAsWikiTabber(dialogueGroups));
        }
        else
        {
            // Fallback: empty tabber skeleton
            sb.AppendLine("<tabber>");
            sb.AppendLine("|-| Event Intro =");
            sb.AppendLine();
            sb.AppendLine("|-| Getting Event Item L4 =");
            sb.AppendLine();
            if (isPet)
            {
                var petFallbackName = !string.IsNullOrEmpty(mystery.PetName)
                    ? FormatPetDisplayName(mystery.PetName) : "Pet";
                sb.AppendLine($"|-| Getting {petFallbackName} =");
                sb.AppendLine();
                sb.AppendLine("|-| Decoration Level 1 =");
                sb.AppendLine();
                sb.AppendLine("|-| Decoration Level 2 =");
                sb.AppendLine();
            }
            else
            {
                for (int i = 1; i <= 5; i++)
                {
                    sb.AppendLine($"|-| Decoration Level {i} =");
                    sb.AppendLine();
                }
            }
            sb.AppendLine("|-| Event Outro =");
            sb.AppendLine();
            sb.AppendLine("</tabber>");
        }
        sb.AppendLine();

        // Gallery — use wiki template (correct convention)
        sb.AppendLine("== Gallery == ");
        sb.AppendLine(isPet ? "{{Mystery Pass/Gallery/Pet}}" : "{{Mystery Pass/Gallery}}");

        return sb.ToString();
    }

    // ── Gallery generation ────────────────────────────────────────

    /// <summary>
    /// Generates a Gallery section with actual decoration image filenames.
    /// Uses the mystery name to derive expected wiki filenames for decorations,
    /// wallpaper, badge, and event item.
    /// </summary>
    public static string GenerateGallerySection(MysteryEvent mystery)
    {
        var sb = new StringBuilder();
        var isPet = mystery.MysteryType == MysteryType.Pet;
        var name = mystery.Name;

        sb.AppendLine("<gallery>");

        // Wallpaper
        sb.AppendLine($"{name} Wallpaper.png|Wallpaper");

        // Badge
        sb.AppendLine($"{name} Badge.png|Badge");

        // Decorations
        if (isPet)
        {
            // Pet mysteries have fewer decorations (typically 2)
            sb.AppendLine($"{name} Decoration 1.png|Decoration 1");
            sb.AppendLine($"{name} Decoration 2.png|Decoration 2");

            // Pet image
            if (!string.IsNullOrEmpty(mystery.PetName))
                sb.AppendLine($"{name} {mystery.PetName}.png|{mystery.PetName}");
        }
        else
        {
            // Standard mysteries have 5 decorations
            for (int i = 1; i <= 5; i++)
                sb.AppendLine($"{name} Decoration {i}.png|Decoration {i}");
        }

        // Event Item
        if (!string.IsNullOrEmpty(mystery.EventItemName))
            sb.AppendLine($"{mystery.EventItemName} Event Item.png|Event Item");

        sb.AppendLine("</gallery>");
        return sb.ToString();
    }

    // ── Wiki page update methods ──────────────────────────────────

    /// <summary>
    /// Updates the main wiki page — adds mystery to the year row in Latest Mystery Events.
    /// Format: table cell with {{Item/Group|EventName}} entries separated by " • ".
    /// </summary>
    public static async Task<string> UpdateMainPageAsync(
        string username, string password,
        string mysteryName, string pageTitle, DateTime? startDate)
    {
        var mainPageTitle = "Merge Mansion Wiki";
        var wikiContent = await FetchPageContentAsync(mainPageTitle);
        if (string.IsNullOrEmpty(wikiContent))
            throw new Exception("Could not fetch main wiki page content.");

        // Check if mystery already listed
        if (wikiContent.Contains(mysteryName, StringComparison.OrdinalIgnoreCase))
            return "Mystery already listed on main page.";

        var year = startDate?.Year.ToString() ?? DateTime.Now.Year.ToString();

        // Find the year row in Latest Mystery Events section
        // Format: | '''2026'''
        //         | {{Item/Group|...}} • {{Item/Group|...}}
        var yearPattern = $@"\| '''({year})'''\s*\n\| ([^\n]+)";
        var yearMatch = Regex.Match(wikiContent, yearPattern);

        if (yearMatch.Success)
        {
            // Append to existing year row
            var existingItems = yearMatch.Groups[2].Value;
            var newTemplate = pageTitle != mysteryName
                ? $"{{{{Item/Group|{pageTitle}|displayName={mysteryName}}}}}"
                : $"{{{{Item/Group|{mysteryName}}}}}";
            var updatedItems = existingItems.TrimEnd() + $" \u2022 {newTemplate}";

            var updatedPage = wikiContent[..yearMatch.Groups[2].Index]
                + updatedItems
                + wikiContent[(yearMatch.Groups[2].Index + yearMatch.Groups[2].Length)..];

            return await PublishPageAsync(username, password, mainPageTitle, updatedPage,
                $"Add {mysteryName} to Latest Mystery Events (via MergeMansionWikiTools)");
        }

        // Year row doesn't exist — need to insert a new year row
        // Find the "Latest [[Mystery Events]]" header
        var headerPattern = @"! colspan = 2 \| Latest \[\[Mystery Events\]\]";
        var headerMatch = Regex.Match(wikiContent, headerPattern);
        if (!headerMatch.Success)
            throw new Exception("Could not find 'Latest Mystery Events' header.");

        // Insert new year row after the most recent year
        // Find the |- after the last mystery year row
        var afterHeader = wikiContent[(headerMatch.Index + headerMatch.Length)..];
        var firstRowSep = afterHeader.IndexOf("\n|-\n", StringComparison.Ordinal);
        if (firstRowSep < 0)
            throw new Exception("Could not find row separator after Mystery Events header.");

        var insertPos = headerMatch.Index + headerMatch.Length + firstRowSep;
        var newTemplate2 = pageTitle != mysteryName
            ? $"{{{{Item/Group|{pageTitle}|displayName={mysteryName}}}}}"
            : $"{{{{Item/Group|{mysteryName}}}}}";
        var newYearRow = $"\n|-\n| '''{year}'''\n| {newTemplate2}";

        var updatedPage2 = wikiContent[..insertPos] + newYearRow + wikiContent[insertPos..];
        return await PublishPageAsync(username, password, mainPageTitle, updatedPage2,
            $"Add {mysteryName} to Latest Mystery Events (via MergeMansionWikiTools)");
    }

    /// <summary>
    /// Updates Module:Datatable/Various p.mysteries table.
    /// Format: integer-keyed [N] = { name = "...", startDate = "DD.MM.YYYY" }.
    /// </summary>
    public static async Task<string> UpdateMysteryTableAsync(
        string username, string password, MysteryEvent mystery)
    {
        var moduleTitle = "Module:Datatable/Various";
        var wikiContent = await FetchPageContentAsync(moduleTitle);
        if (string.IsNullOrEmpty(wikiContent))
            throw new Exception("Could not fetch Module:Datatable/Various content.");

        // Check if mystery already exists by name
        if (wikiContent.Contains($"\"{mystery.Name}\"", StringComparison.OrdinalIgnoreCase))
            return "Mystery already in Module:Datatable/Various.";

        // Find highest index in p.mysteries
        var indexPattern = @"\[(\d+)\]\s*=\s*\{";
        var indexMatches = Regex.Matches(wikiContent, indexPattern);
        int maxIndex = 0;
        foreach (Match m in indexMatches)
            if (int.TryParse(m.Groups[1].Value, out var idx) && idx > maxIndex)
                maxIndex = idx;

        int newIndex = maxIndex + 1;

        // Find the year comment for the mystery's year
        var year = mystery.StartDate?.Year ?? DateTime.Now.Year;
        var yearComment = $"-- {year}";

        // Build new entry
        var dateStr = mystery.StartDate?.ToString("dd.MM.yyyy") ?? "";
        var pageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
        bool needsDisplayName = pageTitle != mystery.Name;

        var entry = $"\t[{newIndex}] = {{ name = \"{pageTitle}\"";
        if (needsDisplayName)
            entry += $", displayName = \"{mystery.Name}\"";
        if (!string.IsNullOrEmpty(dateStr))
            entry += $", startDate = \"{dateStr}\"";
        entry += " },";

        // Find the right insertion point (after the year comment, before older entries)
        var yearCommentIdx = wikiContent.IndexOf(yearComment, StringComparison.Ordinal);
        if (yearCommentIdx >= 0)
        {
            // Insert after the year comment line
            var lineEnd = wikiContent.IndexOf('\n', yearCommentIdx);
            if (lineEnd >= 0)
            {
                var updatedPage = wikiContent[..(lineEnd + 1)] + entry + "\n" + wikiContent[(lineEnd + 1)..];
                return await PublishPageAsync(username, password, moduleTitle, updatedPage,
                    $"Add {mystery.Name} to p.mysteries (via MergeMansionWikiTools)");
            }
        }

        // Year comment doesn't exist — add it before the first entry
        var firstEntryPattern = @"p\.mysteries\s*=\s*\{";
        var firstMatch = Regex.Match(wikiContent, firstEntryPattern);
        if (!firstMatch.Success)
            throw new Exception("Could not find p.mysteries in Module:Datatable/Various.");

        var afterBrace = firstMatch.Index + firstMatch.Length;
        var newContent = $"\n\t{yearComment}\n{entry}";
        var updatedPage3 = wikiContent[..afterBrace] + newContent + wikiContent[afterBrace..];

        return await PublishPageAsync(username, password, moduleTitle, updatedPage3,
            $"Add {mystery.Name} to p.mysteries (via MergeMansionWikiTools)");
    }

    /// <summary>
    /// Updates the /wiki/Mystery page table with a new row.
    /// Format: 7 columns (Level, Icon, Link, Collectable Items, Duration, Year, Start Date, Finish Date).
    /// </summary>
    public static async Task<string> UpdateMysteryPageTableAsync(
        string username, string password, MysteryEvent mystery)
    {
        var wikiPageTitle = "Mystery";
        var wikiContent = await FetchPageContentAsync(wikiPageTitle);
        if (string.IsNullOrEmpty(wikiContent))
            throw new Exception("Could not fetch Mystery page content.");

        var suggestedTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;

        // Check if already listed
        if (wikiContent.Contains(suggestedTitle, StringComparison.OrdinalIgnoreCase))
            return "Mystery already listed on Mystery page.";

        // Find the highest level number in the table
        var levelPattern = @"^\|\s*(\d+)\s*$";
        int maxLevel = 0;
        foreach (var line in wikiContent.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), levelPattern);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var lvl) && lvl > maxLevel)
                maxLevel = lvl;
        }

        int newLevel = maxLevel + 1;

        // Build new row
        var itemName = mystery.EventItemName ?? "Unknown";
        var startDateStr = mystery.StartDate.HasValue
            ? FormatStartDate(mystery.StartDate)
            : "Unknown";
        var year = mystery.StartDate?.Year.ToString() ?? "????";

        var newRow = "|-\n" +
                     $"| {newLevel}\n" +
                     $"| {{{{Item/Icon|{suggestedTitle}}}}}\n" +
                     $"| [[{suggestedTitle}]]\n" +
                     $"| {{{{Item/Group|{itemName}|4}}}}\n" +
                     "| 21 d\n" +
                     $"! {year}\n" +
                     $"| {startDateStr}\n" +
                     "| \n";

        // Insert before |} closing
        var tableEnd = wikiContent.LastIndexOf("|}", StringComparison.Ordinal);
        if (tableEnd < 0)
            throw new Exception("Could not find table end on Mystery page.");

        var updatedPage = wikiContent[..tableEnd] + newRow + wikiContent[tableEnd..];

        return await PublishPageAsync(username, password, wikiPageTitle, updatedPage,
            $"Add {mystery.Name} to mystery table (via MergeMansionWikiTools)");
    }

    // ── Decoration detection ──────────────────────────────────────

    /// <summary>
    /// C# port of Module:Utils FormatFileName from the wiki.
    /// Pipeline: capitalizeAfterSpace → removeSpecialChars → stripSpaces → append level → .png
    /// E.g., "Omoide - The Challenge" + level 1 → "Omoide-TheChallenge01.png"
    /// E.g., "Omoide - The ChallengeDecoration" + level 3 → "Omoide-TheChallengeDecoration03.png"
    /// </summary>
    public static string FormatFileName(string name, int level, bool suppressLevel = false)
    {
        // Step 1: capitalizeAfterSpace — uppercase char after each space
        var sb = new StringBuilder();
        bool capitalizeNext = false;
        foreach (var ch in name)
        {
            if (capitalizeNext && char.IsLetter(ch))
            {
                sb.Append(char.ToUpper(ch));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(ch);
            }
            if (ch == ' ') capitalizeNext = true;
        }
        var result = sb.ToString();

        // Step 2: RemoveSpecialCharacters — remove ' ® ™ : ! ? / and & → And
        result = result.Replace("'", "").Replace("\u00AE", "").Replace("\u2122", "")
            .Replace(":", "").Replace("!", "").Replace("?", "").Replace("/", "")
            .Replace("&", "And");

        // Step 3: strip ALL whitespace
        result = Regex.Replace(result, @"\s+", "");

        // Step 4: append level (zero-padded to 2 digits)
        if (!suppressLevel)
            result += level.ToString("D2");

        // Step 5: append .png
        result += ".png";

        return result;
    }

    /// <summary>
    /// Resolves the Export PNGs directory from app settings.
    /// Path = ImageExporterBasePath / SelectedApkVersion / "Export - PNGs"
    /// </summary>
    public static string? ResolveExportPngsDir(string? basePath, string? apkVersion)
    {
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(apkVersion)) return null;
        var dir = Path.Combine(basePath, apkVersion, "Export - PNGs");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Type of tile extracted from a decoration atlas.
    /// </summary>
    public enum AtlasTileType { Decoration, Icon }

    /// <summary>
    /// Slices a decoration atlas PNG into tiles IN MEMORY (no disk writes).
    /// Detects icon tiles (content ≤100×100px) vs decoration tiles (full 256×256).
    /// Returns list of (type, bitmapData) pairs. Caller decides where to save.
    /// </summary>
    public static List<(AtlasTileType Type, byte[] PngData)> SliceDecorationAtlas(string atlasPath)
    {
        var result = new List<(AtlasTileType, byte[])>();
        if (!File.Exists(atlasPath)) return result;

        var decoder = new PngBitmapDecoder(
            new Uri(atlasPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];

        int w = source.PixelWidth;
        int h = source.PixelHeight;
        int tileSize = 256;
        int cols = w / tileSize;
        int rows = h / tileSize;

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                int x = col * tileSize;
                int y = row * tileSize;
                if (x + tileSize > w || y + tileSize > h) continue;

                var cropped = new CroppedBitmap(source, new System.Windows.Int32Rect(x, y, tileSize, tileSize));
                var pixels = new byte[tileSize * tileSize * 4];
                cropped.CopyPixels(pixels, tileSize * 4, 0);

                // Compute bounding box of non-transparent content
                int minX = tileSize, minY = tileSize, maxX = 0, maxY = 0;
                bool hasContent = false;
                for (int py = 0; py < tileSize; py++)
                {
                    for (int px = 0; px < tileSize; px++)
                    {
                        int alpha = pixels[(py * tileSize + px) * 4 + 3];
                        if (alpha > 10)
                        {
                            hasContent = true;
                            if (px < minX) minX = px;
                            if (px > maxX) maxX = px;
                            if (py < minY) minY = py;
                            if (py > maxY) maxY = py;
                        }
                    }
                }
                if (!hasContent) continue;

                int contentW = maxX - minX + 1;
                int contentH = maxY - minY + 1;
                bool isIcon = contentW <= 100 && contentH <= 100;

                BitmapSource saveSource;
                if (isIcon)
                {
                    int iconSize = 80;
                    int centerX = x + minX + contentW / 2;
                    int centerY = y + minY + contentH / 2;
                    int cropX = Math.Max(0, centerX - iconSize / 2);
                    int cropY = Math.Max(0, centerY - iconSize / 2);
                    if (cropX + iconSize > w) cropX = w - iconSize;
                    if (cropY + iconSize > h) cropY = h - iconSize;
                    saveSource = new CroppedBitmap(source,
                        new System.Windows.Int32Rect(cropX, cropY, iconSize, iconSize));
                }
                else
                {
                    saveSource = cropped;
                }

                // Encode to PNG in memory
                using var ms = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(saveSource));
                encoder.Save(ms);

                result.Add((isIcon ? AtlasTileType.Icon : AtlasTileType.Decoration, ms.ToArray()));
            }
        }

        // Reorder: 2×2 atlas grid stores decorations as [2,1,3,4] not [1,2,3,4]
        var decoIndices = new List<int>();
        for (int i = 0; i < result.Count; i++)
            if (result[i].Item1 == AtlasTileType.Decoration)
                decoIndices.Add(i);

        if (decoIndices.Count >= 2)
        {
            var temp = result[decoIndices[0]];
            result[decoIndices[0]] = result[decoIndices[1]];
            result[decoIndices[1]] = temp;
        }

        return result;
    }

    /// <summary>
    /// Copies a source file to Processed Images with wiki filename.
    /// If an optimized version already exists there, returns that path instead.
    /// </summary>
    private static string CopyToProcessed(string sourcePath, string wikiFilename, string? processedDir)
    {
        if (string.IsNullOrEmpty(processedDir)) return sourcePath;

        var destPath = Path.Combine(processedDir, wikiFilename);

        // If optimized version already exists, don't overwrite
        if (File.Exists(destPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(destPath);
                if (Views.OptimizationWindow.HasOptMarker(bytes))
                    return destPath; // keep optimized version
            }
            catch { }
        }

        // Copy source to processed dir (with wiki filename)
        try { File.Copy(sourcePath, destPath, overwrite: true); }
        catch { return sourcePath; }

        return destPath;
    }

    /// <summary>Returns file size if file has optimization marker, null otherwise.</summary>
    private static long? CheckOptMarker(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (Views.OptimizationWindow.HasOptMarker(bytes))
                return bytes.Length;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extracts decorations using sprite metadata from atlas_data.json.
    /// Returns true if sprites were found and extracted.
    /// </summary>
    private static bool ExtractDecorationsFromSpriteMetadata(
        string exportDir, string progressionEventId, string mysteryName,
        string pageNameUnderscore, string? processedDir, bool isPet,
        ref int decoNum, List<DetectedDecorationFile> result,
        MysteryEvent? mystery = null)
    {
        // Load atlas_data.json from parent of Export - PNGs
        var versionDir = Path.GetDirectoryName(exportDir);
        if (string.IsNullOrEmpty(versionDir)) return false;

        var atlasDataPath = Path.Combine(versionDir, "atlas_data.json");
        if (!File.Exists(atlasDataPath)) return false;

        try
        {
            var json = File.ReadAllText(atlasDataPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("sprites", out var sprites)) return false;

            // Get exact decoration IDs from mystery rewards (deterministic)
            var allowedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mystery != null)
            {
                var allTiers = mystery.FreeTier.Concat(mystery.SilverTier).Concat(mystery.GoldTier);
                foreach (var level in allTiers)
                    foreach (var r in level.Rewards)
                        if (r.Type == MysteryRewardType.Decoration && !string.IsNullOrEmpty(r.DecorationId))
                            allowedSlots.Add(r.DecorationId);
            }

            // Find sprites — filter by allowed slots if available, else by prefix
            var decoSprites = new List<(string Name, string TextureName, int X, int Y, int W, int H)>();
            string? iconTexture = null;
            int iconX = 0, iconY = 0, iconW = 0, iconH = 0;

            foreach (var sprite in sprites.EnumerateArray())
            {
                var name = sprite.GetProperty("name").GetString() ?? "";
                var texName = sprite.GetProperty("textureName").GetString() ?? "";

                if (!texName.StartsWith("sactx-")) continue;

                // Match both naming conventions:
                // NEW: SP_{Event}_Decoration_Slot{NN} (2025+ mysteries)
                // OLD: SP_{Event}_Decor_Item_{NN} (2022-2025 mysteries)
                bool isNewFormat = name.StartsWith($"{progressionEventId}_Decoration_Slot",
                    StringComparison.OrdinalIgnoreCase);
                bool isOldFormat = name.StartsWith($"{progressionEventId}_Decor_Item_",
                    StringComparison.OrdinalIgnoreCase);

                if (isNewFormat || isOldFormat)
                {
                    // Map old format name to new format for allowedSlots comparison
                    // Old: SP_RumorsRing2025_Decor_Item_25 → New: SP_RumorsRing2025_Decoration_Slot25
                    string canonicalName = name;
                    if (isOldFormat)
                    {
                        var numPart = name[(name.LastIndexOf('_') + 1)..];
                        canonicalName = $"{progressionEventId}_Decoration_Slot{numPart}";
                    }

                    if (allowedSlots.Count > 0 && !allowedSlots.Contains(canonicalName))
                        continue;

                    decoSprites.Add((
                        canonicalName, texName, // use canonical name for consistent slot ordering
                        (int)sprite.GetProperty("rectX").GetSingle(),
                        (int)sprite.GetProperty("rectY").GetSingle(),
                        (int)sprite.GetProperty("rectWidth").GetSingle(),
                        (int)sprite.GetProperty("rectHeight").GetSingle()
                    ));
                }
                else if (name.Equals($"{progressionEventId}_Set_Icon",
                    StringComparison.OrdinalIgnoreCase))
                {
                    iconTexture = texName;
                    iconX = (int)sprite.GetProperty("rectX").GetSingle();
                    iconY = (int)sprite.GetProperty("rectY").GetSingle();
                    iconW = (int)sprite.GetProperty("rectWidth").GetSingle();
                    iconH = (int)sprite.GetProperty("rectHeight").GetSingle();
                }
            }

            // Need BOTH decorations and icon for full extraction.
            // If only icon found (no decoration sprites), return false to trigger grid scan fallback.
            if (decoSprites.Count == 0) return false;

            // Sort decorations by events.json reward order (Track1 first, then Track2)
            var slotOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (mystery != null)
            {
                int orderIdx = 0;
                foreach (var level in mystery.SilverTier)
                    foreach (var r in level.Rewards)
                        if (r.Type == MysteryRewardType.Decoration && !string.IsNullOrEmpty(r.DecorationId))
                            slotOrder.TryAdd(r.DecorationId, orderIdx++);
                foreach (var level in mystery.GoldTier)
                    foreach (var r in level.Rewards)
                        if (r.Type == MysteryRewardType.Decoration && !string.IsNullOrEmpty(r.DecorationId))
                            slotOrder.TryAdd(r.DecorationId, orderIdx++);
            }

            // Filter to allowed slots only + sort by reward order
            var sortedDecos = decoSprites
                .Where(s => allowedSlots.Count == 0 || allowedSlots.Contains(s.Name))
                .OrderBy(s => slotOrder.TryGetValue(s.Name, out var o) ? o : ExtractSlotNumber(s.Name))
                .ToList();

            // Deduplicate by name
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            sortedDecos = sortedDecos.Where(s => seenNames.Add(s.Name)).ToList();

            // Crop each decoration from its SPECIFIC atlas page (textureName per sprite)
            var textureCache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

            foreach (var deco in sortedDecos)
            {
                var wikiName = FormatFileName(mysteryName + "Decoration", decoNum);

                // Reuse existing ONLY if it has optimization marker
                if (!string.IsNullOrEmpty(processedDir))
                {
                    var existingPath = Path.Combine(processedDir, wikiName);
                    if (File.Exists(existingPath))
                    {
                        var optSize = CheckOptMarker(existingPath);
                        if (optSize.HasValue)
                        {
                            result.Add(new DetectedDecorationFile
                            {
                                SourcePath = existingPath,
                                WikiFilename = wikiName,
                                Category = "Decoration",
                                Width = deco.W, Height = deco.H,
                                OptimizedSize = optSize
                            });
                            decoNum++;
                            continue;
                        }
                    }
                }

                // Crop from the SPECIFIC atlas page (textureName is per-sprite after extraction fix)
                var texPath = Path.Combine(exportDir, deco.TextureName + ".png");
                if (!File.Exists(texPath)) { decoNum++; continue; }

                if (!textureCache.TryGetValue(texPath, out var texBmp))
                {
                    var dec2 = new PngBitmapDecoder(new Uri(texPath),
                        BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    texBmp = dec2.Frames[0];
                    textureCache[texPath] = texBmp;
                }

                // Unity Y is bottom-up, WPF is top-down
                int texH = texBmp.PixelHeight;
                int cropY = texH - deco.Y - deco.H;
                if (cropY < 0) cropY = 0;

                int cropW = Math.Min(deco.W, texBmp.PixelWidth - deco.X);
                int cropH = Math.Min(deco.H, texH - cropY);
                if (cropW <= 0 || cropH <= 0) { decoNum++; continue; }

                var cropped = new CroppedBitmap(texBmp,
                    new System.Windows.Int32Rect(deco.X, cropY, cropW, cropH));

                // Pad to 256×256 canvas if needed
                BitmapSource saveBmp = cropped;
                if (cropW < 256 || cropH < 256)
                {
                    int canvasSize = 256;
                    var renderTarget = new RenderTargetBitmap(canvasSize, canvasSize, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawImage(cropped, new System.Windows.Rect(
                            (canvasSize - cropW) / 2.0, (canvasSize - cropH) / 2.0, cropW, cropH));
                    }
                    renderTarget.Render(dv);
                    saveBmp = renderTarget;
                }

                byte[] pngData;
                using (var ms = new MemoryStream())
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(saveBmp));
                    enc.Save(ms);
                    pngData = ms.ToArray();
                }

                var savePath = !string.IsNullOrEmpty(processedDir)
                    ? Path.Combine(processedDir, wikiName)
                    : Path.Combine(Path.GetTempPath(), wikiName);
                File.WriteAllBytes(savePath, pngData);

                result.Add(new DetectedDecorationFile
                {
                    SourcePath = savePath,
                    WikiFilename = wikiName,
                    Category = "Decoration",
                    Width = deco.W, Height = deco.H
                });
                decoNum++;
            }

            // Icon — crop from atlas using exact sprite metadata coordinates
            if (iconTexture != null && iconW > 0 && iconH > 0 && !result.Any(r => r.Category == "Icon"))
            {
                var iconWikiName = $"{pageNameUnderscore}_Icon.png";

                // Check for existing optimized
                if (!string.IsNullOrEmpty(processedDir))
                {
                    var existingIcon = Path.Combine(processedDir, iconWikiName);
                    if (File.Exists(existingIcon))
                    {
                        var optSize = CheckOptMarker(existingIcon);
                        if (optSize.HasValue)
                        {
                            result.Add(new DetectedDecorationFile
                            {
                                SourcePath = existingIcon,
                                WikiFilename = iconWikiName,
                                Category = "Icon",
                                OptimizedSize = optSize
                            });
                            return true;
                        }
                    }
                }

                var iconTexPath = Path.Combine(exportDir, iconTexture + ".png");
                if (File.Exists(iconTexPath))
                {
                    if (!textureCache.TryGetValue(iconTexPath, out var iconTexBmp))
                    {
                        var idec = new PngBitmapDecoder(new Uri(iconTexPath),
                            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        iconTexBmp = idec.Frames[0];
                    }

                    int texHeight = iconTexBmp.PixelHeight;
                    int cropIconY = texHeight - iconY - iconH;
                    if (cropIconY < 0) cropIconY = 0;

                    var iconCrop = new CroppedBitmap(iconTexBmp,
                        new System.Windows.Int32Rect(iconX, cropIconY,
                            Math.Min(iconW, iconTexBmp.PixelWidth - iconX),
                            Math.Min(iconH, texHeight - cropIconY)));

                    byte[] iconPng;
                    using (var ms = new MemoryStream())
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(iconCrop));
                        enc.Save(ms);
                        iconPng = ms.ToArray();
                    }

                    var iconSavePath = !string.IsNullOrEmpty(processedDir)
                        ? Path.Combine(processedDir, iconWikiName)
                        : Path.Combine(Path.GetTempPath(), iconWikiName);
                    File.WriteAllBytes(iconSavePath, iconPng);

                    result.Add(new DetectedDecorationFile
                    {
                        SourcePath = iconSavePath,
                        WikiFilename = iconWikiName,
                        Category = "Icon"
                    });
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ExtractDecorationsFromSpriteMetadata failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reorders decoration tiles to match existing wiki images by pixel comparison.
    /// Downloads wiki decoration images and finds the best tile match for each level.
    /// If wiki images don't exist, returns tiles in original scan order.
    /// </summary>
    private static List<(BitmapSource Bmp, byte[] PngData, bool IsIcon)> AutoOrderByWikiMatch(
        List<(BitmapSource Bmp, byte[] PngData, bool IsIcon)> tiles,
        string mysteryName, int expectedCount, bool isPet)
    {
        if (tiles.Count <= 1) return tiles;

        try
        {
            var http = new HttpClient();
            var orderedResult = new (BitmapSource Bmp, byte[] PngData, bool IsIcon)?[tiles.Count];
            var usedTiles = new HashSet<int>();
            int matched = 0;

            for (int level = isPet ? 0 : 1; level <= tiles.Count + (isPet ? 0 : 1) - 1; level++)
            {
                var wikiFilename = FormatFileName(mysteryName + "Decoration", level);

                // Fetch wiki image info
                var apiUrl = $"https://merge-mansion.fandom.com/api.php?action=query&titles=File:{Uri.EscapeDataString(wikiFilename)}" +
                    "&prop=imageinfo&iiprop=url&format=json";

                string? imageUrl = null;
                try
                {
                    var json = http.GetStringAsync(apiUrl).GetAwaiter().GetResult();
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
                    {
                        if (page.Value.TryGetProperty("imageinfo", out var ii) && ii.GetArrayLength() > 0)
                            imageUrl = ii[0].GetProperty("url").GetString();
                    }
                }
                catch { continue; }

                if (imageUrl == null) continue;

                // Download wiki image
                byte[] wikiBytes;
                try
                {
                    var sep = imageUrl.Contains('?') ? "&" : "?";
                    wikiBytes = http.GetByteArrayAsync(imageUrl + sep + "format=original").GetAwaiter().GetResult();
                }
                catch { continue; }

                // Decode wiki image to get pixels
                BitmapSource wikiBmp;
                try
                {
                    var ms = new MemoryStream(wikiBytes);
                    var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    wikiBmp = dec.Frames[0];
                    if (wikiBmp.Format != System.Windows.Media.PixelFormats.Bgra32)
                        wikiBmp = new FormatConvertedBitmap(wikiBmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                }
                catch { continue; }

                // Compare wiki image with each unmatched tile
                int bestTileIdx = -1;
                double bestSimilarity = 0;

                for (int ti = 0; ti < tiles.Count; ti++)
                {
                    if (usedTiles.Contains(ti)) continue;

                    var tileBmp = tiles[ti].Bmp;
                    if (tileBmp.PixelWidth != wikiBmp.PixelWidth || tileBmp.PixelHeight != wikiBmp.PixelHeight)
                        continue;

                    // Quick pixel comparison (sample ~100 pixels)
                    int w = tileBmp.PixelWidth, h = tileBmp.PixelHeight;
                    var tilePixels = new byte[w * h * 4];
                    var wikiPixels = new byte[w * h * 4];

                    var tileBmp32 = tileBmp.Format == System.Windows.Media.PixelFormats.Bgra32
                        ? tileBmp : new FormatConvertedBitmap(tileBmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    tileBmp32.CopyPixels(tilePixels, w * 4, 0);
                    wikiBmp.CopyPixels(wikiPixels, w * 4, 0);

                    int matchCount = 0, sampleCount = 0;
                    int step = Math.Max(1, (w * h) / 200); // sample ~200 pixels
                    for (int pi = 0; pi < w * h; pi += step)
                    {
                        sampleCount++;
                        int off = pi * 4;
                        // Compare RGB (ignore alpha differences from compression)
                        if (Math.Abs(tilePixels[off] - wikiPixels[off]) < 20 &&
                            Math.Abs(tilePixels[off + 1] - wikiPixels[off + 1]) < 20 &&
                            Math.Abs(tilePixels[off + 2] - wikiPixels[off + 2]) < 20)
                            matchCount++;
                    }

                    double similarity = sampleCount > 0 ? (double)matchCount / sampleCount : 0;
                    if (similarity > bestSimilarity)
                    {
                        bestSimilarity = similarity;
                        bestTileIdx = ti;
                    }
                }

                if (bestTileIdx >= 0 && bestSimilarity > 0.7) // 70% threshold
                {
                    int resultIdx = level - (isPet ? 0 : 1);
                    if (resultIdx >= 0 && resultIdx < orderedResult.Length)
                    {
                        orderedResult[resultIdx] = tiles[bestTileIdx];
                        usedTiles.Add(bestTileIdx);
                        matched++;
                    }
                }
            }

            if (matched == 0)
                return tiles; // No wiki matches — return original order

            // Fill remaining slots with unmatched tiles
            int fillIdx = 0;
            for (int i = 0; i < orderedResult.Length; i++)
            {
                if (orderedResult[i] == null)
                {
                    while (fillIdx < tiles.Count && usedTiles.Contains(fillIdx))
                        fillIdx++;
                    if (fillIdx < tiles.Count)
                    {
                        orderedResult[i] = tiles[fillIdx];
                        fillIdx++;
                    }
                }
            }

            AppLogger.Info($"AutoOrderByWikiMatch: matched {matched}/{tiles.Count} decorations");
            return orderedResult.Where(t => t != null).Select(t => t!.Value).ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"AutoOrderByWikiMatch failed: {ex.Message}");
            return tiles;
        }
    }

    private static int ExtractSlotNumber(string spriteName)
    {
        var match = Regex.Match(spriteName, @"Slot(\d+)$");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    /// <summary>
    /// Detects and prepares mystery image files from the Export PNGs directory.
    /// Slices decoration atlases into individual tiles with correct wiki naming.
    /// </summary>
    public static List<DetectedDecorationFile> DetectDecorationFiles(
        string exportDir, string progressionEventId, string mysteryName,
        bool isPet = false, MysteryEvent? mystery = null)
    {
        var result = new List<DetectedDecorationFile>();
        if (!Directory.Exists(exportDir)) return result;

        var pageNameUnderscore = mysteryName.Replace(' ', '_');
        var fileBase = FormatFileName(mysteryName, 0, suppressLevel: true).Replace(".png", "");

        // For pet mysteries, the asset prefix may differ from progressionEventId
        // (e.g., SP_AmyPet2025 → files use SP_AmyTheCat2025)
        // Detect alternative prefix by scanning for _Decor_Pet files
        var altPrefix = progressionEventId;
        if (isPet)
        {
            var decoPatterns = Directory.GetFiles(exportDir, "*_Decor_Pet.png")
                .Select(f => Path.GetFileNameWithoutExtension(f).Replace("_Decor_Pet", ""))
                .Where(p => !string.Equals(p, progressionEventId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Find the one that shares a keyword with our event ID
            // SP_AmyPet2025 → keyword "Amy" → matches SP_AmyTheCat2025
            var eventSuffix = progressionEventId.Replace("SP_", "").Replace("Pet", "")
                .TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            var match = decoPatterns.FirstOrDefault(p =>
                p.Contains(eventSuffix, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                altPrefix = match;
                AppLogger.Info($"DetectDecorationFiles: alt prefix '{altPrefix}' for '{progressionEventId}'");
            }
        }

        // Resolve Processed Images dir for copying non-atlas files
        string? processedImagesDir = null;
        var versionDir0 = Path.GetDirectoryName(exportDir);
        var workspaceDir0 = !string.IsNullOrEmpty(versionDir0) ? Path.GetDirectoryName(versionDir0) : null;
        if (!string.IsNullOrEmpty(workspaceDir0))
        {
            processedImagesDir = Path.Combine(workspaceDir0, "Processed Images");
            if (!Directory.Exists(processedImagesDir))
                Directory.CreateDirectory(processedImagesDir);
        }

        // ── Wallpaper: different patterns for Standard vs Pet ──
        var wallpaperPatterns = isPet
            ? new[] {
                $"PopupSharedArt_{progressionEventId}*.png",
                $"Popup_Shared_Art_{progressionEventId}*.png",
                $"Popup_Header_Art_{progressionEventId}*.png",
                $"Popup_Progression_Art_{progressionEventId}*.png",
                $"ProgressionPopupArt_{progressionEventId}*.png",
                // Alt prefix for pet mysteries where asset name differs from event ID
                $"PopupSharedArt_{altPrefix}*.png",
                $"Popup_Shared_Art_{altPrefix}*.png",
                $"Popup_Header_Art_{altPrefix}*.png",
                $"Popup_Header_background_{altPrefix}*.png",
            }
            : new[] {
                $"ProgressionPopupArt_{progressionEventId}*.png",
                $"Popup_Progression_Art_{progressionEventId}*.png",
                $"Popup_Header_{progressionEventId}*.png",
            };
        // Collect all wallpaper candidates, pick the largest by resolution
        var wallpaperCandidates = new List<string>();
        foreach (var pattern in wallpaperPatterns)
            wallpaperCandidates.AddRange(Directory.GetFiles(exportDir, pattern));

        if (wallpaperCandidates.Count > 0)
        {
            string bestFile;
            if (isPet)
            {
                // Pet: pick closest to 844×760
                const int targetW = 844, targetH = 760;
                bestFile = wallpaperCandidates[0];
                double bestDist = double.MaxValue;
                foreach (var file in wallpaperCandidates)
                {
                    try
                    {
                        var dec = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.None);
                        int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
                        double dist = Math.Sqrt(Math.Pow(w - targetW, 2) + Math.Pow(h - targetH, 2));
                        if (dist < bestDist) { bestDist = dist; bestFile = file; }
                    }
                    catch { }
                }
            }
            else
            {
                // Standard: pick closest to 1440×760
                const int targetW = 1440, targetH = 760;
                bestFile = wallpaperCandidates[0];
                double bestDist = double.MaxValue;
                foreach (var file in wallpaperCandidates)
                {
                    try
                    {
                        var dec = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.None);
                        int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
                        double dist = Math.Sqrt(Math.Pow(w - targetW, 2) + Math.Pow(h - targetH, 2));
                        if (dist < bestDist) { bestDist = dist; bestFile = file; }
                    }
                    catch { }
                }
            }

            var wikiName = $"{pageNameUnderscore}.png";
            var finalPath = CopyToProcessed(bestFile, wikiName, processedImagesDir);
            result.Add(new DetectedDecorationFile
            {
                SourcePath = finalPath,
                WikiFilename = wikiName,
                Category = "Wallpaper",
                OptimizedSize = CheckOptMarker(finalPath)
            });
        }

        // ── Badge: multiple naming conventions + alt prefix ──
        var badgePatterns = new List<string> {
            $"MainHubBadgeArt_{progressionEventId}*.png",
            $"MainHub_Badge_{progressionEventId}*.png"
        };
        if (altPrefix != progressionEventId)
        {
            badgePatterns.Add($"MainHubBadgeArt_{altPrefix}*.png");
            badgePatterns.Add($"MainHub_Badge_{altPrefix}*.png");
        }
        foreach (var pattern in badgePatterns)
            foreach (var file in Directory.GetFiles(exportDir, pattern))
            {
                var wikiName = FormatFileName(mysteryName, 1);
                var finalPath = CopyToProcessed(file, wikiName, processedImagesDir);
                result.Add(new DetectedDecorationFile
                {
                    SourcePath = finalPath,
                    WikiFilename = wikiName,
                    Category = "Badge",
                    OptimizedSize = CheckOptMarker(finalPath)
                });
            }

        // ── Decorations: use sprite metadata from atlas_data.json for precise extraction ──
        int decoNum = isPet ? 0 : 1;

        // Try sprite-based extraction first (deterministic from Unity metadata)
        var spriteDecorations = ExtractDecorationsFromSpriteMetadata(
            exportDir, progressionEventId, mysteryName, pageNameUnderscore,
            processedImagesDir, isPet, ref decoNum, result, mystery);

        // Fallback: atlas slicing (only if no sprites found)
        var atlasFiles = spriteDecorations
            ? new List<string>() // skip atlas slicing
            : Directory.GetFiles(exportDir, $"*{progressionEventId}*Decorations*Atlas*.png")
                .OrderBy(f => f).ToList();

        if (atlasFiles.Count > 0)
        {
            // Resolve output dir: Processed Images (workspace root) → Export - Items (fallback)
            string? outputDir = null;
            // Try Processed Images in workspace root (2 levels up from Export - PNGs)
            var versionDir = Path.GetDirectoryName(exportDir); // e.g., 26.02.02/
            var workspaceDir = !string.IsNullOrEmpty(versionDir) ? Path.GetDirectoryName(versionDir) : null;
            if (!string.IsNullOrEmpty(workspaceDir))
            {
                var processedDir = Path.Combine(workspaceDir, "Processed Images");
                if (!Directory.Exists(processedDir))
                    Directory.CreateDirectory(processedDir);
                outputDir = processedDir;
            }
            // Fallback: Export - Items
            if (string.IsNullOrEmpty(outputDir) && !string.IsNullOrEmpty(versionDir))
            {
                var itemsDir = Path.Combine(versionDir, "Export - Items");
                if (!Directory.Exists(itemsDir))
                    Directory.CreateDirectory(itemsDir);
                outputDir = itemsDir;
            }

            // Pre-compute expected filenames for this atlas
            // Check if optimized versions already exist before slicing
            var expectedDecoFiles = new List<(string WikiName, string Category, int DecoIdx)>();
            {
                int tempDecoNum = decoNum;
                // Estimate tile count from atlas dimensions
                foreach (var atlas in atlasFiles)
                {
                    try
                    {
                        var dec = new PngBitmapDecoder(new Uri(atlas), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
                        int cols = w / 256, rows = h / 256;
                        for (int r = 0; r < rows; r++)
                            for (int c = 0; c < cols; c++)
                                expectedDecoFiles.Add((FormatFileName(mysteryName + "Decoration", tempDecoNum++), "Decoration", tempDecoNum - 1));
                        // Last tile might be icon → add icon entry
                        expectedDecoFiles.Add(($"{pageNameUnderscore}_Icon.png", "Icon", -1));
                    }
                    catch { /* ignore */ }
                }
            }

            // Check for existing optimized versions
            var existingOptimized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(outputDir))
            {
                foreach (var (wikiName, _, _) in expectedDecoFiles)
                {
                    var existingPath = Path.Combine(outputDir, wikiName);
                    if (File.Exists(existingPath))
                    {
                        try
                        {
                            var bytes = File.ReadAllBytes(existingPath);
                            if (Views.OptimizationWindow.HasOptMarker(bytes))
                                existingOptimized[wikiName] = existingPath;
                        }
                        catch { /* ignore */ }
                    }
                }
            }

            // Slice atlases — use wiki filenames, skip if optimized version exists
            foreach (var atlas in atlasFiles)
            {
                // Copy original atlas to output dir for reference
                if (!string.IsNullOrEmpty(outputDir))
                {
                    try
                    {
                        var destAtlas = Path.Combine(outputDir, Path.GetFileName(atlas));
                        if (!File.Exists(destAtlas))
                            File.Copy(atlas, destAtlas);
                    }
                    catch { /* ignore */ }
                }

                // Max decorations: Standard=5, Pet=3
                int maxDecorations = isPet ? 3 : 5;
                bool iconFound = result.Any(r => r.Category == "Icon");

                var tiles = SliceDecorationAtlas(atlas);
                foreach (var (type, pngData) in tiles)
                {
                    string wikiName;
                    string category;
                    int tileW = 0, tileH = 0;

                    if (type == AtlasTileType.Icon)
                    {
                        if (iconFound) continue; // already have an icon
                        wikiName = $"{pageNameUnderscore}_Icon.png";
                        category = "Icon";
                        iconFound = true;
                    }
                    else
                    {
                        // Stop adding decorations after limit
                        int currentDecoCount = result.Count(r => r.Category == "Decoration");
                        if (currentDecoCount >= maxDecorations) continue;

                        wikiName = FormatFileName(mysteryName + "Decoration", decoNum);
                        category = "Decoration";
                        tileW = 256; tileH = 256;
                        decoNum++;
                    }

                    // If optimized version exists → use it, don't write anything
                    if (existingOptimized.TryGetValue(wikiName, out var existingPath))
                    {
                        result.Add(new DetectedDecorationFile
                        {
                            SourcePath = existingPath,
                            WikiFilename = wikiName,
                            Category = category,
                            Width = tileW, Height = tileH,
                            OptimizedSize = new FileInfo(existingPath).Length
                        });
                    }
                    else
                    {
                        // Write sliced tile to disk with wiki filename
                        var finalPath = !string.IsNullOrEmpty(outputDir)
                            ? Path.Combine(outputDir, wikiName)
                            : Path.Combine(Path.GetTempPath(), wikiName);

                        File.WriteAllBytes(finalPath, pngData);

                        result.Add(new DetectedDecorationFile
                        {
                            SourcePath = finalPath,
                            WikiFilename = wikiName,
                            Category = category,
                            Width = tileW, Height = tileH
                        });
                    }
                }
            }
        }

        // Source B: Standalone decoration PNGs (Pet mysteries: SP_*_Decor_*.png)
        var standalonDecos = Directory.GetFiles(exportDir, $"{progressionEventId}_Decor_*.png")
            .OrderBy(f => f).ToList();
        // Alt prefix fallback for pet mysteries
        if (standalonDecos.Count == 0 && altPrefix != progressionEventId)
            standalonDecos = Directory.GetFiles(exportDir, $"{altPrefix}_Decor_*.png")
                .OrderBy(f => f).ToList();

        foreach (var file in standalonDecos)
        {
            var decoWikiName = FormatFileName(mysteryName + "Decoration", decoNum);
            var finalPath = CopyToProcessed(file, decoWikiName, processedImagesDir);
            result.Add(new DetectedDecorationFile
            {
                SourcePath = finalPath,
                WikiFilename = decoWikiName,
                Category = "Decoration",
                OptimizedSize = CheckOptMarker(finalPath)
            });
            decoNum++;
        }

        // ── Icon: standalone icon files (Pet mysteries) ──
        // SP_*_Set_Icon*.png (not _Set_Icon_Badge)
        var iconFiles = Directory.GetFiles(exportDir, $"{progressionEventId}_Set_Icon*.png")
            .Where(f => !Path.GetFileName(f).Contains("Badge", StringComparison.OrdinalIgnoreCase)).ToList();
        if (iconFiles.Count == 0 && altPrefix != progressionEventId)
            iconFiles = Directory.GetFiles(exportDir, $"{altPrefix}_Set_Icon*.png")
                .Where(f => !Path.GetFileName(f).Contains("Badge", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var file in iconFiles)
        {
            if (!result.Any(r => r.Category == "Icon"))
            {
                var iconWikiName = $"{pageNameUnderscore}_Icon.png";
                var finalPath = CopyToProcessed(file, iconWikiName, processedImagesDir);
                result.Add(new DetectedDecorationFile
                {
                    SourcePath = finalPath,
                    WikiFilename = iconWikiName,
                    Category = "Icon",
                    OptimizedSize = CheckOptMarker(finalPath)
                });
            }
        }

        // ── Event item sprite sheet: SP_*_CollectableItems.png ──
        foreach (var file in Directory.GetFiles(exportDir, $"{progressionEventId}*CollectableItems*.png"))
        {
            result.Add(new DetectedDecorationFile
            {
                SourcePath = file,
                WikiFilename = "",
                Category = "EventItem"
            });
        }

        return result;
    }

    /// <summary>
    /// Generates a preview of what wiki updates will be performed.
    /// Returns a human-readable summary for the confirmation dialog.
    /// </summary>
    public static async Task<string> PreviewWikiUpdatesAsync(MysteryEvent mystery)
    {
        var sb = new StringBuilder();
        var pageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;

        sb.AppendLine($"Mystery: {mystery.Name}");
        sb.AppendLine($"Page title: {pageTitle}");
        sb.AppendLine();

        // 1. Main page
        sb.AppendLine("1. Merge Mansion Wiki (main page):");
        var mainContent = await FetchPageContentAsync("Merge Mansion Wiki");
        if (mainContent?.Contains(mystery.Name, StringComparison.OrdinalIgnoreCase) == true)
            sb.AppendLine("   → Already listed (no change)");
        else
            sb.AppendLine($"   → Add {{{{Item/Group|{pageTitle}}}}} to {mystery.StartDate?.Year ?? DateTime.Now.Year} row");

        // 2. Mystery table
        sb.AppendLine("2. Mystery page (table):");
        var mysteryContent = await FetchPageContentAsync("Mystery");
        if (mysteryContent?.Contains(pageTitle, StringComparison.OrdinalIgnoreCase) == true)
            sb.AppendLine("   → Already listed (no change)");
        else
            sb.AppendLine($"   → Add row: {mystery.Name}, {mystery.EventItemName}, 21 d");

        // 3. Module:Datatable/Various
        sb.AppendLine("3. Module:Datatable/Various (p.mysteries):");
        var moduleContent = await FetchPageContentAsync("Module:Datatable/Various");
        if (moduleContent?.Contains($"\"{mystery.Name}\"", StringComparison.OrdinalIgnoreCase) == true)
            sb.AppendLine("   → Already listed (no change)");
        else
            sb.AppendLine($"   → Add entry: {{ name = \"{mystery.Name}\", startDate = \"{mystery.StartDate?.ToString("dd.MM.yyyy")}\" }}");

        return sb.ToString();
    }
}
