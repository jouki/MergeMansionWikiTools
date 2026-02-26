using System.Globalization;
using System.Text;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public class LuaGeneratorService
{
    // ── Area Lua ──────────────────────────────────────────────────────

    /// <summary>
    /// Splits areas into labelled chunks and generates a Lua module for each.
    /// chunkSizes defines the size per chunk (e.g. [40, 30]). Areas beyond defined
    /// chunks default to 40 areas per chunk.
    /// Returns a list of (Label, Lua) pairs, e.g. ("1–40", "local p = ...").
    /// </summary>
    public List<(string Label, string Lua)> GenerateAreaChunks(
        List<LuaArea> areas,
        List<int> chunkSizes)
    {
        var result = new List<(string, string)>();
        if (areas.Count == 0) return result;

        var chunks = BuildChunkBoundaries(areas.Count, chunkSizes);
        foreach (var (start, end) in chunks)
        {
            var label = $"{start + 1}–{end + 1}";
            result.Add((label, GenerateAreasLua(areas, start, end)));
        }

        return result;
    }

    private static List<(int Start, int End)> BuildChunkBoundaries(int total, List<int> sizes)
    {
        var result = new List<(int, int)>();
        var fallback = 40; // areas beyond defined chunks always default to 40
        var cursor = 0;
        var sizeIdx = 0;

        while (cursor < total)
        {
            var size = sizeIdx < sizes.Count ? Math.Max(1, sizes[sizeIdx]) : fallback;
            var end = Math.Min(cursor + size - 1, total - 1);
            result.Add((cursor, end));
            cursor = end + 1;
            sizeIdx++;
        }

        return result;
    }

    private static string GenerateAreasLua(List<LuaArea> areas, int startIdx, int endIdx)
    {
        var sb = new StringBuilder();
        sb.Append("local p = {}\n\np.areas = {");

        var count = endIdx - startIdx + 1;
        for (int i = 0; i < count; i++)
        {
            var area = areas[startIdx + i];
            var comma = i < count - 1 ? "," : "";
            var tasks = BuildTasksTable(area.Tasks, 3);

            sb.Append($"\n\t[\"{Esc(area.DisplayName)}\"] = {{\n");
            sb.Append($"\t\tname = \"{Esc(area.DisplayName)}\",\n");
            sb.Append($"\t\tingameName = \"{Esc(area.InternalName)}\",\n");
            sb.Append($"\t\ttasks = {tasks}\n");
            sb.Append($"\t}}{comma}");
        }

        sb.Append("\n}\n\nreturn p");
        return sb.ToString();
    }

    private static string BuildTasksTable(List<LuaTask> tasks, int depth)
    {
        var p0 = new string('\t', depth - 1);
        var p1 = new string('\t', depth);
        var p2 = new string('\t', depth + 1);

        // Tasks are already filtered/sorted by AreasService, but filter again for safety
        var list = tasks
            .Where(t => t.ParentIds.Count > 0 || t.ChildIds.Count > 0)
            .OrderBy(t => t.Index)
            .ToList();

        if (list.Count == 0) return "{}";

        var sb = new StringBuilder();
        sb.Append("{");

        for (int i = 0; i < list.Count; i++)
        {
            var task = list[i];
            var comma = i < list.Count - 1 ? "," : "";

            sb.Append($"\n{p1}[\"{Esc(task.Id)}\"] = {{\n");
            sb.Append($"{p2}index = {task.Index},\n");
            sb.Append($"{p2}id = \"{Esc(task.Id)}\",\n");
            sb.Append($"{p2}desc = \"{Esc(task.Title)}\",");

            // Rewards
            if (task.XpReward.HasValue || task.ItemReward != null)
            {
                sb.Append($"\n{p2}rewards = {{");
                var rparts = new List<string>();
                if (task.XpReward.HasValue)
                    rparts.Add($"xp = {task.XpReward.Value}");
                if (!string.IsNullOrEmpty(task.ItemReward))
                    rparts.Add($"item = \"{Esc(task.ItemReward)}\"");
                sb.Append(string.Join(", ", rparts));
                sb.Append("},");
            }

            // Parents
            if (task.ParentIds.Count > 0)
            {
                sb.Append($"\n{p2}parents = {{");
                sb.Append(string.Join(", ", task.ParentIds.Select(id => $"\"{Esc(id)}\"")));
                sb.Append("},");
            }

            // Children
            if (task.ChildIds.Count > 0)
            {
                sb.Append($"\n{p2}children = {{");
                sb.Append(string.Join(", ", task.ChildIds.Select(id => $"\"{Esc(id)}\"")));
                sb.Append("},");
            }

            // Requirements
            if (task.Requirements.Count > 0)
            {
                sb.Append($"\n{p2}requirements = {{");
                sb.Append(string.Join(", ", task.Requirements.Select(
                    kv => $"{{name = \"{Esc(kv.Key)}\", amount = {kv.Value}}}")));
                sb.Append("}");
            }

            sb.Append($"\n{p1}}}{comma}");
        }

        sb.Append($"\n{p0}}}");
        return sb.ToString();
    }

    // ── Items + ChainNames Lua ────────────────────────────────────────

    /// <summary>
    /// Generates the combined items + chainNames Lua module.
    /// Output: local str = require('Module:Strings') / local p = {} / p.items = {...} / p.chainNames = {...} / return p
    /// </summary>
    public string GenerateCombinedItemsAndChainNamesLua(List<ParsedChain> chains)
    {
        var items = BuildFlatItems(chains);
        var itemsBlock = BuildItemsTable(items);
        var chainNamesBlock = BuildChainNamesTable(items);

        return $"local str = require('Module:Strings')\nlocal p = {{}}\n\n{itemsBlock}\n\n{chainNamesBlock}\n\nreturn p";
    }

    private sealed record FlatItem(
        string ItemType,
        string Name,
        int Level,
        bool IsGenerator,
        bool IsTemporary,
        string ChainName,
        Dictionary<string, double>? Odds,
        string Description);

    private static List<FlatItem> BuildFlatItems(List<ParsedChain> chains)
    {
        var list = new List<FlatItem>();
        foreach (var chain in chains)
        {
            foreach (var item in chain.Items)
            {
                if (string.IsNullOrEmpty(item.ItemType)) continue;
                list.Add(new FlatItem(
                    item.ItemType,
                    item.Name,
                    item.Level,
                    item.IsGenerator,
                    item.IsTemporary,
                    chain.DisplayName,
                    item.IsGenerator ? item.DropOdds : null,
                    item.Description));
            }
        }
        return list;
    }

    private static string BuildItemsTable(List<FlatItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("p.items = {");

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var comma = i < items.Count - 1 ? "," : "";

            sb.Append($"\n\t[\"{Esc(it.ItemType)}\"] = {{");
            sb.Append($"name = \"{Esc(it.Name)}\", ");
            sb.Append($"level = {it.Level}, ");

            if (it.IsGenerator) sb.Append("isGen = true, ");
            if (it.IsTemporary) sb.Append("isTemp = true, ");

            sb.Append($"chainName = \"{Esc(it.ChainName)}\", ");

            // odds
            if (it.Odds?.Count > 0)
            {
                var ordered = it.Odds
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal);
                sb.Append("odds = {");
                sb.Append(string.Join(", ", ordered.Select(kv =>
                    $"{{id = \"{Esc(kv.Key)}\", value = {kv.Value.ToString(CultureInfo.InvariantCulture)}}}")));
                sb.Append("}, ");
            }

            // desc — real newlines become Lua string concatenation with 'br'
            var desc = Esc(it.Description).Replace("\n", "\" .. br .. \"");
            sb.Append($"desc = \"{desc}\"");

            sb.Append($"}}{comma}");
        }

        sb.Append("\n}");
        return sb.ToString();
    }

    private static string BuildChainNamesTable(List<FlatItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("p.chainNames = {");

        var groups = items
            .Where(i => !string.IsNullOrWhiteSpace(i.ChainName))
            .GroupBy(i => i.ChainName)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var comma = gi < groups.Count - 1 ? "," : "";

            var ids = g
                .Where(x => !string.IsNullOrEmpty(x.ItemType))
                .OrderBy(x => x.Level)
                .ThenBy(x => x.ItemType, StringComparer.Ordinal)
                .Select(x => $"\"{Esc(x.ItemType)}\"");

            sb.Append($"\n\t[\"{Esc(g.Key)}\"] = {{ {string.Join(", ", ids)} }}{comma}");
        }

        sb.Append("\n}");
        return sb.ToString();
    }

    // ── Lua string escaping ───────────────────────────────────────────

    /// <summary>
    /// Escapes backslashes and double-quotes for use inside Lua double-quoted strings.
    /// Note: real newline characters are handled separately (replaced with Lua concatenation).
    /// </summary>
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
