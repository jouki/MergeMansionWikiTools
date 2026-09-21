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
/// Tests for the per-event <c>subGoal</c> / <c>subGoalRewards</c> fields of
/// Module:Datatable/Events. The flag marks events that run the Old Map side track; the Old Map
/// page lists them through Module:Events instead of hard-coding a list that goes stale.
///
/// Detection is on the dump's <c>&lt;eventId&gt;_SubGoalLevel&lt;NN&gt;</c> event levels — NOT on the
/// item, because the generic SubGoal_MapItem chain is retargeted to whichever event currently
/// uses it. The reward ladder is hand-maintained on the wiki and must survive regeneration.
/// </summary>
public class EventSubGoalTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    private string WriteDumpJson(string eventName, string eventId, DateTime start, bool withSubGoalLevels)
    {
        var levels = new List<object>
        {
            new { EventLevelId = eventId + "_RewardLevel01", RequiredPoints = 10 }
        };
        if (withSubGoalLevels)
            for (var i = 1; i <= 6; i++)
                levels.Add(new { EventLevelId = $"{eventId}_SubGoalLevel{i:00}", RequiredPoints = 1 });

        var obj = new
        {
            CreatedAt = "2026-09-18T00:00:00",
            Data = new
            {
                CollectibleBoards = new[]
                {
                    new
                    {
                        Name = eventName,
                        CollectibleBoardEventId = eventId,
                        DisplayName = eventName,
                        PrefabsOverride = (string?)null,
                        ActivableParams = new
                        {
                            IsEnabled = true,
                            Lifetime = "ScheduleBased",
                            Schedule = new
                            {
                                Start = start.ToString("yyyy-MM-ddTHH:mm:ss"),
                                Duration = "5d 0h 0min 0s"
                            }
                        }
                    }
                },
                EventLevels = levels
            }
        };
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        File.WriteAllText(path, JsonSerializer.Serialize(obj));
        return path;
    }

    // ── detection ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_flagsEvent_whenDumpHasSubGoalLevels()
    {
        var path = WriteDumpJson("Haunted Halloween", "CBE_Halloween2025", new DateTime(2025, 10, 28, 8, 0, 0), true);
        var svc = new EventScheduleService();
        await svc.LoadAsync(path);

        Assert.True(svc.Groups.Single(g => g.Name == "Haunted Halloween").SubGoal);
    }

    [Fact]
    public async Task Load_leavesFlagOff_whenEventHasNoSubGoalLevels()
    {
        var path = WriteDumpJson("Legacy Lane", "CBE_LegacyLane", new DateTime(2026, 6, 1, 8, 0, 0), false);
        var svc = new EventScheduleService();
        await svc.LoadAsync(path);

        Assert.False(svc.Groups.Single(g => g.Name == "Legacy Lane").SubGoal);
    }

    // ── emit ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Emit_includesSubGoal_whenSet()
    {
        var group = new EventScheduleGroup { Name = "Sweet Mess Express", Category = "Seasonal Event", SubGoal = true };
        group.Runs.Add(new EventScheduleRun(new DateTime(2026, 4, 3, 8, 0, 0), TimeSpan.FromDays(5), "CBE_SweetMess"));

        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup> { group }, "2026-09-18T00:00:00");

        Assert.Contains("subGoal = true", lua);
    }

    [Fact]
    public void Emit_omitsSubGoal_whenUnset()
    {
        var group = new EventScheduleGroup { Name = "Legacy Lane", Category = "Seasonal Event" };
        group.Runs.Add(new EventScheduleRun(new DateTime(2026, 6, 1, 8, 0, 0), TimeSpan.FromDays(5), "CBE_LegacyLane"));

        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup> { group }, "2026-09-18T00:00:00");

        Assert.DoesNotContain("subGoal", lua.Split("return {")[1]);
    }

    [Fact]
    public void Emit_writesRewardLadderInOrder()
    {
        var group = new EventScheduleGroup { Name = "Murder at the Mansion", Category = "Seasonal Event", SubGoal = true };
        group.SubGoalRewards.AddRange(new[] { "{{Item/Group|Clues Envelope|1}}", "{{Energy}} 15" });
        group.Runs.Add(new EventScheduleRun(new DateTime(2026, 9, 17, 8, 0, 0), TimeSpan.FromDays(6), "LDE_MurderAtTheMansion"));

        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup> { group }, "2026-09-18T00:00:00");

        var ladder = lua.Split("subGoalRewards = {")[1].Split("},")[0];
        Assert.True(ladder.IndexOf("Clues Envelope|1") < ladder.IndexOf("{{Energy}} 15"));
    }

    // ── merge-preserve ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_keepsRewardLadderFromLiveModule_whenDumpHasNone()
    {
        // The dump never carries the wikitext ladder; regeneration must not wipe it.
        var live = @"return { events = { {
            name = ""Haunted Halloween"", category = ""Seasonal Event"", subGoal = true,
            subGoalRewards = { ""{{Energy}} 15"", ""{{Item/nolevel|Hourglass|1}}"" },
            runs = { { start = { year = 2025, month = 10, day = 28, hour = 8 }, durationDays = 5 } }
        } } }";
        var path = WriteDumpJson("Haunted Halloween", "CBE_Halloween2025", new DateTime(2025, 10, 28, 8, 0, 0), true);

        var svc = new EventScheduleService();
        await svc.LoadAsync(path, live);

        var group = svc.Groups.Single(g => g.Name == "Haunted Halloween");
        Assert.Equal(new[] { "{{Energy}} 15", "{{Item/nolevel|Hourglass|1}}" }, group.SubGoalRewards);
    }

    [Fact]
    public async Task Merge_keepsFlag_whenTheEventIsGoneFromTheDump()
    {
        // Purged from config but still on the wiki: the live entry alone has to carry the flag.
        var live = @"return { events = { {
            name = ""Bella's Holiday Workshop"", category = ""Seasonal Event"", subGoal = true,
            subGoalRewards = { ""{{Energy}} 15"" },
            runs = { { start = { year = 2025, month = 12, day = 12, hour = 8 }, durationDays = 5 } }
        } } }";
        var path = WriteDumpJson("Legacy Lane", "CBE_LegacyLane", new DateTime(2026, 6, 1, 8, 0, 0), false);

        var svc = new EventScheduleService();
        await svc.LoadAsync(path, live);

        var group = svc.Groups.Single(g => g.Name == "Bella's Holiday Workshop");
        Assert.True(group.SubGoal);
        Assert.Single(group.SubGoalRewards);
    }
}
