using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for <see cref="DailyScoopDatatableService"/> — the Module:Datatable/DailyScoop renderer
/// that replaced the <c>tools/daily_scoop_datatable_gen.py</c> bridge (v0.24.68). The renderer is
/// pure (daily_scoop.json in, Lua out), so everything here is fixture-driven; the extractor that
/// fills that JSON needs a real config archive and is verified separately, by regenerating the
/// pre-<c>_v2</c> week set and byte-comparing it against the published module.
/// <para>
/// The shape asserted here is a contract with Module:DailyScoop: segment order is positional,
/// values that agree across segments collapse to a scalar, and only the differing ones expand.
/// </para>
/// </summary>
public class DailyScoopDatatableTests
{
    private static readonly string[] SegKeys = { "l51", "l15", "l26", "l46", "lt5" };

    /// <summary>
    /// A one-week, one-day fixture. <paramref name="goalsPerSegment"/> is the task's target in
    /// segment order, so a test can make the segments agree or disagree at will.
    /// </summary>
    private static DailyScoopDump Fixture(
        Action<DailyScoopDump.TaskEntry, int>? tweak = null,
        int[]? goalsPerSegment = null,
        string revision = "_v2")
    {
        var goals = goalsPerSegment ?? new[] { 150, 150, 150, 150, 150 };
        var dump = new DailyScoopDump { Revision = revision, SelectedFrom = "DailyChallenges_21 (HardWeek_v2)" };
        foreach (var k in SegKeys)
            dump.Segments.Add(new DailyScoopDump.SegmentEntry { Key = k, Id = SegId(k) });

        var week = new DailyScoopDump.WeekEntry { Type = "HardWeek", Key = "Hard", Name = "Hard Week" };
        for (var s = 0; s < SegKeys.Length; s++)
        {
            // A full seven-day week: a short one is an anomaly the renderer reports, and these tests
            // assert that a clean fixture produces no warnings.
            week.Days[SegKeys[s]] = new List<string>();
            for (var d = 1; d <= 7; d++)
            {
                var dayId = $"{SegId(SegKeys[s])}_HardWeek_Day{d}{revision}";
                var taskId = $"{SegId(SegKeys[s])}_HardWeek_Day{d}_Task1{revision}";
                week.Days[SegKeys[s]].Add(dayId);
                dump.Days[dayId] = new DailyScoopDump.DayEntry
                {
                    ReqCompleted = 10,
                    Reward = "daily",
                    Std = new List<string> { taskId },
                };
                var task = new DailyScoopDump.TaskEntry
                {
                    Type = "Merge",
                    Req = goals[s],
                    Prio = 1,
                    Loc = "DailyChallenges_Task_Merge_Description",
                    Points = 5,
                    Reward = new DailyScoopDump.RewardEntry { Id = "Coins", Type = "Currency", Aux = "Garage", Amount = 50 },
                };
                tweak?.Invoke(task, s);
                dump.Tasks[taskId] = task;
            }
        }
        dump.Weeks.Add(week);
        return dump;
    }

    private static string SegId(string key) => key switch
    {
        "l51" => "PlayerLevels51above",
        "l15" => "PlayerLevels15to25",
        "l26" => "PlayerLevels26to45",
        "l46" => "PlayerLevels46to50",
        _ => "LT500",
    };

    private static string Render(DailyScoopDump dump, out DailyScoopDatatableService svc)
    {
        svc = new DailyScoopDatatableService();
        return svc.Build(dump, "");
    }

    [Fact]
    public void SegmentHeader_keepsThePositionalOrderModuleDailyScoopIndexesOn()
    {
        var lua = Render(Fixture(), out _);
        Assert.Contains("segments = { \"l51\", \"l15\", \"l26\", \"l46\", \"lt5\" },", lua);
        Assert.Contains("segmentIds = { l51 = \"PlayerLevels51above\", l15 = \"PlayerLevels15to25\", "
            + "l26 = \"PlayerLevels26to45\", l46 = \"PlayerLevels46to50\", lt5 = \"LT500\" },", lua);
    }

    [Fact]
    public void GoalEqualAcrossSegments_collapsesToAScalar()
    {
        var lua = Render(Fixture(), out var svc);
        Assert.Contains("{ type = \"Merge\", goals = 150, points = 5, reward = {\"coins\", 50} },", lua);
        Assert.Equal(7, svc.TaskCount);
    }

