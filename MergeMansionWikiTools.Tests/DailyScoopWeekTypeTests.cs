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
