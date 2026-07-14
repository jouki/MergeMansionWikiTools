using System;
using System.Collections.Generic;
using System.Linq;

namespace MergeMansionWikiTools.Services;

/// <summary>One whole event added or removed between the old and new module (with run count/date range).</summary>
public sealed record EventsEventChange(string Name, int RunCount, DateTime? FirstStart, DateTime? LastStart);

/// <summary>One run occurrence added or removed on an event that exists on both sides.</summary>
public sealed record EventsRunChange(string EventName, DateTime Start, TimeSpan Duration);

/// <summary>A per-event (or per-run, when <see cref="RunStart"/> is set) field value change.</summary>
public sealed record EventsFieldChange(string EventName, string Field, string OldValue, string NewValue, DateTime? RunStart = null);

/// <summary>
/// Semantic diff of <c>Module:Datatable/Events</c> between the live wiki version and the freshly
/// generated one. Produced by <see cref="EventScheduleDiff.Compute"/> and rendered in the
/// "Update Events Data on Wiki" dialog so the user can review the actual schedule changes (new events,
/// new runs, field changes such as the Garage-Cleanup <c>parent</c>) instead of a static message.
/// Describes ONLY the Events module — Garage Cleanup GRID changes (Module:Datatable/Various) have their
/// own change list, so there is no double-counting.
/// </summary>
public sealed class EventsChangeSet
{
    public List<EventsEventChange> NewEvents { get; } = new();
    public List<EventsEventChange> RemovedEvents { get; } = new();
    public List<EventsRunChange> NewRuns { get; } = new();
    public List<EventsRunChange> RemovedRuns { get; } = new();
    public List<EventsFieldChange> FieldChanges { get; } = new();

    /// <summary>The old module was absent/empty/unparseable — everything is reported as new and the
    /// raw text diff is meaningless (there is nothing to diff against).</summary>
    public bool OldModuleMissing { get; set; }

    public bool HasChanges =>
        NewEvents.Count + RemovedEvents.Count + NewRuns.Count + RemovedRuns.Count + FieldChanges.Count > 0;
}

/// <summary>
/// Computes the semantic change set between two <c>Module:Datatable/Events</c> Lua texts. Pure and
/// UI/IO-free — parses both sides with <see cref="LuaTableReader"/> and reuses
/// <see cref="EventScheduleService.ExpandModuleRun"/> so run/recurrence handling matches the merge exactly.
/// </summary>
public static class EventScheduleDiff
{
    /// <summary>Per-event snapshot: event-level string fields + expanded run occurrences with their flags.</summary>
    private sealed class Snap
    {
        public string Parent = "";
        public string Category = "";
        public string Badge = "";
        public string Prefix = "";
        // start instant → (duration, disabled flag, weekType). One entry per expanded occurrence.
        public Dictionary<DateTime, (TimeSpan Dur, bool Disabled, string? WeekType)> Runs { get; } = new();
    }

    public static EventsChangeSet Compute(string? oldLua, string newLua)
    {
        var cs = new EventsChangeSet();
        var newMap = Parse(newLua);

        // Old module absent/empty/unparseable → treat everything as new, hide the raw diff.
        if (string.IsNullOrWhiteSpace(oldLua) || LuaTableReader.Parse(oldLua)?.Tbl("events") == null)
        {
            cs.OldModuleMissing = true;
            foreach (var (name, s) in newMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                cs.NewEvents.Add(ToEventChange(name, s));
            return cs;
        }

        var oldMap = Parse(oldLua);

        foreach (var (name, ns) in newMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!oldMap.TryGetValue(name, out var os))
            {
                cs.NewEvents.Add(ToEventChange(name, ns));
                continue;
            }

            AddFieldChange(cs, name, "parent", os.Parent, ns.Parent);
            AddFieldChange(cs, name, "category", os.Category, ns.Category);
            AddFieldChange(cs, name, "badge", os.Badge, ns.Badge);
            AddFieldChange(cs, name, "prefix", os.Prefix, ns.Prefix);

            foreach (var (start, r) in ns.Runs.OrderBy(kv => kv.Key))
            {
                if (!os.Runs.TryGetValue(start, out var o))
                {
                    cs.NewRuns.Add(new EventsRunChange(name, start, r.Dur));
                    continue;
                }
                if (o.Disabled != r.Disabled)
                    cs.FieldChanges.Add(new EventsFieldChange(name, "disabled",
                        o.Disabled ? "disabled" : "enabled", r.Disabled ? "disabled" : "enabled", start));
                var owt = o.WeekType ?? "";
                var nwt = r.WeekType ?? "";
                if (!string.Equals(owt, nwt, StringComparison.Ordinal))
                    cs.FieldChanges.Add(new EventsFieldChange(name, "weekType", owt, nwt, start));
            }

            foreach (var (start, r) in os.Runs.OrderBy(kv => kv.Key))
                if (!ns.Runs.ContainsKey(start))
                    cs.RemovedRuns.Add(new EventsRunChange(name, start, r.Dur));
        }

        foreach (var (name, os) in oldMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            if (!newMap.ContainsKey(name))
                cs.RemovedEvents.Add(ToEventChange(name, os));

        return cs;
    }

    private static void AddFieldChange(EventsChangeSet cs, string name, string field, string oldVal, string newVal)
    {
        if (!string.Equals(oldVal ?? "", newVal ?? "", StringComparison.Ordinal))
            cs.FieldChanges.Add(new EventsFieldChange(name, field, oldVal ?? "", newVal ?? ""));
    }

    private static EventsEventChange ToEventChange(string name, Snap s)
    {
        DateTime? first = s.Runs.Count > 0 ? s.Runs.Keys.Min() : null;
        DateTime? last = s.Runs.Count > 0 ? s.Runs.Keys.Max() : null;
        return new EventsEventChange(name, s.Runs.Count, first, last);
    }

    private static Dictionary<string, Snap> Parse(string? lua)
    {
        var map = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
        var events = LuaTableReader.Parse(lua)?.Tbl("events");
        if (events == null) return map;

        foreach (var o in events.Array)
        {
            if (o is not LuaTable e) continue;
            var name = e.Str("name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            // One entry per name is expected (the generator groups by name); if a name repeats, the
            // last entry's fields win and its runs are merged in.
            if (!map.TryGetValue(name, out var snap))
                map[name] = snap = new Snap();
            snap.Parent = e.Str("parent")?.Trim() ?? "";
            snap.Category = e.Str("category")?.Trim() ?? "";
            snap.Badge = e.Str("badge")?.Trim() ?? "";
            snap.Prefix = e.Str("prefix")?.Trim() ?? "";

            var runs = e.Tbl("runs");
            if (runs == null) continue;
            foreach (var ro in runs.Array)
            {
                if (ro is not LuaTable r) continue;
                var disabled = r.Get("disabled") is true;
                var weekType = r.Str("weekType");
                foreach (var (start, dur) in EventScheduleService.ExpandModuleRun(r))
                    snap.Runs[start] = (dur, disabled, weekType);
            }
        }
        return map;
    }
}
