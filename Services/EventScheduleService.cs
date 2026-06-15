using System.IO;
using System.Text.Json;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// One scheduled run of an event (explicit instance, all times UTC).
/// <paramref name="OnFireVariant"/> is a hand-maintained flag (set on the wiki module) marking
/// the run that carries the "On Fire" booster; it is NOT in the game dump, so the merge must
/// preserve it across regenerations.
/// </summary>
public sealed record EventScheduleRun(DateTime Start, TimeSpan Duration, string SourceId, bool OnFireVariant = false, string? Badge = null, string? Parent = null, bool Disabled = false);

/// <summary>Runs grouped under one display name (= one calendar entry / wiki page).</summary>
public sealed class EventScheduleGroup
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    /// <summary>Badge/asset handle from the dump (PrefabsOverride/AssetOverride) — reference for icon mapping; may be null.</summary>
    public string? Badge { get; set; }
    /// <summary>For sub-events (Garage Cleanup): the seasonal event this accompanies; the widget nests under it. Null otherwise.</summary>
    public string? Parent { get; set; }
    public List<EventScheduleRun> Runs { get; } = new();
}

/// <summary>
/// Parses the scheduled event libraries of events.json into a flat schedule
/// (one entry per run, grouped by display name) for Module:Datatable/Events generation.
///
/// Filtering applied during load (each decision is reported via <see cref="Notes"/>):
///  - disabled entries (ActivableParams.IsEnabled == false) whose run already ENDED are
///    kept — they are real historical runs (the config disables events after airing);
///    disabled entries with a future/ongoing run are skipped (planned but unconfirmed);
///  - placeholder runs are dropped: start exactly Jan 1 08:00 UTC shared by 2+ entries
///    of the same display name (template slots, e.g. inactive DE_* Re-Archeology rounds);
///  - identical (name, start) duplicates are merged (A/B segment variants).
/// </summary>
public class EventScheduleService
{
    public string? CreatedAt { get; private set; }
    public List<EventScheduleGroup> Groups { get; } = new();

    /// <summary>Human-readable log of skipped/merged entries and unlocalized names (for UI review).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Total runs kept across all groups.</summary>
    public int RunCount => Groups.Sum(g => g.Runs.Count);

    private sealed record LibSpec(
        string Library,
        string IdField,
        string NameField,
        Func<string, string> CategoryById,
        string? ForcedName = null,
        string? BadgeField = null);

