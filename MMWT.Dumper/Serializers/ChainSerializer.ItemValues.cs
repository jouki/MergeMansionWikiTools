using GameLogic.Config;
using GameLogic.Player.Items;
using GameLogic.Player.Items.Activation;
using GameLogic.Player.Items.Fishing;
using GameLogic.Player.Items.Production;
using GameLogic.Player.Items.Spawning;
using Metaplay.Core.Math;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// The computed per-item values half of <see cref="ChainSerializer"/>: everything an item carries in
/// <c>chain_item_odds.json</c> that is nowhere in the config as such but derived from it — the water
/// droplet spread of a fishing rod, the two Daily Trade valuations, the extra-spawn currency and
/// token payouts, and the gem price of skipping one production cycle.
/// </summary>
public sealed partial class ChainSerializer
{
    /// <summary>
    /// Fishing rods advertise the spread of water droplets their catches are worth: the min and max
    /// over every item in <c>FishingRodFeatures.ItemOdds</c>, looked up in
    /// <c>FishingSettings.SmallFishWaterDropletCounts</c> and, failing that,
    /// <c>NonFishWaterDropletCounts</c>.
    /// </summary>
    private void WriteWaterDroplets(JsonWriter w, ItemDefinition item)
    {
        if (item.FishingRodFeatures is not FishingRodFeatures rod || rod.ItemOdds == null || rod.ItemOdds.Count == 0) return;
        var settings = _config.FishingSettings;
        if (settings == null) return;

        int? min = null, max = null;
        foreach (var odds in rod.ItemOdds)
        {
            var key = odds?.Type?.ConfigKey;
            if (key == null) continue;
            int count;
            if (settings.SmallFishWaterDropletCounts?.TryGetValue(key.Value, out count) != true
                && settings.NonFishWaterDropletCounts?.TryGetValue(key.Value, out count) != true)
                continue;
            min = min == null ? count : System.Math.Min(min.Value, count);
            max = max == null ? count : System.Math.Max(max.Value, count);
        }
        if (min == null) return;

        w.WritePropertyName("WaterDropletCountMin");
        w.WriteValue(min.Value);
        w.WritePropertyName("WaterDropletCountMax");
        w.WriteValue(max!.Value);
    }

    /// <summary>
    /// The two Daily Trade valuations every bubbleable item carries: the bubble's gem cost scaled by
    /// the chain's requirement / reward multiplier from <c>DailyTasksV2MergeChains</c> (1.0 when the
    /// chain has no entry).
    /// <para>
    /// Two traps here, both confirmed against golden. First, the multipliers are F32 and are NOT the
    /// values printed in <c>events.json</c> — those go through the ceiling-applying MetaMath
    /// converter, so a real 0.3 reads as 1.0 there and a real 1.2 reads as 2.0. Second, the scaling
    /// happens in <b>fixed point</b>: <c>int * F32</c> multiplies the raw 16.16 value in a 32-bit
    /// int, which silently wraps once the product passes ~32767. That is not a rounding artifact to
    /// be smoothed over — it is visible in the golden data (chain <c>Song</c>, bubble cost 46 000,
    /// writes <c>-19536</c>), so the arithmetic is reproduced exactly rather than done in doubles.
    /// </para>
    /// </summary>
    private void WriteItemTradeValues(JsonWriter w, ItemDefinition item)
    {
        var bubble = item.BubbleFeatures;
        if (bubble == null) return;

        F32 requirement = F32.FromInt(1), reward = F32.FromInt(1);
        var chainId = item.MergeChainDef?.ConfigKey;
        if (chainId != null && _config.DailyTasksV2MergeChains != null
            && _config.DailyTasksV2MergeChains.TryGetValue(chainId, out var info) && info != null)
        {
            requirement = info.RequirementMultiplier;
            reward = info.RewardMultiplier;
        }

        w.WritePropertyName("RequiredItemValue");
        w.WriteValue(F32.RoundToInt(bubble.OpenQuantity * requirement));
        w.WritePropertyName("RewardItemValue");
        w.WriteValue(F32.RoundToInt(bubble.OpenQuantity * reward));
    }

    /// <summary>
    /// Per-item extra-spawn payouts, one JSON key per currency / core-support-event token
    /// (<c>DigEventTaps</c>, <c>QuaternaryEnergy</c>, ...), in the config's own order.
    /// </summary>
    private void WriteExtraSpawnValues(JsonWriter w, ItemDefinition item)
    {
        var values = ExtraSpawnHelper.GetItemValues(_config, item.ConfigKey);
        if (values == null) return;
        foreach (var (key, value) in values)
        {
            w.WritePropertyName(key);
            w.WriteValue(value);
        }
    }

