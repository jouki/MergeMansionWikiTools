using System.Collections;
using System.Reflection;
using Code.GameLogic.ExtraSpawns;
using GameLogic.Config;
using GameLogic.Player.Requirements;
using Metaplay.Core.Math;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// Reads the per-item "extra spawn" currency/token values out of
/// <c>SharedGameConfig.ExtraSpawnItemValues</c>. Both value maps are PRIVATE
/// <c>[MetaMember]</c> properties on <see cref="ExtraSpawnItemValueInfo"/>, so they are reached by
/// reflection. In the chain dump these become the per-item keys named after the currency or
/// core-support-event token (<c>DigEventTaps</c>, <c>QuaternaryEnergy</c>,
/// <c>ClassicRacesSailPoints</c>, ...), written as raw doubles.
/// </summary>
/// <remarks>
/// Carried over unchanged (apart from namespace/style) from the legacy dumper's own
/// <c>ExtraSpawnHelper</c> — one of the seven files that are entirely this project's work.
/// </remarks>
internal static class ExtraSpawnHelper
{
    private static readonly PropertyInfo? CustomValuesByCurrencyProp =
        typeof(ExtraSpawnItemValueInfo).GetProperty("CustomValuesByCurrency",
            BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly PropertyInfo? CustomValuesByTokenProp =
        typeof(ExtraSpawnItemValueInfo).GetProperty("CustomValuesByCoreSupportEventTokenId",
            BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Gets extra spawn custom values for a single item (currency + token combined), in the
    /// config's own insertion order. Returns null if the item is not in the library or has no
    /// custom values at all.
    /// </summary>
    public static Dictionary<string, double>? GetItemValues(SharedGameConfig config, int itemId)
    {
        if (config.ExtraSpawnItemValues == null ||
            !config.ExtraSpawnItemValues.TryGetValue(itemId, out var info))
            return null;

        var result = new Dictionary<string, double>();

        if (CustomValuesByCurrencyProp?.GetValue(info) is IDictionary currDict)
            foreach (DictionaryEntry entry in currDict)
                result[entry.Key.ToString()!] = ((F32)entry.Value!).Double;

        if (CustomValuesByTokenProp?.GetValue(info) is IDictionary tokDict)
            foreach (DictionaryEntry entry in tokDict)
                result[entry.Key.ToString()!] = ((F32)entry.Value!).Double;

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Sums extra spawn values across all requirement items of a task. Returns an empty dictionary
    /// when nothing matched.
    /// </summary>
    public static Dictionary<string, int> SumRequirementValues(
        SharedGameConfig config,
        IEnumerable<PlayerRequirement> requirements)
    {
        var sums = new Dictionary<string, double>();

        foreach (var req in requirements)
        {
            if (req is PlayerItemRequirement pir && pir.Items != null)
            {
                foreach (var itemId in pir.Items)
                {
                    var values = GetItemValues(config, itemId);
                    if (values == null) continue;

                    foreach (var kv in values)
                        sums[kv.Key] = sums.GetValueOrDefault(kv.Key) + kv.Value;
                }
            }
        }

        return sums.ToDictionary(kv => kv.Key, kv => (int)System.Math.Round(kv.Value));
    }
}
