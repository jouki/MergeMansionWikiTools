using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

// ── Models ───────────────────────────────────────────────────────────

public record ArchivedItem(string ChainName, string ItemId, string RawLuaEntry);

public class ArchiveDiff
{
    /// Items that were live on wiki but no longer in current local dump → move to archive.
    public List<ArchivedItem> NewlyArchived { get; init; } = new();
    /// Items that were in archive but now reappear in live data → remove from archive.
    public List<ArchivedItem> Restored { get; init; } = new();
    /// Items already in archive that stay (unchanged carry-over).
    public List<ArchivedItem> Carried { get; init; } = new();

    /// Final archive content after applying the diff: chain → id → raw Lua entry.
    public Dictionary<string, IReadOnlyDictionary<string, string>> FinalArchive { get; init; }
        = new(StringComparer.Ordinal);

    public bool HasChanges => NewlyArchived.Count > 0 || Restored.Count > 0;

    /// chain → list of all archived ids in FinalArchive (for chainNames generator).
    public Dictionary<string, IReadOnlyList<string>> ArchivedIdsByChain()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (chain, items) in FinalArchive)
            result[chain] = items.Keys.ToList();
        return result;
    }
}

// ── Service ──────────────────────────────────────────────────────────

public static class ItemsArchiveService
{
    public const string ArchiveModuleTitle = "Module:Datatable/Items/Archive";
    public const string ItemsConsumerModuleTitle = "Module:Items";

    // Markers injected by PatchConsumerModule. Idempotency / upgrade decisions use substring presence.
    public const string ArchiveLoaderMarker = "_archiveFlat = nil";
    // Distinguishes the v0.20.32+ "archive-priority" patch from the older "or-fallback" form.
    public const string ResolveItemPriorityMarker = "itemsData.archived and itemsData.archived[id]";

