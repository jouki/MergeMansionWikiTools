using System.Globalization;
using System.Text;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Builds chain-level relationship graphs for seasonal events and renders as SVG.
/// Nodes = chains. Edges = spawn/drop/decay/sink. Sugiyama layout with rounded edge routing.
/// </summary>
internal static class EventChainFlowchartService
{
    // ── Constants ─────────────────────────────────────────────────

    const double PadX = 10;
    const double PadY = 8;
    const double NodeGapX = 45;
    const double LayerGap = 70;
    const double MaxNodeW = 220;
    const double MinNodeW = 120;
    const double HeaderH = 28;
    const double SubH = 18;
    const double ArrowSize = 6;
    const double BusOffset = 18;
    const double BendRadius = 8;
    const double BendRadiusLarge = 13;
    const double SnapThresh = 4;
    const double FontSzTitle = 13;
    const double FontSzSub = 10.5;

    static readonly Dictionary<ChainEdgeType, (string color, string label)> EdgeStyles = new()
    {
        [ChainEdgeType.SpawnDrop]  = ("#5A4A3A", "Spawn / Drop"),
        [ChainEdgeType.Decay]     = ("#8B5CF6", "Decay"),
        [ChainEdgeType.SinkInput] = ("#DC2626", "Sink Input"),
        [ChainEdgeType.SinkOutput]= ("#16A34A", "Sink Output"),
    };

    // ── Internal model ───────────────────────────────────────────

    class N
    {
        public string Id = "";
        public string Title = "";
        public string Sub = "";
        public HashSet<string> Parents = new();
        public HashSet<string> Children = new();
        public int Layer, Order;
        public double X, Y, W, H;
        public bool IsDummy;
    }

    // ══════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ══════════════════════════════════════════════════════════════

    public static string GenerateSvg(List<ParsedChain> eventChains, DataService ds, string eventName)
    {
        var (nodes, edges) = BuildChainGraph(eventChains, ds);
        if (nodes.Count == 0)
            return EmptySvg("No chain relationships found.");

        // Filter out Entrance/Exit chains
        nodes = nodes.Where(n =>
            !n.ChainKey.Contains("Entrance", StringComparison.OrdinalIgnoreCase) &&
            !n.ChainKey.Contains("Exit", StringComparison.OrdinalIgnoreCase)).ToList();

        // Build internal nodes
        var graph = new Dictionary<string, N>();
        foreach (var n in nodes)
        {
            // Strip parenthetical from display name: "Chocolates (Sweet Mess Express)" → "Chocolates"
            var title = n.DisplayName;
            int parenIdx = title.IndexOf(" (");
            if (parenIdx > 0) title = title[..parenIdx];

            double titleW = title.Length * 7.5 + PadX * 2;
            double w = Math.Clamp(titleW, MinNodeW, MaxNodeW);
            graph[n.ChainKey] = new N
            {
                Id = n.ChainKey,
                Title = title,
                Sub = $"{n.ItemCount} items",
                W = w, H = HeaderH + SubH + PadY,
            };
        }

        // Build adjacency (use ALL edge types for layout)
        foreach (var e in edges)
        {
            if (!graph.ContainsKey(e.SourceChainKey) || !graph.ContainsKey(e.TargetChainKey)) continue;
            graph[e.SourceChainKey].Children.Add(e.TargetChainKey);
            graph[e.TargetChainKey].Parents.Add(e.SourceChainKey);
        }

        // Keep all nodes (including isolated — e.g. characters on board)

        // Layout
        var layers = SugiyamaLayout(graph);

        // Render
        return RenderSvg(graph, edges, layers, eventName);
    }

    static string EmptySvg(string msg) =>
        $"<svg xmlns='http://www.w3.org/2000/svg' width='500' height='80'><text x='20' y='45' fill='#888' font-family='Segoe UI' font-size='14'>{msg}</text></svg>";

    // ══════════════════════════════════════════════════════════════
    //  GRAPH BUILDER (same as before, unchanged)
    // ══════════════════════════════════════════════════════════════

