using System.Text.Json.Serialization;

namespace MergeMansionWikiTools.Models;

// ── Top-level JSON wrapper ──────────────────────────────────────────

public class ChainDataRoot
{
    public string CreatedAt { get; set; } = "";
    public List<ChainDefinition> Data { get; set; } = new();
}

// ── Chain definition ────────────────────────────────────────────────

public class ChainDefinition
{
    public string Name { get; set; } = "";
    public string ConfigKey { get; set; } = "";
    public string? CodexCategory { get; set; }
    public string? DiscoveryRewardRef { get; set; }
    public List<ChainEntry> PrimaryChain { get; set; } = new();
    public List<ChainEntry> FallbackChain { get; set; } = new();
}

public class ChainEntry
{
    public ItemDefinition? Item { get; set; }
}

// ── Item definition ─────────────────────────────────────────────────

public class ItemDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int SellCoins { get; set; }
    public string ConfigKey { get; set; } = "";
    public string ItemType { get; set; } = "";
    public int LevelNumber { get; set; }
    public bool Unsellable { get; set; }
    public int ExperienceValue { get; set; }
    public string? Rarity { get; set; }

    // Features
    public ActivationFeaturesData? ActivationFeatures { get; set; }
    public SpawnFeaturesData? SpawnFeatures { get; set; }
    public DecayFeaturesData? DecayFeatures { get; set; }
    public MergeFeaturesData? MergeFeatures { get; set; }

    // Tags
    public List<string>? Tags { get; set; }
}

// ── Activation features (generators) ────────────────────────────────

public class ActivationFeaturesData
{
    public ActivationSpawnData? ActivationSpawn { get; set; }
    public ActivationCycleData? ActivationCycle { get; set; }
    public int StorageMax { get; set; }
}

public class ActivationSpawnData
{
    public string? Marker { get; set; }
    public ProducerData? BaseProducer { get; set; }
}

public class ProducerData
{
    public string? Constant { get; set; }
    public ControlledRandomData? ControlledRandom { get; set; }
    public RandomOddsData? Random { get; set; }
}

public class ControlledRandomData
{
    public string? RollType { get; set; }
    public string? ItemType { get; set; }
    public Dictionary<string, double>? Odds { get; set; }
}

public class RandomOddsData
{
    public Dictionary<string, double>? Odds { get; set; }
}

public class ActivationCycleData
{
    public long MiniChargeCooldown { get; set; }
    public long InitialCooldown { get; set; }
    public int HowManyCycles { get; set; } = -1;
    public long ChargeCooldown { get; set; }
    public int MiniChargesInSingleCharge { get; set; }
    public int DropsInSingleMiniCharge { get; set; }
    public double TimerSkipMultiplier { get; set; }
}

// ── Spawn features ──────────────────────────────────────────────────

public class SpawnFeaturesData
{
    public ProducerData? Spawn { get; set; }
    public SpawnCycleData? SpawnCycle { get; set; }
    public int StorageMax { get; set; }
    public ProducerData? DecayProducer { get; set; }
    public string? SpawnVisibility { get; set; }
    public bool Spawnable { get; set; }
    public bool DecaysWhenCyclesAreDone { get; set; }
}

public class SpawnCycleData
{
    public int SpawnDelay { get; set; }
    public int FirstCycleStartDelay { get; set; }
    public long DelayBetweenCycles { get; set; }
    public int HowManyAreGeneratedPerSpawn { get; set; }
    public int SpawnAmountInCycle { get; set; }
    public int HowManyCycles { get; set; } = -1;
}

// ── Decay features ──────────────────────────────────────────────────

public class DecayFeaturesData
{
    public bool DoesDecay { get; set; }
    public long Lifetime { get; set; }
    public ProducerData? ItemProducer { get; set; }
    public string? DecayMergeMode { get; set; }
    public bool ShowDecayTimer { get; set; }
}

// ── Merge features ──────────────────────────────────────────────────

public class MergeFeaturesData
{
    // Simplified — extend if needed
    public bool IsMergeable { get; set; }
}

// ── Parsed chain model (for UI) ─────────────────────────────────────

public class ParsedChain
{
    public string ConfigKey { get; set; } = "";

    /// <summary>Display name: custom name → chain Name → ConfigKey</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Original name from JSON (may be empty)</summary>
    public string OriginalName { get; set; } = "";

