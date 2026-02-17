using System.Text;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Generates wiki-formatted item chain tables.
/// </summary>
public class WikiTableGenerator
{
    private readonly DataService _data;

    public WikiTableGenerator(DataService data)
    {
        _data = data;
    }

    /// <summary>Warnings encountered during generation.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Generates a wiki table for the given chain.
    /// </summary>
    /// <param name="chain">Parsed chain data</param>
    /// <param name="tableName">Display name for the table header</param>
    /// <param name="lowPrices">If true, sets LowPrices variable to true</param>
    /// <param name="existingTable">Optional existing table text to extract Speed Up Cost from</param>
    public string Generate(ParsedChain chain, string tableName, bool lowPrices, string? existingTable = null)
    {
        Warnings.Clear();

        var items = chain.Items.OrderBy(i => i.Level).ToList();
        if (items.Count == 0) return "<!-- No items in chain -->";

        // ── Determine which columns to show ──
        bool showSellsFor = items.Any(i => !i.Unsellable);
        bool showDrops = items.Any(i => HasDrops(i));
        bool showDropValues = items.Any(i =>
            (i.IsGenerator && i.ActivationAmountInCycle > 0)
            || (i.IsSpawner && i.SpawnStorageMax > 0));
        bool showRechargeTime = items.Any(i => i.IsGenerator && i.RechargeTimeMs >= 1000)
                             || items.Any(i => i.IsSpawner && i.SpawnDelayMs >= 1000);
        bool showChargeTime = items.Any(i => i.IsGenerator && i.FirstCycleStartDelayMs >= 5000);
        bool showSpeedUpCost = showRechargeTime; // Show alongside recharge
        bool showDecaysInto = items.Any(i =>
            !string.IsNullOrEmpty(i.SpawnDecayIntoItemType)
            || !string.IsNullOrEmpty(i.DecayAfterLastCycleItemType)
            || (i.HasDecay && !string.IsNullOrEmpty(i.DecayIntoItemType)));

        // ── Extract Speed Up Cost from existing table if provided ──
        var speedUpCosts = new Dictionary<int, string>();
        if (!string.IsNullOrEmpty(existingTable))
            speedUpCosts = ExtractSpeedUpCosts(existingTable);

        // ── Build table ──
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("{| class=\"article-table\"");
        sb.AppendLine($"|+ <u>{{{{PAGENAME}}}}</u>");
        sb.AppendLine("! Lvl");
        sb.AppendLine("! Image");
        sb.AppendLine("! Item");

        if (showSellsFor)
            sb.AppendLine("! [[Coins|Sells for]]");

        if (showDrops)
            sb.AppendLine("! Drops");

        if (showDropValues)
            sb.AppendLine("! Drops Values");

        if (showChargeTime)
            sb.AppendLine("! {{Time}} Charge Time");

        if (showRechargeTime)
            sb.AppendLine("! {{Time}} Recharge [[Time]]");

        if (showDecaysInto)
            sb.AppendLine("! Decays Into");

        if (showSpeedUpCost)
            sb.AppendLine("! Speed Up Cost");

        // Rows
        bool isFirst = true;
        foreach (var item in items)
        {
            if (isFirst)
            {
                sb.Append($"|- {{{{#vardefine:LowPrices|{(lowPrices ? "true" : "false")}}}}}");
                sb.AppendLine($"{{{{#vardefine:Level|{item.Level}}}}}");
                isFirst = false;
            }
            else
            {
                sb.AppendLine($"|-{{{{#vardefine:Level|{{{{#expr:{{{{#var:Level}}}}+1}}}}}}}}");
            }

            // Lvl
            sb.AppendLine($"| {{{{#var:Level}}}} <!-- {item.Level} -->");

            // Image
            sb.AppendLine($"| style=\"text-align:center;\" | {{{{Item/Icon|{{{{PAGENAME}}}}|{{{{#var:Level}}}}}}}}");

            // Item name
            sb.AppendLine($"| <u>{{{{#Invoke:Items|GetItemNameFromChainName|{{{{#var:Level}}}}}}}}</u>");

            // Sells for
            if (showSellsFor)
            {
                if (item.Unsellable)
                    sb.AppendLine("| {{Dash}}");
                else
                    sb.AppendLine("| {{#Invoke:Items|GetItemPriceByLevel|{{#var:Level}}}}");
            }

            // Drops
            if (showDrops)
                sb.AppendLine($"| {BuildDropsCell(item)}");

            // Drop Values
            if (showDropValues)
                sb.AppendLine($"| {BuildDropValuesCell(item)}");

            // Charge Time (FirstCycleStartDelay)
            if (showChargeTime)
                sb.AppendLine($"| {BuildChargeTimeCell(item)}");

            // Recharge Time
            if (showRechargeTime)
                sb.AppendLine($"| {BuildRechargeTimeCell(item)}");

            // Decays Into
            if (showDecaysInto)
                sb.AppendLine($"| {BuildDecaysIntoCell(item)}");

            // Speed Up Cost
            if (showSpeedUpCost)
            {
                if (speedUpCosts.TryGetValue(item.Level, out var cost))
                    sb.AppendLine($"| {cost}");
                else if (item.IsGenerator || (item.IsSpawner && item.SpawnDelayMs > 0))
                    sb.AppendLine("| {{Gems}} ?");
                else
                    sb.AppendLine("| {{Dash}}");
            }
        }

        sb.AppendLine("|}");

        return sb.ToString();
    }

