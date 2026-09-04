using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Order recipes (OrderFeatures — Empty Jam Jar, 26.07.01) must reach Datatable/Items so
/// Module:Areas can price a required chain (Pantry Jam) that only an order produces. The parser
/// already collected <see cref="ParsedItem.OrderTasks"/>; the emit dropped them, so the wiki
/// Area Requirements table silently skipped Jam.
/// </summary>
public class OrderTasksEmitTests
{
    private static ParsedChain Chain(params ParsedItem[] items)
    {
        var chain = new ParsedChain { ConfigKey = "FirstFloorPantry_Jar", DisplayName = "Empty Jam Jars", OriginalName = "Empty Jam Jars" };
        chain.Items.AddRange(items);
        return chain;
    }

    [Fact]
    public void Order_item_emits_recipes_with_odds_required_and_rewards_in_declaration_order()
    {
        var jar = new ParsedItem { ItemType = "FirstFloorPantry_Jar_01", Name = "Empty Jam Jar", Level = 1, Description = "d", IsOrder = true };
        jar.OrderTasks = new List<ParsedTask>
        {
            new() { Odds = 60, OddsWeight = 6, Required = { ("FirstFloorPantry_Fruit_03", 1), ("FirstFloorPantry_Spices_03", 1) }, Rewards = { ("FirstFloorPantry_Jam_01", 1) } },
            new() { Odds = 30, OddsWeight = 3, Required = { ("FirstFloorPantry_Fruit_09", 1), ("FirstFloorPantry_Spices_05", 1) }, Rewards = { ("FirstFloorPantry_Jam_02", 1) } },
            new() { Odds = 10, OddsWeight = 1, Required = { ("FirstFloorPantry_Fruit_12", 1), ("FirstFloorPantry_Spices_07", 1) }, Rewards = { ("FirstFloorPantry_Jam_03", 1) } },
        };

        var lua = new LuaGeneratorService().GenerateCombinedItemsAndChainNamesLua(new List<ParsedChain> { Chain(jar) });

        Assert.Contains("isOrder = true, orderTasks = {" +
            "{odds = 60, req = {{id = \"FirstFloorPantry_Fruit_03\", amt = 1}, {id = \"FirstFloorPantry_Spices_03\", amt = 1}}, rew = {{id = \"FirstFloorPantry_Jam_01\", amt = 1}}}, " +
            "{odds = 30, req = {{id = \"FirstFloorPantry_Fruit_09\", amt = 1}, {id = \"FirstFloorPantry_Spices_05\", amt = 1}}, rew = {{id = \"FirstFloorPantry_Jam_02\", amt = 1}}}, " +
            "{odds = 10, req = {{id = \"FirstFloorPantry_Fruit_12\", amt = 1}, {id = \"FirstFloorPantry_Spices_07\", amt = 1}}, rew = {{id = \"FirstFloorPantry_Jam_03\", amt = 1}}}}, ",
            lua);
    }

    [Fact]
    public void Non_order_item_emits_nothing_order_related()
    {
        var plain = new ParsedItem { ItemType = "FirstFloorPantry_Jam_01", Name = "Peachy Jam", Level = 1, Description = "d" };
        var lua = new LuaGeneratorService().GenerateCombinedItemsAndChainNamesLua(new List<ParsedChain> { Chain(plain) });
        Assert.DoesNotContain("orderTasks", lua);
        Assert.DoesNotContain("isOrder", lua);
    }
}
