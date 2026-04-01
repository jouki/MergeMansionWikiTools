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
    public List<ClueCollectionSet> Sets { get; set; } = new();
    public List<string> GrandRewards { get; set; } = new(); // formatted wiki markup
    public bool ExistsOnWiki { get; set; }
}

public class ClueCollectionSet
{
    public int Index { get; set; } // 1-based within case
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
            var rewards = ParseRewards(cs);
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
                    caseObj.Sets.Add(new ClueCollectionSet
                    {
                        Index = setIdx,
                        ConfigKey = sid,
                        DisplayName = setInfo.Name ?? sid,
                        CardCount = setInfo.Cards > 0 ? setInfo.Cards : 9,
                        Rewards = setInfo.Rewards
                    });
                    setIdx++;
                }
            }

            // Parse grand rewards
            caseObj.GrandRewards = ParseRewards(ev);

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
    public static string GenerateModuleEntry(ClueCollectionCase caseObj)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\t[{caseObj.Index}] = {{name = \"{EscLua(caseObj.DisplayName)}\",");
        sb.AppendLine("\t\tclueSets ={ ");
        foreach (var set in caseObj.Sets)
        {
            var rewardStr = set.Rewards.Count > 0 ? string.Join("<br>", set.Rewards) : "";
            if (!string.IsNullOrEmpty(rewardStr))
                sb.AppendLine($"\t\t\t[{set.Index}]  = {{name = \"{EscLua(set.DisplayName)}\", reward = \"{EscLua(rewardStr)}\"}},");
            else
                sb.AppendLine($"\t\t\t[{set.Index}]  = {{name = \"{EscLua(set.DisplayName)}\"}},");
        }
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t},");
        return sb.ToString();
    }

    /// <summary>Generates a History table row for the wiki page.</summary>
    public static string GenerateHistoryRow(ClueCollectionCase caseObj)
    {
        int totalClues = caseObj.Sets.Sum(s => s.CardCount);
        string grandRewardStr = string.Join("<br>", caseObj.GrandRewards);

        var sb = new StringBuilder();
        sb.AppendLine("|- {{#vardefine:caseNumber|{{#expr:{{#var:caseNumber}}+1}}}}");
        sb.AppendLine("| {{#var:caseNumber}}");
        sb.AppendLine($"| {{{{#Invoke:Various|GetClueCollectionName|{{{{#var:caseNumber}}}}}}}}");
        sb.AppendLine($"| {{{{#Invoke:Various|GetClueCollectionSetsCount|{{{{#var:caseNumber}}}}}}}}");
        sb.AppendLine($"| {{{{#Invoke:Various|GetTotalCluesCount|{{{{#var:caseNumber}}}}}}}}");
        sb.AppendLine($"| {{{{Time}}}} 63 days"); // TODO: dynamic from ActivableParams if available
        sb.AppendLine($"| {grandRewardStr}");
        return sb.ToString();
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
                files.Add($"TCE_Case{caseNum:D2}_Set{set.Index:D2}_{card:D2}.png");
        }
        return files;
    }

    /// <summary>Finds existing image files in export directory.</summary>
    public static List<(string FileName, string FullPath, int Width, int Height)> FindExistingImages(
        string exportDir, ClueCollectionCase caseObj)
    {
        var result = new List<(string, string, int, int)>();
        var expected = GetExpectedImageFiles(caseObj);
        foreach (var fn in expected)
        {
            var fullPath = Path.Combine(exportDir, fn);
            if (File.Exists(fullPath))
            {
                // Quick size check via PNG header (first 24 bytes)
                var (w, h) = ReadPngDimensions(fullPath);
                result.Add((fn, fullPath, w, h));
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

    private static List<string> ParseRewards(JsonElement el)
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
                var name = ResolveItemName(itemDef);
                if (amount > 1)
                    result.Add($"{{{{Item/Group|{name}}}}} ×{amount}");
                else
                    result.Add($"{{{{Item/Group|{name}}}}}");
            }
        }
        return result;
    }

    private static string ResolveItemName(string itemDef)
    {
        // Known item mappings for card collection rewards
        return itemDef switch
        {
            "LevelUpBoosterBlueCard_01" => "Ursula's Blue Card",
            "LevelUpBoosterBlackCard_01" => "Boulton's Lightning Card",
            "LevelDownBoosterScissors_01" => "Scissors",
            "BrownChest_01" => "Brown Chest",
            "FancyBlueChest_01" => "Fancy Blue Chest",
            "InfiniteEnergySmall_01" => "Unlimited Energy",
            "InfiniteEnergyMid_01" => "Unlimited Energy",
            "InfiniteEnergyBig_01" => "Unlimited Energy",
            "TimeSkipBooster_03" => "Hourglass",
            "PiggyBank_04" => "Piggy Bank",
            "Tcce_final_chest_01" => "Reward Box|4",
            "Tcce_final_chest_producers_01" => "Reward Box|5",
            _ => itemDef
        };
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