    // ── Drops cell ──────────────────────────────────────────────────

    private string BuildDropsCell(ParsedItem item)
    {
        var parts = new List<string>();

        // XP drop for level 5+
        if (item.Level >= 5)
            parts.Add("{{XPDrop}}");

        // Generator drops (ActivationFeatures)
        if (item.IsGenerator && item.DropOdds != null && item.DropOdds.Count > 0)
        {
            var grouped = GroupDropsByChain(item.DropOdds);
            foreach (var group in grouped)
                parts.Add(FormatDropGroup(group));
        }

        // Spawner drops (SpawnFeatures)
        if (item.IsSpawner)
        {
            if (item.SpawnOdds != null && item.SpawnOdds.Count > 0)
            {
                // Random spawn with odds
                var grouped = GroupDropsByChain(item.SpawnOdds);
                foreach (var group in grouped)
                {
                    if (item.SpawnHowManyCycles > 0)
                        parts.Add($"{item.SpawnHowManyCycles * item.SpawnAmountInCycle}x {FormatDropGroup(group)}");
                    else
                        parts.Add(FormatDropGroup(group));
                }
            }
            else if (!string.IsNullOrEmpty(item.SpawnItemType))
            {
                // Constant spawn
                var spawnName = ResolveChainName(item.SpawnItemType);
                var spawnLevel = DataService.GetLevelFromItemType(item.SpawnItemType);

                if (item.SpawnHowManyCycles > 0 && item.SpawnHowManyCycles != -1)
                {
                    int totalSpawns = item.SpawnHowManyCycles * item.SpawnAmountInCycle;
                    parts.Add($"{totalSpawns}x {{{{Item|{spawnName}|{spawnLevel}}}}}");
                }
                else
                {
                    // Infinite spawner — no count
                    parts.Add($"{{{{Item|{spawnName}|{spawnLevel}}}}}");
                }
            }
        }

        if (parts.Count == 0)
            return "{{Dash}}";

        return string.Join("<br>", parts);
    }

    /// <summary>
    /// Groups item refs by their chain (e.g., Vase_01, Vase_02, Vase_03 → one group).
    /// Returns groups sorted by total drop chance (descending).
    /// </summary>
    private List<DropGroup> GroupDropsByChain(Dictionary<string, double> odds)
    {
        var groups = new Dictionary<string, DropGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var (itemRef, chance) in odds)
        {
            var chainKey = DataService.GetChainKeyFromItemType(itemRef);
            var level = DataService.GetLevelFromItemType(itemRef);

            if (!groups.TryGetValue(chainKey, out var group))
            {
                group = new DropGroup { ChainKey = chainKey };
                groups[chainKey] = group;
            }

            group.TotalChance += chance;
            group.Levels.Add(level);
        }

