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
    public string? ReleaseDate { get; set; }
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
    public string? UnlockDate { get; set; }
    public Dictionary<string, int> TokenValues { get; set; } = new();
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
    public string? UnlockDate { get; set; }
    public Dictionary<string, int> TokenValues { get; set; } = new();
}

// ── Service ──────────────────────────────────────────────────────────

public class AreasService
{
    public List<LuaArea> Areas { get; private set; } = new();
    public string CreatedAt { get; private set; } = "";

    public async Task LoadAsync(string filePath)
    {
        Areas.Clear();
        await using var stream = File.OpenRead(filePath);
        var doc = await JsonDocument.ParseAsync(stream);

        if (doc.RootElement.TryGetProperty("CreatedAt", out var ca))
            CreatedAt = ca.GetString() ?? "";

        if (!doc.RootElement.TryGetProperty("Data", out var dataArray))
            throw new InvalidDataException("areas.json missing 'Data' array.");

        // Pass 1: build global hotspot lookup across all areas
        // (DOT graphs can reference tasks from other areas)
        var globalHotspots = new Dictionary<string, HotspotInfo>(StringComparer.Ordinal);
        foreach (var areaEl in dataArray.EnumerateArray())
        {
            foreach (var kv in BuildHotspotsLookup(areaEl))
                globalHotspots.TryAdd(kv.Key, kv.Value);
        }

        // Pass 2: parse areas with global hotspot fallback
        foreach (var areaEl in dataArray.EnumerateArray())
        {
            var area = ParseArea(areaEl, globalHotspots);
            if (area != null) Areas.Add(area);
        }
    }

    // ── Area parsing ─────────────────────────────────────────────────

    private static LuaArea? ParseArea(JsonElement el,
        Dictionary<string, HotspotInfo>? globalHotspots = null)
    {
        var name = GetStr(el, "Name");
        var areaId = GetStr(el, "AreaId");
        var taskDeps = GetStr(el, "TaskDependencies");

        if (string.IsNullOrEmpty(name)) return null;

        // Local hotspots first, merge multi-step CardStack groups, then global fallback
        var hotspots = BuildHotspotsLookup(el);
        MergeMultistepCardStackGroups(el, hotspots);
        var localHotspotIds = new HashSet<string>(hotspots.Keys, StringComparer.Ordinal);
        if (globalHotspots != null)
            foreach (var kv in globalHotspots)
                hotspots.TryAdd(kv.Key, kv.Value);

        var releaseDate = ParseTimeNeeded(el, "UnlockRequirements");
        var tasks = ParseTaskDependencies(taskDeps, hotspots, localHotspotIds);

        return new LuaArea
        {
            InternalName = name,
            DisplayName = BuildDisplayName(name),
            AreaId = areaId,
            ReleaseDate = releaseDate,
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
        string? ItemReward,
        string? UnlockDate,
        Dictionary<string, int> TokenValues);

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
            var unlockDate = ParseTimeNeeded(hs, "UnlockRequirementsList");
            var tokens = ParseTokenValues(hs);
            result[id] = new HotspotInfo(desc, reqs, xp, item, unlockDate, tokens);
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

            if (req.TryGetProperty("ItemAcquired", out var itemsEl))
            {
                if (itemsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var itemEl in itemsEl.EnumerateArray())
                    {
                        var itemRef = GetStr(itemEl, "ItemRef");
                        var amount = GetInt(itemEl, "Requirement");
                        if (!string.IsNullOrEmpty(itemRef))
                            reqs[itemRef] = amount;
                    }
                }
                else if (itemsEl.ValueKind == JsonValueKind.Object)
                {
                    // Older dump format: ItemAcquired is a single object, not an array
                    var itemRef = GetStr(itemsEl, "ItemRef");
                    var amount = GetInt(itemsEl, "Requirement");
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
            else if (req.TryGetProperty("CardStack", out _))
            {
                // CardStack requirement — extract items from CardStackRef.Cards
                ParseCardStackCards(hs, reqs);
            }
        }

        return reqs;
    }

