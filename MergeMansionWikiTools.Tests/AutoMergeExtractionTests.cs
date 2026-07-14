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
/// Auto-Merge Madness extraction: the enabled AutoMerge_Daily CoreSupportEvents (00/06/12/18 UTC,
/// ~1h each via Lifetime, recurring daily) are captured as a compact <c>AutoMergeWindows</c> pattern
/// instead of being expanded into ~720 flooding runs, and the Lua emit surfaces them under an
/// <c>autoMerge</c> block. Disabled legacy AutoMerge_NN one-offs are dropped entirely.
/// </summary>
public class AutoMergeExtractionTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    /// <summary>Minimal events.json with AutoMerge* CoreSupportEvents. <paramref name="recurrence"/>
    /// null = a non-recurring legacy one-off; a numeric <paramref name="lifetimeMs"/> becomes the
    /// active-window duration (the game's Lifetime, preferred over Schedule.Duration).</summary>
    private string WriteDumpJson(params (string ActivableId, bool IsEnabled, long LifetimeMs, string? Recurrence, DateTime Start)[] entries)
    {
        var obj = new
        {
            CreatedAt = "2026-07-08T00:00:00",
            Data = new
            {
                CoreSupportEvents = entries.Select(e => new
                {
                    ActivableId = e.ActivableId,
                    Name = "Auto Merge",
                    EventType = "AutoMerge",
                    ActivableParams = new
                    {
                        IsEnabled = e.IsEnabled,
                        Lifetime = e.LifetimeMs,
                        Schedule = new
                        {
                            Start = e.Start.ToString("yyyy-MM-ddTHH:mm:ss"),
                            Duration = "300min 0s",
                            Recurrence = e.Recurrence,
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

    [Fact]
    public async Task EnabledDailyWindows_captured_asPattern_notFloodingGroups()
    {
        var path = WriteDumpJson(
            ("AutoMerge_Daily",    true,  3600000, "1d 0h 0min 0s", new DateTime(2026, 4, 21, 0,  0, 0, DateTimeKind.Utc)),
            ("AutoMerge_Daily_02", true,  3600000, "1d 0h 0min 0s", new DateTime(2026, 4, 21, 6,  0, 0, DateTimeKind.Utc)),
            ("AutoMerge_Daily_03", true,  3600000, "1d 0h 0min 0s", new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc)),
            ("AutoMerge_Daily_04", true,  3600000, "1d 0h 0min 0s", new DateTime(2026, 4, 21, 18, 0, 0, DateTimeKind.Utc)),
            // Disabled legacy one-off (no recurrence) — must be dropped, not captured.
            ("AutoMerge_01",       false, 21600000, null,           new DateTime(2025, 9, 23, 8,  0, 0, DateTimeKind.Utc)));
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, null);

        // Four enabled daily windows captured; the disabled one-off is gone.
        Assert.Equal(4, svc.AutoMergeWindows.Count);
        // Window duration comes from Lifetime (1h), NOT the 5h Schedule.Duration.
        Assert.All(svc.AutoMergeWindows, w => Assert.Equal(TimeSpan.FromHours(1), w.Duration));
        // Daily recurrence.
        Assert.All(svc.AutoMergeWindows, w => Assert.Equal(TimeSpan.FromDays(1), w.Interval));
        Assert.Equal(new[] { 0, 6, 12, 18 }, svc.AutoMergeWindows.Select(w => w.Start.Hour).OrderBy(h => h).ToArray());
        // No AutoMerge event floods the normal schedule (would be ~720 runs if expanded).
        Assert.DoesNotContain(svc.Groups, g => g.Name.Contains("Auto", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GeneratedLua_emits_autoMerge_block()
    {
        var windows = new List<AutoMergeWindow>
        {
            new(new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1), TimeSpan.FromDays(1)),
            new(new DateTime(2026, 4, 21, 6, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1), TimeSpan.FromDays(1)),
        };

        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup>(), windows, createdAt: null);

        Assert.Contains("autoMerge = {", lua);
        Assert.Contains("name = \"Auto-Merge\"", lua);
        Assert.Contains("durationSec = 3600", lua);
        Assert.Contains("intervalSec = 86400", lua);
        Assert.Contains("hour = 6", lua);
    }

    [Fact]
    public void GeneratedLua_withoutWindows_emitsNoAutoMergeBlock()
    {
        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup>(), createdAt: null);
        Assert.DoesNotContain("autoMerge = {", lua);
    }
}
