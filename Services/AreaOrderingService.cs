using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

// ── Models ───────────────────────────────────────────────────────────

public record AreaUnlockInfo(
    string Name,                          // human display name (e.g. "Factory Floor") — key in Lua mapping
    string AreaId,                        // internal id (e.g. "FactoryFloor") — referenced by AreaCompleted
    string? UnlockAreaCompleted,          // parent areaId from UnlockRequirements (null if Impossible / no AreaCompleted)
    string? UnlockStartDate,              // ISO date string from UnlockRequirements TimeNeeded.StartInclusive
    string? TeaseAreaCompleted,           // fallback parent areaId from TeaseRequirements
    string? TeaseStartDate);              // fallback ISO date from TeaseRequirements TimeNeeded.StartInclusive

public record DeducedEntry(
    string Name,
    int OrderingIndex,
    bool IsCommented);

public record RemovedCommentedEntry(
    string Name,
    double OrderingIndex);

// ── Service ──────────────────────────────────────────────────────────

public static class AreaOrderingService
{
    /// <summary>
    /// Hardcoded list of Names that should be skipped from missing-ordering deduction.
    /// These are tutorial/intro areas that don't need an ordering index.
    /// </summary>
    public static readonly HashSet<string> SkipNames = new(StringComparer.Ordinal)
    {
        "Maddie Meets Mansion"
    };