    /// <summary>Whether the original name looks "natural" (not internal ID)</summary>
    public bool HasNaturalName { get; set; }

    /// <summary>Custom user-assigned name, if any</summary>
    public string? CustomName { get; set; }

    /// <summary>Whether DisplayName was overridden by wiki mapping data</summary>
    public bool IsNameFromWiki { get; set; }

    /// <summary>PoolTag from the first item — used for visual asset (skeleton/texture) resolution.</summary>
    public string PoolTag { get; set; } = "";

    /// <summary>Whether the first item has a "Test" tag — dev/placeholder chain with reused PoolTag.</summary>
    public bool HasTestTag { get; set; }

    /// <summary>When this chain was merged from multiple JSON chains, lists all source ConfigKeys.</summary>
    public List<string>? MergedFromConfigKeys { get; set; }

    /// <summary>Whether two or more items share the same level in this chain.</summary>
    public bool HasLevelCollisions { get; set; }

    /// <summary>
    /// True when this chain is the data-named remainder of a game source chain that was PARTIALLY
    /// adopted onto the wiki: some sibling items carry a wiki <c>chainName</c> (and moved into a
    /// renamed wiki chain) while the items here have no mapping of their own, so they fell back to the
    /// original game name. Signals a gap — these items are missing from the wiki mapping. Detected in
    /// <c>ApplyWikiMappingToChains</c>; only set when the source chain genuinely SPLIT (data name ≠
    /// wiki name), never when unmapped items simply rejoined a same-named chain.
    /// </summary>
    public bool IsUnmappedStraggler { get; set; }

    /// <summary>When <see cref="IsUnmappedStraggler"/> is true, the wiki chain name the mapped siblings
    /// moved into (e.g. "Orange Flower" for the "A beautiful Orange Flower" remainder). Used for the badge tooltip.</summary>
    public string? StragglerParentWikiName { get; set; }

    public List<ParsedItem> Items { get; set; } = new();

    /// <summary>Compact summary like "12 levels, Generator"</summary>
    public string Summary => BuildSummary();

    /// <summary>Whether any item is a generator (has ActivationFeatures)</summary>
    public bool HasGenerators => Items.Any(i => i.IsGenerator);

    /// <summary>Whether any item has SpawnFeatures</summary>
    public bool HasSpawners => Items.Any(i => i.IsSpawner);

    /// <summary>All known event chain ConfigKey prefix bases (without trailing underscore).</summary>
    public static readonly string[] EventPrefixes =
        { "LS", "LC", "LDE", "CBE", "MME", "GM", "DE", "SLBE", "TCE", "BLE", "LBE", "SE", "CSE", "JP" };

    /// <summary>Whether this looks like an event chain (matches prefix with or without underscore separator).</summary>
    public bool IsEventChain => EventPrefixes.Any(p =>
        ConfigKey.StartsWith(p + "_") || (ConfigKey.StartsWith(p) && ConfigKey.Length > p.Length && char.IsUpper(ConfigKey[p.Length])));

    /// <summary>Whether this chain has a human-readable name (wiki, custom, or natural original name)</summary>
    public bool HasHumanReadableName =>
        IsNameFromWiki
        || !string.IsNullOrEmpty(CustomName)
        || (!string.IsNullOrWhiteSpace(OriginalName) && !OriginalName.Contains('_'));

    private string BuildSummary()
    {
        int maxLvl = Items.Count > 0 ? Items.Max(i => i.Level) : 0;
        int aliasCount = Items.Count - maxLvl;
        var label = aliasCount > 0
            ? $"{maxLvl} lvl · {aliasCount} alias{(aliasCount != 1 ? "es" : "")}"
            : $"{maxLvl} lvl";
        var parts = new List<string> { label };

        if (HasGenerators) parts.Add("Generator");
        else if (HasSpawners) parts.Add("Spawner");
        else parts.Add("Product");

        if (IsEventChain) parts.Add("Event");

        return string.Join(" · ", parts);
    }
}

public class ParsedItem
{
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public int SellCoins { get; set; }
    public bool Unsellable { get; set; }

    // Daily Trade (DailyTasksV2) bubble values — present only for bubble-able items
    // (dumper writes them from BubbleFeatures; null = item can never be a trade req/reward).
    public int? RequiredItemValue { get; set; }
    public int? RewardItemValue { get; set; }

