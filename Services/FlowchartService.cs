using System.Globalization;
using System.Text;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Generates SVG flowcharts from area task dependency graphs.
/// Uses Sugiyama layered layout with transitive reduction and orthogonal edge routing.
/// </summary>
internal class FlowchartService
{
    // ── Internal models ──────────────────────────────────────────────

    private class FNode
    {
        public string Id = "";
        public string Title = "";
        public int DisplayIndex;
        public List<FReq> Requirements = new();
        public int? XpReward;
        public string? ItemRewardText;
        public string? ItemRewardType;        // raw item type key for icon lookup
        public string? ItemRewardWikiName;     // chain display name for wiki link
        public bool ItemRewardSingleLevel;     // true when chain has only 1 unique level
        public Dictionary<string, int> TokenValues = new(); // field name → value
        public HashSet<string> Parents = new();
        public HashSet<string> Children = new();
        public bool IsDummy;

        // Layout
        public int Layer;
        public int Order;
        public double X, Y, Width, Height;
        public int HeaderLines = 1;

        // Link references (saved before SugiyamaLayout modifies Parents/Children)
        public List<int> ParentDisplayIndices = new();
        public List<int> ChildDisplayIndices = new();
    }

    private class FReq
    {
        public int Qty;
        public string ItemName = "";   // "Wheelbarrow [L5]"
        public string? Tooltip;        // chain display name
        public string ItemType = "";   // raw item type key for icon lookup
        public string? ChainWikiName;  // chain display name for wiki link (e.g. "Sewing Supplies")
        public bool HasIcon;           // true when itemIcons has base64 for this item
    }

    // ── Constants ────────────────────────────────────────────────────

    const double PadX = 8;
    const double PadY = 7;
    const double HeaderH = 34;
    const double LineH = 18;
    const double SepGap = 8;
    const double LayerGap = 55;
    const double NodeGapX = 35;
    const double MaxNodeW = 280;
    const double ArrowSize = 7;

    const double FontSzHeader = 12;
    const double FontSzTitle = 13;
    const double FontSzItem = 11.5;
    const double FontSzReward = 11;

    const double IndexColW = 24;    // width for right-aligned "#NNN"
    const double IndexTitleGap = 6; // gap between index and title
    const double QtyColW = 24;      // width for right-aligned "99x"
    const double QtyNameGap = 6;    // gap between qty and item name

    const double IconSize = 24;     // XP icon display size
    const double LinkFontSz = 9;    // font size for parent/child link references
    const double LinkLineH = 12;    // line height for link section
    const double LinkSectionPad = 3; // padding around link sections
    const double ItemSlotSize = 42;    // outer slot rectangle size
    const double ItemIconDisplay = 32; // displayed image size inside slot
    const double ItemSlotCorner = 6;   // slot corner radius
    const double ItemSlotGap = 6;      // gap between slot and qty column
    const double BusOffset = 20;    // how far below source the edge bus is
    const double EdgePad = 0;       // gap between node boundary and edge start/end
    const double SnapThresh = 5;    // snap small X offsets to straight
    const double AlignThresh = 20;  // snap drift in dummy waypoints
    const double BendRadius = 7;    // default rounded corner radius on edge bends
    const double BendRadiusLarge = 12; // larger radius for divergence/convergence junction bends
    const int MaxTitleChars = 37;     // max chars before title wraps to 2 lines

    // ── XP icon (base64 PNG) ────────────────────────────────────────

    private const string CHECKMARK_ICON_BASE64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEQAAABECAMAAAAPzWOAAAACu1BMVEUAAAADCwMhRBAiRBEjNBIDNQAEDQA0Yx8cYAATbgAzVREiRBEqXRkOLAoAAgAAAQAAAwBEqhFEmRZTqRUtYBwdcwAnZQREmRFguzdNgDwyZSFEmREbcAAjVhIzVREqkAARRAAiRBEhVBANLw1UuhBUuhBguzdmzCI+pABguzdNgDw6XClRhEBguzdRhEBguzc+pAARMwD8/9NmzCJ43jNEqhBs0ihLqxRmzCZx1y1bvR///9RZux1t2Ct53TB02zBlyiRVtxz//9Jx2yx73jFbwShJpxH+/9xmzihXuR1JqRNozyhhxiROrhVRtBhJphGC4TR93zJr0ytr1SpUtxpPrxZo0CpLoBhQsBdNrRRLpxJfxSNJqhR33C913S5p0ilu2ixs1ypjyCRdwSKA4DJoziRcvyD//9xzzjl23TBdwyNfwyJPqRVUtRxEmRFt1S5Wshz7/8hy3C1t1yxp0yhGoRV73TFz3C5WvSNcwSFXuBtTtRlm3TNlzShmuyJJpRVw1jFs2ixdwypZvyBNqxSe53JmzDNXthxTsRdKnxf//8z//8vF8KVquTh12ixguSxWrCNRrheM4lNz2DhkyiBYtB9PsxpTrxlMrxKX5mR+3URzvkJ720Fgxi1q1ilVsCNVuRxRrxVJpQ/1/MLX6KS935ur64R64EZ32jpnzTRlujLe77yQ4l2H4FB5w0l+4TRQrB/6/9i01oGC30N2zEN4zTResC1v2Clp1SdexBpKjhfC/5WX5m2Mzl+EzlOG7FKE3kt3zDNivTFYsyfu/8zl9bzv77zd/7vq/7fv76ve76u87p3M7pmz7Y++8Yu6/4em6HuU42Ck/1+O9FuY/lSY7VR0xkJrxjp94zlarzhguzd2yzJctyNkqCBl1x1gxhz//7vd/6rC/6Df35u645nG6JOu+IST12B7rkgH2ovEAAAAMnRSTlMAL26UaFsjnZCOiHlmPygVC/TNysm5k/Hu6urqq52Xk5GHd0Lt5tza1dPJuLa1tLKhQ0tp36kAAAZCSURBVFjD7df7P1NxHMfxId3v9/v9fs92KgqZJgtdNkwXsUpKRCyGmly7ulVUEorcoiRFut/v9/v9fvkfen/OzuxsRvRDP/V62MNxzvf79P2eDSP437+vxaSRvd/Yoze9x0xo0bn5QOdBI0Si1YgQ+iwSDR/UqVmEWbe3ImSAoLfdzJpBYEJoqKOjY2jEMgqHvvjwtba27tqyacbkqSJkbx8RoUMiCCQECVs1ZRmjp2oRRAin6BHhqA5/MoZORXxkDQWlDkFDGzfaJn1QFmdlXSrftetkcfWaNd7eLAIQSGysrW2YLRK2bcwYmJSk3J1V/nj/ucT9u06KYGiDEspHhAMbW0dSEoyyR4WF91lFZKcNSgq3FC3S8FrMk1B2edm5u3d37iyEAqROSQnlIcjctNFSKA8O3pxdlnh/J2KR8PDw2RQUEbJGtrZKZZhSmSPsaBKZKJRDKd6VWAjjW+JjbIcMTjFE5DnC8aaMwUJhDi3l0v7Cs2d/JJaVZ9mLYCykoMC4Jy54okeEg01sBvuswVLkWWWPEhPPn7+Ubf9BRAanwPAXi8UFa4AEywkxsaGeeJGpVHLZ7uLsrKKi3daOEcvs7GYvXHmcWjjbrqhoxgwr9H3jxo1ymUyG4a2MjU4fcdZHJZNthrIbe/cN1SJshDzUIvmEyFnkY8f6CwHiI5MFQ7EmxDHCm5AFFJQisR5RqVQ+NL6n0ULe6xAVFEJsjZCHPAQGi7w3/G1nWeqCfKBsRmFhtr6x2I8OEaF8sZXVDHQWk2UyH08ar7E0QHqUOiMXT09PPMmb5VBisR8e8kzMIT/ZJXt60njNOAOktA6RBQcHy6AoYx3t7cLrkDuccosMT6yDRTR8Y1hp6Sbk7EwKgpIT5stDnu3gkEpCiKDxGs0UHtL+jN905ELB8YGixH2x94aCTp26vcMK+fvfKMFOMGg6m9+Z9jxkABBtm2hXxMjlymrHFG+7cHTq+Q4OuVji6eKMYTpkAA/pvmfPLMRdo135qKAoq1NSvBEWwiJYSAk2wg6i8Xv2jOUhfYDowg/FJmd6uqfKa5TV1ikIC2ERLKQEBDeQkD48pItEspwNBtdBVFOTI/dD1wGsR3dwnp2tHewqkTA8hJFIXNmWL8e4V0dQwdODB1UIxhMrDnmFi9x8qj6yhM3VFeOuzUH4qT/oh00BmcMhD4TIVRsNXgvEcDsBbEuW6BHxU6Gfn4uf3xEd8osQjgigJJIuPKSvOi4gYMWKtUgikTyYgTDvuhBdvuzvT8iJExfjMFnbWiogIE7dl/8U58VFRq5gA3KeRVAlIZ845Etc3Fo2fDttkXF53XlIq0x1fOQqFBkpQbc4RAzkqz+HvAainUutotSZrfgveyDxNqts4uPjCanUIZ+Flf4ccg4INzeeskHqTP7LvoNCMXOmTV3qo3NwM/XhPh+h2TZGKRQGbxAUCulMfepsY+SmhibhEh76FAoBv/4KqVQ6U8qlxlL4CAwNO0uKD16KHgaIpcIhKkrqwKWGYoBoNJq8PKk0Ly8qykFfnsJSYFBtYKC7exTagkJCQlZvR2I0B2XjBF1zd9iyhWZLoxzc3d0DA2s7GyKtCXHAtaCgoOgQVFWHHK0KQe4oOiiIEFBapLXxX1FCMD+aHvNQWhWHHE1Lo6+j0Txcrbh69WrGlqAgQur9HW0DJDNz3jx6sEha2v6C/PyCqjQgXKBCUq9cuZKRwSJtBMZ1Yhhm69at0V5btXkZl753L+P1LjX5wIEDqRk4ZhgzE++TGCY93cvLw8vrEOVhXHr6Xo8MGMeOcYjJ90oWjJuHh5ebx6FDbqaCkgvjwoVjye8y9gYyFqbfBjMJCYvcFi1yw6N+OJ1O64BxOiM98DA2Y7IWDNqHEqaZKCEh4XTyixcvX6bmMqiFoIHaNYwgIKeTk1Nzc2MwrJ2gwYYwzGIEZGm9oFTk5uYGeSxdyjBDGjZoLYeBBE6ba6ppMRUVFTB462jkvixGNMvJaf58JzYcx8TE0HnufjSemYUWcZrrRAhFEg51iEVT/v0yZzagGMzjEKRF6DxeY03KrA0hmEnIYQrHOCSkTdP/C2zZulaB1qFtiD4rUG3rloJmZdnfGOlhKfiLOphbdO3Xa9u2Xv26WpgPE/zvn/cbMuy/WdwyIekAAAAASUVORK5CYII=";

    private const string XP_ICON_BASE64 =
        "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";

    // ── Token slot ordering (left to right) ─────────────────────────
    // Field name → display order (lower = more left)
    private static readonly (string Field, string Label)[] TokenSlotOrder =
    {
        ("SoloMilestoneHotspotValue", "Teatime"),
        ("BoultonLeaguePoints",       "BLE"),
        ("DigEventTaps",              "DE"),
        ("ClassicRacesSailPoints",    "Horizon"),
        ("RollTheDiceToken",          "RTD"),
    };

    const double TokenSlotSize = 22;   // outer circle diameter (small — icon overflows)
    const double TokenIconSize = 20;   // icon display size (same or bigger than circle)
    const double TokenSlotGap = 6;     // gap between token slots (center-to-center spacing via colW)
    const double TokenFontSz = 10;     // font size for token value text
    const int TokenCols = 3;           // columns per row
    const double TokenColW = 26;       // column width for token grid
    const double TokenRowH = 28;       // row height (must match rendering row spacing)

    // ── Public API ───────────────────────────────────────────────────

    public static string GenerateSvg(LuaArea area, DataService? ds, bool forDiscord = false,
        Dictionary<string, string>? itemIcons = null,
        Dictionary<string, string>? tokenIcons = null)
    {
        // 1. Build graph from LuaTask data (uses LuaTask.Index for display)
        var nodes = BuildGraph(area.Tasks, ds);
        if (nodes.Count == 0) return "";

        // 2. Remove self-loops
        foreach (var n in nodes.Values)
        {
            n.Children.Remove(n.Id);
            n.Parents.Remove(n.Id);
        }

        // 3. Remove empty nodes (no requirements) and reconnect
        RemoveEmptyNodes(nodes);

        // 4. Transitive reduction
        TransitiveReduction(nodes);

        // 5. Remove isolated nodes (no parents AND no children after reduction)
        var isolated = nodes.Where(kv => kv.Value.Parents.Count == 0 && kv.Value.Children.Count == 0)
                            .Select(kv => kv.Key).ToList();
        foreach (var id in isolated) nodes.Remove(id);

        if (nodes.Count == 0) return "";

        // 6. DisplayIndex already set from wiki sort — no renumbering

        // 7. Save parent/child display indices BEFORE dummy insertion modifies Parents/Children
        foreach (var n in nodes.Values)
        {
            if (n.IsDummy) continue;
            n.ParentDisplayIndices = n.Parents
                .Where(pid => nodes.ContainsKey(pid) && !nodes[pid].IsDummy)
                .Select(pid => nodes[pid].DisplayIndex)
                .OrderBy(i => i).ToList();
            n.ChildDisplayIndices = n.Children
                .Where(cid => nodes.ContainsKey(cid) && !nodes[cid].IsDummy)
                .Select(cid => nodes[cid].DisplayIndex)
                .OrderBy(i => i).ToList();
        }

        // 7b. Mark requirements that have icons available (icons displayed only in Discord/HTML)
        if (forDiscord && itemIcons != null)
        {
            foreach (var n in nodes.Values)
                foreach (var req in n.Requirements)
                    req.HasIcon = itemIcons.ContainsKey(req.ItemType);
        }

        // 8. Calculate node dimensions (uses ParentDisplayIndices/ChildDisplayIndices for link sections)
        foreach (var n in nodes.Values)
            CalculateNodeSize(n, forDiscord);

        // 9. Collect real edges BEFORE dummy insertion modifies Children/Parents
        var realEdges = new List<(string from, string to)>();
        foreach (var n in nodes.Values)
            foreach (var cid in n.Children)
                if (nodes.ContainsKey(cid))
                    realEdges.Add((n.Id, cid));

        // 10. Sugiyama layout
        var layers = SugiyamaLayout(nodes);

        // 11. Generate SVG
        var svgContent = RenderSvg(area.DisplayName, nodes, layers, realEdges, forDiscord, itemIcons, tokenIcons);

        if (forDiscord)
        {
            int realNodeCount = nodes.Values.Count(n => !n.IsDummy);
            return WrapAsInteractiveHtml(svgContent, area.DisplayName, realNodeCount);
        }

        return svgContent;
    }

    // ── Graph building ───────────────────────────────────────────────

    private static Dictionary<string, FNode> BuildGraph(
        List<LuaTask> tasks, DataService? ds)
    {
        var nodes = new Dictionary<string, FNode>(StringComparer.Ordinal);

        foreach (var t in tasks)
        {
            var (rewardText, rewardWiki, rewardSingleLevel) = FormatItemReward(t.ItemReward, ds);
            var node = new FNode
            {
                Id = t.Id,
                Title = t.Title,
                DisplayIndex = t.Index,
                Parents = new HashSet<string>(t.ParentIds, StringComparer.Ordinal),
                Children = new HashSet<string>(t.ChildIds, StringComparer.Ordinal),
                Requirements = ResolveRequirements(t.Requirements, ds),
                XpReward = t.XpReward,
                ItemRewardText = rewardText,
                ItemRewardType = t.ItemReward,
                ItemRewardWikiName = rewardWiki,
                ItemRewardSingleLevel = rewardSingleLevel,
                TokenValues = t.TokenValues ?? new()
            };
            nodes[t.Id] = node;
        }

        return nodes;
    }

    private static List<FReq> ResolveRequirements(Dictionary<string, int> reqs, DataService? ds)
    {
        var result = new List<FReq>();
        foreach (var (itemType, qty) in reqs)
        {
            string name;
            string? tooltip = null;

            if (ds != null)
            {
                name = ds.ResolveItemName(itemType);
                var level = DataService.GetLevelFromItemType(itemType);
                var chainKey = DataService.GetChainKeyFromItemType(itemType);
                tooltip = ds.ResolveChainDisplayName(chainKey);

                name = level > 0 ? $"{name} [L{level}]" : name;
            }
            else
            {
                name = itemType;
            }

            result.Add(new FReq { Qty = qty, ItemName = name, Tooltip = tooltip, ItemType = itemType, ChainWikiName = tooltip });
        }
        return result;
    }