    /// <summary>
    /// Loads all areas from areas.json and extracts their unlock/tease parent areaId
    /// and date for ordering deduction.
    /// </summary>
    public static async Task<List<AreaUnlockInfo>> LoadFromAreasJsonAsync(string path)
    {
        var result = new List<AreaUnlockInfo>();

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream);
        if (!doc.RootElement.TryGetProperty("Data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var el in data.EnumerateArray())
        {
            var rawName = GetStr(el, "Name");
            var areaId = GetStr(el, "AreaId");
            if (string.IsNullOrEmpty(rawName) || string.IsNullOrEmpty(areaId)) continue;

            // The Lua mapping uses DisplayName (e.g. "First Floor Kitchen") — same logic as AreasService.BuildDisplayName.
            // For unresolved Names like "HotspotTitle_FirstFloorKitchen", strip prefix + split camelCase.
            var displayName = BuildDisplayName(rawName);

            var (unlockParent, unlockDate) = ExtractFirstParentAndDate(el, "UnlockRequirements");
            var (teaseParent, teaseDate) = ExtractFirstParentAndDate(el, "TeaseRequirements");

            result.Add(new AreaUnlockInfo(displayName, areaId, unlockParent, unlockDate, teaseParent, teaseDate));
        }
        return result;
    }

    /// <summary>
    /// Mirrors AreasService.BuildDisplayName so the Name field matches the Lua mapping key.
    /// "HotspotTitle_FirstFloorKitchen" → "First Floor Kitchen" (when LocMan can't resolve).
    /// "Factory Floor" (already resolved) → unchanged.
    /// </summary>
    private static string BuildDisplayName(string name)
    {
        if (name.Contains('_'))
        {
            var parts = name.Split('_');
            if (parts.Length >= 2)
                return Regex.Replace(parts[1], @"([A-Z])", " $1").Trim();
        }
        return name;
    }

    /// <summary>
    /// Extracts existing commented orderingIndex rows from module text — used to display REMOVE diff.
    /// Matches only `--["Name"] = {orderingIndex = N},` shape (NOT generic `-- text` or `--[[ block ]]` comments).
    /// </summary>
    public static List<RemovedCommentedEntry> ExtractCommentedEntries(string moduleContent)
    {
        var result = new List<RemovedCommentedEntry>();
        if (string.IsNullOrEmpty(moduleContent)) return result;

        var rx = new Regex(
            @"^[ \t]*--\[""([^""]+)""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*([0-9.]+)[^}]*\}\s*,?\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        foreach (Match m in rx.Matches(moduleContent))
        {
            if (double.TryParse(m.Groups[2].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var idx))
                result.Add(new RemovedCommentedEntry(m.Groups[1].Value, idx));
        }
        return result;
    }

    /// <summary>
    /// Deduces orderingIndex for each missing area name.
    /// Uses topological resolution from UnlockRequirements.AreaCompleted (fallback TeaseRequirements).
    /// Sibling order under the same parent: by UnlockStartDate asc (fallback TeaseStartDate, then Name).
    /// Areas without a resolvable parent (Impossible/cycle) are bucketed at lastValid+1, all sharing the same index, marked commented.
    /// </summary>
    public static List<DeducedEntry> Deduce(
        IReadOnlyList<AreaUnlockInfo> allAreas,
        IReadOnlyDictionary<string, double> existingOrdering,
        IEnumerable<string> missingNames)
    {
        // Index helpers
        var nameByAreaId = new Dictionary<string, string>(StringComparer.Ordinal);
        var infoByName = new Dictionary<string, AreaUnlockInfo>(StringComparer.Ordinal);
        foreach (var a in allAreas)
        {
            nameByAreaId.TryAdd(a.AreaId, a.Name);
            infoByName.TryAdd(a.Name, a);
        }

        // Working set — start with all existing ordering, will grow as we resolve
        var resolved = new Dictionary<string, double>(existingOrdering, StringComparer.Ordinal);

        // Filter missing: skip tutorial names + names not present in areas.json
        var unresolved = new List<AreaUnlockInfo>();
        foreach (var n in missingNames)
        {
            if (SkipNames.Contains(n)) continue;
            if (infoByName.TryGetValue(n, out var info))
                unresolved.Add(info);
        }

        // Compute starting index = floor(max existing index) + 1
        int nextIdx = (int)Math.Floor(existingOrdering.Count > 0 ? existingOrdering.Values.Max() : 0.0) + 1;

        var output = new List<DeducedEntry>();

        // Topological loop
        while (unresolved.Count > 0)
        {
            var candidates = new List<(AreaUnlockInfo Info, string ParentName, double ParentIdx, string Date)>();
            foreach (var u in unresolved)
            {
                var parentAreaId = u.UnlockAreaCompleted ?? u.TeaseAreaCompleted;
                if (parentAreaId == null) continue;
                if (!nameByAreaId.TryGetValue(parentAreaId, out var parentName)) continue;
                if (!resolved.TryGetValue(parentName, out var parentIdx)) continue;

                var date = u.UnlockStartDate ?? u.TeaseStartDate ?? "9999-99-99";
                candidates.Add((u, parentName, parentIdx, date));
            }

            if (candidates.Count == 0) break;

            // Sort: parent index asc, then date asc, then name asc
            candidates.Sort((a, b) =>
            {
                int c = a.ParentIdx.CompareTo(b.ParentIdx);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Date, b.Date);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Info.Name, b.Info.Name);
            });

            foreach (var c in candidates)
            {
                output.Add(new DeducedEntry(c.Info.Name, nextIdx, false));
                resolved[c.Info.Name] = nextIdx;
                unresolved.Remove(c.Info);
                nextIdx++;
            }
        }

        // Remaining unresolved → all share nextIdx (= lastValid+1), commented out
        if (unresolved.Count > 0)
        {
            // Sort impossible bucket by name for stable output
            unresolved.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var u in unresolved)
                output.Add(new DeducedEntry(u.Name, nextIdx, true));
        }

