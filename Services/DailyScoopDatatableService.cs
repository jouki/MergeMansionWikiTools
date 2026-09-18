using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Renders <c>daily_scoop.json</c> (written by <see cref="DailyScoopExtractor"/>) into
/// <c>Module:Datatable/DailyScoop</c> — the task lists of all four week types, all seven days and
/// all five player segments, as consumed by <c>Module:DailyScoop</c>.
/// <para>
/// This replaces the <c>tools/daily_scoop_datatable_gen.py</c> bridge, which ran off a
/// <c>DumpHarness --probe-daily-challenges</c> text dump and had the week ids hard-coded without a
/// revision suffix — so from the 2026-08-31 week on it kept publishing the superseded task set while
/// the game served the <c>_v2</c> one (user report: "Merge 150 times" in game, "Complete 6 Tasks" on
/// the wiki). The emitted shape is unchanged, so <c>Module:DailyScoop</c> needs no edit.
/// </para>
/// </summary>
public sealed class DailyScoopDatatableService
{
    /// <summary>Wiki module this service writes.</summary>
    public const string ModuleTitle = "Module:Datatable/DailyScoop";

    /// <summary>Issues hit while rendering; surfaced next to the extractor's own warnings.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Number of primary tasks emitted (the card/dialog summary line).</summary>
    public int TaskCount { get; private set; }

