using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Folds a <see cref="ParsedItem.IsTransient"/> stage out of a transform/decay target: instead of
/// linking to a thing that exists for a moment, the generator links to WHAT IT BECOMES and carries
/// the odds of that roll.
/// <para>
/// The case it was written for is the Voyance's House loop (<c>_CONTEXT/Game/Eventy.md</c>): the
/// locked house is fuelled into <c>VoyanceHouseUnlockedAB_01</c>, which lives 3 seconds and then
/// splits 50/50 into the Investigation or the Seance. Linking the reader to that 3-second stage says
/// nothing; "50% Investigation / 50% Seance" is the fact they came for.
/// </para>
/// </summary>
public static class TransientFold
{
    /// <summary>One resolved outcome: an item type and, when the roll is not certain, its chance.</summary>
    public readonly record struct Outcome(string ItemType, double? Odds);

    /// <summary>Depth cap — a transient rolling into a transient is plausible, a cycle is not.</summary>
    private const int MaxDepth = 4;

    /// <summary>
    /// Expands <paramref name="targetItemType"/> while it resolves to a transient item, multiplying
    /// the odds along the way. A non-transient target (or one whose outcomes cannot be resolved)
    /// comes back unchanged, so callers can always use the result directly.
    /// </summary>
    public static IReadOnlyList<Outcome> Resolve(string targetItemType, Func<string, ParsedItem?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var result = new List<Outcome>();
        Expand(targetItemType, null, lookup, 0, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private static void Expand(string itemType, double? odds, Func<string, ParsedItem?> lookup,
        int depth, List<Outcome> result, HashSet<string> visited)
    {
        var item = lookup(itemType);
        if (item == null || !item.IsTransient || depth >= MaxDepth || !visited.Add(itemType))
        {
            result.Add(new Outcome(itemType, odds));
            return;
        }

        var outcomes = OutcomesOf(item);
        if (outcomes.Count == 0)
        {
            // Flagged transient but nothing to roll into — keep the item rather than drop the row,
            // so a mis-set flag shows up as "unchanged" instead of silently deleting information.
            result.Add(new Outcome(itemType, odds));
            return;
        }

        foreach (var (next, chance) in outcomes)
            Expand(next, Combine(odds, chance), lookup, depth + 1, result, visited);
    }

    /// <summary>What a transient turns into: its decay roll, or its single decay target at 100%.</summary>
    private static List<(string ItemType, double? Odds)> OutcomesOf(ParsedItem item)
    {
        var list = new List<(string, double?)>();

        if (item.DecayIntoOdds is { Count: > 0 })
            foreach (var (type, pct) in item.DecayIntoOdds)
                list.Add((type, pct));
        else if (item.DecayAfterLastCycleOdds is { Count: > 0 })
            foreach (var (type, pct) in item.DecayAfterLastCycleOdds)
                list.Add((type, pct));
        else if (!string.IsNullOrEmpty(item.DecayIntoItemType))
            list.Add((item.DecayIntoItemType!, null));
        else if (!string.IsNullOrEmpty(item.DecayAfterLastCycleItemType))
            list.Add((item.DecayAfterLastCycleItemType!, null));
        else if (!string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
            list.Add((item.SpawnDecayIntoItemType!, null));

        return list;
    }

    /// <summary>Chains two probabilities; a null on either side means "certain".</summary>
    private static double? Combine(double? outer, double? inner) =>
        outer == null ? inner
        : inner == null ? outer
        : outer.Value * inner.Value / 100.0;

    /// <summary>
    /// Renders an outcome as a wiki link, prefixing the chance when the roll is not a certainty —
    /// the same "quantity first" shape the Drops column uses (<c>8× {{Item|…}}</c>).
    /// </summary>
    /// <param name="iconLevel">
    /// Draws the icon of a DIFFERENT level than the one the link points at (<c>{{Item|…|1|iconLevel=2}}</c>).
    /// Needed where level 1 of the target is an empty, not-yet-started state whose sprite is the one
    /// the player just came from: Location: The Mansion transforms into Investigation: The Mansion
    /// (L1), whose art is the open location — three rows in a row showing the same building told the
    /// reader nothing (user report, 2026-09-18). The level in the label stays truthful.
    /// </param>
    /// <param name="sourceLevel">
    /// The level of the row the cell belongs to. Given it, the percentage is emitted as an invoke
    /// instead of a baked-in number: the odds table further down the page reads live data, so a
    /// hardcoded cell would silently disagree with it after any rebalance — one half of the page
    /// fixes itself, the other does not (user report, 2026-09-18). The invoke defaults its source
    /// chain to the page it sits on, so the page name is not repeated in the wikitext. Without a
    /// level the literal is kept, so callers that cannot name their row still render something.
    /// </param>
    public static string Format(string chainName, int level, double? odds, int? iconLevel = null,
        int? sourceLevel = null)
    {
        var icon = iconLevel is { } il && il != level ? $"|iconLevel={il}" : "";
        var link = $"{{{{Item|{chainName}|{level}{icon}}}}}";
        if (odds is null or >= 99.995) return link;

        // &nbsp;, not a plain space: the Transforms To column is narrow and the browser was breaking
        // the line between the percentage and the item it belongs to (user report, 2026-09-18).
        if (sourceLevel is { } sl)
            return $"{{{{#Invoke:Items|GetTransformOddsFromChainName|{sl}|{chainName}|{level}}}}}&nbsp;{link}";

        return $"{odds.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%&nbsp;{link}";
    }
}