    /// <summary>
    /// The gem price of skipping one full production cycle, plus the weighted average time-skip
    /// price it is derived from. See <c>_CONTEXT/App/DataPipeline.md</c> §2.3/§2.4:
    /// <c>ceil(amountPerCycle × generatedPerStep × weightedAvgTSP)</c>, over the activation spawner
    /// for tap generators and the spawn cycle for automatic spawners.
    /// </summary>
    private void WriteSpeedUpCost(JsonWriter w, ItemDefinition item)
    {
        var (cost, avgTsp) = ComputeSpeedUpCost(item);
        if (cost == null) return;
        w.WritePropertyName("WeightedAvgTSP");
        w.WriteValue(avgTsp!.Value);
        w.WritePropertyName("SpeedUpCostGems");
        w.WriteValue(cost.Value);
    }

    private (int? Cost, double? AvgTsp) ComputeSpeedUpCost(ItemDefinition item)
    {
        IItemSpawner? spawner = null;
        int amount = 0, generated = 0;

        if (item.ActivationFeatures is ActivationFeatures activation
            && activation.ActivationSpawn != null
            && activation.ActivationCycle is ActivationCycle cycle
            && cycle.DailyActivationCyclesData is ActivationCycleData data
            && data.ActivationAmountInCycle is { Count: > 0 } amounts
            && data.HowManyAreGeneratedInCycle is { Count: > 0 } generatedList)
        {
            spawner = activation.ActivationSpawn;
            amount = amounts[0];
            generated = generatedList[0];
        }
        else if (item.SpawnFeatures is SpawnFeatures spawn && spawn.Spawn != null && spawn.SpawnCycle is SpawnCycle spawnCycle)
        {
            spawner = spawn.Spawn;
            amount = spawnCycle.SpawnAmountInCycle;
            generated = spawnCycle.HowManyAreGeneratedPerSpawn;
        }

        if (spawner == null) return (null, null);
        var avg = ComputeWeightedAvgTsp(spawner);
        // A producer kind with no odds of its own (the garage-cleanup event producer, an empty
        // producer) yields no average, and neither does one whose drops are all worth nothing —
        // golden carries no zero WeightedAvgTSP, its smallest is 0.164.
        if (avg is null or <= 0) return (null, null);
        return ((int)System.Math.Ceiling(amount * generated * avg.Value), avg);
    }

    /// <summary>
    /// The average gem worth of one drop from a spawner, weighted by the drop odds (exact bracketing
    /// in <see cref="WeightedTsp"/>). The API's own <c>TimeSkipPriceGems</c> implementations average
    /// <i>unweighted</i>, which does not match the in-game price, so the kinds that carry odds are
    /// computed here and only the rest fall back to the API.
    /// </summary>
    private double? ComputeWeightedAvgTsp(object producer)
    {
        switch (producer)
        {
            case PrefixProducer prefix when prefix.BaseProducer != null:
                return ComputeWeightedAvgTsp(prefix.BaseProducer);
            case ConstantProducer constant:
                return AverageTsp(constant.Products);
            case RandomProducer random:
                return WeightedTsp(random.OddsList);
            case ControlledRandomProducer controlled:
                return WeightedTsp(controlled.GenerationOdds);
            case ControlledRandomSequenceProducer sequence:
                return WeightedTsp(sequence.OddsList);
            case IItemSpawner spawner:
                // Everything else (predefined sequences above all — 500 generators) falls back to
                // the producer's own price. Several kinds only throw there; those simply get no
                // speed-up cost, exactly as in golden.
                try { return spawner.TimeSkipPriceGems(null).Double; }
                catch { return null; }
            default:
                return null;
        }
    }

    private double? AverageTsp(List<ItemDef>? products)
    {
        if (products == null || products.Count == 0) return null;
        double sum = 0;
        int count = 0;
        foreach (var def in products)
        {
            var item = Resolve(def);
            if (item == null) continue;
            sum += item.TimeSkipPriceGems.Double;
            count++;
        }
        return count == 0 ? null : sum / count;
    }

    /// <summary>
    /// Σ(TSP × (weight / total)) — the weight is turned into a fraction first, and only then scales
    /// the price. The bracketing is not cosmetic: summing the products and dividing once at the end
    /// lands on a different double for 325 of the generators in the 26.07.01 golden, and so does
    /// dividing each product instead of each weight.
    /// </summary>
    private double? WeightedTsp(List<ItemOdds>? odds)
    {
        if (odds == null || odds.Count == 0) return null;
        long totalWeight = 0;
        foreach (var entry in odds) totalWeight += entry?.Weight ?? 0;
        if (totalWeight == 0) return null;

        double weighted = 0;
        foreach (var entry in odds)
        {
            var item = Resolve(entry?.Type);
            if (item == null) continue;
            weighted += item.TimeSkipPriceGems.Double * (entry!.Weight / (double)totalWeight);
        }
        return weighted;
    }
}
