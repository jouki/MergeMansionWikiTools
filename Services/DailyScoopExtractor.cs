using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Extracts the Daily Scoop (DailyChallenges V2) task ladder straight from the imported
/// <see cref="SharedGameConfig"/> into <c>daily_scoop.json</c>, the input of
/// <see cref="DailyScoopDatatableService"/> (which turns it into <c>Module:Datatable/DailyScoop</c>).
/// <para>
/// <b>Why this does not read events.json.</b> events.json already carries the DailyChallenges
/// libraries, but NOT the per-task event gate: the reference ("golden") dumper predates
/// <c>HasActivableKindActiveForDurationRequirement</c> and writes every such requirement as a bare
/// <c>{}</c> (see <c>MMWT.Dumper/Support/RequirementModel.cs</c>, UnsupportedKinds). That gate is the
/// <c>RegexPattern</c> — <c>"LBE_"</c>, <c>"LC_"</c>, <c>"DE_"</c>, … — that
/// <c>Module:DailyScoop</c> needs to decide which variant of an event-tied task is actually offered;
/// the live module carries 354 of them. Teaching either dump engine to emit it would break the
/// byte-for-byte Legacy/Native parity that the engines are verified with, so the gate is read here
/// instead, from the config object itself. Being written by the app rather than by an
/// <c>IDumpEngine</c>, this file is outside the compared golden set and is identical under both
/// engines.
/// </para>
/// <para>
/// <b>Only the live week set is extracted.</b> Since the 2026-08-31 week the game serves a second,
/// complete set of weeks whose ids carry a <c>_v2</c> suffix (same milestones and rewards, different
/// daily tasks — see <c>_CONTEXT/Game/DailyScoop.md</c> §2b). The set is picked from the MinigameId
/// of the currently running weekly event, so a future <c>_v3</c> re-cut is followed automatically and
/// the superseded set is simply dropped (it is not archived — user decision, 2026-09-15).
/// </para>
/// </summary>
public static class DailyScoopExtractor
{
    /// <summary>File name written next to events.json in the dump folder.</summary>
    public const string FileName = "daily_scoop.json";

    /// <summary>
    /// Wiki-side segment keys, in the order every <c>goals</c>/<c>points</c> array in
    /// <c>Module:Datatable/DailyScoop</c> uses. This order is a contract with
    /// <c>Module:DailyScoop</c> (it indexes the arrays positionally), so it is fixed here rather
    /// than derived from the config's own segment order.
    /// </summary>
    private static readonly (string Key, string IdPrefix)[] Segments =
    {
        ("l51", "PlayerLevels51above"),
        ("l15", "PlayerLevels15to25"),
        ("l26", "PlayerLevels26to45"),
        ("l46", "PlayerLevels46to50"),
        ("lt5", "LT500"),
    };

