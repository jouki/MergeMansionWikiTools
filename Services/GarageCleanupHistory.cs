using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MergeMansionWikiTools.Services;

/// <summary>Pure Garage Cleanup history reasoning (no IO) shared by app + replay harness: variant naming
/// (year → Month Year → Round N), multi-round grouping, and the CreatedAt air-status/content decider.</summary>
public static class GarageCleanupHistory
{
    /// <summary>One concrete GC: its start date + its round position within its airing (1-based; 0 when the
    /// airing has a single GC).</summary>
    public sealed record GcVariant(DateTime Start, int RoundIndex);

    /// <summary>Derives the wiki/Various key per variant (§3.2): single airing → plain; distinct years →
    /// "(YYYY)"; 2+ airings in one year → "(Month YYYY)"; an airing's rounds append " Round N". Months
    /// English (InvariantCulture).</summary>
    public static Dictionary<GcVariant, string> DeriveVariantNames(string baseName, IReadOnlyList<GcVariant> variants)
    {
        var result = new Dictionary<GcVariant, string>();

        DateTime AiringStart(GcVariant v)
        {
            var key = AiringKey(variants, v);
            return variants.Where(x => AiringKey(variants, x) == key).OrderBy(x => x.Start).First().Start;
        }

        var airingDates = variants.Select(AiringStart).Distinct().OrderBy(d => d).ToList();
        int airingCount = airingDates.Count;
        var yearCounts = airingDates.GroupBy(d => d.Year).ToDictionary(g => g.Key, g => g.Count());

        foreach (var v in variants)
        {
            var airingStart = AiringStart(v);
            string suffix;
            if (airingCount <= 1) suffix = "";                                  // single airing → plain
            else if (yearCounts[airingStart.Year] >= 2)                         // same-year collision → Month Year
                suffix = $" ({airingStart.ToString("MMMM", CultureInfo.InvariantCulture)} {airingStart.Year})";
            else suffix = $" ({airingStart.Year})";                             // distinct year
            string round = v.RoundIndex > 0 ? $" Round {v.RoundIndex}" : "";
            result[v] = baseName + suffix + round;
        }
        return result;
    }

    /// <summary>Airing identity for a variant: a non-round GC is its own airing; rounds (RoundIndex &gt; 0)
    /// within 14 days of each other are one airing keyed by the EARLIEST round's start.</summary>
    private static DateTime AiringKey(IReadOnlyList<GcVariant> all, GcVariant v)
    {
        if (v.RoundIndex == 0) return v.Start.Date;
        var cluster = all.Where(x => x.RoundIndex > 0 && Math.Abs((x.Start - v.Start).TotalDays) <= 14)
                         .OrderBy(x => x.Start).ToList();
        return cluster.Count > 0 ? cluster[0].Start.Date : v.Start.Date;
    }

    public sealed record GcRoundInput(string GcId, string ParentCbeId, DateTime Start);

    /// <summary>GcId → RoundIndex. GCs sharing a ParentCbeId whose starts cluster within 14 days are rounds of
    /// one airing (1-based, chronological); a GC alone in its cluster → 0 (no round suffix).</summary>
    public static Dictionary<string, int> GroupRounds(IReadOnlyList<GcRoundInput> gcs)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var byParent in gcs.GroupBy(g => g.ParentCbeId, StringComparer.Ordinal))
        {
            var ordered = byParent.OrderBy(g => g.Start).ToList();
            var used = new bool[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                if (used[i]) continue;
                var cluster = new List<int> { i };
                for (int j = i + 1; j < ordered.Count; j++)
                    if (!used[j] && (ordered[j].Start - ordered[i].Start).TotalDays <= 14) cluster.Add(j);
                if (cluster.Count == 1) { result[ordered[i].GcId] = 0; used[i] = true; }
                else
                    for (int n = 0; n < cluster.Count; n++) { result[ordered[cluster[n]].GcId] = n + 1; used[cluster[n]] = true; }
            }
        }
        return result;
    }

    /// <summary>Finds the parent event run a GC airing belongs to (§2.7): the run whose window
    /// [Start, Start+DurationDays] contains <paramref name="gcStart"/> (±1 day tolerance for hour-level
    /// drift). If none contains it, returns the nearest run by start date; null when there are no runs.</summary>
    public static (DateTime Start, double DurationDays)? MatchParentRun(
        DateTime gcStart, IReadOnlyList<(DateTime Start, double DurationDays)> runs)
    {
        if (runs.Count == 0) return null;
        foreach (var r in runs)
            if (gcStart >= r.Start.AddDays(-1) && gcStart <= r.Start.AddDays(r.DurationDays + 1))
                return r;
        return runs.OrderBy(r => Math.Abs((r.Start - gcStart).TotalDays)).First();
    }

    public sealed record DumpObservation(DateTime CreatedAt, bool IsEnabled);
    public sealed record AirVerdict(bool Aired, bool Disabled, DateTime? TrustedContentAt);

    /// <summary>CreatedAt rule (§3.0). A) air-status from the LAST pre-E dump (post-E locked; no pre-E →
    /// fallback aired). B) content source = newest trusted dump (pre-E, or post-E enabled within trustWindow).</summary>
    public static AirVerdict DecideAir(DateTime start, double durationDays, IReadOnlyList<DumpObservation> obs, int trustWindowDays = 60)
    {
        var end = start.AddDays(durationDays);
        var ordered = obs.OrderBy(o => o.CreatedAt).ToList();

        // A) air-status
        var preE = ordered.Where(o => o.CreatedAt < end).ToList();
        bool aired = preE.Count > 0 ? preE[^1].IsEnabled : true;   // no pre-E → fallback aired

        // B) content source: newest pre-E OR post-E enabled within window
        DateTime? trusted = null;
        foreach (var o in ordered)
        {
            bool trustworthy = o.CreatedAt < end
                || (o.IsEnabled && o.CreatedAt <= end.AddDays(trustWindowDays));
            if (trustworthy) trusted = o.CreatedAt;
        }
        return new AirVerdict(aired, !aired, trusted);
    }
}
