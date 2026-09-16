using GameLogic.Config;
using GameLogic.Player.Items.Order;
using GameLogic.Player.Items.Production;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// The producer half of <see cref="ChainSerializer"/>. Every <c>IItemProducer</c> /
/// <c>IOrderProducer</c> is written as a one-key wrapper <c>{ "&lt;Kind&gt;": &lt;payload&gt; }</c>,
/// where the kind is the class name with its <c>Producer</c> (or <c>OrderProducer</c>) suffix cut
/// off — so <c>ControlledRandomProducer</c> and <c>ControlledRandomOrderProducer</c> both appear as
/// <c>ControlledRandom</c>, differing only in their payload. <c>PrefixProducer</c> is the single
/// exception: it has no wrapper and writes its three members inline.
/// </summary>
public sealed partial class ChainSerializer
{
    private void WriteProducer(JsonWriter w, object producer, JsonSerializer s)
    {
        // The prefix producer is written inline (no wrapper key): its guaranteed opening item
        // sequence plus the base producer that takes over afterwards.
        if (producer is PrefixProducer prefix)
        {
            w.WriteStartObject();
            w.WritePropertyName("Marker");
            w.WriteValue(prefix.Marker);
            w.WritePropertyName("Prefix");
            WriteRunLengthArray(w, prefix.Items);
            WriteIfNotNull(w, s, "BaseProducer", prefix.BaseProducer);
            w.WriteEndObject();
            return;
        }

        // Producers with no serialized state of their own (EmptyProducer, InstantDecayProducer,
        // GarageCleanupEventProducer): the whole value collapses to the bare kind string —
        // "DecayAfterLastCycleProducer": "Empty" — instead of a wrapper around nothing.
        if (!HasPayload(producer))
        {
            w.WriteValue(ProducerKind(producer.GetType()));
            return;
        }

        w.WriteStartObject();
        w.WritePropertyName(ProducerKind(producer.GetType()));
        switch (producer)
        {
            case ConstantProducer constant:
                WriteItemQuantityArray(w, constant.Products, constant.Quantities);
                break;

            case RandomProducer random:
                WriteOddsObject(w, random.OddsList, null, null);
                break;
            case PredefinedSequenceProducer predefined:
                WriteOddsObject(w, predefined.OddsList, null, null);
                break;
            case ControlledRandomSequenceProducer randomSequence:
                WriteOddsObject(w, randomSequence.OddsList, null, null);
                break;

            case ControlledRandomProducer controlledRandom:
                WriteOddsObject(w, controlledRandom.GenerationOdds, controlledRandom.RollType, controlledRandom.ItemType);
                break;
            case ControlledMixedSequenceProducer mixedSequence:
                WriteOddsObject(w, mixedSequence.OddsList, mixedSequence.RollType, mixedSequence.ItemType);
                break;
            case ControlledPredefinedSequenceProducer predefinedSequence:
                WriteOddsObject(w, predefinedSequence.OddsList, predefinedSequence.RollType, predefinedSequence.ItemType);
                break;

            case ConstantOrderProducer constantOrder:
                WriteTasksObject(w, constantOrder.GenerationOdds, constantOrder.RollType, constantOrder.ItemType);
                break;
            case ControlledRandomOrderProducer randomOrder:
                WriteTasksObject(w, randomOrder.GenerationOdds, randomOrder.RollType, randomOrder.ItemType);
                break;
            case ControlledPredefinedSequenceOrderProducer predefinedOrder:
                WriteTasksObject(w, predefinedOrder.GenerationOdds, predefinedOrder.RollType, predefinedOrder.ItemType);
                break;

            default:
                // Unreachable as long as HasPayload and this switch list the same kinds; kept so a
                // kind added to one and forgotten in the other still produces valid JSON.
                w.WriteStartObject();
                w.WriteEndObject();
                break;
        }
        w.WriteEndObject();
    }

    /// <summary>Whether this producer kind has a payload worth wrapping (see <see cref="WriteProducer"/>).</summary>
    private static bool HasPayload(object producer) => producer is
        ConstantProducer or RandomProducer or PredefinedSequenceProducer or ControlledRandomSequenceProducer
        or ControlledRandomProducer or ControlledMixedSequenceProducer or ControlledPredefinedSequenceProducer
        or ConstantOrderProducer or ControlledRandomOrderProducer or ControlledPredefinedSequenceOrderProducer;

    private static string ProducerKind(Type type)
    {
        var name = type.Name;
        if (name.EndsWith("OrderProducer", StringComparison.Ordinal)) return name[..^"OrderProducer".Length];
        if (name.EndsWith("Producer", StringComparison.Ordinal)) return name[..^"Producer".Length];
        return name;
    }