    private static void ParseCardStackCards(JsonElement hs, Dictionary<string, int> reqs)
    {
        if (!hs.TryGetProperty("CardStackRef", out var stackRef) ||
            stackRef.ValueKind != JsonValueKind.Object)
            return;

        if (!stackRef.TryGetProperty("Cards", out var cards) ||
            cards.ValueKind != JsonValueKind.Array)
            return;

        // Group cards by Row (sequence layer)
        var rowItems = new Dictionary<int, Dictionary<string, int>>();
        foreach (var card in cards.EnumerateArray())
        {
            if (!card.TryGetProperty("ItemDef", out var itemDef) ||
                itemDef.ValueKind != JsonValueKind.Object)
                continue;

            var itemType = GetStr(itemDef, "ItemType");
            if (string.IsNullOrEmpty(itemType)) continue;

            var row = card.TryGetProperty("Row", out var rowEl) ? rowEl.GetInt32() : 0;

            if (!rowItems.TryGetValue(row, out var items))
                rowItems[row] = items = new Dictionary<string, int>(StringComparer.Ordinal);

            items[itemType] = items.GetValueOrDefault(itemType) + 1;
        }

        // Pair cancellation: within each sequence, identical pairs cancel out.
        // Only items with odd count contribute (count % 2 == 1 → need 1).
        foreach (var (_, items) in rowItems)
        {
            foreach (var (itemType, count) in items)
            {
                var needed = count % 2;
                if (needed > 0)
                    reqs[itemType] = reqs.GetValueOrDefault(itemType) + needed;
            }
        }
    }

    /// <summary>
    /// For CardStack tasks sharing a MultistepGroupId (e.g. Lounge poker, Speakeasy cards),
    /// merges all card requirements into the final task and removes intermediate tasks.
    /// The "final" task is the one not referenced as a parent by any other group member.
    /// Intermediate tasks are removed from the lookup and will be filtered out as empty.
    /// </summary>
    private static void MergeMultistepCardStackGroups(
        JsonElement areaEl,
        Dictionary<string, HotspotInfo> hotspots)
    {
        if (!areaEl.TryGetProperty("HotspotsRefs", out var hsArray) ||
            hsArray.ValueKind != JsonValueKind.Array)
            return;

        // Collect MultistepGroupId and parent refs for CardStack hotspots
        var groupMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var parentRefs = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var hs in hsArray.EnumerateArray())
        {
            var id = GetStr(hs, "Id");
            var type = GetStr(hs, "Type");
            var groupId = GetStr(hs, "MultistepGroupId");

            if (type != "CardStack" || string.IsNullOrEmpty(groupId)) continue;

            if (!groupMembers.TryGetValue(groupId, out var members))
                groupMembers[groupId] = members = new();
            members.Add(id);

            if (hs.TryGetProperty("UnlockingParentRefs", out var parArr) &&
                parArr.ValueKind == JsonValueKind.Array)
            {
                var parents = new List<string>();
                foreach (var p in parArr.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String)
                        parents.Add(p.GetString()!);
                parentRefs[id] = parents;
            }
        }

        foreach (var (groupId, members) in groupMembers)
        {
            if (members.Count <= 1) continue;

            // Final task = the one NOT referenced as parent by any other group member
            var memberSet = new HashSet<string>(members, StringComparer.Ordinal);
            var referencedAsParent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in members)
            {
                if (!parentRefs.TryGetValue(id, out var parents)) continue;
                foreach (var p in parents)
                    if (memberSet.Contains(p))
                        referencedAsParent.Add(p);
            }

            var finalId = members.FirstOrDefault(id => !referencedAsParent.Contains(id));
            if (finalId == null || !hotspots.TryGetValue(finalId, out var finalInfo)) continue;

