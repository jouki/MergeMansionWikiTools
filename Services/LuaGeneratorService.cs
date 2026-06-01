using System.Globalization;
using System.Text;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public record ItemChunksResult(
    List<(string Label, string Lua)> Chunks,
    int FirstEventChunkIndex,                   // 1-based; 0 = no event chunks
    List<LuaGeneratorService.FlatItem> FlatItems); // Live items used to build the chunks (preserved for chainNames regeneration with archive)

public class LuaGeneratorService
{
    /// <summary>
    /// Builds the standard Lua header comments (createdAt + mmwtVersion).
    /// </summary>
    private static string BuildLuaHeader(string? createdAt)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(createdAt))
            sb.Append($"-- createdAt: {createdAt}\n");
        sb.Append($"-- mmwtVersion: {AppVersion.Version}\n");
        return sb.ToString();
    }

    // ── Area Lua ──────────────────────────────────────────────────────

    /// <summary>
    /// Dynamically splits areas into chunks that fit within 90% of 2 MB.
    /// Each area stays whole (never split mid-area).
    /// Returns a list of (Label, Lua) pairs, e.g. ("1–35", "local p = ...").
    /// </summary>
    public List<(string Label, string Lua)> GenerateAreaChunks(
        List<LuaArea> areas,
        string? createdAt = null)
    {
        var result = new List<(string, string)>();
        if (areas.Count == 0) return result;

        // Try single chunk first
        var full = GenerateAreasLua(areas, 0, areas.Count - 1, createdAt);
        if (Encoding.UTF8.GetByteCount(full) < ChunkThreshold)
        {
            result.Add(("1–" + areas.Count, full));
            return result;
        }

        // Generate Lua for each area individually to measure sizes
        var perArea = new List<string>(areas.Count);
        for (int i = 0; i < areas.Count; i++)
            perArea.Add(GenerateSingleAreaEntry(areas[i], isLast: false));

        // Calculate wrapper overhead
        var wrapperTemplate = BuildAreaWrapper("", createdAt);
        var wrapperBytes = (long)Encoding.UTF8.GetByteCount(wrapperTemplate);
        var targetBytes = ChunkThreshold - wrapperBytes;

        // Greedily pack areas into chunks
        var chunkStart = 0;
        long currentBytes = 0;

        for (int i = 0; i < areas.Count; i++)
        {
            var entryBytes = Encoding.UTF8.GetByteCount(perArea[i]);

            if (currentBytes + entryBytes > targetBytes && i > chunkStart)
            {
                // Emit current chunk
                var label = $"{chunkStart + 1}–{i}";
                result.Add((label, GenerateAreasLua(areas, chunkStart, i - 1, createdAt)));
                chunkStart = i;
                currentBytes = 0;
            }

            currentBytes += entryBytes;
        }

        // Emit final chunk
        var finalLabel = $"{chunkStart + 1}–{areas.Count}";
        result.Add((finalLabel, GenerateAreasLua(areas, chunkStart, areas.Count - 1, createdAt)));

        return result;
    }

    private static string GenerateAreasLua(List<LuaArea> areas, int startIdx, int endIdx, string? createdAt = null)
    {
        var sb = new StringBuilder();
        sb.Append(BuildLuaHeader(createdAt));
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
            if (!string.IsNullOrEmpty(area.ReleaseDate))
                sb.Append($"\t\trelease = \"{Esc(area.ReleaseDate)}\",\n");
            sb.Append($"\t\ttasks = {tasks}\n");
            sb.Append($"\t}}{comma}");
        }

        sb.Append("\n}\n\nreturn p");
        return sb.ToString();
    }

    /// <summary>Generates the Lua entry for a single area (without wrapper).</summary>
    private static string GenerateSingleAreaEntry(LuaArea area, bool isLast)
    {
        var sb = new StringBuilder();
        var comma = isLast ? "" : ",";
        var tasks = BuildTasksTable(area.Tasks, 3);
        sb.Append($"\n\t[\"{Esc(area.DisplayName)}\"] = {{\n");
        sb.Append($"\t\tname = \"{Esc(area.DisplayName)}\",\n");
        sb.Append($"\t\tingameName = \"{Esc(area.InternalName)}\",\n");
        if (!string.IsNullOrEmpty(area.ReleaseDate))
            sb.Append($"\t\trelease = \"{Esc(area.ReleaseDate)}\",\n");
        sb.Append($"\t\ttasks = {tasks}\n");
        sb.Append($"\t}}{comma}");
        return sb.ToString();
    }

    /// <summary>Builds the area Lua wrapper (header + footer) with empty content, for size measurement.</summary>
    private static string BuildAreaWrapper(string content, string? createdAt)
    {
        var sb = new StringBuilder();
        sb.Append(BuildLuaHeader(createdAt));
        sb.Append($"local p = {{}}\n\np.areas = {{{content}\n}}\n\nreturn p");
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

            if (!string.IsNullOrEmpty(task.Minigame))
                sb.Append($"\n{p2}minigame = \"{Esc(task.Minigame)}\",");

            if (!string.IsNullOrEmpty(task.UnlockDate))
                sb.Append($"\n{p2}unlock = \"{Esc(task.UnlockDate)}\",");

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
                sb.Append("},");
            }

            // Token values (ExtraSpawn: DigEventTaps, QuaternaryEnergy, etc.)
            if (task.TokenValues.Count > 0)
            {
                sb.Append($"\n{p2}tokens = {{");
                sb.Append(string.Join(", ", task.TokenValues.Select(
                    kv => $"{CamelToLua(kv.Key)} = {kv.Value}")));
                sb.Append("}");
            }

            sb.Append($"\n{p1}}}{comma}");
        }

        sb.Append($"\n{p0}}}");
        return sb.ToString();
    }

    // ── Items + ChainNames Lua ────────────────────────────────────────

    private const long ChunkThreshold = (long)(0.9 * 2 * 1024 * 1024); // 90% of 2 MB = 1,843,200 bytes

    /// <summary>
    /// Generates the combined items + chainNames Lua module.
    /// Uses chain.DisplayName (may include wiki mapping / custom names).
    /// Output: local str = require('Module:Strings') / local p = {} / p.items = {...} / p.chainNames = {...} / return p
    /// </summary>
    public string GenerateCombinedItemsAndChainNamesLua(List<ParsedChain> chains)
    {
        var items = BuildFlatItems(chains, useRawNames: false);
        var itemsBlock = BuildItemsTable(items);
        var chainNamesBlock = BuildChainNamesTable(items);

        return $"local str = require('Module:Strings')\nlocal p = {{}}\n\n{itemsBlock}\n\n{chainNamesBlock}\n\nreturn p";
    }

    /// <summary>
    /// Generates the combined items + chainNames Lua module using raw JSON names only.
    /// Uses chain.OriginalName (fallback ConfigKey) — no wiki mapping, no custom names.
    /// </summary>
    public string GenerateRawItemsAndChainNamesLua(List<ParsedChain> chains, string? createdAt = null)
    {
        var items = BuildFlatItems(chains, useRawNames: true);
        var itemsBlock = BuildItemsTable(items);
        var chainNamesBlock = BuildChainNamesTable(items);

        var prefix = BuildLuaHeader(createdAt);
        return $"{prefix}local str = require('Module:Strings')\nlocal p = {{}}\n\n{itemsBlock}\n\n{chainNamesBlock}\n\nreturn p";
    }

    /// <summary>
    /// Generates item data as one or more chunks based on size.
    /// If the combined module would exceed 90% of 2 MB, items are split into multiple chunks.
    /// Main game items are placed first, event items after them.
    /// Each chunk: "-- createdAt: ...\nlocal p = {}\n\np.items = {\n\t[...]\n}\n\nreturn p"
    /// Returns ItemChunksResult with (Label, Lua) pairs and firstEventChunkIndex (1-based; 0 = no event separation).
    /// </summary>
    public ItemChunksResult GenerateItemChunks(
        List<ParsedChain> chains, bool useRawNames, string? createdAt = null)
    {
        // Split chains into main and event groups
        var mainChains = chains.Where(c => !c.IsEventChain).ToList();
        var eventChains = chains.Where(c => c.IsEventChain).ToList();

        var mainItems = BuildFlatItems(mainChains, useRawNames);
        var eventItems = BuildFlatItems(eventChains, useRawNames);
        var allItems = mainItems.Concat(eventItems).ToList();

        var mainItemsBlock = BuildItemsTable(mainItems);
        var eventItemsBlock = BuildItemsTable(eventItems);
        var chainNamesBlock = BuildChainNamesTable(allItems);

        var prefix = BuildLuaHeader(createdAt);

        // Build full single module to measure size
        var combinedItemsBlock = BuildItemsTable(allItems);
        var full = $"{prefix}local str = require('Module:Strings')\nlocal p = {{}}\n\n{combinedItemsBlock}\n\n{chainNamesBlock}\n\nreturn p";
        var totalBytes = Encoding.UTF8.GetByteCount(full);

        if (totalBytes < ChunkThreshold)
        {
            // Single chunk — no splitting needed
            return new ItemChunksResult(
                new List<(string, string)> { ("", full) },
                FirstEventChunkIndex: 0,
                FlatItems: allItems);
        }

        // Multi-chunk: split main and event entries separately, then concatenate
        var mainEntries = SplitItemEntries(mainItemsBlock);
        var eventEntries = SplitItemEntries(eventItemsBlock);

        // Calculate overhead per chunk (wrapper bytes)
        var wrapperTemplate = $"{prefix}local p = {{}}\n\np.items = {{\n}}\n\nreturn p";
        var wrapperBytes = Encoding.UTF8.GetByteCount(wrapperTemplate);
        var targetChunkBytes = ChunkThreshold - wrapperBytes;

        // Chunk main entries
        var mainChunks = ChunkEntries(mainEntries, targetChunkBytes, prefix);

        // Chunk event entries
        var eventChunks = ChunkEntries(eventEntries, targetChunkBytes, prefix);

        // Renumber: main chunks 1..N, event chunks N+1..M
        var result = new List<(string, string)>();
        for (int i = 0; i < mainChunks.Count; i++)
            result.Add(BuildItemChunk(mainChunks[i], result.Count + 1, prefix));
        var firstEventChunkIndex = eventChunks.Count > 0 ? result.Count + 1 : 0;
        for (int i = 0; i < eventChunks.Count; i++)
            result.Add(BuildItemChunk(eventChunks[i], result.Count + 1, prefix));

        return new ItemChunksResult(result, firstEventChunkIndex, allItems);
    }

    /// <summary>
    /// Splits a list of item entries into sized chunk groups.
    /// Returns a list of entry groups, each fitting within targetBytes.
    /// </summary>
    private static List<List<string>> ChunkEntries(List<string> entries, long targetBytes, string prefix)
    {
        if (entries.Count == 0) return new List<List<string>>();

        var totalBytes = entries.Sum(e => (long)Encoding.UTF8.GetByteCount(e));
        var chunkCount = Math.Max(1, (int)Math.Ceiling((double)totalBytes / targetBytes));
        var targetPerChunk = totalBytes / chunkCount;

        var chunks = new List<List<string>>();
        var current = new List<string>();
        var currentBytes = 0L;

        for (int i = 0; i < entries.Count; i++)
        {
            var entryBytes = Encoding.UTF8.GetByteCount(entries[i]);
            current.Add(entries[i]);
            currentBytes += entryBytes;

            var isLastEntry = i == entries.Count - 1;
            var chunksFull = chunks.Count >= chunkCount - 1;
            if (!isLastEntry && !chunksFull && currentBytes >= targetPerChunk)
            {
                chunks.Add(current);
                current = new List<string>();
                currentBytes = 0;
            }
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    /// <summary>
    /// Generates just the p.chainNames block (for embedding in the arbiter module).
    /// </summary>
    public string GenerateChainNamesLua(List<ParsedChain> chains, bool useRawNames)
    {
        var items = BuildFlatItems(chains, useRawNames);
        return BuildChainNamesTable(items);
    }

    private static List<string> SplitItemEntries(string itemsBlock)
    {
        // itemsBlock starts with "p.items = {" and ends with "\n}"
        // Each entry is "\n\t[\"key\"] = {...}," or "\n\t[\"key\"] = {...}" (last one no comma)
        var entries = new List<string>();
        var lines = itemsBlock.Split('\n');

        // Skip first line "p.items = {" and last line "}"
        for (int i = 1; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            if (line.StartsWith("\t[\""))
                entries.Add(line.TrimEnd(','));
        }

        return entries;
    }

    private static (string Label, string Lua) BuildItemChunk(
        List<string> entries, int chunkNumber, string prefix)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(prefix)) sb.Append(prefix);
        sb.Append("local p = {}\n\np.items = {");

        for (int i = 0; i < entries.Count; i++)
        {
            var comma = i < entries.Count - 1 ? "," : "";
            sb.Append($"\n{entries[i]}{comma}");
        }

        sb.Append("\n}\n\nreturn p");
        return (chunkNumber.ToString(), sb.ToString());
    }

    public sealed record FlatItem(
        string ItemType,
        string Name,
        int Level,
        bool IsGenerator,
        bool IsTemporary,
        string ChainName,
        Dictionary<string, double>? Odds,
        string Description,
        bool HasBubble,
        long BubbleDurationMs,
        int BubbleOpenCost,
        int BubbleSpawnOdds,
        Dictionary<string, double>? ExtraSpawnValues,
        int? SpeedUpCostGems,
        long RechargeTimeMs,
        int Charges,
        int DropsPerCharge,
        long ChargeTimeMs,
        Dictionary<string, int>? Fuels,
        string? FueledResult,
        /// <summary>Single-use drop target ItemType (cycles=1 Constant drop, consumed). Filtered by main-source trump + same-chain.</summary>
        string? SingleUseDrop,
        /// <summary>Decay target ItemType (DecayFeatures or DecayAfterLastCycle Constant). Filtered by main-source trump + same-chain.</summary>
        string? DecayInto,
        /// <summary>Cross-chain merge result ItemType. MergeFeatures.Mechanic.ResultProducer pointing to L1 of a DIFFERENT chain
        /// (e.g. SeedBagEmpty_04 → GoldRoot_01). Same-chain merges (normal L+1 progression) are filtered out — they are implicit
        /// in chain expansion. Used by Lua itemGraph to add a merge edge with sourcesPerOp = 2 (= 2 source items per 1 target).</summary>
        string? MergeResult,
        /// <summary>Multi-target decay odds map (ControlledRandom targets → probability). Used by Markov cycle solver. No filtering.</summary>
        Dictionary<string, double>? DecayOdds,
        /// <summary>Total HowManyCycles for finite generators/spawners (>0). Used by wiki Lua to compute lifetime maxDrops = dropsPerCharge × cycles. 0 = infinite or unset.</summary>
        int Cycles);

    /// <summary>
    /// True iff the item is a "truly infinite producer" — stable main source for its drops/spawns.
    /// Rule: cycles == -1 AND DecayAfterLastCycleProducer field absent in JSON.
    /// </summary>
    private static bool IsTrulyInfiniteProducer(ParsedItem item)
    {
        if (item.IsGenerator
            && item.ActivationHowManyCycles == -1
            && !item.HasDecayAfterLastCycleField)
            return true;
        if (item.IsSpawner
            && item.SpawnHowManyCycles == -1
            && !item.DecaysWhenCyclesAreDone)
            return true;
        return false;
    }

    /// <summary>
    /// Lookup chain (DisplayName) that contains a given ItemType. For same-chain filter.
    /// Skips Test-tagged items so they can't be referenced as legitimate sources.
    /// </summary>
    private static Dictionary<string, string> BuildItemToChainMap(List<ParsedChain> chains)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in chains)
            foreach (var item in chain.Items)
                if (!string.IsNullOrEmpty(item.ItemType) && !item.IsTestTag)
                    map.TryAdd(item.ItemType, chain.DisplayName);
        return map;
    }

    /// <summary>
    /// Transitive closure of "permanent-reachable" chains starting from chains with a truly-infinite
    /// producer. A chain becomes permanent if any permanent chain has a cross-chain relation into it
    /// (Constant drop/decay/spawn, or stochastic output from a truly-infinite producer).
    /// Used to prioritise real merge-game paths over event-only / temporary producers when choosing
    /// the cheapest source for a target.
    /// </summary>
    private static HashSet<string> BuildPermanentChains(
        List<ParsedChain> chains, Dictionary<string, string> itemToChain)
    {
        var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in chains)
            if (chain.Items.Any(it => !it.IsTestTag && IsTrulyInfiniteProducer(it)))
                seeds.Add(chain.DisplayName);

        var edges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void AddEdge(string srcChain, string? tgtItem)
        {
            if (string.IsNullOrEmpty(tgtItem)) return;
            if (!itemToChain.TryGetValue(tgtItem, out var tgtChain)) return;
            if (string.Equals(srcChain, tgtChain, StringComparison.OrdinalIgnoreCase)) return;
            if (!edges.TryGetValue(srcChain, out var set))
                edges[srcChain] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(tgtChain);
        }

        foreach (var chain in chains)
        {
            foreach (var item in chain.Items)
            {
                if (item.IsTestTag) continue;
                bool isInf = IsTrulyInfiniteProducer(item);

                // Drops — all keys if truly-infinite; only Constant (single ≥99.9%) otherwise
                if (item.DropOdds != null)
                {
                    if (isInf) foreach (var k in item.DropOdds.Keys) AddEdge(chain.DisplayName, k);
                    else if (item.DropOdds is { Count: 1 } d && d.First().Value >= 99.9)
                        AddEdge(chain.DisplayName, d.First().Key);
                }
                // Spawns — same rule
                if (item.SpawnOdds != null)
                {
                    if (isInf) foreach (var k in item.SpawnOdds.Keys) AddEdge(chain.DisplayName, k);
                    else if (item.SpawnOdds is { Count: 1 } s && s.First().Value >= 99.9)
                        AddEdge(chain.DisplayName, s.First().Key);
                }
                AddEdge(chain.DisplayName, item.SpawnItemType);
                AddEdge(chain.DisplayName, item.DecayIntoItemType);
                AddEdge(chain.DisplayName, item.DecayAfterLastCycleItemType);
                AddEdge(chain.DisplayName, item.SpawnDecayIntoItemType);
            }
        }

        var result = new HashSet<string>(seeds, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(seeds);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (!edges.TryGetValue(c, out var targets)) continue;
            foreach (var t in targets)
                if (result.Add(t)) queue.Enqueue(t);
        }
        return result;
    }

    /// <summary>
    /// Computes item level in L1 equivalents (= 2^(level-1)).
    /// </summary>
    private static double L1CountByLevel(int level) => level > 0 ? Math.Pow(2, level - 1) : 1;

    /// <summary>
    /// For each target ItemType, finds the source chain with MINIMUM cost per 1 target_L1 (cheapest-source filter).
    /// Cost = sourceItem L1 equivalents / (targetLevel L1 equivalents × dropsPerCharge).
    /// Resolves the "Shrapnel should come from Vase L1 (cheap), not Bottle L8 (128× L1 expensive)" issue.
    /// Permanent-chain priority: a permanent-reachable source trumps non-permanent one regardless of cost —
    /// prevents event-only / standalone chains from being picked just because they happen to tie on per-cycle cost.
    /// Considers only direct (1-hop) relations — multi-hop cascades in wiki Lua BFS follow whichever chain emits the relation.
    /// </summary>
    private static Dictionary<string, string> BuildBestSourceChainMap(
        List<ParsedChain> chains, Dictionary<string, string> itemToChain, HashSet<string> permanentChains)
    {
        // target ItemType → (minCost, chainDisplayName, isPermanent)
        var best = new Dictionary<string, (double cost, string chain, bool isPerm)>(StringComparer.OrdinalIgnoreCase);

        void Consider(string target, double cost, string chain)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(chain)) return;
            bool isPerm = permanentChains.Contains(chain);
            if (!best.TryGetValue(target, out var cur))
            {
                best[target] = (cost, chain, isPerm);
                return;
            }
            // Permanent source always trumps non-permanent, regardless of cost
            if (cur.isPerm && !isPerm) return;
            if (isPerm && !cur.isPerm) { best[target] = (cost, chain, isPerm); return; }
            // Same perm status → cheapest wins
            if (cost < cur.cost) best[target] = (cost, chain, isPerm);
        }

        int TargetLevel(string itemType)
        {
            // Extract level from ItemType suffix "_NN" if present
            var idx = itemType.LastIndexOf('_');
            if (idx > 0 && int.TryParse(itemType.AsSpan(idx + 1), out var lvl)) return lvl;
            return 1;
        }

        foreach (var chain in chains)
        {
            foreach (var item in chain.Items)
            {
                if (string.IsNullOrEmpty(item.ItemType) || item.IsTestTag) continue;
                double srcL1 = L1CountByLevel(item.Level);

                // Single-use drop (cycles != -1 OR DecayAfterLastCycle field present, Constant drop ≥99.9%)
                bool isSingleUseGen = item.IsGenerator
                    && (item.ActivationHowManyCycles != -1 || item.HasDecayAfterLastCycleField);
                if (isSingleUseGen && item.DropOdds is { Count: 1 } odds && odds.First().Value >= 99.9)
                {
                    var target = odds.First().Key;
                    if (!string.Equals(itemToChain.GetValueOrDefault(target), chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        double tgtL1 = L1CountByLevel(TargetLevel(target));
                        int drops = item.HowManyGeneratedInCycle > 0 ? item.HowManyGeneratedInCycle : 1;
                        double cost = srcL1 / (tgtL1 * drops);
                        Consider(target, cost, chain.DisplayName);
                    }
                }

                // Decay (DecayFeatures.ItemProducer Constant)
                if (item.HasDecay && !string.IsNullOrEmpty(item.DecayIntoItemType))
                {
                    var target = item.DecayIntoItemType;
                    if (!string.Equals(itemToChain.GetValueOrDefault(target), chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        double tgtL1 = L1CountByLevel(TargetLevel(target));
                        double cost = srcL1 / tgtL1;
                        Consider(target, cost, chain.DisplayName);
                    }
                }

                // Decay-after-last-cycle Constant (only when item is NOT truly infinite)
                if (item.IsGenerator && !string.IsNullOrEmpty(item.DecayAfterLastCycleItemType)
                    && !IsTrulyInfiniteProducer(item))
                {
                    var target = item.DecayAfterLastCycleItemType;
                    if (!string.Equals(itemToChain.GetValueOrDefault(target), chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        double tgtL1 = L1CountByLevel(TargetLevel(target));
                        double cost = srcL1 / tgtL1;
                        Consider(target, cost, chain.DisplayName);
                    }
                }

                // Spawner — finite/depleting spawner with Constant spawn target
                bool isFiniteSpawner = item.IsSpawner
                    && (item.SpawnHowManyCycles != -1 || item.DecaysWhenCyclesAreDone);
                if (isFiniteSpawner && !string.IsNullOrEmpty(item.SpawnItemType))
                {
                    var target = item.SpawnItemType;
                    if (!string.Equals(itemToChain.GetValueOrDefault(target), chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        double tgtL1 = L1CountByLevel(TargetLevel(target));
                        int drops = item.SpawnAmountInCycle > 0 ? item.SpawnAmountInCycle : 1;
                        double cost = srcL1 / (tgtL1 * drops);
                        Consider(target, cost, chain.DisplayName);
                    }
                }

                // Spawner decay (SpawnFeatures.DecayProducer Constant)
                if (item.IsSpawner && !string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
                {
                    var target = item.SpawnDecayIntoItemType;
                    if (!string.Equals(itemToChain.GetValueOrDefault(target), chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        double tgtL1 = L1CountByLevel(TargetLevel(target));
                        double cost = srcL1 / tgtL1;
                        Consider(target, cost, chain.DisplayName);
                    }
                }
            }
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.chain, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collects target ItemTypes that have a truly-infinite producer (stable main source).
    /// Used to filter out "temporary" producers (single-use drops, decay) when a stable source exists.
    /// Includes stochastic drops from infinite producers (e.g. Toolbox drops Screws at 3-9% per merge
    /// but it's still the designed source) — transient single-use finite gens never reach this list because
    /// IsTrulyInfiniteProducer already filters them out.
    /// </summary>
    private static HashSet<string> BuildMainSourceTargets(List<ParsedChain> chains)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in chains)
            foreach (var item in chain.Items)
            {
                if (item.IsTestTag) continue;
                if (!IsTrulyInfiniteProducer(item)) continue;
                if (item.DropOdds != null)
                    foreach (var k in item.DropOdds.Keys) targets.Add(k);
                if (item.SpawnOdds != null)
                    foreach (var k in item.SpawnOdds.Keys) targets.Add(k);
                if (!string.IsNullOrEmpty(item.SpawnItemType))
                    targets.Add(item.SpawnItemType);
            }
        return targets;
    }

    private static List<FlatItem> BuildFlatItems(List<ParsedChain> chains, bool useRawNames = false)
    {
        // Build NumericConfigKey → ItemType lookup for resolving sink fuel references
        var configKeyToItemType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in chains)
            foreach (var item in chain.Items)
                if (!string.IsNullOrEmpty(item.NumericConfigKey) && !string.IsNullOrEmpty(item.ItemType))
                    configKeyToItemType.TryAdd(item.NumericConfigKey, item.ItemType);

        // Main-source trump rule: targets with stable infinite producer skip temporary (single-use/decay) relations
        var mainSourceTargets = BuildMainSourceTargets(chains);
        var itemToChain = BuildItemToChainMap(chains);
        // Permanent-reachable chains (transitive closure from truly-infinite seeds)
        var permanentChains = BuildPermanentChains(chains, itemToChain);
        // Cheapest-source filter: prefer permanent sources, then minimum L1 cost
        var bestSourceMap = BuildBestSourceChainMap(chains, itemToChain, permanentChains);

        var list = new List<FlatItem>();
        foreach (var chain in chains)
        {
            // Raw mode: use OriginalName from JSON (fallback ConfigKey), ignoring wiki/custom overrides
            var chainName = useRawNames
                ? (!string.IsNullOrEmpty(chain.OriginalName) ? chain.OriginalName : chain.ConfigKey)
                : chain.DisplayName;

            foreach (var item in chain.Items)
            {
                if (string.IsNullOrEmpty(item.ItemType) || item.IsAlias || item.IsTestTag) continue;
                // Resolve generator fields — primary (ActivationFeatures) or secondary (SpawnFeatures)
                int? skipPrice = null;
                long rechargeTime = 0;
                int charges = 0;
                int dropsPerCharge = 0;
                long chargeTime = 0;
                int cycles = 0; // HowManyCycles when finite (>0); 0 means infinite or unset.

                if (item.IsGenerator)
                {
                    // Sentinel detection: 9999 in mini-charge fields = "always-active marker" items
                    // (DiningPianoMetronomeActive_*, LDE_Hopeberry2024_FurnaceProducing_01,
                    //  CBE_SweetMess_ChocoMachineProducing_01, SLBE_Football_Storage_01).
                    // These are stateful event/minigame items, NOT real droppable generators.
                    // Skip drops accounting (charges/dropsPerCharge/rechargeTime stay 0 → not emitted to Lua)
                    // — wiki preserves dash output. Item still added to items table for chain navigation.
                    // See memory/game-mechanic-rules.md.
                    bool isSentinel = item.ActivationAmountInCycle >= 9999
                                   || item.HowManyGeneratedInCycle >= 9999;
                    if (!isSentinel)
                    {
                        skipPrice = item.SpeedUpCostGems;
                        rechargeTime = item.RechargeTimeMs;
                        int totalDropsPerCharge = item.ActivationAmountInCycle * item.HowManyGeneratedInCycle;
                        int storageCharges = totalDropsPerCharge > 0 && item.StorageMax > 0
                            ? item.StorageMax / totalDropsPerCharge : 0;
                        // StartsFull-conditional formula matching MetaMergeChainSerializer.
                        // - sf=true:  raw StorageMax IS per-cycle drops capacity (= reality);
                        //             charges per cycle = StorageMax/dpc.
                        //             Examples: Mane Comb (4/1=4), Water Bucket (30/30=1).
                        // - sf=false: 1 batch-tap per cycle (StorageMax was overridden in
                        //             dumper to hmc × dpc as total drops).
                        //             Examples: Plain Box, White Moth, Suitcase.
                        // - infinite (hmc=-1): per-cycle taps = storageCharges, ALE pokud item
                        //             používá mini-charge mechaniku (StorageMax < dpc, např. Secret
                        //             Code Book: storage=32, dpc=96), storageCharges=0 → wrap to 1.
                        //             Mini-charges jsou interní batching v rámci 1 main charge,
                        //             nepočítají se jako individuální charges. Viz game-mechanic-rules.md.
                        if (item.ActivationHowManyCycles > 0)
                        {
                            charges = item.StartsFull
                                ? Math.Max(1, storageCharges)
                                : 1;
                        }
                        else
                        {
                            charges = Math.Max(1, storageCharges);
                        }
                        dropsPerCharge = totalDropsPerCharge;
                        // InitialCooldown (FirstCycleStartDelayMs) only emitted when significant (> 5s).
                        // - Vanishing/decay items typically have ~50-500ms (engine spin-up) → first charge
                        //   considered ready by player, total recharge = (effectiveCharges - 1) × rechargeTime.
                        // - Vase L1-L3 etc. with real initial cooldown (> 5s) WILL emit, and wiki Module:Items
                        //   adds it to total recharge time.
                        // Threshold chosen by user based on practical gameplay perception.
                        chargeTime = item.FirstCycleStartDelayMs > 5000 ? item.FirstCycleStartDelayMs : 0;
                        if (item.ActivationHowManyCycles > 0)
                            cycles = item.ActivationHowManyCycles;
                    }
                }
                else if (item.IsSpawner)
                {
                    skipPrice = item.SpeedUpCostGems;
                    rechargeTime = item.SpawnDelayMs;
                    charges = item.SpawnAmountInCycle > 0 && item.SpawnStorageMax > 0
                        ? item.SpawnStorageMax / item.SpawnAmountInCycle : 0;
                    dropsPerCharge = item.SpawnAmountInCycle;
                    if (item.SpawnHowManyCycles > 0)
                        cycles = item.SpawnHowManyCycles;
                }

                // Resolve sink fuel references: NumericConfigKey → ItemType
                Dictionary<string, int>? fuels = null;
                string? fueledResult = null;
                if (item.IsSink && item.SinkRequirementAmounts is { Count: > 0 })
                {
                    fuels = new Dictionary<string, int>();
                    foreach (var (configKey, amount) in item.SinkRequirementAmounts)
                    {
                        if (configKeyToItemType.TryGetValue(configKey, out var fuelItemType))
                            fuels[fuelItemType] = amount;
                    }
                    if (fuels.Count == 0) fuels = null;
                    fueledResult = item.SinkRewardItemType;
                }

                // ── Consumption relations (single-use drop + decay) with main-source trump + same-chain filter ──
                string? singleUseDrop = null;
                string? decayInto = null;

                // Single-use drop: generator with finite cycles OR stable-destruct via DecayAfterLastCycle=Empty.
                // Constant drop target only (ControlledRandom → future phase with statistical average).
                bool isSingleUseGenerator = item.IsGenerator
                    && (item.ActivationHowManyCycles != -1 || item.HasDecayAfterLastCycleField);
                if (isSingleUseGenerator
                    && item.DropOdds is { Count: 1 } odds
                    && odds.First().Value >= 99.9) // Constant (single key with ~100% odds)
                {
                    var target = odds.First().Key;
                    if (!mainSourceTargets.Contains(target)
                        && itemToChain.TryGetValue(target, out var targetChain)
                        && !string.Equals(targetChain, chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Cheapest-source filter: only emit if THIS chain is the cheapest source of the target
                        if (bestSourceMap.TryGetValue(target, out var bestSingle)
                            && string.Equals(bestSingle, chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                            singleUseDrop = target;
                    }
                }

                // Spawner single-use drop: finite/depleting spawner with Constant SpawnItemType
                // (equivalent semantic to generator single-use drop)
                bool isFiniteSpawnerItem = item.IsSpawner
                    && (item.SpawnHowManyCycles != -1 || item.DecaysWhenCyclesAreDone);
                if (singleUseDrop == null && isFiniteSpawnerItem && !string.IsNullOrEmpty(item.SpawnItemType))
                {
                    var target = item.SpawnItemType;
                    if (!mainSourceTargets.Contains(target)
                        && itemToChain.TryGetValue(target, out var spawnTargetChain)
                        && !string.Equals(spawnTargetChain, chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (bestSourceMap.TryGetValue(target, out var bestSpawn)
                            && string.Equals(bestSpawn, chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                            singleUseDrop = target;
                    }
                }

                // Decay into: DecayFeatures.ItemProducer Constant (from DecayIntoItemType) OR
                // DecayAfterLastCycle Constant (when item is NOT truly infinite) OR
                // SpawnFeatures.DecayProducer Constant (SpawnDecayIntoItemType)
                string? decayCandidate = null;
                if (item.HasDecay && !string.IsNullOrEmpty(item.DecayIntoItemType))
                    decayCandidate = item.DecayIntoItemType;
                else if (item.IsGenerator && !string.IsNullOrEmpty(item.DecayAfterLastCycleItemType)
                         && !IsTrulyInfiniteProducer(item))
                    decayCandidate = item.DecayAfterLastCycleItemType;
                else if (item.IsSpawner && !string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
                    decayCandidate = item.SpawnDecayIntoItemType;

                if (!string.IsNullOrEmpty(decayCandidate)
                    && itemToChain.TryGetValue(decayCandidate, out var decayChain))
                {
                    bool sameChain = string.Equals(decayChain, chain.DisplayName, StringComparison.OrdinalIgnoreCase);
                    if (sameChain)
                    {
                        // Same-chain decay: emit ONLY FORWARD progressions (target level > source level).
                        // Forward = Sink-Producer ladder where the producer decays into the NEXT processing
                        // stage of the same chain (e.g. Shipping Container: ShippingContainerProducer_01 L2
                        // → ShippingContainer_02 L3 → … → ScarabBox). The Lua solver needs these edges to
                        // walk the chain when reached via a fuel relation from another chain (Tools → SC).
                        // Backward (Wood L4→L3 depletion) and same-level (PoolToys repeatable producer loops
                        // reverting to their Sink form) are NOT forward pipelines and stay filtered to avoid
                        // circular/self-referential cost cascades. Levels are raw game levels; wiki aliases
                        // (mapping table only) don't affect this comparison.
                        var tgtItem = chain.Items.FirstOrDefault(i =>
                            string.Equals(i.ItemType, decayCandidate, StringComparison.OrdinalIgnoreCase));
                        if (tgtItem != null && tgtItem.Level > item.Level)
                            decayInto = decayCandidate;
                    }
                    else if (!mainSourceTargets.Contains(decayCandidate))
                    {
                        // Cross-chain decay: skip when target has a permanent main source elsewhere,
                        // and emit only if THIS chain is the cheapest source of the target.
                        if (bestSourceMap.TryGetValue(decayCandidate, out var bestDecay)
                            && string.Equals(bestDecay, chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                            decayInto = decayCandidate;
                    }
                }

                // ── Cross-chain merge result (MergeFeatures.Mechanic.ResultProducer) ──
                // Emitted only when merge target lives in a DIFFERENT wiki chain. Same-chain
                // L+1 merges are the default progression and don't need a graph edge — the
                // BFS solver already expands through chain levels. Only the chain-terminal
                // cross-chain merge (e.g. SeedBagEmpty_04 → GoldRoot_01, Crate Parts L4 →
                // Recycled Field Table L1) needs to be exposed to the Lua solver so it can
                // back-attribute "Empty Seed Bag is needed for Golden Tree".
                string? mergeResult = null;
                if (!string.IsNullOrEmpty(item.MergeResultItemType)
                    && itemToChain.TryGetValue(item.MergeResultItemType, out var mergeTargetChain)
                    && !string.Equals(mergeTargetChain, chain.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    mergeResult = item.MergeResultItemType;
                }

                list.Add(new FlatItem(
                    item.ItemType,
                    item.Name,
                    item.Level,
                    item.IsGenerator,
                    item.IsTemporary,
                    chainName,
                    item.IsGenerator ? item.DropOdds : null,
                    item.Description,
                    item.HasBubble,
                    item.BubbleDurationMs,
                    item.BubbleOpenCost,
                    item.BubbleSpawnOdds,
                    item.ExtraSpawnValues,
                    skipPrice,
                    rechargeTime,
                    charges,
                    dropsPerCharge,
                    chargeTime,
                    fuels,
                    fueledResult,
                    singleUseDrop,
                    decayInto,
                    mergeResult,
                    item.DecayAfterLastCycleOdds,
                    cycles));
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
            if (it.SpeedUpCostGems.HasValue) sb.Append($"skipPrice = {it.SpeedUpCostGems.Value}, ");
            if (it.RechargeTimeMs > 0) sb.Append($"rechargeTime = {it.RechargeTimeMs}, ");
            if (it.Charges > 0) sb.Append($"charges = {it.Charges}, ");
            if (it.Cycles > 0) sb.Append($"cycles = {it.Cycles}, ");
            if (it.DropsPerCharge > 0) sb.Append($"dropsPerCharge = {it.DropsPerCharge}, ");
            if (it.ChargeTimeMs > 0) sb.Append($"chargeTime = {it.ChargeTimeMs}, ");

            sb.Append($"chainName = \"{Esc(it.ChainName)}\", ");

            // bubble
            if (it.HasBubble)
            {
                var durationMinutes = Math.Round(it.BubbleDurationMs / 1000.0 / 60.0, 0);
                sb.Append($"bubble = {{duration = {durationMinutes:F0}, cost = {it.BubbleOpenCost}, spawnOdds = {it.BubbleSpawnOdds}}}, ");
            }

            // extraSpawn token values
            if (it.ExtraSpawnValues?.Count > 0)
            {
                sb.Append("tokens = {");
                sb.Append(string.Join(", ", it.ExtraSpawnValues.Select(
                    kv => $"{CamelToLua(kv.Key)} = {kv.Value.ToString(CultureInfo.InvariantCulture)}")));
                sb.Append("}, ");
            }

            // fuels + fueledResult (sink/transform items)
            if (it.Fuels is { Count: > 0 })
            {
                sb.Append("fuels = {");
                sb.Append(string.Join(", ", it.Fuels.Select(
                    kv => $"[\"{Esc(kv.Key)}\"] = {{amount = {kv.Value}}}")));
                sb.Append("}, ");
                if (!string.IsNullOrEmpty(it.FueledResult))
                    sb.Append($"fueledResult = \"{Esc(it.FueledResult)}\", ");
            }

            // Consumption relations (Phase 1: single-use drop + decay, filtered by main-source trump + same-chain)
            if (!string.IsNullOrEmpty(it.SingleUseDrop))
                sb.Append($"singleUseDrop = \"{Esc(it.SingleUseDrop)}\", ");
            if (!string.IsNullOrEmpty(it.DecayInto))
                sb.Append($"decayInto = \"{Esc(it.DecayInto)}\", ");
            // Cross-chain merge result — only for items whose MergeFeatures.Mechanic.ResultProducer points
            // to L1 of a different chain. Lua solver attaches an itemGraph edge with sourcesPerOp = 2.
            if (!string.IsNullOrEmpty(it.MergeResult))
                sb.Append($"mergeResult = \"{Esc(it.MergeResult)}\", ");

            // Multi-target decay odds (for Markov cycle solver — no filtering, raw transition probabilities)
            if (it.DecayOdds is { Count: > 0 } decayOdds)
            {
                var orderedDecay = decayOdds
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal);
                sb.Append("decayOdds = {");
                sb.Append(string.Join(", ", orderedDecay.Select(kv =>
                    $"{{id = \"{Esc(kv.Key)}\", value = {kv.Value.ToString(CultureInfo.InvariantCulture)}}}")));
                sb.Append("}, ");
            }

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

    /// <summary>
    /// Builds p.chainNames as a positional list per chain (chain name → array of item ids).
    /// Format: <code>["Chain"] = { "item_01", "item_02", "archived_id" }</code>.
    /// Includes BOTH live items (from <paramref name="items"/>) and archived item ids
    /// (from <paramref name="archivedIdsByChain"/>) so wiki callers can iterate one list and
    /// resolve each id (live → p.items; archived → Archive module via lazy fallback).
    /// Live ids come first, sorted by Level/ItemType (game order); archived ids appended,
    /// sorted alphabetically. ipairs over the result is well-defined and fast.
    /// </summary>
    public static string BuildChainNamesTable(
        List<FlatItem> items,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? archivedIdsByChain = null)
    {
        var sb = new StringBuilder();
        sb.Append("p.chainNames = {");

        // Group live items by chain (live = items present in current dump)
        var liveByChain = items
            .Where(i => !string.IsNullOrWhiteSpace(i.ChainName) && !string.IsNullOrEmpty(i.ItemType))
            .GroupBy(i => i.ChainName)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(x => x.Level)
                .ThenBy(x => x.ItemType, StringComparer.Ordinal)
                .Select(x => x.ItemType)
                .ToList(),
                StringComparer.Ordinal);

        // Union of all chain names (live + archived chains)
        var allChains = new HashSet<string>(liveByChain.Keys, StringComparer.Ordinal);
        if (archivedIdsByChain != null)
            foreach (var k in archivedIdsByChain.Keys) allChains.Add(k);

        var sortedChains = allChains.OrderBy(c => c, StringComparer.Ordinal).ToList();
        for (int gi = 0; gi < sortedChains.Count; gi++)
        {
            var chain = sortedChains[gi];
            var trailingComma = gi < sortedChains.Count - 1 ? "," : "";

            var liveIds = liveByChain.GetValueOrDefault(chain, new List<string>());
            var archivedIds = archivedIdsByChain != null && archivedIdsByChain.TryGetValue(chain, out var arr)
                ? (IReadOnlyList<string>)arr
                : Array.Empty<string>();

            // Combine: live (already sorted) + archived (alphabetical, dedup against live)
            var liveSet = new HashSet<string>(liveIds, StringComparer.Ordinal);
            var combined = new List<string>(liveIds);
            foreach (var id in archivedIds.OrderBy(x => x, StringComparer.Ordinal))
                if (liveSet.Add(id)) combined.Add(id);

            var idsLua = string.Join(", ", combined.Select(id => $"\"{Esc(id)}\""));
            sb.Append($"\n\t[\"{Esc(chain)}\"] = {{ {idsLua} }}{trailingComma}");
        }

        sb.Append("\n}");
        return sb.ToString();
    }

    /// <summary>
    /// Builds <code>p.archived = { ["item_id"] = true, ... }</code> — a flat boolean map of all
    /// currently-archived item ids. Loaded into the Items arbiter alongside p.chainNames so
    /// wiki callers can quickly check `p.archived[id]` without consulting the heavy Archive module.
    /// Empty input yields <c>p.archived = {}</c> (always emitted for consistency).
    /// </summary>
    public static string BuildArchivedFlagsTable(IEnumerable<string> archivedIds)
    {
        var sortedIds = archivedIds
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (sortedIds.Count == 0)
            return "p.archived = {}";

        var sb = new StringBuilder();
        sb.Append("p.archived = {");
        for (int i = 0; i < sortedIds.Count; i++)
        {
            var trailingComma = i < sortedIds.Count - 1 ? "," : "";
            sb.Append($"\n\t[\"{Esc(sortedIds[i])}\"] = true{trailingComma}");
        }
        sb.Append("\n}");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the Module:Datatable/Items/Archive content.
    /// Structure: <code>p.items = { ["Chain"] = { ["item_id"] = { full data }, ... }, ... }</code>.
    /// Each item's full data is the verbatim raw Lua entry as it last appeared in the live module
    /// (preserved at the time of removal so wiki pages keep working).
    /// </summary>
    public static string BuildArchiveModule(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> archivedByChain,
        string? createdAt = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(createdAt))
            sb.AppendLine($"-- createdAt: {createdAt}");
        sb.AppendLine("local str = require('Module:Strings')");
        sb.AppendLine("local p = {}");
        sb.AppendLine();
        sb.Append("p.items = {");

        var sortedChains = archivedByChain.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList();
        for (int gi = 0; gi < sortedChains.Count; gi++)
        {
            var chain = sortedChains[gi];
            var items = archivedByChain[chain];
            if (items.Count == 0) continue;
            var trailingComma = gi < sortedChains.Count - 1 ? "," : "";

            sb.Append($"\n\t[\"{Esc(chain)}\"] = {{");
            var sortedIds = items.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            for (int ei = 0; ei < sortedIds.Count; ei++)
            {
                var id = sortedIds[ei];
                var rawEntry = items[id];
                var entryComma = ei < sortedIds.Count - 1 ? "," : "";
                sb.Append($"\n\t\t[\"{Esc(id)}\"] = {rawEntry}{entryComma}");
            }
            sb.Append($"\n\t}}{trailingComma}");
        }

        sb.Append("\n}\n");
        sb.AppendLine();
        sb.AppendLine("return p");
        return sb.ToString();
    }

    // ── Lua helpers ────────────────────────────────────────────────────

    /// <summary>Converts PascalCase to camelCase for Lua field names.</summary>
    private static string CamelToLua(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];

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
