#nullable enable
using System;
using System.Collections.Generic;

namespace GameLogic
{
    /// <summary>
    /// CUSTOM: Runtime name registry for <see cref="HotspotId"/>. The compiled enum is a
    /// snapshot of one game version; every update adds members, and a stale enum makes the
    /// dumper emit integer Ids with no description. The app loads the current game's members
    /// here (read from global-metadata.dat via <see cref="Il2Cpp.Il2CppMetadataEnumReader"/>)
    /// before dumping; the enum remains the fallback when nothing is loaded.
    ///
    /// Single source of truth for "what is this hotspot called" — LocMan, the area serializer
    /// and requirement/JSON output all go through <see cref="Resolve"/> / <see cref="IsKnown"/>
    /// instead of <c>Enum.IsDefined</c> / <c>ToString()</c>.
    /// </summary>
    public static class HotspotIdNames
    {
        private static Dictionary<int, string> _overrides = new();

        /// <summary>Number of loaded runtime members (0 = enum-only fallback).</summary>
        public static int LoadedCount => _overrides.Count;

        /// <summary>Optional label of what was loaded (e.g. game version) — diagnostics only.</summary>
        public static string? LoadedSource { get; private set; }

        /// <summary>Replaces the runtime map. Pass an empty collection to revert to enum-only.</summary>
        public static void Load(IEnumerable<KeyValuePair<int, string>> members, string? source = null)
        {
            var map = new Dictionary<int, string>();
            foreach (var kv in members)
                if (!string.IsNullOrEmpty(kv.Value)) map[kv.Key] = kv.Value;
            _overrides = map;
            LoadedSource = source;
        }

        public static void Clear() => Load(Array.Empty<KeyValuePair<int, string>>(), null);

        /// <summary>True when the value has a name in the runtime map OR in the compiled enum.</summary>
        public static bool IsKnown(HotspotId id)
            => _overrides.ContainsKey((int)id) || Enum.IsDefined(typeof(HotspotId), id);

        /// <summary>
        /// Name for the value: runtime map first, then the enum, else the raw number (what
        /// <c>ToString()</c> would print for an undefined value).
        /// </summary>
        public static string Resolve(HotspotId id)
            => _overrides.TryGetValue((int)id, out var name) ? name : id.ToString();

        /// <summary>Runtime-map name only (null when the value is not in the loaded map).</summary>
        public static string? TryResolveOverride(HotspotId id)
            => _overrides.TryGetValue((int)id, out var name) ? name : null;
    }
}