            // Merge requirements from all intermediate tasks into the final task
            var mergedReqs = new Dictionary<string, int>(finalInfo.Requirements, StringComparer.Ordinal);
            foreach (var id in members)
            {
                if (id == finalId) continue;
                if (!hotspots.TryGetValue(id, out var memberInfo)) continue;

                foreach (var (key, amount) in memberInfo.Requirements)
                    mergedReqs[key] = mergedReqs.GetValueOrDefault(key) + amount;

                // Remove intermediate task — it will be filtered as empty in ParseTaskDependencies
                hotspots.Remove(id);
            }

            hotspots[finalId] = finalInfo with { Requirements = mergedReqs };
        }
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
            {
                item = GetStr(itemEl, "ItemDef");
                if (string.IsNullOrEmpty(item))
                    item = GetStr(itemEl, "ItemRef"); // older dump format
            }
        }

        return (xp, item);
    }

    private static readonly string[] TokenFieldNames =
    {
        "SoloMilestoneHotspotValue", "BoultonLeaguePoints",
        "DigEventTaps", "QuaternaryEnergy",
        "ClassicRacesSailPoints", "RollTheDiceToken", "BuilderEventToken"
    };

    private static Dictionary<string, int> ParseTokenValues(JsonElement hs)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in TokenFieldNames)
        {
            var val = GetInt(hs, name);
            if (val > 0) result[name] = val;
        }
        return result;
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
        Dictionary<string, HotspotInfo> hotspots,
        HashSet<string>? localHotspotIds = null)
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

        // Parse nodes — skip cross-area tasks (present in DOT as unlock parents from other areas)
        foreach (Match m in nodeRegex.Matches(normalized))
        {
            if (!int.TryParse(m.Groups[1].Value, out var dotIdx)) continue;
            var taskId = m.Groups[2].Value.Trim();

            // Skip tasks that belong to a different area
            if (localHotspotIds != null && !localHotspotIds.Contains(taskId))
                continue;

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
                ItemReward = hs?.ItemReward,
                UnlockDate = hs?.UnlockDate,
                TokenValues = hs?.TokenValues ?? new()
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

        // Remove empty tasks (no requirements + no rewards) and reroute edges
        var emptyNodes = byId.Values
            .Where(n => n.Requirements.Count == 0 && n.XpReward == null && string.IsNullOrEmpty(n.ItemReward))
            .ToList();

        foreach (var empty in emptyNodes)
        {
            foreach (var parent in empty.Parents)
            {
                parent.Children.Remove(empty);
                foreach (var child in empty.Children)
                    if (!parent.Children.Contains(child))
                        parent.Children.Add(child);
            }
            foreach (var child in empty.Children)
            {
                child.Parents.Remove(empty);
                foreach (var parent in empty.Parents)
                    if (!child.Parents.Contains(parent))
                        child.Parents.Add(parent);
            }

            byId.Remove(empty.Id);
            byDotIndex.Remove(empty.DotIndex);
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
                ItemReward = n.ItemReward,
                UnlockDate = n.UnlockDate,
                TokenValues = n.TokenValues
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

    // ── Date helpers ──────────────────────────────────────────────────

    private static string? ParseTimeNeeded(JsonElement el, string listPropName)
    {
        if (!el.TryGetProperty(listPropName, out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var req in arr.EnumerateArray())
        {
            if (req.ValueKind != JsonValueKind.Object) continue;
            if (req.TryGetProperty("TimeNeeded", out var tn) &&
                tn.TryGetProperty("StartInclusive", out var start) &&
                start.ValueKind == JsonValueKind.String)
            {
                var raw = start.GetString();
                if (!string.IsNullOrEmpty(raw) && DateTime.TryParse(raw, out var dt))
                    return dt.ToString("dd.MM.yyyy");
            }
        }

        return null;
    }

    // ── JSON helpers ─────────────────────────────────────────────────

    private static string GetStr(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        // Accept numeric values as strings (e.g., HotspotId integers when enum can't resolve)
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetRawText();
        return "";
    }

    private static int GetInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out var n)) return n;
        return 0;
    }
}