    [Fact]
    public void GoalDifferingPerSegment_expandsInSegmentOrder()
    {
        // The real Hard Week Day 3 slot 1 of the _v2 set: 150 for L51+/L26-45/LT500, 125 for the rest.
        var lua = Render(Fixture(goalsPerSegment: new[] { 150, 125, 150, 125, 150 }), out _);
        Assert.Contains("goals = {150, 125, 150, 125, 150}", lua);
    }

    [Fact]
    public void FallbackIds_areQuoted()
    {
        var lua = Render(Fixture((t, _) => t.Fallbacks.Add("Fallback_Merge_05_v2")), out _);
        Assert.Contains("fallbacks = {\"Fallback_Merge_05_v2\"}", lua);
    }

    [Fact]
    public void FallbacksDifferingPerSegment_becomeAPerSegmentMap()
    {
        var lua = Render(Fixture((t, s) => t.Fallbacks.Add(s == 0 ? "Fallback_A" : "Fallback_B")), out _);
        Assert.Contains("fallbacks = { l51 = {\"Fallback_A\"}, l15 = {\"Fallback_B\"}, l26 = {\"Fallback_B\"}, "
            + "l46 = {\"Fallback_B\"}, lt5 = {\"Fallback_B\"} }", lua);
    }

    [Fact]
    public void OnlyReferencedFallbacks_areDefined_andUnknownOnesAreReported()
    {
        var dump = Fixture((t, _) => t.Fallbacks.Add("Fallback_Merge_05_v2"));
        dump.Tasks["Fallback_Merge_05_v2"] = new DailyScoopDump.TaskEntry
        {
            Type = "Merge", Req = 200, Loc = "DailyChallenges_Task_Merge_Description", Points = 10,
        };
        dump.Tasks["Fallback_NeverReferenced"] = new DailyScoopDump.TaskEntry { Type = "Merge", Req = 1 };

        var lua = Render(dump, out var svc);
        Assert.Contains("[\"Fallback_Merge_05_v2\"] = { type = \"Merge\", goals = 200, points = 10 },", lua);
        Assert.DoesNotContain("Fallback_NeverReferenced", lua);
        Assert.Equal(1, svc.FallbackCount);
        Assert.Empty(svc.Warnings);
    }

