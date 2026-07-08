using System.IO;
using System.Linq;
using System.Text.Json;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// One scheduled run of an event (explicit instance, all times UTC).
/// <paramref name="OnFireVariant"/> is a hand-maintained flag (set on the wiki module) marking
/// the run that carries the "On Fire" booster; it is NOT in the game dump, so the merge must
/// preserve it across regenerations.
/// </summary>
public sealed record EventScheduleRun(DateTime Start, TimeSpan Duration, string SourceId, bool OnFireVariant = false, string? Badge = null, string? Parent = null, bool Disabled = false, string? IdenticalTo = null, string? Prefix = null, string? WeekType = null);

/// <summary>
/// A live-module run that the drift classifier cannot auto-resolve: the dump has a run for the
/// same event that starts beyond the existing run's duration but within the separate threshold.
/// A human must decide whether to treat it as the same run (update) or a new separate run.
/// </summary>
public sealed record EventDriftDecision(string EventName, DateTime ExistingStart, DateTime NewStart, double ExistingDurationDays);

/// <summary>Runs grouped under one display name (= one calendar entry / wiki page).</summary>
public sealed class EventScheduleGroup
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    /// <summary>Badge/asset handle from the dump (PrefabsOverride/AssetOverride) — reference for icon mapping; may be null.</summary>
    public string? Badge { get; set; }
    /// <summary>For sub-events (Garage Cleanup): the seasonal event this accompanies; the widget nests under it. Null otherwise.</summary>
    public string? Parent { get; set; }
    /// <summary>
    /// Event-id family prefix (CollectibleBoards only: "CBE"/"LDE"/"LC"/"LS"/"SE"), derived from the
    /// game id before the first '_'. Emitted to Module:Datatable/Events so Lua (Module:DailyScoop
    /// fallback resolution) can match Daily Scoop tasks whose params are ID-PREFIX filters
    /// (e.g. CompleteNCollectibleBoardEventMilestone param "CBE_" matches CBE_* events only,
    /// NOT LDE_*). Preserved by the merge on historical entries. Null for other libraries.
    /// </summary>
    public string? Prefix { get; set; }
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

    /// <summary>
    /// Live-module runs that the drift classifier could not auto-resolve (NeedsDecision verdict).
    /// Populated during <see cref="LoadAsync"/> when <c>existingModuleContent</c> is supplied.
    /// Cleared at the start of each <see cref="LoadAsync"/> call.
    /// </summary>
    public List<EventDriftDecision> PendingDecisions { get; } = new();


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
        new("ProgressionPackEvents", "ProgressionPackId", "Name", _ => "Progression Pack"),
        // NOTE (A3b): Garage Cleanups are NO LONGER emitted by this pass-1 build. GC run history is
        // produced separately (GarageCleanupGridService.BuildGcEventGroups) and merged into the Events
        // module in the GC pass, so this service stays NON-GC only. The live-module GC entries are also
        // excluded from MergeExistingModule (see the category guard there).
        new("CoreSupportEvents", "ActivableId", "Name", _ => "Core Support Event",
            BadgeField: "AssetOverride"),
        // Mix a Booster (MixABoost_*) — a recurring CoreSupport-style booster-mixing event.
        // The dumper resolves the in-game title into Name ("Mix a Booster"); id = ConfigKey. The event
        // config has no icon ref, so the dumper hardcodes the generic badge into AssetOverride
        // ("MAB_MHB_Generic" → MAB_MHB_Generic.png).
        new("MixABoosterEvents", "ConfigKey", "Name", _ => "Core Support Event", BadgeField: "AssetOverride"),
        new("BoultonLeagueEvents", "EventId", "Name", _ => "Leaderboard Event"),
        new("Leaderboards", "LeaderboardEventId", "Name", _ => "Leaderboard Event"),
        // Card Collection Supporting Events (CCSE_*) — weekly "Clue Rush". Per-event NameLocId/
        // DisplayName are stubs; the dumper resolves the shared title "Clue Rush" into Name.
        new("CardCollectionSupportingEvents", "ConfigKey", "Name", _ => "Card Collection Event"),
        // DisplayName is "Terrific Tea Party" but the event/wiki page + icon use "Teatime Delight".
        new("SoloMilestoneEvents", "ConfigKey", "Name", _ => "Solo Milestone",
            ForcedName: "Teatime Delight"),
    };

    /// <summary>
    /// Display-name corrections (the dump's DisplayName is wrong/internal for these).
    /// Keyed by the resolved name AFTER trim + "Season Pass - " strip.
    /// </summary>
    private static readonly Dictionary<string, string> NameOverrides = new(StringComparer.Ordinal)
    {
        // (empty) DigEvents ("Maddie's Re-Archaeology") and ClassicRaces ("Hopewell Bay Horizons
        // Cup") used to be corrected here; both are now resolved at the dumper level (EventDumper
        // resolves the real loc key {ConfigKey-without-round}_Name, with DE_Generic_Name fallback),
        // so events.json already carries the correct DisplayName. Add an entry only for a name the
        // dumper cannot resolve.
    };

    /// <summary>
    /// Event categories whose ambiguous re-air (drift classifier returns NeedsDecision) prompts the
    /// manual drift dialog (variant A = two separate runs / variant B = same run, updated). Every
    /// other category auto-separates silently (variant A). Seasonal Events and Season Passes are
    /// curated wiki pages where a human must judge whether a shifted re-air is the same event moved
    /// or a distinct airing; recurring/utility categories (Lucky Event, Progression Pack, …) are not.
    /// </summary>
    private static readonly HashSet<string> DriftDialogCategories =
        new(StringComparer.OrdinalIgnoreCase) { "Seasonal Event", "Season Pass" };

    /// <param name="existingModuleContent">
    /// Optional live content of <c>Module:Datatable/Events</c>. When supplied, its runs are
    /// MERGED with the dump so historical runs the game config no longer carries are preserved
    /// (Metaplay drops old scheduled runs after they air). Never overwrites — union by (name, start).
    /// </param>
    public async Task LoadAsync(string filePath, string? existingModuleContent = null)
    {
        Groups.Clear();
        Notes.Clear();
        PendingDecisions.Clear();
        CreatedAt = null;

        await using var stream = File.OpenRead(filePath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        if (root.TryGetProperty("CreatedAt", out var ca) && ca.ValueKind == JsonValueKind.String)
            CreatedAt = ca.GetString();

        if (!root.TryGetProperty("Data", out var data)) return;

        // Solo Milestone milestones that grant the On Fire booster — used to AUTO-flag the booster
        // week of Teatime Delight (onFireVariant). A milestone qualifies if any of its Rewards is a
        // "RewardOnFire" (e.g. MySummerTea_19_Onfire). A run is a booster week if any of its
        // Milestones is in this set.
        var onFireMilestones = new HashSet<string>(StringComparer.Ordinal);
        if (data.TryGetProperty("SoloMilestoneMilestones", out var smm) && smm.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in smm.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object
                    || !m.TryGetProperty("Rewards", out var rw) || rw.ValueKind != JsonValueKind.Array)
                    continue;
                bool grantsOnFire = rw.EnumerateArray().Any(r =>
                    r.ValueKind == JsonValueKind.Object && r.TryGetProperty("RewardOnFire", out _));
                if (grantsOnFire && m.TryGetProperty("ConfigKey", out var ck) && ck.ValueKind == JsonValueKind.String)
                    onFireMilestones.Add(ck.GetString());
            }
        }

        // 1) Collect raw runs across all libraries
        var raw = new List<(string Name, string Category, EventScheduleRun Run)>();
        var disabledFuture = new List<string>();
        var disabledPastKept = 0;
        var unlocalized = new List<string>();
        var recurringEntries = 0;
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
                // Prefer an explicit numeric Lifetime (milliseconds) — some events (e.g. Clue Rush /
                // CCSE) schedule a 3d window but the actual active lifetime is shorter (1d). When
                // Lifetime is "ScheduleBased" (a string) fall back to the schedule's Duration.
                TimeSpan? duration = null;
                if (ap.TryGetProperty("Lifetime", out var lt) && lt.ValueKind == JsonValueKind.Number)
                    duration = TimeSpan.FromMilliseconds(lt.GetDouble());
                else if (sched.TryGetProperty("Duration", out var du) && du.ValueKind == JsonValueKind.String)
                    duration = EventService.ParseDuration(du.GetString());
                if (start == null || duration == null) continue;

                // Recurring schedule: the config stores ONE anchor Start + a Recurrence period
                // (MetaCalendarPeriod, e.g. "14d 0h 0min 0s") + optional NumRepeats (total occurrences).
                // Mix a Booster recurs every 14d unbounded; Lucky Catch/Snap Summer recur every 14d ×7.
                // The dump only carries the anchor, so without expanding the recent/current run never
                // appears. (Pre-fix the period serialized as a bare type name and was lost — see the
                // BaseMetaJsonSerializer.CanConvert nullable fix.)
                string? recurrence = sched.TryGetProperty("Recurrence", out var rc) && rc.ValueKind == JsonValueKind.String
                    ? rc.GetString() : null;
                int? numRepeats = sched.TryGetProperty("NumRepeats", out var nr) && nr.ValueKind == JsonValueKind.Number
                    ? nr.GetInt32() : null;

                var id = GetString(e, spec.IdField);
                if (string.IsNullOrEmpty(id)) id = GetString(e, "ConfigKey");
                if (string.IsNullOrEmpty(id)) id = GetString(e, "GroupId");

                // DailyChallenges_* / DailyTasks are internal daily-task CoreSupportEvents (Name=None,
                // so the display name falls back to the raw id) — NOT user-facing events: no wiki page,
                // they render as red raw codenames in the widget. Skip them at the source. (The live-
                // module merge has a matching guard for any that already leaked into the module.)
                if (id.StartsWith("DailyChallenges", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("DailyTasks", StringComparison.OrdinalIgnoreCase))
                    continue;

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

                // Id-family prefix (CBE_/LDE_/LC_/LS_/SE_) — only CollectibleBoards ids form these
                // families; other libraries' ConfigKeys would yield meaningless prefixes.
                string? prefix = null;
                if (spec.Library == "CollectibleBoards")
                {
                    var us = id.IndexOf('_');
                    if (us > 0) prefix = id[..us];
                }

                // Auto-detect the On Fire booster week (Teatime Delight): any of the event's
                // Milestones grants a RewardOnFire. Other libraries have no Milestones → false.
                bool onFireVariant = onFireMilestones.Count > 0
                    && e.TryGetProperty("Milestones", out var ms) && ms.ValueKind == JsonValueKind.Array
                    && ms.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && onFireMilestones.Contains(x.GetString()));

                var category = spec.CategoryById(id);
                var occurrences = ExpandRecurrence(start.Value, recurrence, numRepeats, now.AddDays(RecurrenceHorizonDays)).ToList();
                if (occurrences.Count > 1) recurringEntries++;
                foreach (var occStart in occurrences)
                    raw.Add((name, category,
                        new EventScheduleRun(occStart, duration.Value, id,
                            OnFireVariant: onFireVariant,
                            Badge: string.IsNullOrEmpty(badge) ? null : badge,
                            Parent: parent,
                            Disabled: !isEnabled,
                            Prefix: prefix)));
            }
        }

        if (recurringEntries > 0)
            Notes.Add($"{recurringEntries} recurring event(s) expanded into explicit occurrences from their Recurrence period (e.g. Mix a Booster, Lucky Catch/Snap Summer — every 14d).");
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

        // 3) Merge identical (name, start) duplicates — A/B segment variants of the same run.
        //    Applies ONLY to the exact same start instant (re-airs have a different start and are
        //    never merged). The ENABLED variant wins: A/B tests ship an enabled A + disabled B with
        //    the same schedule (e.g. Legacy Lane CBE_AmeliaBoulton2024 + …2024B), and which one the
        //    dump lists first is arbitrary — without this the run could be flagged disabled (and
        //    hidden by the wiki calendar) even though the event aired.
        var merged = 0;
        var byKey = new Dictionary<(string, DateTime), (string Name, string Category, EventScheduleRun Run)>();
        foreach (var r in kept)
        {
            var key = (r.Name, r.Run.Start);
            if (byKey.TryGetValue(key, out var existing))
            {
                merged++;
                Notes.Add($"Duplicate run merged: \"{r.Name}\" {r.Run.Start:yyyy-MM-dd} ({existing.Run.SourceId} + {r.Run.SourceId}).");
                if (existing.Run.Disabled && !r.Run.Disabled)
                    byKey[key] = r;   // enabled variant supersedes the disabled A/B twin
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
            group.Prefix = group.Runs.FirstOrDefault(r => !string.IsNullOrEmpty(r.Prefix))?.Prefix;
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
        var driftPreDecided = 0;

        // Every (start, duration) the dump already covers → the dump names carrying it. A module run
        // matching one is the SAME airing the dump now carries under a (possibly renamed) name — skip
        // it so a generator rename (e.g. "Garage Cleanup" → "Flashback Rewind Garage Cleanup") doesn't
        // duplicate it. The names are kept so the skip can require RELATED names: two DIFFERENT events
        // that merely share a start instant + duration (batch 08:00 scheduling, standard 3d/5d lengths)
        // must NOT eat each other's history — only a rename (one name contains the other) qualifies.
        var dumpStartDur = new Dictionary<(DateTime, long), List<string>>();
        foreach (var v in byKey.Values)
        {
            var sd = (v.Run.Start, v.Run.Duration.Ticks);
            if (!dumpStartDur.TryGetValue(sd, out var names)) dumpStartDur[sd] = names = new List<string>();
            names.Add(v.Name);
        }

        foreach (var entryObj in eventsTbl.Array)
        {
            if (entryObj is not LuaTable entry) continue;
            var name = entry.Str("name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var category = entry.Str("category")?.Trim() ?? "";
            // A3b: Garage Cleanup runs are owned by the GC pass (BuildGcEventGroups), not pass-1 — never
            // re-introduce GC entries from the live module here, or they'd duplicate the GC-pass output.
            if (string.Equals(category, "Garage Cleanup", StringComparison.Ordinal)) continue;
            // DailyChallenges_* / DailyTasks are internal daily-task configs, NOT user-facing events:
            // no wiki page (they render as raw red codenames in the widget). They leaked into the live
            // module from an external edit; the schedule pass never emits them (not in Libs). Drop them
            // here so a regen purges them from Module:Datatable/Events. (Re-enable later by removing this.)
            if (name.StartsWith("DailyChallenges", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DailyTasks", StringComparison.OrdinalIgnoreCase)
                || category.StartsWith("DailyChallenges", StringComparison.OrdinalIgnoreCase)) continue;
            var runsTbl = entry.Tbl("runs");
            if (runsTbl == null) continue;

            var entryBadge = entry.Str("badge");
            var entryParent = entry.Str("parent");
            var entryPrefix = entry.Str("prefix");

            // All run starts this event ALREADY has on the wiki (recurrences expanded). If a dump run
            // that looks like a forward drift of one live run ALSO already exists here as its own run,
            // the user has effectively already chosen "separate" (both runs coexist on the wiki) — so
            // the drift decision is already made; don't pop the dialog again (see the drift block).
            var liveStarts = new HashSet<DateTime>();
            foreach (var runObj in runsTbl.Array)
                if (runObj is LuaTable lr)
                    foreach (var (s, _) in ExpandModuleRun(lr))
                        liveStarts.Add(s);

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
                    // Same airing already in the dump under a RELATED (renamed) name → skip. Unrelated
                    // names sharing the slot are a coincidence (e.g. a historical "Voyance's Visions"
                    // run vs a dump "Summer Lucky Catch" run, both 08:00 + 3d) — keep those.
                    if (dumpStartDur.TryGetValue((start, duration.Ticks), out var slotNames)
                        && slotNames.Any(dn =>
                            dn.Contains(name, StringComparison.OrdinalIgnoreCase)
                            || name.Contains(dn, StringComparison.OrdinalIgnoreCase)))
                    {
                        renamedSkipped++;
                        continue;
                    }

                    // Drift handling: check whether any dump run for the same event name is a "drift"
                    // of this live run (i.e. the dump's start shifted slightly from the module's start).
                    // AutoUpdate  → the dump run supersedes this live run; skip adding it as historical.
                    // NeedsDecision → ambiguous; keep both and record for human review.
                    // AutoSeparate (or no dump run for this event) → fall through and add normally.
                    //
                    // A dump run is a drift candidate of this live run when it is the SAME airing nudged
                    // in time. Two cases:
                    //   (a) FORWARD — dump start is at or after the live start (re-air pushed later).
                    //   (b) BACKWARD-OVERLAP — dump start is slightly BEFORE the live start but their
                    //       [start, start+dur] intervals still OVERLAP (devs moved the start a day or two
                    //       earlier; e.g. Season Pass "Green with Envy" 08-08 → 08-07, both 21d). Without
                    //       this the live 08-08 run would be kept AND the dump 08-07 added = two runs.
                    // A far-earlier prior-year run does NOT overlap (its end is still long before the live
                    // start), so it stays excluded — preserving the guard against false-supersede (the
                    // classifier returns AutoUpdate for ANY earlier start, so a non-overlapping earlier run
                    // must never reach it).
                    var dumpRunsForEvent = byKey
                        .Where(kv => string.Equals(kv.Key.Item1, name, StringComparison.OrdinalIgnoreCase)
                                  && kv.Value.Run.SourceId != "historical"
                                  && (kv.Value.Run.Start >= start
                                      || (kv.Value.Run.Start < start
                                          && start < kv.Value.Run.Start + kv.Value.Run.Duration)))
                        .Select(kv => kv.Value.Run)
                        .ToList();

                    bool superseded = false;
                    bool needsDecision = false;
                    EventScheduleRun? nearestDumpRun = null;
                    double liveRunDurationDays = duration.TotalDays;

                    foreach (var candidateDumpRun in dumpRunsForEvent)
                    {
                        var verdict = EventDriftClassifier.Classify(start, liveRunDurationDays, candidateDumpRun.Start);
                        if (verdict == DriftVerdict.AutoUpdate)
                        {
                            superseded = true;
                            break;
                        }
                        if (verdict == DriftVerdict.NeedsDecision)
                        {
                            needsDecision = true;
                            nearestDumpRun = candidateDumpRun;
                        }
                    }

                    if (superseded)
                        continue; // live run was superseded by the dump's drifted run

                    if (needsDecision && nearestDumpRun != null)
                    {
                        if (liveStarts.Contains(nearestDumpRun.Start))
                        {
                            // The dump's drift-candidate run is ALREADY on the wiki as its own run, so
                            // "separate" was decided in a previous regeneration — both runs already
                            // coexist. Keep both (fall through), no dialog.
                            driftPreDecided++;
                        }
                        // Only Seasonal Event + Season Pass categories show the drift-decision dialog;
                        // every other category (Lucky Event, Progression Pack, Core Support Event, …)
                        // always auto-separates silently (variant A = keep both runs, no dialog).
                        else if (DriftDialogCategories.Contains(category))
                        {
                            var decision = new EventDriftDecision(name, start, nearestDumpRun.Start, liveRunDurationDays);
                            if (!PendingDecisions.Any(d =>
                                    string.Equals(d.EventName, decision.EventName, StringComparison.OrdinalIgnoreCase)
                                    && d.ExistingStart == decision.ExistingStart
                                    && d.NewStart == decision.NewStart))
                                PendingDecisions.Add(decision);
                        }
                        // fall through: both runs are kept (human will decide later for Seasonal; already kept for others)
                    }

                    byKey[key] = (name, category, new EventScheduleRun(start, duration, "historical", onFire,
                        string.IsNullOrEmpty(entryBadge) ? null : entryBadge,
                        string.IsNullOrEmpty(entryParent) ? null : entryParent,
                        runDisabled,
                        Prefix: string.IsNullOrEmpty(entryPrefix) ? null : entryPrefix));
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
        if (driftPreDecided > 0)
            Notes.Add($"{driftPreDecided} drift decision(s) auto-resolved as separate (both runs already on the wiki — no dialog).");
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

    /// <summary>How far into the future an unbounded recurrence (NumRepeats null, e.g. Mix a Booster)
    /// is expanded. Past occurrences are always emitted in full; the live-module merge then preserves
    /// them across regenerations.</summary>
    private const int RecurrenceHorizonDays = 180;

    private static readonly System.Text.RegularExpressions.Regex PeriodToken =
        new(@"(\d+)\s*(y|mo|d|h|min|s)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Expands a recurring schedule into explicit occurrence start instants. The config stores one
    /// anchor <paramref name="start"/> + a <paramref name="recurrence"/> period (MetaCalendarPeriod,
    /// e.g. "14d 0h 0min 0s") + optional <paramref name="numRepeats"/> (TOTAL occurrence count, matching
    /// the Lua <c>count</c> convention). Bounded series emit exactly NumRepeats occurrences; unbounded
    /// series (NumRepeats null) stop once past <paramref name="horizon"/>. Stepping is calendar-aware
    /// (AddMonths/AddYears) so month/year periods stay correct. A null/empty/zero period yields the
    /// single anchor (non-recurring). A safety cap prevents runaway on malformed data.
    /// </summary>
    private static IEnumerable<DateTime> ExpandRecurrence(DateTime start, string? recurrence, int? numRepeats, DateTime horizon)
    {
        var (y, mo, d, h, min, s) = ParsePeriod(recurrence);
        if (y == 0 && mo == 0 && d == 0 && h == 0 && min == 0 && s == 0)
        {
            yield return start;
            yield break;
        }
        const int SafetyCap = 1000;
        var t = start;
        for (var i = 0; i < SafetyCap; i++)
        {
            if (numRepeats is { } n && i >= n) break;     // bounded: NumRepeats total occurrences
            if (numRepeats == null && t > horizon) break; // unbounded: stop past the horizon
            yield return t;
            t = t.AddYears(y).AddMonths(mo).AddDays(d).AddHours(h).AddMinutes(min).AddSeconds(s);
        }
    }

    /// <summary>Parses a MetaCalendarPeriod string ("14d 0h 0min 0s", "2mo", "1y 6mo") into its parts.
    /// Tokens: <c>y</c> years, <c>mo</c> months, <c>d</c> days, <c>h</c> hours, <c>min</c> minutes,
    /// <c>s</c> seconds (matching the dumper's MetaJsonSerializer.SerializePeriod output).</summary>
    private static (int y, int mo, int d, int h, int min, int s) ParsePeriod(string? period)
    {
        int y = 0, mo = 0, d = 0, h = 0, min = 0, s = 0;
        if (string.IsNullOrWhiteSpace(period)) return (0, 0, 0, 0, 0, 0);
        foreach (System.Text.RegularExpressions.Match m in PeriodToken.Matches(period))
        {
            if (!int.TryParse(m.Groups[1].Value, out var n)) continue;
            switch (m.Groups[2].Value)
            {
                case "y": y = n; break;
                case "mo": mo = n; break;
                case "d": d = n; break;
                case "h": h = n; break;
                case "min": min = n; break;
                case "s": s = n; break;
            }
        }
        return (y, mo, d, h, min, s);
    }

    /// <summary>Applies a resolved drift decision to the built schedule. Variant B (isUpdate=true) treats
    /// the re-air as the SAME run that moved: the old run at <c>ExistingStart</c> is removed (superseded),
    /// leaving only the new run. Variant A (false) keeps both runs (no change). Safe to call when the run
    /// is already absent.</summary>
    public void ApplyDriftDecision(EventDriftDecision d, bool isUpdate)
    {
        if (!isUpdate) return;
        var group = Groups.FirstOrDefault(g => string.Equals(g.Name, d.EventName, StringComparison.OrdinalIgnoreCase));
        group?.Runs.RemoveAll(r => r.Start == d.ExistingStart);
    }
}