    private static readonly LibSpec[] Libs =
    {
        // CollectibleBoards mixes CBE_/LDE_/SE_ seasonal events with LC_/LS_ Lucky events.
        // PrefabsOverride = the event's visual prefab (≈ its badge), present in the dump.
        new("CollectibleBoards", "CollectibleBoardEventId", "Name",
            id => id.StartsWith("LC_", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("LS_", StringComparison.OrdinalIgnoreCase)
                ? "Lucky Event" : "Seasonal Event", BadgeField: "PrefabsOverride"),
        new("Progressions", "ProgressionEventId", "Name", _ => "Season Pass"),
        new("ProgressionPackEvents", "ProgressionPackId", "DisplayName", _ => "Progression Pack"),
        // Garage Cleanups have one shared wiki page; per-event DisplayNames are mostly unlocalized.
        // PrefabsOverride is dumped only after the dumper extension (private prop) — null until then.
        new("GarageCleanups", "GarageCleanupEventId", "DisplayName", _ => "Garage Cleanup",
            ForcedName: "Garage Cleanup", BadgeField: "PrefabsOverride"),
        new("CoreSupportEvents", "ActivableId", "DisplayName", _ => "Core Support Event",
            BadgeField: "AssetOverride"),
        new("BoultonLeagueEvents", "EventId", "DisplayName", _ => "Leaderboard Event"),
        new("Leaderboards", "LeaderboardEventId", "Name", _ => "Leaderboard Event"),
        // DisplayName is "Terrific Tea Party" but the event/wiki page + icon use "Teatime Delight".
        new("SoloMilestoneEvents", "ConfigKey", "DisplayName", _ => "Solo Milestone",
            ForcedName: "Teatime Delight"),
    };

    /// <summary>
    /// Display-name corrections (the dump's DisplayName is wrong/internal for these).
    /// Keyed by the resolved name AFTER trim + "Season Pass - " strip.
    /// </summary>
    private static readonly Dictionary<string, string> NameOverrides = new(StringComparer.Ordinal)
    {
        ["Classic Races"] = "Hopewell Bay Horizons Cup",   // CR_Sailing_* CoreSupportEvents
    };

    /// <param name="existingModuleContent">
    /// Optional live content of <c>Module:Datatable/Events</c>. When supplied, its runs are
    /// MERGED with the dump so historical runs the game config no longer carries are preserved
    /// (Metaplay drops old scheduled runs after they air). Never overwrites — union by (name, start).
    /// </param>
    public async Task LoadAsync(string filePath, string? existingModuleContent = null)
    {
        Groups.Clear();
        Notes.Clear();
        CreatedAt = null;

        await using var stream = File.OpenRead(filePath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        if (root.TryGetProperty("CreatedAt", out var ca) && ca.ValueKind == JsonValueKind.String)
            CreatedAt = ca.GetString();

        if (!root.TryGetProperty("Data", out var data)) return;

        // 1) Collect raw runs across all libraries
        var raw = new List<(string Name, string Category, EventScheduleRun Run)>();
        var disabledFuture = new List<string>();
        var disabledPastKept = 0;
        var unlocalized = new List<string>();
        var now = DateTime.UtcNow;

        // Seasonal event id-suffix → display name, so Garage Cleanups (GC_<suffix>) can be
        // tied to the seasonal event they accompany ("Flashback Rewind Garage Cleanup").
        var seasonalBySuffix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("CollectibleBoards", out var cbs) && cbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in cbs.EnumerateArray())
            {
                var id = GetString(e, "CollectibleBoardEventId");
                var nm = GetString(e, "Name").Trim();
                var us = id.IndexOf('_');
                if (us >= 0 && us < id.Length - 1 && !string.IsNullOrEmpty(nm))
                    seasonalBySuffix[id[(us + 1)..]] = nm;
            }
        }

        foreach (var spec in Libs)
        {
            if (!data.TryGetProperty(spec.Library, out var lib) || lib.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var e in lib.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;

                if (!e.TryGetProperty("ActivableParams", out var ap) || ap.ValueKind != JsonValueKind.Object)
                    continue;
                var isEnabled = !(ap.TryGetProperty("IsEnabled", out var en) && en.ValueKind == JsonValueKind.False);
                if (!ap.TryGetProperty("Schedule", out var sched) || sched.ValueKind != JsonValueKind.Object)
                    continue;

                DateTime? start = null;
                if (sched.TryGetProperty("Start", out var st) && st.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(st.GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var dt))
                    start = dt;
                var duration = sched.TryGetProperty("Duration", out var du) && du.ValueKind == JsonValueKind.String
                    ? EventService.ParseDuration(du.GetString())
                    : null;
                if (start == null || duration == null) continue;

                var id = GetString(e, spec.IdField);
                if (string.IsNullOrEmpty(id)) id = GetString(e, "ConfigKey");
                if (string.IsNullOrEmpty(id)) id = GetString(e, "GroupId");

                // Disabled entries (IsEnabled = false) are ALL kept and flagged `disabled` so the
                // wiki can filter them with |hideDisabled=true|. Past = already-aired history;
                // future/ongoing = unconfirmed/planned. Nothing is excluded here anymore.
                if (!isEnabled)
                {
                    if (start.Value + duration.Value >= now)
                        disabledFuture.Add($"{id} ({start.Value:yyyy-MM-dd})");
                    else
                        disabledPastKept++;
                }

                // Trim: game data contains stray trailing spaces (e.g. "Pirates of Hopewell Bay ")
                var name = (spec.ForcedName ?? GetString(e, spec.NameField)).Trim();
                if (string.IsNullOrEmpty(name)) name = GetString(e, "DisplayName").Trim();
                if (string.IsNullOrEmpty(name)) name = id;

                // Cosmetic cleanup: SP_ names carry a redundant "Season Pass - " prefix
                if (name.StartsWith("Season Pass - ", StringComparison.OrdinalIgnoreCase))
                    name = name["Season Pass - ".Length..];

                // Known display-name corrections (wrong/internal name in the dump).
                if (NameOverrides.TryGetValue(name, out var corrected)) name = corrected;

                // Garage Cleanups: tie to the seasonal event they accompany via id suffix
                // (GC_Flashback2025 → "Flashback Rewind"); name them "<Parent> Garage Cleanup".
                string? parent = null;
                if (spec.Library == "GarageCleanups")
                {
                    var core = id.StartsWith("GC_", StringComparison.OrdinalIgnoreCase) ? id[3..] : id;
                    core = System.Text.RegularExpressions.Regex.Replace(core, "_\\d+$", "");
                    if (seasonalBySuffix.TryGetValue(core, out var parentName))
                    {
                        parent = parentName;
                        name = parentName + " Garage Cleanup";
                    }
                }

                // Unlocalized DisplayName = raw localization key (contains '_')
                if (spec.ForcedName == null && name.Contains('_'))
                    unlocalized.Add($"{spec.Library}/{id}: \"{name}\"");

                var badge = spec.BadgeField != null ? GetString(e, spec.BadgeField) : "";
                raw.Add((name, spec.CategoryById(id),
                    new EventScheduleRun(start.Value, duration.Value, id,
                        Badge: string.IsNullOrEmpty(badge) ? null : badge,
                        Parent: parent,
                        Disabled: !isEnabled)));
            }
        }

        if (disabledPastKept > 0)
            Notes.Add($"{disabledPastKept} disabled entries kept as historical runs (already ended), flagged disabled=true.");
        if (disabledFuture.Count > 0)
            Notes.Add($"{disabledFuture.Count} disabled future/ongoing run(s) kept & flagged (unconfirmed; hide on wiki with hideDisabled=true): {string.Join(", ", disabledFuture)}");
        if (unlocalized.Count > 0)
            Notes.Add($"{unlocalized.Count} unlocalized name(s) kept as-is: {string.Join("; ", unlocalized)}");

        // 2) Drop placeholder runs: Jan 1 08:00 UTC start shared by 2+ entries of the same name
        //    (template slots for not-yet-scheduled rounds — observed on DE_* Re-Archeology)
        var startCounts = raw
            .GroupBy(r => (r.Name, r.Run.Start))
            .ToDictionary(g => g.Key, g => g.Count());

        var kept = new List<(string Name, string Category, EventScheduleRun Run)>();
        var placeholders = 0;
        foreach (var r in raw)
        {
            var s = r.Run.Start;
            if (s.Month == 1 && s.Day == 1 && s.Hour == 8 && s.Minute == 0
                && startCounts[(r.Name, s)] >= 2)
            {
                placeholders++;
                continue;
            }
            kept.Add(r);
        }
        if (placeholders > 0)
            Notes.Add($"{placeholders} placeholder run(s) dropped (Jan 1 08:00 UTC template slots).");

        // 3) Merge identical (name, start) duplicates — A/B segment variants of the same run
        var merged = 0;
        var byKey = new Dictionary<(string, DateTime), (string Name, string Category, EventScheduleRun Run)>();
        foreach (var r in kept)
        {
            var key = (r.Name, r.Run.Start);
            if (byKey.TryGetValue(key, out var existing))
            {
                merged++;
                Notes.Add($"Duplicate run merged: \"{r.Name}\" {r.Run.Start:yyyy-MM-dd} ({existing.Run.SourceId} + {r.Run.SourceId}).");
                continue;
            }
            byKey[key] = r;
        }

        // Category per event name comes from the dump (authoritative for current events).
        var nameCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in byKey.Values)
            nameCategory.TryAdd(r.Name, r.Category);

