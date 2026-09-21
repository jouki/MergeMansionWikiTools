using GameLogic.Config;
using GameLogic.Merge;
using GameLogic.MergeChains;
using GameLogic.Player.Items;
using GameLogic.Player.Items.Activation;
using GameLogic.Player.Items.Collectable;
using GameLogic.Player.Items.Production;
using GameLogic.Player.Items.Sink;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// <para>
/// The one converter that owns every shape in <c>chain_item_odds.json</c> that plain reflection
/// cannot produce: the merge chain wrapper, its elements, <see cref="ItemDefinition"/> itself, the
/// activation block (whose JSON names are the player-facing charge vocabulary, not the config field
/// names) and the whole <c>IItemProducer</c>/<c>IOrderProducer</c> family. Two more partials carry
/// the bulky parts: <c>ChainSerializer.Producers.cs</c> (every producer kind) and
/// <c>ChainSerializer.ItemValues.cs</c> (the computed per-item numbers).
/// </para>
/// <para>
/// Everything the converter does NOT claim — every <c>*Features</c> object, merge mechanics, cycles,
/// requirements, rewards — is deliberately left to Newtonsoft's default reflection, which writes a
/// type's public properties in declaration order. That is exactly the golden order for those types
/// (verified against <c>DecayFeatures</c>, <c>ChestFeatures</c>, <c>SinkFeatures</c>,
/// <c>SpawnFeatures</c>, <c>CollectableFeatures</c>, <c>MergeFeatures</c> and the four merge
/// mechanics), so claiming them would only risk drift.
/// </para>
/// </summary>
public sealed partial class ChainSerializer : JsonConverter
{
    private readonly SharedGameConfig _config;
    private readonly bool _dropsAsPercent;
    private readonly IDumpLog _log;

