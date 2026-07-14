using System.Collections.Generic;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Merge Stages table: every level renders exactly ONE row block, no matter how many mapped
/// variants share it. Regression for Basic Camera (user report 2026-07-11): six seasonal chains
/// mapped as isVariant onto one wiki chain rendered a full row per item — and because the
/// displayed Lvl is an incrementing #var counter, the table showed phantom levels 6..30.
/// Identical variants collapse into one row; variants with differing column values render the
/// existing variant sub-rows (Variant column A/B/C…).
/// </summary>
public class VariantLevelRowTests
{
    private static WikiTableGenerator NewGen() => new(new DataService(new ChainNameService()));

    private static ParsedItem Item(string itemType, int level, bool variant = false)
        => new()
        {
            ItemType = itemType,
            Name = $"Item L{level}",
            Level = level,
            Description = "d",
            IsVariant = variant,
        };

    private static int CountLevelRows(string table)
        => Regex.Matches(table, Regex.Escape("{{#var:Level}} <!--")).Count;

    [Fact]
    public void IdenticalVariants_RenderSingleRowPerLevel()
    {
        var chain = new ParsedChain { ConfigKey = "LS_LuckySnap2024_BasicCamera", DisplayName = "Basic Camera" };
        foreach (var season in new[] { "LS_LuckySnap2024", "LS_Easter", "LS_Summer" })
            for (int lvl = 1; lvl <= 5; lvl++)
                chain.Items.Add(Item($"{season}_BasicCamera_{lvl:00}", lvl, variant: season != "LS_LuckySnap2024"));

        var table = NewGen().Generate(chain, "Basic Camera", lowPrices: false);

        // 15 items over 5 levels → exactly 5 row blocks, never 15
        Assert.Equal(5, CountLevelRows(table));
    }

    [Fact]
    public void DifferingVariants_RenderVariantSubRowsNotPhantomLevels()
    {
        var chain = new ParsedChain { ConfigKey = "LS_LuckySnap2024_BasicCamera", DisplayName = "Basic Camera" };
        // L1 identical pair + L5 pair differing in fishing drops
        chain.Items.Add(Item("LS_LuckySnap2024_BasicCamera_01", 1));
        chain.Items.Add(Item("LS_Summer_BasicCamera_01", 1, variant: true));

        var top24 = Item("LS_LuckySnap2024_BasicCamera_05", 5);
        top24.IsFishingRod = true;
        top24.FishingOdds = new Dictionary<string, double> { ["LS_Winter2024_Common_Mallard_01"] = 100.0 };
        var topSummer = Item("LS_Summer_BasicCamera_05", 5, variant: true);
        topSummer.IsFishingRod = true;
        topSummer.FishingOdds = new Dictionary<string, double> { ["LS_Common_HoodedWarbler_01"] = 100.0 };
        // primary item must also be flagged variant so the variant set includes both
        top24.IsVariant = true;
        chain.Items.Add(top24);
        chain.Items.Add(topSummer);

        var table = NewGen().Generate(chain, "Basic Camera", lowPrices: false);

        // 4 items over 2 levels → exactly 2 level blocks
        Assert.Equal(2, CountLevelRows(table));
        // differing L5 gets the Variant column with letters
        Assert.Contains("| A", table);
        Assert.Contains("| B", table);
    }
}
