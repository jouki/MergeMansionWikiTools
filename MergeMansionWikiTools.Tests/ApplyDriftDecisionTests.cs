using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for <see cref="EventScheduleService.ApplyDriftDecision"/>.
/// Verifies that Variant B (isUpdate=true) removes the old ExistingStart run and that
/// Variant A (isUpdate=false) keeps both runs untouched.
/// </summary>
public class ApplyDriftDecisionTests : IDisposable
{
    private static readonly string EventName = "Flashback Rewind";
    private static readonly string EventId   = "CBE_FlashbackRewind2026";

    private readonly System.Collections.Generic.List<string> _tempFiles = new();

    /// <summary>
    /// Writes a minimal events.json with two CollectibleBoards entries for EventName —
    /// one at <paramref name="startA"/> and one at <paramref name="startB"/> — so that
    /// after LoadAsync the group has both runs.
    /// </summary>
    private string WriteDumpJsonTwoRuns(DateTime startA, DateTime startB, double durationDays)
    {
        int days = (int)durationDays;
        string durStr = $"{days}d 0h 0min 0s";

        static object MakeEntry(string id, string name, string start, string dur) => new
        {
            Name = name,
            CollectibleBoardEventId = id,
            DisplayName = name,
            PrefabsOverride = (string?)null,
            ActivableParams = new
            {
                IsEnabled = true,
                Lifetime = "ScheduleBased",
                Schedule = new { Start = start, Duration = dur }
            }
        };

        var obj = new
        {
            CreatedAt = "2026-06-26T00:00:00",
            Data = new
            {
                CollectibleBoards = new[]
                {
                    MakeEntry(EventId + "_A", EventName, startA.ToString("yyyy-MM-ddTHH:mm:ss"), durStr),
                    MakeEntry(EventId + "_B", EventName, startB.ToString("yyyy-MM-ddTHH:mm:ss"), durStr),
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

    // ── helper: load a service with two distinct runs for EventName ─────────────

    private async Task<(EventScheduleService svc, DateTime existingStart, DateTime newStart)>
        LoadTwoRunServiceAsync()
    {
        // Use starts that are clearly AutoSeparate (>60 days apart) so LoadAsync keeps both
        // without adding a PendingDecision — we want to test ApplyDriftDecision directly.
        var existingStart = new DateTime(2025, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var newStart      = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc); // ~365d apart
        double dur = 7;

        var path = WriteDumpJsonTwoRuns(existingStart, newStart, dur);
        var svc  = new EventScheduleService();
        await svc.LoadAsync(path); // no live module — we control the group directly
        return (svc, existingStart, newStart);
    }

    // ── Test 1: isUpdate = true removes the ExistingStart run ──────────────────

    [Fact]
    public async Task ApplyDriftDecision_IsUpdateTrue_RemovesExistingStartRun()
    {
        var (svc, existingStart, newStart) = await LoadTwoRunServiceAsync();

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, group.Runs.Count); // sanity: both runs loaded

        var decision = new EventDriftDecision(EventName, existingStart, newStart, ExistingDurationDays: 7);
        svc.ApplyDriftDecision(decision, isUpdate: true);

        // After applying isUpdate=true the old run must be gone.
        Assert.DoesNotContain(group.Runs, r => r.Start == existingStart);
        // The new run must still be present.
        Assert.Contains(group.Runs, r => r.Start == newStart);
        Assert.Single(group.Runs);
    }

    // ── Test 2: isUpdate = false keeps both runs ────────────────────────────────

    [Fact]
    public async Task ApplyDriftDecision_IsUpdateFalse_KeepsBothRuns()
    {
        var (svc, existingStart, newStart) = await LoadTwoRunServiceAsync();

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, group.Runs.Count);

        var decision = new EventDriftDecision(EventName, existingStart, newStart, ExistingDurationDays: 7);
        svc.ApplyDriftDecision(decision, isUpdate: false);

        // No change expected — both runs remain.
        Assert.Equal(2, group.Runs.Count);
        Assert.Contains(group.Runs, r => r.Start == existingStart);
        Assert.Contains(group.Runs, r => r.Start == newStart);
    }

    // ── Test 3: safe when the run is already absent ─────────────────────────────

    [Fact]
    public async Task ApplyDriftDecision_IsUpdateTrue_AlreadyAbsent_NoException()
    {
        var (svc, existingStart, newStart) = await LoadTwoRunServiceAsync();

        var decision = new EventDriftDecision(EventName, existingStart, newStart, ExistingDurationDays: 7);

        // Apply twice — second call must not throw even though the run is already gone.
        svc.ApplyDriftDecision(decision, isUpdate: true);
        svc.ApplyDriftDecision(decision, isUpdate: true); // idempotent
    }

    // ── Test 4: unknown event name is a no-op ──────────────────────────────────

    [Fact]
    public async Task ApplyDriftDecision_UnknownEventName_NoException()
    {
        var (svc, existingStart, newStart) = await LoadTwoRunServiceAsync();

        var decision = new EventDriftDecision("NonExistentEvent", existingStart, newStart, ExistingDurationDays: 7);
        svc.ApplyDriftDecision(decision, isUpdate: true); // must not throw

        // All original runs still present.
        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, group.Runs.Count);
    }
}
