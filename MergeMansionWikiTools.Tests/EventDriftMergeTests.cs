using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Integration tests for the drift-handling logic in EventScheduleService.MergeExistingModule.
/// Each test builds a minimal events.json with one dump run, and a minimal Lua module string
/// with one live run for the same event at a different start, then asserts the expected outcome
/// based on the DriftVerdict returned by EventDriftClassifier.
/// </summary>
public class EventDriftMergeTests : IDisposable
{
    // ── shared helpers ──────────────────────────────────────────────────────────

    private static readonly string EventName = "Flashback Rewind";
    private static readonly string EventId   = "CBE_FlashbackRewind2026";

    // Builds a minimal events.json with ONE CollectibleBoards entry for EventName
    // starting at dumpStart with durationDays duration, then writes it to a temp file
    // and returns the path.
    private string WriteDumpJson(DateTime dumpStart, double durationDays)
    {
        // Duration as "Xd 0h 0min 0s"
        int days = (int)durationDays;
        string durStr = $"{days}d 0h 0min 0s";

        var obj = new
        {
            CreatedAt = "2026-06-25T00:00:00",
            Data = new
            {
                CollectibleBoards = new[]
                {
                    new
                    {
                        Name = EventName,
                        CollectibleBoardEventId = EventId,
                        DisplayName = EventName,
                        PrefabsOverride = (string?)null,
                        ActivableParams = new
                        {
                            IsEnabled = true,
                            Lifetime = "ScheduleBased",
                            Schedule = new
                            {
                                Start = dumpStart.ToString("yyyy-MM-ddTHH:mm:ss"),
                                Duration = durStr
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

    // Builds a minimal Module:Datatable/Events Lua string with ONE entry for EventName
    // at liveStart with liveEventDurationDays.
    private static string BuildLiveModule(DateTime liveStart, double liveEventDurationDays) => $@"return {{
    events = {{
        {{
            name = ""{EventName}"",
            category = ""Seasonal Event"",
            runs = {{
                {{
                    start = {{ year = {liveStart.Year}, month = {liveStart.Month}, day = {liveStart.Day}, hour = {liveStart.Hour}, min = {liveStart.Minute} }},
                    durationDays = {liveEventDurationDays.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                }}
            }}
        }}
    }}
}}";

    private readonly System.Collections.Generic.List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    // ── Test 1: AutoUpdate ──────────────────────────────────────────────────────
    // Dump run starts 2026-05-31; live run at 2026-05-29 (dur 5d → window [29, 3-Jun]).
    // 2026-05-31 is within [2026-05-29, 2026-05-29 + 5d] → AutoUpdate.
    // Expected: ONE run in the group (the dump's start 2026-05-31), PendingDecisions empty.

    [Fact]
    public async Task AutoUpdate_LiveRunSuperseded_OneDumpRunKept()
    {
        var liveStart  = new DateTime(2026, 5, 29, 8, 0, 0, DateTimeKind.Utc);
        var dumpStart  = new DateTime(2026, 5, 31, 8, 0, 0, DateTimeKind.Utc);
        double liveDur = 5;

        var path        = WriteDumpJson(dumpStart, liveDur);
        var liveModule  = BuildLiveModule(liveStart, liveDur);
        var svc         = new EventScheduleService();

        await svc.LoadAsync(path, liveModule);

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));

        // Only ONE run (the dump run); the live run was superseded.
        Assert.Single(group.Runs);
        Assert.Equal(dumpStart, group.Runs[0].Start);

        // No pending decisions — the classifier was able to auto-resolve.
        Assert.Empty(svc.PendingDecisions);
    }

    // ── Test 2: NeedsDecision ───────────────────────────────────────────────────
    // Dump run starts 2026-06-25; live run at 2026-05-29 (dur 5d).
    // Gap = 27d, beyond dur(5d) but <60d → NeedsDecision.
    // Expected: BOTH runs kept, PendingDecisions has one entry for EventName.

    [Fact]
    public async Task NeedsDecision_BothRunsKept_PendingDecisionRecorded()
    {
        var liveStart  = new DateTime(2026, 5, 29, 8, 0, 0, DateTimeKind.Utc);
        var dumpStart  = new DateTime(2026, 6, 25, 8, 0, 0, DateTimeKind.Utc);
        double liveDur = 5;

        var path       = WriteDumpJson(dumpStart, liveDur);
        var liveModule = BuildLiveModule(liveStart, liveDur);
        var svc        = new EventScheduleService();

        await svc.LoadAsync(path, liveModule);

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));

        // Both runs are present.
        Assert.Equal(2, group.Runs.Count);
        Assert.Contains(group.Runs, r => r.Start == dumpStart);
        Assert.Contains(group.Runs, r => r.Start == liveStart);

        // One pending decision recorded.
        Assert.Single(svc.PendingDecisions);
        var d = svc.PendingDecisions[0];
        Assert.Equal(EventName, d.EventName);
        Assert.Equal(liveStart,  d.ExistingStart);
        Assert.Equal(dumpStart,  d.NewStart);
        Assert.Equal(liveDur,    d.ExistingDurationDays);
    }

    // ── Test 3: AutoSeparate ───────────────────────────────────────────────────
    // Dump run starts 2026-08-15; live run at 2026-05-29 (dur 5d).
    // Gap = ~78d ≥ 60d → AutoSeparate.
    // Expected: BOTH runs kept, PendingDecisions EMPTY (clearly separate airings).

    [Fact]
    public async Task AutoSeparate_BothRunsKept_NoPendingDecision()
    {
        var liveStart  = new DateTime(2026, 5, 29, 8, 0, 0, DateTimeKind.Utc);
        var dumpStart  = new DateTime(2026, 8, 15, 8, 0, 0, DateTimeKind.Utc);
        double liveDur = 5;

        var path       = WriteDumpJson(dumpStart, liveDur);
        var liveModule = BuildLiveModule(liveStart, liveDur);
        var svc        = new EventScheduleService();

        await svc.LoadAsync(path, liveModule);

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));

        // Both runs are present.
        Assert.Equal(2, group.Runs.Count);
        Assert.Contains(group.Runs, r => r.Start == dumpStart);
        Assert.Contains(group.Runs, r => r.Start == liveStart);

        // No pending decisions — auto-resolved as separate airings.
        Assert.Empty(svc.PendingDecisions);
    }