    private static (string? text, string? wikiName, bool singleLevel) FormatItemReward(
        string? itemReward, DataService? ds)
    {
        if (string.IsNullOrEmpty(itemReward)) return (null, null, false);

        if (ds != null)
        {
            var name = ds.ResolveItemName(itemReward);
            var level = DataService.GetLevelFromItemType(itemReward);
            var chainKey = DataService.GetChainKeyFromItemType(itemReward);
            var wikiName = ds.ResolveChainDisplayName(chainKey);

            // Check if chain has only 1 unique level
            bool singleLevel = false;
            var chain = ds.Chains.FirstOrDefault(c =>
                string.Equals(c.ConfigKey, chainKey, StringComparison.Ordinal));
            if (chain != null)
            {
                var uniqueLevels = chain.Items.Select(i => i.Level).Distinct().Count();
                singleLevel = uniqueLevels <= 1;
            }

            var text = level > 0 && !singleLevel ? $"{name} [L{level}]" : name;
            return (text, wikiName, singleLevel);
        }
        return (itemReward, null, false);
    }

    // ── Empty node removal ───────────────────────────────────────────

    private static void RemoveEmptyNodes(Dictionary<string, FNode> nodes)
    {
        var toRemove = nodes.Values
            .Where(n => n.Requirements.Count == 0)
            .Select(n => n.Id)
            .ToList();

        foreach (var id in toRemove)
        {
            if (!nodes.TryGetValue(id, out var node)) continue;

            foreach (var pid in node.Parents)
            {
                if (!nodes.TryGetValue(pid, out var parent)) continue;
                parent.Children.Remove(id);
                foreach (var cid in node.Children)
                {
                    if (cid == pid) continue;
                    parent.Children.Add(cid);
                    if (nodes.TryGetValue(cid, out var child))
                        child.Parents.Add(pid);
                }
            }

            foreach (var cid in node.Children)
            {
                if (!nodes.TryGetValue(cid, out var child)) continue;
                child.Parents.Remove(id);
            }

            nodes.Remove(id);
        }
    }

    // ── Transitive reduction ─────────────────────────────────────────

    private static void TransitiveReduction(Dictionary<string, FNode> nodes)
    {
        foreach (var u in nodes.Values)
        {
            var edgesToRemove = new List<string>();
            foreach (var vId in u.Children)
            {
                if (HasAlternativePath(nodes, u.Id, vId))
                    edgesToRemove.Add(vId);
            }

            foreach (var vId in edgesToRemove)
            {
                u.Children.Remove(vId);
                if (nodes.TryGetValue(vId, out var v))
                    v.Parents.Remove(u.Id);
            }
        }
    }

    private static bool HasAlternativePath(Dictionary<string, FNode> nodes, string fromId, string toId)
    {
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { fromId };

        if (!nodes.TryGetValue(fromId, out var fromNode)) return false;

        foreach (var cid in fromNode.Children)
        {
            if (cid == toId) continue;
            if (visited.Add(cid))
                queue.Enqueue(cid);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == toId) return true;

            if (!nodes.TryGetValue(current, out var node)) continue;
            foreach (var cid in node.Children)
            {
                if (visited.Add(cid))
                    queue.Enqueue(cid);
            }
        }

