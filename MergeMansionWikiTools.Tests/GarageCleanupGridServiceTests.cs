using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class GarageCleanupGridServiceTests
{
    private static GarageCleanupGridService MakeService()
    {
        var ds = new DataService(new ChainNameService());
        // GenerateLua/ParseLive/MergeForWrite/Combine don't touch DataService → an empty one is fine.
        return new GarageCleanupGridService(ds, null);
    }

    [Fact]
    public void Smoke() => Assert.True(true);

    // ── GC event group parent (v0.24.25) ──────────────────────────────────

    [Fact]
    public void BuildGcEventGroups_derivesParentFromName()
    {
        var svc = MakeService();
        var airings = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["Legacy Lane Garage Cleanup"] = new()
            {
                new GarageCleanupGridService.GcAiring(
                    new System.DateTime(2026, 7, 26, 8, 0, 0), 3, false, new List<GarageCleanupGridService.GcLevel>())
            },
            // Generic cleanup with no seasonal host → no parent.
            ["Garage Cleanup"] = new()
            {
                new GarageCleanupGridService.GcAiring(
                    new System.DateTime(2026, 1, 1, 8, 0, 0), 3, false, new List<GarageCleanupGridService.GcLevel>())
            },
        };

        var groups = svc.BuildGcEventGroups(airings);

        Assert.Equal("Legacy Lane", groups.Single(g => g.Name == "Legacy Lane Garage Cleanup").Parent);
        Assert.Null(groups.Single(g => g.Name == "Garage Cleanup").Parent);
    }

    // ── Task 1: RewardsBodyLua ────────────────────────────────────────────

    [Fact]
    public void RewardsBodyLua_EmitsGroupsAndSlotFill()
    {
        var lr = new GarageCleanupGridService.LevelRewards();
        var g = new GarageCleanupGridService.RewardGroup { Type = "Lines", Count = 10 };
        g.Rewards.Add(new GarageCleanupGridService.RewardEntry { Chain = "Ingredients", Level = 2, Amount = 1 });
        lr.Groups.Add(g);
        lr.SlotFill.Add(new GarageCleanupGridService.RewardEntry { Currency = "Coins", Amount = 10 });

        var body = GarageCleanupGridService.RewardsBodyLua(lr);

        Assert.Contains("groups = {", body);
        Assert.Contains("{ type = \"Lines\", count = 10, rewards = {{ chain = \"Ingredients\", level = 2, amount = 1 }} }", body);
        Assert.Contains("slotFill = {{ currency = \"Coins\", amount = 10 }}", body);
        Assert.StartsWith("{", body.TrimStart());
        Assert.EndsWith("}", body.TrimEnd());
    }

    // ── Task 2: Combine ───────────────────────────────────────────────────

    [Fact]
    public void Combine_ZipsGridAndRewardsPerLevel()
    {
        var grids = new Dictionary<string, List<GarageCleanupGridService.BoardGrid>>();
        var bg = new GarageCleanupGridService.BoardGrid();
        bg.Chains.Add("Ingredients"); bg.Rows.Add(new List<string> { "1.1" });
        grids["X Garage Cleanup"] = new() { bg };

        var rewards = new Dictionary<string, List<GarageCleanupGridService.LevelRewards>>();
        var lr = new GarageCleanupGridService.LevelRewards();
        lr.SlotFill.Add(new GarageCleanupGridService.RewardEntry { Currency = "Coins", Amount = 10 });
        rewards["X Garage Cleanup"] = new() { lr };

        var combined = GarageCleanupGridService.Combine(grids, rewards);

        var lvl = combined["X Garage Cleanup"][0];
        Assert.Equal(new[] { "Ingredients" }, lvl.Chains);
        Assert.Equal(new[] { "1.1" }, lvl.Rows[0]);
        Assert.NotNull(lvl.RewardsBody);
        Assert.Contains("slotFill", lvl.RewardsBody!);
    }

    [Fact]
    public void Combine_NullRewardsWhenMissing()
    {
        var grids = new Dictionary<string, List<GarageCleanupGridService.BoardGrid>>();
        var bg = new GarageCleanupGridService.BoardGrid(); bg.Chains.Add("A");
        grids["Y"] = new() { bg };
        var combined = GarageCleanupGridService.Combine(grids, new());
        Assert.Null(combined["Y"][0].RewardsBody);
    }

    // ── Task 3: GenerateLua (combined) ────────────────────────────────────

    [Fact]
    public void GenerateLua_EmitsCombinedStructure()
    {
        var svc = MakeService();
        var lvl = new GarageCleanupGridService.GcLevel();
        lvl.Chains.Add("Ingredients"); lvl.Rows.Add(new List<string> { "1.1", "1.2" });
        lvl.RewardsBody = "{ groups = { { type = \"Full\", count = 1, rewards = {{ chain = \"Pastries\", level = 1, amount = 1 }} }, }, slotFill = {{ currency = \"Coins\", amount = 10 }} }";
        var data = new Dictionary<string, List<GarageCleanupGridService.GcLevel>> { ["X GC (2025)"] = new() { lvl } };

        var lua = svc.GenerateLua(data);

        Assert.Contains("p.garageCleanupGrids = {", lua);
        Assert.Contains("[\"X GC (2025)\"] = {", lua);
        Assert.Contains("chains = {", lua);
        Assert.Contains("[1] = {\"1.1\", \"1.2\"},", lua);
        Assert.Contains("rewards = { groups = {", lua);
        Assert.DoesNotContain("garageCleanupRewards", lua);
        Assert.Equal(lua.Count(c => c == '{'), lua.Count(c => c == '}'));  // balanced
    }

    // ── Task 4: ParseLive round-trip (combined, incl. rewards body) ───────

    [Fact]
    public void ParseLive_RoundTripsGenerateLua_IncludingRewardsBody()
    {
        var svc = MakeService();
        var lvl = new GarageCleanupGridService.GcLevel();
        lvl.Chains.Add("Ingredients"); lvl.Chains.Add("Pastries");
        lvl.Rows.Add(new List<string> { "1.1", "2.3" });
        lvl.RewardsBody = "{ groups = { { type = \"Full\", count = 1, rewards = {{ chain = \"Pastries\", level = 1, amount = 1 }} }, }, slotFill = {{ currency = \"Coins\", amount = 10 }} }";
        var lua = "local p = {}\n" + svc.GenerateLua(new() { ["X GC (2025)"] = new() { lvl } }) + "return p\n";

        var parsed = GarageCleanupGridService.ParseLive(lua);

        var p = parsed["X GC (2025)"][0];
        Assert.Equal(new[] { "Ingredients", "Pastries" }, p.Chains);
        Assert.Equal(new[] { "1.1", "2.3" }, p.Rows[0]);
        Assert.NotNull(p.RewardsBody);
        Assert.Contains("slotFill", p.RewardsBody!);
    }

    [Fact]
    public void GenerateSectionWikitext_RoundN_AndRunsInvoke()
    {
        var baseName = "Legacy Lane Garage Cleanup";
        var variants = new List<GarageCleanupGridService.GcSectionVariant>
        {
            new() { Name = baseName + " (2024) Round 1", LevelCount = 4 },
            new() { Name = baseName + " (2024) Round 2", LevelCount = 4 },
            new() { Name = baseName + " (2025)", LevelCount = 4 },
            new() { Name = baseName + " (2026)", LevelCount = 4 },
        };
        var wt = GarageCleanupGridService.GenerateSectionWikitext(baseName, variants);

        Assert.Contains("== Garage Cleanup ==", wt);
        Assert.Contains("{{Infobox Garage Cleanup|Legacy Lane Garage Cleanup (2026)}}", wt);   // newest airing
        Assert.Contains("{{#Invoke:Various|GenerateGarageCleanupRuns|name=Legacy Lane Garage Cleanup}}", wt);
        Assert.Contains("=== 2026 ===", wt);
        Assert.Contains("=== 2024 ===", wt);
        Assert.Contains("==== Round 1 ====", wt);
        Assert.Contains("==== Round 2 ====", wt);
        Assert.Contains("name=Legacy Lane Garage Cleanup (2024) Round 1|level=1", wt);
        Assert.Contains("name=Legacy Lane Garage Cleanup (2026)|level=1", wt);
        Assert.Contains("mw-collapsed", wt);    // older airings collapsed
        // newest section (2026) appears before older (2024) — descending
        Assert.True(wt.IndexOf("=== 2026 ===", System.StringComparison.Ordinal) < wt.IndexOf("=== 2024 ===", System.StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateSectionWikitext_SinglePlain_NoHeadersOrYear()
    {
        var baseName = "Murder at the Mansion Garage Cleanup";
        var variants = new List<GarageCleanupGridService.GcSectionVariant> { new() { Name = baseName, LevelCount = 4 } };
        var wt = GarageCleanupGridService.GenerateSectionWikitext(baseName, variants);
        Assert.DoesNotContain("=== ", wt);                       // no year/round headings
        Assert.Contains("{{Infobox Garage Cleanup}}", wt);       // plain infobox (no name param)
        Assert.Contains("name=Murder at the Mansion Garage Cleanup|level=1", wt);
        Assert.Contains("{{#Invoke:Various|GenerateGarageCleanupRuns|name=Murder at the Mansion Garage Cleanup}}", wt);
    }

    [Fact]
    public void GenerateSectionWikitext_SameYearMonths_UsesMonthYearHeaders()
    {
        var baseName = "Circus Caper Garage Cleanup";
        var variants = new List<GarageCleanupGridService.GcSectionVariant>
        {
            new() { Name = baseName + " (March 2025)", LevelCount = 4 },
            new() { Name = baseName + " (November 2025)", LevelCount = 4 },
        };
        var wt = GarageCleanupGridService.GenerateSectionWikitext(baseName, variants);
        Assert.Contains("=== March 2025 ===", wt);
        Assert.Contains("=== November 2025 ===", wt);
        Assert.Contains("name=Circus Caper Garage Cleanup (November 2025)|level=1", wt);
    }

    [Fact]
    public void FirstDifference_DetectsRewardChange_SameGrid()
    {
        GarageCleanupGridService.GcLevel L(string body)
        {
            var l = new GarageCleanupGridService.GcLevel();
            l.Chains.Add("A"); l.Rows.Add(new() { "1.1" }); l.RewardsBody = body;
            return l;
        }
        var a = new List<GarageCleanupGridService.GcLevel> { L("{ groups = { { type = \"Full\", count = 1, rewards = {{ chain = \"X\", level = 1, amount = 1 }} }, }, slotFill = {{ currency = \"Coins\", amount = 10 }} }") };
        var b = new List<GarageCleanupGridService.GcLevel> { L("{ groups = { { type = \"Full\", count = 1, rewards = {{ chain = \"X\", level = 2, amount = 1 }} }, }, slotFill = {{ currency = \"Coins\", amount = 10 }} }") };

        Assert.Null(GarageCleanupGridService.FirstDifferenceForTest(a, a));    // identical incl rewards
        Assert.NotNull(GarageCleanupGridService.FirstDifferenceForTest(a, b)); // reward differs → not identical
    }

    [Fact]
    public void BuildEventRuns_IdenticalReair_PointsToHolderDate()
    {
        GarageCleanupGridService.GcLevel L(string chain)
        {
            var l = new GarageCleanupGridService.GcLevel(); l.Chains.Add(chain); l.Rows.Add(new() { "1.1" });
            l.RewardsBody = "{ groups = {  }, slotFill = {{ currency = \"Coins\", amount = 10 }} }";
            return l;
        }
        var gridA = new List<GarageCleanupGridService.GcLevel> { L("A") };
        var gridB = new List<GarageCleanupGridService.GcLevel> { L("B") };
        var airings = new List<(System.DateTime, List<GarageCleanupGridService.GcLevel>)>
        {
            (System.DateTime.Parse("2025-08-03"), gridA),
            (System.DateTime.Parse("2026-05-31"), gridA),
            (System.DateTime.Parse("2027-06-10"), gridB),
        };
        var runs = GarageCleanupGridService.BuildEventRuns(airings);
        Assert.Null(runs[0].IdenticalTo);
        Assert.Equal("03.08.2025", runs[1].IdenticalTo);
        Assert.Null(runs[2].IdenticalTo);
    }

    [Theory]
    [InlineData("Legacy Lane Garage Cleanup", "Legacy Lane Garage Cleanup")]
    [InlineData("Legacy Lane Garage Cleanup (2024)", "Legacy Lane Garage Cleanup")]
    [InlineData("Legacy Lane Garage Cleanup (2024) Round 1", "Legacy Lane Garage Cleanup")]
    [InlineData("X Garage Cleanup (March 2025)", "X Garage Cleanup")]
    [InlineData("X Garage Cleanup Round 2", "X Garage Cleanup")]
    public void StripVariantSuffix_RemovesYearAndRound(string key, string expected)
        => Assert.Equal(expected, GarageCleanupGridService.StripVariantSuffix(key));

    [Fact]
    public void ParseEventRuns_ReadsStartsAndDurations()
    {
        var lua = "return {\n events = {\n" +
            "{ name = \"Legacy Lane\", runs = {\n" +
            "  { start = { year = 2024, month = 7, day = 2, hour = 8 }, durationDays = 13 },\n" +
            "  { start = { year = 2026, month = 5, day = 29, hour = 8 }, durationDays = 5 },\n" +
            "} },\n },\n}\n";
        var runs = GarageCleanupGridService.ParseEventRuns(lua);
        Assert.Equal(2, runs["Legacy Lane"].Count);
        Assert.Equal(new System.DateTime(2024, 7, 2, 8, 0, 0), runs["Legacy Lane"][0].Start);
        Assert.Equal(13, runs["Legacy Lane"][0].DurationDays);
    }

    // Helper: a one-level GcLevel grid keyed on a chain name, with a fixed reward body.
    private static List<GarageCleanupGridService.GcLevel> Grid(string chain)
    {
        var l = new GarageCleanupGridService.GcLevel();
        l.Chains.Add(chain); l.Rows.Add(new() { "1.1" });
        l.RewardsBody = "{ groups = {  }, slotFill = {{ currency = \"Coins\", amount = 10 }} }";
        return new List<GarageCleanupGridService.GcLevel> { l };
    }

    [Fact]
    public void MergeAirings_AddOnly_AddsNewParentRun_KeepsExisting()
    {
        var svc = MakeService();
        var existing = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["X Garage Cleanup"] = new()
            {
                new(System.DateTime.Parse("2024-07-07"), 13, false, Grid("g24")),
                new(System.DateTime.Parse("2025-08-03"), 5, false, Grid("g25")),
            }
        };
        var active = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["X Garage Cleanup"] = new()
            {
                new(System.DateTime.Parse("2026-05-31"), 5, false, Grid("g26")),         // NEW airing
                new(System.DateTime.Parse("2025-08-03"), 5, false, Grid("g25changed")),  // existing → must NOT override
            }
        };
        var events = "return { events = { { name = \"X\", runs = {" +
            "{ start = { year = 2024, month = 7, day = 2 }, durationDays = 13 }," +
            "{ start = { year = 2025, month = 8, day = 1 }, durationDays = 5 }," +
            "{ start = { year = 2026, month = 5, day = 29 }, durationDays = 5 }," +
            "} } } }";

        var merged = svc.MergeAirings(existing, active, events);
        var m = merged["X Garage Cleanup"];

        Assert.Equal(3, m.Count);                                              // 2024 + 2025 + new 2026
        Assert.Contains(m, x => x.Start == System.DateTime.Parse("2026-05-31"));
        var a25 = m.First(x => x.Start == System.DateTime.Parse("2025-08-03"));
        Assert.Equal("g25", a25.Grid[0].Chains[0]);                           // kept original, NOT overridden
    }

    [Fact]
    public void ReconstructAirings_RoundTripsComposeAndEventGroups()
    {
        var svc = MakeService();
        var r1 = Grid("R1");
        var r2 = Grid("R2");
        var g25 = Grid("G25");
        var byBase = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["Legacy Lane Garage Cleanup"] = new()
            {
                new(System.DateTime.Parse("2024-07-07"), 13, false, r1),
                new(System.DateTime.Parse("2024-07-12"), 13, false, r2),
                new(System.DateTime.Parse("2025-08-03"), 5, false, g25),
                new(System.DateTime.Parse("2026-05-31"), 5, false, g25),   // identical to 2025
            }
        };

        // Various holds only the grids block; the Events module holds the run history (A3/A4).
        var various = svc.ComposeGarageCleanup(byBase);
        var eventsLua = new LuaGeneratorService().GenerateEventScheduleLua(svc.BuildGcEventGroups(byBase), null);

        var recon = GarageCleanupGridService.ReconstructAirings(various, eventsLua);

        var a = recon["Legacy Lane Garage Cleanup"];
        Assert.Equal(4, a.Count);
        // 2026 (identical re-air) reconstructed with the 2025 holder's grid
        var a26 = a.First(x => x.Start == System.DateTime.Parse("2026-05-31"));
        var a25 = a.First(x => x.Start == System.DateTime.Parse("2025-08-03"));
        Assert.Null(GarageCleanupGridService.FirstDifferenceForTest(a26.Grid, a25.Grid));
        // duration + disabled survive the round-trip (A3a)
        Assert.Equal(5, a25.DurationDays);
        Assert.False(a25.Disabled);

        // strongest check: compose → reconstruct → compose is byte-stable (faithful inverse)
        var various2 = svc.ComposeGarageCleanup(recon);
        Assert.Equal(various, various2);
    }

    [Fact]
    public void ComposeGarageCleanup_LegacyLane_YearRound_GridsOnly_NoRuns()
    {
        var svc = MakeService();
        var r1 = Grid("Round1");
        var r2 = Grid("Round2");
        var g25 = Grid("Grid2025");
        var byBase = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["Legacy Lane Garage Cleanup"] = new()
            {
                new(System.DateTime.Parse("2024-07-07"), 13, false, r1),    // round 1
                new(System.DateTime.Parse("2024-07-12"), 13, false, r2),    // round 2 (same airing)
                new(System.DateTime.Parse("2025-08-03"), 5, false, g25),    // 2025 airing
                new(System.DateTime.Parse("2026-05-31"), 5, false, g25),    // 2026 re-air, grid identical to 2025
            }
        };

        var lua = svc.ComposeGarageCleanup(byBase);

        Assert.Contains("[\"Legacy Lane Garage Cleanup (2024) Round 1\"]", lua);
        Assert.Contains("[\"Legacy Lane Garage Cleanup (2024) Round 2\"]", lua);
        Assert.Contains("[\"Legacy Lane Garage Cleanup (2025)\"]", lua);
        Assert.DoesNotContain("[\"Legacy Lane Garage Cleanup (2026)\"] = {", lua);  // identical → no grid stored
        Assert.DoesNotContain("garageCleanupRuns", lua);                            // A4: run data lives in Events now
        Assert.DoesNotContain("identicalTo", lua);                                  // identicalTo moved to Events runs
        Assert.Equal(lua.Count(c => c == '{'), lua.Count(c => c == '}'));
    }

    // ── A3c: BuildGcEventGroups (run history → Events module) ──────────────

    [Fact]
    public void BuildGcEventGroups_IdenticalReair_SecondRunPointsToFirst_DisabledAndDuration()
    {
        var svc = MakeService();
        var gridA = Grid("A");
        var byBase = new Dictionary<string, List<GarageCleanupGridService.GcAiring>>
        {
            ["X Garage Cleanup"] = new()
            {
                new(System.DateTime.Parse("2025-08-03"), 5, true, gridA),         // disabled holder
                new(System.DateTime.Parse("2026-05-31"), 7, false, gridA),        // identical grid → identicalTo holder
            }
        };

        var groups = svc.BuildGcEventGroups(byBase);
        var g = Assert.Single(groups);
        Assert.Equal("X Garage Cleanup", g.Name);
        Assert.Equal("Garage Cleanup", g.Category);
        Assert.Equal("X", g.Parent);   // parent recovered from the name (v0.24.25 nesting fix)

        // runs are in chronological order (BuildGcEventGroups sorts ascending)
        var first = g.Runs.First(r => r.Start == System.DateTime.Parse("2025-08-03"));
        var second = g.Runs.First(r => r.Start == System.DateTime.Parse("2026-05-31"));
        Assert.Null(first.IdenticalTo);                          // grid holder → no identicalTo
        Assert.Equal("03.08.2025", second.IdenticalTo);          // re-air → first run's date
        Assert.True(first.Disabled);
        Assert.False(second.Disabled);
        Assert.Equal(System.TimeSpan.FromDays(5), first.Duration);
        Assert.Equal(System.TimeSpan.FromDays(7), second.Duration);
    }

    [Fact]
    public void ParseGcRunsFromEvents_ReadsOnlyGarageCleanupCategory()
    {
        var lua = "return { events = {\n" +
            "{ name = \"X Garage Cleanup\", category = \"Garage Cleanup\", runs = {\n" +
            "  { start = { year = 2025, month = 8, day = 3, hour = 8 }, durationDays = 5 },\n" +
            "  { start = { year = 2026, month = 5, day = 31 }, durationDays = 7, disabled = true, identicalTo = \"03.08.2025\" },\n" +
            "} },\n" +
            "{ name = \"Some Seasonal\", category = \"Seasonal Event\", runs = {\n" +
            "  { start = { year = 2025, month = 1, day = 1 }, durationDays = 10 },\n" +
            "} },\n" +
            "} }\n";
        var runs = GarageCleanupGridService.ParseGcRunsFromEvents(lua);
        Assert.True(runs.ContainsKey("X Garage Cleanup"));
        Assert.False(runs.ContainsKey("Some Seasonal"));        // non-GC category excluded
        var x = runs["X Garage Cleanup"];
        Assert.Equal(2, x.Count);
        Assert.Equal(new System.DateTime(2025, 8, 3, 8, 0, 0), x[0].Start);
        Assert.Equal(5, x[0].DurationDays);
        Assert.False(x[0].Disabled);
        Assert.True(x[1].Disabled);
        Assert.Equal(new System.DateTime(2025, 8, 3), x[1].IdenticalTo);
    }

    [Fact]
    public void GenerateLua_RewardsIndent_StableAcrossRoundTrips()
    {
        var svc = MakeService();
        var lr = new GarageCleanupGridService.LevelRewards();
        var g = new GarageCleanupGridService.RewardGroup { Type = "Lines", Count = 10 };
        g.Rewards.Add(new GarageCleanupGridService.RewardEntry { Chain = "X", Level = 1, Amount = 1 });
        lr.Groups.Add(g);
        lr.SlotFill.Add(new GarageCleanupGridService.RewardEntry { Currency = "Coins", Amount = 10 });

        var lvl = new GarageCleanupGridService.GcLevel();
        lvl.Chains.Add("A"); lvl.Rows.Add(new() { "1.1" });
        lvl.RewardsBody = GarageCleanupGridService.RewardsBodyLua(lr);
        var data = new Dictionary<string, List<GarageCleanupGridService.GcLevel>> { ["X GC"] = new() { lvl } };

        var lua1 = svc.GenerateLua(data);
        var lua2 = svc.GenerateLua(GarageCleanupGridService.ParseLive("local p = {}\n" + lua1 + "return p\n"));
        Assert.Equal(lua1, lua2);   // indentation must not accrete across parse→emit cycles
    }

    // ── Task 5: MergeForWrite on GcLevel ──────────────────────────────────

    [Fact]
    public void MergeForWrite_KeepsExistingAndAddsNew_WithRewards()
    {
        var svc = MakeService();
        var existingLvl = new GarageCleanupGridService.GcLevel();
        existingLvl.Chains.Add("A"); existingLvl.Rows.Add(new() { "1.1" });
        existingLvl.RewardsBody = "{ groups = {  }, slotFill = {{ currency = \"Coins\", amount = 10 }} }";
        var live = "local p = {}\n" + svc.GenerateLua(new() { ["Other GC"] = new() { existingLvl } }) + "return p\n";

        var newLvl = new GarageCleanupGridService.GcLevel();
        newLvl.Chains.Add("B"); newLvl.Rows.Add(new() { "1.1" });
        newLvl.RewardsBody = "{ groups = {  }, slotFill = {{ currency = \"Coins\", amount = 10 }} }";
        var gen = new Dictionary<string, List<GarageCleanupGridService.GcLevel>> { ["New GC"] = new() { newLvl } };
        var changes = new List<GarageCleanupGridService.GridChange>
            { new() { EventName = "New GC", Action = GarageCleanupGridService.GridAction.New } };

        var block = svc.MergeForWrite(gen, live, changes);

        Assert.Contains("[\"Other GC\"]", block);   // existing preserved
        Assert.Contains("[\"New GC\"]", block);      // new added
        Assert.Contains("rewards = {", block);
        Assert.DoesNotContain("garageCleanupRewards", block);
    }
}
