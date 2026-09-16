using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Cloud.Config;
using GameLogic;
using GameLogic.Area;
using GameLogic.Config;
using GameLogic.Config.Costs;
using GameLogic.Hotspots;
using GameLogic.Player.Items;
using GameLogic.Player.Requirements;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;
using MergeMansionWikiTools.Dumper.Support;
using Metaplay.Core.Config;
using Metaplay.Unity;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// Covers the pure, config-only pieces of the area dump: the Graphviz task-dependency graph and the
/// item-flavoured requirement payloads. Localization is not loaded, so every LocMan lookup returns
/// its own key — which keeps the expected strings deterministic.
/// </summary>
[Collection(GlobalGameStateCollection.Name)]
public class AreaSerializerTests
{
    /// <summary>
    /// The expected DOT labels below are the ones LocMan produces with NO language loaded (it echoes
    /// the key). <c>MetaplaySDK.ActiveLanguage</c> is process-global and the parity test loads a real
    /// language into it, so clear it here as well — the shared collection makes the two classes run
    /// one after the other, and this makes the order between them irrelevant.
    /// </summary>
    public AreaSerializerTests() => MetaplaySDK.ActiveLanguage = null;

    private sealed class NullRegistry : IGameConfigDataRegistry
    {
        public void RegisterReferenceResolver(Type type, Func<object, object> tryResolveFunc) { }
    }

    private static GameConfigLibrary<TKey, TValue> Library<TKey, TValue>(Dictionary<TKey, TValue> byKey)
        where TKey : notnull
        => (GameConfigLibrary<TKey, TValue>)Activator.CreateInstance(
            typeof(GameConfigLibrary<TKey, TValue>),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { byKey, new NullRegistry() },
            culture: null)!;

    private static ItemDef ItemRef(int key)
    {
        var def = (ItemDef)Activator.CreateInstance(typeof(ItemDef), nonPublic: true)!;
        def.ConfigKey = key;
        return def;
    }

    private static HotspotDef HotspotRef(HotspotId id)
    {
        var def = (HotspotDef)Activator.CreateInstance(typeof(HotspotDef), nonPublic: true)!;
        def.ConfigKey = id;
        return def;
    }

    private static HotspotDefinition Hotspot(HotspotId id, List<PlayerRequirement>? requirements = null, params HotspotId[] parents)
    {
        var hotspot = new HotspotDefinition { Id = id, RequirementsList = requirements ?? new List<PlayerRequirement>() };
        hotspot.UnlockingParentRefs = new List<HotspotDef>();
        foreach (var parent in parents) hotspot.UnlockingParentRefs.Add(HotspotRef(parent));
        return hotspot;
    }

    private static GameCurrencyCost Coins(long amount)
    {
        var cost = (GameCurrencyCost)Activator.CreateInstance(typeof(GameCurrencyCost), nonPublic: true)!;
        cost.Type = Currencies.Coins;
        cost.CurrencyAmount = amount;
        return cost;
    }

