using System.Linq;

namespace MergeMansionWikiTools.Models;

// ── Daily Trade (DailyTasksV2) config models — parsed from events.json ──

public class DailyTradeTask
{
    public string TaskId { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int StreakCountMin { get; set; }
    public int StreakCountMax { get; set; }
    /// <summary>0 = Easy queue, 1 = Hard queue (game shows both per streak).</summary>
    public int UIOrder { get; set; }
    public List<DailyTradeStep> Steps { get; set; } = new();
}

public class DailyTradeStep
{
    public int RequirementItemValue { get; set; }
    public int RewardItemValue { get; set; }
    public bool RefreshEnabled { get; set; }
    public List<int> RefreshCosts { get; set; } = new();
    /// <summary>Attempt order, e.g. "HH" = requirement High × reward High.</summary>
    public List<string> AlgorithmAttempts { get; set; } = new();
}

public class DailyTradeChainRule
{
    public string ConfigKey { get; set; } = "";
    public bool CanBeRequirement { get; set; }
    public bool CanBeReward { get; set; }
    public int? MinLevel { get; set; }
    public int? MaxLevel { get; set; }
    public double RequirementMultiplier { get; set; } = 1.0;
    public double RewardMultiplier { get; set; } = 1.0;
    public bool RewardOnlyIfInHotspotRequirement { get; set; }
    public bool OnlyPossibleRequirementsHighPriority { get; set; }
    public List<string> RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas { get; set; } = new();
    public List<string> RequireOnlyIfHaveProducer { get; set; } = new();
}

public class DailyTradeSettings
{
    public int NextHotspotsCount { get; set; } = 6;
    public int MaxLevelDifferenceFromHotspotToRemoveRequirementItem { get; set; } = 3;
    public int LastGeneratedTaskRewardItemsHistoryCountMaxSize { get; set; } = 4;
    public int MaxStreakCount { get; set; } = 4;
    public List<int> AgeValues { get; set; } = new();
    public List<string> AlgorithmAttemptsDefault { get; set; } = new();
    public int SortingRangeForPossibleRequirementsHighPriority { get; set; } = 5;
    public int SortingRangeGeneric { get; set; } = 2;
}

// ── Predictor page state (persisted to predictor_state.json) ──

/// <summary>One aggregated board entry (chain + level + how many held). Consumed by the engine.</summary>
public class PredictorBoardItem
{
    public string ChainKey { get; set; } = "";
    public int Level { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>One filled tile of the 7×9 board grid (the UI's source of truth). Empty tiles are
/// simply absent. The engine board (<see cref="PredictorState.Board"/>) is aggregated from these.</summary>
public class PredictorBoardCell
{
    public int Row { get; set; }
    public int Col { get; set; }
    public string ChainKey { get; set; } = "";
    public int Level { get; set; }
}

/// <summary>One item of a currently-visible trade (requirement or reward side).</summary>
public class PredictorTradeItem
{
    public string ChainKey { get; set; } = "";
    public int Level { get; set; }
}

/// <summary>The trade a queue currently shows: requirement → reward (either side optional).</summary>
public class PredictorKnownTrade
{
    public PredictorTradeItem? Requirement { get; set; }
    public PredictorTradeItem? Reward { get; set; }
}

public class PredictorState
{
    /// <summary>Board grid tiles (7 cols × 9 rows). Source of truth for the UI + persistence.</summary>
    public List<PredictorBoardCell> Cells { get; set; } = new();
    /// <summary>Basic (pocket) inventory tiles — part of the algorithm's "garage items" pool just
    /// like the board (only the producer inventory is excluded). Row/Col are grid positions in the
    /// inventory grid (7 per row).</summary>
    public List<PredictorBoardCell> InventoryCells { get; set; } = new();
    /// <summary>Permanent inventory slots the player owns: 7 free + up to 43 purchased (coins),
    /// game max 50 (config library InventorySlots).</summary>
    public int InventorySlotsOwned { get; set; } = 7;
    /// <summary>Temporary Season Pass perk slots: 0, +3 (Super Pass) or +6 (Premium Pass).</summary>
    public int InventorySpBonus { get; set; }
    public string? AreaInternalName { get; set; }
    /// <summary>Ids of the tasks the player currently sees as active hotspots (multi-select in the
    /// UI). These drive the ±level hotspot blocking instead of a single look-ahead index.</summary>
    public List<string> ActiveTaskIds { get; set; } = new();
    public int Streak { get; set; }
    /// <summary>Which trade slot each daily queue is on (0 = first). The queues advance independently.</summary>
    public int Queue1StepIndex { get; set; }
    public int Queue2StepIndex { get; set; }
    /// <summary>The trades currently shown in each queue. Their req/reward items are EXCLUDED from
    /// the predicted candidates (game: existingRequirement/RewardItemIds → RemoveExisting* filters —
    /// both queues can never ask for / reward the same item at once).</summary>
    public PredictorKnownTrade Queue1Trade { get; set; } = new();
    public PredictorKnownTrade Queue2Trade { get; set; } = new();
    /// <summary>How many times each queue's CURRENT trade has been refreshed. Indexes into the
    /// step's RefreshCosts (next refresh price) and feeds the diminishing context (game state
    /// RefreshCount / AccumulatedDiminishingValue — repeats inflate the desired reward value).</summary>
    public int Queue1Refreshes { get; set; }
    public int Queue2Refreshes { get; set; }
    public List<string> LastRewardChainKeys { get; set; } = new();
    /// <summary>Recently picked chains (newest first) for the board-cell picker's "Recent" column.
    /// Persisted so the history survives app restarts.</summary>
    public List<string> RecentChainKeys { get; set; } = new();

    /// <summary>Aggregated (chain, level) → count over board + basic inventory (both belong to the
    /// algorithm's garage pool). Not persisted (rebuilt from the cells). This is what the engine reads.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<PredictorBoardItem> Board => Cells.Concat(InventoryCells)
        .GroupBy(c => (c.ChainKey, c.Level))
        .Select(g => new PredictorBoardItem { ChainKey = g.Key.ChainKey, Level = g.Key.Level, Count = g.Count() })
        .ToList();
}