    public ChainSerializer(SharedGameConfig config, bool dropsAsPercent, IDumpLog log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dropsAsPercent = dropsAsPercent;
        _log = log ?? ConsoleDumpLog.Instance;
    }

    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) =>
        typeof(MergeChainDefinition).IsAssignableFrom(objectType)
        || typeof(IMergeChainElement).IsAssignableFrom(objectType)
        || typeof(ItemDefinition).IsAssignableFrom(objectType)
        || typeof(ActivationFeatures).IsAssignableFrom(objectType)
        || typeof(IActivationCycle).IsAssignableFrom(objectType)
        || typeof(IItemProducer).IsAssignableFrom(objectType)
        || typeof(IOrderProducer).IsAssignableFrom(objectType)
        || objectType == typeof(ItemOdds)
        || objectType == typeof(MergeCollection)
        || objectType == typeof(TransformCollectAction)
        || objectType == typeof(SimpleSinkStateFactory)
        || objectType == typeof(SingleTargetSinkStateFactory)
        || objectType == typeof(TagSinkStateFactory);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        switch (value)
        {
            case null: writer.WriteNull(); return;
            case MergeChainDefinition chain: WriteChain(writer, chain, serializer); return;
            case SingleMergeChainElement single: WriteSingleElement(writer, single, serializer); return;
            case ListMergeChainElement list: WriteListElement(writer, list, serializer); return;
            case ItemDefinition item: WriteItem(writer, item, serializer); return;
            case ActivationFeatures activation: WriteActivationFeatures(writer, activation, serializer); return;
            case ActivationCycle cycle: WriteActivationCycle(writer, cycle, serializer); return;
            case ItemOdds odds: WriteItemOdds(writer, odds, serializer); return;
            case MergeCollection collection: WriteMergeCollection(writer, collection, serializer); return;
            case TransformCollectAction transform: WriteTransformCollectAction(writer, transform, serializer); return;
            case SimpleSinkStateFactory simple: WriteSinkFactory(writer, serializer, simple.Scores, simple.ScoreTarget, simple.RewardDef); return;
            case SingleTargetSinkStateFactory single: WriteSinkFactory(writer, serializer, single.Scores, single.ScoreTarget, single.RewardDef); return;
            case TagSinkStateFactory tag: WriteTagSinkFactory(writer, tag, serializer); return;
            case IItemProducer or IOrderProducer: WriteProducer(writer, value, serializer); return;
            // Only reachable if a future game version adds another IActivationCycle implementation;
            // the TagId-ordered member dump is a better guess than silence.
            default: MetaObjectWriter.WriteObject(writer, value, serializer, _log); return;
        }
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(ChainSerializer)} is write-only (dumper never deserializes).");

    // ── chain + elements ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A chain is the localized category <c>Name</c> (omitted when the localization has no entry for
    /// it) followed by the definition's own <c>[MetaMember]</c>s in TagId order.
    /// </summary>
    private void WriteChain(JsonWriter w, MergeChainDefinition chain, JsonSerializer s)
    {
        w.WriteStartObject();
        if (TryGetChainName(chain, out var name))
        {
            w.WritePropertyName("Name");
            w.WriteValue(name);
        }
        MetaObjectWriter.WriteMembers(w, chain, s, _log);
        w.WriteEndObject();
    }

    /// <summary>
    /// <para>
    /// The chain's display name is the localized item CATEGORY of its first item. Three keys are
    /// tried, in this order, and the first one the active language actually has wins:
    /// <c>ItemCategory_&lt;chainId&gt;</c>, the item's <c>OverrideLocalizationItemCategory</c> used
    /// as a whole key, and finally <c>ItemCategory_&lt;first item's pool tag&gt;</c>. Chains that
    /// match none of the three are written without a <c>Name</c>.
    /// </para>
    /// <para>
    /// All three steps are needed and the order is load-bearing: <c>OrangeFlower</c> takes its own
    /// chain key ("A beautiful Orange Flower") over its pool tag's ("Flower"); <c>TimeSkipBooster2</c>
    /// has no chain key and falls through to the <c>TimeSkipBoosterItem</c> pool tag ("Time Skip
    /// Booster"); <c>TCE_WildCardSpecial</c> has neither and only the override
    /// (<c>TCE_Generic_InformantTip_Special</c>) resolves it.
    /// </para>
    /// <para>
    /// <b>Override before pool tag (v0.24.73).</b> The game reads the override first and only falls
    /// back to <c>Concat("ItemCategory_", PoolTag)</c> when it is empty — the ISIL of
    /// <c>LocMan.GetItemCategoryName(IItemDefinition)</c> jumps straight to the return on a non-empty
    /// override. Having it last cost 171 chains their name, most visibly the 155 Season Pass chests
    /// that share the pool tag <c>MysteryPassChest</c>: five of them per pass collapsed onto
    /// "Mystery Streak Chest" instead of "Challenge Chest 1"…"5". The chain-id key stays ahead of
    /// both because it belongs to the game's other resolution path, the one keyed by
    /// <c>MergeChainId</c>.
    /// </para>
    /// <para>
    /// <b>Empirical, mechanism unverified:</b> a chain whose first item is tagged <c>Test</c> or
    /// <c>Artifact</c> never gets a name, even when its keys resolve perfectly well (<c>Radio</c>,
    /// <c>MakeupTools</c>, <c>ArtifactDrawer</c> …). The rule was derived by fitting the 26.07.01
    /// golden, where it separates all 3661 chains without a single exception (2785 named, 876 not);
    /// a plausible reading is that legacy resolves categories through a pool-tag map that dev-only
    /// items are kept out of, but that is a guess.
    /// </para>
    /// </summary>
    private bool TryGetChainName(MergeChainDefinition chain, out string name)
    {
        name = null!;
        var first = FirstItem(chain);
        if (first == null) return false;
        if (first.Tags != null && first.Tags.Any(t => NonContentTags.Contains(t))) return false;

        if (chain.ConfigKey?.Value is { Length: > 0 } chainKey
            && LocMan.TryGet($"ItemCategory_{chainKey}", out name)) return true;
        if (first.OverrideLocalizationItemCategory is { Length: > 0 } overrideKey
            && LocMan.TryGet(overrideKey, out name)) return true;
        return first.PoolTag is { Length: > 0 } poolTag
            && LocMan.TryGet($"ItemCategory_{poolTag}", out name);
    }

    /// <summary>Item tags that mark dev-only content and suppress the chain name (see <see cref="TryGetChainName"/>).</summary>
    private static readonly HashSet<string> NonContentTags = new(StringComparer.Ordinal) { "Test", "Artifact" };

    /// <summary>The item at the head of the chain's primary path, or null for an empty chain.</summary>
    private ItemDefinition? FirstItem(MergeChainDefinition chain)
    {
        var element = chain.PrimaryChain?.FirstOrDefault();
        return element switch
        {
            SingleMergeChainElement single => Resolve(single.Item),
            ListMergeChainElement list => Resolve(list.Items?.FirstOrDefault()),
            _ => null,
        };
    }

    /// <summary>A single-item chain step: the resolved item plus the element's own Count.</summary>
    private void WriteSingleElement(JsonWriter w, SingleMergeChainElement element, JsonSerializer s)
    {
        w.WriteStartObject();
        w.WritePropertyName("Item");
        s.Serialize(w, Resolve(element.Item));
        w.WritePropertyName("Count");
        w.WriteValue(element.Count);
        w.WriteEndObject();
    }

    /// <summary>
    /// A multi-item chain step (chains where one level has several interchangeable items). The
    /// config often repeats the same item several times inside one element (a level with a single
    /// variant listed once per slot); only the distinct items are written, in first-seen order.
    /// </summary>
    private void WriteListElement(JsonWriter w, ListMergeChainElement element, JsonSerializer s)
    {
        w.WriteStartObject();
        w.WritePropertyName("Items");
        w.WriteStartArray();
        var seen = new HashSet<int>();
        foreach (var def in element.Items ?? new List<ItemDef>())
        {
            if (def == null || !seen.Add(def.ConfigKey)) continue;
            s.Serialize(w, Resolve(def));
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    // ── item ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <para>An item is a block of computed/localized values followed by its <c>[MetaMember]</c>s in
    /// TagId order. The prefix, in golden order:</para>
    /// <c>OverrideLocKey</c>, <c>FullOverrideLocKey</c> (only when set), <c>Name</c>,
    /// <c>ItemType</c>, <c>Description</c>, <c>UnlockRequirements</c>, <c>SellCoins</c>,
    /// <c>WaterDropletCountMin/Max</c> (fishing rods), <c>RequiredItemValue</c>/
    /// <c>RewardItemValue</c> (anything bubbleable), the extra-spawn currency/token values, and
    /// <c>WeightedAvgTSP</c>/<c>SpeedUpCostGems</c> (anything that produces items).
    /// <para><c>ItemType</c> (TagId 35) and <c>UnlockRequirements</c> (TagId 42) are hoisted into the
    /// prefix and therefore suppressed when the reflected block reaches them.</para>
    /// </summary>
    private void WriteItem(JsonWriter w, ItemDefinition item, JsonSerializer s)
    {
        var overrideKey = item.OverrideLocalizationItemKey;
        var fullOverrideKey = item.FullOverrideLocalizationItemKey;

        w.WriteStartObject();

        if (overrideKey != null) { w.WritePropertyName("OverrideLocKey"); w.WriteValue(overrideKey); }
        if (fullOverrideKey != null) { w.WritePropertyName("FullOverrideLocKey"); w.WriteValue(fullOverrideKey); }

        w.WritePropertyName("Name");
        w.WriteValue(Loc.SafeLoc(() => LocMan.GetItemName(item.ItemType, overrideKey, fullOverrideKey), item.ItemType, _log, item.ItemType));

        w.WritePropertyName("ItemType");
        w.WriteValue(item.ItemType);

        w.WritePropertyName("Description");
        w.WriteValue(Loc.SafeLoc(() => LocMan.GetDescription(item.ItemType, item.LevelNumber, overrideKey, fullOverrideKey), "", _log, $"description of {item.ItemType}"));

        if (item.UnlockRequirements != null)
        {
            w.WritePropertyName("UnlockRequirements");
            s.Serialize(w, item.UnlockRequirements);
        }

        w.WritePropertyName("SellCoins");
        w.WriteValue(item.GetItemSellPrice(_config.SharedGlobals));

        WriteWaterDroplets(w, item);
        WriteItemTradeValues(w, item);
        WriteExtraSpawnValues(w, item);
        WriteSpeedUpCost(w, item);

        MetaObjectWriter.WriteMembers(w, item, s, _log, (name, value) =>
        {
            if (name is "ItemType" or "UnlockRequirements") return true; // already written above
            if (name == "TimeSkipPriceGems")
            {
                // The shared MetaMath converter rounds F32/F64 up; this one member keeps its exact
                // double, because the speed-up cost of every generator is derived from it (see
                // _CONTEXT/App/DataPipeline.md §2.3) and a ceiled value cannot be un-rounded.
                w.WritePropertyName(name);
                w.WriteValue(item.TimeSkipPriceGems.Double);
                return true;
            }
            if (name.Length > 1 && name[0] == '_')
            {
                // The feature blocks are private backing fields (_MergeFeatures, _TimeContainer, ...);
                // the JSON key is the name without the underscore.
                w.WritePropertyName(name[1..]);
                s.Serialize(w, value);
                return true;
            }
            return false;
        });

        w.WriteEndObject();
    }

    // ── activation ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Activation is the only feature block with a hand-written shape: <c>Activable</c> is hoisted to
    /// the front, <c>MaxCharges</c> is injected (it exists nowhere in the config — see
    /// <see cref="ChargeMath"/>), <c>StorageMax</c> may be rewritten to the real lifetime drop count,
    /// and the two derived booleans that are not <c>[MetaMember]</c>s at all
    /// (<c>DecayAfterLastCycleAndActivation</c>, <c>HasDecayDelay</c>) are dropped.
    /// <c>ActivationStartTime</c> and <c>DecayDelay</c> go through the same write-if-not-null helper
    /// as their neighbours; both are null on every item in the corpus, so today they never appear,
    /// but a config that sets one would carry it rather than silently lose it.
    /// </summary>
    private void WriteActivationFeatures(JsonWriter w, ActivationFeatures features, JsonSerializer s)
    {
        var (maxCharges, storageMax) = ChargeMath(features);

        w.WriteStartObject();
        w.WritePropertyName("Activable");
        w.WriteValue(features.Activable);
        WriteIfNotNull(w, s, "ActivationSpawn", features.ActivationSpawn);
        WriteIfNotNull(w, s, "Placement", features.Placement);
        WriteIfNotNull(w, s, "ActivationCycle", features.ActivationCycle);
        if (maxCharges != null) { w.WritePropertyName("MaxCharges"); w.WriteValue(maxCharges.Value); }
        w.WritePropertyName("StorageMax");
        w.WriteValue(storageMax);
        WriteIfNotNull(w, s, "DecayAfterLastCycleProducer", features.DecayAfterLastCycleProducer);
        w.WritePropertyName("SpawnVisibility");
        s.Serialize(w, features.SpawnVisibility);
        w.WritePropertyName("StartsFull");
        w.WriteValue(features.StartsFull);
        WriteIfNotNull(w, s, "ActivationRequirements", features.ActivationRequirements);
        WriteIfNotNull(w, s, "ActivationStartTime", features.ActivationStartTime);
        if (features.ActivationCost != null) { w.WritePropertyName("ActivationCost"); w.WriteValue(features.ActivationCost.Value); }
        w.WritePropertyName("ShowTapTextOnDiscovery");
        w.WriteValue(features.ShowTapTextOnDiscovery);
        w.WritePropertyName("AllowCooldownRemover");
        w.WriteValue(features.AllowCooldownRemover);
        w.WritePropertyName("AllowEnergyMode");
        w.WriteValue(features.AllowEnergyMode);
        WriteIfNotNull(w, s, "DecayDelay", features.DecayDelay);
        w.WriteEndObject();
    }

    /// <summary>
    /// Player-perspective charge accounting (<c>_CONTEXT/App/DataPipeline.md</c> §2.2). Drops per
    /// charge = mini-charges × drops per mini-charge; how the raw <c>StorageMax</c> relates to that
    /// depends on whether the generator refills forever and whether it starts full:
    /// <list type="bullet">
    /// <item><description>infinite cycles: the reported StorageMax is raised to a whole charge if the
    /// config set it lower, and taps per cycle = StorageMax / dpc.</description></item>
    /// <item><description>finite + StartsFull: raw StorageMax is per-cycle capacity, so taps per cycle
    /// = max(1, StorageMax / dpc), storage kept.</description></item>
    /// <item><description>finite + not StartsFull: one batch tap per cycle, and StorageMax is
    /// rewritten to the lifetime drop count (cycles × dpc).</description></item>
    /// </list>
    /// <c>MaxCharges</c> is then taps per cycle × cycles for finite generators, taps per cycle for
    /// infinite ones.
    /// </summary>
    private static (int? MaxCharges, int StorageMax) ChargeMath(ActivationFeatures features)
    {
        int storageMax = features.StorageMax;
        if (features.ActivationCycle is not ActivationCycle cycle
            || cycle.DailyActivationCyclesData is not ActivationCycleData data
            || data.ActivationAmountInCycle is not { Count: > 0 } amounts
            || data.HowManyAreGeneratedInCycle is not { Count: > 0 } generated)
            return (null, storageMax);

        // 9999 mini-charges is the config's "never runs out" sentinel (card decks and similar
        // always-available taps). Charges are meaningless there, so no MaxCharges is written and the
        // raw StorageMax is left alone — 32 such generators in the 26.07.01 golden.
        if (amounts[0] == InfiniteChargeSentinel) return (null, storageMax);

        int dropsPerCharge = amounts[0] * generated[0];
        if (dropsPerCharge <= 0) return (null, storageMax);

        int cycles = cycle.HowManyCycles;
        int tapsPerCycle;
        if (cycles == -1)
        {
            // Refills forever: storage is the tap budget, but it can be configured below one full
            // charge (Shoes L3: storage 3, 5 drops per charge) — the player still gets the whole
            // charge, so the reported storage is at least one charge's worth.
            storageMax = System.Math.Max(storageMax, dropsPerCharge);
            tapsPerCycle = storageMax / dropsPerCharge;
        }
        else if (features.StartsFull)
        {
            tapsPerCycle = System.Math.Max(1, storageMax / dropsPerCharge);
        }
        else
        {
            tapsPerCycle = 1;
            storageMax = cycles * dropsPerCharge;
        }

        return (cycles >= 1 ? tapsPerCycle * cycles : tapsPerCycle, storageMax);
    }

    /// <summary>Config value for "this generator never depletes" (see <see cref="ChargeMath"/>).</summary>
    private const int InfiniteChargeSentinel = 9999;

    /// <summary>
    /// The activation cycle in player vocabulary: the config's <c>ActivationDelay</c> /
    /// <c>FirstCycleStartDelay</c> plus the first entry of each per-cycle list become the
    /// mini-charge / charge cooldowns and counts the wiki and the app speak in.
    /// </summary>
    private void WriteActivationCycle(JsonWriter w, ActivationCycle cycle, JsonSerializer s)
    {
        var data = cycle.DailyActivationCyclesData as ActivationCycleData;

        w.WriteStartObject();
        w.WritePropertyName("MiniChargeCooldown");
        w.WriteValue(cycle.ActivationDelay.Milliseconds);
        w.WritePropertyName("InitialCooldown");
        w.WriteValue(cycle.FirstCycleStartDelay.Milliseconds);
        w.WritePropertyName("HowManyCycles");
        w.WriteValue(cycle.HowManyCycles);
        if (data?.DelaysBetweenCycles is { Count: > 0 } delays)
        {
            w.WritePropertyName("ChargeCooldown");
            w.WriteValue(delays[0].Milliseconds);
        }
        if (data?.ActivationAmountInCycle is { Count: > 0 } amounts)
        {
            w.WritePropertyName("MiniChargesInSingleCharge");
            w.WriteValue(amounts[0]);
        }
        if (data?.HowManyAreGeneratedInCycle is { Count: > 0 } generated)
        {
            w.WritePropertyName("DropsInSingleMiniCharge");
            w.WriteValue(generated[0]);
        }
        if (data?.TimerSkipMultiplier is { Count: > 0 } multipliers)
        {
            w.WritePropertyName("TimerSkipMultiplier");
            w.WriteValue(multipliers[0].Double);
        }
        w.WriteEndObject();
    }

    // ── odds entries + merge collections ────────────────────────────────────────────────────────

    /// <summary>
    /// A weighted catch entry (fishing rods). Unlike everywhere else, <c>Type</c> is expanded into
    /// the full item definition rather than its item-type string.
    /// </summary>
    private void WriteItemOdds(JsonWriter w, ItemOdds odds, JsonSerializer s)
    {
        w.WriteStartObject();
        w.WritePropertyName("Type");
        s.Serialize(w, Resolve(odds.Type));
        w.WritePropertyName("Weight");
        w.WriteValue(odds.Weight);
        w.WriteEndObject();
    }

    /// <summary>
    /// Collecting a transform item swaps it for another one — and the target is written out in
    /// full, not as an item-type string, because a reader of the chain dump has no other way to see
    /// what the collected item becomes (all 14 in the 26.07.01 golden are expanded).
    /// </summary>
    private void WriteTransformCollectAction(JsonWriter w, TransformCollectAction action, JsonSerializer s)
    {
        w.WriteStartObject();
        w.WritePropertyName("TransformsInto");
        s.Serialize(w, Resolve(action.TransformsInto));
        w.WriteEndObject();
    }

    /// <summary>
    /// The single-target sink factories (a transformative item that consumes N items and hands back
    /// one reward). Like <see cref="WriteTransformCollectAction"/>, the reward is expanded to the
    /// full item. Their multi-target sibling is left to default reflection and keeps the plain
    /// item-type string — 603 of the 617 sink factories in the golden take that path.
    /// </summary>
    private void WriteSinkFactory(JsonWriter w, JsonSerializer s, Dictionary<int, int>? scores, int scoreTarget, ItemDef? rewardDef)
    {
        w.WriteStartObject();
        if (scores != null)
        {
            w.WritePropertyName("Scores");
            s.Serialize(w, scores);
        }
        w.WritePropertyName("ScoreTarget");
        w.WriteValue(scoreTarget);
        if (rewardDef != null)
        {
            w.WritePropertyName("RewardDef");
            s.Serialize(w, Resolve(rewardDef));
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// A tag sink, plus the two tables needed to read it — a deliberate divergence from golden.
    /// <para>
    /// Tag/InputCount/RewardTagName are private fields, which default reflection would drop, so
    /// they go through the MetaMember writer (that part matches golden). What golden does NOT have
    /// is any way to answer "which fuel gives which reward": the factory only names a
    /// <c>RewardTagName</c>, and the game resolves the result from the <b>summed SinkPoints</b> of
    /// what was consumed, against the <c>TagRewards</c> library. Nothing else in any dump file
    /// carries that library, so the reward of a tag sink used to be underivable — visible on the
    /// wiki as a DNA Kit page that said nothing about Murder Weapons and hardcoded one drop count,
    /// although the four weapons give runs of 8, 10, 12 and 20.
    /// </para>
    /// <para>
    /// Two extra keys therefore follow, both resolved to item types:
    /// <list type="bullet">
    /// <item><c>TagFuel</c> — every item carrying this <c>Tag</c>, with the <c>SinkPoints</c> it
    /// contributes.</item>
    /// <item><c>TagRewards</c> — point total to the items produced at that total.</item>
    /// </list>
    /// They are kept as two tables rather than one joined list because the join is only trivial at
    /// <c>InputCount == 1</c>; at 3 (Tarot Table, Kitchen Tools) a total is a sum over three items.
    /// </para>
    /// <para>
    /// Byte parity with Legacy stopped being a criterion in v0.24.73, and this is the second
    /// recorded deliberate divergence — see <c>Dumper/NativeDumperRules.md</c>.
    /// </para>
    /// </summary>
    private void WriteTagSinkFactory(JsonWriter w, TagSinkStateFactory factory, JsonSerializer s)
    {
        w.WriteStartObject();

        // The three private [MetaMember]s, in TagId order, exactly as golden has them. The hook
        // only observes; returning false leaves the writing to WriteMembers.
        string? tag = null, rewardTagName = null;
        MetaObjectWriter.WriteMembers(w, factory, s, _log, (name, value) =>
        {
            if (name == "Tag") tag = value as string;
            else if (name == "RewardTagName") rewardTagName = value as string;
            return false;
        });

        List<ItemDefinition?> fuel = new();
        if (!string.IsNullOrEmpty(tag) && _config.Items != null)
        {
            // EnumerateAll() is the untyped IGameConfigLibrary view, hence the cast.
            fuel = _config.Items.EnumerateAll()
                .Select(kv => kv.Value as ItemDefinition)
                .Where(i => i != null && string.Equals(i.SinkTag, tag, StringComparison.Ordinal))
                .OrderBy(i => i!.SinkPoints)
                .ThenBy(i => i!.ItemType, StringComparer.Ordinal)
                .ToList();
            if (fuel.Count > 0)
            {
                w.WritePropertyName("TagFuel");
                w.WriteStartArray();
                foreach (var item in fuel)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("Item");
                    w.WriteValue(item!.ItemType);
                    w.WritePropertyName("SinkPoints");
                    w.WriteValue(item.SinkPoints);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
        }

        if (!string.IsNullOrEmpty(rewardTagName) && _config.TagRewards != null)
        {
            var rewards = _config.TagRewards.EnumerateAll()
                .Select(kv => kv.Value as TagRewardsInfo)
                .Where(r => r != null && string.Equals(r.RewardTagName, rewardTagName, StringComparison.Ordinal))
                .OrderBy(r => r!.TotalPoints)
                .ToList();
            if (rewards.Count > 0)
            {
                w.WritePropertyName("TagRewards");
                w.WriteStartArray();
                foreach (var reward in rewards)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("TotalPoints");
                    w.WriteValue(reward!.TotalPoints);

                    // TotalPoints is the SUM of the SinkPoints of everything consumed, so at
                    // InputCount 1 it is just the one item's SinkPoints and the row can name the
                    // fuel outright — no cross-referencing TagFuel by hand. Above 1 a total is a
                    // sum over several items (3 Tarot Cards make totals 3-9) and no single fuel
                    // owns the row, so the key is left out rather than guessed at.
                    var inputCount = InputCountOf(factory);
                    if (inputCount == 1)
                    {
                        var owners = fuel.Where(i => i!.SinkPoints == reward.TotalPoints).ToList();
                        if (owners.Count > 0)
                        {
                            w.WritePropertyName("Fuel");
                            w.WriteStartArray();
                            foreach (var owner in owners) w.WriteValue(owner!.ItemType);
                            w.WriteEndArray();
                        }
                    }

                    w.WritePropertyName("Produces");
                    w.WriteStartArray();
                    foreach (var itemType in RewardItemTypes(reward))
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("Item");
                        w.WriteValue(itemType);
                        WriteProducedDetail(w, s, itemType);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
        }

        w.WriteEndObject();
    }

    /// <summary>
    /// The produced item's own drop count and spawn table, written into the TagRewards row itself.
    /// The spawn goes in unconditionally: a consumer needs to know WHAT is dropped, not only how
    /// many, and a single-outcome run (the DNA Kit's) would otherwise carry no item at all.
    /// <para>
    /// Not redundant with the item reference next to it: <b>23 of the 40 tag-reward targets in
    /// 26.07.01 are not in chain_item_odds.json at all</b> (every compost and donation-box
    /// producer, the kitchen-utensil decays), because they belong to no PrimaryChain. For those the
    /// reference is a dangling id and this is the only place the numbers appear.
    /// </para>
    /// <para>
    /// There is deliberately no "points to drops" factor anywhere: the relation is a lookup table,
    /// not a multiplier (MaaTM runs 1 to 8, 2 to 10, 3 to 12, 4 to 20 drops).
    /// </para>
    /// </summary>
    private void WriteProducedDetail(JsonWriter w, JsonSerializer s, string itemType)
    {
        var produced = _config.Items?.EnumerateAll()
            .Select(kv => kv.Value as ItemDefinition)
            .FirstOrDefault(i => i != null && string.Equals(i.ItemType, itemType, StringComparison.Ordinal));
        if (produced?.ActivationFeatures is not ActivationFeatures af) return;

        // An endless producer's storage is a sentinel (9999), not a drop count.
        var cycles = (af.ActivationCycle as ActivationCycle)?.HowManyCycles;
        if (cycles != -1)
        {
            var (_, storageMax) = ChargeMath(af);
            if (storageMax > 0)
            {
                w.WritePropertyName("Drops");
                w.WriteValue(storageMax);
            }
        }

        if (af.ActivationSpawn != null)
        {
            w.WritePropertyName("DropOdds");
            s.Serialize(w, af.ActivationSpawn);
        }
    }

    /// <summary><c>InputCount</c> is a private [MetaMember], so it is read the same way.</summary>
    private int InputCountOf(TagSinkStateFactory factory)
    {
        foreach (var (name, _, get) in MetaObjectWriter.Members(factory))
            if (name == "InputCount")
                return get() is int count ? count : 1;
        return 1;
    }

    /// <summary>
    /// Distinct item types a TagRewards row can produce. The same target is normally listed twice
    /// with equal weight; the Tarot Table is the one place where a total really can roll two
    /// different results, so this de-duplicates rather than taking the first.
    /// </summary>
    private IEnumerable<string> RewardItemTypes(TagRewardsInfo reward)
    {
        var seen = new List<string>();
        if (reward.ItemProducer is not ControlledRandomProducer producer || producer.GenerationOdds == null)
            return seen;
        foreach (var odds in producer.GenerationOdds)
        {
            var itemType = ItemTypeOf(odds?.Type);
            if (itemType != null && !seen.Contains(itemType, StringComparer.Ordinal))
                seen.Add(itemType);
        }
        return seen;
    }

    /// <summary>
    /// A merge collection's pair-keyed producer map, flattened to <c>"&lt;itemA&gt;:&lt;itemB&gt;"</c>
    /// keys (the pair object itself would not be a legal JSON property name).
    /// </summary>
    private void WriteMergeCollection(JsonWriter w, MergeCollection collection, JsonSerializer s)
    {
        w.WriteStartObject();
        foreach (var (pair, producer) in collection.Collection ?? new Dictionary<MergeCollection.ItemPair, IItemProducer>())
        {
            w.WritePropertyName($"{ItemTypeOf(pair.First)}:{ItemTypeOf(pair.Second)}");
            s.Serialize(w, producer);
        }
        w.WriteEndObject();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static void WriteIfNotNull(JsonWriter w, JsonSerializer s, string name, object? value)
    {
        if (value == null) return;
        w.WritePropertyName(name);
        s.Serialize(w, value);
    }

    private ItemDefinition? Resolve(ItemDef? def)
        => def != null && _config.Items != null && _config.Items.TryGetValue(def.ConfigKey, out var item) ? item : null;

    /// <summary>
    /// An item id as its item-type string, or — when the config has no such item — the id in
    /// 8-digit uppercase hex, which is how a dangling reference shows up in golden (e.g. the
    /// Perfumery distillery's order producer points at <c>0012A3F0</c>).
    /// </summary>
    private string ItemTypeOf(int configKey)
        => _config.Items != null && _config.Items.TryGetValue(configKey, out var item) && item?.ItemType != null
            ? item.ItemType
            : configKey.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    private string? ItemTypeOf(ItemDef? def) => def == null ? null : ItemTypeOf(def.ConfigKey);

    /// <summary>
    /// Localization helpers throw on malformed item types (<c>GetItemName</c> insists on a
    /// <c>_&lt;level&gt;</c> suffix). A broken item must not take the whole dump down.
    /// </summary>
}
