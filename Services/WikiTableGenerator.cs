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
    private readonly WikiMappingCache? _wikiMapping;

    public WikiTableGenerator(DataService data, WikiMappingCache? wikiMapping = null)
    {
        _data = data;
        _wikiMapping = wikiMapping;
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

        // Fuel For — build lookup: NumericConfigKey → list of (sinkChain, sinkItem)
        var fuelForMap = BuildFuelForMap(items);
        bool showFuelFor = fuelForMap.Count > 0;

        // Fuel — this chain has sink items that require fuel
        var fuelMap = BuildFuelMap(items);
        bool showFuel = fuelMap.Count > 0;

        // ── Extract Speed Up Cost from existing table if provided ──
        var speedUpCosts = new Dictionary<int, string>();
        if (!string.IsNullOrEmpty(existingTable))
            speedUpCosts = ExtractSpeedUpCosts(existingTable);

        // ── Build table ──
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("{| class=\"article-table\"");
        // If tableName contains brackets, use text before the bracket; otherwise use {{PAGENAME}}
        var bracketIdx = tableName.IndexOf('(');
        var captionName = bracketIdx >= 0
            ? tableName[..bracketIdx].Trim()
            : "{{PAGENAME}}";
        sb.AppendLine($"|+ <u>{captionName}</u>");
        sb.AppendLine("! Lvl");
        sb.AppendLine("! Image");
        sb.AppendLine("! Item");

        if (showSellsFor)
            sb.AppendLine("! {{Coins}} [[Coins|Sells for]]");

        if (showFuel)
            sb.AppendLine("! Fuel");

        if (showDrops)
            sb.AppendLine("! Drops");

        if (showDropValues)
            sb.AppendLine("! Drops Values");

        if (showFuelFor)
            sb.AppendLine("! Fuel For");

        if (showDecaysInto)
            sb.AppendLine("! Decays Into");

        if (showChargeTime)
            sb.AppendLine("! {{Time}} Charge Time");

        if (showRechargeTime)
            sb.AppendLine("! {{Time}} Recharge [[Time]]");

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

            // Fuel (what this sink item needs)
            if (showFuel)
                sb.AppendLine($"| {BuildFuelCell(item, fuelMap)}");

            // Drops
            if (showDrops)
                sb.AppendLine($"| {BuildDropsCell(item)}");

            // Drop Values
            if (showDropValues)
                sb.AppendLine($"| {BuildDropValuesCell(item)}");

            // Fuel For (which sinks consume this item)
            if (showFuelFor)
                sb.AppendLine($"| {BuildFuelForCell(item, fuelForMap)}");

            // Decays Into
            if (showDecaysInto)
                sb.AppendLine($"| {BuildDecaysIntoCell(item)}");

            // Charge Time (FirstCycleStartDelay)
            if (showChargeTime)
                sb.AppendLine($"| {BuildChargeTimeCell(item)}");

            // Recharge Time
            if (showRechargeTime)
                sb.AppendLine($"| {BuildRechargeTimeCell(item)}");

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
                var spawnLevel = _data.ResolveLevel(item.SpawnItemType, _wikiMapping);

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
            var level = _data.ResolveLevel(itemRef, _wikiMapping);

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
            var level = _data.ResolveLevel(item.DecayAfterLastCycleItemType, _wikiMapping);
            return $"{{{{Item|{name}|{level}}}}}";
        }

        // SpawnFeatures decay (after finite spawn cycles)
        if (!string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
        {
            var name = ResolveChainName(item.SpawnDecayIntoItemType);
            var level = _data.ResolveLevel(item.SpawnDecayIntoItemType, _wikiMapping);
            return $"{{{{Item|{name}|{level}}}}}";
        }

        // DecayFeatures (item transforms)
        if (item.HasDecay && !string.IsNullOrEmpty(item.DecayIntoItemType))
        {
            var name = ResolveChainName(item.DecayIntoItemType);
            var level = _data.ResolveLevel(item.DecayIntoItemType, _wikiMapping);
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

    // ── Fuel (what sink items in this chain require) ──────────────────

    /// <summary>
    /// Builds a map: item Level → list of required (chain, item) for sink items in this chain.
    /// </summary>
    private Dictionary<int, List<(ParsedChain Chain, ParsedItem Item)>> BuildFuelMap(List<ParsedItem> chainItems)
    {
        var sinkItems = chainItems.Where(i => i.IsSink && i.SinkRequirementConfigKeys != null).ToList();
        if (sinkItems.Count == 0) return new();

        // Build ConfigKey → (chain, item) lookup
        var configKeyToItem = new Dictionary<string, (ParsedChain Chain, ParsedItem Item)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var item in chain.Items)
                if (!string.IsNullOrEmpty(item.NumericConfigKey))
                    configKeyToItem.TryAdd(item.NumericConfigKey, (chain, item));

        var map = new Dictionary<int, List<(ParsedChain, ParsedItem)>>();

        foreach (var sinkItem in sinkItems)
        {
            var list = new List<(ParsedChain, ParsedItem)>();
            foreach (var reqKey in sinkItem.SinkRequirementConfigKeys!)
            {
                if (configKeyToItem.TryGetValue(reqKey, out var match))
                    list.Add(match);
            }
            if (list.Count > 0)
                map[sinkItem.Level] = list;
        }

        return map;
    }

    private string BuildFuelCell(ParsedItem item, Dictionary<int, List<(ParsedChain Chain, ParsedItem Item)>> fuelMap)
    {
        if (!fuelMap.TryGetValue(item.Level, out var requirements)) return "{{Dash}}";

        var parts = new List<string>();
        foreach (var (reqChain, reqItem) in requirements)
            parts.Add($"{{{{Item|{reqChain.DisplayName}|{reqItem.Level}}}}}");

        return parts.Count > 0 ? string.Join("<br>", parts) : "{{Dash}}";
    }

    // ── Fuel For ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a map: NumericConfigKey → list of (sinkChain, sinkItem) for all sinks
    /// that require any item from this chain.
    /// </summary>
    private Dictionary<string, List<(ParsedChain Chain, ParsedItem Item)>> BuildFuelForMap(List<ParsedItem> chainItems)
    {
        var myConfigKeys = chainItems
            .Where(i => !string.IsNullOrEmpty(i.NumericConfigKey))
            .Select(i => i.NumericConfigKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (myConfigKeys.Count == 0) return new();

        var map = new Dictionary<string, List<(ParsedChain, ParsedItem)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var otherChain in _data.Chains)
        {
            foreach (var item in otherChain.Items)
            {
                if (!item.IsSink || item.SinkRequirementConfigKeys == null) continue;

                foreach (var reqKey in item.SinkRequirementConfigKeys)
                {
                    if (!myConfigKeys.Contains(reqKey)) continue;

                    if (!map.TryGetValue(reqKey, out var list))
                    {
                        list = new();
                        map[reqKey] = list;
                    }
                    list.Add((otherChain, item));
                }
            }
        }

        return map;
    }

    private string BuildFuelForCell(ParsedItem item, Dictionary<string, List<(ParsedChain Chain, ParsedItem Item)>> fuelForMap)
    {
        if (string.IsNullOrEmpty(item.NumericConfigKey)) return "{{Dash}}";
        if (!fuelForMap.TryGetValue(item.NumericConfigKey, out var sinks)) return "{{Dash}}";

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sinkChain, sinkItem) in sinks)
        {
            if (!seen.Add(sinkChain.ConfigKey)) continue;
            parts.Add($"{{{{Item|{sinkChain.DisplayName}|{sinkItem.Level}}}}}");
        }

        return parts.Count > 0 ? string.Join("<br>", parts) : "{{Dash}}";
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private string ResolveChainName(string itemTypeOrChainKey)
    {
        // Try as full ItemType — wiki mapping has priority
        if (_data.ItemNames.ContainsKey(itemTypeOrChainKey))
            return _data.ResolveChainDisplayNameFromItemType(itemTypeOrChainKey, _wikiMapping);

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
