using System.Collections.Generic;
using GameLogic.Codex;
using GameLogic.Merge;
using GameLogic.MergeChains;
using GameLogic.Player.Items.Production;
using GameLogic.Player.Items;
using Metaplay.Core;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Linq;
using GameLogic.Config;
using GameLogic.Player.Items.Persistent;
using GameLogic.Player.Items.Activation;
using GameLogic.DailyTasksV2;
using GameLogic.Player.Items.Order;
using Metaplay.Core.Math;
using GameLogic.Player.Items.Sink;
using GameLogic.Player.Items.Spawning;

namespace merge_mansion_dumper.Dumper.Json.Metaplay
{
    class MetaMergeChainSerializer : BaseMetaJsonSerializer
    {
        private readonly ILogger _output;
        private readonly bool _dropAsPercent;
        private readonly SharedGameConfig _config;

        private readonly Type[] _supportedTypes =
        {
            typeof(MergeChainDefinition),
            typeof(MergeChainDef),
            typeof(CodexCategoryInfo),
            typeof(ItemDefinition),
            typeof(ItemDef),
            typeof(MergeCollection),
            typeof(IItemProducer),
            typeof(IOrderProducer),
            typeof(PersistentFeatures),
            typeof(ISinkStateFactory),
            typeof(IActivationCycle),
            typeof(ActivationFeatures),
            typeof(ListMergeChainElement)
        };

        public MetaMergeChainSerializer(SharedGameConfig config, bool dropAsPercent, ILogger output)
        {
            _config = config;
            _dropAsPercent = dropAsPercent;
            _output = output;
        }

