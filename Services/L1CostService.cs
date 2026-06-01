using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Calculates the total "L1 cost" of items — how many Level 1 items are needed
/// to produce a given item through merging and sink/transform chains.
///
/// Basic merge: L(N) item = 2^(N-1) L1 items (binary merge tree, always 2:1).
/// Sink/Transform: If the item is created by transforming a sink item with fuel,
/// the cost includes both the sink item's merge cost AND the fuel items' costs.
/// Full recursion: Fuel items that are themselves produced via sinks are followed recursively.
/// Cycle detection: Prevents infinite loops in circular transform chains.
/// </summary>
public class L1CostService
{
    private readonly DataService _data;

    // Cache: ItemType → L1 cost (avoids repeated computation for same items)
    private readonly Dictionary<string, long> _cache = new(StringComparer.OrdinalIgnoreCase);

    // NumericConfigKey → (Chain, Item) lookup (built once)
    private Dictionary<string, (ParsedChain Chain, ParsedItem Item)>? _configKeyLookup;

    public L1CostService(DataService data)
    {
        _data = data;
    }

    /// <summary>
    /// Computes the L1 cost of a single item (identified by ItemType).
    /// Returns the total number of L1 items needed to produce this item.
    /// </summary>
    public long ComputeItemL1Cost(string itemType)
    {
        if (string.IsNullOrEmpty(itemType)) return 0;
        if (_cache.TryGetValue(itemType, out var cached)) return cached;

        var cost = ComputeItemL1CostRecursive(itemType, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _cache[itemType] = cost;
        return cost;
    }

    /// <summary>
    /// Computes the total L1 cost for all requirements of a single task.
    /// Each requirement: quantity × L1 cost of the item.
    /// </summary>
    public long ComputeTaskL1Cost(LuaTask task)
    {
        long total = 0;
        foreach (var (itemType, qty) in task.Requirements)
            total += qty * ComputeItemL1Cost(itemType);
        return total;
    }

    /// <summary>
    /// Computes the total L1 cost for all tasks in an area.
    /// </summary>
    public long ComputeAreaL1Cost(LuaArea area)
    {
        long total = 0;
        foreach (var task in area.Tasks)
            if (task.Requirements.Count > 0)
                total += ComputeTaskL1Cost(task);
        return total;
    }

    /// <summary>
    /// Returns the number of tasks with at least one requirement.
    /// </summary>
    public static int CountTasksWithRequirements(LuaArea area)
        => area.Tasks.Count(t => t.Requirements.Count > 0);

    // ── Core recursive computation ──────────────────────────────────

    private long ComputeItemL1CostRecursive(string itemType, HashSet<string> visited)
    {
        if (string.IsNullOrEmpty(itemType)) return 0;

        // Cycle detection
        if (!visited.Add(itemType)) return 0;

        try
        {
            // Check cache first (within recursion too)
            if (_cache.TryGetValue(itemType, out var cached)) return cached;

            var chainKey = _data.ResolveChainKeyFromItemType(itemType);
            var level = DataService.GetLevelFromItemType(itemType);

            // Find the chain and item in DataService
            var chain = _data.Chains.FirstOrDefault(c =>
                string.Equals(c.ConfigKey, chainKey, StringComparison.OrdinalIgnoreCase));

            if (chain == null || level <= 0)
            {
                // Unknown chain or level — treat as 1 L1 item
                return 1;
            }

            var item = chain.Items.FirstOrDefault(i => i.Level == level);

            // Basic merge cost: 2^(level-1)
            long mergeCost = 1L << (level - 1); // 2^(level-1)

            // Check if THIS item is produced by a sink (transform).
            // Look for any sink item in any chain whose SinkRewardItemType == this itemType.
            long sinkCost = ComputeSinkProductionCost(itemType, visited);

            if (sinkCost > 0)
            {
                // Item is produced via transformation — use sink cost instead of merge cost
                return sinkCost;
            }

            // Check if this item IS a sink (requires fuel to transform)
            if (item != null && item.IsSink && item.SinkRequirementConfigKeys != null)
            {
                long fuelCost = ComputeSinkFuelCost(item, visited);
                return mergeCost + fuelCost;
            }

            return mergeCost;
        }
        finally
        {
            visited.Remove(itemType);
        }
    }

    /// <summary>
    /// If this itemType is produced as a SinkRewardItemType by some sink item,
    /// compute the total cost: sink item's merge cost + fuel items' costs.
    /// </summary>
    private long ComputeSinkProductionCost(string rewardItemType, HashSet<string> visited)
    {
        foreach (var chain in _data.Chains)
        {
            foreach (var item in chain.Items)
            {
                if (!item.IsSink) continue;
                if (!string.Equals(item.SinkRewardItemType, rewardItemType, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Found: this sink produces our item
                // Cost = sink item's own merge cost + fuel costs
                var sinkItemType = $"{chain.ConfigKey}_{item.Level:00}";
                // Try to find actual ItemType
                if (!string.IsNullOrEmpty(item.ItemType))
                    sinkItemType = item.ItemType;

                long sinkMergeCost = 1L << (item.Level - 1);
                long fuelCost = ComputeSinkFuelCost(item, visited);

                return sinkMergeCost + fuelCost;
            }
        }

        return 0; // Not produced by any sink
    }

    /// <summary>
    /// Computes the total L1 cost of all fuel items required by a sink.
    /// </summary>
    private long ComputeSinkFuelCost(ParsedItem sinkItem, HashSet<string> visited)
    {
        if (sinkItem.SinkRequirementConfigKeys == null) return 0;

        EnsureConfigKeyLookup();
        long totalFuelCost = 0;

        foreach (var configKey in sinkItem.SinkRequirementConfigKeys)
        {
            int amount = 1;
            if (sinkItem.SinkRequirementAmounts != null &&
                sinkItem.SinkRequirementAmounts.TryGetValue(configKey, out var amt))
                amount = amt;

            if (_configKeyLookup!.TryGetValue(configKey, out var match))
            {
                var fuelItemType = match.Item.ItemType;
                if (string.IsNullOrEmpty(fuelItemType))
                    fuelItemType = $"{match.Chain.ConfigKey}_{match.Item.Level:00}";

                long fuelUnitCost = ComputeItemL1CostRecursive(fuelItemType, visited);
                totalFuelCost += amount * fuelUnitCost;
            }
        }

        return totalFuelCost;
    }

    // ── ConfigKey lookup ────────────────────────────────────────────

    private void EnsureConfigKeyLookup()
    {
        if (_configKeyLookup != null) return;

        _configKeyLookup = new Dictionary<string, (ParsedChain, ParsedItem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in _data.Chains)
            foreach (var item in chain.Items)
                if (!string.IsNullOrEmpty(item.NumericConfigKey))
                    _configKeyLookup.TryAdd(item.NumericConfigKey, (chain, item));
    }
}
