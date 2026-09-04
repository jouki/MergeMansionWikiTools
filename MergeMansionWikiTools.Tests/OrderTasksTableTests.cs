using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// The Tasks table must reflect how the game actually picks the next order, which depends on the
/// OrderProducer wrapper (verified against ISIL of 26.05.01 libil2cpp):
///
/// * <c>ControlledRandom</c> — Produce() calls WeightedDistributionStates.Roll and
///   AdvanceSequenceIndex() is an empty method. There is no sequence index, so no fixed order and
///   no repeating cycle: OddsWeight is a probability weight, not a slot count. Rendered as a
///   "Chance" column with the config percentages.
/// * <c>Constant</c> / <c>ControlledPredefinedSequence</c> — Produce(orderIndex) indexes
///   GenerationOdds directly and OrderCount is GenerationOdds.Count (never the weight sum), so the
///   cycle is one row per declared task. Rendered as a fixed-order "Task" column numbered 1..N.
///
/// Regression: Empty Jam Jars (ControlledRandom, weights 6/3/1) was rendered as "1 - 6 / 7 - 9 / 10"
/// with a "fixed order" intro, but players see the three recipes interleaved at random.
/// </summary>
public class OrderTasksTableTests
{
    private static WikiTableGenerator NewGen() => new(new DataService(new ChainNameService()));

    private static List<ParsedTask> JamJarTasks() => new()
    {
        new() { Odds = 60, OddsWeight = 6, Required = { ("FirstFloorPantry_Fruit_03", 1), ("FirstFloorPantry_Spices_03", 1) }, Rewards = { ("FirstFloorPantry_Jam_01", 1) } },
        new() { Odds = 30, OddsWeight = 3, Required = { ("FirstFloorPantry_Fruit_09", 1), ("FirstFloorPantry_Spices_05", 1) }, Rewards = { ("FirstFloorPantry_Jam_02", 1) } },
        new() { Odds = 10, OddsWeight = 1, Required = { ("FirstFloorPantry_Fruit_12", 1), ("FirstFloorPantry_Spices_07", 1) }, Rewards = { ("FirstFloorPantry_Jam_03", 1) } },
    };

    private static ParsedItem OrderItem(string producerKind, List<ParsedTask> tasks) => new()
    {
        ItemType = "FirstFloorPantry_Jar_01",
        Name = "Empty Jam Jar",
        Level = 1,
        Description = "d",
        IsOrder = true,
        OrderProducerKind = producerKind,
        OrderTasks = tasks,
    };

    // ── ControlledRandom: chances, no ordering claim ──

    [Fact]
    public void ControlledRandom_emits_chance_column_with_config_percentages()
    {
        var table = NewGen().GenerateOrderTasksTable(OrderItem("ControlledRandom", JamJarTasks()));

        Assert.NotNull(table);
        Assert.Contains("! Chance", table);
        Assert.Contains("! 60%", table);
        Assert.Contains("! 30%", table);
        Assert.Contains("! 10%", table);
        // Slot ranges are a fixed-order concept and must not appear.
        Assert.DoesNotContain("! Task", table);
        Assert.DoesNotContain("1 - 6", table);
        Assert.DoesNotContain("7 - 9", table);
    }

    [Fact]
    public void ControlledRandom_intro_does_not_claim_a_fixed_order()
    {
        var table = NewGen().GenerateOrderTasksTable(OrderItem("ControlledRandom", JamJarTasks()));

        Assert.NotNull(table);
        Assert.DoesNotContain("fixed order", table!);
        Assert.Contains("random", table);
    }

    [Fact]
    public void ControlledRandom_rounds_repeating_percentages_to_two_decimals()
    {
        // Distillation Apparatus: weights 4/4/4/1/1/1 → 26.666…% and 6.666…%
        var tasks = new List<ParsedTask>
        {
            new() { Odds = 26.666666666666668, OddsWeight = 4, Required = { ("Perfumery_Lemon_03", 1) }, Rewards = { ("Perfumery_Perfume_01", 1) } },
            new() { Odds = 6.666666666666667, OddsWeight = 1, Required = { ("Perfumery_Bottle_03", 2) }, Rewards = { ("Perfumery_PerfumeCollection_02", 1) } },
        };

        var table = NewGen().GenerateOrderTasksTable(OrderItem("ControlledRandom", tasks));

        Assert.NotNull(table);
        Assert.Contains("! 26.67%", table);
        Assert.Contains("! 6.67%", table);
        Assert.DoesNotContain("26.666", table);
    }

    // ── Constant / ControlledPredefinedSequence: fixed order, one row per task ──

    [Fact]
    public void Constant_numbers_rows_by_declaration_index_ignoring_weights()
    {
        // Vending Machine: Constant with weights 3/5/1/1/1/5. OrderCount is the task count (6),
        // so the cycle is 6 orders — the weights never expand into slots.
        var tasks = new List<ParsedTask>();
        foreach (var w in new[] { 3, 5, 1, 1, 1, 5 })
            tasks.Add(new ParsedTask { Odds = 100.0 * w / 16, OddsWeight = w, Required = { ("FactoryReception_Coin_05", 1) }, Rewards = { ("FactoryReception_Edible_01", 8) } });

        var table = NewGen().GenerateOrderTasksTable(OrderItem("Constant", tasks));

        Assert.NotNull(table);
        Assert.Contains("! Task", table);
        Assert.Contains("fixed order", table);
        for (var i = 1; i <= 6; i++)
            Assert.Contains($"! {i}\n", table!.Replace("\r\n", "\n"));
        // Cumulative slot ranges from weights must be gone.
        Assert.DoesNotContain("1 - 3", table);
        Assert.DoesNotContain("4 - 8", table);
        Assert.DoesNotContain("12 - 16", table);
        Assert.DoesNotContain("Chance", table);
    }

    [Fact]
    public void PredefinedSequence_numbers_rows_by_declaration_index()
    {
        var tasks = new List<ParsedTask>();
        for (var i = 0; i < 3; i++)
            tasks.Add(new ParsedTask { Odds = 33.333, OddsWeight = 1, Required = { ("LDE_GreenAcresQuest2024_Crop_03", 1) }, Rewards = { ("LDE_GreenAcresQuest2024_Token_01", 1) } });

        var table = NewGen().GenerateOrderTasksTable(OrderItem("ControlledPredefinedSequence", tasks));

        Assert.NotNull(table);
        Assert.Contains("! Task", table);
        Assert.Contains("fixed order", table);
        Assert.DoesNotContain("Chance", table);
    }

    [Fact]
    public void Unknown_producer_kind_falls_back_to_fixed_order_rows()
    {
        var table = NewGen().GenerateOrderTasksTable(OrderItem("", JamJarTasks()));

        Assert.NotNull(table);
        Assert.Contains("! Task", table);
        Assert.DoesNotContain("Chance", table);
    }
}
