using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

// ── Area models ──────────────────────────────────────────────────────

public class LuaArea
{
    public string InternalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AreaId { get; set; } = "";
    public List<LuaTask> Tasks { get; set; } = new();
}

public class LuaTask
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int Index { get; set; }
    public Dictionary<string, int> Requirements { get; set; } = new();
    public List<string> ParentIds { get; set; } = new();
    public List<string> ChildIds { get; set; } = new();
    public int? XpReward { get; set; }
    public string? ItemReward { get; set; }
}

// Internal helper — keeps task relationships during parsing
internal class TaskNode
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int DotIndex { get; set; }
    public int SortIndex { get; set; }
    public Dictionary<string, int> Requirements { get; set; } = new();
    public List<TaskNode> Parents { get; set; } = new();
    public List<TaskNode> Children { get; set; } = new();
    public int? XpReward { get; set; }
    public string? ItemReward { get; set; }
}

// ── Service ──────────────────────────────────────────────────────────

public class AreasService
{
    public List<LuaArea> Areas { get; private set; } = new();

    public async Task LoadAsync(string filePath)
    {
        Areas.Clear();
        await using var stream = File.OpenRead(filePath);
        var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("Data", out var dataArray))
            throw new InvalidDataException("areas.json missing 'Data' array.");

        foreach (var areaEl in dataArray.EnumerateArray())
        {
            var area = ParseArea(areaEl);
            if (area != null) Areas.Add(area);
        }
    }

    // ── Area parsing ─────────────────────────────────────────────────

    private static LuaArea? ParseArea(JsonElement el)
    {
        var name = GetStr(el, "Name");
        var areaId = GetStr(el, "AreaId");
        var taskDeps = GetStr(el, "TaskDependencies");

        if (string.IsNullOrEmpty(name)) return null;

        var hotspots = BuildHotspotsLookup(el);
        var tasks = ParseTaskDependencies(taskDeps, hotspots);

        return new LuaArea
        {
            InternalName = name,
            DisplayName = BuildDisplayName(name),
            AreaId = areaId,
            Tasks = tasks
        };
    }

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

    // ── Hotspots ─────────────────────────────────────────────────────

    private record HotspotInfo(
        string Description,
        Dictionary<string, int> Requirements,
        int? XpReward,
        string? ItemReward);

    private static Dictionary<string, HotspotInfo> BuildHotspotsLookup(JsonElement areaEl)
    {
        var result = new Dictionary<string, HotspotInfo>(StringComparer.Ordinal);

        if (!areaEl.TryGetProperty("HotspotsRefs", out var hsArray) ||
            hsArray.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var hs in hsArray.EnumerateArray())
        {
            var id = GetStr(hs, "Id");
            if (string.IsNullOrEmpty(id)) continue;

            var desc = GetStr(hs, "Description");
            var reqs = ParseHotspotRequirements(hs);
            var (xp, item) = ParseHotspotRewards(hs);
            result[id] = new HotspotInfo(desc, reqs, xp, item);
        }

        return result;
    }

    private static Dictionary<string, int> ParseHotspotRequirements(JsonElement hs)
    {
        var reqs = new Dictionary<string, int>(StringComparer.Ordinal);

        if (!hs.TryGetProperty("RequirementsList", out var reqList) ||
            reqList.ValueKind != JsonValueKind.Array)
            return reqs;

        foreach (var req in reqList.EnumerateArray())
        {
            if (req.ValueKind != JsonValueKind.Object) continue;

            if (req.TryGetProperty("ItemAcquired", out var itemsEl) &&
                itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemEl in itemsEl.EnumerateArray())
                {
                    var itemRef = GetStr(itemEl, "ItemRef");
                    var amount = GetInt(itemEl, "Requirement");
                    if (!string.IsNullOrEmpty(itemRef))
                        reqs[itemRef] = amount;
                }
            }
            else if (req.TryGetProperty("RequiredCost", out var costEl) &&
                     costEl.ValueKind == JsonValueKind.Object)
            {
                var type = GetStr(costEl, "Type");
                var amount = GetInt(costEl, "CurrencyAmount");
                if (!string.IsNullOrEmpty(type))
                    reqs[type] = amount;
            }
        }

        return reqs;
    }

    private static (int? xp, string? item) ParseHotspotRewards(JsonElement hs)
    {
        if (!hs.TryGetProperty("Rewards", out var rewardsEl) ||
            rewardsEl.ValueKind != JsonValueKind.Array)
            return (null, null);

        int? xp = null;
        string? item = null;

        foreach (var r in rewardsEl.EnumerateArray())
        {
            if (r.TryGetProperty("RewardExperience", out var xpEl) &&
                xpEl.ValueKind == JsonValueKind.Object)
                xp = GetInt(xpEl, "Amount");

            if (r.TryGetProperty("RewardItem", out var itemEl) &&
                itemEl.ValueKind == JsonValueKind.Object)
                item = GetStr(itemEl, "ItemDef");
        }

        return (xp, item);
    }

    // ── Task dependency parsing (DOT graph format) ───────────────────

    private static readonly Regex NodeRegex = new(
        @"(\d+)\[label=""([^""\\]+?)(?:\\r\\n|\\n|\r\n|\n|\\r\\n|\\n)([^""\\]*?)""\];?",
        RegexOptions.Compiled);

    private static readonly Regex EdgeRegex = new(
        @"(\d+)->(\d+);",
        RegexOptions.Compiled);

    private static readonly Regex RequirementRegex = new(
        @"([A-Za-z\s]+?)\s*x(\d+)",
        RegexOptions.Compiled);

    private static List<LuaTask> ParseTaskDependencies(
        string raw,
        Dictionary<string, HotspotInfo> hotspots)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();

        // Normalize escape sequences so regex can match consistently
        var normalized = raw
            .Replace(@"\r\n", "\n")
            .Replace(@"\n", "\n");

        // Re-run with simple newline regex after normalization
        var nodeRegex = new Regex(@"(\d+)\[label=""([^""\n]+?)\n([^""]*?)""\];?");
        var edgeRegex = EdgeRegex;

        // dotIndex → TaskNode
        var byDotIndex = new Dictionary<int, TaskNode>();
        // taskId → TaskNode
        var byId = new Dictionary<string, TaskNode>(StringComparer.Ordinal);

        // Parse nodes
        foreach (Match m in nodeRegex.Matches(normalized))
        {
            if (!int.TryParse(m.Groups[1].Value, out var dotIdx)) continue;
            var taskId = m.Groups[2].Value.Trim();
            var rest = m.Groups[3].Value.Trim();

            // Title and requirements come from HotspotsRefs (reliable JSON data)
            // DOT graph text is ignored — it mixes description with requirement labels
            hotspots.TryGetValue(taskId, out var hs);
            var reqs = hs?.Requirements ?? new Dictionary<string, int>();
            var title = hs?.Description;

            var node = new TaskNode
            {
                Id = taskId,
                Title = !string.IsNullOrEmpty(title) ? title : taskId,
                DotIndex = dotIdx,
                Requirements = reqs,
                XpReward = hs?.XpReward,
                ItemReward = hs?.ItemReward
            };

            byDotIndex[dotIdx] = node;
            byId[taskId] = node;
        }

        // Parse edges
        foreach (Match m in edgeRegex.Matches(normalized))
        {
            if (!int.TryParse(m.Groups[1].Value, out var pIdx) ||
                !int.TryParse(m.Groups[2].Value, out var cIdx)) continue;
            if (pIdx == cIdx) continue;

            if (!byDotIndex.TryGetValue(pIdx, out var parent) ||
                !byDotIndex.TryGetValue(cIdx, out var child)) continue;

            parent.Children.Add(child);
            child.Parents.Add(parent);
        }

        // Remove isolated nodes (no parents and no children)
        var connected = byId.Values
            .Where(n => n.Parents.Count > 0 || n.Children.Count > 0)
            .ToDictionary(n => n.Id, StringComparer.Ordinal);

        // Topological sort → assign sort indices
        AssignIndices(connected);

        // Build final LuaTask list
        return connected.Values
            .OrderBy(n => n.SortIndex)
            .Select(n => new LuaTask
            {
                Id = n.Id,
                Title = n.Title,
                Index = n.SortIndex,
                Requirements = n.Requirements,
                ParentIds = n.Parents.Where(p => connected.ContainsKey(p.Id)).Select(p => p.Id).ToList(),
                ChildIds = n.Children.Where(c => connected.ContainsKey(c.Id)).Select(c => c.Id).ToList(),
                XpReward = n.XpReward,
                ItemReward = n.ItemReward
            })
            .ToList();
    }

    private static (string title, string reqs) SplitTitleAndReqs(string text)
    {
        // Heuristic: if there's a capital letter after lowercase, that's the split point
        var m = Regex.Match(text, @"([a-z])([A-Z])");
        if (m.Success)
        {
            var splitAt = m.Index + 1;
            return (text[..splitAt].Trim(), text[splitAt..].Trim());
        }
        return (text.Trim(), "");
    }

    private static void AssignIndices(Dictionary<string, TaskNode> graph)
    {
        var sorted = new List<TaskNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inStack = new HashSet<string>(StringComparer.Ordinal);

        void Visit(TaskNode node)
        {
            if (inStack.Contains(node.Id)) return; // skip cycles
            if (visited.Contains(node.Id)) return;
            inStack.Add(node.Id);
            foreach (var child in node.Children.Where(c => graph.ContainsKey(c.Id)))
                Visit(child);
            inStack.Remove(node.Id);
            visited.Add(node.Id);
            sorted.Add(node);
        }

        foreach (var node in graph.Values.Where(n => n.Parents.Count == 0).OrderBy(n => n.Id))
            Visit(node);
        foreach (var node in graph.Values.Where(n => !visited.Contains(n.Id)).OrderBy(n => n.Id))
            Visit(node);

        sorted.Reverse(); // topological order (roots first)
        for (int i = 0; i < sorted.Count; i++)
            sorted[i].SortIndex = i + 1;
    }

    // ── JSON helpers ─────────────────────────────────────────────────

    private static string GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    private static int GetInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out var n)) return n;
        return 0;
    }
}