    /// <summary>The four week types, with the wiki key/label Module:DailyScoop renders them under.</summary>
    private static readonly (string Type, string Key, string Name)[] WeekTypes =
    {
        ("EasyWeek", "Easy", "Easy Week"),
        ("MedWeek", "Medium", "Medium Week"),
        ("HardWeek", "Hard", "Hard Week"),
        ("SuperWeek", "Super", "Super Week"),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Builds the model from <paramref name="config"/>. <paramref name="nowUtc"/> selects the live
    /// week set (the running weekly event, else the most recent one that has started).
    /// </summary>
    public static DailyScoopDump Extract(SharedGameConfig config, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        var revision = SelectRevision(config, nowUtc, out var pickedFrom);
        return Extract(config, revision, pickedFrom);
    }

    /// <summary>
    /// Extracts an explicitly named week set instead of the live one. Used to verify this extractor
    /// against the output the retired Python bridge produced for the pre-<c>_v2</c> set, and by tests
    /// that must not depend on the wall clock.
    /// </summary>
    public static DailyScoopDump Extract(SharedGameConfig config, string revision, string selectedFrom)
    {
        ArgumentNullException.ThrowIfNull(config);
        var dump = new DailyScoopDump { Revision = revision ?? "", SelectedFrom = selectedFrom ?? "" };

        foreach (var (key, idPrefix) in Segments)
            dump.Segments.Add(new DailyScoopDump.SegmentEntry { Key = key, Id = idPrefix });

        // ── Standard objectives (primary tasks AND the Fallback_* definitions: one library) ──
        if (config.DailyChallengesStandardObjectives != null)
        {
            foreach (var kv in config.DailyChallengesStandardObjectives.EnumerateAll())
            {
                var id = kv.Key?.ToString() ?? "";
                if (id.Length == 0) continue;
                var o = kv.Value;
                var task = new DailyScoopDump.TaskEntry
                {
                    Type = Str(M(o, "ObjectiveType")),
                    Req = Int(M(o, "ObjectiveRequirement")),
                    Prio = Int(M(o, "OrderPriority")),
                    Params = JoinStrings(M(o, "ObjectiveParameter")),
                    Loc = Str(M(o, "LocId")),
                    Event = EventGate(o),
                };
                var pool = M(o, "RewardsPoolData");
                task.Points = SlotAmount(pool, 0);
                task.Reward = SlotReward(pool, 1);
                foreach (var f in Refs(M(o, "FallbackObjectiveIdReferencesList")))
                    task.Fallbacks.Add(f);
                dump.Tasks[id] = task;
            }
        }

        // ── Special objectives (the day's "bonus" ladder: repeatable, energy + Daily Chest) ──
        if (config.DailyChallengesSpecialObjectives != null)
        {
            foreach (var kv in config.DailyChallengesSpecialObjectives.EnumerateAll())
            {
                var id = kv.Key?.ToString() ?? "";
                if (id.Length == 0) continue;
                var o = kv.Value;
                var pool = M(o, "RewardsPoolData");
                var sp = new DailyScoopDump.SpecialEntry
                {
                    Type = Str(M(o, "ObjectiveType")),
                    Params = JoinStrings(M(o, "ObjectiveParameter")),
                    Loc = Str(M(o, "LocId")),
                    Reward = SlotReward(pool, 1),
                };
                sp.Reqs.AddRange(Ints(M(o, "ObjectiveRequirement")));
                sp.CatchUp.AddRange(Ints(M(pool, "ForcedCatchUpPointsAmounts")));
                sp.Energy.AddRange(SlotAmounts(pool, 0));
                dump.Specials[id] = sp;
            }
        }

        // ── Days ──
        if (config.DailyChallengesDays != null)
        {
            foreach (var kv in config.DailyChallengesDays.EnumerateAll())
            {
                var id = kv.Key?.ToString() ?? "";
                if (id.Length == 0) continue;
                var d = kv.Value;
                var day = new DailyScoopDump.DayEntry
                {
                    ReqCompleted = Int(M(d, "RequiredCompletedObjectivesForDayReward")),
                    Reward = DayRewardKind(M(d, "Rewards"), config),
                };
                foreach (var t in Refs(M(d, "StandardObjectives")))
                    day.Std.Add(t);
                dump.Days[id] = day;
            }
        }

        // ── Week index: type -> segment -> the 7 day ids, for the live revision only ──
        foreach (var (type, key, name) in WeekTypes)
        {
            var week = new DailyScoopDump.WeekEntry { Type = type, Key = key, Name = name };
            foreach (var (segKey, idPrefix) in Segments)
            {
                var weekId = $"{idPrefix}_{type}_1{dump.Revision}";
                var dayIds = WeekDays(config, weekId);
                if (dayIds.Count == 0)
                {
                    dump.Warnings.Add($"week {weekId} not found in DailyChallengesWeeks");
                    continue;
                }
                week.Days[segKey] = dayIds;
            }
            if (week.Days.Count > 0) dump.Weeks.Add(week);
            else dump.Warnings.Add($"week type {type}{dump.Revision} has no segment variant");
        }

        return dump;
    }

    /// <summary>Extracts and writes <c>daily_scoop.json</c>; returns the path, or null on failure.</summary>
    public static string? Write(SharedGameConfig config, string outputPath, DateTime nowUtc)
    {
        var dump = Extract(config, nowUtc);
        var json = JsonSerializer.Serialize(dump, JsonOpts);
        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        return outputPath;
    }

    // ── Revision selection ────────────────────────────────────────────

    /// <summary>
    /// The <c>_v&lt;N&gt;</c> suffix of the week set the game currently serves: the MinigameId of the
    /// enabled weekly event whose window contains <paramref name="nowUtc"/>, else of the most recent
    /// one that has already started, else of the last one in the library.
    /// </summary>
    private static string SelectRevision(SharedGameConfig config, DateTime nowUtc, out string pickedFrom)
    {
        pickedFrom = "";
        (DateTime Start, string Id, string Minigame)? best = null;
        (DateTime Start, string Id, string Minigame)? newest = null;

        if (config.CoreSupportEvents != null)
        {
            foreach (var kv in config.CoreSupportEvents.EnumerateAll())
            {
                var info = kv.Value;
                if (!string.Equals(Str(M(info, "EventType")), "DailyChallengesEvent", StringComparison.Ordinal))
                    continue;
                var minigame = Str(M(info, "MinigameId"));
                if (minigame.Length == 0) continue;
                var ap = M(info, "ActivableParams");
                var start = ScheduleStart(M(ap, "Schedule"));
                if (start == null) continue;
                var row = (start.Value, kv.Key?.ToString() ?? "", minigame);
                if (newest == null || row.Item1 > newest.Value.Start) newest = row;
                if (row.Item1 <= nowUtc && (best == null || row.Item1 > best.Value.Start)) best = row;
            }
        }

        var chosen = best ?? newest;
        if (chosen == null) return "";
        pickedFrom = $"{chosen.Value.Id} ({chosen.Value.Minigame})";
        return EventScheduleService.SplitRevision(chosen.Value.Minigame).Suffix;
    }

    private static DateTime? ScheduleStart(object? schedule)
    {
        var start = M(schedule, "Start");
        if (start == null) return null;
        var y = Int(M(start, "Year"));
        var mo = Int(M(start, "Month"));
        var d = Int(M(start, "Day"));
        if (y <= 0 || mo <= 0 || d <= 0) return null;
        var h = Int(M(start, "Hour"));
        var mi = Int(M(start, "Minute"));
        try { return new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static List<string> WeekDays(SharedGameConfig config, string weekId)
    {
        var days = new List<string>();
        if (config.DailyChallengesWeeks == null) return days;
        foreach (var kv in config.DailyChallengesWeeks.EnumerateAll())
        {
            if (!string.Equals(kv.Key?.ToString(), weekId, StringComparison.Ordinal)) continue;
            days.AddRange(Refs(M(kv.Value, "Days")));
            break;
        }
        return days;
    }

    // ── Member readers ────────────────────────────────────────────────

    /// <summary>
    /// The task's event gate: the <c>RegexPattern</c> of a
    /// <c>HasActivableKindActiveForDurationRequirement</c> in the objective's private
    /// <c>_requirements</c> — the whole reason this extractor exists (see the class remarks).
    /// </summary>
    private static string? EventGate(object? objective)
    {
        if (M(objective, "_requirements") is not System.Collections.IEnumerable reqs)
            return null;
        foreach (var r in reqs)
        {
            if (r == null) continue;
            var pattern = Str(M(r, "RegexPattern"));
            if (pattern.Length > 0) return pattern;
        }
        return null;
    }

    /// <summary>Reward slot amount (slot 0 of a task pool = the Daily Scoop points it awards).</summary>
    private static int SlotAmount(object? pool, int slot)
    {
        var amounts = SlotAmounts(pool, slot);
        return amounts.Count > 0 ? amounts[0] : 0;
    }

    private static List<int> SlotAmounts(object? pool, int slot)
    {
        var def = SlotDefinition(pool, slot);
        return def == null ? new List<int>() : Ints(M(def, "RewardAmounts"));
    }

    /// <summary>Slot 1 of a task pool = the item/currency reward shown next to the points.</summary>
    private static DailyScoopDump.RewardEntry? SlotReward(object? pool, int slot)
    {
        var def = SlotDefinition(pool, slot);
        if (def == null) return null;
        var amounts = Ints(M(def, "RewardAmounts"));
        return new DailyScoopDump.RewardEntry
        {
            Id = Str(M(def, "RewardId")),
            Type = Str(M(def, "RewardType")),
            Aux = Str(M(def, "RewardAux0")),
            Amount = amounts.Count > 0 ? amounts[0] : 1,
        };
    }

    private static object? SlotDefinition(object? pool, int slot)
    {
        if (M(pool, "RewardDefinitionsBySlotId") is not System.Collections.IEnumerable slots)
            return null;
        var i = 0;
        foreach (var s in slots)
        {
            if (i++ != slot) continue;
            if (s is not System.Collections.IEnumerable defs) return null;
            foreach (var d in defs) return d;   // one weighted entry per slot in every live config
            return null;
        }
        return null;
    }

    /// <summary>
    /// Config ids out of a reference list. The entries are <c>ConfigId&lt;,&gt;</c> wrappers, which
    /// have no ToString of their own (they would stringify as the generic type name) — the id lives
    /// in their inherited <c>ConfigKey</c> member, the same one events.json writes them as.
    /// </summary>
    private static List<string> Refs(object? list)
    {
        var res = new List<string>();
        if (list is not System.Collections.IEnumerable items) return res;
        foreach (var it in items)
        {
            if (it == null) continue;
            var s = (M(it, "ConfigKey") ?? it).ToString() ?? "";
            if (s.Length > 0) res.Add(s);
        }
        return res;
    }

    private static List<int> Ints(object? list)
    {
        var res = new List<int>();
        if (list is not System.Collections.IEnumerable items) return res;
        foreach (var it in items)
            if (it is int i) res.Add(i);
            else if (it != null && int.TryParse(it.ToString(), out var p)) res.Add(p);
        return res;
    }

    private static string JoinStrings(object? list)
    {
        if (list is not System.Collections.IEnumerable items) return "";
        return string.Join(",", Refs(items));
    }

    /// <summary>
    /// Which box a day awards: the seventh day of each week hands out the weekly chest
    /// (<c>CSE_DailyChallenge_WeeklyChest*</c>), the others the daily one. The reward is a nested
    /// <c>PlayerReward</c> whose item is an <see cref="ItemDef"/> reference — an int key that
    /// stringifies as its type name — so the graph is walked and the key resolved against the item
    /// library, exactly as the dump's own ConfigDefinition converter does.
    /// </summary>
    private static string DayRewardKind(object? rewards, SharedGameConfig config)
    {
        foreach (var name in ItemNames(rewards, config, 0))
            if (name.Contains("WeeklyChest", StringComparison.Ordinal))
                return "weekly";
        return "daily";
    }

    private static IEnumerable<string> ItemNames(object? node, SharedGameConfig config, int depth)
    {
        if (node == null || depth > 4) yield break;

        if (node is ItemDef item)
        {
            if (config.Items != null && config.Items.TryGetValue(item.ConfigKey, out var def) && def?.ItemType != null)
                yield return def.ItemType;
            yield break;
        }
        if (node is string || node.GetType().IsPrimitive || node.GetType().IsEnum) yield break;

        if (node is System.Collections.IEnumerable items)
        {
            foreach (var child in items)
                foreach (var name in ItemNames(child, config, depth + 1))
                    yield return name;
            yield break;
        }

        foreach (var (_, _, get) in MetaObjectWriter.Members(node))
        {
            object? v = null;
            try { v = get(); } catch { /* an unreadable derived getter is simply skipped */ }
            foreach (var name in ItemNames(v, config, depth + 1))
                yield return name;
        }
    }

    /// <summary>
    /// Null-safe <c>[MetaMember]</c> read: <see cref="MetaObjectWriter.GetMember"/> dereferences its
    /// argument, and half the members here hang off an optional parent (a reward pool, a schedule),
    /// so a missing parent must degrade to null instead of throwing mid-extraction.
    /// </summary>
    private static object? M(object? obj, string name) =>
        obj == null ? null : MetaObjectWriter.GetMember(obj, name, ConsoleDumpLog.Instance);

    private static string Str(object? v) => v?.ToString() ?? "";

    private static int Int(object? v) => v is int i ? i : (v != null && int.TryParse(v.ToString(), out var p) ? p : 0);
}
