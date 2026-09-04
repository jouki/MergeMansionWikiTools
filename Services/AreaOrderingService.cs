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
    string? TeaseStartDate,               // fallback ISO date from TeaseRequirements TimeNeeded.StartInclusive
    bool UnlockImpossible);               // UnlockRequirements contains literal "Impossible" — area can't be opened yet

public record DeducedEntry(
    string Name,
    int OrderingIndex,
    bool IsCommented);

public record RemovedCommentedEntry(
    string Name,
    double OrderingIndex);

public record RenamedEntry(
    string OldName,                       // stale key currently in the mapping
    string NewName,                       // area's current display name from areas.json
    double OrderingIndex,                 // kept as-is — rename never moves the slot
    bool IsCommented);                    // row is a `--["..."]` in-prep entry

public record StaleEntry(
    string Name,                          // mapping key with no counterpart in areas.json
    double OrderingIndex,
    bool IsCommented);

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

            var (unlockParent, unlockDate, unlockImpossible) = ExtractFirstParentAndDate(el, "UnlockRequirements");
            var (teaseParent, teaseDate, _) = ExtractFirstParentAndDate(el, "TeaseRequirements");

            result.Add(new AreaUnlockInfo(displayName, areaId, unlockParent, unlockDate, teaseParent, teaseDate, unlockImpossible));
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
        // Trim: game data occasionally ships names with stray whitespace (" Walk-in Closet",
        // 26.06.01) — untrimmed they never match mapping keys and read as stale rows.
        return name.Trim();
    }

    /// <summary>
    /// Fallback display name derived from an AreaId by splitting camelCase
    /// ("FirstFloorPantry" → "First Floor Pantry"). This is exactly what BuildDisplayName
    /// produced for areas whose localization was missing at dump time — and therefore the
    /// key to detecting renames: a stale mapping row whose name equals this fallback for
    /// some unmapped area belongs to that area (its real localization arrived later).
    /// </summary>
    public static string FallbackNameFromAreaId(string areaId)
        => Regex.Replace(areaId, @"([A-Z])", " $1").Trim();

    /// <summary>
    /// Detects mapping rows (active + commented) whose names no longer exist in areas.json.
    /// A stale row whose name equals <see cref="FallbackNameFromAreaId"/> of some area missing
    /// from the mapping is a RENAME (e.g. "First Floor Pantry" → "Pantry") and keeps its
    /// orderingIndex under the new name. Any other stale row is a DELETE.
    /// </summary>
    public static (List<RenamedEntry> Renames, List<StaleEntry> Deletes) DetectStaleEntries(
        IReadOnlyList<AreaUnlockInfo> allAreas,
        IReadOnlyDictionary<string, double> activeOrdering,
        IReadOnlyList<RemovedCommentedEntry> commentedEntries)
    {
        var currentNames = new HashSet<string>(allAreas.Select(a => a.Name), StringComparer.Ordinal);

        // Rename targets = areas NOT present in the mapping under their current name
        var mappedNames = new HashSet<string>(activeOrdering.Keys, StringComparer.Ordinal);
        foreach (var c in commentedEntries) mappedNames.Add(c.Name);
        var unmappedAreas = allAreas.Where(a => !mappedNames.Contains(a.Name)).ToList();

        var renames = new List<RenamedEntry>();
        var deletes = new List<StaleEntry>();
        var claimedTargets = new HashSet<string>(StringComparer.Ordinal);

        void Classify(string name, double idx, bool isCommented)
        {
            if (currentNames.Contains(name) || SkipNames.Contains(name)) return;
            var match = unmappedAreas.FirstOrDefault(a =>
                !claimedTargets.Contains(a.Name) &&
                string.Equals(FallbackNameFromAreaId(a.AreaId), name, StringComparison.Ordinal));
            if (match != null)
            {
                claimedTargets.Add(match.Name);
                renames.Add(new RenamedEntry(name, match.Name, idx, isCommented));
            }
            else
            {
                deletes.Add(new StaleEntry(name, idx, isCommented));
            }
        }

        foreach (var kv in activeOrdering.OrderBy(kv => kv.Value))
            Classify(kv.Key, kv.Value, isCommented: false);
        foreach (var c in commentedEntries)
            Classify(c.Name, c.OrderingIndex, isCommented: true);

        return (renames, deletes);
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
    /// KONVENCE: oblast s UnlockRequirements = Impossible je VŽDY commented (i když teased) — viz DeducedEntry.IsCommented.
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
                // KONVENCE (2026-06-11): dokud má oblast UnlockRequirements = Impossible, postuje se
                // JEN ZAKOMENTOVANÁ — i když už je teased (index se odvodí z Tease parenta normálně).
                // Odkomentuje se až ve chvíli, kdy hra unlock reálně umožní.
                output.Add(new DeducedEntry(c.Info.Name, nextIdx, c.Info.UnlockImpossible));
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
    /// 1. Removes existing `--["AreaName"] = {orderingIndex = N},` commented rows, ale POUZE pro
    ///    oblasti, které se v tomto patchi znovu vkládají (entries) — cizí komentované záznamy
    ///    (in-prep konvence: Unlock = Impossible ⇒ commented) zůstávají nedotčené.
    /// 2. Inserts new entries after the last legit `["..."] = {orderingIndex = N},` row inside the `p` table.
    /// Other comment styles (`-- text`, block comments `--[[...]]`) are left untouched.
    /// </summary>
    /// <summary>
    /// Applies a rename to a single module row line, preserving the whole value part and the
    /// '=' column where the name length allows it. Returns null when the line is not that row.
    /// The active pattern can't match commented rows (`--` breaks `^[ \t]*\[`) and vice versa.
    /// </summary>
    public static string? TryRenameRowLine(string line, RenamedEntry ren)
    {
        var rx = new Regex(
            @"^(?<lead>[ \t]*" + (ren.IsCommented ? "--" : "") + @")\[""" +
            Regex.Escape(ren.OldName) + @"""\](?<gap>[ \t]*)(?<rest>=.*)$");
        var m = rx.Match(line);
        if (!m.Success) return null;
        int newGap = Math.Max(1,
            m.Groups["gap"].Value.Length + ren.OldName.Length - ren.NewName.Length);
        return m.Groups["lead"].Value + "[\"" + ren.NewName + "\"]" +
               new string(' ', newGap) + m.Groups["rest"].Value;
    }

    /// <summary>
    /// Builds a unified-diff view of the mapping module for the ordering dialog: real module
    /// rows in file order (which is orderingIndex order), changed rows marked Added/Removed,
    /// unchanged rows between the first and last change kept as Match context (e.g. an in-prep
    /// commented row sitting between a rename and the new additions). Rows outside the changed
    /// span are omitted. Mirrors exactly what <see cref="PatchModuleContent"/> will do.
    /// Reuses <see cref="Models.DiffLine"/> (Mystery diff model) — Match = unchanged context.
    /// </summary>
    public static List<Models.DiffLine> BuildDiffPreview(
        string moduleContent,
        IReadOnlyList<DeducedEntry> entries,
        IReadOnlyList<RenamedEntry> renames,
        IReadOnlyList<StaleEntry> deletes)
    {
        var lines = moduleContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var rowRx = new Regex(
            @"^[ \t]*(?<c>--)?\[""(?<n>[^""]+)""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*[0-9.]+[^}]*\}\s*,?\s*$",
            RegexOptions.Compiled);

        static Models.DiffLine Line(Models.DiffLineType type, string text)
            => new() { Type = type, Text = text };

        var renameByKey = renames.ToDictionary(r => (r.OldName, r.IsCommented));
        var deleteKeys = new HashSet<(string, bool)>(deletes.Select(d => (d.Name, d.IsCommented)));
        var replacedCommented = new HashSet<string>(entries.Select(e => e.Name), StringComparer.Ordinal);

        var annotated = new List<Models.DiffLine>();
        foreach (var line in lines)
        {
            var m = rowRx.Match(line);
            if (!m.Success)
            {
                annotated.Add(Line(Models.DiffLineType.Match, line));
                continue;
            }
            var name = m.Groups["n"].Value;
            var commented = m.Groups["c"].Success;

            if (deleteKeys.Contains((name, commented)))
            {
                annotated.Add(Line(Models.DiffLineType.Removed, line));
            }
            else if (renameByKey.TryGetValue((name, commented), out var ren))
            {
                annotated.Add(Line(Models.DiffLineType.Removed, line));
                annotated.Add(Line(Models.DiffLineType.Added, TryRenameRowLine(line, ren) ?? line));
            }
            else if (commented && replacedCommented.Contains(name))
            {
                // Cleared in-prep row being re-inserted with a fresh index (shows as add below)
                annotated.Add(Line(Models.DiffLineType.Removed, line));
            }
            else
            {
                annotated.Add(Line(Models.DiffLineType.Match, line));
            }
        }

        if (entries.Count > 0)
        {
            // Each add lands at its orderingIndex slot among surviving rows (Removed rows are
            // ignored — an uncommented Parlor 62 must stay above the in-prep Atelier 63).
            var addLines = GeneratePreviewLua(entries).TrimEnd('\r', '\n')
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < entries.Count; i++)
            {
                int at = FindInsertIndexByOrdering(annotated,
                    d => d.Type == Models.DiffLineType.Removed ? null : d.Text,
                    entries[i].OrderingIndex, fallback: annotated.Count);
                annotated.Insert(at, Line(Models.DiffLineType.Added, addLines[i]));
            }
        }

        // Window: only the span from the first to the last change (keeps in-between context)
        int first = annotated.FindIndex(d => d.Type != Models.DiffLineType.Match);
        if (first < 0) return new List<Models.DiffLine>();
        int last = annotated.FindLastIndex(d => d.Type != Models.DiffLineType.Match);
        return annotated.GetRange(first, last - first + 1);
    }

    public static string PatchModuleContent(string moduleContent, IReadOnlyList<DeducedEntry> entries)
        => PatchModuleContent(moduleContent, entries, Array.Empty<RenamedEntry>(), Array.Empty<StaleEntry>());

    /// <summary>
    /// Full patcher: renames stale rows in place (keeping index + extra fields like right/bot),
    /// deletes stale rows with no counterpart in game data, then applies the original
    /// add/clear-commented logic. All passes are line-anchored — other comment styles untouched.
    /// </summary>
    public static string PatchModuleContent(
        string moduleContent,
        IReadOnlyList<DeducedEntry> entries,
        IReadOnlyList<RenamedEntry> renames,
        IReadOnlyList<StaleEntry> deletes)
    {
        if (entries.Count == 0 && renames.Count == 0 && deletes.Count == 0) return moduleContent;

        // Detect EOL style
        var newline = moduleContent.Contains("\r\n") ? "\r\n" : "\n";
        var lines = moduleContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

        // Pass 0a: renames — swap the key in place, keep the whole value part (incl. extras like
        // right/bot offsets). Padding is adjusted so the '=' column moves as little as possible.
        // Active pattern can't match commented rows (the `--` breaks `^[ \t]*\[`), and vice versa.
        foreach (var ren in renames)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var renamed = TryRenameRowLine(lines[i], ren);
                if (renamed == null) continue;
                lines[i] = renamed;
                break;
            }
        }

        // Pass 0b: deletes — drop stale rows entirely (active or commented per entry)
        foreach (var del in deletes)
        {
            var rx = new Regex(
                @"^[ \t]*" + (del.IsCommented ? "--" : "") + @"\[""" +
                Regex.Escape(del.Name) + @"""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*[0-9.]+[^}]*\}\s*,?\s*$");
            for (int i = lines.Count - 1; i >= 0; i--)
                if (rx.IsMatch(lines[i])) lines.RemoveAt(i);
        }

        if (entries.Count == 0)
            return string.Join(newline, lines);

        // Robust comment-row detection: line is `<spaces/tabs>--["NAME"] = {... orderingIndex = N ...},?`
        // Does NOT match `-- text` (no `[` after `--`) or `--[[` (block comment opener — second char is `[` not `"`)
        var commentedRowRegex = new Regex(
            @"^[ \t]*--\[""([^""]+)""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*[0-9.]+[^}]*\}\s*,?\s*$",
            RegexOptions.Compiled);

        // Legit (uncommented) row detection — same pattern without leading `--`
        var legitRowRegex = new Regex(
            @"^[ \t]*\[""([^""]+)""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*[0-9.]+[^}]*\}\s*,?\s*$",
            RegexOptions.Compiled);

        // Pass 1: remove commented orderingIndex rows being replaced by this patch
        var replacedNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.Ordinal);
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var m = commentedRowRegex.Match(lines[i]);
            if (m.Success && replacedNames.Contains(m.Groups[1].Value))
                lines.RemoveAt(i);
        }

        // Pass 2: insert each new row at its orderingIndex slot — before the first mapping row
        // (active OR commented) with a larger index, else after the last mapping row. Keeps the
        // file orderingIndex-sorted even when an in-prep row is uncommented with its index kept
        // (Parlor 62 must stay above the still-commented Atelier 63), not just for fresh
        // above-max indices. Fallback with no rows at all: append at end.
        var newLines = GeneratePreviewLua(entries).TrimEnd('\r', '\n')
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (int i = 0; i < entries.Count; i++)
        {
            int at = FindInsertIndexByOrdering(lines, l => l, entries[i].OrderingIndex, fallback: lines.Count);
            lines.Insert(at, newLines[i]);
        }

        return string.Join(newline, lines);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Mapping row (active or `--` in-prep) with its orderingIndex captured as group "i".</summary>
    private static readonly Regex MappingRowWithIndexRx = new(
        @"^[ \t]*(?<c>--)?\[""(?<n>[^""]+)""\]\s*=\s*\{[^}]*orderingIndex\s*=\s*(?<i>[0-9.]+)[^}]*\}\s*,?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Single source of truth for where a new mapping row belongs (used by both the patcher and
    /// the diff preview): index of the first surviving mapping row whose orderingIndex is greater
    /// than <paramref name="orderingIndex"/>; if none, right after the last mapping row; if the
    /// module has no rows at all, <paramref name="fallback"/>. <paramref name="textOf"/> returns
    /// null for rows that don't survive (e.g. Removed diff lines) so they never anchor the slot.
    /// </summary>
    internal static int FindInsertIndexByOrdering<T>(
        IList<T> rows, Func<T, string?> textOf, double orderingIndex, int fallback)
    {
        int lastRow = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            var text = textOf(rows[i]);
            if (text == null) continue;
            var m = MappingRowWithIndexRx.Match(text);
            if (!m.Success) continue;
            var idx = double.Parse(m.Groups["i"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (idx > orderingIndex) return i;
            lastRow = i;
        }
        return lastRow >= 0 ? lastRow + 1 : fallback;
    }

    private static (string? Parent, string? Date, bool Impossible) ExtractFirstParentAndDate(JsonElement el, string listProp)
    {
        if (!el.TryGetProperty(listProp, out var list)) return (null, null, false);
        // The property may be an array of requirement objects, OR the single string "Impossible"
        if (list.ValueKind == JsonValueKind.String)
            return (null, null, list.GetString() == "Impossible");
        if (list.ValueKind != JsonValueKind.Array) return (null, null, false);

        string? parent = null;
        string? date = null;
        bool impossible = false;
        foreach (var req in list.EnumerateArray())
        {
            // String elements inside the array — e.g. "Impossible" (areas.json shape: "UnlockRequirements": ["Impossible"])
            if (req.ValueKind == JsonValueKind.String)
            {
                if (req.GetString() == "Impossible") impossible = true;
                continue;
            }
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
        return (parent, date, impossible);
    }

    private static string GetStr(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    }
}