        // 3.5) Merge historical runs from the live module (never drops anything).
        if (!string.IsNullOrWhiteSpace(existingModuleContent))
            MergeExistingModule(existingModuleContent, byKey, nameCategory);

        // 4) Group by NAME (category resolved per name so a differing category label can't
        //     split one event into two groups). Newest first: groups ordered by their most
        //     recent run descending, runs within a group descending too.
        foreach (var g in byKey.Values
                     .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Max(r => r.Run.Start))
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var category = nameCategory.GetValueOrDefault(g.Key, "");
            var group = new EventScheduleGroup { Name = g.Key, Category = category };
            group.Runs.AddRange(g.Select(r => r.Run).OrderByDescending(r => r.Start));
            // Badge from the newest run that carries one (handles re-aired events whose
            // earlier runs predate the prefab field).
            group.Badge = group.Runs.FirstOrDefault(r => !string.IsNullOrEmpty(r.Badge))?.Badge;
            group.Parent = group.Runs.FirstOrDefault(r => !string.IsNullOrEmpty(r.Parent))?.Parent;
            Groups.Add(group);
        }
    }

    /// <summary>
    /// Parses the live <c>Module:Datatable/Events</c> and folds any run it contains that the
    /// dump does NOT (by name + start instant) back into <paramref name="byKey"/>. Recurrence
    /// rules are expanded to explicit instances first. Purely-historical events (deleted from
    /// the dump) are re-introduced with their module category. Reports counts via <see cref="Notes"/>.
    /// </summary>
    private void MergeExistingModule(
        string content,
        Dictionary<(string, DateTime), (string Name, string Category, EventScheduleRun Run)> byKey,
        Dictionary<string, string> nameCategory)
    {
        var root = LuaTableReader.Parse(content);
        var eventsTbl = root?.Tbl("events");
        if (eventsTbl == null)
        {
            Notes.Add("⚠ Live Module:Datatable/Events could not be parsed — historical runs NOT merged. Do not overwrite the live module with this output.");
            return;
        }

        var preservedRuns = 0;
        var renamedSkipped = 0;

        // Every (start, duration) the dump already covers. A module run matching one is the SAME
        // airing the dump now carries under a (possibly renamed) name — skip it so a generator
        // rename (e.g. "Garage Cleanup" → "Flashback Rewind Garage Cleanup") doesn't duplicate it.
        var dumpStartDur = new HashSet<(DateTime, long)>();
        foreach (var v in byKey.Values)
            dumpStartDur.Add((v.Run.Start, v.Run.Duration.Ticks));

        foreach (var entryObj in eventsTbl.Array)
        {
            if (entryObj is not LuaTable entry) continue;
            var name = entry.Str("name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var category = entry.Str("category")?.Trim() ?? "";
            var runsTbl = entry.Tbl("runs");
            if (runsTbl == null) continue;

            var entryBadge = entry.Str("badge");
            var entryParent = entry.Str("parent");
            foreach (var runObj in runsTbl.Array)
            {
                if (runObj is not LuaTable run) continue;
                var onFire = run.Get("onFireVariant") is true;
                var runDisabled = run.Get("disabled") is true;
                foreach (var (start, duration) in ExpandModuleRun(run))
                {
                    var key = (name, start);
                    if (byKey.TryGetValue(key, out var dumpRun))
                    {
                        // Dump already has this run — carry over the hand-set On Fire flag, which
                        // the dump never carries, so regeneration doesn't wipe it.
                        if (onFire && !dumpRun.Run.OnFireVariant)
                            byKey[key] = (dumpRun.Name, dumpRun.Category, dumpRun.Run with { OnFireVariant = true });
                        continue;
                    }
                    // Same airing already in the dump under a different (renamed) name → skip.
                    if (dumpStartDur.Contains((start, duration.Ticks)))
                    {
                        renamedSkipped++;
                        continue;
                    }
                    byKey[key] = (name, category, new EventScheduleRun(start, duration, "historical", onFire,
                        string.IsNullOrEmpty(entryBadge) ? null : entryBadge,
                        string.IsNullOrEmpty(entryParent) ? null : entryParent,
                        runDisabled));
                    preservedRuns++;
                    if (!nameCategory.ContainsKey(name)) nameCategory[name] = category;
                }
            }
        }

        // Events that exist ONLY in the module (no dump run under that name = purged from config).
        var dumpNames = new HashSet<string>(
            byKey.Values.Where(r => r.Run.SourceId != "historical").Select(r => r.Name),
            StringComparer.OrdinalIgnoreCase);
        var moduleOnly = byKey.Values
            .Where(r => r.Run.SourceId == "historical" && !dumpNames.Contains(r.Name))
            .Select(r => r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (preservedRuns > 0)
            Notes.Add($"{preservedRuns} historical run(s) preserved from the live module (kept across re-airs).");
        if (renamedSkipped > 0)
            Notes.Add($"{renamedSkipped} live-module run(s) skipped as duplicates of dump runs renamed by this generation.");
        if (moduleOnly.Count > 0)
            Notes.Add($"{moduleOnly.Count} event(s) exist only in the live module (no longer in the dump): {string.Join(", ", moduleOnly)}");
    }

    /// <summary>
    /// Expands one module run table into explicit (start, duration) instances. A run with
    /// <c>intervalDays</c> is a recurrence rule expanded until <c>untilDate</c> (inclusive)
    /// or <c>count</c> occurrences; otherwise it is a single occurrence. Mirrors the Lua
    /// <c>expandEntry</c> in Module:Events.
    /// </summary>
    private static IEnumerable<(DateTime Start, TimeSpan Duration)> ExpandModuleRun(LuaTable run)
    {
        var startTbl = run.Tbl("start");
        if (startTbl == null) yield break;
        if (TryReadDate(startTbl, out var start) == false) yield break;

        var durationDays = run.Num("durationDays") ?? 0;
        var duration = TimeSpan.FromSeconds(Math.Floor(durationDays * 86400));

        var interval = run.Num("intervalDays");
        if (interval is > 0)
        {
            DateTime? until = null;
            if (run.Tbl("untilDate") is { } u && TryReadDate(u, out var ud))
                until = ud.Date.AddDays(1); // inclusive last start (matches Lua +DAY)
            var maxCount = run.Num("count");

            var t = start;
            var i = 0;
            while (true)
            {
                i++;
                if (maxCount is { } mc && i > mc) break;
                if (until is { } lim && t >= lim) break;
                if (maxCount == null && until == null) break; // runaway guard (matches Lua)
                yield return (t, duration);
                t = t.AddDays(interval.Value);
            }
        }
        else
        {
            yield return (start, duration);
        }
    }

    private static bool TryReadDate(LuaTable t, out DateTime dt)
    {
        dt = default;
        var y = t.Num("year"); var m = t.Num("month"); var d = t.Num("day");
        if (y == null || m == null || d == null) return false;
        var h = (int)(t.Num("hour") ?? 0);
        var min = (int)(t.Num("min") ?? 0);
        try
        {
            dt = new DateTime((int)y, (int)m, (int)d, h, min, 0, DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static string GetString(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
    }
}
