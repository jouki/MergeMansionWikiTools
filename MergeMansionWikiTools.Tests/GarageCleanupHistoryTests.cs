using System;
using System.Collections.Generic;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class GarageCleanupHistoryTests
{
    private static GarageCleanupHistory.GcVariant V(string date, int round = 0)
        => new(DateTime.Parse(date), round);

    [Fact]
    public void DeriveVariantNames_SingleRun_Plain()
    {
        var names = GarageCleanupHistory.DeriveVariantNames("X Garage Cleanup", new[] { V("2025-04-05") });
        Assert.Equal("X Garage Cleanup", names[V("2025-04-05")]);
    }

    [Fact]
    public void DeriveVariantNames_DistinctYears_YearSuffix()
    {
        var vs = new[] { V("2024-07-07"), V("2025-08-03") };
        var names = GarageCleanupHistory.DeriveVariantNames("X Garage Cleanup", vs);
        Assert.Equal("X Garage Cleanup (2024)", names[vs[0]]);
        Assert.Equal("X Garage Cleanup (2025)", names[vs[1]]);
    }

    [Fact]
    public void DeriveVariantNames_SameYearTwice_MonthYear()
    {
        var vs = new[] { V("2025-03-10"), V("2025-11-20") };
        var names = GarageCleanupHistory.DeriveVariantNames("X Garage Cleanup", vs);
        Assert.Equal("X Garage Cleanup (March 2025)", names[vs[0]]);
        Assert.Equal("X Garage Cleanup (November 2025)", names[vs[1]]);
    }

    [Fact]
    public void DeriveVariantNames_SameYearReairIdentical_DoesNotForceMonthYear()
    {
        // Legacy Lane: 2024, 2025, May-2026 holder + June-2026 identical re-air. The identical re-air must
        // NOT count as a second 2026 airing → the holder stays "(2026)", not "(May 2026)".
        var vs = new[] { V("2024-07-07"), V("2025-08-03"), V("2026-05-31"), V("2026-06-27") };
        var reair = new HashSet<DateTime> { DateTime.Parse("2026-06-27") };
        var names = GarageCleanupHistory.DeriveVariantNames("Legacy Lane Garage Cleanup", vs, reair);
        Assert.Equal("Legacy Lane Garage Cleanup (2024)", names[vs[0]]);
        Assert.Equal("Legacy Lane Garage Cleanup (2025)", names[vs[1]]);
        Assert.Equal("Legacy Lane Garage Cleanup (2026)", names[vs[2]]);   // holder keeps plain year
        Assert.Equal("Legacy Lane Garage Cleanup (2026)", names[vs[3]]);   // re-air mirrors holder, not "(June 2026)"
    }

    [Fact]
    public void DeriveVariantNames_SameYearDistinctGrids_StillMonthYear()
    {
        // Two DISTINCT 2026 grids (neither is a re-air) → legitimate Month-Year disambiguation is kept.
        var vs = new[] { V("2026-05-31"), V("2026-11-20") };
        var names = GarageCleanupHistory.DeriveVariantNames("X Garage Cleanup", vs, new HashSet<DateTime>());
        Assert.Equal("X Garage Cleanup (May 2026)", names[vs[0]]);
        Assert.Equal("X Garage Cleanup (November 2026)", names[vs[1]]);
    }

    [Fact]
    public void DeriveVariantNames_OnlyYearWithIdenticalReair_StaysPlain()
    {
        // Sole airing (May 2026) + identical June re-air → only one distinct grid ever → plain, no suffix.
        var vs = new[] { V("2026-05-31"), V("2026-06-27") };
        var reair = new HashSet<DateTime> { DateTime.Parse("2026-06-27") };
        var names = GarageCleanupHistory.DeriveVariantNames("X Garage Cleanup", vs, reair);
        Assert.Equal("X Garage Cleanup", names[vs[0]]);
        Assert.Equal("X Garage Cleanup", names[vs[1]]);
    }

    [Fact]
    public void DeriveVariantNames_MultiRound_AppendsRoundN()
    {
        var vs = new[] { V("2024-07-07", 1), V("2024-07-12", 2) };
        var names = GarageCleanupHistory.DeriveVariantNames("X Garage Cleanup", vs);
        Assert.Equal("X Garage Cleanup Round 1", names[vs[0]]);
        Assert.Equal("X Garage Cleanup Round 2", names[vs[1]]);
    }

    [Fact]
    public void GroupRounds_SameParentCloseStarts_AreRounds()
    {
        var gcs = new[]
        {
            new GarageCleanupHistory.GcRoundInput("GC_A2024",    "CBE_A", DateTime.Parse("2024-07-07")),
            new GarageCleanupHistory.GcRoundInput("GC_A2024_01", "CBE_A", DateTime.Parse("2024-07-12")),
            new GarageCleanupHistory.GcRoundInput("GC_B2025",    "CBE_B", DateTime.Parse("2025-01-01")),
        };
        var r = GarageCleanupHistory.GroupRounds(gcs);
        Assert.Equal(1, r["GC_A2024"]);
        Assert.Equal(2, r["GC_A2024_01"]);
        Assert.Equal(0, r["GC_B2025"]);
    }

    [Fact]
    public void GroupRounds_SameParentFarApart_AreSeparateAirings()
    {
        var gcs = new[]
        {
            new GarageCleanupHistory.GcRoundInput("GC_A2024", "CBE_A", DateTime.Parse("2024-07-07")),
            new GarageCleanupHistory.GcRoundInput("GC_A2025", "CBE_A", DateTime.Parse("2025-08-03")),
        };
        var r = GarageCleanupHistory.GroupRounds(gcs);
        Assert.Equal(0, r["GC_A2024"]);
        Assert.Equal(0, r["GC_A2025"]);
    }

    [Fact]
    public void MatchParentRun_GcInsideWindow_PicksThatRun()
    {
        var runs = new List<(DateTime, double)>
        {
            (DateTime.Parse("2024-07-02T08:00"), 13),   // 2024 airing window covers both GC rounds
            (DateTime.Parse("2026-05-29T08:00"), 5),
        };
        Assert.Equal(2024, GarageCleanupHistory.MatchParentRun(DateTime.Parse("2024-07-07T12:00"), runs)!.Value.Start.Year);
        Assert.Equal(2024, GarageCleanupHistory.MatchParentRun(DateTime.Parse("2024-07-12T12:00"), runs)!.Value.Start.Year);
        Assert.Equal(2026, GarageCleanupHistory.MatchParentRun(DateTime.Parse("2026-05-31T08:00"), runs)!.Value.Start.Year);
    }

    private static GarageCleanupHistory.DumpObservation O(string at, bool en) => new(DateTime.Parse(at), en);

    [Fact]
    public void DecideAir_LastPreE_Enabled_Aired_NotDisabled()
    {
        var v = GarageCleanupHistory.DecideAir(DateTime.Parse("2025-04-05"), 3,
            new[] { O("2025-03-01", false), O("2025-04-04", true) });
        Assert.True(v.Aired); Assert.False(v.Disabled);
        Assert.Equal(DateTime.Parse("2025-04-04"), v.TrustedContentAt);
    }

    [Fact]
    public void DecideAir_LastPreE_Disabled_NotAired()
    {
        var v = GarageCleanupHistory.DecideAir(DateTime.Parse("2025-04-05"), 3, new[] { O("2025-04-04", false) });
        Assert.False(v.Aired); Assert.True(v.Disabled);
    }

    [Fact]
    public void DecideAir_PostEEnabledWithinWindow_RescuesContent_NotAirStatus()
    {
        var v = GarageCleanupHistory.DecideAir(DateTime.Parse("2025-04-05"), 3, new[] { O("2025-04-20", true) });
        Assert.True(v.Aired);
        Assert.Equal(DateTime.Parse("2025-04-20"), v.TrustedContentAt);
    }

    [Fact]
    public void DecideAir_PostEDisabled_DoesNotFlipAiredFromPreE()
    {
        var v = GarageCleanupHistory.DecideAir(DateTime.Parse("2025-04-05"), 3,
            new[] { O("2025-04-04", true), O("2025-09-01", false) });
        Assert.True(v.Aired); Assert.False(v.Disabled);
        Assert.Equal(DateTime.Parse("2025-04-04"), v.TrustedContentAt);
    }
}