        return groups.Values
                     .OrderByDescending(g => g.TotalChance)
                     .ToList();
    }

    private string FormatDropGroup(DropGroup group)
    {
        var displayName = ResolveChainName(group.ChainKey);
        int minLevel = group.Levels.Min();
        int maxLevel = group.Levels.Max();

        if (minLevel == maxLevel)
        {
            // Single level: {{Item/Group|Name|level|min=level}}
            return $"{{{{Item/Group|{displayName}|{maxLevel}|min={minLevel}}}}}";
        }
        else
        {
            // Range: {{Item/Group|Name|maxLevel|min=minLevel|max=maxLevel}}
            return $"{{{{Item/Group|{displayName}|{maxLevel}|min={minLevel}|max={maxLevel}}}}}";
        }
    }

    // ── Drop Values cell ────────────────────────────────────────────

    private string BuildDropValuesCell(ParsedItem item)
    {
        // Generator drop values
        if (item.IsGenerator && item.ActivationAmountInCycle > 0)
        {
            int activationAmount = item.ActivationAmountInCycle;
            int batches = item.StorageMax > 0 && activationAmount > 0
                ? item.StorageMax / activationAmount
                : 1;

            return $"{{{{DropValuesTable|{activationAmount}|{batches}}}}}";
        }

        // Spawner drop values: 1 drop per charge, StorageMax charges
        if (item.IsSpawner && item.SpawnStorageMax > 0)
        {
            return $"{{{{DropValuesTable|1|{item.SpawnStorageMax}}}}}";
        }

        return "{{Dash}}";
    }

    // ── Charge Time cell (FirstCycleStartDelay) ─────────────────────

    private string BuildChargeTimeCell(ParsedItem item)
    {
        if (item.IsGenerator && item.FirstCycleStartDelayMs >= 5000)
            return $"{{{{TimeValuesTable|{item.FirstCycleStartDelayMs}}}}}";

        return "{{Dash}}";
    }

    // ── Recharge Time cell ──────────────────────────────────────────

    private string BuildRechargeTimeCell(ParsedItem item)
    {
        if (item.IsGenerator && item.RechargeTimeMs >= 1000)
            return $"{{{{TimeValuesTable|{item.RechargeTimeMs}}}}}";

        if (item.IsSpawner && item.SpawnDelayMs >= 1000)
            return $"{{{{TimeValuesTable|{item.SpawnDelayMs}}}}}";

        return "{{Dash}}";
    }

    // ── Decays Into cell ────────────────────────────────────────────

    private string BuildDecaysIntoCell(ParsedItem item)
    {
        // Generator decay after last cycle (e.g., Vase decays into Shrapnel)
        if (!string.IsNullOrEmpty(item.DecayAfterLastCycleItemType))
        {
            var name = ResolveChainName(item.DecayAfterLastCycleItemType);
            var level = DataService.GetLevelFromItemType(item.DecayAfterLastCycleItemType);
            return $"{{{{Item|{name}|{level}}}}}";
        }

        // SpawnFeatures decay (after finite spawn cycles)
        if (!string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
        {
            var name = ResolveChainName(item.SpawnDecayIntoItemType);
            var level = DataService.GetLevelFromItemType(item.SpawnDecayIntoItemType);
            return $"{{{{Item|{name}|{level}}}}}";
        }

        // DecayFeatures (item transforms)
        if (item.HasDecay && !string.IsNullOrEmpty(item.DecayIntoItemType))
        {
            var name = ResolveChainName(item.DecayIntoItemType);
            var level = DataService.GetLevelFromItemType(item.DecayIntoItemType);
            return $"{{{{Item|{name}|{level}}}}}";
        }

        // Spawner with finite cycles but no decay producer — suspicious
        if (item.IsSpawner && item.SpawnHowManyCycles > 0
            && string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
        {
            Warnings.Add($"Level {item.Level} ({item.Name}): Finite spawn cycles but no DecayProducer — item may vanish from board.");
        }

        return "{{Dash}}";
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private string ResolveChainName(string itemTypeOrChainKey)
    {
        // Try as full ItemType first
        if (_data.ItemNames.TryGetValue(itemTypeOrChainKey, out var fullName))
        {
            // Return the chain display name, not the individual item name
            var ck = DataService.GetChainKeyFromItemType(itemTypeOrChainKey);
            return _data.ResolveChainDisplayName(ck);
        }

        // Try as chain key
        return _data.ResolveChainDisplayName(itemTypeOrChainKey);
    }

    /// <summary>
    /// Extracts Speed Up Cost values from an existing wiki table.
    /// Returns a dictionary of level → cost text.
    /// </summary>
    private Dictionary<int, string> ExtractSpeedUpCosts(string tableText)
    {
        var result = new Dictionary<int, string>();

        try
        {
            var lines = tableText.Split('\n');

            // Find the "Speed Up Cost" column index by counting header lines
            int speedUpCol = -1;
            int colIndex = 0;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("!"))
                {
                    if (trimmed.Contains("Speed Up Cost", StringComparison.OrdinalIgnoreCase))
                        speedUpCol = colIndex;
                    colIndex++;
                }
            }

            if (speedUpCol < 0) return result;

            // Parse rows
            int currentLevel = 0;
            int currentCol = -1;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("|-"))
                {
                    currentCol = 0;
                    continue;
                }

                if (!trimmed.StartsWith("|") || trimmed.StartsWith("|+") || trimmed.StartsWith("|}"))
                    continue;

                if (currentCol < 0) continue;

                var cellValue = trimmed.TrimStart('|').Trim();

                // Try to extract level: from <!-- N --> comment, or plain number in first column
                var lvlComment = Regex.Match(trimmed, @"<!--\s*(\d+)\s*-->");
                if (lvlComment.Success)
                    currentLevel = int.Parse(lvlComment.Groups[1].Value);
                else if (currentCol == 0 && int.TryParse(cellValue.Trim(), out var plainLevel))
                    currentLevel = plainLevel;

                if (currentCol == speedUpCol && currentLevel > 0)
                {
                    if (!string.IsNullOrEmpty(cellValue) && cellValue != "{{Dash}}")
                        result[currentLevel] = cellValue;
                }

                currentCol++;
            }
        }
        catch
        {
            Warnings.Add("Failed to parse existing table for Speed Up Cost values.");
        }

        return result;
    }

    private bool HasDrops(ParsedItem item)
    {
        if (item.IsGenerator && item.DropOdds != null && item.DropOdds.Count > 0)
            return true;

        if (item.IsSpawner && (!string.IsNullOrEmpty(item.SpawnItemType) || (item.SpawnOdds != null && item.SpawnOdds.Count > 0)))
            return true;

        // Level 5+ always shows XPDrop
        if (item.Level >= 5)
            return true;

        return false;
    }

    private class DropGroup
    {
        public string ChainKey { get; set; } = "";
        public double TotalChance { get; set; }
        public List<int> Levels { get; set; } = new();
    }
}