    public static (List<ChainGraphNode> nodes, List<ChainGraphEdge> edges) BuildChainGraph(
        List<ParsedChain> eventChains, DataService ds)
    {
        // Pre-process: merge "Producing" chains into their parent chain.
        // E.g., "Candy Machine - Producing" (1 item) merges into "Candy Machine".
        // Producing chain's items are processed as if they belong to parent chain.
        var producingMerge = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // producing ConfigKey → parent ConfigKey
        var producingChains = new List<ParsedChain>(); // keep for edge processing
        foreach (var chain in eventChains)
        {
            if (!chain.DisplayName.Contains(" - Producing") && !chain.ConfigKey.Contains("Producing")) continue;
            var parentName = chain.DisplayName.Replace(" - Producing", "").Trim();
            var parent = eventChains.FirstOrDefault(c => c != chain &&
                (c.DisplayName.Equals(parentName, StringComparison.OrdinalIgnoreCase) ||
                 c.ConfigKey.EndsWith(chain.ConfigKey.Replace("Producing", "").TrimEnd('_'), StringComparison.OrdinalIgnoreCase)));
            if (parent != null)
            {
                producingMerge[chain.ConfigKey] = parent.ConfigKey;
                producingChains.Add(chain);
            }
        }
        // Remove producing from node list, but keep for edge processing
        eventChains = eventChains.Where(c => !producingMerge.ContainsKey(c.ConfigKey)).ToList();

        var eventChainKeys = eventChains.Select(c => c.ConfigKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var itemToChain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in eventChains)
            foreach (var item in chain.Items)
                itemToChain.TryAdd(item.ItemType, chain.ConfigKey);

        // Also map items from merged producing chains to their parent
        foreach (var (prodKey, parentKey) in producingMerge)
        {
            var prodChain = ds.Chains.FirstOrDefault(c => c.ConfigKey == prodKey);
            if (prodChain != null)
                foreach (var item in prodChain.Items)
                    itemToChain[item.ItemType] = parentKey;
        }

        var numericKeyToChain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in eventChains)
            foreach (var item in chain.Items)
                if (!string.IsNullOrEmpty(item.NumericConfigKey))
                    numericKeyToChain.TryAdd(item.NumericConfigKey, chain.ConfigKey);

        var nodeList = new List<ChainGraphNode>();
        var edgeMap = new Dictionary<string, ChainGraphEdge>();

        foreach (var chain in eventChains)
        {
            nodeList.Add(new ChainGraphNode
            {
                ChainKey = chain.ConfigKey,
                DisplayName = chain.DisplayName,
                ItemCount = chain.Items.Count,
                HasGenerators = chain.HasGenerators,
                HasSpawners = chain.HasSpawners,
                IsSinkChain = chain.Items.Any(i => i.IsSink),
            });

            foreach (var item in chain.Items)
            {
                string lvl = $"L{item.Level}";

                void AddEdge(string targetItemType, ChainEdgeType type)
                {
                    var ck = ResolveChainKey(targetItemType, itemToChain, ds);
                    if (ck != null && eventChainKeys.Contains(ck) && ck != chain.ConfigKey)
                        MergeEdge(edgeMap, chain.ConfigKey, ck, type, lvl);
                }

                // Spawn/Drop
                if (item.DropOdds != null) foreach (var k in item.DropOdds.Keys) AddEdge(k, ChainEdgeType.SpawnDrop);
                if (item.SpawnOdds != null) foreach (var k in item.SpawnOdds.Keys) AddEdge(k, ChainEdgeType.SpawnDrop);
                if (!string.IsNullOrEmpty(item.SpawnItemType)) AddEdge(item.SpawnItemType, ChainEdgeType.SpawnDrop);

                // Decay
                if (!string.IsNullOrEmpty(item.DecayIntoItemType)) AddEdge(item.DecayIntoItemType, ChainEdgeType.Decay);
                if (!string.IsNullOrEmpty(item.SpawnDecayIntoItemType)) AddEdge(item.SpawnDecayIntoItemType, ChainEdgeType.Decay);
                if (!string.IsNullOrEmpty(item.DecayAfterLastCycleItemType)) AddEdge(item.DecayAfterLastCycleItemType, ChainEdgeType.Decay);
                if (item.DecayAfterLastCycleOdds != null) foreach (var k in item.DecayAfterLastCycleOdds.Keys) AddEdge(k, ChainEdgeType.Decay);

                // Sink
                if (item.IsSink)
                {
                    if (item.SinkRequirementConfigKeys != null)
                        foreach (var reqKey in item.SinkRequirementConfigKeys)
                        {
                            string? src = eventChainKeys.Contains(reqKey) ? reqKey
                                : numericKeyToChain.TryGetValue(reqKey, out var nck) ? nck : null;
                            if (src != null && src != chain.ConfigKey)
                                MergeEdge(edgeMap, src, chain.ConfigKey, ChainEdgeType.SinkInput, lvl);
                        }

                    if (!string.IsNullOrEmpty(item.SinkRewardItemType))
                        AddEdge(item.SinkRewardItemType, ChainEdgeType.SinkOutput);
                }

                // Order: required items → this chain (input), this chain → reward items (output)
                if (item.IsOrder)
                {
                    if (item.OrderRequiredItems != null)
                        foreach (var k in item.OrderRequiredItems.Keys)
                        {
                            var reqCk = ResolveChainKey(k, itemToChain, ds);
                            if (reqCk != null && eventChainKeys.Contains(reqCk) && reqCk != chain.ConfigKey)
                                MergeEdge(edgeMap, reqCk, chain.ConfigKey, ChainEdgeType.SinkInput, lvl);
                        }
                    if (item.OrderRewardItems != null)
                        foreach (var k in item.OrderRewardItems.Keys)
                            AddEdge(k, ChainEdgeType.SinkOutput);
                }
            }
        }

