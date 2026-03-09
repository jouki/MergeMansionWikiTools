using System.Globalization;
using System.Text;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public record ItemChunksResult(
    List<(string Label, string Lua)> Chunks,
    int FirstEventChunkIndex);  // 1-based; 0 = no event chunks

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
                FirstEventChunkIndex: 0);
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

        return new ItemChunksResult(result, firstEventChunkIndex);
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

    private sealed record FlatItem(
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
        long ChargeTimeMs);

    private static List<FlatItem> BuildFlatItems(List<ParsedChain> chains, bool useRawNames = false)
    {
        var list = new List<FlatItem>();
        foreach (var chain in chains)
        {
            // Raw mode: use OriginalName from JSON (fallback ConfigKey), ignoring wiki/custom overrides
            var chainName = useRawNames
                ? (!string.IsNullOrEmpty(chain.OriginalName) ? chain.OriginalName : chain.ConfigKey)
                : chain.DisplayName;

            foreach (var item in chain.Items)
            {
                if (string.IsNullOrEmpty(item.ItemType)) continue;
                // Resolve generator fields — primary (ActivationFeatures) or secondary (SpawnFeatures)
                int? skipPrice = null;
                long rechargeTime = 0;
                int charges = 0;
                int dropsPerCharge = 0;
                long chargeTime = 0;

                if (item.IsGenerator)
                {
                    skipPrice = item.SpeedUpCostGems;
                    rechargeTime = item.RechargeTimeMs;
                    charges = item.ActivationAmountInCycle > 0 && item.StorageMax > 0
                        ? item.StorageMax / item.ActivationAmountInCycle : 0;
                    dropsPerCharge = item.ActivationAmountInCycle;
                    chargeTime = item.FirstCycleStartDelayMs;
                }
                else if (item.IsSpawner)
                {
                    skipPrice = item.SpeedUpCostGems;
                    rechargeTime = item.SpawnDelayMs;
                    charges = item.SpawnAmountInCycle > 0 && item.SpawnStorageMax > 0
                        ? item.SpawnStorageMax / item.SpawnAmountInCycle : 0;
                    dropsPerCharge = item.SpawnAmountInCycle;
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
                    chargeTime));
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