    /// <summary>Per-item PoolTag (visual asset key). Usually equal across a chain, but for chains
    /// whose items live in separate atlases (e.g. InfiniteEnergy: LimitedItemInfiniteEnergyA/B/C →
    /// UnlimitedEnergyD/C/A) each item has its OWN PoolTag → its own texture.</summary>
    public string PoolTag { get; set; } = "";

    // Generator info
    public bool IsGenerator { get; set; }
    public Dictionary<string, double>? DropOdds { get; set; }

    /// <summary>
    /// True when <see cref="DropOdds"/> came from a NON-constant producer (ControlledRandom / Random /
    /// Sequence) — i.e. there is real drop variance. A pure ConstantProducer yields a single 100%
    /// entry in DropOdds but leaves this false (nothing to present in the Drop Odds section).
    /// </summary>
    public bool HasRandomDrop { get; set; }
    public int ActivationAmountInCycle { get; set; }
    public int HowManyGeneratedInCycle { get; set; } = 1;
    public int ActivationHowManyCycles { get; set; } = -1;
    public int StorageMax { get; set; }
    public int MaxCharges { get; set; }
    /// <summary>
    /// True when item starts with a pre-filled storage. Determines how to interpret
    /// `StorageMax` semantically: when true raw StorageMax is the per-cycle drops
    /// capacity (e.g. Mane Comb 4 drops, Water Bucket 30/cycle); when false items
    /// start empty and the dumper overrides StorageMax to <c>HowManyCycles × dpc</c>
    /// as total lifetime drops (e.g. Plain Box 5 total, White Moth 60 total).
    /// </summary>
    public bool StartsFull { get; set; }
    public long RechargeTimeMs { get; set; }
    public long MiniChargeCooldownMs { get; set; }
    public long FirstCycleStartDelayMs { get; set; }
    public double TimerSkipMultiplier { get; set; }
    public string? DecayAfterLastCycleItemType { get; set; }
    public Dictionary<string, double>? DecayAfterLastCycleOdds { get; set; }
    /// <summary>
    /// True when JSON has DecayAfterLastCycleProducer field at all (even "Empty").
    /// Used to detect "truly infinite producer" (cycles=-1 AND field absent).
    /// </summary>
    public bool HasDecayAfterLastCycleField { get; set; }

    // Spawner info
    public bool IsSpawner { get; set; }
    public string? SpawnItemType { get; set; }
    public Dictionary<string, double>? SpawnOdds { get; set; }
    public int SpawnHowManyCycles { get; set; } = -1;
    public int SpawnAmountInCycle { get; set; }
    public long SpawnDelayMs { get; set; }
    public int SpawnStorageMax { get; set; }

    // Decay info
    public bool HasDecay { get; set; }
    public string? DecayIntoItemType { get; set; }
    public string? SpawnDecayIntoItemType { get; set; }

    // Chest features — items with ChestFeatures.LootProducer (e.g. Brown Chest, Mystery
    // Chest, Reward Boxes). Each entry is itemType → drop probability (%).
    public bool IsChest { get; set; }
    public Dictionary<string, double>? ChestRewardOdds { get; set; }

    /// <summary>
    /// ConstantProducer payload (Reward Boxes): ordered (Item, Quantity) pairs from
    /// `ChestFeatures.LootProducer.Constant`. Guaranteed drops, not probabilistic.
    /// Null for non-constant loot (Random / ControlledRandom* / PrefixProducer wrappers).
    /// </summary>
    public List<(string Item, int Quantity)>? ChestRewardItems { get; set; }

    // Lua generation extras
    public string Description { get; set; } = "";
    public bool IsTemporary { get; set; }

    /// <summary>True if Tags contains "Test" — dev/placeholder item, exclude from all consumption logic.</summary>
    public bool IsTestTag { get; set; }

    /// <summary>Full tag list from JSON (e.g. "Repeatable", "DontSellIfOnlyOneOrHighest", "GoesOnTopOfPocket"). Used by table/infobox generators.</summary>
    public List<string>? Tags { get; set; }

    // Sink (Transformative Item)
    public bool IsSink { get; set; }
    public List<string>? SinkRequirementConfigKeys { get; set; }
    public Dictionary<string, int>? SinkRequirementAmounts { get; set; }
    public string? SinkRewardItemType { get; set; }

