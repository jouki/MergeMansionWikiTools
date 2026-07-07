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
/// Tests for the per-event `prefix` field of Module:Datatable/Events (id-family prefix of
/// CollectibleBoards events: CBE/LDE/LC/LS/SE). Consumed by Module:DailyScoop to resolve
/// Daily Scoop tasks whose params are ID-PREFIX filters (CBE_ matches CBE_* only, not LDE_*).
/// Covers: extraction from the dump id, Lua emit, and merge-preserve on historical entries.
/// </summary>
public class EventPrefixTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    private string WriteDumpJson(string eventName, string eventId, DateTime start)
    {
        var obj = new
        {
            CreatedAt = "2026-07-05T00:00:00",
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
                }
            }
        };
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        File.WriteAllText(path, JsonSerializer.Serialize(obj));
        return path;
    }

    // ── emit ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Emit_includesPrefix_whenSet()
    {
        var group = new EventScheduleGroup { Name = "Legacy Lane", Category = "Seasonal Event", Prefix = "CBE" };
        group.Runs.Add(new EventScheduleRun(new DateTime(2026, 6, 1, 8, 0, 0), TimeSpan.FromDays(5), "CBE_X"));

        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup> { group }, "2026-07-05T00:00:00");

        Assert.Contains("prefix = \"CBE\"", lua);
    }

    [Fact]
    public void Emit_omitsPrefix_whenNull()
    {
        var group = new EventScheduleGroup { Name = "Teatime Delight", Category = "Solo Milestone" };
        group.Runs.Add(new EventScheduleRun(new DateTime(2026, 6, 1, 8, 0, 0), TimeSpan.FromDays(3), "x"));

        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup> { group }, "2026-07-05T00:00:00");

        Assert.DoesNotContain("prefix =", lua);
    }

    // ── extrakce z dumpu ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CBE_FlashbackRewind2026", "CBE")]
    [InlineData("LDE_GreenAcresQuest2024", "LDE")]
    [InlineData("LC_SummerLuckyCatch2026", "LC")]
    public async Task Load_derivesPrefix_fromCollectibleBoardEventId(string eventId, string expectedPrefix)
    {
        var path = WriteDumpJson("Prefix Probe Event", eventId, new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc));
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, null);

        var group = svc.Groups.Single(g => g.Name == "Prefix Probe Event");
        Assert.Equal(expectedPrefix, group.Prefix);
    }

    // ── merge-preserve ──────────────────────────────────────────────────────────
    // Event žije UŽ JEN v live modulu (z dumpu vypadl) — prefix musí přežít regeneraci
    // na historickém záznamu, stejný vzor jako badge/onFireVariant.

    [Fact]
    public async Task Merge_preservesPrefix_onHistoricalEntry()
    {
        // dump obsahuje jiný event, historický "Voyance's Visions" v něm není
        var path = WriteDumpJson("Other Event", "CBE_Other2026", new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc));
        var liveModule = @"return {
    events = {
        {
            name = ""Voyance's Visions"",
            category = ""Seasonal Event"",
            prefix = ""LDE"",
            runs = {
                { start = { year = 2024, month = 9, day = 12, hour = 8 }, durationDays = 5 },
            },
        },
    },
}";
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, liveModule);

        var group = svc.Groups.Single(g => g.Name == "Voyance's Visions");
        Assert.Equal("LDE", group.Prefix);

        var lua = new LuaGeneratorService().GenerateEventScheduleLua(svc.Groups, "2026-07-05T00:00:00");
        Assert.Contains("prefix = \"LDE\"", lua);
    }
}