        return output;
    }

    /// <summary>
    /// Renders the deduced entries as Lua lines that can be inserted into Module:Datatable/Areas/Mapping.
    /// Format mirrors existing entries: `["Name"]   = {orderingIndex = N},` (commented entries prefixed with `--`).
    /// </summary>
    public static string GeneratePreviewLua(IReadOnlyList<DeducedEntry> entries)
    {
        if (entries.Count == 0) return string.Empty;

        // Align '=' column for visual consistency. Keep tabs for the prefix part to match existing module style.
        // Existing module uses a tab + name + spaces to align '='. We'll use 1 tab + bracketed name + space-padding.
        int maxKey = entries.Max(e => (e.IsCommented ? 2 : 0) + 4 + e.Name.Length); // [" + name + "]
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            var prefix = e.IsCommented ? "--" : "";
            var keyPart = $"{prefix}[\"{e.Name}\"]";
            sb.Append('\t');
            sb.Append(keyPart);
            // Pad to align '='
            int padCount = Math.Max(1, maxKey - keyPart.Length + 1);
            sb.Append(' ', padCount);
            sb.Append("= {orderingIndex = ");
            sb.Append(e.OrderingIndex);
            sb.AppendLine("},");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Patches the raw Lua module content:
    /// 1. Removes any existing `--["AreaName"] = {orderingIndex = N},` commented rows (robust — only this exact shape).
    /// 2. Inserts new entries after the last legit `["..."] = {orderingIndex = N},` row inside the `p` table.
    /// Other comment styles (`-- text`, block comments `--[[...]]`) are left untouched.
    /// </summary>
    public static string PatchModuleContent(string moduleContent, IReadOnlyList<DeducedEntry> entries)
    {
        if (entries.Count == 0) return moduleContent;

        // Detect EOL style
        var newline = moduleContent.Contains("\r\n") ? "\r\n" : "\n";
        var lines = moduleContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

        // Robust comment-row detection: line is `<spaces/tabs>--["NAME"] = {... orderingIndex = N ...},?`
        // Does NOT match `-- text` (no `[` after `--`) or `--[[` (block comment opener — second char is `[` not `"`)
        var commentedRowRegex = new Regex(
            @"^[ \t]*--\[""([^""]+)""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*[0-9.]+[^}]*\}\s*,?\s*$",
            RegexOptions.Compiled);

        // Legit (uncommented) row detection — same pattern without leading `--`
        var legitRowRegex = new Regex(
            @"^[ \t]*\[""([^""]+)""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*[0-9.]+[^}]*\}\s*,?\s*$",
            RegexOptions.Compiled);

        // Pass 1: remove all commented orderingIndex rows
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (commentedRowRegex.IsMatch(lines[i]))
                lines.RemoveAt(i);
        }

        // Pass 2: find the last legit row → that's the insertion point (insert after it)
        int lastLegitIdx = -1;
        for (int i = 0; i < lines.Count; i++)
            if (legitRowRegex.IsMatch(lines[i])) lastLegitIdx = i;
        if (lastLegitIdx < 0)
            // Fallback: append before final `}` of `p` table; if none, append at end
            lastLegitIdx = lines.Count - 1;

        // Build new lines (without trailing newline since we'll insert as separate list elements)
        var preview = GeneratePreviewLua(entries);
        // Strip trailing newline and split, so we get clean lines
        var newLines = preview.TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        // Insert after lastLegitIdx (so the new lines come right after the last legit row)
        lines.InsertRange(lastLegitIdx + 1, newLines);

        return string.Join(newline, lines);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (string? Parent, string? Date) ExtractFirstParentAndDate(JsonElement el, string listProp)
    {
        if (!el.TryGetProperty(listProp, out var list)) return (null, null);
        // The property may be an array of requirement objects, OR the single string "Impossible"
        if (list.ValueKind != JsonValueKind.Array) return (null, null);

        string? parent = null;
        string? date = null;
        foreach (var req in list.EnumerateArray())
        {
            // Strings inside the array (e.g. "Impossible") — skip
            if (req.ValueKind != JsonValueKind.Object) continue;
            // AreaCompleted: "FactoryFloor"
            if (parent == null && req.TryGetProperty("AreaCompleted", out var ac) &&
                ac.ValueKind == JsonValueKind.String)
                parent = ac.GetString();
            // TimeNeeded: { StartInclusive: "..." }
            if (date == null && req.TryGetProperty("TimeNeeded", out var tn) &&
                tn.TryGetProperty("StartInclusive", out var si) &&
                si.ValueKind == JsonValueKind.String)
                date = si.GetString();
        }
        return (parent, date);
    }

    private static string GetStr(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    }
}
