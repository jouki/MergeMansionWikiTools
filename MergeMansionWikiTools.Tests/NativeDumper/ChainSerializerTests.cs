using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using GameLogic.Config;
using GameLogic.Player.Items;
using GameLogic.Player.Items.Activation;
using GameLogic.Player.Items.Production;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;
using Metaplay.Core.Config;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

public class ChainSerializerTests
{
    /// <summary>Registers nothing: the library only needs somewhere to hand its resolvers.</summary>
    private sealed class NullRegistry : IGameConfigDataRegistry
    {
        public void RegisterReferenceResolver(Type type, Func<object, object> tryResolveFunc) { }
    }

    private static ItemDefinition Item(int key, string itemType) => new() { ConfigKey = key, ItemType = itemType };

    private static ItemDef Ref(int key)
    {
        var def = (ItemDef)Activator.CreateInstance(typeof(ItemDef), nonPublic: true)!;
        def.ConfigKey = key;
        return def;
    }

    /// <summary>
    /// A config carrying nothing but an item library. Both the library (protected constructor) and
    /// the config (no usable public constructor) are built without running game code, which is all
    /// the serializer needs to turn item ids into item types.
    /// </summary>
    private static SharedGameConfig ConfigWith(params ItemDefinition[] items)
    {
        var byKey = new Dictionary<int, ItemDefinition>();
        foreach (var item in items) byKey[item.ConfigKey] = item;

        var library = (GameConfigLibrary<int, ItemDefinition>)Activator.CreateInstance(
            typeof(GameConfigLibrary<int, ItemDefinition>),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { byKey, new NullRegistry() },
            culture: null)!;

        var config = (SharedGameConfig)RuntimeHelpers.GetUninitializedObject(typeof(SharedGameConfig));
        config.Items = library;
        return config;
    }

    private static string Serialize(SharedGameConfig config, object value)
    {
        var settings = DumpJson.CreateSettings(new JsonConverter[]
        {
            new ChainSerializer(config, dropsAsPercent: true, ConsoleDumpLog.Instance),
        });
        settings.Formatting = Formatting.None; // shapes are what these tests assert, not indentation
        return JsonConvert.SerializeObject(value, settings);
    }

    [Fact]
    public void ConstantProducer_pairs_products_with_quantities_and_drops_empty_item_types()
    {
        var config = ConfigWith(Item(1, "A"), Item(2, "B"), Item(3, ""));
        var producer = (ConstantProducer)Activator.CreateInstance(typeof(ConstantProducer), nonPublic: true)!;
        producer.Products = new List<ItemDef> { Ref(1), Ref(2), Ref(3) };
        producer.Quantities = new List<int> { 12 };

        // Always an array; quantities are zipped by index and default to 1 when the list runs out;
        // a product with no item type is skipped entirely (GameDataDumper.md §6).
        Assert.Equal(
            """{"Constant":[{"Item":"A","Quantity":12},{"Item":"B","Quantity":1}]}""",
            Serialize(config, producer));
    }

    [Fact]
    public void ConstantProducer_with_a_single_product_is_still_an_array()
    {
        var config = ConfigWith(Item(1, "A"));
        var producer = (ConstantProducer)Activator.CreateInstance(typeof(ConstantProducer), nonPublic: true)!;
        producer.Products = new List<ItemDef> { Ref(1) };
        producer.Quantities = new List<int> { 1 };

        Assert.Equal("""{"Constant":[{"Item":"A","Quantity":1}]}""", Serialize(config, producer));
    }

    [Fact]
    public void Producer_without_a_payload_collapses_to_its_bare_kind_string()
    {
        var config = ConfigWith();
        Assert.Equal("\"Empty\"", Serialize(config, new EmptyProducer()));
    }

    [Fact]
    public void PrefixProducer_run_length_encodes_its_opening_sequence()
    {
        var config = ConfigWith(Item(1, "A"), Item(2, "B"));
        var prefix = (PrefixProducer)Activator.CreateInstance(typeof(PrefixProducer), nonPublic: true)!;
        prefix.Marker = "M";
        prefix.Items = new List<ItemDef> { Ref(1), Ref(2), Ref(2), Ref(2), Ref(1) };
        prefix.BaseProducer = new EmptyProducer();

        Assert.Equal(
            """{"Marker":"M","Prefix":[{"Item":"A","Quantity":1},{"Item":"B","Quantity":3},{"Item":"A","Quantity":1}],"BaseProducer":"Empty"}""",
            Serialize(config, prefix));
    }

