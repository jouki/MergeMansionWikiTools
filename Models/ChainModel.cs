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

    /// <summary>
    /// True when the item's mini-charge fields carry the game's <b>9999 sentinel</b> rather than real
    /// numbers. 9999 in <see cref="ActivationAmountInCycle"/> (<c>MiniChargesInSingleCharge</c>) or
    /// <see cref="HowManyGeneratedInCycle"/> (<c>DropsInSingleMiniCharge</c>) marks a stateful
    /// event/minigame item that produces indefinitely — it is NOT a droppable producer with a finite
    /// charge budget (Piano metronomes, the Hopeberry furnace, the Sweet Mess chocolate machine, the
    /// Football storage, the Murder at the Mansion detective pair). Multiplying those out yields
    /// nonsense like <c>99980001</c> drops.
    /// <para>
    /// Every consumer that turns charges into numbers must skip these: the Lua emitter leaves the
    /// drop fields out, and the table generator must not open a Drops Values column that can only
    /// ever be dashes. Rule + affected items: <c>_CONTEXT/Game/Mechaniky.md</c> → "Sentinel hodnoty
    /// 9999/9999".
    /// </para>
    /// </summary>
    public bool IsActivationSentinel => ActivationAmountInCycle >= 9999 || HowManyGeneratedInCycle >= 9999;
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

    /// <summary>ChestFeatures.HowManyToRoll — number of loot rolls when the chest is opened. 0 = unset.</summary>
    public int ChestRollCount { get; set; }

    /// <summary>
    /// ConstantProducer payload (Reward Boxes): ordered (Item, Quantity) pairs from
    /// `ChestFeatures.LootProducer.Constant`. Guaranteed drops, not probabilistic.
    /// Null for non-constant loot (Random / ControlledRandom* / PrefixProducer wrappers).
    /// </summary>
    public List<(string Item, int Quantity)>? ChestRewardItems { get; set; }

    /// <summary>
    /// PrefixProducer guaranteed prefix (`ChestFeatures.LootProducer.Prefix`): ordered
    /// (Item, Quantity) pairs dropped BEFORE the random BaseProducer rolls take over — a
    /// per-player marker tracks progress, so this is a one-time sequence (e.g. Daily Trades
    /// card chest: 4× 1★ + 1× 2★ envelope, then random materials). Null when absent.
    /// </summary>
    public List<(string Item, int Quantity)>? ChestPrefixItems { get; set; }

    // Fishing-rod features — Lucky Catch rods AND Lucky Snap cameras ("taking a photo"
    // reuses the fishing cast mechanic). FishingRodFeatures.ItemOdds carries weighted
    // catch targets; stored here normalized to percentages (itemType → %).
    public bool IsFishingRod { get; set; }
    public Dictionary<string, double>? FishingOdds { get; set; }

    /// <summary>
    /// FishingRodFeatures.WaterDropletOverride — the side-drop item scattered by every
    /// cast/shot (Lucky Catch: water droplets; Lucky Snap: failed photos, 1–12 per shot).
    /// The dump stores a NUMERIC item ConfigKey; resolve against ParsedItem.NumericConfigKey.
    /// </summary>
    public string? FishingDropletConfigKey { get; set; }

    /// <summary>Droplet/failed-photo COUNT range per shot — scales with the catch (rarer = more;
    /// FishingSettings.SmallFish/NonFishWaterDropletCounts, dumper-computed min/max across catches).
    /// Null when not emitted (pre-v0.24.23 dump).</summary>
    public int? FishingDropletCountMin { get; set; }
    public int? FishingDropletCountMax { get; set; }

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
    /// Tag-based sink: the fuel is not one named item but <b>any one item carrying this tag</b>
    /// (<c>SinkFeatures.Factory.Tag</c>, e.g. <c>MurderWeapons</c> for the Murder at the Mansion
    /// DNA Kit). <see cref="DataService.ResolveTagSinks"/> resolves it into the same
    /// <see cref="SinkRequirementConfigKeys"/>/<see cref="SinkRequirementAmounts"/>/
    /// <see cref="SinkRewardItemType"/> the ScoreTargets form uses, so every downstream generator
    /// keeps working unchanged; <see cref="SinkIsAnyOf"/> is what tells them the list is an OR.
    /// </summary>
    public string? SinkTagName { get; set; }

    /// <summary>How many tagged items one activation consumes (<c>Factory.InputCount</c>).</summary>
    public int SinkInputCount { get; set; }

    /// <summary>Lookup key into the TagRewards table (<c>Factory.RewardTagName</c>).</summary>
    public string? SinkRewardTagName { get; set; }

    /// <summary>
    /// <c>Factory.TagFuel</c>: every item that carries this sink's tag, with the
    /// <see cref="ParsedItem.SinkPoints"/> it contributes. Resolved by the dumper, because the
    /// point total is what decides which reward comes back.
    /// </summary>
    public List<(string ItemType, int SinkPoints)>? SinkTagFuel { get; set; }

    /// <summary>
    /// <c>Factory.TagRewards</c>: point total to what is produced at that total. For the DNA Kit
    /// this is what says that Murder Weapon #1 gives an 8-drop run and #4 a 20-drop one.
    /// </summary>
    public List<SinkTagReward>? SinkTagRewards { get; set; }

    /// <summary>
    /// True when the requirement list means "any ONE of these" rather than "all of these".
    /// Only tag sinks set it; ScoreTargets sinks stay AND, which is what they have always been.
    /// </summary>
    public bool SinkIsAnyOf { get; set; }

    /// <summary>
    /// The tag under which THIS item can be fed into a tag sink (<c>Item.SinkTag</c>) - the
    /// counterpart of <see cref="SinkTagName"/>.
    /// </summary>
    public string? SinkTag { get; set; }

    /// <summary>
    /// Weight this item contributes when sunk (<c>Item.SinkPoints</c>). The game looks the reward
    /// up by the total points of what was consumed, which is how the four Murder Weapons produce
    /// four different DNA Kit runs (points 1-4 give 8/10/12/20 drops).
    /// </summary>
    public int SinkPoints { get; set; }

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
    /// Tasks table generator can render per-task rows.</summary>
    public List<ParsedTask>? OrderTasks { get; set; }

    /// <summary>OrderProducer wrapper name from JSON: "Constant", "ControlledRandom" or
    /// "ControlledPredefinedSequence". Decides how the wiki Tasks table is rendered, because the
    /// wrapper is what determines whether the next order is picked in sequence or rolled:
    /// <list type="bullet">
    /// <item><c>ControlledRandom</c> — Produce() rolls through WeightedDistributionStates and
    /// AdvanceSequenceIndex() is empty, so there is no order and no cycle. OddsWeight is a
    /// probability weight; the table shows <see cref="ParsedTask.Odds"/> percentages.</item>
    /// <item><c>Constant</c> / <c>ControlledPredefinedSequence</c> — Produce(orderIndex) indexes
    /// the task list directly and OrderCount is the task count (never the weight sum), so tasks
    /// run in declaration order, one row each, repeating after the last one.</item>
    /// </list>
    /// Verified against ISIL of 26.05.01 libil2cpp; empty when the JSON had no recognized wrapper.
    /// </summary>
    public string OrderProducerKind { get; set; } = "";

    /// <summary>One <c>Factory.TagRewards</c> row: a point total and what it hands back.</summary>
    /// <param name="TotalPoints">Summed <c>SinkPoints</c> of everything consumed.</param>
    /// <param name="FuelItemTypes">
    /// The items this row belongs to — only filled at <c>InputCount == 1</c>, where the total is
    /// one item's points. Above that a total is a sum and no single fuel owns the row.
    /// </param>
    public sealed record SinkTagReward(int TotalPoints, List<string> FuelItemTypes, List<SinkTagProduct> Produces);

    /// <summary>What one <c>TagRewards</c> row produces, with the numbers read off the target.</summary>
    /// <param name="Drops">
    /// Total drops of the produced item (0 when endless or unknown). Carried here rather than
    /// looked up, because 23 of the 40 tag-reward targets are in no PrimaryChain and therefore
    /// absent from the chain dump entirely.
    /// </param>
    /// <param name="Odds">Its spawn table, only when it rolls more than one outcome.</param>
    public sealed record SinkTagProduct(string ItemType, int Drops, Dictionary<string, double>? Odds);

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
    /// Display label from the wiki mapping's <c>isVariant = "Spring"</c> (string form). When
    /// <c>isVariant = true</c> this is null → the wiki falls back to letters A/B/C. Distinct from
    /// <see cref="VariantLabel"/> (data-derived area label for decay/transform variants).
    /// </summary>
    public string? MappingVariantLabel { get; set; }

    /// <summary>
    /// ItemType from the mapping's <c>variantItem</c>. When set, the Variant column shows that item
    /// (icon + link, resolved through the mapping) instead of <see cref="MappingVariantLabel"/>.
    /// </summary>
    public string? MappingVariantItemType { get; set; }

    /// <summary>Explicit display order among the chain's variants (wiki mapping <c>variantOrder = N</c>).
    /// Null = unset; renderer falls back to legacy level/name ordering.</summary>
    public int? MappingVariantOrder { get; set; }

    /// <summary>Wiki mapping <c>groupOdds = true</c> — Drop Odds rows grouped by variant.</summary>
    public bool MappingGroupOdds { get; set; }

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
    /// A pass-through stage: the item appears and within seconds turns into something else, so
    /// the wiki never links to it — a transform or decay that lands here is folded through to
    /// whatever it becomes, carrying the odds of that roll. Set from the mapping flag
    /// <c>isTransient = true</c>. See <c>_CONTEXT/Game/Eventy.md</c> (Voyance's House loop).
    /// </summary>
    public bool IsTransient { get; set; }

    /// <summary>First-playthrough-only copy (mapping <c>isFtue</c>) — excluded from aggregated cells.</summary>
    public bool IsFtue { get; set; }

    /// <summary>
    /// Event points awarded when this item is CREATED by merging
    /// (<c>Rewards[].RewardCollectibleBoardEventProgress.Amount</c>). Null when the item gives none.
    /// </summary>
    public int? EventPointsOnCreate { get; set; }

    /// <summary>
    /// Event points awarded when this item is TAPPED
    /// (<c>CollectableFeatures.CollectAction.Progress</c>). Usually the larger of the two: Amelia
    /// Boulton Memorabilia pays half for creating and full for tapping, while Suspect Interviews
    /// pays the same either way.
    /// </summary>
    public int? EventPointsOnTap { get; set; }

    /// <summary>
    /// Id of the side track this item feeds when tapped (<c>CollectableFeatures.CollectAction.TrackId</c>),
    /// e.g. <c>LDE_MurderAtTheMansion_SubGoal</c> for the Old Map. A track id means the item's
    /// <see cref="EventPointsOnTap"/> is progress on that track, NOT event points — which is why the
    /// Points Item type and the points columns gate on <see cref="EventPointsOnCreate"/> instead.
    /// </summary>
    public string? CollectTrackId { get; set; }

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