    /// <summary>
    /// Patches the consumer module (Module:Items) to:
    ///   1. Lazy-load the Archive module via closure (only paid when the page actually resolves
    ///      an archived id — live-only pages never parse Archive).
    ///   2. Make `resolveItem(id)` PREFER the archive entry when <c>p.archived[id] == true</c>:
    ///      handles both regular removed items AND broken-chain shadows (#missing# placeholder)
    ///      by overriding the live entry with the last-known-good wiki Lua data.
    ///
    /// Idempotent — when the priority marker is already present, returns input unchanged.
    /// Auto-upgrades the older "or-fallback" patch (v0.20.30/v0.20.31) to the new shape.
    /// Throws InvalidOperationException when anchors don't match (signals upstream rewrite of Module:Items).
    /// </summary>
    public static (string Patched, bool Changed) PatchConsumerModule(string content)
    {
        if (string.IsNullOrEmpty(content))
            throw new InvalidOperationException("Consumer module content is empty.");

        // Already on the latest patch shape — nothing to do.
        if (content.Contains(ResolveItemPriorityMarker))
            return (content, false);

        var patched = content;

        // ── 1. Inject lazy archive loader (idempotent via marker) ──
        if (!patched.Contains(ArchiveLoaderMarker))
        {
            var loader =
@"-- Archive lookup (Module:Datatable/Items/Archive) — lazy-loaded via closure;
-- only paid on the first page render that resolves a missing-from-live id (= archived).
-- mw.loadData caches the result across #invoke calls within the same page render.
local _archiveFlat = nil
local function archiveFlat()
	if _archiveFlat then return _archiveFlat end
	_archiveFlat = {}
	local ok, archiveData = pcall(mw.loadData, 'Module:Datatable/Items/Archive')
	if ok and archiveData and archiveData.items then
		for _, archivedItems in pairs(archiveData.items) do
			for id, item in pairs(archivedItems) do _archiveFlat[id] = item end
		end
	end
	return _archiveFlat
end

";
            var anchor = new System.Text.RegularExpressions.Regex(
                @"(\n-- Resolves effective item properties[^\n]*\n)?local function resolveItem\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            var anchorMatch = anchor.Match(patched);
            if (!anchorMatch.Success)
                throw new InvalidOperationException(
                    "Cannot patch Module:Items — `local function resolveItem` not found. Module structure may have changed.");
            patched = patched.Insert(anchorMatch.Index, loader);
        }

        // ── 2. Normalize older "or-fallback" forms back to plain assignment ──
        // (so the next pass below catches them uniformly). Two upstream shapes:
        //   • pre-v0.20.43:  `local item = itemsData.items[id]`        (direct mw.loadData proxy)
        //   • v0.20.43+:     `local item = _allItems[id]`              (perf cache after ensureMaterialised)
        // Both can have a previous "or archiveFlat()[id]" appended from the v0.20.30/.31 patch.
        patched = patched.Replace(
            "local item = itemsData.items[id] or archiveFlat()[id]",
            "local item = itemsData.items[id]");
        patched = patched.Replace(
            "local item = _allItems[id] or archiveFlat()[id]",
            "local item = _allItems[id]");

        // ── 3. Replace the first lookup line with archive-priority logic. Match either
        // upstream shape; capture which form to preserve it in the output. Negative
        // lookahead avoids re-patching when our priority block is already present.
        var rxItem = new System.Text.RegularExpressions.Regex(
            @"([ \t]*)local item = (itemsData\.items\[id\]|_allItems\[id\])(?!\s*\n[ \t]*if\s+itemsData\.archived)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var rxMatch = rxItem.Match(patched);
        if (!rxMatch.Success)
            throw new InvalidOperationException(
                "Cannot patch Module:Items — resolveItem first lookup line not found "
                + "(neither `local item = itemsData.items[id]` nor `local item = _allItems[id]`). "
                + "Module structure may have changed again.");

        var indent = rxMatch.Groups[1].Value; // preserve leading whitespace
        var lookupExpr = rxMatch.Groups[2].Value; // "itemsData.items[id]" or "_allItems[id]"
        var replacement =
            "local item = " + lookupExpr + "\n" +
            indent + "if itemsData.archived and itemsData.archived[id] then item = archiveFlat()[id] or item\n" +
            indent + "elseif not item then item = archiveFlat()[id] end";
        patched = patched.Substring(0, rxMatch.Index + indent.Length)
                + replacement
                + patched.Substring(rxMatch.Index + rxMatch.Length);

        return (patched, true);
    }

    /// <summary>
    /// Replaces `chainName = "#missing#…"` values in a chunk's Lua text with the last-known-good
    /// chain name for each broken-chain item id. Surgical regex match — only touches the specific
    /// item entries listed in <paramref name="corrections"/>.
    /// </summary>
    public static string PatchBrokenChainNamesInChunk(string chunkLua,
        IReadOnlyDictionary<string, string> corrections)
    {
        if (string.IsNullOrEmpty(chunkLua) || corrections.Count == 0) return chunkLua;

        var result = chunkLua;
        foreach (var (id, correctChain) in corrections)
        {
            // Match: ["id"] = { ... chainName = "#missing#…" ... }
            // Replace only the chainName field value within that item entry.
            var rx = new System.Text.RegularExpressions.Regex(
                @"(\[""" + System.Text.RegularExpressions.Regex.Escape(id) +
                @"""\]\s*=\s*\{[^\n]*chainName\s*=\s*"")[^""]+("")",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            var escapedChain = correctChain.Replace("\\", "\\\\").Replace("\"", "\\\"");
            result = rx.Replace(result, m => m.Groups[1].Value + escapedChain + m.Groups[2].Value, count: 1);
        }
        return result;
    }

    /// <summary>
    /// Parses Module:Datatable/Items/Archive content into chain → item id → raw Lua entry text.
    /// Expected structure: <code>p.items = { ["Chain"] = { ["item_id"] = { ... }, ... }, ... }</code>.
    /// Returns empty dict on null/empty input or parse failure.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> ParseArchive(string? archiveContent)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(archiveContent)) return result;

        // Locate p.items = { ... } block (top-level).
        var section = GetTopLevelTable(archiveContent, "items");
        if (section == null) return result;

        // Walk the section: find each `["Chain"] = {` at brace-depth 1 (relative to section start),
        // capture the matching closing brace, then extract child `["id"] = { ... }` entries inside.
        var chainRegex = new Regex(@"\[""([^""]+)""\]\s*=\s*\{", RegexOptions.Compiled);
        int idx = 0;
        while (idx < section.Length)
        {
            var m = chainRegex.Match(section, idx);
            if (!m.Success) break;
            var chain = m.Groups[1].Value;
            int chainStart = m.Index + m.Length - 1; // points at '{'
            int chainEnd = FindMatchingBrace(section, chainStart);
            if (chainEnd < 0) break;

            var inner = section.Substring(chainStart + 1, chainEnd - chainStart - 1);
            var items = ParseChainItems(inner);
            if (items.Count > 0)
                result[chain] = items;

            idx = chainEnd + 1;
        }
        return result;
    }

    /// <summary>
    /// Computes the archive diff:
    ///   - existingArchive: parsed archive (chain → id → raw)
    ///   - removedItems: id → raw Lua entry (items currently on wiki arbiter but NOT in local — about to be deleted)
    ///   - liveItemIds: all ids present in local data right now
    /// Returns the merged final archive + lists of newly-archived / restored / carried items.
    /// </summary>
    public static ArchiveDiff Compute(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> existingArchive,
        IReadOnlyDictionary<string, string> removedItems,
        ISet<string> liveItemIds)
    {
        var diff = new ArchiveDiff();
        var working = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // Carry over existing archive — drop any item that has reappeared in live data.
        // Buckets stay under whatever chain name the existing archive used (chainName field from
        // the original wiki entry) — historical chain names from wiki are preserved as-is.
        foreach (var (chain, items) in existingArchive)
        {
            var keep = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, raw) in items)
            {
                if (liveItemIds.Contains(id))
                    diff.Restored.Add(new ArchivedItem(chain, id, raw));
                else
                {
                    keep[id] = raw;
                    diff.Carried.Add(new ArchivedItem(chain, id, raw));
                }
            }
            if (keep.Count > 0)
                working[chain] = keep;
        }

        // Add newly-removed items. Bucket key = chainName field extracted from the wiki Lua entry —
        // preserves whatever chain name was originally on the wiki (no normalization).
        foreach (var (id, raw) in removedItems)
        {
            var chainName = ExtractChainName(raw);
            if (string.IsNullOrEmpty(chainName)) continue;
            if (!working.TryGetValue(chainName, out var bucket))
                working[chainName] = bucket = new Dictionary<string, string>(StringComparer.Ordinal);
            bucket[id] = raw;
            diff.NewlyArchived.Add(new ArchivedItem(chainName, id, raw));
        }

        foreach (var (chain, items) in working)
            diff.FinalArchive[chain] = items;
        return diff;
    }

    // ── Internals ────────────────────────────────────────────────────

    private static Dictionary<string, string> ParseChainItems(string innerLua)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var entryRegex = new Regex(@"\[""([^""]+)""\]\s*=\s*\{", RegexOptions.Compiled);
        int idx = 0;
        while (idx < innerLua.Length)
        {
            var m = entryRegex.Match(innerLua, idx);
            if (!m.Success) break;
            var id = m.Groups[1].Value;
            int braceStart = m.Index + m.Length - 1;
            int braceEnd = FindMatchingBrace(innerLua, braceStart);
            if (braceEnd < 0) break;

            // Capture the entire `{ ... }` text verbatim — that's the raw entry to round-trip.
            var raw = innerLua.Substring(braceStart, braceEnd - braceStart + 1);
            result[id] = raw;
            idx = braceEnd + 1;
        }
        return result;
    }

    /// Finds the position of the closing `}` matching the `{` at <paramref name="openIdx"/>.
    /// Naive brace counting — does NOT respect strings/comments, but Lua entries we generate
    /// don't contain `{` or `}` inside strings, so this is sufficient.
    private static int FindMatchingBrace(string s, int openIdx)
    {
        if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '{') return -1;
        int depth = 0;
        bool inString = false;
        char stringQuote = '\0';
        for (int i = openIdx; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == stringQuote) inString = false;
                continue;
            }
            if (c == '"' || c == '\'') { inString = true; stringQuote = c; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// Extracts the inside of `p.<tableName> = { ... }` (the contents between outer braces).
    /// Returns null if the table can't be located.
    private static string? GetTopLevelTable(string content, string tableName)
    {
        var m = Regex.Match(content, $@"p\.{Regex.Escape(tableName)}\s*=\s*\{{");
        if (!m.Success) return null;
        int start = m.Index + m.Length - 1; // points at '{'
        int end = FindMatchingBrace(content, start);
        if (end < 0) return null;
        return content.Substring(start + 1, end - start - 1);
    }

    private static string? ExtractChainName(string rawLuaEntry)
    {
        var m = Regex.Match(rawLuaEntry, @"chainName\s*=\s*""([^""]+)""");
        return m.Success ? m.Groups[1].Value : null;
    }
}
