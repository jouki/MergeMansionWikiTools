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

    private bool IsSinkSuppressed(ParsedItem item) =>
        _wikiMapping?.Mappings.TryGetValue(item.ItemType, out var entry) == true && entry.IsNil("fuels");

    /// <summary>
    /// Generates a wiki table for the given chain.
    /// </summary>
    /// <param name="chain">Parsed chain data</param>
    /// <param name="tableName">Display name for the table header</param>
    /// <param name="lowPrices">If true, sets LowPrices variable to true</param>
    /// <param name="captionOverride">If set, use this for the table caption instead of tableName/PAGENAME</param>
    /// <param name="sellsForInvoke">If set, force show Sells For column with this invoke template per row (e.g. for event items)</param>
    public string Generate(ParsedChain chain, string tableName, bool lowPrices, bool hardcodeName = false, string? captionOverride = null, string? sellsForInvoke = null)
    {
        Warnings.Clear();

        // Use all items (incl. aliases) for column visibility, but only non-aliases for rows
        var allItems = chain.Items.OrderBy(i => i.Level).ToList();
        var items = allItems.Where(i => !i.IsAlias).ToList();
        if (items.Count == 0) return "<!-- No items in chain -->";

        // ── Determine which columns to show (check ALL items incl. aliases) ──
        // Skip "Sells for" when chain has only one unique level AND every item carries
        // the DontSellIfOnlyOneOrHighest tag — game suppresses the sell action there too,
        // so the column would otherwise show a misleading 1-coin price.
        bool isOneLevel = allItems.Select(i => i.Level).Distinct().Count() == 1;
        bool dontSellOneOrHighest = allItems.All(i =>
            i.Tags != null && i.Tags.Any(t => string.Equals(t, "DontSellIfOnlyOneOrHighest", StringComparison.OrdinalIgnoreCase)));
        bool suppressSellsFor = isOneLevel && dontSellOneOrHighest;
        bool showSellsFor = sellsForInvoke != null || (allItems.Any(i => !i.Unsellable) && !suppressSellsFor);
        bool showDrops = allItems.Any(i => HasDrops(i));
        bool showDropValues = allItems.Any(i =>
            (i.IsGenerator && i.ActivationAmountInCycle > 0)
            || (i.IsSpawner && i.SpawnStorageMax > 0));
        bool showRechargeTime = allItems.Any(i => i.IsGenerator && i.RechargeTimeMs >= 1000)
                             || allItems.Any(i => i.IsSpawner && i.SpawnDelayMs >= 1000);
        bool showChargeTime = allItems.Any(i => i.IsGenerator && i.FirstCycleStartDelayMs >= 5000);
        bool showSpeedUpCost = showRechargeTime || showChargeTime;
        bool showDecaysInto = allItems.Any(i =>
            !string.IsNullOrEmpty(i.SpawnDecayIntoItemType)
            || !string.IsNullOrEmpty(i.DecayAfterLastCycleItemType)
            || (i.DecayAfterLastCycleOdds != null && i.DecayAfterLastCycleOdds.Count > 0)
            || (i.HasDecay && !string.IsNullOrEmpty(i.DecayIntoItemType)));
        bool showDecayOdds = allItems.Any(i =>
            i.DecayAfterLastCycleOdds != null && i.DecayAfterLastCycleOdds.Count > 0);

        // Fuel For — build lookup: NumericConfigKey → list of (sinkChain, sinkItem)
        var fuelForMap = BuildFuelForMap(allItems);
        bool showFuelFor = fuelForMap.Count > 0;

        // Fuel — this chain has sink items that require fuel
        var fuelMap = BuildFuelMap(allItems);
        bool showFuel = fuelMap.Count > 0;

        // OrderFeatures items: emit Fuel + Drops + Drops per Task columns built
        // from per-task Required/Rewards. The "or"-joined chain alternatives per slot
        // mimic the hand-written Distillation Apparatus wiki layout. These columns
        // co-exist with the standard Fuel/Drops columns since OrderFeatures rarely
        // overlap with sink/drop-odds items in the same chain.
        bool showOrderFuel = allItems.Any(i => i.OrderTasks is { Count: > 0 } && i.OrderTasks.Any(t => t.Required.Count > 0));
        bool showOrderDrops = allItems.Any(i => i.OrderTasks is { Count: > 0 } && i.OrderTasks.Any(t => t.Rewards.Count > 0));
        bool showDropsPerTask = showOrderDrops;

        // Transforms To — emitted when chain has at least one of:
        // (1) a sink reward item (skip items with fuels suppressed via mapping)
        // (2) a cross-chain merge target — MergeFeatures.Mechanic.ResultProducer points
        //     to an item from a DIFFERENT chain (e.g. SeedBagEmpty_04 → GoldRoot_01).
        bool HasCrossChainMergeTarget(ParsedItem it) =>
            !string.IsNullOrEmpty(it.MergeResultItemType)
            && _data.Chains.FirstOrDefault(c => c.Items.Any(ci =>
                   string.Equals(ci.ItemType, it.MergeResultItemType, StringComparison.OrdinalIgnoreCase))) is { } target
            && target != chain;

        bool showTransformsTo =
            allItems.Any(i => i.IsSink && !string.IsNullOrEmpty(i.SinkRewardItemType) && !IsSinkSuppressed(i))
            || allItems.Any(HasCrossChainMergeTarget);

        // Transforms To Variant — separate column when transform targets have variants
        bool showTransformsToVariant = false;

        // Build level → all items (incl. aliases) for aggregating cell data
        var itemsByLevel = allItems.GroupBy(i => i.Level)
            .ToDictionary(g => g.Key, g => g.ToList());

        // ── Variant sub-row detection ──
        var levelDiffering = new Dictionary<int, HashSet<string>>();
        foreach (var (level, lvlItems) in itemsByLevel)
        {
            if (lvlItems.Count > 1)
            {
                var diff = GetDifferingColumns(lvlItems);
                if (diff.Count > 0)
                    levelDiffering[level] = diff;
            }
        }
        bool hasVariants = levelDiffering.Count > 0;

        // Detect Transforms To Variant column: any sink OR cross-chain-merge item
        // transforms into a target whose level has multiple variants.
        if (showTransformsTo)
        {
            var targetItemTypes = new List<string>();
            foreach (var it in allItems)
            {
                if (it.IsSink && !string.IsNullOrEmpty(it.SinkRewardItemType) && !IsSinkSuppressed(it))
                    targetItemTypes.Add(it.SinkRewardItemType!);
                if (HasCrossChainMergeTarget(it))
                    targetItemTypes.Add(it.MergeResultItemType!);
            }

            foreach (var targetType in targetItemTypes)
            {
                foreach (var ch in _data.Chains)
                    foreach (var ci in ch.Items)
                        if (string.Equals(ci.ItemType, targetType, StringComparison.OrdinalIgnoreCase))
                            if (ch.Items.Count(i => i.Level == ci.Level) > 1)
                            { showTransformsToVariant = true; goto ttVarDone; }
            }
            ttVarDone:;
        }

        // Determine Variant column insertion point (before first globally-differing visible column)
        string? firstSplitCol = null;
        if (hasVariants)
        {
            var allDiff = new HashSet<string>();
            foreach (var d in levelDiffering.Values) allDiff.UnionWith(d);
            var splitOrder = new[] { "Drops", "DropValues", "DecaysInto", "DecayOdds", "ChargeTime", "RechargeTime", "SpeedUpCost" };
            var colVisible = new Dictionary<string, bool>
            {
                ["Drops"] = showDrops, ["DropValues"] = showDropValues, ["DecaysInto"] = showDecaysInto,
                ["DecayOdds"] = showDecayOdds, ["ChargeTime"] = showChargeTime,
                ["RechargeTime"] = showRechargeTime, ["SpeedUpCost"] = showSpeedUpCost
            };
            firstSplitCol = splitOrder.FirstOrDefault(c => allDiff.Contains(c) && colVisible.GetValueOrDefault(c));
        }

        // Detect decay expansion types:
        // - Non-nested: normal two-column expansion in outer table (needs outer Decay Odds sub-headers)
        // - Nested: nested table with its own headers (outer headers skipped)
        bool hasAnyNonNestedDecayExpansion = false;
        bool hasAnyNestedDecayTable = false;
        if (showDecayOdds)
        {
            foreach (var lvlItems in itemsByLevel.Values)
            {
                var groups = GetDecayChainGroups(lvlItems);
                if (groups.Count > 1)
                {
                    if (groups.Any(g => g.TargetVariantCount > 1 && g.Levels.Count == 1))
                        hasAnyNestedDecayTable = true;
                    else
                        hasAnyNonNestedDecayExpansion = true;
                }
            }
        }
        // 3-column decay area exists when any expansion type is present
        bool hasDecayExpansionColumns = hasAnyNonNestedDecayExpansion || hasAnyNestedDecayTable;

        // ── Pre-compute header nested table (emitted from header row, not data row) ──
        string? headerNestedTable = null;
        int headerNestedRowspan = 0;
        int headerNestedColspan = 0;

        if (hasAnyNestedDecayTable && !hasAnyNonNestedDecayExpansion)
        {
            // Count total data rows across all levels
            int totalDataRows = 0;
            foreach (var itm in items)
            {
                var lvlItems = itemsByLevel.GetValueOrDefault(itm.Level) ?? new List<ParsedItem> { itm };
                bool hasSub = levelDiffering.ContainsKey(itm.Level);
                if (hasSub)
                {
                    var dg = GetDecayChainGroups(lvlItems);
                    bool isNested = dg.Count > 1 && dg.Any(g => g.TargetVariantCount > 1 && g.Levels.Count == 1);
                    totalDataRows += isNested ? lvlItems.Count : lvlItems.Count * Math.Max(dg.Count, 1);
                }
                else
                    totalDataRows += 1;
            }

            // Build nested table from the first level that has one
            foreach (var itm in items)
            {
                var lvlItems = itemsByLevel.GetValueOrDefault(itm.Level) ?? new List<ParsedItem> { itm };
                var dg = GetDecayChainGroups(lvlItems);
                if (dg.Count > 1 && dg.Any(g => g.TargetVariantCount > 1 && g.Levels.Count == 1))
                {
                    headerNestedTable = BuildDecayNestedTable(dg, lvlItems);
                    headerNestedRowspan = 1 + totalDataRows; // 1 header row + all data rows
                    headerNestedColspan = 3; // Decays Into + Variant + Odds
                    if (hasVariants && (firstSplitCol == "DecaysInto" || firstSplitCol == "DecayOdds"))
                        headerNestedColspan++;
                    break;
                }
            }
        }

        // ── Build table ──
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("{| class = \"article-table\"");
        var caption = captionOverride ?? (hardcodeName ? tableName : "{{PAGENAME}}");
        sb.AppendLine($"|+ <u>{caption}</u>");
        var captionName = hardcodeName ? tableName : "{{PAGENAME}}";

        // Two-row header only when normal (non-nested) decay expansion exists
        string hp = hasAnyNonNestedDecayExpansion ? "! rowspan = 2 | " : "! ";
        // Force header height to match nested table's 2 sub-header rows (each 40px)
        if (headerNestedTable != null)
            sb.AppendLine($"{hp}style = \"height: 86.77px;\" | Lvl");
        else
            sb.AppendLine($"{hp}Lvl");
        sb.AppendLine($"{hp}Image");
        sb.AppendLine($"{hp}Item");

        if (showSellsFor)
            sb.AppendLine($"{hp}{{{{Coins}}}} [[Coins|Sells for]]");

        if (showFuel)
            sb.AppendLine($"{hp}Fuel");

        // OrderFeatures Fuel column (rendered same name as sink Fuel; used when items
        // produce rewards via per-task requirements rather than sink fuel mechanics).
        if (showOrderFuel)
            sb.AppendLine($"{hp}Fuel");

        if (showTransformsTo)
            sb.AppendLine($"{hp}Transforms To");
        if (showTransformsToVariant)
            sb.AppendLine($"{hp}Variant");

        if (hasVariants && firstSplitCol == "Drops") sb.AppendLine($"{hp}Variant");
        if (showDrops)
            sb.AppendLine($"{hp}Drops");

        if (showOrderDrops)
            sb.AppendLine($"{hp}Drops");
        if (showDropsPerTask)
            sb.AppendLine($"{hp}Drops per Task");

        if (hasVariants && firstSplitCol == "DropValues") sb.AppendLine($"{hp}Variant");
        if (showDropValues)
            sb.AppendLine($"{hp}Drops Values");

        if (showFuelFor)
            sb.AppendLine($"{hp}Fuel For");

        // Decays Into + Decay Odds: nested table in header row, or blank placeholder
        if (hasAnyNestedDecayTable && !hasAnyNonNestedDecayExpansion)
        {
            if (headerNestedTable != null)
            {
                // Nested table cell in header row — spans header + all data rows
                sb.AppendLine($"| rowspan = {headerNestedRowspan} colspan = {headerNestedColspan} class = \"padding0 fixedCellHeight valignTop\" style = \"height: 0px\" |\n{headerNestedTable}");
            }
            else
            {
                int decayHeaderColspan = 3;
                if (hasVariants && (firstSplitCol == "DecaysInto" || firstSplitCol == "DecayOdds"))
                    decayHeaderColspan++;
                sb.AppendLine($"! colspan = {decayHeaderColspan} |");
            }
        }
        else
        {
            if (hasVariants && firstSplitCol == "DecaysInto") sb.AppendLine($"{hp}Variant");
            if (showDecaysInto)
                sb.AppendLine($"{hp}Decays Into");

            if (hasVariants && firstSplitCol == "DecayOdds") sb.AppendLine($"{hp}Variant");
            if (showDecayOdds)
            {
                if (hasDecayExpansionColumns)
                    sb.AppendLine("! colspan = 2 | Decay Odds");
                else
                    sb.AppendLine("! Decay Odds");
            }
        }

        if (hasVariants && firstSplitCol == "ChargeTime") sb.AppendLine($"{hp}Variant");
        if (showChargeTime)
            sb.AppendLine($"{hp}{{{{Time}}}} Charge Time");

        if (hasVariants && firstSplitCol == "RechargeTime") sb.AppendLine($"{hp}Variant");
        if (showRechargeTime)
            sb.AppendLine($"{hp}{{{{Time}}}} Recharge [[Time]]");

        if (hasVariants && firstSplitCol == "SpeedUpCost") sb.AppendLine($"{hp}Variant");
        if (showSpeedUpCost)
            sb.AppendLine($"{hp}Speed Up Cost");

        // Sub-header row for normal (non-nested) Decay Odds expansion
        if (hasAnyNonNestedDecayExpansion)
        {
            sb.AppendLine("|-");
            sb.AppendLine("! Variant !! Odds");
        }

        // Name invoke: hardcodeName passes chain name as second parameter
        string nameInvoke = hardcodeName
            ? $"{{{{#Invoke:Items|GetItemNameFromChainName|{{{{#var:Level}}}}|{captionName}}}}}"
            : "{{#Invoke:Items|GetItemNameFromChainName|{{#var:Level}}}}";

        // Rows
        bool isFirst = true;
        foreach (var item in items)
        {
            var levelItems = itemsByLevel.GetValueOrDefault(item.Level) ?? new List<ParsedItem> { item };
            bool hasSubRows = levelDiffering.ContainsKey(item.Level);
            var diff = hasSubRows ? levelDiffering[item.Level] : new HashSet<string>();
            int vc = hasSubRows ? levelItems.Count : 1;

            if (hasSubRows)
            {
                // ── Level with variant sub-rows ──
                var decayGroups = GetDecayChainGroups(levelItems);
                int dcCount = decayGroups.Count > 1 ? decayGroups.Count : 1;
                bool hasDecayExpansion = dcCount > 1;

                // Detect target variant expansion (decay targets are aliases at same level)
                bool hasTargetVariantExpansion = hasDecayExpansion
                    && decayGroups.Any(g => g.TargetVariantCount > 1 && g.Levels.Count == 1);

                if (hasTargetVariantExpansion)
                {
                    // ── Target variant expansion via nested table ──
                    // Outer table has vc rows (one per source variant).
                    // Decays Into + Decay Odds are a single colspan cell with a nested table,
                    // independent of source variant count.
                    int totalRows = vc;
                    var nestedTable = BuildDecayNestedTable(decayGroups, levelItems);

                    for (int v = 0; v < vc; v++)
                    {
                        var vi = levelItems[v];

                        if (v == 0)
                        {
                            if (isFirst)
                            {
                                sb.Append($"|- {{{{#vardefine:LowPrices|{(lowPrices ? "true" : "false")}}}}}");
                                sb.AppendLine($"{{{{#vardefine:Level|{item.Level}}}}}");
                                isFirst = false;
                            }
                            else
                                sb.AppendLine($"|-{{{{#vardefine:Level|{{{{#expr:{{{{#var:Level}}}}+1}}}}}}}}");

                            sb.AppendLine($"| rowspan = {totalRows} | {{{{#var:Level}}}} <!-- {item.Level} -->");
                            sb.AppendLine($"| rowspan = {totalRows} style = \"text-align: center;\" | {{{{Item/Icon|{captionName}|{{{{#var:Level}}}}}}}}");
                            sb.AppendLine($"| rowspan = {totalRows} | <u>{nameInvoke}</u>");

                            if (showSellsFor)
                            {
                                if (sellsForInvoke != null)
                                    sb.AppendLine($"| rowspan = {totalRows} | {sellsForInvoke}");
                                else if (levelItems.All(i => i.Unsellable))
                                    sb.AppendLine($"| rowspan = {totalRows} | {{{{Dash}}}}");
                                else
                                    sb.AppendLine($"| rowspan = {totalRows} | {{{{#Invoke:Items|GetItemPriceByLevel|{{{{#var:Level}}}}}}}}");
                            }

                            if (showFuel)
                                sb.AppendLine($"| rowspan = {totalRows} | {BuildFuelCell(item, fuelMap)}");
                            if (showOrderFuel)
                                sb.AppendLine($"| rowspan = {totalRows} | {BuildOrderFuelCell(item)}");
                            if (showTransformsTo)
                                sb.AppendLine($"| rowspan = {totalRows} | {BuildTransformsToCellAggregated(levelItems, chain)}");
                        }
                        else
                        {
                            sb.AppendLine("|-");
                        }

                        // Transforms To Variant — per variant row
                        if (showTransformsToVariant)
                            sb.AppendLine($"| {GetTransformsToVariantLabel(vi, chain) ?? "{{Dash}}"}");

                        EmitSplitColumns(sb, v, vc, vi, item, levelItems, diff,
                            showDrops, showDropValues, showFuelFor, showDecaysInto,
                            showDecayOdds, showChargeTime, showRechargeTime, showSpeedUpCost,
                            hasVariants, firstSplitCol, fuelForMap,
                            showOrderDrops, showDropsPerTask,
                            totalRows: totalRows, hasAnyDecayExpansion: hasDecayExpansionColumns,
                            nestedDecayTable: headerNestedTable != null ? null : nestedTable,
                            skipDecayColumns: headerNestedTable != null);
                    }
                }
                else
                {
                    // ── Normal sub-rows (with or without decay expansion) ──
                    int totalRows = vc * dcCount;

                    for (int dg = 0; dg < dcCount; dg++)
                    {
                        for (int v = 0; v < vc; v++)
                        {
                            var vi = levelItems[v];

                            if (dg == 0 && v == 0)
                            {
                                if (isFirst)
                                {
                                    sb.Append($"|- {{{{#vardefine:LowPrices|{(lowPrices ? "true" : "false")}}}}}");
                                    sb.AppendLine($"{{{{#vardefine:Level|{item.Level}}}}}");
                                    isFirst = false;
                                }
                                else
                                    sb.AppendLine($"|-{{{{#vardefine:Level|{{{{#expr:{{{{#var:Level}}}}+1}}}}}}}}");

                                sb.AppendLine($"| rowspan = {totalRows} | {{{{#var:Level}}}} <!-- {item.Level} -->");
                                sb.AppendLine($"| rowspan = {totalRows} style = \"text-align: center;\" | {{{{Item/Icon|{captionName}|{{{{#var:Level}}}}}}}}");
                                sb.AppendLine($"| rowspan = {totalRows} | <u>{nameInvoke}</u>");

                                if (showSellsFor)
                                {
                                    if (sellsForInvoke != null)
                                        sb.AppendLine($"| rowspan = {totalRows} | {sellsForInvoke}");
                                    else if (levelItems.All(i => i.Unsellable))
                                        sb.AppendLine($"| rowspan = {totalRows} | {{{{Dash}}}}");
                                    else
                                        sb.AppendLine($"| rowspan = {totalRows} | {{{{#Invoke:Items|GetItemPriceByLevel|{{{{#var:Level}}}}}}}}");
                                }

                                if (showFuel)
                                    sb.AppendLine($"| rowspan = {totalRows} | {BuildFuelCell(item, fuelMap)}");
                                if (showTransformsTo)
                                    sb.AppendLine($"| rowspan = {totalRows} | {BuildTransformsToCellAggregated(levelItems, chain)}");
                            }
                            else
                            {
                                sb.AppendLine("|-");
                            }

                            // Transforms To Variant — per variant row
                            if (showTransformsToVariant)
                                sb.AppendLine($"| {GetTransformsToVariantLabel(vi, chain) ?? "{{Dash}}"}");

                            EmitSplitColumns(sb, v, vc, vi, item, levelItems, diff,
                                showDrops, showDropValues, showFuelFor, showDecaysInto,
                                showDecayOdds, showChargeTime, showRechargeTime, showSpeedUpCost,
                                hasVariants, firstSplitCol, fuelForMap,
                                showOrderDrops, showDropsPerTask,
                                dg, dcCount, totalRows, hasDecayExpansion ? decayGroups : null,
                                hasDecayExpansionColumns,
                                skipDecayColumns: headerNestedTable != null);
                        }
                    }
                }
            }
            else
            {
                // ── Normal row (no sub-rows) ──
                if (isFirst)
                {
                    sb.Append($"|- {{{{#vardefine:LowPrices|{(lowPrices ? "true" : "false")}}}}}");
                    sb.AppendLine($"{{{{#vardefine:Level|{item.Level}}}}}");
                    isFirst = false;
                }
                else
                    sb.AppendLine($"|-{{{{#vardefine:Level|{{{{#expr:{{{{#var:Level}}}}+1}}}}}}}}");

                sb.AppendLine($"| {{{{#var:Level}}}} <!-- {item.Level} -->");
                sb.AppendLine($"| style = \"text-align: center;\" | {{{{Item/Icon|{captionName}|{{{{#var:Level}}}}}}}}");
                sb.AppendLine($"| <u>{nameInvoke}</u>");

                if (showSellsFor)
                {
                    if (sellsForInvoke != null)
                        sb.AppendLine($"| {sellsForInvoke}");
                    else if (levelItems.All(i => i.Unsellable))
                        sb.AppendLine("| {{Dash}}");
                    else
                        sb.AppendLine("| {{#Invoke:Items|GetItemPriceByLevel|{{#var:Level}}}}");
                }

                if (showFuel)
                    sb.AppendLine($"| {BuildFuelCell(item, fuelMap)}");
                if (showOrderFuel)
                    sb.AppendLine($"| {BuildOrderFuelCell(item)}");
                if (showTransformsTo)
                    sb.AppendLine($"| {BuildTransformsToCellAggregated(levelItems, chain)}");
                if (showTransformsToVariant)
                    sb.AppendLine($"| {GetTransformsToVariantLabel(item, chain) ?? "{{Dash}}"}");

                // Variant column = Dash for levels without variants
                if (hasVariants && firstSplitCol == "Drops") sb.AppendLine("| {{Dash}}");
                if (showDrops)
                    sb.AppendLine($"| {BuildDropsCellAggregated(levelItems)}");
                if (showOrderDrops)
                    sb.AppendLine($"| {BuildOrderDropsCell(item)}");
                if (showDropsPerTask)
                    sb.AppendLine($"| {BuildOrderDropsPerTaskCell(item)}");

                if (hasVariants && firstSplitCol == "DropValues") sb.AppendLine("| {{Dash}}");
                if (showDropValues)
                    sb.AppendLine($"| {BuildDropValuesCell(item)}");

                if (showFuelFor)
                    sb.AppendLine($"| {BuildFuelForCell(item, fuelForMap)}");

                if (headerNestedTable == null)
                {
                    if (hasVariants && firstSplitCol == "DecaysInto") sb.AppendLine("| {{Dash}}");
                    if (showDecaysInto)
                        sb.AppendLine($"| {BuildDecaysIntoCellAggregated(levelItems)}");

                    if (hasVariants && firstSplitCol == "DecayOdds") sb.AppendLine("| {{Dash}}");
                    if (showDecayOdds)
                    {
                        if (hasDecayExpansionColumns)
                            sb.AppendLine($"| colspan = 2 | {BuildDecayOddsCellAggregated(levelItems)}");
                        else
                            sb.AppendLine($"| {BuildDecayOddsCellAggregated(levelItems)}");
                    }
                }

                if (hasVariants && firstSplitCol == "ChargeTime") sb.AppendLine("| {{Dash}}");
                if (showChargeTime)
                    sb.AppendLine($"| {BuildChargeTimeCell(item)}");

                if (hasVariants && firstSplitCol == "RechargeTime") sb.AppendLine("| {{Dash}}");
                if (showRechargeTime)
                    sb.AppendLine($"| {BuildRechargeTimeCell(item)}");

                if (hasVariants && firstSplitCol == "SpeedUpCost") sb.AppendLine("| {{Dash}}");
                if (showSpeedUpCost)
                {
                    if (levelItems.Any(i => i.IsGenerator || (i.IsSpawner && i.SpawnDelayMs > 0)))
                        sb.AppendLine("| {{Gems}} {{#Invoke:Items|GetItemSkipPriceFromChainName|{{#var:Level}}}}");
                    else
                        sb.AppendLine("| {{Dash}}");
                }
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
            var chainKey = _data.ResolveChainKeyFromItemType(itemRef);
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
        if (item.IsGenerator && item.ActivationAmountInCycle > 0)
            return "{{#Invoke:Items|GetItemDropValuesFromChainName|{{#var:Level}}}}";

        // Spawner drop values: 1 drop per charge, StorageMax charges
        if (item.IsSpawner && item.SpawnStorageMax > 0)
            return $"{{{{DropValuesTable|1|{item.SpawnStorageMax}}}}}";

        return "{{Dash}}";
    }

    // ── Charge Time cell (FirstCycleStartDelay) ─────────────────────

    private string BuildChargeTimeCell(ParsedItem item)
    {
        if (item.IsGenerator && item.FirstCycleStartDelayMs >= 5000)
            return "{{#Invoke:Items|GetItemChargeTimeFromChainName|{{#var:Level}}}}";

        return "{{Dash}}";
    }

    // ── Recharge Time cell ──────────────────────────────────────────

    private string BuildRechargeTimeCell(ParsedItem item)
    {
        if (item.IsGenerator && item.RechargeTimeMs >= 1000)
            return "{{#Invoke:Items|GetItemRechargeTimeFromChainName|{{#var:Level}}}}";

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

    // ── Aggregated cells (combine data from all items at the same level) ──

    /// <summary>Aggregates decay targets from all items at a level (incl. aliases).</summary>
    private string BuildDecaysIntoCellAggregated(List<ParsedItem> levelItems)
    {
        var seen = new HashSet<(string Name, int Level)>();
        var parts = new List<string>();

        // Build ItemType → (chain, item) lookup for resolving decay targets
        var itemTypeToChain = new Dictionary<string, (ParsedChain Chain, ParsedItem Item)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var ci in chain.Items)
                if (!string.IsNullOrEmpty(ci.ItemType))
                    itemTypeToChain.TryAdd(ci.ItemType, (chain, ci));

        foreach (var item in levelItems)
        {
            var decayTypes = new List<string?> {
                item.DecayAfterLastCycleItemType,
                item.SpawnDecayIntoItemType,
                item.HasDecay ? item.DecayIntoItemType : null
            };

            // DecayAfterLastCycleOdds — multiple decay targets
            if (item.DecayAfterLastCycleOdds != null)
                decayTypes.AddRange(item.DecayAfterLastCycleOdds.Keys);

            foreach (var decayType in decayTypes)
            {
                if (string.IsNullOrEmpty(decayType)) continue;

                if (itemTypeToChain.TryGetValue(decayType, out var match))
                {
                    if (seen.Add((match.Chain.DisplayName, match.Item.Level)))
                        parts.Add($"{{{{Item|{match.Chain.DisplayName}|{match.Item.Level}}}}}");
                }
                else
                {
                    // Fallback to ResolveChainName for non-item-type keys
                    var name = ResolveChainName(decayType);
                    var level = _data.ResolveLevel(decayType, _wikiMapping);
                    if (seen.Add((name, level)))
                        parts.Add($"{{{{Item|{name}|{level}}}}}");
                }
            }

            // Warning for suspicious finite spawner
            if (item.IsSpawner && item.SpawnHowManyCycles > 0
                && string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
            {
                Warnings.Add($"Level {item.Level} ({item.Name}): Finite spawn cycles but no DecayProducer — item may vanish from board.");
            }
        }

        return parts.Count > 0 ? string.Join("<br>", parts) : "{{Dash}}";
    }

    /// <summary>Aggregates drops from all items at a level (incl. aliases).</summary>
    private string BuildDropsCellAggregated(List<ParsedItem> levelItems)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in levelItems)
        {
            var cellParts = BuildDropsCell(item);
            if (cellParts != "{{Dash}}" && seen.Add(cellParts))
                parts.Add(cellParts);
        }

        return parts.Count > 0 ? string.Join("<br>", parts) : "{{Dash}}";
    }

    /// <summary>Aggregates transforms-to from all items at a level (incl. aliases).
    /// Includes both sink rewards AND cross-chain merge targets (MergeFeatures.Mechanic
    /// .ResultProducer pointing to an item from a different chain — e.g. SeedBagEmpty_04
    /// → GoldRoot_01).</summary>
    private string BuildTransformsToCellAggregated(List<ParsedItem> levelItems, ParsedChain currentChain)
    {
        var seen = new HashSet<(string, int)>();
        var parts = new List<string>();

        void AddTarget(string targetItemType, bool requireDifferentChain)
        {
            foreach (var chain in _data.Chains)
            {
                if (requireDifferentChain && chain == currentChain) continue;

                foreach (var ci in chain.Items)
                    if (string.Equals(ci.ItemType, targetItemType, StringComparison.OrdinalIgnoreCase))
                        if (seen.Add((chain.DisplayName, ci.Level)))
                            parts.Add($"{{{{Item|{chain.DisplayName}|{ci.Level}}}}}");
            }
        }

        foreach (var item in levelItems)
        {
            if (item.IsSink && !string.IsNullOrEmpty(item.SinkRewardItemType) && !IsSinkSuppressed(item))
                AddTarget(item.SinkRewardItemType!, requireDifferentChain: false);

            if (!string.IsNullOrEmpty(item.MergeResultItemType))
                AddTarget(item.MergeResultItemType!, requireDifferentChain: true);
        }

        return parts.Count > 0 ? string.Join("<br>", parts) : "{{Dash}}";
    }

    // ── Fuel (what sink items in this chain require) ──────────────────

    /// <summary>
    /// Builds a map: item Level → list of required (chain, item) for sink items in this chain.
    /// </summary>
    private Dictionary<int, List<(ParsedChain Chain, ParsedItem Item, int Amount)>> BuildFuelMap(List<ParsedItem> chainItems)
    {
        var sinkItems = chainItems.Where(i => i.IsSink && i.SinkRequirementConfigKeys != null && !IsSinkSuppressed(i)).ToList();
        if (sinkItems.Count == 0) return new();

        // Build ConfigKey → (chain, item) lookup
        var configKeyToItem = new Dictionary<string, (ParsedChain Chain, ParsedItem Item)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var item in chain.Items)
                if (!string.IsNullOrEmpty(item.NumericConfigKey))
                    configKeyToItem.TryAdd(item.NumericConfigKey, (chain, item));

        var map = new Dictionary<int, List<(ParsedChain, ParsedItem, int)>>();

        foreach (var sinkItem in sinkItems)
        {
            var list = new List<(ParsedChain, ParsedItem, int)>();
            foreach (var reqKey in sinkItem.SinkRequirementConfigKeys!)
            {
                if (configKeyToItem.TryGetValue(reqKey, out var match))
                {
                    int amount = sinkItem.SinkRequirementAmounts != null
                        && sinkItem.SinkRequirementAmounts.TryGetValue(reqKey, out var amt) ? amt : 1;
                    list.Add((match.Chain, match.Item, amount));
                }
            }
            if (list.Count > 0)
                map[sinkItem.Level] = list;
        }

        return map;
    }

    private string BuildFuelCell(ParsedItem item, Dictionary<int, List<(ParsedChain Chain, ParsedItem Item, int Amount)>> fuelMap)
    {
        if (!fuelMap.TryGetValue(item.Level, out var requirements)) return "{{Dash}}";

        var parts = new List<string>();
        foreach (var (reqChain, reqItem, amount) in requirements)
        {
            var template = $"{{{{Item|{reqChain.DisplayName}|{reqItem.Level}}}}}";
            parts.Add(amount > 1 ? $"{amount}x {template}" : template);
        }

        return parts.Count > 0 ? string.Join("<br>", parts) : "{{Dash}}";
    }

    // ── OrderFeatures cells (Distillation Apparatus / Vending Machine style) ────

    /// <summary>
    /// Builds the Fuel cell for an OrderFeatures item: groups requirements by slot
    /// position across tasks, dedupes alternative chains per slot, joins alternatives
    /// with "or" within a slot and slots with "+". Each chain entry includes its
    /// required level (single → `{{Item|name|level}}`, range → `{{Item/Group|...}}`).
    /// </summary>
    private string BuildOrderFuelCell(ParsedItem item)
    {
        if (item.OrderTasks is not { Count: > 0 } tasks) return "{{Dash}}";

        int maxSlots = tasks.Max(t => t.Required.Count);
        if (maxSlots == 0) return "{{Dash}}";

        var slotGroups = new List<List<string>>();
        for (int slot = 0; slot < maxSlots; slot++)
        {
            // chainName → unique levels seen across tasks at this slot
            var byChain = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>(); // first-seen order
            foreach (var t in tasks)
            {
                if (slot >= t.Required.Count) continue;
                var (itemType, _) = t.Required[slot];
                var chainName = _data.ResolveChainDisplayName(_data.ResolveChainKeyFromItemType(itemType)) ?? itemType;
                int level = DataService.GetLevelFromItemType(itemType);
                if (!byChain.TryGetValue(chainName, out var levels))
                {
                    byChain[chainName] = levels = new SortedSet<int>();
                    order.Add(chainName);
                }
                if (level > 0) levels.Add(level);
            }
            var alternatives = order.Select(name => FormatLeveledChain(name, byChain[name])).ToList();
            if (alternatives.Count > 0) slotGroups.Add(alternatives);
        }

        if (slotGroups.Count == 0) return "{{Dash}}";

        var lines = new List<string>();
        for (int g = 0; g < slotGroups.Count; g++)
        {
            if (g > 0) lines.Add("+");
            for (int a = 0; a < slotGroups[g].Count; a++)
                lines.Add(a == 0 ? slotGroups[g][a] : $"or {slotGroups[g][a]}");
        }
        return string.Join("<br>", lines);
    }

    /// <summary>Builds the Drops cell for an OrderFeatures item — unique reward chains × levels across all tasks.</summary>
    private string BuildOrderDropsCell(ParsedItem item)
    {
        if (item.OrderTasks is not { Count: > 0 } tasks) return "{{Dash}}";

        var byChain = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var t in tasks)
        {
            foreach (var (itemType, _) in t.Rewards)
            {
                var chainName = _data.ResolveChainDisplayName(_data.ResolveChainKeyFromItemType(itemType)) ?? itemType;
                int level = DataService.GetLevelFromItemType(itemType);
                if (!byChain.TryGetValue(chainName, out var levels))
                {
                    byChain[chainName] = levels = new SortedSet<int>();
                    order.Add(chainName);
                }
                if (level > 0) levels.Add(level);
            }
        }
        if (order.Count == 0) return "{{Dash}}";

        var parts = order.Select(name => FormatLeveledChain(name, byChain[name]));
        return string.Join("<br>", parts);
    }

    /// <summary>Formats a chain reference with its required levels: single level → `{{Item|name|N}}`, range → `{{Item/Group|name|max|min=min|max=max}}`.</summary>
    private static string FormatLeveledChain(string chainName, SortedSet<int> levels)
    {
        if (levels.Count == 0)
            return $"{{{{Item/nolevel|{chainName}}}}}";
        if (levels.Count == 1)
            return $"{{{{Item|{chainName}|{levels.Min}}}}}";
        return $"{{{{Item/Group|{chainName}|{levels.Max}|min={levels.Min}|max={levels.Max}}}}}";
    }

    /// <summary>Drops per Task — typical reward count per task (sum of amounts in first task with rewards).</summary>
    private string BuildOrderDropsPerTaskCell(ParsedItem item)
    {
        if (item.OrderTasks is not { Count: > 0 } tasks) return "{{Dash}}";

        // Use the most common total-per-task; if all tasks have same total, show single number.
        var totals = tasks.Where(t => t.Rewards.Count > 0)
            .Select(t => t.Rewards.Sum(r => r.Amount))
            .ToList();
        if (totals.Count == 0) return "{{Dash}}";

        int min = totals.Min(), max = totals.Max();
        return min == max ? min.ToString() : $"{min} - {max}";
    }

    // ── Transforms To ─────────────────────────────────────────────────

    private string BuildTransformsToCell(ParsedItem item)
    {
        if (!item.IsSink || string.IsNullOrEmpty(item.SinkRewardItemType))
            return "{{Dash}}";

        // Find chain + item for the reward ItemType
        foreach (var chain in _data.Chains)
            foreach (var ci in chain.Items)
                if (string.Equals(ci.ItemType, item.SinkRewardItemType, StringComparison.OrdinalIgnoreCase))
                    return $"{{{{Item|{chain.DisplayName}|{ci.Level}}}}}";

        return "{{Dash}}";
    }

    /// <summary>Returns the variant letter (A, B, C, ...) if the transform target (sink
    /// reward OR cross-chain merge result) is at a level with multiple variants, or null.</summary>
    private string? GetTransformsToVariantLabel(ParsedItem item, ParsedChain currentChain)
    {
        string? Resolve(string targetItemType, bool requireDifferentChain)
        {
            foreach (var chain in _data.Chains)
            {
                if (requireDifferentChain && chain == currentChain) continue;

                foreach (var ci in chain.Items)
                    if (string.Equals(ci.ItemType, targetItemType, StringComparison.OrdinalIgnoreCase))
                    {
                        var targetLevelItems = chain.Items
                            .Where(i => i.Level == ci.Level)
                            .OrderBy(i => i.ItemType)
                            .ToList();
                        if (targetLevelItems.Count > 1)
                        {
                            int idx = targetLevelItems.FindIndex(i =>
                                string.Equals(i.ItemType, targetItemType, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0)
                                return ((char)('A' + idx)).ToString();
                        }
                        return null;
                    }
            }
            return null;
        }

        if (item.IsSink && !string.IsNullOrEmpty(item.SinkRewardItemType))
        {
            var lbl = Resolve(item.SinkRewardItemType!, requireDifferentChain: false);
            if (lbl != null) return lbl;
        }

        if (!string.IsNullOrEmpty(item.MergeResultItemType))
            return Resolve(item.MergeResultItemType!, requireDifferentChain: true);

        return null;
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
                // Skip dev/test items (Tag "Test" → IsTestTag). Same filter Lua export uses.
                // Without this, primary PoolToys chain (3750-3768, Test tag) creates a phantom
                // {{Item|PoolToys|6}} entry next to {{Item|Pool Toys|6}} from Ver2PoolToys.
                // The two PoolToys variants share LegacyEvent + TempSkatie tags — Test is the
                // only reliable differentiator (don't filter LegacyEvent, would hit Ver2 too).
                if (item.IsTestTag) continue;
                if (!item.IsSink || item.SinkRequirementConfigKeys == null || IsSinkSuppressed(item)) continue;

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

    // ── Decay Odds cell ─────────────────────────────────────────────────

    /// <summary>Build Decay Odds cell for a single item — grouped percentages per target chain.</summary>
    private string BuildDecayOddsCell(ParsedItem item)
    {
        if (item.DecayAfterLastCycleOdds == null || item.DecayAfterLastCycleOdds.Count == 0)
            return "{{Dash}}";

        // Build ItemType → chain display name lookup
        var itemTypeToChain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var ci in chain.Items)
                if (!string.IsNullOrEmpty(ci.ItemType))
                    itemTypeToChain.TryAdd(ci.ItemType, chain.DisplayName);

        // Group odds by target chain, preserving insertion order
        var chainOdds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var chainOrder = new List<string>();

        foreach (var (itemType, odds) in item.DecayAfterLastCycleOdds)
        {
            string chainName = itemTypeToChain.TryGetValue(itemType, out var cn)
                ? cn : ResolveChainName(itemType);

            if (!chainOdds.ContainsKey(chainName))
            {
                chainOrder.Add(chainName);
                chainOdds[chainName] = 0;
            }
            chainOdds[chainName] += odds;
        }

        return string.Join("<br>", chainOrder.Select(cn => $"{chainOdds[cn]:0.##}%"));
    }

    /// <summary>Aggregated Decay Odds from all items at a level.</summary>
    private string BuildDecayOddsCellAggregated(List<ParsedItem> levelItems)
    {
        foreach (var item in levelItems)
        {
            var cell = BuildDecayOddsCell(item);
            if (cell != "{{Dash}}") return cell;
        }
        return "{{Dash}}";
    }

    // ── Variant sub-row helpers ────────────────────────────────────────

    /// <summary>Emit columns from Drops onwards for a variant sub-row, handling Variant insertion, rowspan, and decay expansion.</summary>
    private void EmitSplitColumns(StringBuilder sb, int v, int vc, ParsedItem vi, ParsedItem primary,
        List<ParsedItem> levelItems, HashSet<string> diff,
        bool showDrops, bool showDropValues, bool showFuelFor, bool showDecaysInto,
        bool showDecayOdds, bool showChargeTime, bool showRechargeTime, bool showSpeedUpCost,
        bool hasVariants, string? firstSplitCol,
        Dictionary<string, List<(ParsedChain Chain, ParsedItem Item)>> fuelForMap,
        bool showOrderDrops = false, bool showDropsPerTask = false,
        int dg = 0, int dcCount = 1, int totalRows = 0, List<DecayChainGroup>? decayGroups = null,
        bool hasAnyDecayExpansion = false,
        string? nestedDecayTable = null,
        bool skipDecayColumns = false)
    {
        if (totalRows == 0) totalRows = vc;
        bool isFirstRow = dg == 0 && v == 0;
        bool hasNestedTable = nestedDecayTable != null;

        // Drops
        if (hasVariants && firstSplitCol == "Drops") sb.AppendLine($"| {(char)('A' + v)}");
        if (showDrops)
        {
            if (diff.Contains("Drops"))
                sb.AppendLine($"| {BuildDropsCellWithMerge(vi)}");
            else if (isFirstRow)
                sb.AppendLine($"| rowspan = {totalRows} | {BuildDropsCellAggregated(levelItems)}");
        }

        // OrderFeatures Drops + Drops per Task (always rowspan — same across variants)
        if (showOrderDrops && isFirstRow)
            sb.AppendLine($"| rowspan = {totalRows} | {BuildOrderDropsCell(primary)}");
        if (showDropsPerTask && isFirstRow)
            sb.AppendLine($"| rowspan = {totalRows} | {BuildOrderDropsPerTaskCell(primary)}");

        // Drop Values
        if (hasVariants && firstSplitCol == "DropValues") sb.AppendLine($"| {(char)('A' + v)}");
        if (showDropValues)
        {
            if (diff.Contains("DropValues"))
                sb.AppendLine($"| {BuildDropValuesCellLiteral(vi)}");
            else if (isFirstRow)
                sb.AppendLine($"| rowspan = {totalRows} | {BuildDropValuesCell(primary)}");
        }

        // Fuel For (not splittable — always rowspan)
        if (showFuelFor)
        {
            if (isFirstRow)
                sb.AppendLine($"| rowspan = {totalRows} | {BuildFuelForCell(primary, fuelForMap)}");
        }

        // Decays Into + Decay Odds — skip if already emitted in header row
        if (skipDecayColumns)
        {
            // Decay columns handled by nested table in header — nothing to emit here
        }
        else if (hasNestedTable)
        {
            // Nested table replaces Decays Into + Decay Odds columns (fallback: data-row placement)
            if (v == 0)
            {
                int colspan = hasAnyDecayExpansion ? 3 : (showDecayOdds ? 2 : 1);
                if (hasVariants && (firstSplitCol == "DecaysInto" || firstSplitCol == "DecayOdds"))
                    colspan++;
                sb.AppendLine($"| rowspan = {totalRows} colspan = {colspan} class = \"padding0 fixedCellHeight valignTop\" style = \"height: 0px\" |\n{nestedDecayTable}");
            }
        }
        else
        {
            // Decays Into
            if (hasVariants && firstSplitCol == "DecaysInto") sb.AppendLine($"| {(char)('A' + v)}");
            if (showDecaysInto)
            {
                if (decayGroups != null)
                {
                    // Normal decay expansion: rowspan per chain group
                    if (v == 0)
                        sb.AppendLine($"| rowspan = {vc} | {BuildDecaysIntoCellForGroup(decayGroups[dg])}");
                }
                else if (diff.Contains("DecaysInto"))
                    sb.AppendLine($"| {BuildDecaysIntoCell(vi)}");
                else if (isFirstRow)
                    sb.AppendLine($"| rowspan = {totalRows} | {BuildDecaysIntoCellAggregated(levelItems)}");
            }

            // Decay Odds (2 sub-columns when hasAnyDecayExpansion: Variant + Odds)
            if (hasVariants && firstSplitCol == "DecayOdds") sb.AppendLine($"| {(char)('A' + v)}");
            if (showDecayOdds)
            {
                if (decayGroups != null)
                {
                    // Normal decay expansion: individual target at index v → 2 cells
                    var targetOdds = GetDecayTargetOddsAtIndex(vi, decayGroups[dg].ChainDisplayName, v);
                    bool allSameOdds = levelItems.All(li =>
                        GetDecayTargetOddsAtIndex(li, decayGroups[dg].ChainDisplayName, v) == targetOdds);
                    if (allSameOdds)
                    {
                        if (v == 0)
                        {
                            sb.AppendLine($"| rowspan = {vc} | {(char)('A' + v)}");
                            sb.AppendLine($"| rowspan = {vc} | {targetOdds}");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"| {(char)('A' + v)}");
                        sb.AppendLine($"| {targetOdds}");
                    }
                }
                else if (hasAnyDecayExpansion)
                {
                    // Non-expanded level but chain has expansion: colspan=2
                    if (diff.Contains("DecayOdds"))
                        sb.AppendLine($"| colspan = 2 | {BuildDecayOddsCell(vi)}");
                    else if (isFirstRow)
                        sb.AppendLine($"| colspan = 2 rowspan = {totalRows} | {BuildDecayOddsCellAggregated(levelItems)}");
                }
                else
                {
                    if (diff.Contains("DecayOdds"))
                        sb.AppendLine($"| {BuildDecayOddsCell(vi)}");
                    else if (isFirstRow)
                        sb.AppendLine($"| rowspan = {totalRows} | {BuildDecayOddsCellAggregated(levelItems)}");
                }
            }
        }

        // Charge Time
        if (hasVariants && firstSplitCol == "ChargeTime") sb.AppendLine($"| {(char)('A' + v)}");
        if (showChargeTime)
        {
            if (diff.Contains("ChargeTime"))
                sb.AppendLine($"| {BuildChargeTimeCellLiteral(vi)}");
            else if (isFirstRow)
                sb.AppendLine($"| rowspan = {totalRows} | {BuildChargeTimeCell(primary)}");
        }

        // Recharge Time
        if (hasVariants && firstSplitCol == "RechargeTime") sb.AppendLine($"| {(char)('A' + v)}");
        if (showRechargeTime)
        {
            if (diff.Contains("RechargeTime"))
                sb.AppendLine($"| {BuildRechargeTimeCellLiteral(vi)}");
            else if (isFirstRow)
                sb.AppendLine($"| rowspan = {totalRows} | {BuildRechargeTimeCell(primary)}");
        }

        // Speed Up Cost
        if (hasVariants && firstSplitCol == "SpeedUpCost") sb.AppendLine($"| {(char)('A' + v)}");
        if (showSpeedUpCost)
        {
            if (diff.Contains("SpeedUpCost"))
                sb.AppendLine($"| {BuildSpeedUpCellLiteral(vi)}");
            else if (isFirstRow)
            {
                if (levelItems.Any(i => i.IsGenerator || (i.IsSpawner && i.SpawnDelayMs > 0)))
                    sb.AppendLine($"| rowspan = {totalRows} | {{{{Gems}}}} {{{{#Invoke:Items|GetItemSkipPriceFromChainName|{{{{#var:Level}}}}}}}}");
                else
                    sb.AppendLine($"| rowspan = {totalRows} | {{{{Dash}}}}");
            }
        }
    }

    /// <summary>Whether a generator produces all items in a single charge (no Drop Values needed).</summary>
    /// <remarks>
    /// Total drops per charge = ActivationAmountInCycle × HowManyGeneratedInCycle.
    /// Single charge when that equals or exceeds StorageMax.
    /// </remarks>
    private bool IsSingleCharge(ParsedItem item)
    {
        if (!item.IsGenerator || item.StorageMax <= 0 || item.ActivationAmountInCycle <= 0)
            return false;
        int dropsPerCharge = item.ActivationAmountInCycle * item.HowManyGeneratedInCycle;
        return dropsPerCharge >= item.StorageMax;
    }

    /// <summary>Build Drops cell with single-charge count merged in (e.g., "15× {{Item|...}}").</summary>
    private string BuildDropsCellWithMerge(ParsedItem item)
    {
        if (IsSingleCharge(item) && item.DropOdds != null && item.DropOdds.Count > 0)
        {
            var parts = new List<string>();
            if (item.Level >= 5) parts.Add("{{XPDrop}}");
            int totalDrops = item.ActivationAmountInCycle * item.HowManyGeneratedInCycle;
            var grouped = GroupDropsByChain(item.DropOdds);
            foreach (var group in grouped)
                parts.Add($"{totalDrops}× {FormatDropGroup(group)}");
            return parts.Count > 0 ? string.Join("<br>", parts) : "{{Dash}}";
        }
        return BuildDropsCell(item);
    }

    /// <summary>Build Drop Values cell with literal values for per-variant output.</summary>
    /// <remarks>
    /// Always returns {{DropValuesTable|dropsPerCharge|charges}} — no single-charge elimination.
    /// In variant mode, quantities differ per variant so Drop Values must always show the template.
    /// </remarks>
    private string BuildDropValuesCellLiteral(ParsedItem item)
    {
        if (item.IsGenerator && item.ActivationAmountInCycle > 0 && item.StorageMax > 0)
        {
            int dropsPerCharge = item.ActivationAmountInCycle * item.HowManyGeneratedInCycle;
            int charges = item.StorageMax / dropsPerCharge;
            return $"{{{{DropValuesTable|{dropsPerCharge}|{charges}}}}}";
        }
        if (item.IsSpawner && item.SpawnStorageMax > 0)
            return $"{{{{DropValuesTable|1|{item.SpawnStorageMax}}}}}";
        return "{{Dash}}";
    }

    /// <summary>Build Charge Time cell with literal value for per-variant output.</summary>
    private string BuildChargeTimeCellLiteral(ParsedItem item)
    {
        if (item.IsGenerator && item.FirstCycleStartDelayMs >= 5000)
            return $"{{{{TimeValuesTable|{item.FirstCycleStartDelayMs}}}}}";
        return "{{Dash}}";
    }

    /// <summary>Build Recharge Time cell with literal value for per-variant output.</summary>
    private string BuildRechargeTimeCellLiteral(ParsedItem item)
    {
        if (item.IsGenerator && item.RechargeTimeMs >= 1000)
            return $"{{{{TimeValuesTable|{item.RechargeTimeMs}}}}}";
        if (item.IsSpawner && item.SpawnDelayMs >= 1000)
            return $"{{{{TimeValuesTable|{item.SpawnDelayMs}}}}}";
        return "{{Dash}}";
    }

    /// <summary>Build Speed Up Cost cell with literal value for per-variant output.</summary>
    private string BuildSpeedUpCellLiteral(ParsedItem item)
    {
        if (item.IsGenerator || (item.IsSpawner && item.SpawnDelayMs > 0))
        {
            if (item.SpeedUpCostGems is > 0)
                return $"{{{{Gems}}}} {item.SpeedUpCostGems.Value}";
            return "{{Dash}}";
        }
        return "{{Dash}}";
    }

    /// <summary>Get variant cell content for comparison or output.</summary>
    private string GetVariantCellContent(string colId, ParsedItem variant)
    {
        return colId switch
        {
            // Compare drop TARGETS only (ignoring quantities) — variants with same targets but
            // different StorageMax/Amt should rowspan Drops, with differences shown in Drop Values.
            "Drops" => BuildDropsCell(variant),
            "DropValues" => BuildDropValuesCellLiteral(variant),
            "DecaysInto" => BuildDecaysIntoCell(variant),
            "DecayOdds" => BuildDecayOddsCell(variant),
            "ChargeTime" => BuildChargeTimeCellLiteral(variant),
            "RechargeTime" => BuildRechargeTimeCellLiteral(variant),
            "SpeedUpCost" => BuildSpeedUpCellLiteral(variant),
            _ => ""
        };
    }

    /// <summary>Determine which splittable columns differ between variants at a level.</summary>
    private HashSet<string> GetDifferingColumns(List<ParsedItem> levelItems)
    {
        var differing = new HashSet<string>();
        if (levelItems.Count <= 1) return differing;

        string[] splitCandidates = { "Drops", "DropValues", "DecaysInto", "DecayOdds", "ChargeTime", "RechargeTime", "SpeedUpCost" };
        var first = levelItems[0];
        foreach (var col in splitCandidates)
        {
            var firstVal = GetVariantCellContent(col, first);
            if (levelItems.Skip(1).Any(item => GetVariantCellContent(col, item) != firstVal))
                differing.Add(col);
        }
        return differing;
    }

    // ── Decay expansion helpers ────────────────────────────────────────

    /// <summary>Get distinct decay target chain groups from level items' DecayAfterLastCycleOdds.</summary>
    private List<DecayChainGroup> GetDecayChainGroups(List<ParsedItem> levelItems)
    {
        var itemTypeToChain = new Dictionary<string, (string ChainName, int Level)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var ci in chain.Items)
                if (!string.IsNullOrEmpty(ci.ItemType))
                    itemTypeToChain.TryAdd(ci.ItemType, (chain.DisplayName, ci.Level));

        var groups = new Dictionary<string, DecayChainGroup>(StringComparer.OrdinalIgnoreCase);
        var groupTargets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var item in levelItems)
        {
            if (item.DecayAfterLastCycleOdds == null) continue;
            foreach (var (itemType, _) in item.DecayAfterLastCycleOdds)
            {
                string chainName;
                int level;
                if (itemTypeToChain.TryGetValue(itemType, out var match))
                    (chainName, level) = match;
                else
                {
                    chainName = ResolveChainName(itemType);
                    level = _data.ResolveLevel(itemType, _wikiMapping);
                }

                if (!groups.TryGetValue(chainName, out var group))
                {
                    group = new DecayChainGroup { ChainDisplayName = chainName };
                    groups[chainName] = group;
                    groupTargets[chainName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    order.Add(chainName);
                }
                if (!group.Levels.Contains(level))
                    group.Levels.Add(level);
                groupTargets[chainName].Add(itemType);
            }
        }

        foreach (var cn in order)
            groups[cn].TargetVariantCount = groupTargets[cn].Count;

        return order.Select(cn => groups[cn]).ToList();
    }

    /// <summary>Build Decays Into cell for a specific decay chain group (Item or Item/Group range).</summary>
    private string BuildDecaysIntoCellForGroup(DecayChainGroup group)
    {
        var levels = group.Levels.OrderBy(l => l).ToList();
        var name = group.ChainDisplayName;
        if (levels.Count == 0) return "{{Dash}}";
        if (levels.Count == 1)
            return $"{{{{Item|{name}|{levels[0]}}}}}";
        return $"{{{{Item/Group|{name}|{levels.Max()}|min={levels.Min()}|max={levels.Max()}}}}}";
    }

    /// <summary>Build aggregated Decays Into cell from all decay chain groups.</summary>
    private string BuildDecaysIntoCellFromGroups(List<DecayChainGroup> groups)
    {
        var parts = groups.Select(g => BuildDecaysIntoCellForGroup(g)).ToList();
        return parts.Count > 0 ? string.Join("<br>", parts) : "{{Dash}}";
    }

    /// <summary>
    /// Build a nested wiki table for Decays Into + Decay Odds when target variants exist.
    /// Each decay chain group gets a rowspan cell for the item, with individual rows for each target variant + odds.
    /// </summary>
    private string BuildDecayNestedTable(List<DecayChainGroup> decayGroups, List<ParsedItem> levelItems)
    {
        // Use the first item with DecayAfterLastCycleOdds as the odds source
        // (odds are typically the same across all source variants)
        var oddsSource = levelItems.FirstOrDefault(i =>
            i.DecayAfterLastCycleOdds != null && i.DecayAfterLastCycleOdds.Count > 0);

        // Build ItemType → (chain display name, level) lookup
        var itemTypeToChain = new Dictionary<string, (string ChainName, int Level)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var ci in chain.Items)
                if (!string.IsNullOrEmpty(ci.ItemType))
                    itemTypeToChain.TryAdd(ci.ItemType, (chain.DisplayName, ci.Level));

        var nsb = new StringBuilder();
        nsb.AppendLine("{| class = \"article-table invisibleTable\" style = \"margin: 0; height: 100%;\"");
        // Two-row header with fixed height to prevent height:100% stretching
        nsb.AppendLine("|- style = \"height: 40px;\"");
        nsb.AppendLine("! rowspan = 2 | Decays Into !! colspan = 2 | Decay Odds");
        nsb.AppendLine("|- style = \"height: 40px;\"");
        nsb.AppendLine("! Variant !! Odds");

        foreach (var group in decayGroups)
        {
            int tvCount = (group.TargetVariantCount > 1 && group.Levels.Count == 1)
                ? group.TargetVariantCount : 1;

            // Get ordered target entries for this group from the odds source
            var groupEntries = new List<(string ItemType, double Odds)>();
            if (oddsSource?.DecayAfterLastCycleOdds != null)
            {
                groupEntries = oddsSource.DecayAfterLastCycleOdds
                    .Where(kv =>
                    {
                        string cn = itemTypeToChain.TryGetValue(kv.Key, out var m) ? m.ChainName : ResolveChainName(kv.Key);
                        return string.Equals(cn, group.ChainDisplayName, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
            }

            for (int tv = 0; tv < tvCount; tv++)
            {
                nsb.AppendLine("|-");

                // Decays Into: rowspan per group
                if (tv == 0)
                    nsb.AppendLine($"| rowspan = {tvCount} | {BuildDecaysIntoCellForGroup(group)}");

                // Variant + Odds
                double odds = (tv < groupEntries.Count) ? groupEntries[tv].Odds : 0;
                nsb.AppendLine($"| {(char)('A' + tv)}");
                nsb.AppendLine($"| {odds:0.##}%");
            }
        }

        nsb.Append("|}");
        return nsb.ToString();
    }

    /// <summary>Get the odds for the target at a specific index within a decay chain group.</summary>
    private string GetDecayTargetOddsAtIndex(ParsedItem variant, string groupChainName, int index)
    {
        if (variant.DecayAfterLastCycleOdds == null) return "{{Dash}}";

        var itemTypeToChain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var ci in chain.Items)
                if (!string.IsNullOrEmpty(ci.ItemType))
                    itemTypeToChain.TryAdd(ci.ItemType, chain.DisplayName);

        var groupEntries = variant.DecayAfterLastCycleOdds
            .Where(kv =>
            {
                string cn = itemTypeToChain.TryGetValue(kv.Key, out var name) ? name : ResolveChainName(kv.Key);
                return string.Equals(cn, groupChainName, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(kv => kv.Key)
            .ToList();

        if (index >= groupEntries.Count) return "{{Dash}}";

        return $"{groupEntries[index].Value:0.##}%";
    }

    /// <summary>Build Decay Odds cell showing individual target items and their odds for a specific chain group.</summary>
    private string BuildDecayOddsCellForGroup(ParsedItem variant, string groupChainName)
    {
        if (variant.DecayAfterLastCycleOdds == null) return "{{Dash}}";

        var itemTypeToChain = new Dictionary<string, (string ChainName, int Level)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var ci in chain.Items)
                if (!string.IsNullOrEmpty(ci.ItemType))
                    itemTypeToChain.TryAdd(ci.ItemType, (chain.DisplayName, ci.Level));

        var entries = new List<(string DisplayName, int Level, double Odds)>();
        foreach (var (itemType, odds) in variant.DecayAfterLastCycleOdds)
        {
            string chainName;
            int level;
            string displayName;
            if (itemTypeToChain.TryGetValue(itemType, out var match))
            {
                chainName = match.ChainName;
                displayName = match.ChainName;
                level = match.Level;
            }
            else
            {
                chainName = ResolveChainName(itemType);
                displayName = chainName;
                level = _data.ResolveLevel(itemType, _wikiMapping);
            }

            if (string.Equals(chainName, groupChainName, StringComparison.OrdinalIgnoreCase))
                entries.Add((displayName, level, odds));
        }

        if (entries.Count == 0) return "{{Dash}}";

        var parts = entries.OrderBy(e => e.Level)
            .Select(e => $"{{{{Item|{e.DisplayName}|{e.Level}}}}} {e.Odds:0.##}%");

        return string.Join("<br>", parts);
    }

    // ── Order Tasks wikitext ─────────────────────────────────────────
    /// <summary>
    /// Generates the "Tasks" section wikitext for an item that has OrderFeatures (e.g.
    /// Distillation Apparatus, Vending Machine). Mimics the hand-written style on
    /// Distillation Apparatus wiki page: heading, "fixed order" intro, table with
    /// `Task | Required... | Reward` columns. Range-collapses consecutive identical
    /// (Required, Rewards) tuples using cumulative integer slot weights.
    /// </summary>
    public string? GenerateOrderTasksTable(ParsedItem item)
    {
        if (!item.IsOrder || item.OrderTasks is not { Count: > 0 } tasks) return null;

        // Range collapsing only works when ALL weights are positive integers; otherwise
        // each task gets its own row labeled by 1-based index.
        bool allIntWeights = tasks.All(t => t.OddsWeight > 0 && Math.Abs(t.OddsWeight - Math.Round(t.OddsWeight)) < 1e-6);

        // Determine column count for "Required" — max # of required slots across tasks.
        int requiredCols = tasks.Max(t => t.Required.Count);
        if (requiredCols == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("=== Tasks ===");
        sb.AppendLine("The tasks are in a fixed order and repeat once a player completes all the listed tasks.");
        sb.AppendLine("{| class=\"article-table\"");
        sb.AppendLine("|+ <u>{{PAGENAME}}</u>");
        sb.AppendLine("! Task");
        if (requiredCols == 1)
            sb.AppendLine("! Item");
        else
            for (int i = 1; i <= requiredCols; i++)
                sb.AppendLine($"! Item {i}");
        sb.AppendLine("! Reward");

        int slotStart = 1;
        for (int i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            int slotsInThisTask = allIntWeights ? (int)Math.Round(t.OddsWeight) : 1;
            int slotEnd = slotStart + slotsInThisTask - 1;

            string label = slotsInThisTask == 1
                ? slotStart.ToString()
                : $"{slotStart} - {slotEnd}";

            sb.AppendLine("|-");
            sb.AppendLine($"! {label}");

            // Required cells (pad with empty cells when this task has fewer required items)
            for (int c = 0; c < requiredCols; c++)
            {
                if (c < t.Required.Count)
                {
                    var (it, amt) = t.Required[c];
                    sb.AppendLine($"| {FormatItemRef(it, amt)}");
                }
                else
                {
                    sb.AppendLine("|");
                }
            }

            // Reward cell (multi-item join with <br>)
            if (t.Rewards.Count == 0)
            {
                sb.AppendLine("| {{Dash}}");
            }
            else
            {
                var rewardParts = t.Rewards.Select(r => FormatItemRef(r.ItemType, r.Amount));
                sb.AppendLine($"| {string.Join("<br>", rewardParts)}");
            }

            slotStart = slotEnd + 1;
        }

        sb.AppendLine("|}");
        return sb.ToString();
    }

    /// <summary>
    /// Post-processes raw wikitext fetched from the wiki Game Tips section. Four cleanups:
    /// (1) Ensures "* " spacing on list markers (`*foo` → `* foo`, multi-level too).
    /// (2) Renames `{{Item/nolevel|X}}` → `{{Item/Group|X|MAX}}` (with chain max level when known).
    /// (3) Collapses `{{Item/Group|Name|N}} (L2-7)` → `{{Item/Group|Name|7|min=2|max=7}}` (positional
    /// level is corrected to the bracket range max). Single-level `(L5)` collapses to `{{Item|Name|5}}`.
    /// (4) Plain `[[Item Name]]` link → `{{Item/Group|Item Name|MAX}}` when name matches a known chain.
    /// Piped links `[[X|Y]]` are left alone.
    /// </summary>
    public string PostProcessGameplayTips(string wikitext, string? currentChainName = null)
    {
        if (string.IsNullOrEmpty(wikitext)) return wikitext;
        var nameToMaxLevel = BuildNameToMaxLevelDict();

        // (1) "* foo" spacing
        wikitext = Regex.Replace(wikitext, @"^(\*+)(?=\S)", "$1 ", RegexOptions.Multiline);

        // (2) Item/nolevel → Item/Group (+max level when matched)
        wikitext = Regex.Replace(wikitext, @"\{\{Item/nolevel\|([^|}]+)\}\}", m =>
        {
            var name = m.Groups[1].Value.Trim();
            return nameToMaxLevel.TryGetValue(name, out var maxLvl)
                ? $"{{{{Item/Group|{name}|{maxLvl}}}}}"
                : $"{{{{Item/Group|{name}}}}}";
        });

        // (3) Item/Group ... (L2-7) → min/max collapse
        wikitext = Regex.Replace(wikitext,
            @"\{\{Item/Group\|([^|}]+)\|\d+\}\}\s*\(L(\d+)(?:-(\d+))?\)",
            m =>
            {
                var name = m.Groups[1].Value.Trim();
                int min = int.Parse(m.Groups[2].Value);
                int max = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : min;
                return min == max
                    ? $"{{{{Item|{name}|{min}}}}}"
                    : $"{{{{Item/Group|{name}|{max}|min={min}|max={max}}}}}";
            });

        // (4) [[Plain Name]] → {{Item/Group|Plain Name|MAX}} when known
        wikitext = Regex.Replace(wikitext, @"\[\[([^\[\]|]+)\]\]", m =>
        {
            var name = m.Groups[1].Value.Trim();
            return nameToMaxLevel.TryGetValue(name, out var maxLvl)
                ? $"{{{{Item/Group|{name}|{maxLvl}}}}}"
                : m.Value;
        });

        // (5) Swap CURRENT chain name → {{PAGENAME}} in Item-family templates. This means the
        // generated output uses the wiki placeholder, which Hardcode-name mode (handled by caller)
        // can swap back to the literal name in one pass.
        if (!string.IsNullOrEmpty(currentChainName))
        {
            wikitext = Regex.Replace(wikitext,
                $@"(\{{\{{Item(?:/Group|/nolevel|/Icon)?\|){Regex.Escape(currentChainName)}(?=\||\}}\}})",
                m => m.Groups[1].Value + "{{PAGENAME}}");
        }

        // (6) Strip "|1" from single-level Item templates. {{Item/Group|Name|1}} for a chain
        // that has only one level is redundant — wiki default level is 1. Reduces visual noise.
        wikitext = Regex.Replace(wikitext,
            @"(\{\{Item(?:/Group|/nolevel|/Icon)?\|)([^|}]+)\|1\}\}",
            m =>
            {
                var name = m.Groups[2].Value.Trim();
                // PAGENAME is the current chain — check its level count
                string lookupName = name == "{{PAGENAME}}" ? (currentChainName ?? "") : name;
                if (!string.IsNullOrEmpty(lookupName)
                    && nameToMaxLevel.TryGetValue(lookupName, out var maxL)
                    && maxL == 1)
                {
                    return $"{m.Groups[1].Value}{m.Groups[2].Value}}}}}";
                }
                return m.Value;
            });

        return wikitext;
    }

    /// <summary>
    /// Builds a (wiki chain name → max level) lookup across all loaded chains. Wiki-resolved name
    /// is used (so wiki mapping overrides win). Helper for <see cref="PostProcessGameplayTips"/>.
    /// </summary>
    private Dictionary<string, int> BuildNameToMaxLevelDict()
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
        {
            var firstItem = chain.Items.FirstOrDefault();
            string name = firstItem != null && !string.IsNullOrEmpty(firstItem.ItemType)
                ? _data.ResolveChainDisplayNameFromItemType(firstItem.ItemType, _wikiMapping)
                : chain.DisplayName;
            int maxLvl = chain.Items.Count > 0 ? chain.Items.Max(i => i.Level) : 0;
            if (maxLvl > 0 && !string.IsNullOrEmpty(name))
                dict[name] = maxLvl;
        }
        return dict;
    }

    /// <summary>
    /// Resolves the wiki page name for a chain — applies wiki mapping override on top of
    /// custom/JSON name resolution. Used by Infobox Section intro and Hardcode name retrofit.
    /// </summary>
    public string GetWikiChainName(ParsedChain chain)
    {
        var firstItemType = chain.Items.FirstOrDefault()?.ItemType;
        if (string.IsNullOrEmpty(firstItemType))
            return chain.DisplayName;
        return _data.ResolveChainDisplayNameFromItemType(firstItemType, _wikiMapping);
    }

    /// <summary>
    /// Item Descriptions section (== Item Descriptions ==). Emits one line per level (1..maxLevel)
    /// with item icon + Lua-resolved per-level description. Gated by presence of any non-empty
    /// game-side Description on chain items (Lua resolver would emit empty strings otherwise).
    /// When <paramref name="hardcodedName"/> is non-null, replaces {{PAGENAME}} with the literal name.
    /// </summary>
    public string? GenerateItemDescriptionsSection(ParsedChain chain, string? hardcodedName = null)
    {
        bool hasAnyDesc = chain.Items.Any(i => !string.IsNullOrEmpty(i.Description));
        if (!hasAnyDesc) return null;

        int maxLevel = chain.Items.Max(i => i.Level);
        if (maxLevel <= 0) return null;

        string nameRef = hardcodedName ?? "{{PAGENAME}}";
        string descArg = hardcodedName != null ? $"|{hardcodedName}" : "";

        var sb = new StringBuilder();
        sb.AppendLine("== Item Descriptions ==");
        for (int lvl = 1; lvl <= maxLevel; lvl++)
        {
            sb.AppendLine($"{{{{Item/Icon|{nameRef}|{lvl}}}}} {{{{#Invoke:Items|GetItemDescFromChainName|{lvl}{descArg}}}}}");
            if (lvl < maxLevel)
                sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Drop Odds section (=== Drop Odds ===). Emits when any item in chain has randomized
    /// production: DropOdds (generators), SpawnOdds (spawners), or DecayAfterLastCycleOdds
    /// (randomized end-cycle outcome). Pure ConstantProducer decay (DecayProducer / ItemProducer)
    /// is deterministic — no Drop Odds needed there.
    /// </summary>
    public string? GenerateDropOddsSection(ParsedChain chain, string? hardcodedName = null)
    {
        bool hasRandomOdds = chain.Items.Any(i =>
            (i.DropOdds != null && i.DropOdds.Count > 0) ||
            (i.SpawnOdds != null && i.SpawnOdds.Count > 0) ||
            (i.DecayAfterLastCycleOdds != null && i.DecayAfterLastCycleOdds.Count > 0));
        if (!hasRandomOdds) return null;

        string arg = hardcodedName != null ? $"|{hardcodedName}" : "";
        var sb = new StringBuilder();
        sb.AppendLine("=== Drop Odds ===");
        sb.AppendLine($"{{{{#Invoke:Items|GetItemOddsTableFromChainName{arg}}}}}");
        return sb.ToString();
    }

    /// <summary>
    /// Double Bubbles section (=== [[Double Bubble]]s ===). Emits when:
    /// (a) chain has more than 1 distinct level (single-level chains have no bubble progression to render),
    /// (b) at least one item has an active bubble feature (HasBubble + BubbleSpawnOdds > 0
    /// per memory/bubble-features-game-rule.md).
    /// </summary>
    public string? GenerateDoubleBubblesSection(ParsedChain chain, string? hardcodedName = null)
    {
        int distinctLevels = chain.Items.Select(i => i.Level).Distinct().Count();
        if (distinctLevels <= 1) return null;

        bool hasActiveBubble = chain.Items.Any(i => i.HasBubble && i.BubbleSpawnOdds > 0);
        if (!hasActiveBubble) return null;

        string arg = hardcodedName != null ? $"|{hardcodedName}" : "";
        var sb = new StringBuilder();
        sb.AppendLine("=== [[Double Bubble]]s ===");
        sb.AppendLine($"{{{{#Invoke:Items|GetItemBubbleTableFromChainName{arg}}}}}");
        return sb.ToString();
    }

    /// <summary>
    /// Uses section (== Uses ==). Emits when chain has any downstream consumption:
    /// (1) ItemType referenced in any task.Requirements across all areas,
    /// (2) ItemType referenced in another chain's OrderRequiredItems,
    /// (3) NumericConfigKey referenced in another chain's SinkRequirementConfigKeys.
    /// Indirect/transitive uses are computed by the wiki Lua solver itself
    /// ({{#Invoke:Items|GetAllItemUses}}) — this gating only decides whether to emit the section.
    /// </summary>
    public string? GenerateUsesSection(ParsedChain chain, IReadOnlyList<ParsedChain> allChains, IReadOnlyList<LuaArea>? areas, string? hardcodedName = null)
    {
        var myItemTypes = chain.Items
            .Where(i => !string.IsNullOrEmpty(i.ItemType))
            .Select(i => i.ItemType!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (myItemTypes.Count == 0) return null;

        var myConfigKeys = chain.Items
            .Where(i => !string.IsNullOrEmpty(i.NumericConfigKey))
            .Select(i => i.NumericConfigKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool usedInTask = false;
        if (areas != null)
        {
            foreach (var area in areas)
            {
                foreach (var task in area.Tasks)
                {
                    if (task.Requirements.Keys.Any(k => myItemTypes.Contains(k)))
                    {
                        usedInTask = true;
                        break;
                    }
                }
                if (usedInTask) break;
            }
        }

        bool usedAsRequirement = !usedInTask && allChains
            .Where(c => c != chain)
            .SelectMany(c => c.Items)
            .Any(i =>
                (i.IsSink && i.SinkRequirementConfigKeys != null
                    && i.SinkRequirementConfigKeys.Any(k => myConfigKeys.Contains(k))) ||
                (i.IsOrder && i.OrderRequiredItems != null
                    && i.OrderRequiredItems.Keys.Any(k => myItemTypes.Contains(k))));

        if (!usedInTask && !usedAsRequirement) return null;

        string arg = hardcodedName != null ? $"|{hardcodedName}" : "";
        var sb = new StringBuilder();
        sb.AppendLine("== Uses ==");
        sb.AppendLine("{{UsesTableFeatures}}");
        sb.AppendLine($"{{{{#Invoke:Items|GetAllItemUses{arg}}}}}");
        return sb.ToString();
    }

    /// <summary>
    /// Infobox Section intro sentence (no heading) — goes directly under the infobox on the wiki page.
    /// Three variants:
    ///   - Default: `{{Item/Group|<name>|<max>}} are items in '''''Merge Mansion'''''. They are used on the Main Board.`
    ///   - Temporary: `... are items in '''''Merge Mansion'''''. They are used on the Main Board in {{Area|<area>}}.`
    ///   - Event: `... are items in '''''Merge Mansion'''''. They are used in the {{Item/Group|{{#var:EventName}}}} Event.`
    /// Singular form ("is an item ... It is used ...") for single-level chains.
    /// </summary>
    public string? GenerateInfoboxSectionIntro(
        ParsedChain chain,
        string? hardcodedName,
        IReadOnlyList<LuaArea>? areas,
        EventService? eventService)
    {
        if (chain.Items.Count == 0) return null;

        int maxLevel = chain.Items.Max(i => i.Level);
        int distinctLevels = chain.Items.Select(i => i.Level).Distinct().Count();
        bool isMulti = distinctLevels > 1;
        string nameRef = hardcodedName ?? "{{PAGENAME}}";

        // Parenthetical chain (e.g. "Tools (Beach Shack)") — when using {{PAGENAME}} the rendered
        // page name keeps the disambiguation suffix, so we explicitly pass displayName so the
        // visible text reads "Tools" instead of "Tools (Beach Shack)". Hardcoded name is the
        // already-stripped chain name, so no extra param needed there.
        bool hasParen = nameRef == "{{PAGENAME}}"
            && chain.DisplayName.TrimEnd().EndsWith(")") && chain.DisplayName.Contains('(');
        string displayNameSuffix = hasParen ? "|displayName={{#var:DisplayTitle}}" : "";

        string subject = isMulti
            ? $"{{{{Item/Group|{nameRef}|{maxLevel}{displayNameSuffix}}}}}"
            : $"{{{{Item|{nameRef}{displayNameSuffix}}}}}";
        string verb = isMulti ? "are items" : "is an item";
        string usedPronoun = isMulti ? "They are" : "It is";

        // Classify chain — event chain takes precedence (event items are temporary by definition
        // but the event sentence is more specific).
        var sb = new StringBuilder();
        if (chain.IsEventChain && eventService?.FindEventForChain(chain) is { } evt
            && !string.IsNullOrEmpty(evt.DisplayName))
        {
            sb.Append($"{subject} {verb} in '''''Merge Mansion'''''. {usedPronoun} used in the {{{{Item/Group|{{{{#var:EventName}}}}}}}} Event.");
        }
        else if (chain.Items.Any(i => i.IsTemporary) && areas != null)
        {
            var chainItemTypes = chain.Items.Select(i => i.ItemType).Where(t => !string.IsNullOrEmpty(t)).ToList();
            var matchingAreas = AreasService.FindAreasRequiringChain(chainItemTypes!, areas);
            if (matchingAreas.Count > 0)
            {
                var areaRefs = string.Join(", ", matchingAreas.Select(a => $"{{{{Area|{a.DisplayName}}}}}"));
                sb.Append($"{subject} {verb} in '''''Merge Mansion'''''. {usedPronoun} used on the Main Board in {areaRefs}.");
            }
            else
            {
                sb.Append($"{subject} {verb} in '''''Merge Mansion'''''. {usedPronoun} used on the Main Board.");
            }
        }
        else
        {
            sb.Append($"{subject} {verb} in '''''Merge Mansion'''''. {usedPronoun} used on the Main Board.");
        }
        return sb.ToString();
    }

    /// <summary>Resolves an ItemType to `Nx {{Item|ChainName|Level}}` (or just `{{Item|...}}` when amount=1).</summary>
    private string FormatItemRef(string itemType, int amount)
    {
        var chainName = _data.ResolveChainDisplayName(_data.ResolveChainKeyFromItemType(itemType)) ?? itemType;
        var level = DataService.GetLevelFromItemType(itemType);
        var amtPrefix = amount > 1 ? $"{amount}x " : "";
        return level > 0
            ? $"{amtPrefix}{{{{Item|{chainName}|{level}}}}}"
            : $"{amtPrefix}{{{{Item/nolevel|{chainName}}}}}";
    }

    private class DropGroup
    {
        public string ChainKey { get; set; } = "";
        public double TotalChance { get; set; }
        public List<int> Levels { get; set; } = new();
    }

    private class DecayChainGroup
    {
        public string ChainDisplayName { get; set; } = "";
        public List<int> Levels { get; set; } = new();
        public int TargetVariantCount { get; set; }
    }
}
