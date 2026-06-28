using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Verifies EventScheduleService expands a recurring schedule (Schedule.Recurrence period +
/// optional NumRepeats) into explicit occurrence runs — the mechanism behind Mix a Booster
/// (14d, unbounded) and Lucky Catch/Snap Summer (14d, ×7).
/// </summary>
public class RecurrenceExpansionTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _tempFiles = new();

    private string WriteMixDump(string start, string? recurrence, int? numRepeats)
    {
        object schedule = numRepeats is { } n
            ? new { Start = start, Duration = "2d 0h 0min 0s", Recurrence = recurrence, NumRepeats = n }
            : recurrence != null
                ? new { Start = start, Duration = "2d 0h 0min 0s", Recurrence = recurrence }
                : (object)new { Start = start, Duration = "2d 0h 0min 0s" };

        var obj = new
        {
            CreatedAt = "2026-06-27T00:00:00",
            Data = new
            {
                MixABoosterEvents = new[]
                {
                    new
                    {
                        ConfigKey = "MixABoost_07",
                        Name = "Mix a Booster",
                        ActivableParams = new
                        {
                            IsEnabled = true,
                            Lifetime = "ScheduleBased",
                            Schedule = schedule
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

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    // ── Bounded recurrence (NumRepeats) → exactly N biweekly occurrences ──────────
    [Fact]
    public async Task Bounded_NumRepeats_ExpandsToExactCount()
    {
        // 14d period, 7 repeats from 2026-06-11 → 7 runs, biweekly (matches LC/LS Summer).
        var path = WriteMixDump("2026-06-11T08:00:00", "14d 0h 0min 0s", 7);
        var svc = new EventScheduleService();
        await svc.LoadAsync(path);

        var group = svc.Groups.Single(g => g.Name == "Mix a Booster");
        Assert.Equal(7, group.Runs.Count);
        var starts = group.Runs.Select(r => r.Start.Date).OrderBy(d => d).ToList();
        Assert.Equal(new DateTime(2026, 6, 11), starts.First());
        Assert.Equal(new DateTime(2026, 9, 3), starts.Last()); // 06-11 + 6*14d
        Assert.Contains(new DateTime(2026, 6, 25), starts);
        Assert.Contains(new DateTime(2026, 7, 9), starts);
    }

    // ── Unbounded recurrence (NumRepeats null) → multiple runs up to the horizon ──
    [Fact]
    public async Task Unbounded_ExpandsAnchorPlusBiweekly_UpToHorizon()
    {
        // Anchor 30 days before "now"; unbounded → several past+near-future runs, 14d apart,
        // none beyond now+60d. (Mix a Booster real-world shape.)
        var anchor = DateTime.UtcNow.Date.AddDays(-30);
        var path = WriteMixDump(anchor.ToString("yyyy-MM-ddTHH:mm:ss"), "14d 0h 0min 0s", null);
        var svc = new EventScheduleService();
        await svc.LoadAsync(path);

        var group = svc.Groups.Single(g => g.Name == "Mix a Booster");
        Assert.True(group.Runs.Count > 1, $"expected multiple occurrences, got {group.Runs.Count}");
        Assert.Contains(group.Runs, r => r.Start.Date == anchor.AddDays(14));   // concrete expanded occurrence
        // Forward horizon is 180 days; a 14d cadence must reach beyond 60 days now (≥ now+150d run exists).
        Assert.Contains(group.Runs, r => r.Start >= DateTime.UtcNow.AddDays(150));
        var horizon = DateTime.UtcNow.AddDays(180);
        Assert.All(group.Runs, r => Assert.True(r.Start <= horizon));
    }

    // ── No recurrence → single anchor run (regression: non-recurring unaffected) ──
    [Fact]
    public async Task NoRecurrence_SingleRun()
    {
        var path = WriteMixDump("2025-10-14T08:00:00", null, null);
        var svc = new EventScheduleService();
        await svc.LoadAsync(path);

        var group = svc.Groups.Single(g => g.Name == "Mix a Booster");
        Assert.Single(group.Runs);
    }
}