    // ── Test 6: backward-overlap drift (dump nudged start EARLIER) → AutoUpdate ──
    // Devs moved the start one day earlier (08-08 → 08-07), both 21d → intervals overlap heavily.
    // Must supersede the live 08-08 run (NOT keep both as two runs). Regression for Season Pass
    // "Green with Envy". Distinct from a far-earlier prior-year run (Test 3b), which does NOT overlap.
    [Fact]
    public async Task BackwardOverlapDrift_AutoUpdate_SingleRunNoDecision()
    {
        var liveStart = new DateTime(2026, 8, 8, 8, 0, 0, DateTimeKind.Utc);
        var dumpStart = new DateTime(2026, 8, 7, 8, 0, 0, DateTimeKind.Utc); // 1d earlier, dur 21 → overlap
        double dur = 21;

        var path = WriteDumpJson(dumpStart, dur);
        var liveModule = BuildLiveModule(liveStart, dur);
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, liveModule);

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));
        // The live 08-08 run is superseded by the drifted dump 08-07 run → exactly one run, at 08-07.
        Assert.Single(group.Runs);
        Assert.Equal(dumpStart, group.Runs[0].Start);
        Assert.DoesNotContain(group.Runs, r => r.Start == liveStart);
        Assert.Empty(svc.PendingDecisions);
    }

    // ── Test 3b: an EARLIER unrelated dump run must not supersede a later live run ──
    // Regression for "Love on the Vine": the dump carries TWO runs for the event — an old
    // 2024-05-13 airing (which also exists in the live module) AND the re-aired 2025-07-04 run.
    // The live module holds the ORIGINAL 2025-06-04 run (drifted 30d → NeedsDecision vs July).
    // The earlier 2024-05-13 dump run must NOT falsely "supersede" the 2025-06-04 live run; both
    // the June live run and the July dump run must survive.

    // Builds an events.json with TWO CollectibleBoards runs for the same event name (distinct ids).
    private string WriteDumpJsonTwoRuns(
        DateTime startA, double durA, string idA,
        DateTime startB, double durB, string idB)
    {
        object Entry(DateTime s, double d, string id, bool enabled) => new
        {
            Name = EventName,
            CollectibleBoardEventId = id,
            DisplayName = EventName,
            PrefabsOverride = (string?)null,
            ActivableParams = new
            {
                IsEnabled = enabled,
                Lifetime = "ScheduleBased",
                Schedule = new
                {
                    Start = s.ToString("yyyy-MM-ddTHH:mm:ss"),
                    Duration = $"{(int)d}d 0h 0min 0s"
                }
            }
        };

        var obj = new
        {
            CreatedAt = "2025-06-18T00:00:00",
            Data = new { CollectibleBoards = new[] { Entry(startA, durA, idA, false), Entry(startB, durB, idB, true) } }
        };
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        File.WriteAllText(path, JsonSerializer.Serialize(obj));
        return path;
    }

    [Fact]
    public async Task EarlierDumpRun_DoesNotSupersede_LaterLiveRun()
    {
        // Dump: 2024-05-13 (old airing) + 2025-07-04 (re-air). Live: 2025-06-04 (original 2025 airing).
        var oldDump  = new DateTime(2024, 5, 13, 8, 0, 0, DateTimeKind.Utc);
        var reairDump = new DateTime(2025, 7, 4, 8, 0, 0, DateTimeKind.Utc);
        var liveStart = new DateTime(2025, 6, 4, 8, 0, 0, DateTimeKind.Utc);

        var path = WriteDumpJsonTwoRuns(oldDump, 15, "LDE_Hopeberry2024", reairDump, 5, "LDE_Hopeberry2025");
        var liveModule = BuildLiveModule(liveStart, 5);
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, liveModule);

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));

        // All three runs survive: old dump airing, original live airing, and the re-air.
        Assert.Equal(3, group.Runs.Count);
        Assert.Contains(group.Runs, r => r.Start == oldDump);
        Assert.Contains(group.Runs, r => r.Start == liveStart);   // <-- the run that was wrongly dropped
        Assert.Contains(group.Runs, r => r.Start == reairDump);

        // June→July is the NeedsDecision drift; one decision recorded.
        Assert.Single(svc.PendingDecisions);
        Assert.Equal(liveStart,  svc.PendingDecisions[0].ExistingStart);
        Assert.Equal(reairDump,  svc.PendingDecisions[0].NewStart);
    }

    // ── Test 5: Non-Seasonal category auto-separates — NeedsDecision skipped, both runs kept ──
    // Same NeedsDecision scenario as Test 2, but the live module uses category "Lucky Event"
    // (not "Seasonal Event"). Under the new category gate, non-Seasonal events ALWAYS auto-
    // separate silently: both runs are kept and PendingDecisions stays empty (no dialog).
    // This replaces the old AutoSeparateEvents exclude-list test.

    // Builds a minimal live module with a specified category (used by Test 5).
    private static string BuildLiveModuleWithCategory(DateTime liveStart, double liveEventDurationDays, string category) => $@"return {{
    events = {{
        {{
            name = ""{EventName}"",
            category = ""{category}"",
            runs = {{
                {{
                    start = {{ year = {liveStart.Year}, month = {liveStart.Month}, day = {liveStart.Day}, hour = {liveStart.Hour}, min = {liveStart.Minute} }},
                    durationDays = {liveEventDurationDays.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                }}
            }}
        }}
    }}
}}";

    [Fact]
    public async Task NonSeasonalCategory_NeedsDecision_NoPendingDecision_BothRunsKept()
    {
        // Same timing as Test 2 (NeedsDecision gap), but category = "Lucky Event" (non-Seasonal).
        var liveStart = new DateTime(2026, 5, 29, 8, 0, 0, DateTimeKind.Utc);
        var dumpStart = new DateTime(2026, 6, 25, 8, 0, 0, DateTimeKind.Utc);
        double liveDur = 5;

        // WriteDumpJson emits a CollectibleBoards entry whose category is derived from the id.
        // LC_* / LS_* → "Lucky Event"; CBE_* → "Seasonal Event". Use an LC_ id.
        var luckyDumpPath = Path.GetTempFileName();
        _tempFiles.Add(luckyDumpPath);
        var luckyObj = new
        {
            CreatedAt = "2026-06-25T00:00:00",
            Data = new
            {
                CollectibleBoards = new[]
                {
                    new
                    {
                        Name = EventName,
                        CollectibleBoardEventId = "LC_FlashbackRewind2026",  // LC_ → "Lucky Event"
                        DisplayName = EventName,
                        PrefabsOverride = (string?)null,
                        ActivableParams = new
                        {
                            IsEnabled = true,
                            Lifetime = "ScheduleBased",
                            Schedule = new
                            {
                                Start = dumpStart.ToString("yyyy-MM-ddTHH:mm:ss"),
                                Duration = $"{(int)liveDur}d 0h 0min 0s"
                            }
                        }
                    }
                }
            }
        };
        File.WriteAllText(luckyDumpPath, System.Text.Json.JsonSerializer.Serialize(luckyObj));

        // Live module also uses "Lucky Event" so the in-scope category variable matches.
        var liveModule = BuildLiveModuleWithCategory(liveStart, liveDur, "Lucky Event");
        var svc = new EventScheduleService();

        await svc.LoadAsync(luckyDumpPath, liveModule);

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));

        // Both runs are still present — non-Seasonal auto-separates silently (variant A).
        Assert.Equal(2, group.Runs.Count);
        Assert.Contains(group.Runs, r => r.Start == dumpStart);
        Assert.Contains(group.Runs, r => r.Start == liveStart);

        // No pending decisions — the dialog must NOT have been triggered.
        Assert.Empty(svc.PendingDecisions);
    }

    // ── Test 5b: Season Pass category DOES show the drift dialog ────────────────
    // Season Pass joined Seasonal Event in the drift-dialog category gate: an ambiguous re-air must
    // prompt the manual variant A/B decision (PendingDecision recorded), not auto-separate.
    [Fact]
    public async Task SeasonPassCategory_NeedsDecision_PendingDecisionRecorded()
    {
        var liveStart = new DateTime(2026, 5, 29, 8, 0, 0, DateTimeKind.Utc);
        var dumpStart = new DateTime(2026, 6, 25, 8, 0, 0, DateTimeKind.Utc); // 27d gap → NeedsDecision
        double liveDur = 5;

        // Progressions lib → category "Season Pass".
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        var obj = new
        {
            CreatedAt = "2026-06-25T00:00:00",
            Data = new
            {
                Progressions = new[]
                {
                    new
                    {
                        Name = EventName,
                        ProgressionEventId = "SP_FlashbackRewind2026",
                        DisplayName = EventName,
                        ActivableParams = new
                        {
                            IsEnabled = true,
                            Lifetime = "ScheduleBased",
                            Schedule = new
                            {
                                Start = dumpStart.ToString("yyyy-MM-ddTHH:mm:ss"),
                                Duration = $"{(int)liveDur}d 0h 0min 0s"
                            }
                        }
                    }
                }
            }
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(obj));

        var liveModule = BuildLiveModuleWithCategory(liveStart, liveDur, "Season Pass");
        var svc = new EventScheduleService();

        await svc.LoadAsync(path, liveModule);

        var group = svc.Groups.Single(g => string.Equals(g.Name, EventName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, group.Runs.Count);               // both runs kept pending the decision
        Assert.Single(svc.PendingDecisions);             // dialog WAS triggered for Season Pass
        Assert.Equal(EventName, svc.PendingDecisions[0].EventName);
    }

    // ── Test 4: PendingDecisions cleared between LoadAsync calls ────────────────

    [Fact]
    public async Task PendingDecisions_ClearedOnReload()
    {
        var liveStart  = new DateTime(2026, 5, 29, 8, 0, 0, DateTimeKind.Utc);
        var dumpStart  = new DateTime(2026, 6, 25, 8, 0, 0, DateTimeKind.Utc);
        double liveDur = 5;

        var path       = WriteDumpJson(dumpStart, liveDur);
        var liveModule = BuildLiveModule(liveStart, liveDur);
        var svc        = new EventScheduleService();

        // First load — produces a NeedsDecision entry.
        await svc.LoadAsync(path, liveModule);
        Assert.Single(svc.PendingDecisions);

        // Second load (AutoSeparate, no live module) — PendingDecisions must be cleared.
        var dumpStart2 = new DateTime(2026, 8, 15, 8, 0, 0, DateTimeKind.Utc);
        var path2 = WriteDumpJson(dumpStart2, liveDur);
        await svc.LoadAsync(path2); // no existingModuleContent
        Assert.Empty(svc.PendingDecisions);
    }
}
