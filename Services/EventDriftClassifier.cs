namespace MergeMansionWikiTools.Services;

/// <summary>Outcome of classifying a re-aired event's new start against an existing run.</summary>
public enum DriftVerdict { AutoUpdate, NeedsDecision, AutoSeparate }

/// <summary>Pure classifier: decides whether a new run start is a drift (auto-update of the existing
/// run), an ambiguous case (human decides), or clearly a separate run. Boundaries measured start-to-start.</summary>
public static class EventDriftClassifier
{
    public static DriftVerdict Classify(System.DateTime existingStart, double existingDurationDays,
        System.DateTime newStart, int separateThresholdDays = 60)
    {
        if (newStart <= existingStart.AddDays(existingDurationDays)) return DriftVerdict.AutoUpdate;
        if ((newStart - existingStart).TotalDays < separateThresholdDays) return DriftVerdict.NeedsDecision;
        return DriftVerdict.AutoSeparate;
    }

    /// <summary>The existing run whose Start is closest to <paramref name="newStart"/>, or null if none.</summary>
    public static (System.DateTime Start, double Dur)? Nearest(
        System.Collections.Generic.IReadOnlyList<(System.DateTime Start, double Dur)> existing, System.DateTime newStart)
    {
        if (existing == null || existing.Count == 0) return null;
        (System.DateTime Start, double Dur)? best = null; double bestAbs = double.MaxValue;
        foreach (var r in existing)
        {
            var abs = System.Math.Abs((r.Start - newStart).TotalDays);
            if (abs < bestAbs) { bestAbs = abs; best = r; }
        }
        return best;
    }
}
