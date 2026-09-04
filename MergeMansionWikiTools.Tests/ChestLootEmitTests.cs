using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Chest loot emit: items with ChestFeatures.LootProducer surface their contents in the
/// generated items Lua — a random chest as <c>chestLoot = {{id, value}, …}</c> (per-roll odds,
/// descending), a constant Reward Box as the ORDERED guaranteed <c>chestConstant = {{id, qty}, …}</c>
/// payload — plus <c>isChest</c> and <c>chestRolls</c> (HowManyToRoll). Non-chests stay untouched.
/// Previously the parser collected the data but the emit dropped them, so no chest on the wiki
/// showed its drops.
/// </summary>
public class ChestLootEmitTests
{
    private static ParsedChain Chain(params ParsedItem[] items)
    {
        var chain = new ParsedChain
        {
            ConfigKey = "TestChain",
            DisplayName = "Test Chain",
            OriginalName = "Test Chain",
        };
        chain.Items.AddRange(items);
        return chain;
    }

    private static ParsedItem Item(string itemType, int level = 1)
        => new()
        {
            ItemType = itemType,
            Name = "Test Item",
            Level = level,
            Description = "desc",
        };

    [Fact]
    public void RandomChest_EmitsChestLootOddsAndRolls()
    {
        var chest = Item("CSE_DailyChallenge_DailyChest1_01");
        chest.IsChest = true;
        chest.ChestRollCount = 5;
        chest.ChestRewardOdds = new Dictionary<string, double>
        {
            ["InfiniteEnergySmall_01"] = 1.4265335235378032,
            ["LevelDownBoosterScissors_01"] = 2.8530670470756063,
        };

        var lua = new LuaGeneratorService().GenerateCombinedItemsAndChainNamesLua(
            new List<ParsedChain> { Chain(chest) });

        Assert.Contains("isChest = true", lua);
        Assert.Contains("chestRolls = 5", lua);
        // descending by odds: Scissors (2.85…) before InfiniteEnergySmall (1.43…)
        Assert.Contains(
            "chestLoot = {{id = \"LevelDownBoosterScissors_01\", value = 2.8530670470756063}, " +
            "{id = \"InfiniteEnergySmall_01\", value = 1.4265335235378032}}",
            lua);
        Assert.DoesNotContain("chestConstant", lua);
    }

    [Fact]
    public void ConstantChest_EmitsOrderedPayloadInsteadOfSynthesizedOdds()
    {
        var chest = Item("CSE_SoloMilestone_Chest1_01");
        chest.IsChest = true;
        chest.ChestRollCount = 9;
        chest.ChestRewardItems = new List<(string Item, int Quantity)>
        {
            ("TCE_CardPackBasic_1Stars_02", 3),
            ("TCE_CardPackBasic_2Stars_02", 2),
        };
        // the parser synthesizes 100% odds next to the constant payload — those must NOT be emitted
        chest.ChestRewardOdds = new Dictionary<string, double>
        {
            ["TCE_CardPackBasic_1Stars_02"] = 100.0,
            ["TCE_CardPackBasic_2Stars_02"] = 100.0,
        };

        var lua = new LuaGeneratorService().GenerateCombinedItemsAndChainNamesLua(
            new List<ParsedChain> { Chain(chest) });

        Assert.Contains("isChest = true", lua);
        Assert.Contains("chestRolls = 9", lua);
        Assert.Contains(
            "chestConstant = {{id = \"TCE_CardPackBasic_1Stars_02\", qty = 3}, " +
            "{id = \"TCE_CardPackBasic_2Stars_02\", qty = 2}}",
            lua);
        Assert.DoesNotContain("chestLoot", lua);
    }

    [Fact]
    public void PrefixChest_EmitsChestPrefixAndRandomLoot()
    {
        // PrefixProducer chest (Daily Trades card chest): guaranteed prefix drops first
        // (4x 1-star + 1x 2-star envelope, ORDER preserved), remaining rolls from the random
        // BaseProducer. Both must surface: chestPrefix (ordered qty list) + chestLoot (odds).
        var chest = Item("DailyTasksV2ChestCardsProducers3_01");
        chest.IsChest = true;
        chest.ChestRollCount = 7;
        chest.ChestPrefixItems = new List<(string, int)>
        {
            ("TCE_CardPackBasic_1Stars_01", 4),
            ("TCE_CardPackBasic_2Stars_01", 1),
        };
        chest.ChestRewardOdds = new Dictionary<string, double>
        {
            ["BroomCabinet_01"] = 12.820512820512821,
            ["Toolbox_01"] = 12.820512820512821,
        };

        var lua = new LuaGeneratorService().GenerateCombinedItemsAndChainNamesLua(
            new List<ParsedChain> { Chain(chest) });

        Assert.Contains("chestRolls = 7", lua);
        Assert.Contains(
            "chestPrefix = {{id = \"TCE_CardPackBasic_1Stars_01\", qty = 4}, " +
            "{id = \"TCE_CardPackBasic_2Stars_01\", qty = 1}}",
            lua);
        Assert.Contains("chestLoot = {", lua);   // random part still emitted alongside the prefix
    }

    [Fact]
    public void NonChest_HasNoChestFields()
    {
        var lua = new LuaGeneratorService().GenerateCombinedItemsAndChainNamesLua(
            new List<ParsedChain> { Chain(Item("Toolbox_01")) });

        Assert.DoesNotContain("isChest", lua);
        Assert.DoesNotContain("chestLoot", lua);
        Assert.DoesNotContain("chestConstant", lua);
        Assert.DoesNotContain("chestRolls", lua);
    }
}
