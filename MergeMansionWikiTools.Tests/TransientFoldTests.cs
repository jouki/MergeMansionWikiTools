using System;
using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// A stage flagged <c>isTransient</c> exists for seconds and immediately rolls on, so the wiki links
/// to what it BECOMES instead — odds included (v0.24.80).
/// <para>
/// From the Voyance's House loop: the locked house is fuelled into
/// <c>VoyanceHouseUnlockedAB_01</c>, which lives 3 s and splits 50/50 into the Investigation or the
/// Seance. "Transforms to: Lady Voyance's House" told the reader nothing; "50% Investigation / 50%
/// Seance" is the fact they came for.
/// </para>
/// </summary>
public class TransientFoldTests
{
    private static ParsedItem Plain(string type) => new() { ItemType = type, Name = type, Level = 1, Description = "d" };

    private static ParsedItem Transient(string type, params (string Target, double Pct)[] roll)
    {
        var item = Plain(type);
        item.IsTransient = true;
        item.DecayIntoOdds = roll.ToDictionary(r => r.Target, r => r.Pct);
        return item;
    }

    private static Func<string, ParsedItem?> Lookup(params ParsedItem[] items)
    {
        var map = items.ToDictionary(i => i.ItemType, i => i, System.StringComparer.OrdinalIgnoreCase);
        return t => map.TryGetValue(t, out var i) ? i : null;
    }

    [Fact]
    public void AnOrdinaryTarget_comesBackUnchanged()
    {
        var result = TransientFold.Resolve("Plain_01", Lookup(Plain("Plain_01")));

        Assert.Equal(new[] { "Plain_01" }, result.Select(r => r.ItemType));
        Assert.Null(result[0].Odds);
    }

    [Fact]
    public void AnUnknownTarget_comesBackUnchanged()
    {
        // Targets outside the loaded chains must not vanish — the caller still renders them.
        var result = TransientFold.Resolve("Nowhere_01", Lookup());

        Assert.Equal(new[] { "Nowhere_01" }, result.Select(r => r.ItemType));
    }

    [Fact]
    public void ATransient_isReplacedByItsOutcomes_withOdds()
    {
        var lookup = Lookup(Transient("Unlocked_01", ("SinkI_01", 50), ("SinkS_01", 50)),
                            Plain("SinkI_01"), Plain("SinkS_01"));

        var result = TransientFold.Resolve("Unlocked_01", lookup);

        Assert.Equal(new[] { "SinkI_01", "SinkS_01" }, result.Select(r => r.ItemType));
        Assert.All(result, r => Assert.Equal(50, r.Odds));
    }

    [Fact]
    public void ChainedTransients_multiplyTheirOdds()
    {
        var lookup = Lookup(Transient("A_01", ("B_01", 50), ("Z_01", 50)),
                            Transient("B_01", ("C_01", 20), ("D_01", 80)),
                            Plain("C_01"), Plain("D_01"), Plain("Z_01"));

        var result = TransientFold.Resolve("A_01", lookup);

        Assert.Equal(10, result.Single(r => r.ItemType == "C_01").Odds);   // 50% * 20%
        Assert.Equal(40, result.Single(r => r.ItemType == "D_01").Odds);   // 50% * 80%
        Assert.Equal(50, result.Single(r => r.ItemType == "Z_01").Odds);
    }

    [Fact]
    public void ATransientRollingBackIntoItself_doesNotLoopForever()
    {
        var lookup = Lookup(Transient("A_01", ("B_01", 100)), Transient("B_01", ("A_01", 100)));

        var result = TransientFold.Resolve("A_01", lookup);

        Assert.NotEmpty(result);
        Assert.True(result.Count < 10);
    }

    [Fact]
    public void ATransientWithNothingToRollInto_isKept_soAMisSetFlagCannotDeleteTheRow()
    {
        var stuck = Plain("Stuck_01");
        stuck.IsTransient = true;

        var result = TransientFold.Resolve("Stuck_01", Lookup(stuck));

        Assert.Equal(new[] { "Stuck_01" }, result.Select(r => r.ItemType));
    }

    [Fact]
    public void ATransient_withASingleDecayTarget_carriesNoOdds()
    {
        var single = Plain("Single_01");
        single.IsTransient = true;
        single.DecayAfterLastCycleItemType = "Next_01";

        var result = TransientFold.Resolve("Single_01", Lookup(single, Plain("Next_01")));

        Assert.Equal(new[] { "Next_01" }, result.Select(r => r.ItemType));
        Assert.Null(result[0].Odds);
    }

    // The chance is glued to the item with &nbsp; -- the Transforms To column is narrow enough
    // that a plain space let the browser break the line between them.
    [Theory]
    [InlineData(null, "{{Item|Page|1}}")]
    [InlineData(100d, "{{Item|Page|1}}")]        // a certainty needs no percentage
    [InlineData(50d, "50%&nbsp;{{Item|Page|1}}")]
    [InlineData(12.5d, "12.5%&nbsp;{{Item|Page|1}}")]
    [InlineData(33.333d, "33.33%&nbsp;{{Item|Page|1}}")]
    public void Format_prefixesTheChanceOnlyWhenItIsNotCertain(double? odds, string expected)
    {
        Assert.Equal(expected, TransientFold.Format("Page", 1, odds));
    }
}