    /// <summary>Number of distinct fallback definitions emitted.</summary>
    public int FallbackCount { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads <c>daily_scoop.json</c>; returns null when the file is absent or unreadable.</summary>
    public static DailyScoopDump? Load(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<DailyScoopDump>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[DailyScoop] {DailyScoopExtractor.FileName} unreadable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Builds the full module text. <paramref name="header"/> is prepended verbatim.</summary>
    public string Build(DailyScoopDump dump, string header)
    {
        ArgumentNullException.ThrowIfNull(dump);
        Warnings.Clear();
        Warnings.AddRange(dump.Warnings);
        TaskCount = 0;

        var segs = dump.Segments;
        var sb = new StringBuilder();
        sb.Append(header);
        sb.AppendLine($"-- {ModuleTitle}");
        sb.AppendLine("-- Data for The Daily Scoop (DailyChallenges V2) — task lists of all week types, days and segments.");
        sb.AppendLine("-- GENERATED from game data. DO NOT EDIT BY HAND — changes will be overwritten by the");
        sb.AppendLine("-- next regeneration (Wiki Data Parser: Generate Events). Rendered by Module:DailyScoop.");
        sb.AppendLine("--");
        sb.AppendLine("-- The game occasionally re-cuts the whole week set, keeping the milestones and rewards but");
        sb.AppendLine("-- replacing the daily tasks. Only the set the live schedule points at is published here;");
        sb.AppendLine($"-- this one is \"{(dump.Revision.Length == 0 ? "(original)" : dump.Revision)}\", read from {(dump.SelectedFrom.Length == 0 ? "the schedule" : dump.SelectedFrom)}.");
        sb.AppendLine("--");
        sb.AppendLine("-- Shape:");
        sb.AppendLine("--   segments      -- segment keys; their order = order of values in every goals/points array");
        sb.AppendLine("--   weeks[]       -- { key, name, days[7] = { reward = \"daily\"|\"weekly\", reqCompleted, tasks[], bonus } }");
        sb.AppendLine("--   task          -- { type, params?, minutes?, goals, points, reward?, fallbacks?, event?, variants? }");
        sb.AppendLine("--                    goals/points: scalar when equal across segments, else per-segment array");
        sb.AppendLine("--                    fallbacks: one list when equal, else { seg = {...} } map");
        sb.AppendLine("--                    event: id-family prefix of the event that must run for the task to appear");
        sb.AppendLine("--                    variants: { seg = { type, params?, minutes? } } only where the task TYPE differs");
        sb.AppendLine("--   fallbackTasks -- { id = { type, params?, minutes?, goals, points, reward? } } (referenced only)");
        sb.AppendLine("--   reward        -- {\"coins\",n} | {\"energy\",n} | {\"gems\",n} | {\"env\",stars} | {\"timed\",type,dur} | {\"item\",id,n}");
        sb.AppendLine("return {");
        sb.AppendLine($"\tsegments = {{ {string.Join(", ", segs.Select(s => Q(s.Key)))} }},");
        sb.AppendLine($"\tsegmentIds = {{ {string.Join(", ", segs.Select(s => $"{s.Key} = {Q(s.Id)}"))} }},");
        sb.AppendLine("\tweeks = {");

        var usedFallbacks = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var week in dump.Weeks)
        {
            sb.AppendLine($"\t\t{{ key = {Q(week.Key)}, name = {Q(week.Name)}, days = {{");
            // Driven by the anchor segment's own day list rather than a hard-coded 7, so a week the
            // game ever cuts short renders what it has instead of seven "missing day" warnings.
            var dayCount = week.Days.TryGetValue(segs[0].Key, out var anchorDayIds) ? anchorDayIds.Count : 0;
            if (dayCount != 7) Warnings.Add($"{week.Type}: {dayCount} day(s) instead of 7");
            for (var d = 0; d < dayCount; d++)
            {
                // Per-segment day rows for day d; the first segment is the anchor every
                // per-segment lookup is positional against.
                var dayBySeg = new Dictionary<string, DailyScoopDump.DayEntry>(StringComparer.Ordinal);
                foreach (var s in segs)
                {
                    if (!week.Days.TryGetValue(s.Key, out var ids) || d >= ids.Count) continue;
                    if (dump.Days.TryGetValue(ids[d], out var day)) dayBySeg[s.Key] = day;
                }
                if (!dayBySeg.TryGetValue(segs[0].Key, out var anchorDay))
                {
                    Warnings.Add($"{week.Type} day {d + 1}: no data for anchor segment {segs[0].Key}");
                    continue;
                }
                if (dayBySeg.Values.Select(x => x.Reward).Distinct(StringComparer.Ordinal).Count() > 1)
                    Warnings.Add($"{week.Type} day {d + 1}: day box differs per segment");

                var reqCompleted = CollapseScalar(segs, k => Num(dayBySeg.TryGetValue(k, out var x) ? x.ReqCompleted : anchorDay.ReqCompleted));
                sb.AppendLine($"\t\t\t{{ reward = {Q(anchorDay.Reward)}, reqCompleted = {reqCompleted}, tasks = {{");

                for (var t = 0; t < anchorDay.Std.Count; t++)
                {
                    var perSeg = new Dictionary<string, DailyScoopDump.TaskEntry>(StringComparer.Ordinal);
                    foreach (var s in segs)
                    {
                        if (!dayBySeg.TryGetValue(s.Key, out var day)) continue;
                        if (t >= day.Std.Count)
                        {
                            Warnings.Add($"{week.Type} day {d + 1}: segment {s.Key} has fewer tasks than {segs[0].Key}");
                            continue;
                        }
                        if (dump.Tasks.TryGetValue(day.Std[t], out var task)) perSeg[s.Key] = task;
                        else Warnings.Add($"missing objective definition: {day.Std[t]}");
                    }
                    if (!perSeg.ContainsKey(segs[0].Key)) continue;

                    sb.AppendLine("\t\t\t\t" + EmitTask(segs, perSeg) + ",");
                    TaskCount++;
                    foreach (var task in perSeg.Values)
                        foreach (var f in task.Fallbacks)
                            usedFallbacks.Add(f);
                }
                sb.AppendLine("\t\t\t},");

                var bonus = EmitBonus(dump, segs, week.Type, d + 1);
                if (bonus != null) sb.AppendLine($"\t\t\tbonus = {bonus},");
                sb.AppendLine("\t\t\t},");
            }
            sb.AppendLine("\t\t} },");
        }
        sb.AppendLine("\t},");

        // ── Fallback definitions (only the ones actually referenced above) ──
        sb.AppendLine("\tfallbackTasks = {");
        FallbackCount = 0;
        foreach (var fid in usedFallbacks)
        {
            if (!dump.Tasks.TryGetValue(fid, out var t))
            {
                Warnings.Add($"referenced fallback has no definition: {fid}");
                continue;
            }
            var pairs = new List<string> { $"type = {Q(t.Type)}" };
            if (t.Params.Length > 0) pairs.Add($"params = {Q(t.Params)}");
            if (IsMinutes(t)) pairs.Add("minutes = true");
            pairs.Add($"goals = {Num(t.Req)}");
            pairs.Add($"points = {Num(t.Points)}");
            var rw = EncodeReward(t.Reward);
            if (rw != null) pairs.Add($"reward = {rw}");
            if (!string.IsNullOrEmpty(t.Event)) pairs.Add($"event = {Q(t.Event)}");
            sb.AppendLine($"\t\t[{Q(fid)}] = {{ {string.Join(", ", pairs)} }},");
            FallbackCount++;
        }
        sb.AppendLine("\t},");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── Task emission ─────────────────────────────────────────────────

    /// <summary>
    /// One task table. Values equal across all segments collapse to a scalar / single list; the
    /// per-segment forms only appear where the segments actually differ, which is what keeps the
    /// module readable (and under the Lua memory limit).
    /// </summary>
    private string EmitTask(List<DailyScoopDump.SegmentEntry> segs, Dictionary<string, DailyScoopDump.TaskEntry> perSeg)
    {
        var anchorKey = segs[0].Key;
        var anchor = perSeg[anchorKey];
        DailyScoopDump.TaskEntry At(string key) => perSeg.TryGetValue(key, out var x) ? x : anchor;

        var pairs = new List<string> { $"type = {Q(anchor.Type)}" };
        if (anchor.Params.Length > 0) pairs.Add($"params = {Q(anchor.Params)}");
        if (IsMinutes(anchor)) pairs.Add("minutes = true");
        pairs.Add($"goals = {CollapseScalar(segs, k => Num(At(k).Req))}");
        pairs.Add($"points = {CollapseScalar(segs, k => Num(At(k).Points))}");

        var rewards = segs.Select(s => EncodeReward(At(s.Key).Reward) ?? "nil").ToList();
        if (rewards.Distinct(StringComparer.Ordinal).Count() > 1)
        {
            Warnings.Add($"reward differs per segment: {anchor.Type} ({anchor.Loc})");
            pairs.Add($"reward = {{ {string.Join(", ", segs.Select((s, i) => $"{s.Key} = {rewards[i]}"))} }}");
            pairs.Add("rewardPerSeg = true");
        }
        else if (rewards[0] != "nil")
        {
            pairs.Add($"reward = {rewards[0]}");
        }

        if (segs.Any(s => At(s.Key).Fallbacks.Count > 0))
            pairs.Add($"fallbacks = {CollapseList(segs, k => At(k).Fallbacks.Select(Q).ToList())}");

        if (!string.IsNullOrEmpty(anchor.Event))
            pairs.Add($"event = {Q(anchor.Event)}");

        // Segments whose task IDENTITY (not just its target) differs from the anchor's.
        var variants = segs.Skip(1).Where(s => Core(At(s.Key)) != Core(anchor)).ToList();
        if (variants.Count > 0)
        {
            var parts = variants.Select(s =>
            {
                var v = At(s.Key);
                var vp = new List<string> { $"type = {Q(v.Type)}" };
                if (v.Params.Length > 0) vp.Add($"params = {Q(v.Params)}");
                if (IsMinutes(v)) vp.Add("minutes = true");
                return $"{s.Key} = {{ {string.Join(", ", vp)} }}";
            });
            pairs.Add($"variants = {{ {string.Join(", ", parts)} }}");
        }

        return $"{{ {string.Join(", ", pairs)} }}";
    }

    /// <summary>
    /// The day's repeatable bonus objective (energy + Daily Chest, and catch-up points). Special
    /// objectives are shared across week revisions, so the suffixed id is tried first and the bare
    /// one is the fallback.
    /// </summary>
    private string? EmitBonus(DailyScoopDump dump, List<DailyScoopDump.SegmentEntry> segs, string weekType, int day)
    {
        DailyScoopDump.SpecialEntry? Find(string segId)
        {
            var baseId = $"Special_{segId}_{weekType}_Day{day}";
            if (dump.Revision.Length > 0 && dump.Specials.TryGetValue(baseId + dump.Revision, out var withRev)) return withRev;
            return dump.Specials.TryGetValue(baseId, out var plain) ? plain : null;
        }

        var bySeg = new Dictionary<string, DailyScoopDump.SpecialEntry>(StringComparer.Ordinal);
        foreach (var s in segs)
        {
            var sp = Find(s.Id);
            if (sp != null) bySeg[s.Key] = sp;
        }
        if (!bySeg.TryGetValue(segs[0].Key, out var anchor)) return null;
        DailyScoopDump.SpecialEntry At(string key) => bySeg.TryGetValue(key, out var x) ? x : anchor;

        var pairs = new List<string>
        {
            $"goals = {CollapseList(segs, k => At(k).Reqs.Select(Num).ToList())}",
            $"energy = {CollapseList(segs, k => At(k).Energy.Select(Num).ToList())}",
        };
        var catchUp = segs.Select(s => At(s.Key).CatchUp).Where(c => c.Count > 0).Select(c => c[0]).ToList();
        if (catchUp.Count > 0) pairs.Add($"catchUp = {Num(catchUp.Min())}");
        var rw = EncodeReward(anchor.Reward);
        if (rw != null) pairs.Add($"reward = {rw}");
        return $"{{ {string.Join(", ", pairs)} }}";
    }

    // ── Collapse helpers ──────────────────────────────────────────────

    /// <summary>Scalar when every segment agrees, otherwise an array in segment order.</summary>
    private static string CollapseScalar(List<DailyScoopDump.SegmentEntry> segs, Func<string, string> value)
    {
        var vals = segs.Select(s => value(s.Key)).ToList();
        return vals.Distinct(StringComparer.Ordinal).Count() == 1 ? vals[0] : $"{{{string.Join(", ", vals)}}}";
    }

    /// <summary>One list when every segment agrees, otherwise a <c>{ seg = {...} }</c> map.</summary>
    private static string CollapseList(List<DailyScoopDump.SegmentEntry> segs, Func<string, List<string>> value)
    {
        var vals = segs.Select(s => value(s.Key)).ToList();
        var rendered = vals.Select(v => $"{{{string.Join(", ", v)}}}").ToList();
        if (rendered.Distinct(StringComparer.Ordinal).Count() == 1) return rendered[0];
        return $"{{ {string.Join(", ", segs.Select((s, i) => $"{s.Key} = {rendered[i]}"))} }}";
    }

    // ── Value encoding ────────────────────────────────────────────────

    private static readonly Regex EnvelopeRe = new(@"^TCE_CardPackBasic_(\d)Stars", RegexOptions.Compiled);

    /// <summary>
    /// The reward shown next to the points, in the compact array form Module:DailyScoop decodes.
    /// A reward with no id but an aux value is a timed buff (On Fire, Auto-Merge): the aux carries
    /// its duration.
    /// </summary>
    private static string? EncodeReward(DailyScoopDump.RewardEntry? r)
    {
        if (r == null) return null;
        // Timed buffs (On Fire, Infinite Energy, Cooldown Remover, Skip Time) carry no reward id —
        // the config stores a literally quoted empty string there — and put their duration in the
        // aux field, so the type + duration IS the reward.
        if (r.Id.Trim('"').Length == 0) return $"{{{Q("timed")}, {Q(r.Type)}, {Q(r.Aux)}}}";
        switch (r.Id)
        {
            case "Coins": return $"{{{Q("coins")}, {Num(r.Amount)}}}";
            case "Energy": return $"{{{Q("energy")}, {Num(r.Amount)}}}";
            case "Diamonds": return $"{{{Q("gems")}, {Num(r.Amount)}}}";
        }
        var m = EnvelopeRe.Match(r.Id);
        if (m.Success) return $"{{{Q("env")}, {m.Groups[1].Value}}}";
        return $"{{{Q("item")}, {Q(r.Id)}, {Num(r.Amount)}}}";
    }

    /// <summary>A goal expressed in minutes rather than a count — the loc key is what marks it.</summary>
    private static bool IsMinutes(DailyScoopDump.TaskEntry t) => t.Loc.Contains("Minutes", StringComparison.Ordinal);

    /// <summary>Task identity for variant detection: what it asks for, not how much.</summary>
    private static (string, string, bool) Core(DailyScoopDump.TaskEntry t) => (t.Type, t.Params, IsMinutes(t));

    private static string Q(string? s) =>
        "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Num(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