        // Process producing chains — edges attributed to parent chain
        foreach (var prodChain in producingChains)
        {
            var parentKey = producingMerge[prodChain.ConfigKey];
            foreach (var item in prodChain.Items)
            {
                string lvl = $"L{item.Level}";

                void AddProdEdge(string targetItemType, ChainEdgeType type)
                {
                    var ck = ResolveChainKey(targetItemType, itemToChain, ds);
                    if (ck != null && eventChainKeys.Contains(ck) && ck != parentKey)
                        MergeEdge(edgeMap, parentKey, ck, type, lvl);
                }

                if (item.DropOdds != null) foreach (var k in item.DropOdds.Keys) AddProdEdge(k, ChainEdgeType.SpawnDrop);
                if (item.SpawnOdds != null) foreach (var k in item.SpawnOdds.Keys) AddProdEdge(k, ChainEdgeType.SpawnDrop);
                if (!string.IsNullOrEmpty(item.SpawnItemType)) AddProdEdge(item.SpawnItemType, ChainEdgeType.SpawnDrop);

                if (!string.IsNullOrEmpty(item.DecayIntoItemType)) AddProdEdge(item.DecayIntoItemType, ChainEdgeType.Decay);
                if (!string.IsNullOrEmpty(item.SpawnDecayIntoItemType)) AddProdEdge(item.SpawnDecayIntoItemType, ChainEdgeType.Decay);
                if (!string.IsNullOrEmpty(item.DecayAfterLastCycleItemType)) AddProdEdge(item.DecayAfterLastCycleItemType, ChainEdgeType.Decay);
                if (item.DecayAfterLastCycleOdds != null) foreach (var k in item.DecayAfterLastCycleOdds.Keys) AddProdEdge(k, ChainEdgeType.Decay);

                if (!string.IsNullOrEmpty(item.SinkRewardItemType)) AddProdEdge(item.SinkRewardItemType, ChainEdgeType.SinkOutput);
            }
        }

