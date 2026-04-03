using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

// ── Models ──────────────────────────────────────────────────────────

public class ClueCollectionCase
{
    public int Index { get; set; } // 1-based
    public string ConfigKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int DurationDays { get; set; } = 63; // default 63 (3 mysteries period)
    public List<ClueCollectionSet> Sets { get; set; } = new();
    public List<string> GrandRewards { get; set; } = new(); // formatted wiki markup
    public bool ExistsOnWiki { get; set; }
}

public class ClueCollectionSet
{
    public int Index { get; set; } // 1-based position within case
    public int FileNumber { get; set; } // actual set number in filenames (from ConfigKey, e.g. Set14 = 14)
    public string ConfigKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int CardCount { get; set; }
    public List<string> Rewards { get; set; } = new(); // formatted wiki markup
}

// ── Service ─────────────────────────────────────────────────────────

public class ClueCollectionService
{
    public List<ClueCollectionCase> Cases { get; private set; } = new();

    public async Task LoadAsync(string cardCollectionPath)
    {
        Cases.Clear();
        await using var stream = File.OpenRead(cardCollectionPath);
        var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("Data", out var data)) return;
        if (!data.TryGetProperty("Events", out var events)) return;
        if (!data.TryGetProperty("CardSets", out var cardSetsArr)) return;

        // Build set lookup: ConfigKey → (DisplayName, CardCount, Rewards)
        var setLookup = new Dictionary<string, (string Name, int Cards, List<string> Rewards)>(StringComparer.Ordinal);
        foreach (var cs in cardSetsArr.EnumerateArray())
        {
            var key = GetStr(cs, "ConfigKey");
            var name = GetStr(cs, "DisplayName");
            int cardCount = 0;
            if (cs.TryGetProperty("CardsIds", out var cardsArr) && cardsArr.ValueKind == JsonValueKind.Array)
                cardCount = cardsArr.GetArrayLength();
            var rewards = ParseRewardsRaw(cs);
            if (!string.IsNullOrEmpty(key))
                setLookup[key] = (name, cardCount, rewards);
        }

