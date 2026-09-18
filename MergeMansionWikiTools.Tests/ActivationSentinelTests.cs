using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// The 9999 sentinel: <c>MiniChargesInSingleCharge</c> / <c>DropsInSingleMiniCharge</c> set to 9999
/// is the game's marker for a stateful item that produces indefinitely, NOT a charge count
/// (<c>_CONTEXT/Game/Mechaniky.md</c> → "Sentinel hodnoty 9999/9999").
/// <para>
/// <c>LuaGeneratorService</c> has honoured it since v0.20.x — it emits no drop data for such items,
/// so the wiki renders a dash. <c>WikiTableGenerator</c> did not, so it still opened a "Drops Values"
/// column whose every cell was that dash (user report: Jackie &amp; Roddy, Murder at the Mansion,
/// where all three items are 9999/1 infinite producers). Fixed in v0.24.74 by sharing one rule,
/// <see cref="ParsedItem.IsActivationSentinel"/>.
/// </para>
/// </summary>
public class ActivationSentinelTests
{
    private static WikiTableGenerator NewGen() => new(new DataService(new ChainNameService()));

    /// <summary>
    /// A generator shaped like the real Jackie &amp; Roddy items: infinite cycles, 9999 storage and
    /// 9999 mini-charges, one drop per mini-charge.
    /// </summary>
    private static ParsedItem Sentinel(string type, string name, int level) => new()
    {
        ItemType = type,
        Name = name,
        Level = level,
        Description = "d",
        IsGenerator = true,
        ActivationAmountInCycle = 9999,
        HowManyGeneratedInCycle = 1,
        ActivationHowManyCycles = -1,
        StorageMax = 9999,
        DropOdds = new Dictionary<string, double> { ["LDE_MurderAtTheMansion_DetectiveTools_01"] = 100 },
    };

    /// <summary>An ordinary finite generator: 3 charges of 2 drops.</summary>
    private static ParsedItem RealProducer(string type, string name, int level) => new()
    {
        ItemType = type,
        Name = name,
        Level = level,
        Description = "d",
        IsGenerator = true,
        ActivationAmountInCycle = 2,
        HowManyGeneratedInCycle = 1,
        ActivationHowManyCycles = 3,
        StorageMax = 6,
        DropOdds = new Dictionary<string, double> { ["SomeChain_01"] = 100 },
    };

    private static ParsedChain Chain(params ParsedItem[] items) => new()
    {
        ConfigKey = "LDE_MurderAtTheMansion_JackieAndRoddy",
        DisplayName = "Jackie & Roddy",
        Items = new List<ParsedItem>(items),
    };

    // ── The rule itself ───────────────────────────────────────────────

    [Theory]
    [InlineData(9999, 1, true)]
    [InlineData(1, 9999, true)]
    [InlineData(9999, 9999, true)]
    [InlineData(10000, 1, true)]   // >= 9999, not == : a bigger marker still is one
    [InlineData(2, 1, false)]
    [InlineData(96, 32, false)]    // Secret Code Book — big but real
    public void IsActivationSentinel_flagsOnlyTheMarkerValues(int miniCharges, int dropsPerMiniCharge, bool expected)
    {
        var item = new ParsedItem
        {
            ActivationAmountInCycle = miniCharges,
            HowManyGeneratedInCycle = dropsPerMiniCharge,
        };

        Assert.Equal(expected, item.IsActivationSentinel);
    }

    // ── Drops Values column ───────────────────────────────────────────

    [Fact]
    public void DropsValuesColumn_isNotEmitted_whenEveryProducerIsASentinel()
    {
        // The reported table: three sentinel producers, so the column could only ever be dashes.
        var lua = NewGen().Generate(
            Chain(Sentinel("LDE_MurderAtTheMansion_JackieAndRoddy_01", "Jackie", 1),
                  Sentinel("LDE_MurderAtTheMansion_JackieAndRoddy_02", "Roddy", 2),
                  Sentinel("LDE_MurderAtTheMansion_JackieAndRoddy_03", "Detective Team", 3)),
            "Jackie & Roddy", lowPrices: false);

        Assert.DoesNotContain("Drops Values", lua);
        Assert.DoesNotContain("GetItemDropValuesFromChainName", lua);
    }

    [Fact]
    public void DropsValuesColumn_survives_whenARealProducerSharesTheChain()
    {
        var lua = NewGen().Generate(
            Chain(Sentinel("Mixed_01", "Marker", 1),
                  RealProducer("Mixed_02", "Producer", 2)),
            "Mixed", lowPrices: false);

        Assert.Contains("Drops Values", lua);
        Assert.Contains("GetItemDropValuesFromChainName", lua);
        // …and the sentinel row still gets a dash, because the invoke has nothing for it.
        Assert.Contains("{{Dash}}", lua);
    }

    [Fact]
    public void DropsValuesColumn_isStillEmitted_forOrdinaryProducers()
    {
        var lua = NewGen().Generate(
            Chain(RealProducer("Plain_01", "Producer", 1)),
            "Plain", lowPrices: false);

        Assert.Contains("Drops Values", lua);
    }

    // ── Drops cell (the single-charge merge) ──────────────────────────

    [Fact]
    public void DropsCell_neverMergesTheSentinelCountIntoTheItemLabel()
    {
        // 9999 x 1 >= StorageMax would otherwise qualify as a "single charge" and render "9999x Item".
        var lua = NewGen().Generate(
            Chain(Sentinel("LDE_MurderAtTheMansion_JackieAndRoddy_01", "Jackie", 1)),
            "Jackie & Roddy", lowPrices: false);

        Assert.DoesNotContain("9999×", lua);
        Assert.DoesNotContain("9999x", lua);
    }
}
