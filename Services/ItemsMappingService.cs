using System.Text;
using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

// ── Models ───────────────────────────────────────────────────────────

/// Represents a single multinameMappings entry.
/// FieldOrder preserves the on-wiki ordering so re-emission is diff-friendly.
public class MappingEntry
{
    public string Id { get; init; } = "";
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.Ordinal);
    public List<string> FieldOrder { get; init; } = new();

    public string EmitInnerLua()
    {
        var sb = new StringBuilder();
        bool first = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in FieldOrder)
        {
            if (!Fields.TryGetValue(key, out var val)) continue;
            if (!first) sb.Append(", ");
            sb.Append(key).Append(" = ").Append(val);
            first = false;
            seen.Add(key);
        }
        foreach (var (key, val) in Fields)
        {
            if (seen.Contains(key)) continue;
            if (!first) sb.Append(", ");
            sb.Append(key).Append(" = ").Append(val);
            first = false;
        }
        return sb.ToString();
    }
}

// ── Service ──────────────────────────────────────────────────────────

public static class ItemsMappingService
{
    public const string MappingModuleTitle = "Module:Datatable/Items/Mapping";

    /// <summary>
    /// Parses <c>Module:Datatable/Items/Mapping</c> content and extracts entries from the
    /// <c>multinameMappings</c> table. Returns id → MappingEntry.
    /// Supports single-line entries: <c>["id"] = {field1 = val1, field2 = val2, ...}</c>.
    /// </summary>
    public static Dictionary<string, MappingEntry> ParseMappingModule(string? content)
    {
        var result = new Dictionary<string, MappingEntry>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content)) return result;

        var section = ExtractTableSection(content, "multinameMappings");
        if (section == null) return result;

        var rxEntry = new Regex(@"\[""([^""]+)""\]\s*=\s*\{([^\n}]*)\}", RegexOptions.Compiled);
        foreach (Match m in rxEntry.Matches(section))
        {
            var id = m.Groups[1].Value;
            var inner = m.Groups[2].Value;
            var (fields, order) = ParseLuaFields(inner);
            result[id] = new MappingEntry { Id = id, Fields = fields, FieldOrder = order };
        }
        return result;
    }

    /// <summary>
    /// Splits a Lua flat table inner text (e.g. <c>k1 = v1, k2 = v2</c>) into ordered fields.
    /// Handles nested <c>{...}</c> and quoted strings. Returns (Dict, OrderedKeys).
    /// </summary>
    public static (Dictionary<string, string> Fields, List<string> Order) ParseLuaFields(string inner)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var part in SplitLuaFields(inner))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var eq = FindTopLevelEquals(trimmed);
            if (eq < 0) continue;
            var key = trimmed.Substring(0, eq).Trim();
            var value = trimmed.Substring(eq + 1).Trim();
            // Strip surrounding [ "..." ] from non-string keys (rare in flat tables, but defensive).
            if (key.StartsWith("[", StringComparison.Ordinal) && key.EndsWith("]", StringComparison.Ordinal))
                key = key.Substring(1, key.Length - 2).Trim().Trim('"');
            if (fields.TryAdd(key, value))
                order.Add(key);
            else
                fields[key] = value; // duplicate key — overwrite, keep first occurrence in order
        }
        return (fields, order);
    }

    /// <summary>
    /// Patches the raw mapping module content by replacing the inner text of each named entry.
    /// Preserves outer braces, indentation, and trailing commas. Idempotent — entries not in
    /// <paramref name="newInners"/> are left untouched.
    /// </summary>
    public static string PatchMappingEntries(string content, IReadOnlyDictionary<string, string> newInners)
    {
        if (string.IsNullOrEmpty(content) || newInners.Count == 0) return content;
        var result = content;
        foreach (var (id, newInner) in newInners)
        {
            var rx = new Regex(
                @"(\[""" + Regex.Escape(id) + @"""\]\s*=\s*)\{[^\n}]*\}",
                RegexOptions.Compiled);
            // Use match callback to avoid backreference interpretation in replacement.
            result = rx.Replace(result, m => m.Groups[1].Value + "{" + newInner + "}", count: 1);
        }
        return result;
    }

    // ── Internals ────────────────────────────────────────────────────

    private static string? ExtractTableSection(string content, string tableName)
    {
        // Match "p.<tableName> = { ... }" (or "<tableName> = { ... }" inside p table).
        // Use brace-depth tracking to find the matching closing brace.
        var rx = new Regex(@"(?:^|\n|\W)" + Regex.Escape(tableName) + @"\s*=\s*\{");
        var m = rx.Match(content);
        if (!m.Success) return null;
        int braceStart = content.IndexOf('{', m.Index);
        if (braceStart < 0) return null;
        int braceEnd = FindMatchingBrace(content, braceStart);
        if (braceEnd < 0) return null;
        return content.Substring(braceStart + 1, braceEnd - braceStart - 1);
    }

    private static int FindMatchingBrace(string s, int openIdx)
    {
        if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '{') return -1;
        int depth = 0;
        bool inString = false;
        char sq = '\0';
        for (int i = openIdx; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == sq) inString = false;
                continue;
            }
            if (c == '"' || c == '\'') { inString = true; sq = c; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<string> SplitLuaFields(string inner)
    {
        var result = new List<string>();
        int depth = 0;
        bool inString = false;
        char sq = '\0';
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == sq) inString = false;
                continue;
            }
            if (c == '"' || c == '\'') { inString = true; sq = c; continue; }
            if (c == '{' || c == '(' || c == '[') depth++;
            else if (c == '}' || c == ')' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(inner.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (start < inner.Length) result.Add(inner.Substring(start));
        return result;
    }

    private static int FindTopLevelEquals(string s)
    {
        int depth = 0;
        bool inString = false;
        char sq = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == sq) inString = false;
                continue;
            }
            if (c == '"' || c == '\'') { inString = true; sq = c; continue; }
            if (c == '{' || c == '(' || c == '[') depth++;
            else if (c == '}' || c == ')' || c == ']') depth--;
            else if (c == '=' && depth == 0) return i;
        }
        return -1;
    }
}