    private static PlayerItemRequirement ItemsNeeded(int count, params int[] itemKeys)
    {
        var requirement = new PlayerItemRequirement { Requirement = count };
        var refs = new List<ItemDef>();
        foreach (var key in itemKeys) refs.Add(ItemRef(key));
        typeof(PlayerItemRequirement)
            .GetProperty("ItemRefs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(requirement, refs);
        return requirement;
    }

    private static SharedGameConfig ConfigWith(
        Dictionary<HotspotId, HotspotDefinition> hotspots,
        Dictionary<int, ItemDefinition>? items = null)
    {
        var config = (SharedGameConfig)RuntimeHelpers.GetUninitializedObject(typeof(SharedGameConfig));
        config.HotspotDefinitions = Library(hotspots);
        config.Items = Library(items ?? new Dictionary<int, ItemDefinition>());
        return config;
    }

    /// <summary>Runs the private DOT builder for one area.</summary>
    private static string Dot(SharedGameConfig config, AreaInfo area) =>
        (string)typeof(AreaSerializer)
            .GetMethod("BuildTaskDependencies", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new AreaSerializer(config, ConsoleDumpLog.Instance), new object[] { area })!;

    private static AreaInfo Area(params HotspotId[] tasks)
    {
        var area = new AreaInfo { HotspotsRefs = new List<HotspotDef>() };
        foreach (var task in tasks) area.HotspotsRefs.Add(HotspotRef(task));
        return area;
    }

    [Fact]
    public void TaskGraph_numbers_a_parent_before_the_child_that_pulled_it_in()
    {
        // The area lists the tasks in an arbitrary order; the graph walks them by ascending
        // HotspotId value instead (402 before 404) and registers each task's parents first. So the
        // parent RemoveLeaves2 (404) becomes node 0 even though the child RemoveLeaves1 (402) is the
        // one that comes first in the walk — the quirk that makes the numbering neither the area's
        // order nor a topological one.
        var config = ConfigWith(new Dictionary<HotspotId, HotspotDefinition>
        {
            [HotspotId.DrivewayRemoveLeaves1] = Hotspot(HotspotId.DrivewayRemoveLeaves1, null, HotspotId.DrivewayRemoveLeaves2),
            [HotspotId.DrivewayRemoveLeaves2] = Hotspot(HotspotId.DrivewayRemoveLeaves2),
        });

        var dot = Dot(config, Area(HotspotId.DrivewayRemoveLeaves2, HotspotId.DrivewayRemoveLeaves1));

        Assert.Equal(
            "digraph{rankdir=\"TB\";node[shape=box];"
            + "0[label=\"DrivewayRemoveLeaves2\r\n\"];1[label=\"DrivewayRemoveLeaves1\r\n\"];"
            + "0->1;}",
            dot);
    }

    [Fact]
    public void TaskGraph_groups_edges_by_source_and_keeps_a_parent_from_outside_the_area()
    {
        // RemoveGrass (403) unlocks both siblings and is not itself one of the area's tasks: it
        // still gets a node, and its two edges are emitted together under its own node number.
        var config = ConfigWith(new Dictionary<HotspotId, HotspotDefinition>
        {
            [HotspotId.DrivewayRemoveLeaves1] = Hotspot(HotspotId.DrivewayRemoveLeaves1, null, HotspotId.DrivewayRemoveGrass),
            [HotspotId.DrivewayRemoveLeaves2] = Hotspot(HotspotId.DrivewayRemoveLeaves2, null, HotspotId.DrivewayRemoveGrass),
            [HotspotId.DrivewayRemoveGrass] = Hotspot(HotspotId.DrivewayRemoveGrass),
        });

        var dot = Dot(config, Area(HotspotId.DrivewayRemoveLeaves1, HotspotId.DrivewayRemoveLeaves2));

        Assert.Equal(
            "digraph{rankdir=\"TB\";node[shape=box];"
            + "0[label=\"DrivewayRemoveGrass\r\n\"];1[label=\"DrivewayRemoveLeaves1\r\n\"];2[label=\"DrivewayRemoveLeaves2\r\n\"];"
            + "0->1;0->2;}",
            dot);
    }

    [Fact]
    public void TaskGraph_labels_carry_the_required_items_and_the_coin_cost()
    {
        var config = ConfigWith(
            new Dictionary<HotspotId, HotspotDefinition>
            {
                [HotspotId.DrivewayRemoveLeaves1] = Hotspot(HotspotId.DrivewayRemoveLeaves1, new List<PlayerRequirement>
                {
                    ItemsNeeded(1, 1),
                    ItemsNeeded(1, 2, 3),
                    new CostRequirement { RequiredCost = Coins(1500) },
                }),
            },
            new Dictionary<int, ItemDefinition>
            {
                [1] = new() { ConfigKey = 1, ItemType = "Edge_01" },
                [2] = new() { ConfigKey = 2, ItemType = "Saw_01" },
                [3] = new() { ConfigKey = 3, ItemType = "ActiveSaw_01" },
            });

        // Separate requirements run together with no separator, alternatives inside ONE requirement
        // are separated by CRLF, and a coin cost adds a line of its own — the exact shape of the
        // golden "Place Rufus sign" label. Without a loaded language LocMan.Get echoes the key, so
        // the item names read "Item_Edge1" and friends.
        Assert.Equal(
            "digraph{rankdir=\"TB\";node[shape=box];"
            + "0[label=\"DrivewayRemoveLeaves1\r\nItem_Edge1 x1Item_Saw1 x1\r\nItem_ActiveSaw1 x1\r\nCoins x1500\"];}",
            Dot(config, Area(HotspotId.DrivewayRemoveLeaves1)));
    }

    [Fact]
    public void ItemAcquired_is_a_list_of_ItemRef_Requirement_pairs()
    {
        var config = ConfigWith(
            new Dictionary<HotspotId, HotspotDefinition>(),
            new Dictionary<int, ItemDefinition>
            {
                [1] = new() { ConfigKey = 1, ItemType = "Axe_01" },
                [2] = new() { ConfigKey = 2, ItemType = "Soap_02" },
            });

        var settings = DumpJson.CreateSettings(new JsonConverter[]
        {
            new PlayerRequirementConverter(ConsoleDumpLog.Instance),
            new ConfigDefinitionConverter(config),
        });
        settings.Formatting = Formatting.None;

        Assert.Equal(
            """{"ItemAcquired":[{"ItemRef":"Axe_01","Requirement":1},{"ItemRef":"Soap_02","Requirement":1}]}""",
            JsonConvert.SerializeObject((PlayerRequirement)ItemsNeeded(1, 1, 2), settings));
    }

    [Fact]
    public void ItemNeededAndConsumed_collapses_to_the_first_item_type()
    {
        var config = ConfigWith(
            new Dictionary<HotspotId, HotspotDefinition>(),
            new Dictionary<int, ItemDefinition> { [7] = new() { ConfigKey = 7, ItemType = "CorridorKey_01" } });

        var settings = DumpJson.CreateSettings(new JsonConverter[]
        {
            new PlayerRequirementConverter(ConsoleDumpLog.Instance),
            new ConfigDefinitionConverter(config),
        });
        settings.Formatting = Formatting.None;

        var requirement = new ItemNeededAndConsumeRequirement { ItemDefs = new List<ItemDef> { ItemRef(7) } };
        Assert.Equal(
            """{"ItemNeededAndConsumed":"CorridorKey_01"}""",
            JsonConvert.SerializeObject((PlayerRequirement)requirement, settings));
    }
}
