using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Renders the fuel of a <b>tag-based</b> sink, for both the Merge Stages "Fuel" column and the
/// infobox <c>needs</c> row — one implementation so the two never drift apart.
/// <para>
/// A ScoreTargets sink lists named items and every one of them is required, so the generators
/// print them one per line. A tag sink is the opposite: it takes <b>any one</b> item carrying the
/// tag, and there can be a lot of them (25 souvenirs feed the Maddie in Paris Phone Camera). Those
/// are therefore collapsed per chain into <c>{{Item/Group}}</c> ranges and joined with " / ", with
/// the input count stated once in front, which is also how the hand-written Flower Compost and
/// Tarot Table pages have always phrased it.
/// </para>
/// </summary>
public static class SinkFuelFormatter
{
    /// <summary>
    /// One wikitext string for the whole requirement, or empty when nothing resolved.
    /// </summary>
    /// <param name="requirements">Resolved fuel items, in any order.</param>
    /// <param name="inputCount">How many of them one activation consumes.</param>
    /// <param name="resolve">
    /// The wiki name and level of one fuel item. Must go through the mapping module, not the raw
    /// chain: the four Murder Weapons are four separate GAME chains, each a lone level 1, that the
    /// mapping merges into one "Murder Weapon" chain of levels 1-4. Grouping on the game chain
    /// printed them as four identical "Murder Weapon (L1)" entries.
    /// </param>
    public static string Format(
        IEnumerable<(ParsedChain Chain, ParsedItem Item)> requirements,
        int inputCount,
        Func<ParsedChain, ParsedItem, (string Name, int Level)> resolve)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(resolve);

        var parts = new List<string>();
        // Grouped by the RESOLVED name, so chains the mapping merges collapse into one range.
        foreach (var group in requirements
                     .Select(r => resolve(r.Chain, r.Item))
                     .GroupBy(r => r.Name, StringComparer.Ordinal))
        {
            var name = group.Key;
            var levels = new SortedSet<int>(group.Select(r => r.Level));
            foreach (var (min, max) in SplitIntoRuns(levels))
            {
                parts.Add(min == max
                    ? $"{{{{Item|{name}|{min}}}}}"
                    : $"{{{{Item/Group|{name}|{max}|min={min}|max={max}}}}}");
            }
        }

        if (parts.Count == 0) return "";
        var joined = string.Join(" / ", parts);
        return inputCount > 1 ? $"{inputCount}x {joined}" : joined;
    }

    /// <summary>Collapses consecutive levels into (min, max) runs: 1,2,3,5 → (1,3) and (5,5).</summary>
    internal static IEnumerable<(int Min, int Max)> SplitIntoRuns(IEnumerable<int> levels)
    {
        int? start = null, prev = null;
        foreach (var level in levels)
        {
            if (start == null) { start = prev = level; continue; }
            if (level == prev + 1) { prev = level; continue; }
            yield return (start.Value, prev!.Value);
            start = prev = level;
        }
        if (start != null) yield return (start.Value, prev!.Value);
    }
}