    /// <summary>
    /// The <c>ConstantProducer</c> payload: a flat array of <c>{Item, Quantity}</c> pairs, one per
    /// product, zipped by index with the quantity list (missing quantity = 1). Products that do not
    /// resolve to a real item type are skipped. Always an array, even for a single product — see
    /// <c>_CONTEXT/Dumper/GameDataDumper.md</c> §6.
    /// </summary>
    private void WriteItemQuantityArray(JsonWriter w, List<ItemDef>? products, List<int>? quantities)
    {
        w.WriteStartArray();
        for (int i = 0; products != null && i < products.Count; i++)
        {
            var itemType = ItemTypeOf(products[i]);
            if (string.IsNullOrEmpty(itemType)) continue;
            w.WriteStartObject();
            w.WritePropertyName("Item");
            w.WriteValue(itemType);
            w.WritePropertyName("Quantity");
            w.WriteValue(quantities != null && i < quantities.Count ? quantities[i] : 1);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>
    /// The payload of every weighted producer: optional <c>RollType</c>/<c>ItemType</c> (only the
    /// "controlled" kinds that roll against a per-player history carry them), then the drop chances
    /// and the raw weights they came from, both keyed by item type.
    /// <para>
    /// An odds list may name the same item more than once (extra entries are how the config raises
    /// an item's share inside a fixed sequence). Since both maps are keyed by item type, those
    /// entries are merged: one key per item, holding the summed weight, and the chance computed
    /// from that sum.
    /// </para>
    /// </summary>
    private void WriteOddsObject(JsonWriter w, List<ItemOdds>? odds, RollHistoryType? rollType, int? itemType)
    {
        w.WriteStartObject();
        if (rollType != null)
        {
            w.WritePropertyName("RollType");
            w.WriteValue(rollType.Value.ToString());
        }
        if (itemType != null)
        {
            w.WritePropertyName("ItemType");
            w.WriteValue(ItemTypeOf(itemType.Value));
        }

        var merged = new List<(string Name, long Weight)>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        long total = 0;
        if (odds != null)
            foreach (var entry in odds)
            {
                if (entry == null) continue;
                var name = ItemTypeOf(entry.Type) ?? "";
                total += entry.Weight;
                if (indexByName.TryGetValue(name, out var at))
                    merged[at] = (name, merged[at].Weight + entry.Weight);
                else
                {
                    indexByName[name] = merged.Count;
                    merged.Add((name, entry.Weight));
                }
            }

        w.WritePropertyName("Odds");
        w.WriteStartObject();
        foreach (var (name, weight) in merged)
        {
            w.WritePropertyName(name);
            w.WriteValue(total == 0 ? 0d : Share(weight, total));
        }
        w.WriteEndObject();

        w.WritePropertyName("OddsWeights");
        w.WriteStartObject();
        foreach (var (name, weight) in merged)
        {
            w.WritePropertyName(name);
            w.WriteValue((double)weight);
        }
        w.WriteEndObject();

        w.WriteEndObject();
    }

    /// <summary>
    /// The <c>PrefixProducer</c> payload: the guaranteed opening drops, run-length encoded — a run
    /// of the same item in a row becomes one <c>{Item, Quantity}</c> entry (the Drawer generator's
    /// 37-step opening sequence collapses to 27 entries this way).
    /// </summary>
    private void WriteRunLengthArray(JsonWriter w, List<ItemDef>? items)
    {
        w.WriteStartArray();
        for (int i = 0; items != null && i < items.Count;)
        {
            var itemType = ItemTypeOf(items[i]);
            int run = 1;
            while (i + run < items.Count && ItemTypeOf(items[i + run]) == itemType) run++;
            if (!string.IsNullOrEmpty(itemType))
            {
                w.WriteStartObject();
                w.WritePropertyName("Item");
                w.WriteValue(itemType);
                w.WritePropertyName("Quantity");
                w.WriteValue(run);
                w.WriteEndObject();
            }
            i += run;
        }
        w.WriteEndArray();
    }

    /// <summary>
    /// The order-producer payload: the roll identity plus one entry per possible order, each with
    /// its chance, its raw weight, and the items it asks for and gives back (resolved through the
    /// <c>OrderRequirements</c> library).
    /// </summary>
    private void WriteTasksObject(JsonWriter w, List<(OrderRequirementsId Id, int Weight)>? tasks, RollHistoryType rollType, int itemType)
    {
        w.WriteStartObject();
        w.WritePropertyName("RollType");
        w.WriteValue(rollType.ToString());
        w.WritePropertyName("ItemType");
        w.WriteValue(ItemTypeOf(itemType));

        long total = 0;
        if (tasks != null)
            foreach (var task in tasks)
                total += task.Weight;

        w.WritePropertyName("Tasks");
        w.WriteStartArray();
        if (tasks != null)
            foreach (var (id, weight) in tasks)
            {
                OrderRequirements? requirements = null;
                if (id != null && _config.OrderRequirements != null)
                    _config.OrderRequirements.TryGetValue(id, out requirements);

                w.WriteStartObject();
                w.WritePropertyName("Odds");
                w.WriteValue(total == 0 ? 0d : Share(weight, total));
                w.WritePropertyName("OddsWeight");
                w.WriteValue((double)weight);
                w.WritePropertyName("Required");
                WriteItemAmountArray(w, requirements?.SinkItemsAndAmounts);
                w.WritePropertyName("Rewards");
                WriteItemAmountArray(w, requirements?.ActivationRewardsAndAmounts);
                w.WriteEndObject();
            }
        w.WriteEndArray();

        w.WriteEndObject();
    }

    private void WriteItemAmountArray(JsonWriter w, Dictionary<int, int>? itemsAndAmounts)
    {
        w.WriteStartArray();
        if (itemsAndAmounts != null)
            foreach (var (itemId, amount) in itemsAndAmounts)
            {
                w.WriteStartObject();
                w.WritePropertyName("Item");
                w.WriteValue(ItemTypeOf(itemId));
                w.WritePropertyName("Amount");
                w.WriteValue(amount);
                w.WriteEndObject();
            }
        w.WriteEndArray();
    }

    /// <summary>
    /// One entry's share of the total weight — as a percentage when the dumper was asked for
    /// percentages (what the app always does), otherwise as a plain fraction. Multiplying before
    /// dividing is deliberate: it is what reproduces the golden doubles bit for bit.
    /// </summary>
    private double Share(long weight, long total)
        => _dropsAsPercent ? weight * 100d / total : weight / (double)total;
}
