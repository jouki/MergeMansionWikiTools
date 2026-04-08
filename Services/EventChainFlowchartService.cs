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
    const double NodeGapX = 50;
    const double MinStreamSlots = 3;   // minimum stream slots between layers
    const double StreamGap = 20;       // gap between node edge and stream, and between adjacent streams (shared)
    const double MaxNodeW = 260;
    const double MinNodeW = 160;
    const double HeaderH = 30;
    const double BodyH = 44;
    const double SubH = 0;
    const double ArrowSize = 6;
    const double BendRadius = 8;
    const double BendRadiusLarge = 13;
    const double SnapThresh = 4;
    const double DecayStartExtend = 5; // extend decay start upward into node corner rounding
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
        public double WeightedDist = double.MaxValue; // weighted distance from nearest root
        public HashSet<string> Parents = new();
        public List<string> Children = new();  // ordered list, not HashSet — order matters for layout
        public int Layer, Order;
        public double X, Y, W, H;
        public bool IsDummy;
    }

    // ══════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ══════════════════════════════════════════════════════════════

    public static string GenerateSvg(List<ParsedChain> eventChains, DataService ds, string eventName,
        Dictionary<string, string>? chainIcons = null)
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
                W = w, H = HeaderH + BodyH,
            };
        }

        // Build adjacency — SpawnDrop, Decay, and SinkInput form parent→child hierarchy.
        // SinkOutput (reward edges) only form hierarchy for non-generator targets.
        // This ensures characters (Order items fed by SinkInput) appear below their source items,
        // while SinkInput to Machines (transformative items) doesn't create wrong parent relationships.
        var orderChains = new HashSet<string>(eventChains
            .Where(c => c.Items.Any(i => i.IsOrder))
            .Select(c => c.ConfigKey), StringComparer.OrdinalIgnoreCase);

        foreach (var e in edges.OrderBy(e => e.EdgeType))
        {
            if (!graph.ContainsKey(e.SourceChainKey) || !graph.ContainsKey(e.TargetChainKey)) continue;

            // Skip SinkInput unless target is an Order chain (character)
            if (e.EdgeType == ChainEdgeType.SinkInput && !orderChains.Contains(e.TargetChainKey)) continue;
            var src = graph[e.SourceChainKey];
            if (!src.Children.Contains(e.TargetChainKey))
                src.Children.Add(e.TargetChainKey);
            graph[e.TargetChainKey].Parents.Add(e.SourceChainKey);
        }

        // Keep all nodes (including isolated — e.g. characters on board)

        // Layout
        var layers = SugiyamaLayout(graph, edges);

        // Build icon lookup: chainKey → base64 PNG (try highest level, fallback to level 1)
        var iconByChain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (chainIcons != null)
        {
            foreach (var chain in eventChains)
            {
                if (chain.Items.Count == 0) continue;
                // Try highest level first
                foreach (var item in chain.Items.OrderByDescending(i => i.Level))
                {
                    if (!string.IsNullOrEmpty(item.ItemType) && chainIcons.TryGetValue(item.ItemType, out var b64))
                    {
                        iconByChain[chain.ConfigKey] = b64;
                        break;
                    }
                }
            }
        }

        // Render
        return RenderSvg(graph, edges, layers, eventName, iconByChain);
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

                // Spawn/Drop — sorted by drop rate descending (highest rate = first child)
                if (item.DropOdds != null)
                    foreach (var k in item.DropOdds.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
                        AddEdge(k, ChainEdgeType.SpawnDrop);
                if (item.SpawnOdds != null)
                    foreach (var k in item.SpawnOdds.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
                        AddEdge(k, ChainEdgeType.SpawnDrop);
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
                        {
                            // Skip reward edges targeting infinite generators (no decay)
                            var targetCk = ResolveChainKey(k, itemToChain, ds);
                            var targetChain = targetCk != null ? eventChains.FirstOrDefault(c => c.ConfigKey == targetCk) : null;
                            bool isInfiniteGen = targetChain?.Items.Any(i =>
                                i.IsGenerator
                                && string.IsNullOrEmpty(i.DecayAfterLastCycleItemType)
                                && i.DecayAfterLastCycleOdds == null) == true;
                            if (!isInfiniteGen)
                                AddEdge(k, ChainEdgeType.SinkOutput);
                        }
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
    /// <summary>Extracts minimum level number from label like "L3, L5" or "L6-L10" → 3 or 6.</summary>
    static double ExtractMinLevel(string label)
    {
        if (string.IsNullOrEmpty(label)) return 1;
        double min = double.MaxValue;
        foreach (var part in label.Split(',', StringSplitOptions.TrimEntries))
        {
            var p = part.Trim();
            if (p.StartsWith("L") && int.TryParse(p[1..].Split('-')[0], out var n))
                min = Math.Min(min, n);
        }
        return min < double.MaxValue ? min : 1;
    }

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

    static List<List<string>> SugiyamaLayout(Dictionary<string, N> g, List<ChainGraphEdge> edges)
    {
        AssignLayers(g, edges);

        int maxLayer = g.Values.Max(n => n.Layer);
        var layers = Enumerable.Range(0, maxLayer + 1).Select(_ => new List<string>()).ToList();
        foreach (var n in g.Values) layers[n.Layer].Add(n.Id);

        InsertDummyNodes(g, layers);
        MinimizeCrossings(g, layers);
        AssignXPositions(g, layers);
        AssignYPositions(g, layers);
        CompactColumns(g);

        return layers;
    }

    static void AssignLayers(Dictionary<string, N> g, List<ChainGraphEdge> edges)
    {
        // Weighted shortest-path layer assignment (Dijkstra-like).
        // Edge weight = minimum level number from label (L1 = cheap, L10 = expensive).
        // Nodes appear at layer = ceil(weighted_distance / bucket_size).
        // This ensures Lollipops (L1 from Candy Machine, dist=13) appears BELOW
        // Candy Machine (L5 from Machine Assembly, dist=12), not beside it.

        // Build edge weight lookup: source→target → min level
        var edgeWeight = new Dictionary<string, double>();
        foreach (var e in edges)
        {
            var key = $"{e.SourceChainKey}→{e.TargetChainKey}";
            double w = ExtractMinLevel(e.Label);
            if (!edgeWeight.ContainsKey(key) || w < edgeWeight[key])
                edgeWeight[key] = w;
        }

        // Dijkstra from all roots
        var roots = g.Values.Where(n => !n.Parents.Any(p => g.ContainsKey(p))).Select(n => n.Id).ToList();
        foreach (var n in g.Values) n.WeightedDist = double.MaxValue;
        var pq = new SortedSet<(double dist, string id)>();

        foreach (var r in roots)
        {
            g[r].WeightedDist = 0;
            pq.Add((0, r));
        }

        while (pq.Count > 0)
        {
            var (dist, id) = pq.Min;
            pq.Remove(pq.Min);
            var n = g[id];
            if (dist > n.WeightedDist) continue;

            foreach (var c in n.Children)
            {
                if (!g.TryGetValue(c, out var cn)) continue;
                var wKey = $"{id}→{c}";
                double w = edgeWeight.GetValueOrDefault(wKey, 1);
                double newDist = dist + w;
                if (newDist < cn.WeightedDist)
                {
                    cn.WeightedDist = newDist;
                    pq.Add((newDist, c));
                }
            }
        }

        // Assign layers: sort by weighted distance, bucket into layers.
        // Nodes with same weighted distance = same layer.
        // Bucket: group by distinct sorted distances.
        var allDists = g.Values
            .Where(n => n.WeightedDist < double.MaxValue)
            .Select(n => n.WeightedDist)
            .Distinct().OrderBy(d => d).ToList();

        var distToLayer = new Dictionary<double, int>();
        for (int i = 0; i < allDists.Count; i++)
            distToLayer[allDists[i]] = i;

        foreach (var n in g.Values)
            n.Layer = n.WeightedDist < double.MaxValue ? distToLayer[n.WeightedDist] : 0;

        // Push-down: ensure every node is BELOW all its hierarchical parents.
        var topo = TopologicalSort(g);
        bool changed = true;
        for (int pass = 0; pass < 10 && changed; pass++)
        {
            changed = false;
            foreach (var id in topo)
            {
                var n = g[id];
                foreach (var p in n.Parents)
                {
                    if (!g.TryGetValue(p, out var pn)) continue;
                    if (n.Layer <= pn.Layer)
                    {
                        n.Layer = pn.Layer + 1;
                        changed = true;
                    }
                }
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
        // Layer-by-layer BFS ordering: process each layer left-to-right,
        // append each node's children in order to the next layer.
        // This produces intuitive flowchart ordering.

        // Order layer 0: most total descendants first (main event chain tree left)
        int CountDescendants(string rootId)
        {
            var seen = new HashSet<string>();
            var q = new Queue<string>();
            q.Enqueue(rootId);
            while (q.Count > 0)
            {
                var nid = q.Dequeue();
                if (!seen.Add(nid) || !g.TryGetValue(nid, out var nn)) continue;
                foreach (var c in nn.Children.Where(c => g.ContainsKey(c)))
                    q.Enqueue(c);
            }
            return seen.Count;
        }
        layers[0] = layers[0].OrderByDescending(CountDescendants).ToList();
        for (int i = 0; i < layers[0].Count; i++)
            g[layers[0][i]].Order = i;

        AppLogger.Info($"[FLOWCHART-ORDER] Layer 0: {string.Join(", ", layers[0].Select(id => g[id].Title))}");
        foreach (var rid in layers[0])
            AppLogger.Info($"[FLOWCHART-ORDER]   {g[rid].Title} descendants={CountDescendants(rid)} children=[{string.Join(", ", g[rid].Children.Select(c => g.TryGetValue(c, out var cn) ? cn.Title : c))}]");

        // For each subsequent layer: order by parent position
        for (int li = 1; li < layers.Count; li++)
        {
            var layerNodes = new HashSet<string>(layers[li]);
            var ordered = new List<string>();
            var added = new HashSet<string>();

            // Walk previous layer left-to-right, append each parent's children
            foreach (var parentId in layers[li - 1])
            {
                if (!g.TryGetValue(parentId, out var parent)) continue;
                foreach (var childId in parent.Children)
                {
                    if (layerNodes.Contains(childId) && added.Add(childId))
                    {
                        ordered.Add(childId);
                        if (li <= 3)
                            AppLogger.Info($"[FLOWCHART-ORDER] Layer {li}: parent '{g[parentId].Title}' → child '{(g.TryGetValue(childId, out var cn) ? cn.Title : childId)}'");
                    }
                }
            }

            // Append any remaining nodes not reached (disconnected or from earlier layers)
            foreach (var id in layers[li])
                if (added.Add(id))
                    ordered.Add(id);

            layers[li] = ordered;
            for (int i = 0; i < ordered.Count; i++)
                g[ordered[i]].Order = i;
        }

        // No barycenter refinement — BFS parent-first ordering is authoritative.
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

        // NO centering — left-aligned layout. Nodes positioned by median improvement below.
        double maxW = layers.Max(l => l.Count > 0 ? l.Max(id => g[id].X + g[id].W) : 0);
        // Skip centering — was causing the layout to appear centered
        if (false) foreach (var layer in layers)
        {
            if (layer.Count == 0) continue;
            double lw = layer.Max(id => g[id].X + g[id].W);
            double off = (maxW - lw) / 2;
            foreach (var id in layer) g[id].X += off;
        }

        // Improve: median of parents/children (16 iterations for better convergence)
        for (int iter = 0; iter < 16; iter++)
        {
            bool topDown = iter % 2 == 0;
            var order = topDown ? layers : layers.AsEnumerable().Reverse().ToList();
            foreach (var layer in order)
            {
                for (int i = 0; i < layer.Count; i++)
                {
                    var n = g[layer[i]];
                    if (n.IsDummy) continue;
                    var refs = (topDown ? (IEnumerable<string>)n.Parents : n.Children)
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

        // Final pass: snap nodes with single parent directly under parent center
        foreach (var layer in layers)
        {
            for (int i = 0; i < layer.Count; i++)
            {
                var n = g[layer[i]];
                if (n.IsDummy) continue;
                var parents = n.Parents.Where(p => g.ContainsKey(p) && !g[p].IsDummy).ToList();
                if (parents.Count != 1) continue;

                var parent = g[parents[0]];
                double desired = parent.X + parent.W / 2 - n.W / 2;
                double leftBound = i > 0 ? g[layer[i - 1]].X + g[layer[i - 1]].W + NodeGapX : double.MinValue;
                double rightBound = i < layer.Count - 1 ? g[layer[i + 1]].X - n.W - NodeGapX : double.MaxValue;
                n.X = Math.Clamp(desired, leftBound, rightBound);
            }
        }
    }

    static void AssignYPositions(Dictionary<string, N> g, List<List<string>> layers)
    {
        // LayerGap = StreamGap (node-to-stream) + MinStreamSlots * StreamGap + StreamGap (stream-to-node)
        double layerGap = StreamGap + MinStreamSlots * StreamGap + StreamGap;

        double y = 0;
        foreach (var layer in layers)
        {
            double maxH = layer.Where(id => !g[id].IsDummy).Select(id => g[id].H).DefaultIfEmpty(20).Max();
            foreach (var id in layer)
            {
                var n = g[id];
                n.Y = n.IsDummy ? y + maxH / 2 : y + (maxH - n.H) / 2;
            }
            y += maxH + layerGap;
        }
    }

    static void CompactColumns(Dictionary<string, N> g)
    {
        var realNodes = g.Values.Where(n => !n.IsDummy).ToList();
        if (realNodes.Count == 0) return;

        var cols = realNodes.GroupBy(n => Math.Round(n.X, 1)).OrderBy(c => c.Key).ToList();
        double yBuf = (StreamGap + MinStreamSlots * StreamGap + StreamGap) / 2;

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
        List<List<string>> layers, string eventName, Dictionary<string, string>? iconByChain = null)
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
        RouteAndRenderEdges(sb, graph, edges, layers, ox, oy, ci);

        // ── Render nodes ─────────────────────────────────────────
        foreach (var n in graph.Values.Where(n => !n.IsDummy))
        {
            double nx = n.X + ox, ny = n.Y + oy;
            double hh = HeaderH;

            // Body
            sb.AppendLine($"<rect x='{F(nx)}' y='{F(ny)}' width='{F(n.W)}' height='{F(n.H)}' class='node-body'/>");

            // Header background (rounded top, clipped)
            var clipId = $"clip-{Math.Abs(n.Id.GetHashCode()):X}";
            sb.AppendLine($"<clipPath id='{clipId}'><rect x='{F(nx)}' y='{F(ny)}' width='{F(n.W)}' height='{F(n.H)}' rx='8'/></clipPath>");
            sb.AppendLine($"<rect x='{F(nx)}' y='{F(ny)}' width='{F(n.W)}' height='{F(hh)}' fill='#4A7CBF' clip-path='url(#{clipId})'/>");
            sb.AppendLine($"<rect x='{F(nx)}' y='{F(ny + hh - 4)}' width='{F(n.W)}' height='4' fill='#3A5F96'/>");

            // Title (centered in header)
            int maxChars = Math.Max(5, (int)((n.W - PadX * 2) / 7.8));
            string title = n.Title.Length > maxChars ? n.Title[..Math.Max(maxChars - 3, 3)] + "..." : n.Title;
            sb.AppendLine($"<text x='{F(nx + n.W / 2)}' y='{F(ny + hh / 2 + 5)}' text-anchor='middle' class='node-title'>{Esc(title)}</text>");

            // Body: icon slot + subtitle
            double bodyY = ny + hh;
            string? iconB64 = null;
            bool hasIcon = iconByChain != null && iconByChain.TryGetValue(n.Id, out iconB64);
            double iconSize = 32;
            double slotSize = iconSize + 2;
            double slotX = nx + PadX - 2;
            double slotY = bodyY + (BodyH - slotSize) / 2;

            // Icon slot background
            sb.AppendLine($"<rect x='{F(slotX)}' y='{F(slotY)}' width='{F(slotSize)}' height='{F(slotSize)}' rx='4' fill='#D4C4A8' stroke='#B8A888' stroke-width='1'/>");

            if (hasIcon)
            {
                sb.AppendLine($"<image x='{F(slotX + 1)}' y='{F(slotY + 1)}' width='{F(iconSize)}' height='{F(iconSize)}' " +
                    $"href='data:image/png;base64,{iconB64}' preserveAspectRatio='xMidYMid meet'/>");
            }

            // Subtitle text right of icon
            double subTextX = slotX + slotSize + 6;
            sb.AppendLine($"<text x='{F(subTextX)}' y='{F(bodyY + BodyH / 2 + 4)}' class='node-sub'>{Esc(n.Sub)}</text>");
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
        List<ChainGraphEdge> edges, List<List<string>> layers, double ox, double oy, CultureInfo ci)
    {
        // Node bounding boxes (with generous margin to prevent edge-on-border)
        double margin = 8;
        var boxes = graph.Values.Where(n => !n.IsDummy)
            .Select(n => (left: n.X + ox - margin, top: n.Y + oy - margin,
                right: n.X + ox + n.W + margin, bottom: n.Y + oy + n.H + margin, id: n.Id))
            .ToList();

        // Layer Y ranges for gap routing
        var layerBottom = new double[layers.Count];
        var layerTop = new double[layers.Count];
        for (int li = 0; li < layers.Count; li++)
        {
            double minY = double.MaxValue, maxYH = double.MinValue;
            foreach (var nid in layers[li])
            {
                if (!graph.TryGetValue(nid, out var n) || n.IsDummy) continue;
                double ny = n.Y + oy;
                minY = Math.Min(minY, ny);
                maxYH = Math.Max(maxYH, ny + n.H);
            }
            layerTop[li] = minY == double.MaxValue ? 0 : minY;
            layerBottom[li] = maxYH == double.MinValue ? 0 : maxYH;
        }

        // Node → layer lookup
        var nodeLayer = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int li = 0; li < layers.Count; li++)
            foreach (var nid in layers[li])
                if (graph.TryGetValue(nid, out var n) && !n.IsDummy)
                    nodeLayer[nid] = li;

        // Per-source index for Y offset — edges from different sources route at different Y levels
        var srcChildIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        // Route each edge
        foreach (var group in edges.GroupBy(e => e.EdgeType).OrderBy(g => g.Key))
        {
            var color = EdgeStyles[group.Key].color;
            var markerId = $"arr-{group.Key}";

            foreach (var edge in group)
            {
                if (!graph.TryGetValue(edge.SourceChainKey, out var src) ||
                    !graph.TryGetValue(edge.TargetChainKey, out var tgt)) continue;

                bool isDecay = edge.EdgeType == ChainEdgeType.Decay;

                // Per-source Y offset: edges from different sources route at different Y in the gap
                // Combined with per-type offset to fully separate all edge groups
                double typeYOff = (int)edge.EdgeType * 8;
                if (!srcChildIndex.ContainsKey(edge.SourceChainKey))
                    srcChildIndex[edge.SourceChainKey] = srcChildIndex.Count;
                double srcYOff = srcChildIndex[edge.SourceChainKey] * 8;

                // Anchor points
                double sx, sy, tx, ty;
                if (isDecay)
                {
                    // Decay: start from right-bottom corner, extended up into rounding
                    sx = src.X + ox + src.W;
                    sy = src.Y + oy + src.H - DecayStartExtend;  // start slightly inside bottom for rounding
                    tx = tgt.X + ox + tgt.W / 2;
                    ty = tgt.Y + oy;
                }
                else
                {
                    sx = src.X + ox + src.W / 2;
                    sy = src.Y + oy + src.H;
                    tx = tgt.X + ox + tgt.W / 2;
                    ty = tgt.Y + oy;
                }

                // For backward edges (source below target), swap to route upward
                bool isBackward = sy >= ty - 2;
                if (isBackward && !isDecay)
                {
                    // Route as decay-like: right side exit, route around
                    sx = src.X + ox + src.W;
                    sy = src.Y + oy + src.H / 2;
                }

                int srcL = nodeLayer.GetValueOrDefault(src.Id, -1);
                int tgtL = nodeLayer.GetValueOrDefault(tgt.Id, -1);

                var pts = new List<(double x, double y)> { (sx, sy) };

                // Bus offset for multi-child sources
                bool isMultiChild = src.Children.Count > 1;
                double routeX = sx, routeY = sy;
                if (isMultiChild && !isDecay)
                {
                    double busY = sy + StreamGap;
                    pts.Add((sx, busY));
                    routeY = busY;
                }

                if (isDecay)
                {
                    // Decay: always go DOWN from right-bottom corner, then turn horizontally
                    // Stream Y = 20px below source node bottom
                    double nodeBottom = src.Y + oy + src.H;
                    double streamY = nodeBottom + StreamGap;

                    // Route: down from corner to stream Y, then horizontal to target X, then up to target
                    pts.Add((sx, streamY));      // down to stream level
                    pts.Add((tx, streamY));      // horizontal to target column
                    pts.Add((tx, ty));           // up to target top
                }
                else
                {
                    // Strategy 1: Straight vertical
                    bool routed = false;
                    if (Math.Abs(routeX - tx) < SnapThresh)
                    {
                        if (!Hits(routeX, routeY, routeX, ty, boxes, src.Id, tgt.Id))
                        {
                            pts.Add((routeX, ty));
                            routed = true;
                        }
                    }

                    if (!routed)
                    {
                        // Strategy 2: Z-bend at inter-layer gap (offset per source + type)
                        if (srcL >= 0 && tgtL >= 0 && tgtL > srcL)
                        {
                            for (int gl = srcL; gl < tgtL && !routed; gl++)
                            {
                                double gapY = (layerBottom[gl] + layerTop[gl + 1]) / 2 + srcYOff + typeYOff;
                                if (gapY <= routeY + 2 || gapY >= ty - 2) continue;

                                bool hit = Hits(routeX, routeY, routeX, gapY, boxes, src.Id, tgt.Id)
                                    || Hits(routeX, gapY, tx, gapY, boxes, src.Id, tgt.Id)
                                    || Hits(tx, gapY, tx, ty, boxes, src.Id, tgt.Id);
                                if (!hit)
                                {
                                    pts.Add((routeX, gapY));
                                    pts.Add((tx, gapY));
                                    pts.Add((tx, ty));
                                    routed = true;
                                }
                            }
                        }
                    }

                    if (!routed)
                    {
                        // Strategy 3: Corridor routing
                        double gapY1 = routeY + StreamGap;
                        double gapY2 = ty - StreamGap;
                        if (srcL >= 0 && tgtL >= 0 && tgtL > srcL)
                        {
                            double g1 = (layerBottom[srcL] + layerTop[Math.Min(srcL + 1, layers.Count - 1)]) / 2;
                            if (g1 > routeY + 2) gapY1 = g1;
                            double g2 = (layerBottom[Math.Max(tgtL - 1, 0)] + layerTop[tgtL]) / 2;
                            if (g2 < ty - 2) gapY2 = g2;
                        }

                        // Find clear corridor X
                        var occupied = boxes.Where(b => b.id != src.Id && b.id != tgt.Id
                            && b.bottom + margin >= gapY1 && b.top - margin <= gapY2)
                            .Select(b => (b.left - margin, b.right + margin)).OrderBy(x => x.Item1).ToList();

                        var merged = new List<(double l, double r)>();
                        foreach (var iv in occupied)
                        {
                            if (merged.Count > 0 && iv.Item1 <= merged[^1].r)
                                merged[^1] = (merged[^1].l, Math.Max(merged[^1].r, iv.Item2));
                            else merged.Add(iv);
                        }

                        var cands = new List<double>();
                        if (merged.Count > 0 && merged[0].l > NodeGapX)
                            cands.Add(merged[0].l - NodeGapX / 2);
                        for (int i = 0; i < merged.Count - 1; i++)
                            cands.Add((merged[i].r + merged[i + 1].l) / 2);
                        if (merged.Count > 0)
                            cands.Add(merged[^1].r + NodeGapX / 2);
                        if (cands.Count == 0) { cands.Add(routeX); cands.Add(tx); }

                        double bestX = cands[0]; int bestH = int.MaxValue;
                        double midX = (routeX + tx) / 2;
                        foreach (var cx in cands)
                        {
                            int h = 0;
                            if (Hits(routeX, routeY, routeX, gapY1, boxes, src.Id, tgt.Id)) h++;
                            if (Hits(routeX, gapY1, cx, gapY1, boxes, src.Id, tgt.Id)) h++;
                            if (Hits(cx, gapY1, cx, gapY2, boxes, src.Id, tgt.Id)) h++;
                            if (Hits(cx, gapY2, tx, gapY2, boxes, src.Id, tgt.Id)) h++;
                            if (h < bestH || (h == bestH && Math.Abs(cx - midX) < Math.Abs(bestX - midX)))
                            { bestH = h; bestX = cx; if (h == 0) break; }
                        }

                        if (Math.Abs(bestX - routeX) < SnapThresh)
                        { pts.Add((routeX, gapY2)); pts.Add((tx, gapY2)); pts.Add((tx, ty)); }
                        else if (Math.Abs(bestX - tx) < SnapThresh)
                        { pts.Add((routeX, gapY1)); pts.Add((tx, gapY1)); pts.Add((tx, ty)); }
                        else
                        { pts.Add((routeX, gapY1)); pts.Add((bestX, gapY1)); pts.Add((bestX, gapY2)); pts.Add((tx, gapY2)); pts.Add((tx, ty)); }
                    }
                }

                var filtered = FilterCollinear(pts);
                var path = BuildRoundedPath(filtered, BendRadius, ci);
                sb.AppendLine($"<path d='{path}' stroke='{color}' stroke-width='1.8' fill='none' marker-end='url(#{markerId})'/>");

                // Label on first vertical segment
                var edgeLabel = CompressLevelLabel(edge.Label);
                if (!string.IsNullOrEmpty(edgeLabel) && filtered.Count >= 2)
                {
                    // Find first vertical segment
                    int seg = 0;
                    for (int i = 0; i < filtered.Count - 1; i++)
                        if (Math.Abs(filtered[i].x - filtered[i + 1].x) < 1) { seg = i; break; }

                    double lx = filtered[seg].x - 12; // left of vertical line
                    double ly = (filtered[seg].y + filtered[seg + 1].y) / 2 + 3;
                    sb.AppendLine($"<text x='{lx.ToString("F1", ci)}' y='{ly.ToString("F1", ci)}' text-anchor='end' class='edge-label' fill='{color}'>{Esc(edgeLabel)}</text>");
                }
            }
        }
    }

    static bool Hits(double x1, double y1, double x2, double y2,
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