    /// <summary>
    /// Merge result ItemType from MergeFeatures.Mechanic.ResultProducer.Constant.
    /// For normal merges this points to L+1 of the same chain; for cross-chain merges
    /// (e.g. Bigger Pile of Seed Bags L4 → Golden Seed L1) this points to a different chain.
    /// Used by Infobox / chain table to emit transforms_to when target is in a different chain.
    /// </summary>
    public string? MergeResultItemType { get; set; }

    // Order features (tasks that require items and give rewards)
    public bool IsOrder { get; set; }
    /// <summary>ItemType → total amount required across all order tasks</summary>
    public Dictionary<string, int>? OrderRequiredItems { get; set; }
    /// <summary>ItemType → total amount rewarded across all order tasks</summary>
    public Dictionary<string, int>? OrderRewardItems { get; set; }

    /// <summary>Full task list with per-task odds, weight, requirements, rewards.
    /// Preserved separately from aggregated OrderRequiredItems/OrderRewardItems so the wiki
    /// Tasks table generator can render per-task rows (range-collapsing identical tuples
    /// based on integer slot weights, mimicking Distillation Apparatus wiki style).</summary>
    public List<ParsedTask>? OrderTasks { get; set; }

    /// <summary>Numeric ConfigKey from JSON item (used by SinkFeatures ScoreTargets)</summary>
    public string NumericConfigKey { get; set; } = "";

    // Bubble features
    public bool HasBubble { get; set; }
    public long BubbleDurationMs { get; set; }
    public int BubbleOpenCost { get; set; }
    public int BubbleSpawnOdds { get; set; }

    // Depleting spawner
    public bool DecaysWhenCyclesAreDone { get; set; }

    // ExtraSpawn token values (DigEventTaps, QuaternaryEnergy, etc.)
    public Dictionary<string, double>? ExtraSpawnValues { get; set; }

    // Speed-up cost in gems (generators only)
    public int? SpeedUpCostGems { get; set; }

    /// <summary>Area variant label for multi-variant chain levels (e.g. "Sauna").</summary>
    public string? VariantLabel { get; set; }

    /// <summary>
    /// Multi-target decay-roll odds (targetItemType → percent) from a "roller" item's
    /// <c>DecayFeatures.ItemProducer</c> ControlledRandom/Random (e.g. Flower Bed L6 merge → A/B/C at
    /// 65/30/5). Emitted to the data module as <c>decayInto = {{id, value}, …}</c>; the Decay Odds
    /// section's Lua reads it and cross-references the mapping's <c>isVariant</c> flags.
    /// </summary>
    public Dictionary<string, double>? DecayIntoOdds { get; set; }

    /// <summary>Whether this item collides (same level) with another item in its chain.</summary>
    public bool IsColliding { get; set; }

    /// <summary>Whether this item is an alias (secondary) in a wiki merge group.</summary>
    public bool IsAlias { get; set; }

    /// <summary>
    /// Whether this item is an explicit display VARIANT (e.g. the A/B/C generator outcomes of a
    /// branching merge). Like alias, it suppresses the same-level collision warning; unlike alias it
    /// is meant to be SHOWN as a variant sub-row. Set from the mapping flag <c>isVariant = true</c>.
    /// </summary>
    public bool IsVariant { get; set; }

    /// <summary>ConfigKey of the original chain this item came from (before wiki mapping merge).</summary>
    public string SourceChainKey { get; set; } = "";

    /// <summary>Visual skin/sprite name from game config (maps to Unity Sprite asset names).</summary>
    public string SkinName { get; set; } = "";

    // Original definition reference
    public ItemDefinition? Source { get; set; }
}

/// <summary>Single OrderFeatures task — required items, rewards, and roll odds.
/// For ControlledRandom: OddsWeight is integer slot count (e.g. 4 = 4 of 15 rotation slots).
/// For Constant: Odds is the % chance per spawn.</summary>
public class ParsedTask
{
    public double Odds { get; set; }
    public double OddsWeight { get; set; }
    /// <summary>Required ItemType → Amount (in declaration order from JSON).</summary>
    public List<(string ItemType, int Amount)> Required { get; set; } = new();
    /// <summary>Reward ItemType → Amount (in declaration order from JSON).</summary>
    public List<(string ItemType, int Amount)> Rewards { get; set; } = new();
}