        return false;
    }

    // ── Topological sort ─────────────────────────────────────────────

    private static List<FNode> TopologicalSort(Dictionary<string, FNode> nodes)
    {
        var sorted = new List<FNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inStack = new HashSet<string>(StringComparer.Ordinal);

        void Visit(FNode node)
        {
            if (inStack.Contains(node.Id) || visited.Contains(node.Id)) return;
            inStack.Add(node.Id);
            foreach (var cid in node.Children)
                if (nodes.TryGetValue(cid, out var child))
                    Visit(child);
            inStack.Remove(node.Id);
            visited.Add(node.Id);
            sorted.Add(node);
        }

        foreach (var n in nodes.Values.Where(n => n.Parents.Count == 0).OrderBy(n => n.Id))
            Visit(n);
        foreach (var n in nodes.Values.Where(n => !visited.Contains(n.Id)).OrderBy(n => n.Id))
            Visit(n);

        sorted.Reverse();
        return sorted;
    }

    // ── Node sizing ──────────────────────────────────────────────────

    private static void CalculateNodeSize(FNode node, bool forDiscord = false)
    {
        if (node.IsDummy) { node.Width = 0; node.Height = 0; return; }

        // Header width: "#NNN" (IndexColW) + gap + title (wraps to 2 lines if too long)
        double titleCharW = FontSzTitle * 0.64; // bold
        double availTitleW = MaxNodeW - PadX * 2 - IndexColW - IndexTitleGap;
        int maxTitleChars = MaxTitleChars;

        double headerContentW;
        if (node.Title.Length > maxTitleChars)
        {
            node.HeaderLines = 2;
            int breakAt = node.Title.LastIndexOf(' ', Math.Min(maxTitleChars, node.Title.Length - 1));
            if (breakAt <= 0) breakAt = maxTitleChars;
            string line1 = node.Title[..breakAt].TrimEnd();
            string line2 = node.Title[breakAt..].TrimStart();
            double line1W = line1.Length * titleCharW;
            double line2W = Math.Min(line2.Length, maxTitleChars) * titleCharW;
            headerContentW = IndexColW + IndexTitleGap + Math.Max(line1W, line2W);
        }
        else
        {
            node.HeaderLines = 1;
            headerContentW = IndexColW + IndexTitleGap + node.Title.Length * titleCharW;
        }

        // Item widths: "Nx" (QtyColW) + gap + item name
        double maxItemW = 0;
        foreach (var req in node.Requirements)
        {
            double nameW = req.ItemName.Length * FontSzItem * 0.60;
            double lineW = QtyColW + QtyNameGap + nameW;
            if (lineW > maxItemW) maxItemW = lineW;
        }

        // Reward width
        double rewardW = 0;
        bool hasXp = node.XpReward.HasValue && node.XpReward.Value > 0;
        bool hasItemReward = !string.IsNullOrEmpty(node.ItemRewardText);

        if (hasXp)
        {
            string xpText = node.XpReward!.Value.ToString();
            rewardW = xpText.Length * FontSzReward * 0.60 + 6 + IconSize;
            if (hasItemReward)
                rewardW += " · ".Length * FontSzReward * 0.60 + node.ItemRewardText!.Length * FontSzReward * 0.60;
        }
        else if (hasItemReward)
        {
            rewardW = node.ItemRewardText!.Length * FontSzReward * 0.60;
        }

        double maxContentW = Math.Max(headerContentW, Math.Max(maxItemW, rewardW));
        node.Width = MaxNodeW;

        // Height (dynamic header for 2-line titles)
        double headerH = node.HeaderLines == 2 ? 50.0 : HeaderH;
        double h = 0;

        // Parent link section (above header — 7px top pad + 6px link area)
        if (node.ParentDisplayIndices.Count > 0)
        {
            h += 13;
            headerH -= 5; // shorter header bar when parent links take up space above
        }

        h += headerH;
        h += PadY; // top padding of body

        // Items (always present — we only show nodes with requirements)
        bool hasAnyIcon = node.Requirements.Any(r => r.HasIcon);
        double itemLineH = hasAnyIcon ? ItemSlotSize + 2 : LineH;
        h += node.Requirements.Count * itemLineH;

        // Reward + tokens (tokens float right, share vertical space with reward)
        bool hasReward = hasXp || hasItemReward;
        bool hasTokens = node.TokenValues.Count > 0 && forDiscord;
        if (hasReward || hasTokens)
        {
            h += SepGap;
            // Left side: XP line + optional reward item line below
            double leftH = 0;
            if (hasXp) leftH += IconSize + 2;
            if (hasItemReward && forDiscord) leftH += 6 + LineH; // 6px gap + reward item line in HTML
            else if (hasItemReward && hasXp) leftH += 0; // SVG: inline with XP
            else if (hasItemReward) leftH += LineH;

            if (hasTokens)
            {
                int tokenCount = node.TokenValues.Count;
                int rows = (tokenCount + TokenCols - 1) / TokenCols;
                double tokenH = rows * TokenRowH - 22; // tokens overflow visually
                // 2+ rows: tokens drive height; 1 row: ensure reward item fits (+6px for icon clearance)
                h += rows >= 2 ? tokenH : Math.Max(leftH + (hasItemReward ? 6 : 0), tokenH);
            }
            else
            {
                h += leftH;
            }
        }

        // Bottom padding (reduced when tokens provide visual spacing)
        h += hasTokens ? 2 : PadY;

        // Child link section (separator + links below rewards, +2px bottom)
        if (node.ChildDisplayIndices.Count > 0)
            h += SepGap + LinkLineH + LinkSectionPad + 2;

        node.Height = h;
    }

    // ── Sugiyama layout ──────────────────────────────────────────────

    private static List<List<string>> SugiyamaLayout(Dictionary<string, FNode> nodes)
    {
        AssignLayers(nodes);

        int maxLayer = nodes.Values.Max(n => n.Layer);
        var layers = new List<List<string>>();
        for (int i = 0; i <= maxLayer; i++)
            layers.Add(new List<string>());
        foreach (var n in TopologicalSort(nodes))
            layers[n.Layer].Add(n.Id);

        InsertDummyNodes(nodes, layers);

        // Diagnostic: log layer assignment and graph structure for problem area
        var diagRange = nodes.Values
            .Where(nd => !nd.IsDummy && nd.DisplayIndex >= 78 && nd.DisplayIndex <= 95)
            .OrderBy(nd => nd.DisplayIndex)
            .ToList();
        if (diagRange.Count > 0)
        {
            AppLogger.Info("[FLOWCHART-V3] === Layer assignment & graph structure (tasks 78-95) ===");
            foreach (var nd in diagRange)
            {
                var realParents = nd.Parents.Where(pid => nodes.ContainsKey(pid) && !nodes[pid].IsDummy)
                    .Select(pid => nodes[pid].DisplayIndex.ToString()).ToList();
                var realChildren = nd.Children.Where(cid => nodes.ContainsKey(cid) && !nodes[cid].IsDummy)
                    .Select(cid => nodes[cid].DisplayIndex.ToString()).ToList();
                AppLogger.Info($"[FLOWCHART-V3]   Task {nd.DisplayIndex}: Layer={nd.Layer}, Parents=[{string.Join(",", realParents)}], Children=[{string.Join(",", realChildren)}]");
            }
        }

        MinimizeCrossings(nodes, layers, iterations: 12);
        AssignXPositions(nodes, layers);
        AlignLinearChains(nodes, layers);

        // Diagnostic: FINAL positions after AlignLinearChains returns
        if (diagRange.Count > 0)
        {
            AppLogger.Info("[FLOWCHART-V3] === FINAL X positions after AlignLinearChains ===");
            foreach (var nd in diagRange)
                AppLogger.Info($"[FLOWCHART-V3]   Task {nd.DisplayIndex}: X={nd.X:F0}");

            // Also log layer 30 (where T81+T218 live) to see cascade effects
            for (int li = 0; li < layers.Count; li++)
            {
                bool hasDiag = layers[li].Any(nid => nodes.TryGetValue(nid, out var nd2) && !nd2.IsDummy && nd2.DisplayIndex >= 80 && nd2.DisplayIndex <= 82);
                if (hasDiag)
                {
                    var layerInfo = layers[li]
                        .Where(nid => nodes.ContainsKey(nid) && !nodes[nid].IsDummy)
                        .Select(nid => { var nd2 = nodes[nid]; return $"T{nd2.DisplayIndex}:X={nd2.X:F0}"; });
                    AppLogger.Info($"[FLOWCHART-V3]   Layer {li} final: [{string.Join(", ", layerInfo)}]");
                }
            }
        }

        AssignYPositions(nodes, layers);

        // Column compaction — merge non-overlapping columns to reduce horizontal width
        CompactColumns(nodes);

        return layers;
    }

    private static void AssignLayers(Dictionary<string, FNode> nodes)
    {
        // Phase 1: Standard longest-path layer assignment (nodes as early as possible)
        foreach (var n in nodes.Values) n.Layer = 0;
        foreach (var n in TopologicalSort(nodes))
        {
            foreach (var cid in n.Children)
            {
                if (nodes.TryGetValue(cid, out var child))
                    child.Layer = Math.Max(child.Layer, n.Layer + 1);
            }
        }

        // Phase 2: Pull down — move each node to its latest valid layer
        // (closest to children). This aligns parallel branches at the same depth.
        // Capped at 10 layers below the Phase 1 position to avoid extreme stretching.
        var phase1Layers = nodes.Values.ToDictionary(n => n.Id, n => n.Layer);
        var revTopo = TopologicalSort(nodes);
        revTopo.Reverse();
        foreach (var n in revTopo)
        {
            var childLayers = n.Children
                .Where(cid => nodes.ContainsKey(cid))
                .Select(cid => nodes[cid].Layer)
                .ToList();
            if (childLayers.Count > 0)
            {
                int idealLayer = childLayers.Min() - 1;
                int maxAllowed = phase1Layers[n.Id] + 10;
                n.Layer = Math.Min(idealLayer, maxAllowed);
            }
        }

        // Phase 2.5: Sibling proximity — children of the same parent must be
        // at the same layer or at most 1 layer apart. This prevents long "bypass"
        // edges that wrap around entire subtrees. The deeper child's own subtree
        // will use dummy nodes for the remaining span, producing a clean vertical chain.
        const int MaxSiblingSpan = 0;
        bool siblingChanged = true;
        int siblingAttempts = 0;
        while (siblingChanged && siblingAttempts++ < 5)
        {
            siblingChanged = false;
            foreach (var parent in nodes.Values)
            {
                var childIds = parent.Children
                    .Where(cid => nodes.ContainsKey(cid))
                    .ToList();
                if (childIds.Count < 2) continue;

                int minChildLayer = childIds.Min(cid => nodes[cid].Layer);

                foreach (var cid in childIds)
                {
                    var child = nodes[cid];
                    if (child.Layer <= minChildLayer + MaxSiblingSpan) continue;

                    int targetLayer = minChildLayer + MaxSiblingSpan;

                    // Ensure child stays above its own children
                    var gcLayers = child.Children
                        .Where(gcid => nodes.ContainsKey(gcid))
                        .Select(gcid => nodes[gcid].Layer)
                        .ToList();
                    if (gcLayers.Count > 0)
                        targetLayer = Math.Min(targetLayer, gcLayers.Min() - 1);

                    // Ensure child stays below ALL its parents (not just this one)
                    var allParentLayers = child.Parents
                        .Where(pid => nodes.ContainsKey(pid))
                        .Select(pid => nodes[pid].Layer);
                    foreach (var pl in allParentLayers)
                        targetLayer = Math.Max(targetLayer, pl + 1);

                    if (targetLayer < child.Layer)
                    {
                        child.Layer = targetLayer;
                        siblingChanged = true;
                    }
                }
            }
        }

        // Phase 3: Compact — remove empty layer gaps
        void CompactLayers()
        {
            var used = nodes.Values.Select(n => n.Layer).Distinct().OrderBy(l => l).ToList();
            if (used.Count > 0)
            {
                var map = new Dictionary<int, int>();
                for (int i = 0; i < used.Count; i++)
                    map[used[i]] = i;
                foreach (var n in nodes.Values)
                    n.Layer = map[n.Layer];
            }
        }

        CompactLayers();

        // Phase 4: Column limit — max 5 real nodes per layer.
        // If a layer exceeds this, push the excess nodes (by highest DisplayIndex)
        // down by 1 layer, then re-compact.
        const int MaxColumnsPerLayer = 5;
        bool columnChanged = true;
        int columnAttempts = 0;
        while (columnChanged && columnAttempts++ < 10)
        {
            columnChanged = false;

            var layerGroups = nodes.Values
                .GroupBy(n => n.Layer)
                .Where(g => g.Count() > MaxColumnsPerLayer)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in layerGroups)
            {
                var overflow = group
                    .OrderByDescending(n => n.DisplayIndex)
                    .Take(group.Count() - MaxColumnsPerLayer)
                    .ToList();

                foreach (var n in overflow)
                {
                    // Check we can push down (must stay above all children)
                    var childLayers = n.Children
                        .Where(cid => nodes.ContainsKey(cid))
                        .Select(cid => nodes[cid].Layer)
                        .ToList();
                    int maxAllowed = childLayers.Count > 0 ? childLayers.Min() - 1 : n.Layer + 1;

                    if (n.Layer + 1 <= maxAllowed)
                    {
                        n.Layer = n.Layer + 1;
                        columnChanged = true;
                    }
                }
            }

            if (columnChanged)
                CompactLayers();
        }
    }

    private static void InsertDummyNodes(
        Dictionary<string, FNode> nodes, List<List<string>> layers)
    {
        int dummyCounter = 0;

        var edges = new List<(string from, string to)>();
        foreach (var n in nodes.Values)
            foreach (var cid in n.Children)
                if (nodes.ContainsKey(cid))
                    edges.Add((n.Id, cid));

        foreach (var (fromId, toId) in edges)
        {
            var from = nodes[fromId];
            var to = nodes[toId];
            int span = to.Layer - from.Layer;
            if (span <= 1) continue;

            string prevId = fromId;

            for (int layer = from.Layer + 1; layer < to.Layer; layer++)
            {
                var dummyId = $"__dummy_{dummyCounter++}";
                var dummy = new FNode
                {
                    Id = dummyId,
                    IsDummy = true,
                    Layer = layer,
                    Width = 0,
                    Height = 0
                };
                nodes[dummyId] = dummy;
                layers[layer].Add(dummyId);

                nodes[prevId].Children.Remove(toId);
                nodes[prevId].Children.Add(dummyId);
                dummy.Parents.Add(prevId);
                dummy.Children.Add(toId);

                prevId = dummyId;
            }

            if (prevId != fromId)
            {
                nodes[prevId].Children.Clear();
                nodes[prevId].Children.Add(toId);
                nodes[toId].Parents.Remove(fromId);
                nodes[toId].Parents.Add(prevId);
            }
        }
    }

    // ── Crossing minimization ────────────────────────────────────────

    private static void MinimizeCrossings(
        Dictionary<string, FNode> nodes, List<List<string>> layers, int iterations)
    {
        for (int li = 0; li < layers.Count; li++)
            for (int i = 0; i < layers[li].Count; i++)
                nodes[layers[li][i]].Order = i;

        for (int iter = 0; iter < iterations; iter++)
        {
            for (int li = 1; li < layers.Count; li++)
                ReorderLayer(nodes, layers, li, useParents: true);

            for (int li = layers.Count - 2; li >= 0; li--)
                ReorderLayer(nodes, layers, li, useParents: false);
        }
    }

    private static void ReorderLayer(
        Dictionary<string, FNode> nodes, List<List<string>> layers,
        int layerIdx, bool useParents)
    {
        var layer = layers[layerIdx];
        var barycenters = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var nid in layer)
        {
            var n = nodes[nid];
            var neighbors = useParents ? n.Parents : n.Children;
            var positions = new List<int>();

            foreach (var nbId in neighbors)
                if (nodes.TryGetValue(nbId, out var nb))
                    positions.Add(nb.Order);

            barycenters[nid] = positions.Count > 0
                ? positions.Average()
                : n.Order;
        }

        var sorted = layer.OrderBy(id => barycenters[id]).ThenBy(id => nodes[id].Order).ToList();
        layers[layerIdx] = sorted;

        for (int i = 0; i < sorted.Count; i++)
            nodes[sorted[i]].Order = i;
    }

    // ── Position assignment ──────────────────────────────────────────

    private static void AssignXPositions(Dictionary<string, FNode> nodes, List<List<string>> layers)
    {
        foreach (var layer in layers)
        {
            double x = 0;
            foreach (var nid in layer)
            {
                var n = nodes[nid];
                n.X = x;
                x += (n.IsDummy ? NodeGapX : n.Width) + NodeGapX;
            }
        }

        double maxWidth = 0;
        foreach (var layer in layers)
        {
            if (layer.Count == 0) continue;
            var last = nodes[layer[^1]];
            var layerWidth = last.X + (last.IsDummy ? 0 : last.Width);
            if (layerWidth > maxWidth) maxWidth = layerWidth;
        }

        foreach (var layer in layers)
        {
            if (layer.Count == 0) continue;
            var last = nodes[layer[^1]];
            var layerWidth = last.X + (last.IsDummy ? 0 : last.Width);
            var offset = (maxWidth - layerWidth) / 2;
            foreach (var nid in layer)
                nodes[nid].X += offset;
        }

        // Diagnostic: track positions of tasks in suspected problem range
        var diagIds = nodes.Values
            .Where(nd => !nd.IsDummy && nd.DisplayIndex >= 78 && nd.DisplayIndex <= 95)
            .OrderBy(nd => nd.DisplayIndex)
            .ToList();
        if (diagIds.Count > 0)
        {
            AppLogger.Info("[FLOWCHART-V3] === ImproveLayerPositions start ===");
            AppLogger.Info($"[FLOWCHART-V3] Pre-iteration: {string.Join(", ", diagIds.Select(d => $"T{d.DisplayIndex}:X={d.X:F0}"))}");
        }

        for (int iter = 0; iter < 8; iter++)
        {
            for (int li = 1; li < layers.Count; li++)
                ImproveLayerPositions(nodes, layers[li], useParents: true);

            if (diagIds.Count > 0)
                AppLogger.Info($"[FLOWCHART-V3] Iter {iter} top-down:  {string.Join(", ", diagIds.Select(d => $"T{d.DisplayIndex}:X={d.X:F0}"))}");

            for (int li = layers.Count - 2; li >= 0; li--)
                ImproveLayerPositions(nodes, layers[li], useParents: false);

            if (diagIds.Count > 0)
                AppLogger.Info($"[FLOWCHART-V3] Iter {iter} bottom-up: {string.Join(", ", diagIds.Select(d => $"T{d.DisplayIndex}:X={d.X:F0}"))}");
        }

        if (diagIds.Count > 0)
            AppLogger.Info("[FLOWCHART-V3] === ImproveLayerPositions end ===");
    }

    private static void ImproveLayerPositions(
        Dictionary<string, FNode> nodes, List<string> layer, bool useParents)
    {
        foreach (var nid in layer)
        {
            var n = nodes[nid];
            var neighbors = useParents ? n.Parents : n.Children;
            if (neighbors.Count == 0) continue;

            var centers = new List<double>();
            foreach (var nbId in neighbors)
            {
                if (!nodes.TryGetValue(nbId, out var nb)) continue;
                centers.Add(nb.X + (nb.IsDummy ? 0 : nb.Width) / 2);
            }

            if (centers.Count == 0) continue;
            centers.Sort();
            double median = centers[centers.Count / 2];
            double idealX = median - (n.IsDummy ? 0 : n.Width) / 2;

            double shift = idealX - n.X;
            if (Math.Abs(shift) < 1) continue;

            int myIdx = layer.IndexOf(nid);
            if (myIdx < 0) continue;

            double myLeft = n.X + shift;
            double myRight = myLeft + (n.IsDummy ? NodeGapX : n.Width);

            if (myIdx > 0)
            {
                var leftN = nodes[layer[myIdx - 1]];
                double leftRight = leftN.X + (leftN.IsDummy ? 0 : leftN.Width) + NodeGapX;
                if (myLeft < leftRight)
                    shift = Math.Max(shift, leftRight - n.X);
            }

            if (myIdx < layer.Count - 1)
            {
                var rightN = nodes[layer[myIdx + 1]];
                double rightLeft = rightN.X - NodeGapX;
                if (myRight > rightLeft && shift > 0)
                    shift = Math.Min(shift, rightN.X - NodeGapX - (n.IsDummy ? 0 : n.Width) - n.X);
            }

            if (Math.Abs(shift) >= 1)
                n.X += shift;
        }
    }

    /// <summary>
    /// V3 — FORCE alignment: for every node with exactly 1 real parent where it is
    /// the parent's first child (by DisplayIndex), force it directly under the parent.
    /// Overlapping neighbors are pushed aside (cascading). Processed top-down so parent
    /// positions are stable when children are aligned. This guarantees zero zigzag in
    /// linear sequences.
    /// </summary>
    private static void AlignLinearChains(Dictionary<string, FNode> nodes, List<List<string>> layers)
    {
        // Build layer lookup
        var layerOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int li = 0; li < layers.Count; li++)
            foreach (var nid in layers[li])
                layerOf[nid] = li;

        AppLogger.Info("[FLOWCHART-V3] === AlignLinearChains (FORCE mode) ===");

        // Track nodes that have been force-aligned — protected from push-aside cascade
        var forceAligned = new HashSet<string>(StringComparer.Ordinal);

        // Process all nodes top-down (topological order = parents before children)
        foreach (var node in TopologicalSort(nodes))
        {
            if (node.IsDummy) continue;

            // Only process nodes with exactly 1 real parent
            var realParents = node.Parents
                .Where(pid => nodes.TryGetValue(pid, out var p) && !p.IsDummy)
                .Select(pid => nodes[pid])
                .ToList();
            if (realParents.Count != 1) continue;

            var parent = realParents[0];

            // Only force if this node is the parent's FIRST real child (by DisplayIndex)
            var siblingIds = parent.Children
                .Where(cid => nodes.TryGetValue(cid, out var c) && !c.IsDummy)
                .Select(cid => nodes[cid])
                .OrderBy(c => c.DisplayIndex)
                .ToList();
            var firstChildId = siblingIds.FirstOrDefault()?.Id;
            if (firstChildId != node.Id)
            {
                // Log skipped nodes in diagnostic range or sharing layers 29-31
                if (node.DisplayIndex >= 78 && node.DisplayIndex <= 95
                    || (layerOf.TryGetValue(node.Id, out var skipLi) && skipLi >= 29 && skipLi <= 31))
                    AppLogger.Info($"[FLOWCHART-V3]   Task {node.DisplayIndex}: SKIPPED — not first child of parent {parent.DisplayIndex} (first child={siblingIds.FirstOrDefault()?.DisplayIndex}, siblings=[{string.Join(",", siblingIds.Select(s => s.DisplayIndex))}])");
                continue;
            }

            // Target: center of parent
            double targetCenterX = parent.X + parent.Width / 2;
            double shift = targetCenterX - (node.X + node.Width / 2);
            if (Math.Abs(shift) < 1)
            {
                forceAligned.Add(node.Id);
                if (node.DisplayIndex >= 78 && node.DisplayIndex <= 95)
                    AppLogger.Info($"[FLOWCHART-V3]   Task {node.DisplayIndex}: already aligned under parent {parent.DisplayIndex} (shift={shift:F1})");
                continue;
            }

            if (!layerOf.TryGetValue(node.Id, out int li)) continue;
            var layer = layers[li];
            int idx = layer.IndexOf(node.Id);
            if (idx < 0) continue;

            // Clamp shift to avoid displacing already force-aligned nodes
            double clampedShift = ClampShiftForProtected(nodes, layer, idx, shift, forceAligned);

            // Log the force alignment — log any node sharing a layer with T80-T82 (layers 29-31)
            bool logThis = (node.DisplayIndex >= 78 && node.DisplayIndex <= 95) || (li >= 29 && li <= 31);
            if (logThis)
            {
                var layerNeighbors = new List<string>();
                for (int j = 0; j < layer.Count; j++)
                {
                    var ln = nodes[layer[j]];
                    if (!ln.IsDummy)
                        layerNeighbors.Add($"T{ln.DisplayIndex}:X={ln.X:F0}");
                }
                var clampNote = Math.Abs(clampedShift - shift) > 1 ? $" CLAMPED from {shift:F1}" : "";
                AppLogger.Info($"[FLOWCHART-V3]   Task {node.DisplayIndex}: FORCE X {node.X:F0}→{node.X + clampedShift:F0} (shift={clampedShift:F1}{clampNote}, parent=T{parent.DisplayIndex}@X={parent.X:F0}, layer={li}, layerIdx={idx}, siblings=[{string.Join(",", siblingIds.Select(s => s.DisplayIndex))}])");
                AppLogger.Info($"[FLOWCHART-V3]     Layer {li} before push: [{string.Join(", ", layerNeighbors)}]");
            }

            if (Math.Abs(clampedShift) < 1)
            {
                forceAligned.Add(node.Id);
                continue;
            }

            // FORCE the X position with clamped shift
            node.X += clampedShift;
            forceAligned.Add(node.Id);

            // Push overlapping neighbors aside (cascade)
            PushNeighborsAside(nodes, layer, idx);

            if (logThis)
            {
                var layerAfter = new List<string>();
                for (int j = 0; j < layer.Count; j++)
                {
                    var ln = nodes[layer[j]];
                    if (!ln.IsDummy)
                        layerAfter.Add($"T{ln.DisplayIndex}:X={ln.X:F0}");
                }
                AppLogger.Info($"[FLOWCHART-V3]     Layer {li} after push:  [{string.Join(", ", layerAfter)}]");
            }
        }
    }

    /// <summary>
    /// After forcefully repositioning a node, pushes any overlapping real neighbors
    /// aside to maintain NodeGapX spacing. Cascades in both directions.
    /// </summary>
    private static void PushNeighborsAside(Dictionary<string, FNode> nodes, List<string> layer, int idx)
    {
        var node = nodes[layer[idx]];

        // Push left neighbors leftward (cascade)
        double leftBound = node.X;
        for (int j = idx - 1; j >= 0; j--)
        {
            var left = nodes[layer[j]];
            if (left.IsDummy) continue;
            double neededRight = leftBound - NodeGapX;
            if (left.X + left.Width <= neededRight) break; // no overlap
            left.X = neededRight - left.Width;
            leftBound = left.X;
        }

        // Push right neighbors rightward (cascade)
        double rightBound = node.X + node.Width;
        for (int j = idx + 1; j < layer.Count; j++)
        {
            var right = nodes[layer[j]];
            if (right.IsDummy) continue;
            if (right.X >= rightBound + NodeGapX) break; // no overlap
            right.X = rightBound + NodeGapX;
            rightBound = right.X + right.Width;
        }
    }

    /// <summary>
    /// Calculates how far a node at 'idx' can shift without displacing any protected node
    /// in the cascade. Returns clamped shift value.
    /// Strategy: find the nearest protected node in the cascade direction, then compute
    /// the minimum distance needed from the protected node through all intermediate real nodes.
    /// </summary>
    private static double ClampShiftForProtected(
        Dictionary<string, FNode> nodes, List<string> layer, int idx,
        double desiredShift, HashSet<string> forceAligned)
    {
        var node = nodes[layer[idx]];

        if (desiredShift < 0)
        {
            // Shifting left — find first protected node to the left (cascade direction)
            int protectedIdx = -1;
            for (int j = idx - 1; j >= 0; j--)
            {
                var left = nodes[layer[j]];
                if (left.IsDummy) continue;
                if (forceAligned.Contains(left.Id)) { protectedIdx = j; break; }
            }

            if (protectedIdx < 0) return desiredShift; // no protected node — shift freely

            // Compute minimum X for our node: start from protected node's right edge,
            // then add width+gap for each real intermediate node between protected and us
            var pNode = nodes[layer[protectedIdx]];
            double minX = pNode.X + pNode.Width + NodeGapX;
            for (int k = protectedIdx + 1; k < idx; k++)
            {
                var mid = nodes[layer[k]];
                if (mid.IsDummy) continue;
                minX += mid.Width + NodeGapX;
            }

            // Clamp: our node can't go below minX
            double clampedShift = Math.Max(desiredShift, minX - node.X);
            return clampedShift;
        }

        if (desiredShift > 0)
        {
            // Shifting right — find first protected node to the right
            int protectedIdx = -1;
            for (int j = idx + 1; j < layer.Count; j++)
            {
                var left = nodes[layer[j]];
                if (left.IsDummy) continue;
                if (forceAligned.Contains(left.Id)) { protectedIdx = j; break; }
            }

            if (protectedIdx < 0) return desiredShift; // no protected node

            // Compute maximum X: from protected node's left edge, subtract widths going back
            var pNode = nodes[layer[protectedIdx]];
            double maxRight = pNode.X - NodeGapX;
            for (int k = protectedIdx - 1; k > idx; k--)
            {
                var mid = nodes[layer[k]];
                if (mid.IsDummy) continue;
                maxRight -= mid.Width + NodeGapX;
            }
            double maxX = maxRight - node.Width;

            double clampedShift = Math.Min(desiredShift, maxX - node.X);
            return clampedShift;
        }

        return desiredShift;
    }

    private static void AssignYPositions(Dictionary<string, FNode> nodes, List<List<string>> layers)
    {
        double y = 0;
        for (int li = 0; li < layers.Count; li++)
        {
            double maxH = 0;
            foreach (var nid in layers[li])
            {
                var n = nodes[nid];
                if (!n.IsDummy && n.Height > maxH) maxH = n.Height;
            }

            if (maxH == 0) maxH = LineH;

            foreach (var nid in layers[li])
            {
                var n = nodes[nid];
                n.Y = y + (maxH - n.Height) / 2;
                if (n.IsDummy) n.Y = y + maxH / 2;
            }

            y += maxH + LayerGap;
        }
    }

    // ── Column compaction ──────────────────────────────────────────────

    /// <summary>
    /// Post-processing: merges columns whose Y ranges don't overlap into the same
    /// X position, reducing total flowchart width. Uses greedy first-fit bin packing.
    /// </summary>
    private static void CompactColumns(Dictionary<string, FNode> nodes)
    {
        const double yBuffer = LayerGap / 2; // vertical buffer between merged ranges

        // Step 1: Group real nodes by X position
        var columnGroups = new Dictionary<double, List<FNode>>();
        foreach (var n in nodes.Values)
        {
            if (n.IsDummy) continue;
            double key = Math.Round(n.X, 1);
            if (!columnGroups.TryGetValue(key, out var list))
            {
                list = new List<FNode>();
                columnGroups[key] = list;
            }
            list.Add(n);
        }

        // Step 2: Build column info with Y intervals
        var columns = new List<(double origX, double yMin, double yMax, List<FNode> nodes)>();
        foreach (var (xKey, nodeList) in columnGroups)
        {
            double minY = nodeList.Min(n => n.Y) - yBuffer;
            double maxY = nodeList.Max(n => n.Y + n.Height) + yBuffer;
            columns.Add((xKey, minY, maxY, nodeList));
        }

        // Step 3: Sort by original X (left to right)
        columns.Sort((a, b) => a.origX.CompareTo(b.origX));

        // Step 4: Greedy first-fit packing into slots
        var slots = new List<(double x, List<(double min, double max)> intervals)>();

        foreach (var col in columns)
        {
            int bestSlot = -1;
            for (int si = 0; si < slots.Count; si++)
            {
                bool overlaps = false;
                foreach (var (min, max) in slots[si].intervals)
                {
                    if (col.yMin < max && col.yMax > min) { overlaps = true; break; }
                }
                if (!overlaps) { bestSlot = si; break; }
            }

            double assignedX;
            if (bestSlot >= 0)
            {
                slots[bestSlot].intervals.Add((col.yMin, col.yMax));
                assignedX = slots[bestSlot].x;
            }
            else
            {
                double newX = slots.Count == 0 ? 0 : slots.Max(s => s.x) + MaxNodeW + NodeGapX;
                slots.Add((newX, new List<(double, double)> { (col.yMin, col.yMax) }));
                assignedX = newX;
            }

            // Apply to real nodes
            foreach (var n in col.nodes)
                n.X = assignedX;
        }

        // Step 5: Update dummy nodes — map old X → new X from nearest real column
        var xMapping = new Dictionary<double, double>();
        foreach (var col in columns)
        {
            var newX = col.nodes[0].X;
            xMapping.TryAdd(Math.Round(col.origX, 1), newX);
        }

        foreach (var n in nodes.Values)
        {
            if (!n.IsDummy) continue;
            double roundedX = Math.Round(n.X, 1);
            if (xMapping.TryGetValue(roundedX, out double newX))
            {
                n.X = newX;
            }
            else
            {
                // Find nearest mapped X
                double closestKey = 0;
                double closestDist = double.MaxValue;
                foreach (var key in xMapping.Keys)
                {
                    double dist = Math.Abs(key - roundedX);
                    if (dist < closestDist) { closestDist = dist; closestKey = key; }
                }
                if (closestDist < double.MaxValue)
                    n.X += xMapping[closestKey] - closestKey;
            }
        }

        AppLogger.Info($"[FLOWCHART-V3] CompactColumns: {columns.Count} columns → {slots.Count} slots");
    }

    // ── SVG rendering ────────────────────────────────────────────────

    private static string RenderSvg(string areaName,
        Dictionary<string, FNode> nodes, List<List<string>> layers,
        List<(string from, string to)> realEdges, bool forDiscord = false,
        Dictionary<string, string>? itemIcons = null,
        Dictionary<string, string>? tokenIcons = null)
    {
        var ci = CultureInfo.InvariantCulture;

        // Calculate viewport
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var n in nodes.Values.Where(n => !n.IsDummy))
        {
            minX = Math.Min(minX, n.X);
            minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.Width);
            maxY = Math.Max(maxY, n.Y + n.Height);
        }

        foreach (var n in nodes.Values.Where(n => n.IsDummy))
        {
            minX = Math.Min(minX, n.X - 10);
            maxX = Math.Max(maxX, n.X + 10);
        }

        double margin = 30;
        double svgW = maxX - minX + margin * 2;
        double svgH = maxY - minY + margin * 2;
        double offsetX = -minX + margin;
        double offsetY = -minY + margin;

        var sb = new StringBuilder();

        // SVG header (SVG 1.1 + xlink for image href, Fandom-compatible)
        if (!forDiscord)
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" version=\"1.1\"");
        sb.AppendLine($"     width=\"{svgW.ToString(ci)}\" height=\"{svgH.ToString(ci)}\"");
        sb.AppendLine($"     viewBox=\"0 0 {svgW.ToString(ci)} {svgH.ToString(ci)}\">");
        sb.AppendLine();

        sb.AppendLine($"  <title>{Esc(areaName)} — Task Flowchart</title>");
        sb.AppendLine();

        // Inline CSS — Merge Mansion warm theme
        sb.AppendLine("  <style>");
        sb.AppendLine("    @import url('https://fonts.cdnfonts.com/css/tisa-sans-pro');");
        sb.AppendLine("    svg { scroll-behavior: smooth; background: #FBF4E8; width: 100%; min-width: " + svgW.ToString(ci) + "px; }");
        sb.AppendLine("    .node-header { fill: url(#grad-header); }");
        sb.AppendLine("    .node-body { fill: url(#grad-body); filter: url(#shadow-node); }");
        sb.AppendLine("    .node-stroke-outer { fill: none; stroke: #c3732a; stroke-width: 2; }");
        sb.AppendLine("    .node-stroke-inner { fill: none; stroke: #91521d; stroke-width: 2; }");
        sb.AppendLine("    .header-idx { fill: #d4e8ff; font-family: 'Tisa Sans Pro', Georgia, serif; font-size: 12px; font-style: italic; }");
        sb.AppendLine("    .header-title { fill: #eef7ff; stroke: #124d76; stroke-width: 3px; paint-order: stroke fill; font-family: 'Tisa Sans Pro', Georgia, serif; font-size: 13px; font-weight: bold; font-style: italic; }");
        sb.AppendLine("    .item-qty { fill: #9B7B58; font-family: 'Tisa Sans Pro', Trebuchet MS, sans-serif; font-size: 11.5px; font-weight: 500; }");
        sb.AppendLine("    .item-name { fill: #955417; font-family: 'Tisa Sans Pro', Trebuchet MS, sans-serif; font-size: 11.5px; font-weight: 500; }");
        sb.AppendLine("    .reward-text { fill: #9B7B58; font-family: 'Tisa Sans Pro', Trebuchet MS, sans-serif; font-size: 11px; font-style: italic; }");
        sb.AppendLine("    .sep-line { stroke: #D4A860; stroke-width: 0.8; stroke-dasharray: 4 3; }");
        sb.AppendLine("    .link-ref { fill: #A07840; font-family: 'Tisa Sans Pro', Trebuchet MS, sans-serif; font-size: 9px; cursor: pointer; }");
        sb.AppendLine("    .link-ref-header { fill: #96c0f0; font-family: 'Tisa Sans Pro', Trebuchet MS, sans-serif; font-size: 9px; cursor: pointer; }");
        sb.AppendLine("    .link-ref:hover { fill: #E8961E; text-decoration: underline; }");
        sb.AppendLine("    .link-ref-header:hover { fill: #FFFFFF; text-decoration: underline; }");
        sb.AppendLine("    .item-slot { fill: #D4C4A8; stroke: #B8A888; stroke-width: 1; }");
        sb.AppendLine("    .icon-hover { transition: transform 0.3s ease; }");
        sb.AppendLine("    .icon-hover:hover { transform: scale(1.1); }");
        sb.AppendLine("    a .header-idx:hover { text-decoration: underline; }");
        sb.AppendLine("    a .item-name { cursor: pointer; }");
        sb.AppendLine("    a .item-name:hover { text-decoration: underline; }");
        sb.AppendLine("    a .reward-text { cursor: pointer; }");
        sb.AppendLine("    a .reward-text:hover { text-decoration: underline; }");
        sb.AppendLine("    .token-circle { fill: #FFFDF5; stroke: #C8B088; stroke-width: 1.5; }");
        sb.AppendLine("    .token-value { fill: #6B4520; stroke: #FFFDF5; stroke-width: 2.5px; paint-order: stroke fill; font-family: 'Tisa Sans Pro', Georgia, serif; font-weight: 800; text-anchor: middle; }");
        sb.AppendLine("    .edge-path { fill: none; stroke: #B8874A; stroke-width: 1.5; }");
        sb.AppendLine("    .edge-arrow { fill: #9C6F3A; }");
        sb.AppendLine("    @keyframes node-shake { 0%,100%{transform:translate(0,0)} 15%,55%{transform:translate(-1.5px,0)} 35%,75%{transform:translate(1.5px,0)} }");
        sb.AppendLine("    @keyframes node-glow { 0%{filter:drop-shadow(0 0 6px rgba(255,248,220,0.9)) drop-shadow(0 0 14px rgba(230,180,60,0.7)) drop-shadow(0 0 28px rgba(200,130,20,0.4))} 40%{filter:drop-shadow(0 0 4px rgba(255,240,200,0.6)) drop-shadow(0 0 10px rgba(218,165,32,0.4))} 100%{filter:url(#shadow-node)} }");
        sb.AppendLine("    @keyframes node-stroke-flash { 0%{stroke:#E8C050;stroke-width:3} 30%{stroke:#D4A040;stroke-width:2.6} 100%{stroke:#c3732a;stroke-width:2} }");
        sb.AppendLine("    rect:target + g { transform-box:fill-box; transform-origin:center; filter:drop-shadow(0 0 6px rgba(255,248,220,0.9)) drop-shadow(0 0 14px rgba(230,180,60,0.7)) drop-shadow(0 0 28px rgba(200,130,20,0.4)); animation:node-shake 0.4s ease-in-out 0.3s, node-glow 3s ease-out 0.7s forwards }");
        sb.AppendLine("    rect:target + g .node-stroke-outer { stroke:#E8C050; stroke-width:3; animation:node-stroke-flash 3s ease-out 0.7s forwards }");
        if (forDiscord)
        {
            sb.AppendLine("    /* ── Interactive completed state (warm pastel green, game-inspired) ── */");
            sb.AppendLine("    [data-node-idx] { cursor: pointer; }");
            sb.AppendLine("    [data-node-idx] .node-stroke-outer, [data-node-idx] .node-stroke-inner { transition: stroke 0.3s ease; }");
            sb.AppendLine("    [data-node-idx] .node-body { transition: filter 0.4s ease; }");
            sb.AppendLine("    .check-icon { opacity: 0; transition: opacity 0.3s ease; }");
            sb.AppendLine("    @keyframes complete-glow { 0%{filter:drop-shadow(0 0 0px rgba(120,180,60,0))} 20%{filter:drop-shadow(0 0 12px rgba(120,190,60,0.8)) drop-shadow(0 0 24px rgba(80,160,40,0.5))} 60%{filter:drop-shadow(0 0 6px rgba(120,180,60,0.4)) drop-shadow(0 0 14px rgba(80,160,40,0.2))} 100%{filter:none} }");
            sb.AppendLine("    .completed { animation: complete-glow 0.8s ease-out; }");
            sb.AppendLine("    .completed .node-body { fill: url(#grad-body-done); filter: url(#shadow-node-done); }");
            sb.AppendLine("    .completed .node-header { fill: url(#grad-header-done); }");
            sb.AppendLine("    .completed .header-fill-btm { fill: #A0C470; }");
            sb.AppendLine("    .completed .node-stroke-outer { stroke: #8AAD5A; stroke-width: 2.2; }");
            sb.AppendLine("    .completed .node-stroke-inner { stroke: #7A9D4A; }");
            sb.AppendLine("    .completed .header-idx { fill: #E8F4D8; }");
            sb.AppendLine("    .completed .header-title { fill: #F0F8E4; stroke: #4A7A28; }");
            sb.AppendLine("    .completed .item-qty { fill: #6A8E3E; }");
            sb.AppendLine("    .completed .item-name { fill: #5A7E30; }");
            sb.AppendLine("    .completed .reward-text { fill: #6A8E3E; }");
            sb.AppendLine("    .completed .sep-line { stroke: #B8CC8A; }");
            sb.AppendLine("    .completed .link-ref { fill: #6A8E3E; }");
            sb.AppendLine("    .completed .link-ref-header { fill: #D8F0B8; }");
            sb.AppendLine("    .completed .item-slot { fill: #C8D8A8; stroke: #A0B888; }");
            sb.AppendLine("    .completed .check-icon { opacity: 1; }");
            sb.AppendLine("    .completed .token-circle { fill: #E8F4D0; stroke: #A0B888; }");
            sb.AppendLine("    .completed .token-value { fill: #5A7E30; }");
            sb.AppendLine("    /* ── Anchor highlight (JS-driven for HTML wrapper) ── */");
            sb.AppendLine("    @keyframes anchor-glow { 0%{filter:drop-shadow(0 0 8px rgba(255,220,100,0.9)) drop-shadow(0 0 18px rgba(230,180,60,0.6))} 50%{filter:drop-shadow(0 0 5px rgba(255,220,100,0.5)) drop-shadow(0 0 12px rgba(218,165,32,0.3))} 100%{filter:none} }");
            sb.AppendLine("    .anchor-highlight { animation: anchor-glow 2s ease-out forwards; }");
            sb.AppendLine("    .anchor-highlight .node-stroke-outer { stroke: #E8C050; stroke-width: 3; animation: node-stroke-flash 2s ease-out forwards; }");
        }
        sb.AppendLine("  </style>");
        sb.AppendLine();

        // Gradients, filters, markers
        sb.AppendLine("  <defs>");
        sb.AppendLine("    <linearGradient id=\"grad-header\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
        sb.AppendLine("      <stop offset=\"0%\" stop-color=\"#4A7CBF\" />");
        sb.AppendLine("      <stop offset=\"100%\" stop-color=\"#3A5F96\" />");
        sb.AppendLine("    </linearGradient>");
        sb.AppendLine("    <linearGradient id=\"grad-body\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
        sb.AppendLine("      <stop offset=\"0%\" stop-color=\"#FFF8EC\" />");
        sb.AppendLine("      <stop offset=\"100%\" stop-color=\"#FAEDD4\" />");
        sb.AppendLine("    </linearGradient>");
        sb.AppendLine("    <linearGradient id=\"grad-header-inset\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
        sb.AppendLine("      <stop offset=\"0%\" stop-color=\"#000\" stop-opacity=\"0.30\" />");
        sb.AppendLine("      <stop offset=\"100%\" stop-color=\"#000\" stop-opacity=\"0\" />");
        sb.AppendLine("    </linearGradient>");
        sb.AppendLine("    <linearGradient id=\"grad-header-shadow\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
        sb.AppendLine("      <stop offset=\"0%\" stop-color=\"#000\" stop-opacity=\"0.25\" />");
        sb.AppendLine("      <stop offset=\"35%\" stop-color=\"#000\" stop-opacity=\"0.10\" />");
        sb.AppendLine("      <stop offset=\"70%\" stop-color=\"#000\" stop-opacity=\"0.03\" />");
        sb.AppendLine("      <stop offset=\"100%\" stop-color=\"#000\" stop-opacity=\"0\" />");
        sb.AppendLine("    </linearGradient>");
        sb.AppendLine("    <filter id=\"shadow-node\" x=\"-4%\" y=\"-2%\" width=\"110%\" height=\"110%\">");
        sb.AppendLine("      <feDropShadow dx=\"2\" dy=\"3\" stdDeviation=\"3\" flood-color=\"#4A3520\" flood-opacity=\"0.2\" />");
        sb.AppendLine("    </filter>");
        if (forDiscord)
        {
            sb.AppendLine($"    <image id=\"xp-icon\" width=\"{IconSize.ToString(ci)}\" height=\"{IconSize.ToString(ci)}\"");
            sb.AppendLine($"           href=\"data:image/png;base64,{XP_ICON_BASE64}\"");
            sb.AppendLine($"           xlink:href=\"data:image/png;base64,{XP_ICON_BASE64}\" />");
        }
        sb.AppendLine($"    <marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"10\" refY=\"5\"");
        sb.AppendLine($"            markerWidth=\"{ArrowSize.ToString(ci)}\" markerHeight=\"{ArrowSize.ToString(ci)}\"");
        sb.AppendLine("            orient=\"auto-start-reverse\">");
        sb.AppendLine("      <path d=\"M 0 0 L 10 5 L 0 10 z\" class=\"edge-arrow\" />");
        sb.AppendLine("    </marker>");
        if (forDiscord)
        {
            sb.AppendLine("    <!-- Completed state gradients (warm pastel green) -->");
            sb.AppendLine("    <linearGradient id=\"grad-header-done\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
            sb.AppendLine("      <stop offset=\"0%\" stop-color=\"#B8D48C\" />");
            sb.AppendLine("      <stop offset=\"100%\" stop-color=\"#A0C470\" />");
            sb.AppendLine("    </linearGradient>");
            sb.AppendLine("    <linearGradient id=\"grad-body-done\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
            sb.AppendLine("      <stop offset=\"0%\" stop-color=\"#E8F2D8\" />");
            sb.AppendLine("      <stop offset=\"100%\" stop-color=\"#DCE8C8\" />");
            sb.AppendLine("    </linearGradient>");
            sb.AppendLine("    <filter id=\"shadow-node-done\" x=\"-8%\" y=\"-6%\" width=\"118%\" height=\"118%\">");
            sb.AppendLine("      <feDropShadow dx=\"0\" dy=\"0\" stdDeviation=\"4\" flood-color=\"#6AAE40\" flood-opacity=\"0.35\" />");
            sb.AppendLine("      <feDropShadow dx=\"1\" dy=\"2\" stdDeviation=\"2\" flood-color=\"#3A5A20\" flood-opacity=\"0.15\" />");
            sb.AppendLine("    </filter>");
            sb.AppendLine($"    <image id=\"check-icon\" width=\"28\" height=\"28\"");
            sb.AppendLine($"           href=\"data:image/png;base64,{CHECKMARK_ICON_BASE64}\"");
            sb.AppendLine($"           xlink:href=\"data:image/png;base64,{CHECKMARK_ICON_BASE64}\" />");
        }
        // Item icon images (deduplicated, Discord/HTML only) — each unique icon gets an <image> in defs
        if (forDiscord && itemIcons != null && itemIcons.Count > 0)
        {
            var iconDisplaySz = ItemIconDisplay.ToString(ci);
            sb.AppendLine("    <!-- Item icons -->");
            var emittedBase64 = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (itemType, base64) in itemIcons)
            {
                if (!emittedBase64.Add(base64)) continue; // skip duplicate images
                var iconId = ItemIconDefId(itemType);
                sb.AppendLine($"    <image id=\"{iconId}\" width=\"{iconDisplaySz}\" height=\"{iconDisplaySz}\"");
                sb.AppendLine($"           href=\"data:image/png;base64,{base64}\"");
                sb.AppendLine($"           xlink:href=\"data:image/png;base64,{base64}\" />");
            }
        }
        // Token icon images (Discord/HTML only)
        if (forDiscord && tokenIcons != null && tokenIcons.Count > 0)
        {
            var tokenSz = TokenIconSize.ToString(ci);
            sb.AppendLine("    <!-- Token icons -->");
            foreach (var (field, base64) in tokenIcons)
            {
                sb.AppendLine($"    <image id=\"token-{field}\" width=\"{tokenSz}\" height=\"{tokenSz}\"");
                sb.AppendLine($"           href=\"data:image/png;base64,{base64}\"");
                sb.AppendLine($"           xlink:href=\"data:image/png;base64,{base64}\" />");
            }
        }
        // Token shadow filter (subtle circular shadow)
        sb.AppendLine("    <filter id=\"shadow-token\" x=\"-20%\" y=\"-10%\" width=\"140%\" height=\"140%\">");
        sb.AppendLine("      <feDropShadow dx=\"0\" dy=\"1\" stdDeviation=\"1\" flood-color=\"#6A5030\" flood-opacity=\"0.25\" />");
        sb.AppendLine("    </filter>");
        sb.AppendLine("  </defs>");
        sb.AppendLine();

        // Render edges (behind nodes) — independent routing after final positions
        sb.AppendLine("  <!-- Edges -->");
        RouteEdges(sb, realEdges, nodes, layers, offsetX, offsetY, ci);

        sb.AppendLine();

        // Render nodes
        sb.AppendLine("  <!-- Nodes -->");
        foreach (var n in nodes.Values.Where(n => !n.IsDummy))
            RenderNode(sb, n, offsetX, offsetY, ci, forDiscord, itemIcons, areaName, tokenIcons);

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void RenderNode(StringBuilder sb, FNode n, double ox, double oy,
        CultureInfo ci, bool forDiscord = false, Dictionary<string, string>? itemIcons = null,
        string? areaName = null, Dictionary<string, string>? tokenIcons = null)
    {
        double x = n.X + ox;
        double y = n.Y + oy;
        double w = n.Width;
        double r = 10; // corner radius (game-style rounded panels)
        double headerH = n.HeaderLines == 2 ? 44.0 : HeaderH;
        double parentLinkH = n.ParentDisplayIndices.Count > 0 ? 13 : 0; // 7px top + 6px link
        if (parentLinkH > 0) headerH -= 5; // shorter header bar when parent links above

        // Invisible scroll anchor shifted above node so it lands ~center of viewport
        double anchorOffset = 300;
        sb.AppendLine($"  <rect id=\"node-{n.DisplayIndex}\" x=\"{x.ToString(ci)}\" y=\"{(y - anchorOffset).ToString(ci)}\"" +
                      $" width=\"0\" height=\"0\" fill=\"none\" />");

        string nodeAttrs;
        if (forDiscord)
        {
            var parentsAttr = n.ParentDisplayIndices.Count > 0
                ? $" data-parents=\"{string.Join(",", n.ParentDisplayIndices)}\""
                : "";
            nodeAttrs = $" data-node-idx=\"{n.DisplayIndex}\"{parentsAttr}";
        }
        else
        {
            nodeAttrs = "";
        }
        sb.AppendLine($"  <g id=\"ng-{n.DisplayIndex}\"{nodeAttrs}>");

        // Body rectangle (fill + shadow, no stroke)
        sb.AppendLine($"    <rect class=\"node-body\" x=\"{x.ToString(ci)}\" y=\"{y.ToString(ci)}\"" +
                      $" width=\"{w.ToString(ci)}\" height=\"{n.Height.ToString(ci)}\" rx=\"{r.ToString(ci)}\" />");

        // Combined header region: parent links (if any) + header bar, all with header colour
        double headerY = y; // top of combined header region
        double combinedHeaderH = parentLinkH + headerH;

        // Clip for rounded top corners of combined header
        sb.AppendLine($"    <clipPath id=\"clip-{n.DisplayIndex}\">");
        sb.AppendLine($"      <rect x=\"{x.ToString(ci)}\" y=\"{y.ToString(ci)}\"" +
                      $" width=\"{w.ToString(ci)}\" height=\"{combinedHeaderH.ToString(ci)}\" rx=\"{r.ToString(ci)}\" />");
        sb.AppendLine("    </clipPath>");
        sb.AppendLine($"    <rect class=\"node-header\" x=\"{x.ToString(ci)}\" y=\"{y.ToString(ci)}\"" +
                      $" width=\"{w.ToString(ci)}\" height=\"{combinedHeaderH.ToString(ci)}\"" +
                      $" clip-path=\"url(#clip-{n.DisplayIndex})\" />");
        // Inset shadow at top of header (subtle top-down darkening)
        double insetShadowH = 8;
        sb.AppendLine($"    <rect fill=\"url(#grad-header-inset)\" x=\"{x.ToString(ci)}\" y=\"{y.ToString(ci)}\"" +
                      $" width=\"{w.ToString(ci)}\" height=\"{insetShadowH.ToString(ci)}\"" +
                      $" clip-path=\"url(#clip-{n.DisplayIndex})\" />");
        // Fill bottom corners of combined header
        double stripY = y + combinedHeaderH - r;
        if (stripY > y)
        {
            var btmCls = forDiscord ? " class=\"header-fill-btm\"" : "";
            sb.AppendLine($"    <rect fill=\"#3A5F96\"{btmCls} x=\"{x.ToString(ci)}\" y=\"{stripY.ToString(ci)}\"" +
                          $" width=\"{w.ToString(ci)}\" height=\"{r.ToString(ci)}\" />");
        }

        // Shadow below header (header casts shadow onto body)
        double shadowBelowH = 5;
        sb.AppendLine($"    <rect fill=\"url(#grad-header-shadow)\" x=\"{x.ToString(ci)}\" y=\"{(y + combinedHeaderH).ToString(ci)}\"" +
                      $" width=\"{w.ToString(ci)}\" height=\"{shadowBelowH.ToString(ci)}\" />");

        // ── Parent link section (above header bar, header-coloured background) ──
        if (n.ParentDisplayIndices.Count > 0)
        {
            double linkY = y + 7 + 6 * 0.75; // 7px top pad, centered in 6px link area
            double linkX = x + w / 2; // centered
            RenderLinkRefs(sb, n.ParentDisplayIndices, linkX, linkY, ci, "link-ref-header");
        }

        // Actual header bar starts after parent links
        double headerBarY = y + parentLinkH;

        // ── Header text: "#NNN" (right-aligned, wiki-linked) + "Title" (left-aligned) ──

        double idxEndX = x + PadX + IndexColW;
        double titleStartX = idxEndX + IndexTitleGap;
        double availTitleW = w - PadX - IndexColW - IndexTitleGap - PadX;

        // Wiki link for task ID: area#TNNN
        string? taskHref = areaName != null ? $"{WikiUrl(areaName)}#T{n.DisplayIndex}" : null;

        if (n.HeaderLines == 2)
        {
            // 2-line header: index centered vertically, title on two lines (+1px bottom nudge)
            double idxY = headerBarY + headerH / 2 + FontSzHeader * 0.35 + 1;
            if (parentLinkH > 0) idxY -= 5;
            if (taskHref != null)
            {
                sb.AppendLine($"    <a href=\"{Esc(taskHref)}\" target=\"_blank\">");
                sb.AppendLine($"      <text class=\"header-idx\" x=\"{idxEndX.ToString(ci)}\" y=\"{idxY.ToString(ci)}\"" +
                              $" text-anchor=\"end\">#{n.DisplayIndex}</text>");
                sb.AppendLine("    </a>");
            }
            else
            {
                sb.AppendLine($"    <text class=\"header-idx\" x=\"{idxEndX.ToString(ci)}\" y=\"{idxY.ToString(ci)}\"" +
                              $" text-anchor=\"end\">#{n.DisplayIndex}</text>");
            }

            int maxChars = MaxTitleChars;
            int breakAt = n.Title.LastIndexOf(' ', Math.Min(maxChars, n.Title.Length - 1));
            if (breakAt <= 0) breakAt = maxChars;
            string line1 = n.Title[..breakAt].TrimEnd();
            string line2 = n.Title[breakAt..].TrimStart();
            if (line2.Length > maxChars && maxChars > 3)
                line2 = line2[..(maxChars - 3)] + "...";

            double line1Y = headerBarY + headerH / 2 - FontSzTitle * 0.25 + 1;
            if (parentLinkH > 0) line1Y -= 5;
            double line2Y = line1Y + FontSzTitle * 1.3;
            sb.AppendLine($"    <text class=\"header-title\" x=\"{titleStartX.ToString(ci)}\" y=\"{line1Y.ToString(ci)}\"" +
                          $">{Esc(line1)}</text>");
            sb.AppendLine($"    <text class=\"header-title\" x=\"{titleStartX.ToString(ci)}\" y=\"{line2Y.ToString(ci)}\"" +
                          $">{Esc(line2)}</text>");
        }
        else
        {
            // Title baseline (larger font with outline)
            double titleY = headerBarY + headerH / 2 + FontSzTitle * 0.35 + 1;
            // Index baseline — nudge up 1.5px to visually center against outlined title
            double idxY = headerBarY + headerH / 2 + FontSzHeader * 0.35 - 0.5;
            // When parent links present, nudge title+idx up to reduce link→title gap
            if (parentLinkH > 0) { titleY -= 5; idxY -= 5; }

            // Index (right-aligned, wiki-linked)
            if (taskHref != null)
            {
                sb.AppendLine($"    <a href=\"{Esc(taskHref)}\" target=\"_blank\">");
                sb.AppendLine($"      <text class=\"header-idx\" x=\"{idxEndX.ToString(ci)}\" y=\"{idxY.ToString(ci)}\"" +
                              $" text-anchor=\"end\">#{n.DisplayIndex}</text>");
                sb.AppendLine("    </a>");
            }
            else
            {
                sb.AppendLine($"    <text class=\"header-idx\" x=\"{idxEndX.ToString(ci)}\" y=\"{idxY.ToString(ci)}\"" +
                              $" text-anchor=\"end\">#{n.DisplayIndex}</text>");
            }

            // Title (left-aligned, truncated if needed)
            string title = TruncateText(n.Title, availTitleW, FontSzTitle, true);
            sb.AppendLine($"    <text class=\"header-title\" x=\"{titleStartX.ToString(ci)}\" y=\"{titleY.ToString(ci)}\"" +
                          $">{Esc(title)}</text>");
        }

        // ── Body content ──

        double textX = x + PadX;
        double cy = headerBarY + headerH + PadY;

        // Items (quantity aligned + name aligned, optionally with icon slots — icons only in HTML/Discord)
        bool hasAnyIcon = forDiscord && n.Requirements.Any(r => r.HasIcon);
        double iconOffset = hasAnyIcon ? ItemSlotSize + ItemSlotGap : 0;
        double qtyEndX = x + PadX + iconOffset + QtyColW;
        double nameStartX = qtyEndX + QtyNameGap;
        double availNameW = w - PadX - iconOffset - QtyColW - QtyNameGap - PadX;
        double itemLineH = hasAnyIcon ? ItemSlotSize + 2 : LineH;

        foreach (var req in n.Requirements)
        {
            if (hasAnyIcon)
            {
                // Icon slot (rounded rect + icon image, wiki-linked)
                double slotX = x + PadX;
                double slotY = cy;

                // Wrap slot in <a> link to chain wiki page if available
                string? chainHref = req.ChainWikiName != null ? WikiUrl(req.ChainWikiName) : null;
                if (chainHref != null)
                    sb.AppendLine($"    <a href=\"{Esc(chainHref)}\" target=\"_blank\">");

                sb.AppendLine($"    <rect class=\"item-slot\" x=\"{slotX.ToString(ci)}\" y=\"{slotY.ToString(ci)}\"" +
                              $" width=\"{ItemSlotSize.ToString(ci)}\" height=\"{ItemSlotSize.ToString(ci)}\"" +
                              $" rx=\"{ItemSlotCorner.ToString(ci)}\" />");

                if (req.HasIcon && itemIcons != null && itemIcons.ContainsKey(req.ItemType))
                {
                    double iconX = slotX + (ItemSlotSize - ItemIconDisplay) / 2;
                    double iconY2 = slotY + (ItemSlotSize - ItemIconDisplay) / 2;
                    double iconCx = iconX + ItemIconDisplay / 2;
                    double iconCy = iconY2 + ItemIconDisplay / 2;
                    var iconId = ItemIconDefId(req.ItemType);
                    sb.AppendLine($"    <g class=\"icon-hover\" style=\"transform-origin:{iconCx.ToString(ci)}px {iconCy.ToString(ci)}px\">");
                    sb.AppendLine($"      <use href=\"#{iconId}\" xlink:href=\"#{iconId}\"" +
                                  $" x=\"{iconX.ToString(ci)}\" y=\"{iconY2.ToString(ci)}\" />");
                    sb.AppendLine("    </g>");
                }

                if (chainHref != null)
                    sb.AppendLine("    </a>");

                // Text vertically centered with icon slot
                double textCy = slotY + ItemSlotSize / 2 + FontSzItem * 0.35;

                // Quantity (right-aligned)
                sb.AppendLine($"    <text class=\"item-qty\" x=\"{qtyEndX.ToString(ci)}\" y=\"{textCy.ToString(ci)}\"" +
                              $" text-anchor=\"end\">{req.Qty}x</text>");

                // Item name (left-aligned, with tooltip)
                string itemText = TruncateText(req.ItemName, availNameW, FontSzItem, false);
                if (req.Tooltip != null)
                {
                    sb.AppendLine($"    <text class=\"item-name\" x=\"{nameStartX.ToString(ci)}\" y=\"{textCy.ToString(ci)}\">");
                    sb.AppendLine($"      <title>{Esc(req.Tooltip)}</title>");
                    sb.AppendLine($"      {Esc(itemText)}</text>");
                }
                else
                {
                    sb.AppendLine($"    <text class=\"item-name\" x=\"{nameStartX.ToString(ci)}\" y=\"{textCy.ToString(ci)}\"" +
                                  $">{Esc(itemText)}</text>");
                }
                cy += itemLineH;
            }
            else
            {
                cy += FontSzItem * 0.85;

                // Quantity (right-aligned)
                sb.AppendLine($"    <text class=\"item-qty\" x=\"{qtyEndX.ToString(ci)}\" y=\"{cy.ToString(ci)}\"" +
                              $" text-anchor=\"end\">{req.Qty}x</text>");

                // Item name (left-aligned, wiki-linked to chain page)
                string itemText = TruncateText(req.ItemName, availNameW, FontSzItem, false);
                string? chainHref2 = req.ChainWikiName != null ? WikiUrl(req.ChainWikiName) : null;

                if (chainHref2 != null)
                    sb.AppendLine($"    <a href=\"{Esc(chainHref2)}\" target=\"_blank\">");

                if (req.Tooltip != null)
                {
                    sb.AppendLine($"    <text class=\"item-name\" x=\"{nameStartX.ToString(ci)}\" y=\"{cy.ToString(ci)}\">");
                    sb.AppendLine($"      <title>{Esc(req.Tooltip)}</title>");
                    sb.AppendLine($"      {Esc(itemText)}</text>");
                }
                else
                {
                    sb.AppendLine($"    <text class=\"item-name\" x=\"{nameStartX.ToString(ci)}\" y=\"{cy.ToString(ci)}\"" +
                                  $">{Esc(itemText)}</text>");
                }

                if (chainHref2 != null)
                    sb.AppendLine("    </a>");

                cy += LineH - FontSzItem * 0.85;
            }
        }

        // ── Reward section (XP + optional reward item; tokens float right) ──

        bool hasXp = n.XpReward.HasValue && n.XpReward.Value > 0;
        bool hasItemReward = !string.IsNullOrEmpty(n.ItemRewardText);
        bool hasReward = hasXp || hasItemReward;
        bool hasTokens = n.TokenValues.Count > 0 && forDiscord;

        if (hasReward || hasTokens)
        {
            // Separator
            cy += SepGap / 2;
            sb.AppendLine($"    <line class=\"sep-line\" x1=\"{x.ToString(ci)}\" y1=\"{cy.ToString(ci)}\"" +
                          $" x2=\"{(x + w).ToString(ci)}\" y2=\"{cy.ToString(ci)}\" />");
            cy += SepGap / 2;

            double rewardTop = cy;
            // rewardBottom must match sizing exactly
            double bottomPad = hasTokens ? 2 : PadY;
            double childSectionH = n.ChildDisplayIndices.Count > 0
                ? SepGap + LinkLineH + LinkSectionPad + 2 : 0;
            double rewardBottom = y + n.Height - bottomPad - childSectionH;
            double rewardAvail = rewardBottom - rewardTop;

            // ── Tokens (float right) ──
            if (hasTokens)
            {
                var tokenEntries = new List<(string field, int value)>();
                foreach (var (tf, _) in TokenSlotOrder)
                    if (n.TokenValues.TryGetValue(tf, out var tv) && tv > 0)
                        tokenEntries.Add((tf, tv));

                if (tokenEntries.Count > 0)
                {
                    int tRows = (tokenEntries.Count + TokenCols - 1) / TokenCols;
                    double gridW = TokenCols * TokenColW;
                    double gridStartX = x + w - PadX - gridW;
                    double gridTopY = rewardTop + 2;

                    for (int i = 0; i < tokenEntries.Count; i++)
                    {
                        int row = i / TokenCols;
                        int col = i % TokenCols;
                        var (field, value) = tokenEntries[i];

                        double cellCx = gridStartX + col * TokenColW + TokenColW / 2;
                        double cellTopY = gridTopY + row * (TokenRowH + 3); // 3px inter-row gap
                        double circleCy = cellTopY + TokenSlotSize / 2 + 2;

                        // Circle (small background)
                        sb.AppendLine($"    <circle class=\"token-circle\" cx=\"{cellCx.ToString(ci)}\" cy=\"{circleCy.ToString(ci)}\"" +
                                      $" r=\"{(TokenSlotSize / 2).ToString(ci)}\" filter=\"url(#shadow-token)\" />");

                        // Icon (overflows top of circle)
                        if (tokenIcons != null && tokenIcons.ContainsKey(field))
                        {
                            double tiX = cellCx - TokenIconSize / 2;
                            double tiY = circleCy - TokenSlotSize / 2 - TokenIconSize * 0.25; // overflow top
                            sb.AppendLine($"    <use href=\"#token-{field}\" xlink:href=\"#token-{field}\"" +
                                          $" x=\"{tiX.ToString(ci)}\" y=\"{tiY.ToString(ci)}\" />");
                        }

                        // Value text (overlaps bottom of circle, italic)
                        double valY = circleCy + TokenSlotSize / 2 - 1;
                        sb.AppendLine($"    <text class=\"token-value\" x=\"{cellCx.ToString(ci)}\" y=\"{valY.ToString(ci)}\"" +
                                      $" font-size=\"{TokenFontSz.ToString(ci)}\" font-style=\"italic\">{value}</text>");
                    }
                }
            }

            // ── Left side: XP line ──
            double xpLineH = hasXp ? IconSize + 4 : 0;
            double leftCursorY = rewardTop;

            if (hasXp)
            {
                double xpCenterY = leftCursorY + xpLineH / 2;
                double iconY2 = xpCenterY - IconSize / 2;
                double xpTextY = xpCenterY + FontSzReward * 0.35;
                double cursorX = textX + 8;

                if (forDiscord)
                {
                    sb.AppendLine($"    <use href=\"#xp-icon\" xlink:href=\"#xp-icon\" x=\"{cursorX.ToString(ci)}\" y=\"{iconY2.ToString(ci)}\" />");
                    cursorX += IconSize + 4;
                }
                else
                {
                    sb.AppendLine($"    <text class=\"reward-text\" x=\"{cursorX.ToString(ci)}\" y=\"{xpTextY.ToString(ci)}\"" +
                                  $" font-weight=\"bold\">XP</text>");
                    cursorX += 2 * FontSzReward * 0.65 + 4;
                }

                string xpStr = n.XpReward!.Value.ToString();
                sb.AppendLine($"    <text class=\"reward-text\" x=\"{cursorX.ToString(ci)}\" y=\"{xpTextY.ToString(ci)}\"" +
                              $">{Esc(xpStr)}</text>");

                // SVG: inline item reward after XP (no separate line)
                if (hasItemReward && !forDiscord)
                {
                    cursorX += xpStr.Length * FontSzReward * 0.60 + 4;
                    string itemStr = $" · {n.ItemRewardText}";
                    double availW = w - (cursorX - x) - PadX;
                    string truncItem = TruncateText(itemStr, availW, FontSzReward, false);
                    var rewardHref = n.ItemRewardWikiName != null ? WikiUrl(n.ItemRewardWikiName) : null;
                    if (rewardHref != null)
                        sb.AppendLine($"    <a href=\"{Esc(rewardHref)}\" target=\"_blank\">");
                    sb.AppendLine($"    <text class=\"reward-text\" x=\"{cursorX.ToString(ci)}\" y=\"{xpTextY.ToString(ci)}\"" +
                                  $">{Esc(truncItem)}</text>");
                    if (rewardHref != null)
                        sb.AppendLine("    </a>");
                }

                leftCursorY += xpLineH + 6; // 6px gap before reward item line
            }

            // ── Left side: Reward item on new line (HTML), or standalone (no XP) ──
            if (hasItemReward && (forDiscord || !hasXp))
            {
                if (forDiscord)
                {
                    double riLineY = leftCursorY + LineH / 2 + FontSzReward * 0.35 + 1;
                    double cursorX = textX + 8;

                    // Reward item icon
                    bool hasRewardIcon = n.ItemRewardType != null &&
                        itemIcons != null && itemIcons.ContainsKey(n.ItemRewardType);
                    if (hasRewardIcon)
                    {
                        double riSize = 24; // display size for reward item icon
                        double riScale = riSize / ItemIconDisplay; // scale from defs size
                        double riImgY = leftCursorY + (LineH - riSize) / 2 + 1;
                        double riCx = cursorX + riSize / 2;
                        double riCy = riImgY + riSize / 2;
                        var riHref = n.ItemRewardWikiName != null ? WikiUrl(n.ItemRewardWikiName) : null;
                        if (riHref != null)
                            sb.AppendLine($"    <a href=\"{Esc(riHref)}\" target=\"_blank\">");
                        var riIconId = ItemIconDefId(n.ItemRewardType!);
                        // Scale icon from defs size (32) to display size via transform
                        sb.AppendLine($"    <g class=\"icon-hover\" style=\"transform-origin:{riCx.ToString(ci)}px {riCy.ToString(ci)}px\">");
                        sb.AppendLine($"      <g transform=\"translate({cursorX.ToString(ci)},{riImgY.ToString(ci)}) scale({riScale.ToString(ci)})\">");
                        sb.AppendLine($"        <use href=\"#{riIconId}\" xlink:href=\"#{riIconId}\" x=\"0\" y=\"0\" />");
                        sb.AppendLine("      </g>");
                        sb.AppendLine("    </g>");
                        if (riHref != null) sb.AppendLine("    </a>");
                        cursorX += riSize + 4;
                    }

                    // Reward item text (limited width to not overlap tokens)
                    double tokenGridW = hasTokens ? TokenCols * TokenColW + 4 : 0;
                    double rwAvailW = w - (cursorX - x) - PadX - tokenGridW;
                    string truncReward = TruncateText(n.ItemRewardText!, rwAvailW, FontSzReward, false);
                    var rwHref = n.ItemRewardWikiName != null ? WikiUrl(n.ItemRewardWikiName) : null;
                    if (rwHref != null)
                        sb.AppendLine($"    <a href=\"{Esc(rwHref)}\" target=\"_blank\">");
                    sb.AppendLine($"    <text class=\"reward-text\" x=\"{cursorX.ToString(ci)}\" y=\"{riLineY.ToString(ci)}\"" +
                                  $">{Esc(truncReward)}</text>");
                    if (rwHref != null) sb.AppendLine("    </a>");
                }
                else if (!hasXp)
                {
                    // SVG: item reward only (no XP)
                    double centerY = rewardTop + rewardAvail / 2 + FontSzReward * 0.35;
                    double cursorX = textX + 8;
                    string truncReward = TruncateText(n.ItemRewardText!, w - PadX * 2 - 8, FontSzReward, false);
                    var rwHref = n.ItemRewardWikiName != null ? WikiUrl(n.ItemRewardWikiName) : null;
                    if (rwHref != null)
                        sb.AppendLine($"    <a href=\"{Esc(rwHref)}\" target=\"_blank\">");
                    sb.AppendLine($"    <text class=\"reward-text\" x=\"{cursorX.ToString(ci)}\" y=\"{centerY.ToString(ci)}\"" +
                                  $">{Esc(truncReward)}</text>");
                    if (rwHref != null) sb.AppendLine("    </a>");
                }
            }

            cy = rewardBottom;
        }

        // ── Child link section (below rewards, with separator) ──
        if (n.ChildDisplayIndices.Count > 0)
        {
            bool hasTokens2 = n.TokenValues.Count > 0 && forDiscord;
            double bPad = hasTokens2 ? 2 : PadY;
            double cSec = SepGap + LinkLineH + LinkSectionPad + 2;
            double sepY = y + n.Height - bPad - cSec + SepGap / 2;
            sb.AppendLine($"    <line class=\"sep-line\" x1=\"{x.ToString(ci)}\" y1=\"{sepY.ToString(ci)}\"" +
                          $" x2=\"{(x + w).ToString(ci)}\" y2=\"{sepY.ToString(ci)}\" />");
            // Center link text vertically between separator and bottom edge (accounting for stroke inset, -2px nudge)
            double footerTop = sepY + SepGap / 2;
            double footerBottom = y + n.Height - 3;
            double linkY = footerTop + (footerBottom - footerTop) / 2 + LinkFontSz * 0.35 - 2;
            double linkX = x + w / 2; // centered
            RenderLinkRefs(sb, n.ChildDisplayIndices, linkX, linkY, ci);
        }

        // Double stroke on top of everything: outer 2px #c3732a, inner 2px #91521d (inset by 2px)
        sb.AppendLine($"    <rect class=\"node-stroke-outer\" x=\"{x.ToString(ci)}\" y=\"{y.ToString(ci)}\"" +
                      $" width=\"{w.ToString(ci)}\" height=\"{n.Height.ToString(ci)}\" rx=\"{r.ToString(ci)}\" />");
        double si = 2; // stroke inset for inner border
        sb.AppendLine($"    <rect class=\"node-stroke-inner\" x=\"{(x + si).ToString(ci)}\" y=\"{(y + si).ToString(ci)}\"" +
                      $" width=\"{(w - si * 2).ToString(ci)}\" height=\"{(n.Height - si * 2).ToString(ci)}\" rx=\"{(r - si).ToString(ci)}\" />");

        // Checkmark badge (hidden by default, shown when .completed)
        if (forDiscord)
        {
            double checkSz = 28;
            double checkX = x + w - checkSz - 2;
            double checkY = y - 4;
            sb.AppendLine($"    <use class=\"check-icon\" href=\"#check-icon\" xlink:href=\"#check-icon\"" +
                          $" x=\"{checkX.ToString(ci)}\" y=\"{checkY.ToString(ci)}\" />");
        }

        sb.AppendLine("  </g>");
    }

    /// <summary>
    /// Renders a row of clickable "[#NNN]" link references, centered at the given position.
    /// </summary>
    private static void RenderLinkRefs(StringBuilder sb, List<int> indices,
        double centerX, double y, CultureInfo ci, string cssClass = "link-ref")
    {
        // Build link texts and measure total width
        const double charW = LinkFontSz * 0.62;
        const double gap = 4;

        var items = indices.Select(idx =>
        {
            string text = $"[#{idx}]";
            double w = text.Length * charW;
            return (idx, text, w);
        }).ToList();

        double totalW = items.Sum(i => i.w) + (items.Count - 1) * gap;
        double cursorX = centerX - totalW / 2;

        foreach (var (idx, text, w) in items)
        {
            sb.AppendLine($"    <a href=\"#node-{idx}\" xlink:href=\"#node-{idx}\">");
            sb.AppendLine($"      <text class=\"{cssClass}\" x=\"{cursorX.ToString(ci)}\" y=\"{y.ToString(ci)}\">{Esc(text)}</text>");
            sb.AppendLine("    </a>");
            cursorX += w + gap;
        }
    }

    // ── Edge routing (independent of dummy nodes) ─────────────────

    /// <summary>
    /// Routes all edges independently after final node positions.
    /// Strategy 1: Straight vertical (if source and target aligned)
    /// Strategy 2: Z-bend at inter-layer gap
    /// Strategy 3: Corridor routing (2 horizontal + vertical corridor)
    /// </summary>
    private static void RouteEdges(StringBuilder sb,
        List<(string from, string to)> realEdges,
        Dictionary<string, FNode> nodes, List<List<string>> layers,
        double ox, double oy, CultureInfo ci)
    {
        // Build bounding boxes for collision detection (real nodes only, with margin)
        double margin = EdgePad + 2;
        var nodeBoxes = new List<(double left, double top, double right, double bottom, string id)>();
        foreach (var n in nodes.Values)
        {
            if (n.IsDummy) continue;
            nodeBoxes.Add((n.X + ox, n.Y + oy, n.X + n.Width + ox, n.Y + n.Height + oy, n.Id));
        }

        // Pre-compute inter-layer gaps (Y ranges between consecutive layers)
        var layerBottom = new double[layers.Count]; // max Y+H for each layer
        var layerTop = new double[layers.Count];    // min Y for each layer
        for (int li = 0; li < layers.Count; li++)
        {
            double minY = double.MaxValue, maxYH = double.MinValue;
            foreach (var nid in layers[li])
            {
                var n = nodes[nid];
                if (n.IsDummy) continue;
                double ny = n.Y + oy;
                if (ny < minY) minY = ny;
                if (ny + n.Height > maxYH) maxYH = ny + n.Height;
            }
            if (minY == double.MaxValue) { minY = 0; maxYH = 0; }
            layerTop[li] = minY;
            layerBottom[li] = maxYH;
        }

        // Build layer index for each real node
        var nodeLayer = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int li = 0; li < layers.Count; li++)
            foreach (var nid in layers[li])
                if (nodes.TryGetValue(nid, out var n) && !n.IsDummy)
                    nodeLayer[nid] = li;

        // Count outgoing edges per source for bus detection
        var edgeCountBySource = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (from, _) in realEdges)
            edgeCountBySource[from] = edgeCountBySource.GetValueOrDefault(from) + 1;

        // Count incoming edges per target for convergence detection
        var edgeCountByTarget = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, to) in realEdges)
            edgeCountByTarget[to] = edgeCountByTarget.GetValueOrDefault(to) + 1;

        // ── Pass 1: Route all edges and collect filtered waypoints ──
        var edgeRoutes = new List<(string from, string to, List<(double x, double y)> pts)>();

        foreach (var (fromId, toId) in realEdges)
        {
            if (!nodes.TryGetValue(fromId, out var source) || source.IsDummy) continue;
            if (!nodes.TryGetValue(toId, out var target)) continue;

            double sx = source.X + source.Width / 2 + ox;
            double sy = source.Y + source.Height + oy + EdgePad;
            double tx = target.X + target.Width / 2 + ox;
            double ty = target.Y + oy;

            // Safety: skip backward edges (source below target — layout error)
            if (sy >= ty) continue;

            bool isMultiChild = edgeCountBySource.GetValueOrDefault(fromId) > 1;
            int srcLayer = nodeLayer.GetValueOrDefault(fromId, -1);
            int tgtLayer = nodeLayer.GetValueOrDefault(toId, -1);

            // Collect waypoints
            var pts = new List<(double x, double y)> { (sx, sy) };

            double routeStartX = sx;
            double routeStartY = sy;

            // Bus segment for multi-child sources
            if (isMultiChild)
            {
                double busY = sy + BusOffset;
                pts.Add((sx, busY));
                routeStartY = busY;
            }

            // Strategy 1: Straight vertical (aligned)
            bool straightRouted = false;
            if (Math.Abs(routeStartX - tx) < SnapThresh)
            {
                if (!SegmentHitsNode(routeStartX, routeStartY, routeStartX, ty,
                    nodeBoxes, fromId, toId, margin))
                {
                    pts.Add((routeStartX, ty));
                    straightRouted = true;
                }
            }

            if (!straightRouted)
            {
                // Strategy 2: Z-bend at each inter-layer gap
                bool routed = false;
                if (srcLayer >= 0 && tgtLayer >= 0 && tgtLayer > srcLayer)
                {
                    for (int gapLayer = srcLayer; gapLayer < tgtLayer; gapLayer++)
                    {
                        double gapY = (layerBottom[gapLayer] + layerTop[gapLayer + 1]) / 2;
                        if (gapY <= routeStartY + 2 || gapY >= ty - 2) continue;

                        bool hit =
                            SegmentHitsNode(routeStartX, routeStartY, routeStartX, gapY,
                                nodeBoxes, fromId, toId, margin) ||
                            SegmentHitsNode(routeStartX, gapY, tx, gapY,
                                nodeBoxes, fromId, toId, margin) ||
                            SegmentHitsNode(tx, gapY, tx, ty,
                                nodeBoxes, fromId, toId, margin);

                        if (!hit)
                        {
                            pts.Add((routeStartX, gapY));
                            pts.Add((tx, gapY));
                            pts.Add((tx, ty));
                            routed = true;
                            break;
                        }
                    }
                }

                if (!routed)
                {
                    bool sameColumn = Math.Abs(routeStartX - tx) < MaxNodeW / 2;

                    if (sameColumn)
                    {
                        // Strategy 3a: Same-column edge with blocking nodes.
                        double jogOffset = MaxNodeW / 2 + NodeGapX / 2;

                        double jogLeft = routeStartX - jogOffset;
                        double jogRight = routeStartX + jogOffset;

                        double gapY1 = routeStartY + BusOffset;
                        double gapY2 = ty - BusOffset;
                        if (srcLayer >= 0 && tgtLayer >= 0 && tgtLayer > srcLayer)
                        {
                            double g1 = (layerBottom[srcLayer] + layerTop[Math.Min(srcLayer + 1, layers.Count - 1)]) / 2;
                            if (g1 > routeStartY + 2) gapY1 = g1;
                            double g2 = (layerBottom[Math.Max(tgtLayer - 1, 0)] + layerTop[tgtLayer]) / 2;
                            if (g2 < ty - 2) gapY2 = g2;
                        }

                        int ScoreJog(double jx)
                        {
                            int h = 0;
                            if (SegmentHitsNode(routeStartX, routeStartY, routeStartX, gapY1, nodeBoxes, fromId, toId, margin)) h++;
                            if (SegmentHitsNode(routeStartX, gapY1, jx, gapY1, nodeBoxes, fromId, toId, margin)) h++;
                            if (SegmentHitsNode(jx, gapY1, jx, gapY2, nodeBoxes, fromId, toId, margin)) h++;
                            if (SegmentHitsNode(jx, gapY2, tx, gapY2, nodeBoxes, fromId, toId, margin)) h++;
                            if (SegmentHitsNode(tx, gapY2, tx, ty, nodeBoxes, fromId, toId, margin)) h++;
                            return h;
                        }

                        int leftHits = (jogLeft > 0) ? ScoreJog(jogLeft) : int.MaxValue;
                        int rightHits = ScoreJog(jogRight);
                        double jogX = (leftHits <= rightHits) ? jogLeft : jogRight;

                        pts.Add((routeStartX, gapY1));
                        pts.Add((jogX, gapY1));
                        pts.Add((jogX, gapY2));
                        pts.Add((tx, gapY2));
                        pts.Add((tx, ty));
                    }
                    else
                    {
                        // Strategy 3b: Corridor routing for different-column edges.
                        double gapY1 = routeStartY + BusOffset;
                        double gapY2 = ty - BusOffset;
                        if (srcLayer >= 0 && tgtLayer >= 0 && tgtLayer > srcLayer)
                        {
                            double g1 = (layerBottom[srcLayer] + layerTop[Math.Min(srcLayer + 1, layers.Count - 1)]) / 2;
                            if (g1 > routeStartY + 2) gapY1 = g1;
                            double g2 = (layerBottom[Math.Max(tgtLayer - 1, 0)] + layerTop[tgtLayer]) / 2;
                            if (g2 < ty - 2) gapY2 = g2;
                        }

                        var occupied = new List<(double left, double right)>();
                        foreach (var (nl, nt, nr, nb, nid) in nodeBoxes)
                        {
                            if (nid == fromId || nid == toId) continue;
                            if (nb + margin >= gapY1 && nt - margin <= gapY2)
                                occupied.Add((nl - margin, nr + margin));
                        }
                        occupied.Sort((a, z) => a.left.CompareTo(z.left));

                        var merged = new List<(double left, double right)>();
                        foreach (var iv in occupied)
                        {
                            if (merged.Count > 0 && iv.left <= merged[^1].right)
                                merged[^1] = (merged[^1].left, Math.Max(merged[^1].right, iv.right));
                            else
                                merged.Add(iv);
                        }

                        var candidates = new List<double>();
                        if (merged.Count > 0 && merged[0].left > NodeGapX)
                            candidates.Add(merged[0].left - NodeGapX / 2);
                        for (int mi = 0; mi < merged.Count - 1; mi++)
                            candidates.Add((merged[mi].right + merged[mi + 1].left) / 2);
                        if (merged.Count > 0)
                            candidates.Add(merged[^1].right + NodeGapX / 2);
                        if (candidates.Count == 0)
                        {
                            candidates.Add(routeStartX);
                            candidates.Add(tx);
                        }

                        double midX = (routeStartX + tx) / 2;
                        double bestCX = candidates[0];
                        int bestCHits = int.MaxValue;

                        foreach (var cx in candidates)
                        {
                            if (cx < margin) continue;
                            int hits = 0;
                            if (SegmentHitsNode(routeStartX, routeStartY, routeStartX, gapY1,
                                nodeBoxes, fromId, toId, margin)) hits++;
                            if (SegmentHitsNode(routeStartX, gapY1, cx, gapY1,
                                nodeBoxes, fromId, toId, margin)) hits++;
                            if (SegmentHitsNode(cx, gapY1, cx, gapY2,
                                nodeBoxes, fromId, toId, margin)) hits++;
                            if (SegmentHitsNode(cx, gapY2, tx, gapY2,
                                nodeBoxes, fromId, toId, margin)) hits++;
                            if (SegmentHitsNode(tx, gapY2, tx, ty,
                                nodeBoxes, fromId, toId, margin)) hits++;

                            if (hits < bestCHits ||
                                (hits == bestCHits && Math.Abs(cx - midX) < Math.Abs(bestCX - midX)))
                            {
                                bestCHits = hits;
                                bestCX = cx;
                                if (hits == 0) break;
                            }
                        }

                        if (Math.Abs(bestCX - routeStartX) < SnapThresh)
                        {
                            pts.Add((routeStartX, gapY2));
                            pts.Add((tx, gapY2));
                            pts.Add((tx, ty));
                        }
                        else if (Math.Abs(bestCX - tx) < SnapThresh)
                        {
                            pts.Add((routeStartX, gapY1));
                            pts.Add((tx, gapY1));
                            pts.Add((tx, ty));
                        }
                        else
                        {
                            pts.Add((routeStartX, gapY1));
                            pts.Add((bestCX, gapY1));
                            pts.Add((bestCX, gapY2));
                            pts.Add((tx, gapY2));
                            pts.Add((tx, ty));
                        }
                    }
                }
            }

            // Filter out collinear waypoints (e.g. bus segment on same vertical line)
            // to prevent short segments from over-clamping bend radii
            var filtered = new List<(double x, double y)> { pts[0] };
            for (int fi = 1; fi < pts.Count - 1; fi++)
            {
                var prev = filtered[^1];
                var curr = pts[fi];
                var next = pts[fi + 1];
                if (!IsCollinear(prev, curr, next))
                    filtered.Add(curr);
            }
            filtered.Add(pts[^1]);

            edgeRoutes.Add((fromId, toId, filtered));
        }

        // ── Pass 2: Detect visual junctions ──
        // A bend is a junction (large radius) if:
        //  (a) Multiple edges share the same bend point with different in/out directions
        //  (c) Overlapping horizontal segments from different sources — SOURCE-SIDE bends only
        //  (d) Multi-child divergence: straight + non-straight children → first bend
        //  (e) Multi-parent convergence: straight + non-straight incoming → last bend
        var bendMap = new Dictionary<(int rx, int ry),
            List<(int edgeIdx, int bendIdx, int outDx, int outDy, int inDx, int inDy)>>();

        for (int ei = 0; ei < edgeRoutes.Count; ei++)
        {
            var rPts = edgeRoutes[ei].pts;
            for (int bi = 1; bi < rPts.Count - 1; bi++)
            {
                var key = ((int)Math.Round(rPts[bi].x), (int)Math.Round(rPts[bi].y));
                int outDx = Math.Sign(rPts[bi + 1].x - rPts[bi].x);
                int outDy = Math.Sign(rPts[bi + 1].y - rPts[bi].y);
                int inDx = Math.Sign(rPts[bi].x - rPts[bi - 1].x);
                int inDy = Math.Sign(rPts[bi].y - rPts[bi - 1].y);

                if (!bendMap.ContainsKey(key)) bendMap[key] = new();
                bendMap[key].Add((ei, bi, outDx, outDy, inDx, inDy));
            }
        }

        var junctionBends = new HashSet<(int edgeIdx, int bendIdx)>();

        // (a) Shared bend points with different directions
        foreach (var (_, bends) in bendMap)
        {
            if (bends.Count < 2) continue;
            bool hasDivergence = bends.Select(b => (b.outDx, b.outDy)).Distinct().Count() > 1;
            bool hasConvergence = bends.Select(b => (b.inDx, b.inDy)).Distinct().Count() > 1;
            if (hasDivergence || hasConvergence)
            {
                foreach (var b in bends)
                    junctionBends.Add((b.edgeIdx, b.bendIdx));
            }
        }

        // (c) Overlapping horizontal segments from different sources → SOURCE-SIDE bends only
        // Source-side = where edge enters horizontal from vertical (prev waypoint has same X)
        var hSegs = new List<(int edgeIdx, double y, double xMin, double xMax, int bendL, int bendR)>();
        for (int ei = 0; ei < edgeRoutes.Count; ei++)
        {
            var rPts = edgeRoutes[ei].pts;
            for (int si = 0; si < rPts.Count - 1; si++)
            {
                if (Math.Abs(rPts[si].y - rPts[si + 1].y) < 0.1)
                {
                    hSegs.Add((ei, rPts[si].y,
                        Math.Min(rPts[si].x, rPts[si + 1].x),
                        Math.Max(rPts[si].x, rPts[si + 1].x),
                        si, si + 1));
                }
            }
        }

        foreach (var yGroup in hSegs.GroupBy(s => (int)Math.Round(s.y)))
        {
            var segs = yGroup.ToList();
            if (segs.Count < 2) continue;
            for (int i = 0; i < segs.Count; i++)
            {
                for (int j = i + 1; j < segs.Count; j++)
                {
                    if (edgeRoutes[segs[i].edgeIdx].from == edgeRoutes[segs[j].edgeIdx].from)
                        continue;
                    if (segs[i].xMax <= segs[j].xMin + 1 || segs[j].xMax <= segs[i].xMin + 1)
                        continue;
                    // Overlapping → mark only source-side bends (entering horizontal from vertical)
                    void MarkSourceSideBend(int edgeIdx, int bendIdx)
                    {
                        var ep = edgeRoutes[edgeIdx].pts;
                        if (bendIdx <= 0 || bendIdx >= ep.Count - 1) return;
                        // Source-side: previous waypoint has same X (came from vertical segment)
                        if (Math.Abs(ep[bendIdx - 1].x - ep[bendIdx].x) < 1)
                            junctionBends.Add((edgeIdx, bendIdx));
                    }
                    MarkSourceSideBend(segs[i].edgeIdx, segs[i].bendL);
                    MarkSourceSideBend(segs[i].edgeIdx, segs[i].bendR);
                    MarkSourceSideBend(segs[j].edgeIdx, segs[j].bendL);
                    MarkSourceSideBend(segs[j].edgeIdx, segs[j].bendR);
                }
            }
        }

        // (d) Multi-child divergence: if source has both straight and non-straight edges,
        //     or non-straight edges with different first-bend directions → first bend is divergence
        var edgesBySource = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int ei = 0; ei < edgeRoutes.Count; ei++)
        {
            var src = edgeRoutes[ei].from;
            if (!edgesBySource.ContainsKey(src)) edgesBySource[src] = new();
            edgesBySource[src].Add(ei);
        }

        foreach (var (_, siblings) in edgesBySource)
        {
            if (siblings.Count < 2) continue;

            bool hasStraight = siblings.Any(ei => edgeRoutes[ei].pts.Count <= 2);
            var nonStraight = siblings.Where(ei => edgeRoutes[ei].pts.Count > 2).ToList();

            bool isDivergence = false;
            if (hasStraight && nonStraight.Count > 0)
            {
                // One child goes straight, another turns → bus split divergence
                isDivergence = true;
            }
            else if (nonStraight.Count >= 2)
            {
                // Multiple non-straight: check if first bends differ (different coords or directions)
                var firstBendSigs = nonStraight.Select(ei =>
                {
                    var p = edgeRoutes[ei].pts;
                    return ((int)Math.Round(p[1].x), (int)Math.Round(p[1].y),
                            Math.Sign(p[2].x - p[1].x), Math.Sign(p[2].y - p[1].y));
                }).Distinct().Count();
                if (firstBendSigs > 1) isDivergence = true;
            }

            if (isDivergence)
            {
                foreach (var ei in nonStraight)
                    junctionBends.Add((ei, 1));
            }
        }

        // (e) Multi-parent convergence: target with both straight and non-straight incoming edges,
        //     or non-straight with different last-bend incoming directions → last bend is convergence
        var edgesByTarget = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int ei = 0; ei < edgeRoutes.Count; ei++)
        {
            var tgt = edgeRoutes[ei].to;
            if (!edgesByTarget.ContainsKey(tgt)) edgesByTarget[tgt] = new();
            edgesByTarget[tgt].Add(ei);
        }

        foreach (var (_, siblings) in edgesByTarget)
        {
            if (siblings.Count < 2) continue;

            bool hasStraightIn = siblings.Any(ei => edgeRoutes[ei].pts.Count <= 2);
            var nonStraightIn = siblings.Where(ei => edgeRoutes[ei].pts.Count > 2).ToList();

            bool isConvergence = false;
            if (hasStraightIn && nonStraightIn.Count > 0)
            {
                // One arrives straight, another bends → visible convergence
                isConvergence = true;
            }
            else if (nonStraightIn.Count >= 2)
            {
                // Multiple non-straight: convergence if last bends differ in direction OR position
                // (different horizontal bus heights → different last-bend Y, even if same direction)
                var lastBendInDirs = nonStraightIn.Select(ei =>
                {
                    var p = edgeRoutes[ei].pts;
                    int lb = p.Count - 2;
                    return (Math.Sign(p[lb].x - p[lb - 1].x), Math.Sign(p[lb].y - p[lb - 1].y));
                }).Distinct().Count();
                if (lastBendInDirs > 1) isConvergence = true;

                if (!isConvergence)
                {
                    // Check if last bends are at different coordinates (different bus heights)
                    var lastBendCoords = nonStraightIn.Select(ei =>
                    {
                        var p = edgeRoutes[ei].pts;
                        int lb = p.Count - 2;
                        return ((int)Math.Round(p[lb].x), (int)Math.Round(p[lb].y));
                    }).Distinct().Count();
                    if (lastBendCoords > 1) isConvergence = true;
                }
            }

            if (isConvergence)
            {
                // Compute distance from each edge's last bend to the target
                var bendDistances = nonStraightIn.Select(ei =>
                {
                    var p = edgeRoutes[ei].pts;
                    int lb = p.Count - 2;
                    return (ei, dist: Math.Abs(p[lb + 1].y - p[lb].y) + Math.Abs(p[lb + 1].x - p[lb].x));
                }).ToList();

                double minDist = bendDistances.Min(b => b.dist);

                // Only mark edges whose last bend is close to the target.
                // Edges with bends far from the target are "from above" and don't create
                // a visible junction at their bend point.
                foreach (var (ei, dist) in bendDistances)
                {
                    if (dist <= Math.Max(minDist * 5, 200))
                    {
                        junctionBends.Add((ei, edgeRoutes[ei].pts.Count - 2));
                    }
                }
            }
        }

        // (f) Shared vertical corridor: when two edges from different sources share an
        //     overlapping vertical segment at the same X:
        //     - JOINER (higher yMin): entry bend marked (joins corridor from above)
        //     - LEAVER (lower yMax): exit bend marked (splits off corridor before other ends)
        //     Skips identical segments (overlapping same-path edges).
        var vSegs = new List<(int edgeIdx, double x, double yMin, double yMax, int bendTop, int bendBot)>();
        for (int ei = 0; ei < edgeRoutes.Count; ei++)
        {
            var rPts = edgeRoutes[ei].pts;
            for (int si = 0; si < rPts.Count - 1; si++)
            {
                if (Math.Abs(rPts[si].x - rPts[si + 1].x) < 0.1) // vertical segment
                {
                    int topIdx = rPts[si].y < rPts[si + 1].y ? si : si + 1;
                    int botIdx = rPts[si].y < rPts[si + 1].y ? si + 1 : si;
                    vSegs.Add((ei, rPts[si].x,
                        Math.Min(rPts[si].y, rPts[si + 1].y),
                        Math.Max(rPts[si].y, rPts[si + 1].y),
                        topIdx, botIdx));
                }
            }
        }

        foreach (var xGroup in vSegs.GroupBy(s => (int)Math.Round(s.x)))
        {
            var segs = xGroup.ToList();
            if (segs.Count < 2) continue;
            for (int i = 0; i < segs.Count; i++)
            {
                for (int j = i + 1; j < segs.Count; j++)
                {
                    if (edgeRoutes[segs[i].edgeIdx].from == edgeRoutes[segs[j].edgeIdx].from)
                        continue;
                    // Skip identical segments (same-path overlapping edges)
                    if (Math.Abs(segs[i].yMin - segs[j].yMin) < 1 &&
                        Math.Abs(segs[i].yMax - segs[j].yMax) < 1)
                        continue;
                    // Check vertical overlap
                    if (segs[i].yMax <= segs[j].yMin + 1 || segs[j].yMax <= segs[i].yMin + 1)
                        continue;

                    // JOINER: edge entering the corridor later (higher yMin)
                    var joiner = segs[i].yMin > segs[j].yMin ? segs[i] : segs[j];
                    var epJ = edgeRoutes[joiner.edgeIdx].pts;
                    if (epJ.Count > 2 && joiner.bendTop > 0 && joiner.bendTop < epJ.Count - 1)
                    {
                        if (Math.Abs(epJ[joiner.bendTop - 1].y - epJ[joiner.bendTop].y) < 1)
                            junctionBends.Add((joiner.edgeIdx, joiner.bendTop));
                    }

                    // LEAVER: edge exiting the corridor earlier (lower yMax)
                    var leaver = segs[i].yMax < segs[j].yMax ? segs[i] : segs[j];
                    var epL = edgeRoutes[leaver.edgeIdx].pts;
                    if (epL.Count > 2 && leaver.bendBot > 0 && leaver.bendBot < epL.Count - 1)
                    {
                        // Verify it's an exit to horizontal (next Y matches bend Y)
                        if (Math.Abs(epL[leaver.bendBot + 1].y - epL[leaver.bendBot].y) < 1)
                            junctionBends.Add((leaver.edgeIdx, leaver.bendBot));
                    }
                }
            }
        }

        // ── Pass 3: Render edges with per-bend junction radii ──
        for (int ei = 0; ei < edgeRoutes.Count; ei++)
        {
            var (fromId, toId, ePts) = edgeRoutes[ei];

            HashSet<int>? largeBends = null;
            for (int bi = 1; bi < ePts.Count - 1; bi++)
            {
                if (junctionBends.Contains((ei, bi)))
                {
                    largeBends ??= new HashSet<int>();
                    largeBends.Add(bi);
                }
            }

            if (largeBends != null)
                AppLogger.Info($"[FLOWCHART-V3] Edge {fromId}→{toId}: junction bends at [{string.Join(",", largeBends)}], pts={ePts.Count}");

            sb.AppendLine($"    <path class=\"edge-path\" d=\"{BuildRoundedPath(ePts, BendRadius, BendRadiusLarge, largeBends, ci)}\" marker-end=\"url(#arrow)\" />");
        }
    }

    /// <summary>
    /// Checks if three waypoints are collinear (all on same X or same Y in grid-based routing).
    /// </summary>
    private static bool IsCollinear((double x, double y) a, (double x, double y) b, (double x, double y) c)
    {
        return (Math.Abs(a.x - b.x) < 0.1 && Math.Abs(b.x - c.x) < 0.1) ||
               (Math.Abs(a.y - b.y) < 0.1 && Math.Abs(b.y - c.y) < 0.1);
    }

    /// <summary>
    /// Checks if a horizontal or vertical line segment intersects any node bounding box.
    /// </summary>
    private static bool SegmentHitsNode(
        double x1, double y1, double x2, double y2,
        List<(double left, double top, double right, double bottom, string id)> nodeBoxes,
        string sourceId, string targetId, double margin)
    {
        double minX = Math.Min(x1, x2);
        double maxX = Math.Max(x1, x2);
        double minY = Math.Min(y1, y2);
        double maxY = Math.Max(y1, y2);

        foreach (var (l, t, r, b, id) in nodeBoxes)
        {
            if (id == sourceId || id == targetId) continue;
            if (maxX >= l - margin && minX <= r + margin &&
                maxY >= t - margin && minY <= b + margin)
                return true;
        }
        return false;
    }

    private static string Fmt(double v, CultureInfo ci) => v.ToString(ci);

    /// <summary>
    /// Builds an SVG path string from waypoints, rounding each bend with a quadratic Bezier.
    /// Per-bend radius: bends listed in largeBendIndices use largeRadius, others use defaultRadius.
    /// </summary>
    private static string BuildRoundedPath(
        List<(double x, double y)> pts, double defaultRadius, double largeRadius,
        HashSet<int>? largeBendIndices, CultureInfo ci)
    {
        if (pts.Count < 2)
            return "";

        var sb = new StringBuilder();
        sb.Append($"M{pts[0].x.ToString(ci)},{pts[0].y.ToString(ci)}");

        if (pts.Count == 2)
        {
            sb.Append($" L{pts[1].x.ToString(ci)},{pts[1].y.ToString(ci)}");
            return sb.ToString();
        }

        for (int i = 1; i < pts.Count - 1; i++)
        {
            var prev = pts[i - 1];
            var curr = pts[i];
            var next = pts[i + 1];

            // Lengths of incoming and outgoing segments
            double lenIn = Math.Sqrt((curr.x - prev.x) * (curr.x - prev.x) + (curr.y - prev.y) * (curr.y - prev.y));
            double lenOut = Math.Sqrt((next.x - curr.x) * (next.x - curr.x) + (next.y - curr.y) * (next.y - curr.y));

            // Select radius for this bend point (fixed, no clamping)
            double r = (largeBendIndices?.Contains(i) == true) ? largeRadius : defaultRadius;
            if (r < 0.5 || Math.Min(lenIn, lenOut) < 1)
            {
                // Degenerate — just line to corner
                sb.Append($" L{curr.x.ToString(ci)},{curr.y.ToString(ci)}");
                continue;
            }

            // Point on incoming segment, r before the corner
            double ax = curr.x + (prev.x - curr.x) / lenIn * r;
            double ay = curr.y + (prev.y - curr.y) / lenIn * r;

            // Point on outgoing segment, r after the corner
            double bx = curr.x + (next.x - curr.x) / lenOut * r;
            double by = curr.y + (next.y - curr.y) / lenOut * r;

            // Line to start of curve, then quadratic Bezier through the corner point
            sb.Append($" L{ax.ToString(ci)},{ay.ToString(ci)}");
            sb.Append($" Q{curr.x.ToString(ci)},{curr.y.ToString(ci)} {bx.ToString(ci)},{by.ToString(ci)}");
        }

        // Final line to last point
        var last = pts[pts.Count - 1];
        sb.Append($" L{last.x.ToString(ci)},{last.y.ToString(ci)}");

        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string Esc(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    /// <summary>Builds a Fandom wiki URL from a page title (spaces → underscores, URI-safe).</summary>
    private static string WikiUrl(string pageTitle)
        => $"https://merge-mansion.fandom.com/wiki/{Uri.EscapeDataString(pageTitle.Replace(' ', '_'))}";

    private static string TruncateText(string text, double maxWidth, double fontSize, bool bold)
    {
        double charW = fontSize * (bold ? 0.48 : 0.52);
        int maxChars = (int)(maxWidth / charW);
        if (text.Length <= maxChars) return text;

        // Preserve level suffix like " [L5]" when truncating item names
        if (!bold)
        {
            int lvlIdx = text.LastIndexOf(" [L");
            if (lvlIdx > 0)
            {
                string suffix = text[lvlIdx..];
                int nameMax = maxChars - suffix.Length;
                if (nameMax > 3)
                    return text[..(nameMax - 2)] + ".." + suffix;
            }
        }

        return maxChars > 3 ? text[..(maxChars - 3)] + "..." : text[..maxChars];
    }

    // ── Interactive HTML wrapper (Discord) ────────────────────────────

    /// <summary>
    /// Wraps SVG content in an interactive HTML document with click-to-complete
    /// task tracking, progress bar, and localStorage persistence.
    /// </summary>
    private static string WrapAsInteractiveHtml(string svgContent, string areaName, int totalNodes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>{Esc(areaName)} — Task Flowchart</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("  * { box-sizing: border-box; }");
        sb.AppendLine("  html, body { margin: 0; padding: 0; height: 100%; overflow: hidden; background: #FBF4E8; font-family: 'Tisa Sans Pro', Georgia, serif; }");
        sb.AppendLine("  #app { position: relative; width: 100%; height: 100%; }");
        sb.AppendLine("  #scroll-container { width: 100%; height: 100%; overflow: auto; scroll-behavior: smooth; -webkit-overflow-scrolling: touch; }");
        sb.AppendLine("  #top-bar { position: fixed; top: 0; left: 0; right: 0; z-index: 200; pointer-events: none; }");
        sb.AppendLine("  #progress-bar { height: 4px; background: rgba(74,53,32,0.08); }");
        sb.AppendLine("  #progress-fill { height: 100%; width: 0%; background: linear-gradient(90deg, #6BBF4A, #4CAF50, #43A047); transition: width 0.4s ease; border-radius: 0 2px 2px 0; }");
        sb.AppendLine("  #top-controls { display: flex; justify-content: flex-end; align-items: center; gap: 8px; padding: 4px 14px 0 0; }");
        sb.AppendLine("  #progress-text { font-size: 13px; color: #6B5B3E; background: rgba(251,244,232,0.85); padding: 4px 12px; border-radius: 8px; box-shadow: 0 1px 6px rgba(74,53,32,0.15); user-select: none; pointer-events: auto; }");
        sb.AppendLine("  #btn-reset { font-size: 11px; color: #8B6B4E; background: rgba(251,244,232,0.85); padding: 4px 10px; border-radius: 6px; border: 1px solid #D4C0A0; cursor: pointer; box-shadow: 0 1px 4px rgba(74,53,32,0.12); pointer-events: auto; }");
        sb.AppendLine("  #btn-reset:hover { background: #FFF0D8; color: #6B4B2E; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div id=\"top-bar\">");
        sb.AppendLine("  <div id=\"progress-bar\"><div id=\"progress-fill\"></div></div>");
        sb.AppendLine("  <div id=\"top-controls\">");
        sb.AppendLine("    <button id=\"btn-reset\" title=\"Reset all progress\">Reset</button>");
        sb.AppendLine("    <div id=\"progress-text\">0 / 0</div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div id=\"app\">");
        sb.AppendLine("<div id=\"scroll-container\">");
        sb.Append(svgContent);
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
        sb.AppendLine();
        sb.AppendLine("<script>");
        sb.AppendLine("(function() {");
        sb.AppendLine($"  var KEY = 'flowchart-{EscJs(areaName)}';");
        sb.AppendLine($"  var total = {totalNodes};");
        sb.AppendLine("  var done = {};");
        sb.AppendLine("  try { var s = localStorage.getItem(KEY); if (s) done = JSON.parse(s); } catch(e) {}");
        sb.AppendLine();
        sb.AppendLine("  function save() { try { localStorage.setItem(KEY, JSON.stringify(done)); } catch(e) {} }");
        sb.AppendLine();
        sb.AppendLine("  // Build parent and children lookup maps");
        sb.AppendLine("  var parentMap = {};");
        sb.AppendLine("  var childMap = {};");
        sb.AppendLine("  document.querySelectorAll('[data-node-idx]').forEach(function(g) {");
        sb.AppendLine("    var id = g.getAttribute('data-node-idx');");
        sb.AppendLine("    var p = g.getAttribute('data-parents');");
        sb.AppendLine("    parentMap[id] = p ? p.split(',') : [];");
        sb.AppendLine("    childMap[id] = childMap[id] || [];");
        sb.AppendLine("    (parentMap[id]).forEach(function(pid) {");
        sb.AppendLine("      childMap[pid] = childMap[pid] || [];");
        sb.AppendLine("      childMap[pid].push(id);");
        sb.AppendLine("    });");
        sb.AppendLine("  });");
        sb.AppendLine();
        sb.AppendLine("  function completeWithParents(id) {");
        sb.AppendLine("    if (done[id]) return;");
        sb.AppendLine("    done[id] = 1;");
        sb.AppendLine("    (parentMap[id] || []).forEach(function(pid) { completeWithParents(pid); });");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  function uncompleteWithChildren(id) {");
        sb.AppendLine("    if (!done[id]) return;");
        sb.AppendLine("    delete done[id];");
        sb.AppendLine("    (childMap[id] || []).forEach(function(cid) { uncompleteWithChildren(cid); });");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  var prevDone = {};");
        sb.AppendLine("  function refresh() {");
        sb.AppendLine("    var n = 0;");
        sb.AppendLine("    document.querySelectorAll('[data-node-idx]').forEach(function(g) {");
        sb.AppendLine("      var id = g.getAttribute('data-node-idx');");
        sb.AppendLine("      if (done[id]) {");
        sb.AppendLine("        if (!prevDone[id]) { g.classList.remove('completed'); void g.offsetWidth; }");
        sb.AppendLine("        g.classList.add('completed'); n++;");
        sb.AppendLine("      } else { g.classList.remove('completed'); }");
        sb.AppendLine("    });");
        sb.AppendLine("    prevDone = {}; for (var k in done) prevDone[k] = 1;");
        sb.AppendLine("    var pct = total > 0 ? (n / total * 100) : 0;");
        sb.AppendLine("    document.getElementById('progress-fill').style.width = pct + '%';");
        sb.AppendLine("    document.getElementById('progress-text').textContent = n + ' / ' + total + ' (' + Math.round(pct) + '%)';");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  document.querySelectorAll('[data-node-idx]').forEach(function(g) {");
        sb.AppendLine("    g.addEventListener('click', function(e) {");
        sb.AppendLine("      if (e.target.closest('a')) return;");
        sb.AppendLine("      var id = this.getAttribute('data-node-idx');");
        sb.AppendLine("      if (done[id]) { uncompleteWithChildren(id); }");
        sb.AppendLine("      else { completeWithParents(id); }");
        sb.AppendLine("      save(); refresh();");
        sb.AppendLine("    });");
        sb.AppendLine("  });");
        sb.AppendLine();
        sb.AppendLine("  document.getElementById('btn-reset').addEventListener('click', function() {");
        sb.AppendLine("    if (!confirm('Reset all progress for this area?')) return;");
        sb.AppendLine("    done = {};");
        sb.AppendLine("    save(); refresh();");
        sb.AppendLine("  });");
        sb.AppendLine();
        sb.AppendLine("  refresh();");
        sb.AppendLine();
        sb.AppendLine("  // Anchor highlight: glow animation when navigating to a node via link");
        sb.AppendLine("  function highlightAnchor() {");
        sb.AppendLine("    var h = location.hash;");
        sb.AppendLine("    if (!h || !h.startsWith('#node-')) return;");
        sb.AppendLine("    var idx = h.substring(6);");
        sb.AppendLine("    var g = document.getElementById('ng-' + idx);");
        sb.AppendLine("    if (!g) return;");
        sb.AppendLine("    // Scroll node into center of viewport (works for horizontal + vertical)");
        sb.AppendLine("    var sc = document.getElementById('scroll-container');");
        sb.AppendLine("    var rect = g.getBoundingClientRect();");
        sb.AppendLine("    var scRect = sc.getBoundingClientRect();");
        sb.AppendLine("    var cx = rect.left - scRect.left + sc.scrollLeft + rect.width / 2 - sc.clientWidth / 2;");
        sb.AppendLine("    var cy2 = rect.top - scRect.top + sc.scrollTop + rect.height / 2 - sc.clientHeight / 2;");
        sb.AppendLine("    sc.scrollTo({ left: Math.max(0, cx), top: Math.max(0, cy2), behavior: 'smooth' });");
        sb.AppendLine("    document.querySelectorAll('.anchor-highlight').forEach(function(el) { el.classList.remove('anchor-highlight'); });");
        sb.AppendLine("    void g.offsetWidth;");
        sb.AppendLine("    g.classList.add('anchor-highlight');");
        sb.AppendLine("    setTimeout(function() { g.classList.remove('anchor-highlight'); }, 2200);");
        sb.AppendLine("  }");
        sb.AppendLine("  window.addEventListener('hashchange', highlightAnchor);");
        sb.AppendLine("  highlightAnchor();");
        sb.AppendLine("})();");
        sb.AppendLine("</script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string EscJs(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string ItemIconDefId(string itemType) =>
        $"item-{itemType.Replace("_", "-").ToLowerInvariant()}";
}
