using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Shared Garage Cleanup name normalization — ported from the replay harness so the live app and the
/// harness resolve event/chain/GC names identically (DRY). CBE/GC names regress to raw ids in some dumps
/// and chains lack a localized Name in older dumps; these helpers heal both from the best value found in
/// ANY dump, so every airing of one event keys the same across versions.
/// </summary>
public static class GarageCleanupNameService
{
    /// <summary>Unusable for keying: empty, a raw config id (CBE_/GC_/…), or the generic stub.</summary>
    public static bool IsPlaceholderName(string? n) =>
        string.IsNullOrWhiteSpace(n) || n == "Garage Cleanup" || Regex.IsMatch(n!, @"^(CBE|LDE|SE|GC|LC|LS)_");

    /// <summary>Derives the parent seasonal-event name from a Garage Cleanup name by stripping the
    /// " Garage Cleanup" suffix. Returns the input unchanged if it has no such suffix.</summary>
    public static string DeriveParent(string gcName) =>
        gcName.EndsWith(" Garage Cleanup", StringComparison.Ordinal) ? gcName[..^" Garage Cleanup".Length] : gcName;

    /// <summary>CBE id → best resolved (non-placeholder) Name across all given events.json dumps.</summary>
    public static Dictionary<string, string> BuildGlobalCbeMap(IEnumerable<string> eventsJsonPaths)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in eventsJsonPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("Data", out var d)
                    || !d.TryGetProperty("CollectibleBoards", out var cbs) || cbs.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in cbs.EnumerateArray())
                    if (c.TryGetProperty("CollectibleBoardEventId", out var ie) && ie.ValueKind == JsonValueKind.String
                        && c.TryGetProperty("Name", out var ne) && ne.ValueKind == JsonValueKind.String
                        && !IsPlaceholderName(ne.GetString()))
                        map[ie.GetString()!] = ne.GetString()!;
            }
            catch { /* skip malformed dump */ }
        }
        return map;
    }

    /// <summary>Chain ConfigKey → real Name across all given chain_item_odds.json dumps.</summary>
    public static Dictionary<string, string> BuildGlobalChainNameMap(IEnumerable<string> chainJsonPaths)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in chainJsonPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("Data", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in arr.EnumerateArray())
                    if (c.TryGetProperty("ConfigKey", out var ck) && ck.ValueKind == JsonValueKind.String
                        && c.TryGetProperty("Name", out var nm) && nm.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(nm.GetString()))
                        map[ck.GetString()!] = nm.GetString()!;
            }
            catch { }
        }
        return map;
    }

    /// <summary>CBE id → non-placeholder Name from a SINGLE dump's <c>Data</c> element (mode B: the live app
    /// has only the active dump). Same shape as <see cref="BuildGlobalCbeMap"/> but in-memory.</summary>
    public static Dictionary<string, string> BuildCbeMapFromData(JsonElement data)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("CollectibleBoards", out var cbs) || cbs.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var c in cbs.EnumerateArray())
            if (c.TryGetProperty("CollectibleBoardEventId", out var ie) && ie.ValueKind == JsonValueKind.String
                && c.TryGetProperty("Name", out var ne) && ne.ValueKind == JsonValueKind.String
                && !IsPlaceholderName(ne.GetString()))
                map[ie.GetString()!] = ne.GetString()!;
        return map;
    }

    /// <summary>Canonical GC grid name = parent CBE Name + " Garage Cleanup" (MergeBoardId → CBE id → name).
    /// Falls back to the GC's own non-placeholder Name, else null (caller → dialog §3.4).</summary>
    public static string? ResolveGcCanonicalName(JsonElement gc, IReadOnlyDictionary<string, string> globalCbe)
    {
        string? mb = gc.TryGetProperty("MergeBoardId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        string? cid = (mb != null && mb.EndsWith("_Board", StringComparison.Ordinal)) ? mb[..^6] : null;
        // Trim the parent name: some CBE names carry a trailing space (e.g. "Pirates of Hopewell Bay ")
        // which would otherwise produce a double space in "<name>  Garage Cleanup".
        if (cid != null && globalCbe.TryGetValue(cid, out var pn)) return $"{pn.Trim()} Garage Cleanup";
        var cur = gc.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return !IsPlaceholderName(cur) ? cur : null;
    }

    /// <summary>Airing year: the last 4-digit run in GarageCleanupEventId (GC_MaddieInParis2025 → 2025),
    /// else Schedule.Start year, else 0.</summary>
    public static int YearFromGc(JsonElement gc)
    {
        if (gc.TryGetProperty("GarageCleanupEventId", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var ms = Regex.Matches(id.GetString()!, @"\d{4}");
            if (ms.Count > 0 && int.TryParse(ms[^1].Value, out var y) && y >= 2000 && y < 2100) return y;
        }
        if (gc.TryGetProperty("ActivableParams", out var ap) && ap.ValueKind == JsonValueKind.Object
            && ap.TryGetProperty("Schedule", out var sc) && sc.ValueKind == JsonValueKind.Object
            && sc.TryGetProperty("Start", out var st) && st.ValueKind == JsonValueKind.String
            && DateTime.TryParse(st.GetString(), out var dt)) return dt.Year;
        return 0;
    }

    /// <summary>Copy of chain_item_odds.json with each chain's null/blank Name back-filled from the global
    /// chain map. Returns the written temp path (DataService reads a path).</summary>
    public static string NormalizeChainJson(string chainPath, IReadOnlyDictionary<string, string> globalChain, string outDir, string tag)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(chainPath));
        if (root?["Data"] is System.Text.Json.Nodes.JsonArray arr)
            foreach (var c in arr)
            {
                if (c == null) continue;
                string? ck = (c["ConfigKey"] is System.Text.Json.Nodes.JsonValue cv) ? cv.GetValue<string>() : null;
                string? nm = (c["Name"] is System.Text.Json.Nodes.JsonValue nv) ? nv.GetValue<string>() : null;
                if (ck != null && string.IsNullOrWhiteSpace(nm) && globalChain.TryGetValue(ck, out var real)) c["Name"] = real;
            }
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"chains_{tag}.json");
        File.WriteAllText(path, root!.ToJsonString());
        return path;
    }

    /// <summary>Copy of events.json with CBE Names healed from the global map and each GC's Name set to its
    /// canonical parent-derived name. When 2+ GCs in this dump resolve to the same name, disambiguate by year
    /// ONLY for distinct-year coexistence (Maddie 2025+2026); same-year duplicates (GreenAcres 2024+2024_01)
    /// keep the base name (cross-version split handles them). Returns the written temp path.</summary>
    public static string NormalizeEventsJson(string eventsPath, IReadOnlyDictionary<string, string> globalCbe, string outDir, string tag)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(eventsPath));
        var data = root?["Data"];

        if (data?["CollectibleBoards"] is System.Text.Json.Nodes.JsonArray cbs)
            foreach (var c in cbs)
            {
                string? id = (c?["CollectibleBoardEventId"] is System.Text.Json.Nodes.JsonValue iv) ? iv.GetValue<string>() : null;
                if (id != null && globalCbe.TryGetValue(id, out var gn)) c!["Name"] = gn;
            }

        if (data?["GarageCleanups"] is System.Text.Json.Nodes.JsonArray gcs)
        {
            var infos = new List<(System.Text.Json.Nodes.JsonNode Node, string? Canon, int Year)>();
            foreach (var g in gcs)
            {
                if (g == null) continue;
                using var gd = JsonDocument.Parse(g.ToJsonString());
                var canon = ResolveGcCanonicalName(gd.RootElement, globalCbe);
                var year = YearFromGc(gd.RootElement);
                infos.Add((g, canon, year));
            }
            var byCanon = new Dictionary<string, int>(StringComparer.Ordinal);
            var yearByCanon = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
            foreach (var (_, canon, year) in infos)
                if (canon != null)
                {
                    byCanon[canon] = byCanon.GetValueOrDefault(canon) + 1;
                    if (!yearByCanon.TryGetValue(canon, out var ym)) yearByCanon[canon] = ym = new();
                    ym[year] = ym.GetValueOrDefault(year) + 1;
                }
            foreach (var (node, canon, year) in infos)
            {
                if (canon == null) continue;
                bool suffix = byCanon[canon] >= 2 && year > 0 && yearByCanon[canon][year] == 1;
                node["Name"] = suffix ? $"{canon} ({year})" : canon;
            }
        }

        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"events_{tag}.json");
        File.WriteAllText(path, root!.ToJsonString());
        return path;
    }
}