        protected override Type[] GetTypes() => _supportedTypes;

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is MergeChainDefinition chainDef)
                SerializeMergeChain(writer, chainDef, serializer);
            if (value is MergeChainDef chainDef1)
                SerializeMergeChain(writer, chainDef1, serializer);
            if (value is CodexCategoryInfo codex)
                SerializeCodexCategory(writer, codex, serializer);
            else if (value is ItemDefinition item)
                SerializeItem(writer, item, serializer);
            else if (value is ItemDef item1)
                SerializeItem(writer, item1, serializer);
            else if (value is MergeCollection mergeCollection)
                SerializeMergeCollection(writer, mergeCollection, serializer);
            else if (value is IItemProducer producer)
                SerializeProducer(writer, producer, serializer);
            else if (value is IOrderProducer orderProducer)
                SerializeOrderProducer(writer, orderProducer, serializer);
            else if (value is ISinkStateFactory sinkFactory)
                WriteObject(writer, sinkFactory.GetType(), sinkFactory, serializer);
            else if (value is PersistentFeatures persistent)
                WriteObject(writer, persistent.GetType(), persistent, serializer);
            else if (value is ActivationFeatures afObj)
            {
                _currentAF = afObj;
                WriteObject(writer, afObj.GetType(), afObj, serializer);
                _currentAF = null;
            }
            else if (value is ActivationCycle ac)
                SerializeActivationCycle(writer, ac, serializer);
            else if (value is ListMergeChainElement listEl)
                SerializeListChainElement(writer, listEl, serializer);
        }

        /// <summary>
        /// Serializes one merge-chain position that holds a LIST of item refs.
        /// The game models a branching merge chain (random terminal merge, e.g. Flower Bed → A/B/C
        /// generators at 65/30/5) as a "branch-wide" list per position: each branch lane contributes
        /// one ref, so pre-branch levels list the SAME item ref once per lane. Those identical refs
        /// are real game data, not a dumper artifact — the very same Items list yields the genuinely
        /// distinct A/B/C variants at the final level. We collapse refs whose item is identical
        /// (same ConfigKey = same unique item ID = identical definition); distinct variants survive.
        /// (The element's Count getter is a decompiled stub that is always 0, so it's dropped.)
        /// </summary>
        private void SerializeListChainElement(JsonWriter writer, ListMergeChainElement element, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Items");
            writer.WriteStartArray();

            var seen = new HashSet<int>();
            foreach (var itemDef in element.Items ?? Enumerable.Empty<ItemDef>())
            {
                if (itemDef == null) continue;
                if (!seen.Add(itemDef.ConfigKey)) continue; // drop duplicate identical items
                serializer.Serialize(writer, itemDef);       // routes back through SerializeItem(ItemDef)
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        /// <summary>Tracks current ActivationFeatures for MaxCharges injection before StorageMax.</summary>
        private ActivationFeatures _currentAF;

        private void SerializeMergeChain(JsonWriter writer, MergeChainDefinition chainDef, JsonSerializer serializer)
        {
            if (chainDef.ConfigKey.Value == null)
            {
                WriteEmptyObject(writer);
                return;
            }

            WriteObject(writer, chainDef.GetType(), chainDef, serializer);
        }

        private void SerializeMergeChain(JsonWriter writer, MergeChainDef chainDef, JsonSerializer serializer)
        {
            if (chainDef.ConfigKey.Value == null)
            {
                WriteEmptyObject(writer);
                return;
            }

            WriteObject(writer, typeof(MergeChainDefinition), _config.MergeChains.GetValueOrDefault(chainDef.ConfigKey), serializer);
        }

        private void SerializeCodexCategory(JsonWriter writer, CodexCategoryInfo codex, JsonSerializer serializer)
        {
            if (codex.ConfigKey.Value == null)
            {
                WriteEmptyObject(writer);
                return;
            }

            WriteObject(writer, codex.GetType(), codex, serializer);
        }

        private void SerializeItem(JsonWriter writer, ItemDefinition item, JsonSerializer serializer)
        {
            if (item.ConfigKey == 0)
            {
                WriteEmptyObject(writer);
                return;
            }

            WriteObject(writer, item.GetType(), item, serializer);
        }

        private void SerializeItem(JsonWriter writer, ItemDef item, JsonSerializer serializer)
        {
            if (item.ConfigKey == 0)
            {
                WriteEmptyObject(writer);
                return;
            }

            WriteObject(writer, typeof(ItemDefinition), _config.Items.GetValueOrDefault(item.ConfigKey), serializer);
        }

        private void SerializeMergeCollection(JsonWriter writer, MergeCollection mergeCollection, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            foreach (var mergePair in mergeCollection.Collection)
            {
                var firstItem = _config.Items.GetValueOrDefault(mergePair.Key.First);
                var secondItem = _config.Items.GetValueOrDefault(mergePair.Key.Second);

                var pairValue = firstItem.ItemType + ":" + secondItem.ItemType;
                WriteProperty(writer, pairValue, mergePair.Value, serializer);
            }

            writer.WriteEndObject();
        }

        private void SerializeProducer(JsonWriter writer, IItemProducer producer, JsonSerializer serializer)
        {
            if (producer is EmptyProducer)
                WriteValue(writer, "Empty", serializer);
            else if (producer is GarageCleanupEventProducer)
                WriteValue(writer, "GarageCleanupEvent", serializer);
            else if (producer is InstantDecayProducer)
                WriteValue(writer, "InstantDecay", serializer);
            else if (producer is ConstantProducer cp)
            {
                // ConstantProducer holds parallel Products + Quantities lists.
                // Old serializer emitted only Products[0] as scalar — losing every additional
                // product (e.g. Reward Boxes whose 1st Prize bundles 7 items) and every
                // Quantity (e.g. XP=2 on AdditionalSpawnProducer, 12× Hourglass loot).
                // Always emit array of {Item, Quantity} pairs so the full payload survives.
                writer.WriteStartObject();
                writer.WritePropertyName("Constant");
                writer.WriteStartArray();
                int n = cp.Products?.Count ?? 0;
                for (int i = 0; i < n; i++)
                {
                    var itemType = cp.Products[i].GetDef(_config)?.ItemType;
                    if (string.IsNullOrEmpty(itemType)) continue;
                    int qty = (cp.Quantities != null && i < cp.Quantities.Count) ? cp.Quantities[i] : 1;
                    writer.WriteStartObject();
                    WriteProperty(writer, "Item", itemType, serializer);
                    WriteProperty(writer, "Quantity", qty, serializer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else if (producer is PrefixProducer pp)
            {
                writer.WriteStartObject();

                WriteProperty(writer, "Marker", pp.Marker, serializer);

                if (pp.BaseProducer != null || serializer.NullValueHandling != NullValueHandling.Ignore)
                {
                    writer.WritePropertyName("BaseProducer");
                    SerializeProducer(writer, pp.BaseProducer, serializer);
                }

                writer.WriteEndObject();
            }
            else if (producer is ControlledRandomProducer crp)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("ControlledRandom");
                writer.WriteStartObject();

                WriteProperty(writer, "RollType", crp.RollType.ToString(), serializer);
                WriteProperty(writer, "ItemType", _config.Items.GetValueOrDefault(crp.ItemType).ItemType, serializer);

                writer.WritePropertyName("Odds");
                writer.WriteStartObject();

                double weightSum = crp.GenerationOdds.Sum(x => x.Weight);
                foreach (var odd in crp.GenerationOdds.GroupBy(x => x.Type.GetDef(_config).ItemType))
                {
                    var weight = _dropAsPercent ?
                        odd.Sum(x => x.Weight) * 100 / weightSum :
                        odd.Sum(x => x.Weight);

                    WriteProperty(writer, odd.Key, weight, serializer);
                }

                writer.WriteEndObject();

                if (_dropAsPercent)
                {
                    writer.WritePropertyName("OddsWeights");
                    writer.WriteStartObject();
                    foreach (var odd in crp.GenerationOdds.GroupBy(x => x.Type.GetDef(_config).ItemType))
                        WriteProperty(writer, odd.Key, (double)odd.Sum(x => x.Weight), serializer);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else if (producer is RandomProducer rp)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("Random");
                writer.WriteStartObject();

                writer.WritePropertyName("Odds");
                writer.WriteStartObject();

                double weightSum = rp.OddsList.Sum(x => x.Weight);
                foreach (var odd in rp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                {
                    var weight = _dropAsPercent ?
                        odd.Sum(x => x.Weight) * 100 / weightSum :
                        odd.Sum(x => x.Weight);

                    WriteProperty(writer, odd.Key, weight, serializer);
                }

                writer.WriteEndObject();

                if (_dropAsPercent)
                {
                    writer.WritePropertyName("OddsWeights");
                    writer.WriteStartObject();
                    foreach (var odd in rp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                        WriteProperty(writer, odd.Key, (double)odd.Sum(x => x.Weight), serializer);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else if (producer is PredefinedSequenceProducer psp)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("PredefinedSequence");
                writer.WriteStartObject();

                writer.WritePropertyName("Odds");
                writer.WriteStartObject();

                double weightSum = psp.OddsList.Sum(x => x.Weight);
                foreach (var odd in psp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                {
                    var weight = _dropAsPercent ?
                        odd.Sum(x => x.Weight) * 100 / weightSum :
                        odd.Sum(x => x.Weight);

                    WriteProperty(writer, odd.Key, weight, serializer);
                }

                writer.WriteEndObject();

                if (_dropAsPercent)
                {
                    writer.WritePropertyName("OddsWeights");
                    writer.WriteStartObject();
                    foreach (var odd in psp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                        WriteProperty(writer, odd.Key, (double)odd.Sum(x => x.Weight), serializer);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else if (producer is ControlledRandomSequenceProducer crsp)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("ControlledRandomSequence");
                writer.WriteStartObject();

                writer.WritePropertyName("Odds");
                writer.WriteStartObject();

                double weightSum = crsp.TotalWeight;
                foreach (var odd in crsp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                {
                    var weight = _dropAsPercent ?
                        odd.Sum(x => x.Weight) * 100 / weightSum :
                        odd.Sum(x => x.Weight);

                    WriteProperty(writer, odd.Key, weight, serializer);
                }

                writer.WriteEndObject();

                if (_dropAsPercent)
                {
                    writer.WritePropertyName("OddsWeights");
                    writer.WriteStartObject();
                    foreach (var odd in crsp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                        WriteProperty(writer, odd.Key, (double)odd.Sum(x => x.Weight), serializer);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else if (producer is ControlledMixedSequenceProducer cmsp)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("ControlledMixedSequence");
                writer.WriteStartObject();

                WriteProperty(writer, "RollType", cmsp.RollType.ToString(), serializer);
                WriteProperty(writer, "ItemType", _config.Items.GetValueOrDefault(cmsp.ItemType).ItemType, serializer);

                writer.WritePropertyName("Odds");
                writer.WriteStartObject();

                double weightSum = cmsp.OddsList.Sum(x => x.Weight);
                foreach (var odd in cmsp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                {
                    var weight = _dropAsPercent ?
                        odd.Sum(x => x.Weight) * 100 / weightSum :
                        odd.Sum(x => x.Weight);

                    WriteProperty(writer, odd.Key, weight, serializer);
                }

                writer.WriteEndObject();

                if (_dropAsPercent)
                {
                    writer.WritePropertyName("OddsWeights");
                    writer.WriteStartObject();
                    foreach (var odd in cmsp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                        WriteProperty(writer, odd.Key, (double)odd.Sum(x => x.Weight), serializer);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else if (producer is ControlledPredefinedSequenceProducer cpsp)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("ControlledPredefinedSequence");
                writer.WriteStartObject();

                WriteProperty(writer, "RollType", cpsp.RollType.ToString(), serializer);
                WriteProperty(writer, "ItemType", _config.Items.GetValueOrDefault(cpsp.ItemType).ItemType, serializer);

                writer.WritePropertyName("Odds");
                writer.WriteStartObject();

                double weightSum = cpsp.OddsList.Sum(x => x.Weight);
                foreach (var odd in cpsp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                {
                    var weight = _dropAsPercent ?
                        odd.Sum(x => x.Weight) * 100 / weightSum :
                        odd.Sum(x => x.Weight);

                    WriteProperty(writer, odd.Key, weight, serializer);
                }

                writer.WriteEndObject();

                if (_dropAsPercent)
                {
                    writer.WritePropertyName("OddsWeights");
                    writer.WriteStartObject();
                    foreach (var odd in cpsp.OddsList.GroupBy(x => x.Type.GetDef(_config).ItemType))
                        WriteProperty(writer, odd.Key, (double)odd.Sum(x => x.Weight), serializer);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull();
                _output.Warning("Unknown producer {0}", producer.GetType().Name);
            }
        }

        private void SerializeOrderProducer(JsonWriter writer, IOrderProducer producer, JsonSerializer serializer)
        {
            if (producer is ConstantOrderProducer cop)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("Constant");
                writer.WriteStartObject();

                WriteProperty(writer, "RollType", cop.RollType.ToString(), serializer);
                WriteProperty(writer, "ItemType", _config.Items.GetValueOrDefault(cop.ItemType)?.ItemType ?? $"{cop.ItemType:X8}", serializer);

                writer.WritePropertyName("Tasks");
                writer.WriteStartArray();

                double weightSum = cop.GenerationOdds.Sum(x => x.Item2);
                foreach ((OrderRequirementsId, int) odds in cop.GenerationOdds)
                {
                    if (!_config.OrderRequirements.TryGetValue(odds.Item1, out var orderRequirements))
                        continue;

                    writer.WriteStartObject();

                    var weight = _dropAsPercent ?
                        odds.Item2 * 100 / weightSum :
                        odds.Item2;
                    WriteProperty(writer, "Odds", weight, serializer);
                    if (_dropAsPercent)
                        WriteProperty(writer, "OddsWeight", (double)odds.Item2, serializer);

                    writer.WritePropertyName("Required");
                    writer.WriteStartArray();

                    foreach (var item in orderRequirements.SinkItemsAndAmounts)
                    {
                        writer.WriteStartObject();

                        if (!_config.Items.TryGetValue(item.Key, out var itemDefinition))
                            continue;

                        WriteProperty(writer, "Item", itemDefinition.ItemType, serializer);
                        WriteProperty(writer, "Amount", item.Value, serializer);

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();

                    writer.WritePropertyName("Rewards");
                    writer.WriteStartArray();

                    foreach (var item in orderRequirements.ActivationRewardsAndAmounts)
                    {
                        writer.WriteStartObject();

                        if (!_config.Items.TryGetValue(item.Key, out var itemDefinition))
                            continue;

                        WriteProperty(writer, "Item", itemDefinition.ItemType, serializer);
                        WriteProperty(writer, "Amount", item.Value, serializer);

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else if (producer is ControlledRandomOrderProducer crod)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("ControlledRandom");
                writer.WriteStartObject();

                WriteProperty(writer, "RollType", crod.RollType.ToString(), serializer);
                WriteProperty(writer, "ItemType", _config.Items.GetValueOrDefault(crod.ItemType)?.ItemType ?? $"{crod.ItemType:X8}", serializer);

                writer.WritePropertyName("Tasks");
                writer.WriteStartArray();

                double weightSum = crod.GenerationOdds.Sum(x => x.Item2);
                foreach ((OrderRequirementsId, int) odds in crod.GenerationOdds)
                {
                    if (!_config.OrderRequirements.TryGetValue(odds.Item1, out var orderRequirements))
                        continue;

                    writer.WriteStartObject();

                    var weight = _dropAsPercent ?
                        odds.Item2 * 100 / weightSum :
                        odds.Item2;
                    WriteProperty(writer, "Odds", weight, serializer);
                    if (_dropAsPercent)
                        WriteProperty(writer, "OddsWeight", (double)odds.Item2, serializer);

                    writer.WritePropertyName("Required");
                    writer.WriteStartArray();

                    foreach (var item in orderRequirements.SinkItemsAndAmounts)
                    {
                        writer.WriteStartObject();

                        if (!_config.Items.TryGetValue(item.Key, out var itemDefinition))
                            continue;

                        WriteProperty(writer, "Item", itemDefinition.ItemType, serializer);
                        WriteProperty(writer, "Amount", item.Value, serializer);

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();

                    writer.WritePropertyName("Rewards");
                    writer.WriteStartArray();

                    foreach (var item in orderRequirements.ActivationRewardsAndAmounts)
                    {
                        writer.WriteStartObject();

                        if (!_config.Items.TryGetValue(item.Key, out var itemDefinition))
                            continue;

                        WriteProperty(writer, "Item", itemDefinition.ItemType, serializer);
                        WriteProperty(writer, "Amount", item.Value, serializer);

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else if (producer is ControlledPredefinedSequenceOrderProducer cpsop)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("ControlledPredefinedSequence");
                writer.WriteStartObject();

                WriteProperty(writer, "RollType", cpsop.RollType.ToString(), serializer);
                WriteProperty(writer, "ItemType", _config.Items.GetValueOrDefault(cpsop.ItemType)?.ItemType ?? $"{cpsop.ItemType:X8}", serializer);

                writer.WritePropertyName("Tasks");
                writer.WriteStartArray();

                double weightSum = cpsop.GenerationOdds.Sum(x => x.Item2);
                foreach ((OrderRequirementsId, int) odds in cpsop.GenerationOdds)
                {
                    if (!_config.OrderRequirements.TryGetValue(odds.Item1, out var orderRequirements))
                        continue;

                    writer.WriteStartObject();

                    var weight = _dropAsPercent ?
                        odds.Item2 * 100 / weightSum :
                        odds.Item2;
                    WriteProperty(writer, "Odds", weight, serializer);
                    if (_dropAsPercent)
                        WriteProperty(writer, "OddsWeight", (double)odds.Item2, serializer);

                    writer.WritePropertyName("Required");
                    writer.WriteStartArray();

                    foreach (var item in orderRequirements.SinkItemsAndAmounts)
                    {
                        writer.WriteStartObject();

                        if (!_config.Items.TryGetValue(item.Key, out var itemDefinition))
                            continue;

                        WriteProperty(writer, "Item", itemDefinition.ItemType, serializer);
                        WriteProperty(writer, "Amount", item.Value, serializer);

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();

                    writer.WritePropertyName("Rewards");
                    writer.WriteStartArray();

                    foreach (var item in orderRequirements.ActivationRewardsAndAmounts)
                    {
                        writer.WriteStartObject();

                        if (!_config.Items.TryGetValue(item.Key, out var itemDefinition))
                            continue;

                        WriteProperty(writer, "Item", itemDefinition.ItemType, serializer);
                        WriteProperty(writer, "Amount", item.Value, serializer);

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull();
                _output.Warning("Unknown order producer {0}", producer.GetType().Name);
            }
        }

        protected override void WriteCustomObjectMembers(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is MergeChainDefinition mergeChain)
            {
                var chainName = ResolveChainName(mergeChain);
                if (chainName != null)
                    WriteProperty(writer, "Name", chainName, serializer);
            }
            else if (value is ItemDefinition item)
            {
                var overrideKey = item.OverrideLocalizationItemKey;
                var fullOverrideKey = item.FullOverrideLocalizationItemKey;

                if (!string.IsNullOrEmpty(overrideKey))
                    WriteProperty(writer, "OverrideLocKey", overrideKey, serializer);
                if (!string.IsNullOrEmpty(fullOverrideKey))
                    WriteProperty(writer, "FullOverrideLocKey", fullOverrideKey, serializer);

                var itemName = LocMan.GetItemName(item.ItemType, overrideKey, fullOverrideKey);
                if (itemName != null)
                    WriteProperty(writer, "Name", itemName, serializer);

                // ItemType early — between Name and Description for readability
                WriteProperty(writer, "ItemType", item.ItemType, serializer);

                var descName = LocMan.GetDescription(item.ItemType, item.LevelNumber, overrideKey, fullOverrideKey);
                if (descName != null)
                    WriteProperty(writer, "Description", descName, serializer);

                var sellPrice = item.GetItemSellPrice(_config.SharedGlobals);
                WriteProperty(writer, "SellCoins", sellPrice, serializer);

                if (item.BubbleFeatures != null)
                {
                    var dailyTaskChain = (DailyTasksV2MergeChainInfo)_config.DailyTasksV2MergeChains.GetInfoByKey(item.MergeChainDef.ConfigKey);

                    F32 requiredMultiplier = dailyTaskChain?.RequirementMultiplier ?? new F32(0x10000);
                    int requiredItemValue = F32.RoundToInt(requiredMultiplier * item.BubbleFeatures.OpenCost.Item2);

                    F32 rewardedMultiplier = dailyTaskChain?.RewardMultiplier ?? new F32(0x10000);
                    int rewardedItemValue = F32.RoundToInt(rewardedMultiplier * item.BubbleFeatures.OpenCost.Item2);

                    WriteProperty(writer, "RequiredItemValue", requiredItemValue, serializer);
                    WriteProperty(writer, "RewardItemValue", rewardedItemValue, serializer);
                }

                var extraSpawnValues = ExtraSpawnHelper.GetItemValues(_config, item.ConfigKey);
                if (extraSpawnValues != null)
                    foreach (var kv in extraSpawnValues)
                        WriteProperty(writer, kv.Key, kv.Value, serializer);

                var (speedUpCost, weightedAvgTSP) = ComputeSpeedUpCostGems(item);
                if (weightedAvgTSP.HasValue)
                    WriteProperty(writer, "WeightedAvgTSP", weightedAvgTSP.Value, serializer);
                if (speedUpCost.HasValue)
                    WriteProperty(writer, "SpeedUpCostGems", speedUpCost.Value, serializer);
            }
            else if (value is ActivationFeatures afCustom)
            {
                // Non-MetaMember computed properties (would be lost without WriteObject override)
                WriteProperty(writer, "Activable", afCustom.Activable, serializer);
            }
        }

        protected override void WriteObjectMember(JsonWriter writer, string name, Type type, object value, JsonSerializer serializer)
        {
            if (type.IsAssignableTo(typeof(CodexCategoryInfo)))
            {
                if (name == nameof(CodexCategoryInfo.IconItem))
                {
                    WriteProperty(writer, name, (value as ItemDef)?.GetDef(_config).ItemType ?? string.Empty, serializer);
                    return;
                }
            }
            else if (type.IsAssignableTo(typeof(ItemDefinition)))
            {
                // Skip — already written in WritePreObjectMembers
                if (name == nameof(ItemDefinition.ItemType))
                    return;
                if (name == nameof(ItemDefinition.MergeChainDef))
                {
                    WriteProperty(writer, name, (value as MergeChainDef)?.ConfigKey, serializer);
                    return;
                }
                if (name == nameof(ItemDefinition.TimeSkipPriceGems))
                {
                    // Write exact F64.Double value (MetaMathJsonSerializer uses Math.Ceiling which rounds up)
                    WriteProperty(writer, name, ((F64)value).Double, serializer);
                    return;
                }
            }
            else if (type.IsAssignableTo(typeof(PersistentFeatures)))
            {
                if (name == nameof(PersistentFeatures.ResetToItem))
                {
                    WriteProperty(writer, name, (value as ItemDef)?.GetDef(_config).ItemType ?? string.Empty, serializer);
                    return;
                }
            }
            else if (type.IsAssignableTo(typeof(MultiTargetSinkStateFactory)))
            {
                if (name == nameof(MultiTargetSinkStateFactory.RewardDef))
                {
                    WriteProperty(writer, name, (value as ItemDef)?.GetDef(_config).ItemType ?? string.Empty, serializer);
                    return;
                }
            }
            else if (type.IsAssignableTo(typeof(ActivationFeatures)))
            {
                // Inject MaxCharges immediately before StorageMax + override StorageMax.
                //
                // Empirical rule (verified on 1538 generators in v26.04.01) — the meaning
                // of `StorageMax` depends on `ActivationFeatures.StartsFull`:
                //
                //   StartsFull == true   (item starts pre-filled):
                //     • raw StorageMax IS the per-cycle drops capacity (= reality)
                //     • MaxCharges = StorageMax / dropsPerCharge   (taps per cycle)
                //     • StorageMax in JSON = raw (kept)
                //     Examples: Mane Comb (storage=4 dpc=1 → 4 drops/cycle, hmc=1 → 4 total),
                //               Water Bucket (storage=30 dpc=30 → 1 charge/cycle, hmc=2 → 60 total)
                //
                //   StartsFull == false  (item starts empty, must wait for cycle):
                //     • raw StorageMax is a padded UI hint or per-cycle queue capacity
                //     • MaxCharges = 1 (= one batch tap per cycle)
                //     • StorageMax in JSON = HowManyCycles × dpc  (= total lifetime drops)
                //     Examples: Plain Box (raw=15 → 5), White Moth (raw=20 → 60), Suitcase, Radios
                //
                //   HowManyCycles == -1  (infinite refill, no override):
                //     • MaxCharges = StorageMax / dpc, StorageMax kept
                //     Examples: Flower Pot chain, Drawer chain, Toolbox
                //
                // Lua semantic: total drops = dpc × charges × cycles where charges=MaxCharges,
                // cycles=HowManyCycles. The split into charges/cycles depends on StartsFull
                // (StartsFull=True spreads taps within one cycle; False spreads across cycles).
                if (name == nameof(ActivationFeatures.StorageMax) && _currentAF != null)
                {
                    int storageMax = (int)value;
                    var ac = _currentAF.ActivationCycle as ActivationCycle;
                    var cd = ac?.DailyActivationCyclesData as ActivationCycleData;
                    if (cd != null && storageMax > 0)
                    {
                        int amt = cd.ActivationAmountInCycle?.FirstOrDefault() ?? 0;
                        int hmg = cd.HowManyAreGeneratedInCycle?.FirstOrDefault() ?? 0;
                        // Sentinel guard: 9999/9999 sentinel = stateful event/minigame marker,
                        // NOT a real droppable producer. Leave MaxCharges/StorageMax at raw values
                        // (don't inject computed MaxCharges) so consumers can ignore via raw check.
                        bool isSentinel = amt >= 9999 || hmg >= 9999;
                        int dpc = amt * hmg;
                        if (dpc > 0 && !isSentinel)
                        {
                            int howManyCycles = ac.HowManyCycles;
                            bool startsFull = _currentAF.StartsFull;
                            int storageCharges = storageMax / dpc;
                            // perCycleTaps = how many tap actions fit within one cycle.
                            // From Lua's perspective this becomes the `charges` field;
                            // it's multiplied by `cycles` (= HowManyCycles) for total.
                            int perCycleTaps;
                            int storageOut;
                            if (howManyCycles > 0)
                            {
                                if (startsFull)
                                {
                                    perCycleTaps = System.Math.Max(1, storageCharges);
                                    storageOut = storageMax; // raw is reality
                                }
                                else
                                {
                                    perCycleTaps = 1; // 1 batch-tap per cycle
                                    storageOut = howManyCycles * dpc; // override = total
                                }
                            }
                            else
                            {
                                // infinite refill — when StorageMax < dpc (mini-charge mechanic,
                                // e.g. Secret Code Book storage=32 / dpc=96 = 0), wrap to 1.
                                // Mini-charges are internal batching, NOT individual main charges.
                                // See memory/game-mechanic-rules.md.
                                perCycleTaps = System.Math.Max(1, storageCharges);
                                // For mini-charge items, raw StorageMax represents per-mini-batch
                                // queue size (e.g. SCB raw=32 = drops in one mini-batch). Player
                                // semantic = total drops per main charge → override to dpc.
                                // For regular infinite items (storageCharges >= 1), raw IS total.
                                storageOut = storageCharges < 1 ? dpc : storageMax;
                            }
                            // PLAYER MaxCharges = total tap actions in lifetime.
                            // From player perspective each cycle is "another charge", so
                            // White Moth (1 tap × 3 cycles) shows MaxCharges=3 (not 1).
                            // For infinite items HowManyCycles=-1 → just per-cycle taps.
                            int playerMaxCharges = (howManyCycles > 0)
                                ? perCycleTaps * howManyCycles
                                : perCycleTaps;
                            WriteProperty(writer, "MaxCharges", playerMaxCharges, serializer);
                            WriteProperty(writer, name, storageOut, serializer);
                            return;
                        }
                    }
                }
            }

            base.WriteObjectMember(writer, name, type, value, serializer);
        }

        private (int? cost, double? avgTSP) ComputeSpeedUpCostGems(ItemDefinition item)
        {
            // Primary generators (ActivationFeatures — tap to activate)
            var af = item.ActivationFeatures;
            if (af != null)
            {
                // Must cast to concrete ActivationCycleData — the IActivationCycleData
                // interface properties (Get*) are [IgnoreDataMember] and never populated.
                var cycleData = af.ActivationCycle?.DailyActivationCyclesData as ActivationCycleData;
                if (cycleData != null)
                {
                    var amounts = cycleData.ActivationAmountInCycle;
                    var howMany = cycleData.HowManyAreGeneratedInCycle;
                    if (amounts?.Count > 0 && howMany?.Count > 0 && amounts[0] > 0 && howMany[0] > 0
                        && af.ActivationSpawn != null)
                    {
                        double avgTSP = ComputeWeightedAvgTSP(af.ActivationSpawn);
                        if (avgTSP > 0)
                        {
                            // Game formula: display = ceil(remaining × raw / total).
                            // At fresh timer reset (remaining == total): display = ceil(raw).
                            // So "peak display" = Math.Ceiling(raw). Round(raw) underreports
                            // when fractional part < 0.5 (e.g. Carved Wooden Box: raw 9.186,
                            // Round=9 but game shows 10 at fresh reset).
                            int cost = (int)Math.Ceiling(amounts[0] * howMany[0] * avgTSP);
                            return (cost, avgTSP);
                        }
                    }
                }
            }

            // Secondary generators (SpawnFeatures — automatic spawning, e.g. Vase)
            var sf = item.SpawnFeatures as SpawnFeatures;
            if (sf?.Spawn != null && sf.SpawnCycle is SpawnCycle sc
                && sc.SpawnAmountInCycle > 0 && sc.HowManyAreGeneratedPerSpawn > 0)
            {
                double avgTSP = ComputeWeightedAvgTSP(sf.Spawn);
                if (avgTSP > 0)
                {
                    // See ActivationFeatures branch for rounding rationale (Ceiling = peak display).
                    int cost = (int)Math.Ceiling(sc.SpawnAmountInCycle * sc.HowManyAreGeneratedPerSpawn * avgTSP);
                    return (cost, avgTSP);
                }
            }

            return (null, null);
        }

        private double ComputeWeightedAvgTSP(IItemSpawner spawner)
        {
            if (spawner is PrefixProducer pp && pp.BaseProducer != null)
                return ComputeWeightedAvgTSP(pp.BaseProducer);

            if (spawner is ConstantProducer cp && cp.Products?.Count > 0)
                return cp.Products.Average(p => p.GetDef(_config).TimeSkipPriceGems.Double);

            if (spawner is RandomProducer rp && rp.OddsList?.Count > 0)
            {
                double weightSum = rp.OddsList.Sum(o => (double)o.Weight);
                if (weightSum <= 0) return 0;
                return rp.OddsList.Sum(o => o.Weight / weightSum * o.Type.GetDef(_config).TimeSkipPriceGems.Double);
            }

            if (spawner is ControlledRandomProducer crp && crp.GenerationOdds?.Count > 0)
            {
                double weightSum = crp.GenerationOdds.Sum(o => (double)o.Weight);
                if (weightSum <= 0) return 0;
                return crp.GenerationOdds.Sum(o => o.Weight / weightSum * o.Type.GetDef(_config).TimeSkipPriceGems.Double);
            }

            if (spawner is ControlledRandomSequenceProducer crsp && crsp.OddsList?.Count > 0)
            {
                double weightSum = crsp.OddsList.Sum(o => (double)o.Weight);
                if (weightSum <= 0) return 0;
                return crsp.OddsList.Sum(o => o.Weight / weightSum * o.Type.GetDef(_config).TimeSkipPriceGems.Double);
            }

            // Fallback for other spawner types
            try { return spawner.TimeSkipPriceGems(null).Double; }
            catch { return 0; }
        }

        /// <summary>
        /// Serializes ActivationCycle with renamed and flattened fields.
        /// DailyActivationCyclesData arrays are unwrapped to scalars (all are single-element).
        /// </summary>
        private void SerializeActivationCycle(JsonWriter writer, ActivationCycle ac, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            WriteProperty(writer, "MiniChargeCooldown", ac.ActivationDelay.Milliseconds, serializer);
            WriteProperty(writer, "InitialCooldown", ac.FirstCycleStartDelay.Milliseconds, serializer);
            WriteProperty(writer, "HowManyCycles", ac.HowManyCycles, serializer);

            var cycleData = ac.DailyActivationCyclesData as ActivationCycleData;
            if (cycleData != null)
            {
                if (cycleData.DelaysBetweenCycles?.Count > 0)
                    WriteProperty(writer, "ChargeCooldown", cycleData.DelaysBetweenCycles[0].Milliseconds, serializer);
                if (cycleData.ActivationAmountInCycle?.Count > 0)
                    WriteProperty(writer, "MiniChargesInSingleCharge", cycleData.ActivationAmountInCycle[0], serializer);
                if (cycleData.HowManyAreGeneratedInCycle?.Count > 0)
                    WriteProperty(writer, "DropsInSingleMiniCharge", cycleData.HowManyAreGeneratedInCycle[0], serializer);
                if (cycleData.TimerSkipMultiplier?.Count > 0)
                    WriteProperty(writer, "TimerSkipMultiplier", cycleData.TimerSkipMultiplier[0].Double, serializer);
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Resolves a human-readable chain name using multiple fallback strategies:
        /// 1. Localization by chain ConfigKey (ItemCategory_{ConfigKey})
        /// 2. Localization by PoolTag of first item (ItemCategory_{PoolTag})
        /// 3. OverrideLocalizationItemCategory on first item (direct loc key or with ItemCategory_ prefix)
        /// </summary>
        private string ResolveChainName(MergeChainDefinition mergeChain)
        {
            // Skip name resolution for test/artifact chains
            var firstItem = GetFirstItemDefinition(mergeChain);
            if (firstItem?.Tags != null && (firstItem.Tags.Contains("Test") || firstItem.Tags.Contains("Artifact")))
                return null;

            // 1. Try chain ConfigKey directly
            if (LocMan.HasString(LocMan.GetItemCategoryNameLocId(mergeChain.ConfigKey)))
                return LocMan.GetItemCategoryName(mergeChain.ConfigKey);

            // Get first item for PoolTag / Override fallbacks
            var itemDef = firstItem;
            if (itemDef == null)
                return null;

            // 2. Try PoolTag of first item
            if (!string.IsNullOrEmpty(itemDef.PoolTag))
            {
                var poolLocId = LocMan.GetItemCategoryNameLocId(itemDef.PoolTag);
                if (LocMan.HasString(poolLocId))
                    return LocMan.GetItemCategoryName(itemDef.PoolTag);
            }

            // 3. Try OverrideLocalizationItemCategory
            var overrideCat = itemDef.OverrideLocalizationItemCategory;
            if (!string.IsNullOrEmpty(overrideCat))
            {
                // Value may be a full loc key (e.g. "ItemCategory_X") or just an identifier
                if (LocMan.HasString(overrideCat))
                    return LocMan.Get(overrideCat);

                var withPrefix = LocMan.GetItemCategoryNameLocId(overrideCat);
                if (LocMan.HasString(withPrefix))
                    return LocMan.Get(withPrefix);
            }

            return null;
        }

        private ItemDefinition GetFirstItemDefinition(MergeChainDefinition mergeChain)
        {
            if (mergeChain.PrimaryChain == null || mergeChain.PrimaryChain.Count == 0)
                return null;

            var firstElement = mergeChain.PrimaryChain[0];
            int firstKey = 0;
            if (firstElement is SingleMergeChainElement single)
                firstKey = single.Item.ConfigKey;
            else if (firstElement is ListMergeChainElement list && list.Items?.Count > 0)
                firstKey = list.Items[0].ConfigKey;

            return firstKey != 0 ? _config.Items.GetValueOrDefault(firstKey) : null;
        }
    }
}