        // Parse events
        int caseIndex = 1;
        foreach (var ev in events.EnumerateArray())
        {
            var configKey = GetStr(ev, "ConfigKey");
            var displayName = GetStr(ev, "DisplayName");
            // Strip "CASE N: " prefix if present
            var cleanName = Regex.Replace(displayName, @"^CASE\s+\d+:\s*", "", RegexOptions.IgnoreCase);

            var caseObj = new ClueCollectionCase
            {
                Index = caseIndex,
                ConfigKey = configKey,
                DisplayName = cleanName
            };

            // Parse sets
            if (ev.TryGetProperty("CardSetIds", out var setIds) && setIds.ValueKind == JsonValueKind.Array)
            {
                int setIdx = 1;
                foreach (var setId in setIds.EnumerateArray())
                {
                    var sid = setId.GetString() ?? "";
                    setLookup.TryGetValue(sid, out var setInfo);
                    // Extract file number from ConfigKey (e.g. "TCE_Case02_Set14" → 14)
                    int fileNum = setIdx;
                    var setNumMatch = Regex.Match(sid, @"Set(\d+)$");
                    if (setNumMatch.Success) fileNum = int.Parse(setNumMatch.Groups[1].Value);

                    caseObj.Sets.Add(new ClueCollectionSet
                    {
                        Index = setIdx,
                        FileNumber = fileNum,
                        ConfigKey = sid,
                        DisplayName = setInfo.Name ?? sid,
                        CardCount = setInfo.Cards > 0 ? setInfo.Cards : 9,
                        Rewards = setInfo.Rewards
                    });
                    setIdx++;
                }
            }

            // Parse grand rewards
            caseObj.GrandRewards = ParseRewardsRaw(ev);

            // Parse duration from ActivableParams if available
            if (ev.TryGetProperty("ActivableParams", out var actParams))
            {
                var durStr = GetStr(actParams, "Duration");
                if (!string.IsNullOrEmpty(durStr))
                {
                    // Duration format: "Xd Yh Zmin Ws" — extract days
                    var dayMatch = Regex.Match(durStr, @"(\d+)d\b");
                    if (dayMatch.Success) caseObj.DurationDays = int.Parse(dayMatch.Groups[1].Value);
                }
            }

            Cases.Add(caseObj);
            caseIndex++;
        }
    }

    // ── Wiki detection ──────────────────────────────────────────────

    public async Task DetectExistingOnWikiAsync()
    {
        try
        {
            var moduleContent = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Various");
            if (string.IsNullOrEmpty(moduleContent)) return;

            foreach (var c in Cases)
            {
                // Check if case name exists in p.clueCollections
                c.ExistsOnWiki = moduleContent.Contains($"\"{c.DisplayName}\"", StringComparison.OrdinalIgnoreCase)
                    || moduleContent.Contains($"[{c.Index}]", StringComparison.Ordinal)
                       && moduleContent.Contains(c.DisplayName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
    }

    // ── Wiki content generation ─────────────────────────────────────

    /// <summary>Generates Lua entry for p.clueCollections in Module:Datatable/Various.</summary>
    public static string GenerateModuleEntry(ClueCollectionCase caseObj,
        Dictionary<(int Case, int Set), int>? groupImages = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\t[{caseObj.Index}] = {{name = \"{EscLua(caseObj.DisplayName)}\",");
        sb.AppendLine("\t\tclueSets ={ ");
        foreach (var set in caseObj.Sets)
        {
            var parts = new List<string> { $"name = \"{EscLua(set.DisplayName)}\"" };
            var rewardStr = set.Rewards.Count > 0 ? string.Join("<br>", set.Rewards) : "";
            if (!string.IsNullOrEmpty(rewardStr))
                parts.Add($"reward = \"{EscLua(rewardStr)}\"");
            if (groupImages != null && groupImages.TryGetValue((caseObj.Index, set.Index), out int gi))
                parts.Add($"groupImage = {gi}");
            sb.AppendLine($"\t\t\t[{set.Index}]  = {{{string.Join(", ", parts)}}},");
        }
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t},");
        return sb.ToString();
    }

    /// <summary>
    /// Patches an existing module entry by adding missing reward fields to sets.
    /// Preserves existing set names and other data — only inserts ", reward = ..." where missing.
    /// Works strictly within the p.clueCollections section and the specific case block.
    /// </summary>
    public static string PatchModuleEntryRewards(string moduleContent, ClueCollectionCase caseObj,
        Dictionary<(int Case, int Set), int>? groupImages = null)
    {
        // Find p.clueCollections section boundaries
        int sectionStart = moduleContent.IndexOf("p.clueCollections = {", StringComparison.Ordinal);
        if (sectionStart < 0) return moduleContent;

        // Find the specific case entry: [N] = {name = "CaseName" or "CASE N: CaseName"
        var patterns = new[]
        {
            $"[{caseObj.Index}] = {{name = \"{caseObj.DisplayName}\"",
            $"[{caseObj.Index}] = {{name = \"{EscLua(caseObj.DisplayName)}\"",
            $"[{caseObj.Index}] = {{name = \"CASE {caseObj.Index}: {caseObj.DisplayName}\"",
        };
        int caseStart = -1;
        foreach (var pat in patterns)
        {
            caseStart = moduleContent.IndexOf(pat, sectionStart, StringComparison.OrdinalIgnoreCase);
            if (caseStart >= 0) break;
        }
        if (caseStart < 0) return moduleContent;

        // Find end of this case block — brace matching from the opening {
        int openBrace = moduleContent.IndexOf('{', caseStart);
        if (openBrace < 0) return moduleContent;
        int depth = 1;
        int pos = openBrace + 1;
        while (pos < moduleContent.Length && depth > 0)
        {
            if (moduleContent[pos] == '{') depth++;
            else if (moduleContent[pos] == '}') depth--;
            pos++;
        }
        int caseEnd = pos; // one past the closing }

        // Extract case block, patch it, put it back
        string caseBlock = moduleContent[caseStart..caseEnd];

        // Process sets in REVERSE order so replacements don't shift positions of unprocessed sets
        foreach (var set in caseObj.Sets.OrderByDescending(s => s.Index))
        {
            int clueSetsPos = caseBlock.IndexOf("clueSets", StringComparison.Ordinal);
            if (clueSetsPos < 0) continue;

            string setPattern = $"[{set.Index}]";
            int setPos = caseBlock.IndexOf(setPattern, clueSetsPos, StringComparison.Ordinal);
            if (setPos < 0) continue;

            int lineStart = caseBlock.LastIndexOf('\n', setPos);
            if (lineStart < 0) lineStart = 0; else lineStart++;
            int lineEnd = caseBlock.IndexOf('\n', setPos);
            if (lineEnd < 0) lineEnd = caseBlock.Length;
            string originalLine = caseBlock[lineStart..lineEnd];

            // Build the correct line from scratch
            var fields = new List<string> { $"name = \"{EscLua(set.DisplayName)}\"" };
            if (set.Rewards.Count > 0)
                fields.Add($"reward = \"{EscLua(string.Join("<br>", set.Rewards))}\"");
            if (groupImages != null && groupImages.TryGetValue((caseObj.Index, set.Index), out int gi))
                fields.Add($"groupImage = {gi}");
            else
            {
                // Preserve existing groupImage if present
                var giMatch = Regex.Match(originalLine, @"groupImage\s*=\s*(\d+)");
                if (giMatch.Success) fields.Add($"groupImage = {giMatch.Groups[1].Value}");
            }

            // Reconstruct the line with correct indentation
            string indent = "";
            for (int c = 0; c < originalLine.Length && (originalLine[c] == '\t' || originalLine[c] == ' '); c++)
                indent += originalLine[c];
            string newLine = $"{indent}[{set.Index}]  = {{{string.Join(", ", fields)}}},";

            if (newLine != originalLine.TrimEnd(',') + "," && newLine != originalLine)
                caseBlock = caseBlock[..lineStart] + newLine + caseBlock[lineEnd..];
        }

        return moduleContent[..caseStart] + caseBlock + moduleContent[caseEnd..];
    }

    /// <summary>Generates a History table row for the wiki page.</summary>
    public static string GenerateHistoryRow(ClueCollectionCase caseObj)
    {
        int totalClues = caseObj.Sets.Sum(s => s.CardCount);
        string grandRewardStr = string.Join("<br>", caseObj.GrandRewards);

        var sb = new StringBuilder();
        sb.AppendLine("|-");
        sb.AppendLine($"| {caseObj.Index}");
        sb.AppendLine($"| {{{{#Invoke:Various|GetClueCollectionName|{caseObj.Index}}}}}");
        sb.AppendLine($"| {caseObj.Sets.Count}");
        sb.AppendLine($"| {totalClues}");
        sb.AppendLine($"| {{{{Time}}}} {caseObj.DurationDays} days");
        sb.AppendLine($"| {grandRewardStr}");
        return sb.ToString();
    }

    /// <summary>
    /// Generates the full History table with rowspan merging for identical columns.
    /// Groups consecutive cases with same (SetsCount, TotalClues, Duration, GrandReward).
    /// </summary>
    public static string GenerateMergedHistoryTable(List<ClueCollectionCase> cases)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{| class = \"article-table\"");
        sb.AppendLine("|-");
        sb.AppendLine("! #");
        sb.AppendLine("! Name");
        sb.AppendLine("! Clue Sets");
        sb.AppendLine("! Total clues");
        sb.AppendLine("! Duration");
        sb.AppendLine("! Grand Reward");

        // Build row data — DurationDays from parsed data (default 63)
        var rows = new List<(int Index, string Name, int Sets, int Clues, int Duration, string Reward)>();
        foreach (var c in cases)
        {
            int totalClues = c.Sets.Sum(s => s.CardCount);
            string reward = string.Join("<br>", c.GrandRewards);
            rows.Add((c.Index, c.DisplayName, c.Sets.Count, totalClues, c.DurationDays, reward));
        }

        // Group consecutive rows with identical (Sets, Clues, Duration, Reward)
        int i = 0;
        while (i < rows.Count)
        {
            var current = rows[i];
            int spanStart = i;

            // Find how many consecutive rows share the same mergeable columns
            while (i + 1 < rows.Count &&
                   rows[i + 1].Sets == current.Sets &&
                   rows[i + 1].Clues == current.Clues &&
                   rows[i + 1].Duration == current.Duration &&
                   rows[i + 1].Reward == current.Reward)
                i++;

            int spanCount = i - spanStart + 1;

            // Emit rows
            for (int j = spanStart; j <= i; j++)
            {
                var row = rows[j];
                sb.AppendLine("|-");
                sb.AppendLine($"| {row.Index}");
                sb.AppendLine($"| {{{{#Invoke:Various|GetClueCollectionName|{row.Index}}}}}");

                if (j == spanStart) // First row in span: emit cells (with rowspan if needed)
                {
                    string rs = spanCount > 1 ? $" rowspan=\"{spanCount}\" |" : "";
                    sb.AppendLine($"|{rs} {row.Sets}");
                    sb.AppendLine($"|{rs} {row.Clues}");
                    sb.AppendLine($"|{rs} {{{{Time}}}} {row.Duration} days");
                    sb.AppendLine($"|{rs} {row.Reward}");
                }
                // Merged rows: skip cells (wiki auto-fills from rowspan)
            }

            i++;
        }

        sb.AppendLine("|}");
        return sb.ToString();
    }

    /// <summary>
    /// For each set, finds which card image is identical to the CategoryPhoto (group image).
    /// Returns dict: (caseIndex, setIndex) → cardNumber (1-based).
    /// </summary>
    public static Dictionary<(int Case, int Set), int> DetectGroupImages(string exportDir, List<ClueCollectionCase> cases)
    {
        var result = new Dictionary<(int, int), int>();
        foreach (var c in cases)
        {
            foreach (var set in c.Sets)
            {
                string catFile = Path.Combine(exportDir, $"TCE_Case{c.Index:D2}_Set{set.FileNumber:D2}_CategoryPhoto.png");
                if (!File.Exists(catFile)) continue;
                var catBytes = File.ReadAllBytes(catFile);

                int bestCard = -1;
                int bestMatchingBytes = -1;

                for (int card = 1; card <= set.CardCount; card++)
                {
                    string cardFile = Path.Combine(exportDir, $"TCE_Case{c.Index:D2}_Set{set.FileNumber:D2}_{card:D2}.png");
                    if (!File.Exists(cardFile)) continue;
                    var cardBytes = File.ReadAllBytes(cardFile);

                    // Exact match — best possible
                    if (catBytes.Length == cardBytes.Length && catBytes.AsSpan().SequenceEqual(cardBytes))
                    {
                        bestCard = card;
                        break;
                    }

                    // Count matching bytes in stream (compare up to min length)
                    int minLen = Math.Min(catBytes.Length, cardBytes.Length);
                    int matching = 0;
                    for (int b = 0; b < minLen; b++)
                        if (catBytes[b] == cardBytes[b]) matching++;

                    if (matching > bestMatchingBytes)
                    {
                        bestMatchingBytes = matching;
                        bestCard = card;
                    }
                }

                if (bestCard > 0)
                    result[(c.Index, set.Index)] = bestCard;
            }
        }
        return result;
    }

    // ── Image helpers ───────────────────────────────────────────────

    /// <summary>Returns expected image filenames for a case.</summary>
    public static List<string> GetExpectedImageFiles(ClueCollectionCase caseObj)
    {
        var files = new List<string>();
        int caseNum = caseObj.Index;
        foreach (var set in caseObj.Sets)
        {
            for (int card = 1; card <= set.CardCount; card++)
                // Wiki uses sequential Index (no gaps), local Export uses FileNumber (from ConfigKey)
                files.Add($"TCE_Case{caseNum:D2}_Set{set.Index:D2}_{card:D2}.png");
        }
        return files;
    }

    /// <summary>
    /// Finds existing image files in export directory.
    /// Returns (WikiFileName, LocalFullPath, Width, Height) — WikiFileName uses sequential Index, local uses FileNumber.
    /// </summary>
    public static List<(string FileName, string FullPath, int Width, int Height)> FindExistingImages(
        string exportDir, ClueCollectionCase caseObj)
    {
        var result = new List<(string, string, int, int)>();
        foreach (var set in caseObj.Sets)
        {
            for (int card = 1; card <= set.CardCount; card++)
            {
                // Local file uses FileNumber (from ConfigKey, may have gaps)
                string localFile = $"TCE_Case{caseObj.Index:D2}_Set{set.FileNumber:D2}_{card:D2}.png";
                string localPath = Path.Combine(exportDir, localFile);
                // Wiki file uses sequential Index (no gaps)
                string wikiFile = $"TCE_Case{caseObj.Index:D2}_Set{set.Index:D2}_{card:D2}.png";

                if (File.Exists(localPath))
                {
                    var (w, h) = ReadPngDimensions(localPath);
                    result.Add((wikiFile, localPath, w, h));
                }
            }
        }
        return result;
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        try
        {
            var header = new byte[24];
            using var fs = File.OpenRead(path);
            if (fs.Read(header, 0, 24) < 24) return (0, 0);
            // PNG IHDR: width at offset 16, height at offset 20 (big-endian)
            int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return (w, h);
        }
        catch { return (0, 0); }
    }

    // ── Reward formatting ───────────────────────────────────────────

    private static List<string> ParseRewardsRaw(JsonElement el)
    {
        var result = new List<string>();
        if (!el.TryGetProperty("Rewards", out var rewards) || rewards.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var r in rewards.EnumerateArray())
        {
            if (r.TryGetProperty("RewardEnergy", out var energy))
                result.Add($"{{{{Energy}}}} {GetInt(energy, "Amount")}");
            else if (r.TryGetProperty("RewardDiamonds", out var gems))
                result.Add($"{{{{Gems}}}} {GetInt(gems, "Amount")}");
            else if (r.TryGetProperty("RewardCoins", out var coins))
                result.Add($"{{{{Coins}}}} {GetInt(coins, "Amount")}");
            else if (r.TryGetProperty("RewardItem", out var item))
            {
                var itemDef = GetStr(item, "ItemDef");
                if (string.IsNullOrEmpty(itemDef)) itemDef = GetStr(item, "ItemRef");
                var amount = GetInt(item, "Amount");
                // Store raw: {{Item/Group|RAW_ITEMTYPE}}
                if (amount > 1)
                    result.Add($"{{{{Item/Group|{itemDef}}}}} \u00d7{amount}");
                else
                    result.Add($"{{{{Item/Group|{itemDef}}}}}");
            }
        }
        return result;
    }

    /// <summary>Resolves raw item references in reward strings using DataService chain data.</summary>
    public void ResolveRewardNames()
    {
        if (_dataService == null) return;
        foreach (var c in Cases)
        {
            ResolveList(c.GrandRewards);
            foreach (var set in c.Sets)
                ResolveList(set.Rewards);
        }
    }

    private void ResolveList(List<string> rewards)
    {
        for (int i = 0; i < rewards.Count; i++)
        {
            // Find {{Item/Group|RAW_ITEMTYPE}} and resolve
            var match = Regex.Match(rewards[i], @"\{\{Item/Group\|([^|}]+)\}\}");
            if (!match.Success) continue;
            string raw = match.Groups[1].Value;
            string resolved = ResolveItemName(raw);
            if (resolved != raw)
                rewards[i] = rewards[i].Replace($"{{{{Item/Group|{raw}}}}}", $"{{{{Item/Group|{resolved}}}}}");
        }
    }

    private DataService? _dataService;

    /// <summary>Sets DataService reference for dynamic item name resolution.</summary>
    public void SetDataService(DataService ds) => _dataService = ds;

    /// <summary>
    /// Resolves ItemDef (ItemType like "TimeSkipBooster_04") to wiki name + level.
    /// Uses DataService chain data for dynamic resolution, no hardcoded mappings.
    /// </summary>
    private string ResolveItemName(string itemDef)
    {
        // Parse ItemType: ChainKey_LevelNumber (e.g. "TimeSkipBooster_04" → chain "TimeSkipBooster", level 4)
        int lastUnderscore = itemDef.LastIndexOf('_');
        if (lastUnderscore <= 0 || !int.TryParse(itemDef[(lastUnderscore + 1)..], out int level))
            return itemDef;

        string chainKey = itemDef[..lastUnderscore];

        // Try to resolve via DataService chains (dynamic, data-driven)
        if (_dataService != null)
        {
            // Strategy 1: match by ConfigKey
            var chain = _dataService.Chains.FirstOrDefault(c =>
                string.Equals(c.ConfigKey, chainKey, StringComparison.OrdinalIgnoreCase));

            // Strategy 2: match by finding any item with this ItemType
            if (chain == null)
            {
                foreach (var c in _dataService.Chains)
                {
                    if (c.Items.Any(i => string.Equals(i.ItemType, itemDef, StringComparison.OrdinalIgnoreCase)))
                    {
                        chain = c;
                        break;
                    }
                }
            }

            if (chain != null)
            {
                string displayName = !string.IsNullOrEmpty(chain.DisplayName) ? chain.DisplayName : chainKey;
                return level > 1 ? $"{displayName}|{level}" : displayName;
            }
            AppLogger.Warn($"[ResolveItemName] Chain not found for '{chainKey}' / '{itemDef}' (DataService has {_dataService.Chains.Count} chains)");
        }

        // Fallback: use raw chain key
        return level > 1 ? $"{chainKey}|{level}" : chainKey;
    }

    // ── JSON helpers ────────────────────────────────────────────────

    private static string GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? "";
        return "";
    }

    private static int GetInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return 0;
    }

    private static string EscLua(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