    [Fact]
    public void RandomProducer_merges_repeated_items_and_reports_percentages()
    {
        var config = ConfigWith(Item(1, "A"), Item(2, "B"));
        var producer = (RandomProducer)Activator.CreateInstance(typeof(RandomProducer), nonPublic: true)!;
        producer.OddsList = new List<ItemOdds> { Odds(1, 1), Odds(2, 1), Odds(1, 2) };

        // A repeats twice, so its key holds the summed weight 3 of the total 4.
        Assert.Equal(
            """{"Random":{"Odds":{"A":75.0,"B":25.0},"OddsWeights":{"A":3.0,"B":1.0}}}""",
            Serialize(config, producer));
    }

    // ── ChargeMath ──────────────────────────────────────────────────────────────────────────────

    private static ActivationFeatures Activation(int storageMax, bool startsFull, int howManyCycles, int activationAmount, int generatedPerCycle)
    {
        var data = new ActivationCycleData
        {
            ActivationAmountInCycle = new List<int> { activationAmount },
            HowManyAreGeneratedInCycle = new List<int> { generatedPerCycle },
        };
        var cycle = (ActivationCycle)Activator.CreateInstance(typeof(ActivationCycle), nonPublic: true)!;
        cycle.HowManyCycles = howManyCycles;
        cycle.DailyActivationCyclesData = data;

        var features = (ActivationFeatures)Activator.CreateInstance(typeof(ActivationFeatures), nonPublic: true)!;
        features.StorageMax = storageMax;
        features.StartsFull = startsFull;
        features.ActivationCycle = cycle;
        return features;
    }

    private static (int? MaxCharges, int StorageMax) ChargeMath(ActivationFeatures features) =>
        ((int?, int))typeof(ChainSerializer)
            .GetMethod("ChargeMath", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { features })!;

    [Fact]
    public void ChargeMath_reports_nothing_for_a_generator_with_no_cycle_data()
    {
        var features = (ActivationFeatures)Activator.CreateInstance(typeof(ActivationFeatures), nonPublic: true)!;
        features.StorageMax = 7;

        Assert.Equal((null, 7), ChargeMath(features));
    }

    [Fact]
    public void ChargeMath_treats_9999_mini_charges_as_never_depleting()
    {
        // The sentinel means "always available" (card decks and similar): no MaxCharges at all and
        // the configured StorageMax is left exactly as it is.
        Assert.Equal((null, 3), ChargeMath(Activation(storageMax: 3, startsFull: false, howManyCycles: 5, activationAmount: 9999, generatedPerCycle: 2)));
    }

    [Fact]
    public void ChargeMath_raises_an_infinite_generators_storage_to_one_full_charge()
    {
        // cycles == -1 refills forever, so storage is the tap budget — but it may be configured
        // below a single charge (Shoes L3: storage 3, one charge is 5 drops). The player still gets
        // the whole charge, so the reported storage is max(raw, dropsPerCharge) and there is exactly
        // one charge in it.
        Assert.Equal(((int?)1, 5), ChargeMath(Activation(storageMax: 3, startsFull: false, howManyCycles: -1, activationAmount: 1, generatedPerCycle: 5)));

        // Above one charge the same storage divides into whole charges.
        Assert.Equal(((int?)4, 20), ChargeMath(Activation(storageMax: 20, startsFull: false, howManyCycles: -1, activationAmount: 1, generatedPerCycle: 5)));
    }

    [Fact]
    public void ChargeMath_rewrites_storage_to_the_lifetime_drop_count_for_a_finite_generator()
    {
        // Not StartsFull: one tap per cycle, and the storage the config carries is replaced by what
        // the generator will produce over its whole life (3 cycles x 2 drops).
        Assert.Equal(((int?)3, 6), ChargeMath(Activation(storageMax: 99, startsFull: false, howManyCycles: 3, activationAmount: 1, generatedPerCycle: 2)));

        // StartsFull keeps the configured storage and derives the taps from it instead (8 / 2 = 4
        // per cycle, over 3 cycles).
        Assert.Equal(((int?)12, 8), ChargeMath(Activation(storageMax: 8, startsFull: true, howManyCycles: 3, activationAmount: 1, generatedPerCycle: 2)));
    }

    private static ItemOdds Odds(int itemKey, int weight)
    {
        var odds = (ItemOdds)Activator.CreateInstance(typeof(ItemOdds), nonPublic: true)!;
        odds.Type = Ref(itemKey);
        odds.Weight = weight;
        return odds;
    }
}
