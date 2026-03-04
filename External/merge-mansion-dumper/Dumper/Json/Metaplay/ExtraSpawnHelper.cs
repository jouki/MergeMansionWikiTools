using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Code.GameLogic.ExtraSpawns;
using GameLogic.Config;
using GameLogic.Player.Requirements;
using Metaplay.Core.Math;

namespace merge_mansion_dumper.Dumper.Json.Metaplay
{
    static class ExtraSpawnHelper
    {
        private static readonly PropertyInfo CustomValuesByCurrencyProp =
            typeof(ExtraSpawnItemValueInfo).GetProperty("CustomValuesByCurrency",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly PropertyInfo CustomValuesByTokenProp =
            typeof(ExtraSpawnItemValueInfo).GetProperty("CustomValuesByCoreSupportEventTokenId",
                BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Gets extra spawn custom values for a single item (currency + token combined).
        /// Returns null if item not found or has no custom values.
        /// </summary>
        public static Dictionary<string, double> GetItemValues(SharedGameConfig config, int itemId)
        {
            if (config.ExtraSpawnItemValues == null ||
                !config.ExtraSpawnItemValues.TryGetValue(itemId, out var info))
                return null;

            var result = new Dictionary<string, double>();

            if (CustomValuesByCurrencyProp?.GetValue(info) is IDictionary currDict)
                foreach (DictionaryEntry entry in currDict)
                    result[entry.Key.ToString()] = ((F32)entry.Value).Double;

            if (CustomValuesByTokenProp?.GetValue(info) is IDictionary tokDict)
                foreach (DictionaryEntry entry in tokDict)
                    result[entry.Key.ToString()] = ((F32)entry.Value).Double;

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Sums extra spawn values across all requirement items of a task.
        /// Returns empty dict if no values found.
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

            return sums.ToDictionary(kv => kv.Key, kv => (int)Math.Round(kv.Value));
        }
    }
}
