using System;
using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>Tests for the orphan-cleanup step in <see cref="GarageCleanupGridService.MergeAirings"/>:
/// a merged GC airing whose start is not bracketed by any parent run window is dropped (orphaned).
/// Guard: if the parent has zero runs in the events lua, all airings for that base are kept.</summary>
public class MergeAiringsOrphanTests
{
    private static GarageCleanupGridService MakeService()
    {
        var ds = new DataService(new ChainNameService());
        return new GarageCleanupGridService(ds, null);
    }

    // Minimal one-level grid helper (mirrors GarageCleanupGridServiceTests.Grid).
    private static List<GarageCleanupGridService.GcLevel> Grid(string chain)
    {
        var l = new GarageCleanupGridService.GcLevel();
        l.Chains.Add(chain);
        l.Rows.Add(new List<string> { "1.1" });
        l.RewardsBody = "{ groups = {  }, slotFill = {{ currency = \"Coins\", amount = 10 }} }";
        return new List<GarageCleanupGridService.GcLevel> { l };
    }

    // ── Test 1: Orphan dropped ────────────────────────────────────────────────────────────────

    /// <summary>
    /// existing "Legacy Lane Garage Cleanup" has 3 airings: 2024-07-12, 2025-08-03, 2026-05-31.
    /// Events lua "Legacy Lane" has runs: 2024-07-02 (dur 13), 2025-08-01 (dur 5), 2026-06-25 (dur 5).
    /// Note: NO 2026-05-29 run (it was superseded); 2026-05-31 is NOT bracketed by any present run.
    /// active has the 2026-06-25 GC airing.
    /// Expected result: 2024-07-12, 2025-08-03, 2026-06-25 KEPT; 2026-05-31 DROPPED.
    /// </summary>
    [Fact]
    public void MergeAiringsOrphan_OrphanedAiringDropped()
    {
        var svc = MakeService();

        var existing = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["Legacy Lane Garage Cleanup"] = new()
            {
                new(new DateTime(2024, 7, 12), 13, false, Grid("g24")),
                new(new DateTime(2025, 8,  3), 5,  false, Grid("g25")),
                new(new DateTime(2026, 5, 31), 5,  false, Grid("g26old")), // superseded → orphan
            }
        };

        var active = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["Legacy Lane Garage Cleanup"] = new()
            {
                new(new DateTime(2026, 6, 25), 5, false, Grid("g26new")), // new airing from current run
            }
        };

        // Legacy Lane runs: 2024-07-02 (dur 13), 2025-08-01 (dur 5), 2026-06-25 (dur 5).
        // No 2026-05-29 run — it was superseded.
        var events = "return { events = { { name = \"Legacy Lane\", runs = {" +
            "{ start = { year = 2024, month = 7, day = 2, hour = 8 }, durationDays = 13 }," +
            "{ start = { year = 2025, month = 8, day = 1, hour = 8 }, durationDays = 5 }," +
            "{ start = { year = 2026, month = 6, day = 25, hour = 8 }, durationDays = 5 }," +
            "} } } }";

        var merged = svc.MergeAirings(existing, active, events);
        var m = merged["Legacy Lane Garage Cleanup"];

        // 2024-07-12 is inside 2024-07-02 + 13d (ends 07-15) + 1d tolerance → kept
        Assert.Contains(m, x => x.Start == new DateTime(2024, 7, 12));

        // 2025-08-03 is inside 2025-08-01 + 5d (ends 08-06) + 1d tolerance → kept
        Assert.Contains(m, x => x.Start == new DateTime(2025, 8, 3));

        // 2026-06-25 was added by active (from 2026-06-25 run) → kept
        Assert.Contains(m, x => x.Start == new DateTime(2026, 6, 25));

        // 2026-05-31 is NOT bracketed by any of the three present runs → DROPPED
        Assert.DoesNotContain(m, x => x.Start == new DateTime(2026, 5, 31));

        Assert.Equal(3, m.Count);
    }

    // ── Test 2: No parent runs → all airings dropped ────────────────────────────────────────────

    /// <summary>
    /// A base name whose parent event has zero runs in the events lua has all its airings dropped
    /// as pointless orphans (the parent event simply does not exist in the current data).
    /// </summary>
    [Fact]
    public void MergeAiringsOrphan_NoParentRuns_AllAiringsDropped()
    {
        var svc = MakeService();

        var existing = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["Mystery Garden Garage Cleanup"] = new()
            {
                new(new DateTime(2024, 3, 10), 7, false, Grid("g1")),
                new(new DateTime(2025, 4, 15), 7, false, Grid("g2")),
            }
        };

        // No active airings for this GC.
        var active = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>();

        // Events lua does NOT contain "Mystery Garden" — so zero parent runs for it.
        var events = "return { events = { { name = \"Some Other Event\", runs = {" +
            "{ start = { year = 2024, month = 3, day = 1 }, durationDays = 10 }," +
            "} } } }";

        var merged = svc.MergeAirings(existing, active, events);

        // No parent runs → all airings are dropped. The key should be present but map to an empty list,
        // or be absent from the dict entirely (depending on how the merge dict is built).
        if (merged.ContainsKey("Mystery Garden Garage Cleanup"))
        {
            // Key exists but should be empty (no airings).
            Assert.Empty(merged["Mystery Garden Garage Cleanup"]);
        }
        else
        {
            // Key absent is also valid (no rounds to add in the add step, pruned to zero, not in final dict).
            Assert.True(!merged.ContainsKey("Mystery Garden Garage Cleanup"));
        }
    }

    // ── Test 3: No false drop — all airings bracketed → nothing dropped ──────────────────────

    /// <summary>
    /// When every merged airing is bracketed by a present parent run the result must be identical
    /// to the existing add-only behavior (nothing dropped).
    /// </summary>
    [Fact]
    public void MergeAiringsOrphan_AllBracketed_NothingDropped()
    {
        var svc = MakeService();

        var existing = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["Green Acres Garage Cleanup"] = new()
            {
                new(new DateTime(2024, 9, 5),  14, false, Grid("g24")),
                new(new DateTime(2025, 9, 10), 14, false, Grid("g25")),
            }
        };

        var active = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["Green Acres Garage Cleanup"] = new()
            {
                new(new DateTime(2026, 9, 8), 14, false, Grid("g26")), // new airing
            }
        };

        // "Green Acres" parent has runs for all three years.
        var events = "return { events = { { name = \"Green Acres\", runs = {" +
            "{ start = { year = 2024, month = 9, day = 1 }, durationDays = 14 }," +
            "{ start = { year = 2025, month = 9, day = 8 }, durationDays = 14 }," +
            "{ start = { year = 2026, month = 9, day = 5 }, durationDays = 14 }," +
            "} } } }";

        var merged = svc.MergeAirings(existing, active, events);
        var m = merged["Green Acres Garage Cleanup"];

        // All three airings are bracketed → kept + new one added.
        Assert.Equal(3, m.Count);
        Assert.Contains(m, x => x.Start == new DateTime(2024, 9, 5));
        Assert.Contains(m, x => x.Start == new DateTime(2025, 9, 10));
        Assert.Contains(m, x => x.Start == new DateTime(2026, 9, 8));
    }
}