        return (nodeList, edgeMap.Values.ToList());
    }

    static string? ResolveChainKey(string itemType, Dictionary<string, string> itemToChain, DataService ds)
    {
        if (itemToChain.TryGetValue(itemType, out var ck)) return ck;
        var chainKey = DataService.GetChainKeyFromItemType(itemType);
        return ds.Chains.FirstOrDefault(c => c.ConfigKey.Equals(chainKey, StringComparison.OrdinalIgnoreCase))?.ConfigKey;
    }

    static void MergeEdge(Dictionary<string, ChainGraphEdge> map, string from, string to, ChainEdgeType type, string label)
    {
        var key = $"{from}→{to}→{type}";
        if (map.TryGetValue(key, out var e))
        { if (!e.Label.Contains(label)) e.Label += e.Label.Length > 0 ? $", {label}" : label; }
        else
            map[key] = new ChainGraphEdge { SourceChainKey = from, TargetChainKey = to, EdgeType = type, Label = label };
    }

    /// <summary>Compresses "L3, L4, L5, L7, L8" → "L3-L5, L7-L8"</summary>
    static string CompressLevelLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return label;
        var nums = new List<int>();
        foreach (var part in label.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("L") && int.TryParse(part[1..], out var n))
                nums.Add(n);
            else
                return label; // Non-standard label, return as-is
        }
        if (nums.Count <= 1) return label;

        nums.Sort();
        var ranges = new List<string>();
        int start = nums[0], end = nums[0];
        for (int i = 1; i < nums.Count; i++)
        {
            if (nums[i] == end + 1) { end = nums[i]; }
            else
            {
                ranges.Add(start == end ? $"L{start}" : $"L{start}-L{end}");
                start = end = nums[i];
            }
        }
        ranges.Add(start == end ? $"L{start}" : $"L{start}-L{end}");
        return string.Join(", ", ranges);
    }

    // ══════════════════════════════════════════════════════════════
    //  SUGIYAMA LAYOUT
    // ══════════════════════════════════════════════════════════════

    static List<List<string>> SugiyamaLayout(Dictionary<string, N> g)
    {
        AssignLayers(g);

        int maxLayer = g.Values.Max(n => n.Layer);
        var layers = Enumerable.Range(0, maxLayer + 1).Select(_ => new List<string>()).ToList();
        foreach (var n in g.Values) layers[n.Layer].Add(n.Id);

        InsertDummyNodes(g, layers);
        for (int i = 0; i < 12; i++) MinimizeCrossings(g, layers);
        AssignXPositions(g, layers);
        AssignYPositions(g, layers);
        CompactColumns(g);

        return layers;
    }

    static void AssignLayers(Dictionary<string, N> g)
    {
        // Longest-path
        var topo = TopologicalSort(g);
        foreach (var id in topo)
        {
            var n = g[id];
            int pMax = -1;
            foreach (var p in n.Parents)
                if (g.TryGetValue(p, out var pn)) pMax = Math.Max(pMax, pn.Layer);
            n.Layer = pMax + 1;
        }

        // Pull-down: move nodes closer to children
        foreach (var id in topo.AsEnumerable().Reverse())
        {
            var n = g[id];
            if (n.Children.Count == 0) continue;
            int minChild = int.MaxValue;
            foreach (var c in n.Children)
                if (g.TryGetValue(c, out var cn)) minChild = Math.Min(minChild, cn.Layer);
            if (minChild > n.Layer + 1)
            {
                int target = minChild - 1;
                // Don't push below any parent
                int maxParent = -1;
                foreach (var p in n.Parents)
                    if (g.TryGetValue(p, out var pn)) maxParent = Math.Max(maxParent, pn.Layer);
                if (target > maxParent) n.Layer = target;
            }
        }

        // Compact layer numbers
        var usedLayers = g.Values.Select(n => n.Layer).Distinct().OrderBy(l => l).ToList();
        var remap = new Dictionary<int, int>();
        for (int i = 0; i < usedLayers.Count; i++) remap[usedLayers[i]] = i;
        foreach (var n in g.Values) n.Layer = remap[n.Layer];
    }

    static List<string> TopologicalSort(Dictionary<string, N> g)
    {
        var inDeg = g.ToDictionary(kv => kv.Key, kv => kv.Value.Parents.Count(p => g.ContainsKey(p)));
        var queue = new Queue<string>(inDeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var result = new List<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            result.Add(id);
            foreach (var c in g[id].Children)
                if (g.ContainsKey(c) && --inDeg[c] == 0) queue.Enqueue(c);
        }
        // Handle cycles
        foreach (var id in g.Keys)
            if (!result.Contains(id)) result.Add(id);
        return result;
    }

    static void InsertDummyNodes(Dictionary<string, N> g, List<List<string>> layers)
    {
        int dc = 0;
        var edgesToProcess = new List<(string from, string to)>();
        foreach (var n in g.Values)
            foreach (var c in n.Children)
                if (g.ContainsKey(c)) edgesToProcess.Add((n.Id, c));

        foreach (var (fromId, toId) in edgesToProcess)
        {
            var fn = g[fromId]; var tn = g[toId];
            int span = tn.Layer - fn.Layer;
            if (span <= 1) continue;

            fn.Children.Remove(toId);
            tn.Parents.Remove(fromId);

            string prev = fromId;
            for (int li = fn.Layer + 1; li < tn.Layer; li++)
            {
                var did = $"__d_{dc++}";
                var dn = new N { Id = did, IsDummy = true, Layer = li, W = 0, H = 0 };
                g[did] = dn;
                layers[li].Add(did);

                g[prev].Children.Add(did);
                dn.Parents.Add(prev);
                prev = did;
            }
            g[prev].Children.Add(toId);
            tn.Parents.Add(prev);
        }
    }

    static void MinimizeCrossings(Dictionary<string, N> g, List<List<string>> layers)
    {
        // Barycenter: sweep down then up
        for (int li = 1; li < layers.Count; li++)
        {
            foreach (var id in layers[li])
            {
                var n = g[id];
                var pOrders = n.Parents.Where(p => g.ContainsKey(p)).Select(p => g[p].Order).ToList();
                if (pOrders.Count > 0) n.Order = (int)Math.Round(pOrders.Average());
            }
            layers[li] = layers[li].OrderBy(id => g[id].Order).ToList();
            for (int i = 0; i < layers[li].Count; i++) g[layers[li][i]].Order = i;
        }
        for (int li = layers.Count - 2; li >= 0; li--)
        {
            foreach (var id in layers[li])
            {
                var n = g[id];
                var cOrders = n.Children.Where(c => g.ContainsKey(c)).Select(c => g[c].Order).ToList();
                if (cOrders.Count > 0) n.Order = (int)Math.Round(cOrders.Average());
            }
            layers[li] = layers[li].OrderBy(id => g[id].Order).ToList();
            for (int i = 0; i < layers[li].Count; i++) g[layers[li][i]].Order = i;
        }
    }

    static void AssignXPositions(Dictionary<string, N> g, List<List<string>> layers)
    {
        // Initial left-to-right
        foreach (var layer in layers)
        {
            double x = 0;
            foreach (var id in layer)
            {
                var n = g[id];
                n.X = x;
                x += (n.IsDummy ? NodeGapX : n.W) + NodeGapX;
            }
        }

        // Center layers
        double maxW = layers.Max(l => l.Count > 0 ? l.Max(id => g[id].X + g[id].W) : 0);
        foreach (var layer in layers)
        {
            if (layer.Count == 0) continue;
            double lw = layer.Max(id => g[id].X + g[id].W);
            double off = (maxW - lw) / 2;
            foreach (var id in layer) g[id].X += off;
        }

        // Improve: median of parents/children (8 iterations)
        for (int iter = 0; iter < 8; iter++)
        {
            bool topDown = iter % 2 == 0;
            var order = topDown ? layers : layers.AsEnumerable().Reverse().ToList();
            foreach (var layer in order)
            {
                for (int i = 0; i < layer.Count; i++)
                {
                    var n = g[layer[i]];
                    if (n.IsDummy) continue;
                    var refs = (topDown ? n.Parents : n.Children)
                        .Where(r => g.ContainsKey(r))
                        .Select(r => g[r].X + g[r].W / 2)
                        .OrderBy(x => x).ToList();
                    if (refs.Count == 0) continue;

                    double median = refs[refs.Count / 2];
                    double desired = median - n.W / 2;

                    // Respect neighbors
                    double leftBound = i > 0 ? g[layer[i - 1]].X + g[layer[i - 1]].W + NodeGapX : double.MinValue;
                    double rightBound = i < layer.Count - 1 ? g[layer[i + 1]].X - n.W - NodeGapX : double.MaxValue;
                    n.X = Math.Clamp(desired, leftBound, rightBound);
                }
            }
        }
    }

    static void AssignYPositions(Dictionary<string, N> g, List<List<string>> layers)
    {
        double y = 0;
        foreach (var layer in layers)
        {
            double maxH = layer.Where(id => !g[id].IsDummy).Select(id => g[id].H).DefaultIfEmpty(SubH).Max();
            foreach (var id in layer)
            {
                var n = g[id];
                n.Y = n.IsDummy ? y + maxH / 2 : y + (maxH - n.H) / 2;
            }
            y += maxH + LayerGap;
        }
    }

    static void CompactColumns(Dictionary<string, N> g)
    {
        var realNodes = g.Values.Where(n => !n.IsDummy).ToList();
        if (realNodes.Count == 0) return;

        var cols = realNodes.GroupBy(n => Math.Round(n.X, 1)).OrderBy(c => c.Key).ToList();
        double yBuf = LayerGap / 2;

        var slots = new List<(double x, List<(double yMin, double yMax)> ranges)>();

        var xMap = new Dictionary<double, double>();

        foreach (var col in cols)
        {
            double yMin = col.Min(n => n.Y) - yBuf;
            double yMax = col.Max(n => n.Y + n.H) + yBuf;

            bool placed = false;
            foreach (var slot in slots)
            {
                if (!slot.ranges.Any(r => yMin < r.yMax && yMax > r.yMin))
                {
                    slot.ranges.Add((yMin, yMax));
                    xMap[col.Key] = slot.x;
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                double newX = slots.Count > 0 ? slots.Max(s => s.x) + MaxNodeW + NodeGapX : 0;
                slots.Add((newX, new List<(double, double)> { (yMin, yMax) }));
                xMap[col.Key] = newX;
            }
        }

        // Apply to real nodes
        foreach (var n in realNodes)
        {
            double oldX = Math.Round(n.X, 1);
            if (xMap.TryGetValue(oldX, out var newX))
                n.X = newX + (n.X - oldX);
        }

        // Update dummies
        foreach (var n in g.Values.Where(n => n.IsDummy))
        {
            var closest = xMap.OrderBy(kv => Math.Abs(kv.Key - n.X)).FirstOrDefault();
            n.X += closest.Value - closest.Key;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  SVG RENDERING
    // ══════════════════════════════════════════════════════════════

    static string RenderSvg(Dictionary<string, N> graph, List<ChainGraphEdge> edges,
        List<List<string>> layers, string eventName)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        // Bounds
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in graph.Values.Where(n => !n.IsDummy))
        {
            minX = Math.Min(minX, n.X); minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.W); maxY = Math.Max(maxY, n.Y + n.H);
        }

        double pad = 50;
        double legendH = 60;
        double titleH = 35;
        double svgW = maxX - minX + pad * 2;
        double svgH = maxY - minY + pad * 2 + titleH + legendH;
        double ox = -minX + pad;
        double oy = -minY + pad + titleH;

        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='{F(svgW)}' height='{F(svgH)}' viewBox='0 0 {F(svgW)} {F(svgH)}'>");

        // Styles
        sb.AppendLine("<style>");
        sb.AppendLine("text { font-family: 'Segoe UI', 'Helvetica Neue', sans-serif; }");
        sb.AppendLine($".node-body {{ fill: #FFF8EC; stroke: #C9A96E; stroke-width: 1.5; rx: 8; }}");
        sb.AppendLine($".node-header {{ fill: #4A7CBF; rx: 8; }}");
        sb.AppendLine($".node-header-btm {{ fill: #3A5F96; }}");
        sb.AppendLine($".node-title {{ fill: #EEF7FF; font-size: {F(FontSzTitle)}px; font-weight: 600; }}");
        sb.AppendLine($".node-sub {{ fill: #9B7B58; font-size: {F(FontSzSub)}px; }}");
        sb.AppendLine(".edge-label { font-size: 9.5px; font-weight: 600; }");
        sb.AppendLine(".title { fill: #C9A96E; font-size: 20px; font-weight: 700; }");
        sb.AppendLine(".legend-text { fill: #888; font-size: 11px; }");
        sb.AppendLine(".legend-label { fill: #AAA; font-size: 12px; font-weight: 600; }");
        sb.AppendLine("</style>");

        // Defs: arrow markers per edge type
        sb.AppendLine("<defs>");
        foreach (var (type, (color, _)) in EdgeStyles)
            sb.AppendLine($"<marker id='arr-{type}' markerWidth='{F(ArrowSize)}' markerHeight='{F(ArrowSize)}' refX='{F(ArrowSize)}' refY='{F(ArrowSize / 2)}' orient='auto'>" +
                $"<polygon points='0 0, {F(ArrowSize)} {F(ArrowSize / 2)}, 0 {F(ArrowSize)}' fill='{color}'/></marker>");
        sb.AppendLine("</defs>");

        // Title
        sb.AppendLine($"<text x='{F(svgW / 2)}' y='26' text-anchor='middle' class='title'>{Esc(eventName)}</text>");

        // ── Route and render edges ───────────────────────────────
        RouteAndRenderEdges(sb, graph, edges, ox, oy, ci);

        // ── Render nodes ─────────────────────────────────────────
        foreach (var n in graph.Values.Where(n => !n.IsDummy))
        {
            double nx = n.X + ox, ny = n.Y + oy;
            double hh = HeaderH;

            // Body
            sb.AppendLine($"<rect x='{F(nx)}' y='{F(ny)}' width='{F(n.W)}' height='{F(n.H)}' class='node-body'/>");

            // Header background (rounded top)
            sb.AppendLine($"<clipPath id='clip-{n.Id.GetHashCode():X}'><rect x='{F(nx)}' y='{F(ny)}' width='{F(n.W)}' height='{F(n.H)}' rx='8'/></clipPath>");
            sb.AppendLine($"<rect x='{F(nx)}' y='{F(ny)}' width='{F(n.W)}' height='{F(hh)}' fill='#4A7CBF' clip-path='url(#clip-{n.Id.GetHashCode():X})'/>");
            sb.AppendLine($"<rect x='{F(nx)}' y='{F(ny + hh - 4)}' width='{F(n.W)}' height='4' fill='#3A5F96'/>");

            // Title (centered in header)
            string title = n.Title.Length > 28 ? n.Title[..25] + "..." : n.Title;
            sb.AppendLine($"<text x='{F(nx + n.W / 2)}' y='{F(ny + hh / 2 + 4.5)}' text-anchor='middle' class='node-title'>{Esc(title)}</text>");

            // Subtitle (below header)
            sb.AppendLine($"<text x='{F(nx + PadX)}' y='{F(ny + hh + SubH - 4)}' class='node-sub'>{Esc(n.Sub)}</text>");
        }

        // ── Legend ────────────────────────────────────────────────
        double ly = svgH - legendH + 10;
        sb.AppendLine($"<text x='20' y='{F(ly)}' class='legend-label'>Legend:</text>");
        double lx = 80;
        foreach (var (type, (color, label)) in EdgeStyles)
        {
            sb.AppendLine($"<line x1='{F(lx)}' y1='{F(ly - 4)}' x2='{F(lx + 30)}' y2='{F(ly - 4)}' stroke='{color}' stroke-width='2'/>");
            sb.AppendLine($"<polygon points='{F(lx + 30)},{F(ly - 8)} {F(lx + 36)},{F(ly - 4)} {F(lx + 30)},{F(ly)}' fill='{color}'/>");
            sb.AppendLine($"<text x='{F(lx + 42)}' y='{F(ly)}' class='legend-text'>{Esc(label)}</text>");
            lx += 42 + label.Length * 6.5 + 20;
        }

        sb.AppendLine("</svg>");
        return sb.ToString();

        string F(double v) => v.ToString("F1", ci);
    }

    // ── Edge routing ─────────────────────────────────────────────

    static void RouteAndRenderEdges(StringBuilder sb, Dictionary<string, N> graph,
        List<ChainGraphEdge> edges, double ox, double oy, CultureInfo ci)
    {
        // Node bounding boxes for collision detection
        var boxes = graph.Values.Where(n => !n.IsDummy)
            .Select(n => (left: n.X + ox - 4, top: n.Y + oy - 4, right: n.X + ox + n.W + 4, bottom: n.Y + oy + n.H + 4, id: n.Id))
            .ToList();

        // Count edges per (type + target) for merging — only same-type same-dest may share
        var destTypeCount = new Dictionary<string, int>();
        var destTypeIndex = new Dictionary<string, int>();
        foreach (var e in edges)
        {
            var key = $"{e.EdgeType}→{e.TargetChainKey}";
            destTypeCount.TryAdd(key, 0);
            destTypeCount[key]++;
        }

        // Render edges by type
        foreach (var group in edges.GroupBy(e => e.EdgeType).OrderBy(g => g.Key))
        {
            var color = EdgeStyles[group.Key].color;
            var markerId = $"arr-{group.Key}";

            foreach (var edge in group)
            {
                if (!graph.TryGetValue(edge.SourceChainKey, out var srcN) ||
                    !graph.TryGetValue(edge.TargetChainKey, out var tgtN)) continue;

                // Offset parallel edges of same type to same target
                var dtKey = $"{edge.EdgeType}→{edge.TargetChainKey}";
                destTypeIndex.TryAdd(dtKey, 0);
                int idx = destTypeIndex[dtKey]++;
                int total = destTypeCount[dtKey];
                double mergeOff = total > 1 ? (idx - (total - 1) / 2.0) * 8 : 0;

                // Anchor points
                double sx, sy, tx, ty;
                bool isDecay = edge.EdgeType == ChainEdgeType.Decay;
                bool isSinkIn = edge.EdgeType == ChainEdgeType.SinkInput;

                if (isDecay)
                {
                    sx = srcN.X + ox + srcN.W;
                    sy = srcN.Y + oy + srcN.H * 0.7;
                    tx = tgtN.X + ox + tgtN.W;
                    ty = tgtN.Y + oy + tgtN.H * 0.3;
                }
                else if (isSinkIn)
                {
                    sx = srcN.X + ox + srcN.W / 2 + mergeOff;
                    sy = srcN.Y + oy + srcN.H;
                    tx = tgtN.X + ox;
                    ty = tgtN.Y + oy + tgtN.H * 0.5;
                }
                else
                {
                    sx = srcN.X + ox + srcN.W / 2 + mergeOff;
                    sy = srcN.Y + oy + srcN.H;
                    tx = tgtN.X + ox + tgtN.W / 2 + mergeOff;
                    ty = tgtN.Y + oy;
                }

                // Build waypoints with collision avoidance
                var pts = new List<(double x, double y)> { (sx, sy) };

                if (isDecay)
                {
                    double rightX = Math.Max(sx, tx) + 25;
                    pts.Add((rightX, sy));
                    pts.Add((rightX, ty));
                    pts.Add((tx, ty));
                }
                else if (Math.Abs(sx - tx) < SnapThresh)
                {
                    // Straight vertical — check collision
                    if (!SegmentHitsNode(sx, sy, sx, ty, boxes, srcN.Id, tgtN.Id))
                    {
                        pts.Add((sx, ty));
                    }
                    else
                    {
                        // Jog around: find clear corridor
                        double jogX = srcN.X + ox + srcN.W + NodeGapX / 2;
                        double midY = (sy + ty) / 2;
                        pts.Add((sx, midY));
                        pts.Add((jogX, midY));
                        pts.Add((jogX, ty + (ty - midY)));
                        pts.Add((tx, ty));
                    }
                }
                else
                {
                    // Z-route: down → across → down
                    double midY = (sy + ty) / 2;

                    // Check if horizontal segment hits a node
                    if (SegmentHitsNode(sx, midY, tx, midY, boxes, srcN.Id, tgtN.Id))
                    {
                        // Try routing at layer gap (slightly above or below mid)
                        double altY1 = sy + (ty - sy) * 0.3;
                        double altY2 = sy + (ty - sy) * 0.7;
                        if (!SegmentHitsNode(sx, altY1, tx, altY1, boxes, srcN.Id, tgtN.Id))
                            midY = altY1;
                        else if (!SegmentHitsNode(sx, altY2, tx, altY2, boxes, srcN.Id, tgtN.Id))
                            midY = altY2;
                        // else: keep midY as fallback
                    }

                    pts.Add((sx, midY));
                    pts.Add((tx, midY));
                    pts.Add((tx, ty));
                }

                var filtered = FilterCollinear(pts);
                var path = BuildRoundedPath(filtered, BendRadius, ci);
                sb.AppendLine($"<path d='{path}' stroke='{color}' stroke-width='1.8' fill='none' marker-end='url(#{markerId})'/>");

                // Label — offset perpendicular to first segment
                var edgeLabel = CompressLevelLabel(edge.Label);
                if (!string.IsNullOrEmpty(edgeLabel) && filtered.Count >= 2)
                {
                    double t = 0.25;
                    int seg = filtered.Count > 2 ? 1 : 0; // prefer second segment for Z-routes
                    double lx = filtered[seg].x + (filtered[seg + 1].x - filtered[seg].x) * t;
                    double ly = filtered[seg].y + (filtered[seg + 1].y - filtered[seg].y) * t;

                    double dx = filtered[seg + 1].x - filtered[seg].x;
                    double dy = filtered[seg + 1].y - filtered[seg].y;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len > 1)
                    {
                        double perpX = -dy / len * 12;
                        double perpY = dx / len * 12;
                        // Always offset to the left of the direction
                        lx += perpX;
                        ly += perpY;
                    }
                    else
                    {
                        ly -= 8;
                    }

                    sb.AppendLine($"<text x='{lx.ToString("F1", ci)}' y='{ly.ToString("F1", ci)}' text-anchor='middle' class='edge-label' fill='{color}'>{Esc(edgeLabel)}</text>");
                }
            }
        }
    }

    static bool SegmentHitsNode(double x1, double y1, double x2, double y2,
        List<(double left, double top, double right, double bottom, string id)> boxes,
        string srcId, string tgtId)
    {
        double minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2);
        double minY = Math.Min(y1, y2), maxY = Math.Max(y1, y2);
        foreach (var (l, t, r, b, id) in boxes)
        {
            if (id == srcId || id == tgtId) continue;
            if (maxX >= l && minX <= r && maxY >= t && minY <= b)
                return true;
        }
        return false;
    }

    static List<(double x, double y)> FilterCollinear(List<(double x, double y)> pts)
    {
        if (pts.Count <= 2) return pts;
        var result = new List<(double x, double y)> { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var (ax, ay) = result[^1];
            var (bx, by) = pts[i];
            var (cx, cy) = pts[i + 1];
            bool sameX = Math.Abs(ax - bx) < 0.5 && Math.Abs(bx - cx) < 0.5;
            bool sameY = Math.Abs(ay - by) < 0.5 && Math.Abs(by - cy) < 0.5;
            if (!sameX && !sameY) result.Add(pts[i]);
        }
        result.Add(pts[^1]);
        return result;
    }

    static string BuildRoundedPath(List<(double x, double y)> pts, double r, CultureInfo ci)
    {
        if (pts.Count < 2) return "";
        string F(double v) => v.ToString("F1", ci);
        var sb = new StringBuilder();
        sb.Append($"M{F(pts[0].x)},{F(pts[0].y)}");

        if (pts.Count == 2) { sb.Append($" L{F(pts[1].x)},{F(pts[1].y)}"); return sb.ToString(); }

        for (int i = 1; i < pts.Count - 1; i++)
        {
            var prev = pts[i - 1];
            var cur = pts[i];
            var next = pts[i + 1];

            double inLen = Math.Sqrt(Math.Pow(cur.x - prev.x, 2) + Math.Pow(cur.y - prev.y, 2));
            double outLen = Math.Sqrt(Math.Pow(next.x - cur.x, 2) + Math.Pow(next.y - cur.y, 2));
            double radius = Math.Min(r, Math.Min(inLen, outLen) / 2);

            if (radius < 0.5)
            {
                sb.Append($" L{F(cur.x)},{F(cur.y)}");
                continue;
            }

            // Point r before corner on incoming segment
            double ax = cur.x + (prev.x - cur.x) / inLen * radius;
            double ay = cur.y + (prev.y - cur.y) / inLen * radius;
            // Point r after corner on outgoing segment
            double bx = cur.x + (next.x - cur.x) / outLen * radius;
            double by = cur.y + (next.y - cur.y) / outLen * radius;

            sb.Append($" L{F(ax)},{F(ay)} Q{F(cur.x)},{F(cur.y)} {F(bx)},{F(by)}");
        }

        sb.Append($" L{F(pts[^1].x)},{F(pts[^1].y)}");
        return sb.ToString();
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}