    [Fact]
    public void ReferencedFallbackWithoutDefinition_isWarnedAbout()
    {
        Render(Fixture((t, _) => t.Fallbacks.Add("Fallback_Ghost")), out var svc);
        Assert.Contains(svc.Warnings, w => w.Contains("Fallback_Ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void EventGate_isEmitted_soModuleDailyScoopCanResolveEventTiedTasks()
    {
        // The gate is the whole reason the task ladder is read off the config instead of events.json
        // (the golden dumper writes that requirement kind as {}).
        var lua = Render(Fixture((t, _) => t.Event = "LBE_"), out _);
        Assert.Contains("event = \"LBE_\"", lua);
    }

    [Fact]
    public void SegmentWithADifferentTaskType_getsAVariantEntry_notAnExpandedGoal()
    {
        var lua = Render(Fixture((t, s) =>
        {
            if (s != 1) return;
            t.Type = "CompleteTasks";
            t.Req = 6;
        }), out _);
        Assert.Contains("variants = { l15 = { type = \"CompleteTasks\" } }", lua);
        Assert.Contains("type = \"Merge\"", lua);
    }

    [Theory]
    // A timed buff carries no reward id — the config stores a literally quoted empty string there —
    // and puts its duration in the aux field.
    [InlineData("\"\"", "OnFire", "5m", 1, "reward = {\"timed\", \"OnFire\", \"5m\"}")]
    [InlineData("", "CooldownRemover", "3m", 1, "reward = {\"timed\", \"CooldownRemover\", \"3m\"}")]
    [InlineData("Coins", "Currency", "Garage", 50, "reward = {\"coins\", 50}")]
    [InlineData("Energy", "Currency", "Garage", 7, "reward = {\"energy\", 7}")]
    [InlineData("Diamonds", "Currency", "", 5, "reward = {\"gems\", 5}")]
    [InlineData("TCE_CardPackBasic_5Stars_01", "CardCollectionPack", "", 1, "reward = {\"env\", 5}")]
    [InlineData("CSE_DailyChallenge_DailyChest1_01", "Item", "Garage", 1, "reward = {\"item\", \"CSE_DailyChallenge_DailyChest1_01\", 1}")]
    public void RewardEncoding_matchesTheFormModuleDailyScoopDecodes(string id, string type, string aux, int amount, string expected)
    {
        var lua = Render(Fixture((t, _) =>
            t.Reward = new DailyScoopDump.RewardEntry { Id = id, Type = type, Aux = aux, Amount = amount }), out _);
        Assert.Contains(expected, lua);
    }

    [Fact]
    public void MinutesGoal_isFlagged_fromTheLocKey()
    {
        var lua = Render(Fixture((t, _) =>
        {
            t.Type = "UseItemsFromChain";
            t.Params = "TimeSkipBoosterSingle";
            t.Loc = "DailyChallenges_Task_Use_Items_Minutes_Description";
        }), out _);
        Assert.Contains("params = \"TimeSkipBoosterSingle\", minutes = true", lua);
    }

    [Fact]
    public void Bonus_fallsBackToTheUnsuffixedSpecialObjective()
    {
        // Special objectives are shared between week revisions: the _v2 days reference the same
        // Special_<segment>_<week>_Day<N> ids as the original set.
        var dump = Fixture();
        foreach (var k in SegKeys)
            dump.Specials[$"Special_{SegId(k)}_HardWeek_Day1"] = new DailyScoopDump.SpecialEntry
            {
                Type = "CollectResource",
                Params = "Experience",
                Reqs = new List<int> { 2800, 4000 },
                Energy = new List<int> { 10, 15 },
                CatchUp = new List<int> { 10, 10 },
                Reward = new DailyScoopDump.RewardEntry { Id = "CSE_DailyChallenge_DailyChest2_01", Type = "Item", Aux = "Garage", Amount = 1 },
            };

        var lua = Render(dump, out _);
        Assert.Contains("bonus = { goals = {2800, 4000}, energy = {10, 15}, catchUp = 10, "
            + "reward = {\"item\", \"CSE_DailyChallenge_DailyChest2_01\", 1} },", lua);
    }

    [Fact]
    public void NoSpecialObjective_emitsNoBonusLine()
    {
        var lua = Render(Fixture(), out _);
        Assert.DoesNotContain("bonus = ", lua);
    }

    [Fact]
    public void Header_namesTheWeekSetItPublished_soAStaleSetIsVisibleOnThePage()
    {
        var lua = Render(Fixture(), out _);
        Assert.Contains("this one is \"_v2\", read from DailyChallenges_21 (HardWeek_v2).", lua);
    }

    [Fact]
    public void ExtractorWarnings_areCarriedThroughToTheRenderer()
    {
        var dump = Fixture();
        dump.Warnings.Add("week LT500_HardWeek_1_v3 not found in DailyChallengesWeeks");
        Render(dump, out var svc);
        Assert.Contains(svc.Warnings, w => w.Contains("_v3", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDailyScoopJson_loadsAsNull_soAnOldDumpCannotBlankTheModule()
    {
        Assert.Null(DailyScoopDatatableService.Load(Path.Combine(Path.GetTempPath(), "mmwt-no-such-daily-scoop.json")));
        Assert.Null(DailyScoopDatatableService.Load(""));
    }

    [Fact]
    public void UnreadableDailyScoopJson_loadsAsNull_ratherThanThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mmwt-bad-daily-scoop-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try { Assert.Null(DailyScoopDatatableService.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RoundTrip_throughTheJsonFile_preservesTheRenderedModule()
    {
        var dump = Fixture((t, _) => { t.Event = "LC_"; t.Fallbacks.Add("Fallback_Merge_05_v2"); });
        dump.Tasks["Fallback_Merge_05_v2"] = new DailyScoopDump.TaskEntry
        {
            Type = "Merge", Req = 200, Loc = "DailyChallenges_Task_Merge_Description", Points = 10,
        };
        var direct = Render(dump, out _);

        var path = Path.Combine(Path.GetTempPath(), $"mmwt-daily-scoop-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dump));
        try
        {
            var loaded = DailyScoopDatatableService.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(direct, Render(loaded!, out _));
        }
        finally { File.Delete(path); }
    }
}
