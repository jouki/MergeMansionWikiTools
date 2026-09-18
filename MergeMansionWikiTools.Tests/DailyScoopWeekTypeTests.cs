using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for the app-generated Daily Scoop `weekType` (Phase A of the Daily Scoop Extras
/// elimination): DailyChallenges_NN CoreSupportEvents become "The Daily Scoop" runs, each
/// carrying a per-run WeekType (Easy/Medium/Hard/Super) derived from its game MinigameId.
/// Mirrors EventPrefixTests' fixture/harness conventions. Covers: generation (dump →
/// group/run), merge-preserve of historical weekType, and the Lua emit (per-run weekType +
/// the static dailyScoopWeekRewards table).
/// </summary>
public class DailyScoopWeekTypeTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    /// <summary>Writes a minimal events.json with CoreSupportEvents entries (DailyChallenges_NN +
    /// optionally a DailyTasks internal entry), mirroring EventPrefixTests.WriteDumpJson.</summary>
    private string WriteDumpJson(params (string ActivableId, string? MinigameId, DateTime Start)[] entries)
    {
        var obj = new
        {
            CreatedAt = "2026-07-05T00:00:00",
            Data = new
            {
                CoreSupportEvents = entries.Select(e => new
                {
                    ActivableId = e.ActivableId,
                    Name = (string?)null,
                    MinigameId = e.MinigameId,
                    ActivableParams = new
                    {
                        IsEnabled = true,
                        Lifetime = "ScheduleBased",
                        Schedule = new
                        {
                            Start = e.Start.ToString("yyyy-MM-ddTHH:mm:ss"),
                            Duration = "7d 0h 0min 0s"
                        }
                    }
                }).ToArray()
            }
        };
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        File.WriteAllText(path, JsonSerializer.Serialize(obj));
        return path;
    }

    // ── generation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DailyChallengesEvent_becomes_TheDailyScoop_with_weekType()
    {
        var path = WriteDumpJson(
            ("DailyChallenges_08", "MedWeek", new DateTime(2026, 6, 29, 8, 5, 0, DateTimeKind.Utc)),
            ("DailyChallenges_11", "SuperWeek", new DateTime(2026, 7, 6, 8, 5, 0, DateTimeKind.Utc)),
            ("DailyTasksV2", null, new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc)));
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, null);

        var scoop = svc.Groups.SingleOrDefault(g => g.Name == "The Daily Scoop");
        Assert.NotNull(scoop);
        Assert.Equal("Core Support Event", scoop!.Category);
        Assert.Equal(2, scoop.Runs.Count);
        Assert.Equal("Medium", scoop.Runs.Single(r => r.Start.Month == 6).WeekType);
        Assert.Equal("Super", scoop.Runs.Single(r => r.Start.Month == 7).WeekType);
        Assert.DoesNotContain(svc.Groups, g => g.Name.Contains("DailyTasks"));
    }

    /// <summary>Regression (2026-09-15): config 26.07.01 re-cut the whole Daily Scoop week set and
    /// renamed every MinigameId with a revision suffix (HardWeek → HardWeek_v2, live from the 2026-08-31
    /// week). The exact-match mapping then yielded null, so every run from 2026-08-31 on lost its
    /// weekType and the wiki page stopped marking which difficulty the current week is. The suffix marks
    /// a new task line-up, not a new difficulty, so it must be stripped before mapping.</summary>
    [Fact]
    public async Task VersionSuffixedMinigameId_stillMapsToWeekType()
    {
        var path = WriteDumpJson(
            ("DailyChallenges_19", "SuperWeek_v2", new DateTime(2026, 8, 31, 8, 5, 0, DateTimeKind.Utc)),
            ("DailyChallenges_21", "HardWeek_v2", new DateTime(2026, 9, 14, 8, 5, 0, DateTimeKind.Utc)),
            ("DailyChallenges_22", "EasyWeek_v2", new DateTime(2026, 9, 21, 8, 5, 0, DateTimeKind.Utc)),
            ("DailyChallenges_20", "MedWeek_v3", new DateTime(2026, 9, 7, 8, 5, 0, DateTimeKind.Utc)));
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, null);

        var scoop = svc.Groups.Single(g => g.Name == "The Daily Scoop");
        Assert.Equal("Super", scoop.Runs.Single(r => r.Start.Month == 8).WeekType);
        Assert.Equal("Hard", scoop.Runs.Single(r => r.Start.Day == 14).WeekType);
        Assert.Equal("Easy", scoop.Runs.Single(r => r.Start.Day == 21).WeekType);
        Assert.Equal("Medium", scoop.Runs.Single(r => r.Start.Day == 7).WeekType);   // future _v3 too
        Assert.DoesNotContain(svc.Notes, n => n.Contains("week type", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The suffix half of the same split, which DailyScoopExtractor uses to pick the week SET whose
    /// task lists get published. Stripping it is not enough there: publishing the original set while
    /// the game serves _v2 is exactly the bug that feeds (v0.24.68).
    /// </summary>
    [Theory]
    [InlineData("HardWeek_v2", "HardWeek", "_v2")]
    [InlineData("MedWeek_v13", "MedWeek", "_v13")]
    [InlineData("EasyWeek", "EasyWeek", "")]
    [InlineData("SuperWeek_vX", "SuperWeek_vX", "")]      // not digits -> not a revision
    [InlineData("SuperWeek_v", "SuperWeek_v", "")]        // nothing after _v -> not a revision
    [InlineData("_v2", "_v2", "")]                        // nothing before _v -> not a revision
    [InlineData(null, "", "")]
    public void SplitRevision_separatesTheWeekSetSuffixFromTheBaseId(string? id, string expectedBase, string expectedSuffix)
    {
        var (baseId, suffix) = EventScheduleService.SplitRevision(id);
        Assert.Equal(expectedBase, baseId);
        Assert.Equal(expectedSuffix, suffix);
        Assert.Equal(expectedBase, EventScheduleService.StripRevisionSuffix(id));
    }

    /// <summary>A MinigameId that is genuinely unrecognisable must be REPORTED, not silently dropped —
    /// the silent null is what let the _v2 rename go unnoticed for three weeks.</summary>
    [Fact]
    public async Task UnknownMinigameId_isReportedInNotes()
    {
        var path = WriteDumpJson(
            ("DailyChallenges_30", "MysteryWeek", new DateTime(2026, 10, 5, 8, 5, 0, DateTimeKind.Utc)));
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, null);

        var run = svc.Groups.Single(g => g.Name == "The Daily Scoop").Runs.Single();
        Assert.Null(run.WeekType);
        var note = Assert.Single(svc.Notes, n => n.Contains("MysteryWeek", StringComparison.Ordinal));
        Assert.Contains("DailyChallenges_30", note, StringComparison.Ordinal);
    }

    // ── merge-preserve ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Historical_DailyScoop_run_preserves_weekType()
    {
        // Dump has no Daily Scoop runs at all (unrelated CoreSupportEvent only) — the historical
        // "The Daily Scoop" run lives ONLY in the live module and must survive the merge.
        var path = WriteDumpJson(("DailyTasksV2", null, new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc)));
        var live = @"return { events = { { name = ""The Daily Scoop"", category = ""Core Support Event"", runs = { { start = { year = 2026, month = 5, day = 25, hour = 8, min = 5 }, durationDays = 7, weekType = ""Hard"" } } } } }";
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, live);

        var run = svc.Groups.Single(g => g.Name == "The Daily Scoop").Runs.Single();
        Assert.Equal("Hard", run.WeekType);
    }

    // ── Lua emit ────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedLua_emits_weekType_and_rewardMap()
    {
        var groups = new List<EventScheduleGroup> {
            new() { Name = "The Daily Scoop", Category = "Core Support Event",
                Runs = { new EventScheduleRun(new DateTime(2026,7,6,8,5,0), TimeSpan.FromDays(7), "DailyChallenges_11", WeekType: "Super") } }
        };
        var lua = new LuaGeneratorService().GenerateEventScheduleLua(groups, createdAt: null);
        Assert.Contains("weekType = \"Super\"", lua);
        Assert.Contains("dailyScoopWeekRewards = {", lua);
        Assert.Contains("Super  = { \"Fancy Blue Chest\", 2 }", lua);
        Assert.Contains("Hard   = { \"Red Chest\", 2 }", lua);
    }
}
