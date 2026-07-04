using System.Text;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Generates the precomputed "area requirement index" that lets <c>Module:Items.GetAllItemUses</c>
/// look up where a chain is consumed WITHOUT scanning the full ~26&#160;MB <c>Module:Datatable/Areas</c>
/// (the live scan peaked Lua memory near the 100&#160;MB limit; <c>collectgarbage</c> is blocked in
/// Scribunto and Fandom has no Cargo/DB store, so the only lever is loading less data).
///
/// <para>Two structures, both keyed by the DISAMBIGUATED chain display name (the same value the live
/// <c>resolveItem(itemType).chainName</c> returns):</para>
/// <list type="bullet">
/// <item><b>reqByChain</b>: chain → requirement occurrences <c>{area, level, amount}</c> (Phase&#160;4 attribution).</item>
/// <item><b>areaChains</b>: area → set of chains present via requirements + rewards (Phase&#160;4 producer-presence filter).</item>
/// </list>
///
/// <para><b>reqByChain</b> is sharded by chain first letter — WHOLE letters per shard (a letter never
/// spans shards, so a chain name maps to its shard by first letter alone), balanced ~evenly by
/// serialized size. <b>areaChains</b> is global (consulted for any chain) so it lives in one
/// non-sharded module.</para>
///
/// <para><b>Bit-identical caveats to verify in the wiki-integration phase:</b> the area key uses
/// <see cref="LuaArea.DisplayName"/>; chain names come from
/// <see cref="DataService.ResolveChainDisplayNameFromItemType"/>; level mirrors the Lua
/// resolveItem merge (mapping level overrides JSON level).</para>
/// </summary>
public class UsesIndexService
{
    public const string BaseTitle = "Module:Datatable/Items/UsesIndex";
    public const string AreaChainsTitle = BaseTitle + "/Areas";

    public readonly record struct ReqRow(string Area, int Level, int Amount, string ItemType);

    public sealed record IndexShard(int Number, char FirstLetter, char LastLetter, string Title, string Lua);

    public sealed record GeneratedUsesIndex(
        List<IndexShard> Shards,
        string AreaChainsLua,
        string RouterLua,
        IReadOnlyList<char> ShardUpperBounds,
        int ChainCount,
        int RowCount,
        int AreaCount);

    /// <summary>
    /// Builds the raw index data (no Lua emission). Decoupled from <see cref="DataService"/> via
    /// resolver delegates so it is unit-testable. <paramref name="chainOf"/> returns the
    /// disambiguated chain display name for an itemType (null = skip); <paramref name="levelOf"/>
    /// returns the item's level.
    /// </summary>
    public (SortedDictionary<string, List<ReqRow>> reqByChain,
            SortedDictionary<string, SortedSet<string>> areaChains)
        BuildIndexData(IReadOnlyList<LuaArea> areas, Func<string, string?> chainOf, Func<string, int> levelOf)
    {
        var reqByChain = new SortedDictionary<string, List<ReqRow>>(StringComparer.Ordinal);
        var areaChains = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        string? ChainOf(string? itemType)
        {
            if (string.IsNullOrEmpty(itemType)) return null;
            var cn = chainOf(itemType!);
            return string.IsNullOrEmpty(cn) ? null : cn;
        }
        int LevelOf(string itemType) => levelOf(itemType);

        // Mirror the Lua addChainToSet: add the chain AND its base name (strip a " (...)" suffix), so a
        // state-variant chain like "Construction Kit (Jammed)" also registers "Construction Kit".
        static void AddChainToSet(SortedSet<string> set, string chainName)
        {
            set.Add(chainName);
            int p = chainName.IndexOf(" (", StringComparison.Ordinal);
            if (p > 0) set.Add(chainName[..p]);
        }

        foreach (var area in areas)
        {
            // Match the area name the live Module:Datatable/Areas uses as its table key (and that
            // Module:Items.GetAllItemUses reads as areaName): a few raw game names carry a stray
            // leading/trailing space (e.g. " Walk-in Closet") that the dumped key is trimmed of —
            // without this the index area wouldn't resolve in areaMappings → that area silently
            // dropped from the uses table.
            var an = area.DisplayName?.Trim();
            if (string.IsNullOrEmpty(an)) continue;
            if (!areaChains.TryGetValue(an, out var present))
                areaChains[an] = present = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var task in area.Tasks)
            {
                foreach (var kv in task.Requirements)
                {
                    var cn = ChainOf(kv.Key);
                    if (cn == null) continue;
                    AddChainToSet(present, cn);
                    if (!reqByChain.TryGetValue(cn, out var rows))
                        reqByChain[cn] = rows = new List<ReqRow>();
                    rows.Add(new ReqRow(an, LevelOf(kv.Key), kv.Value, kv.Key)); // kv.Key = itemType (for direct uses)
                }
                var rewardChain = ChainOf(task.ItemReward);
                if (rewardChain != null) AddChainToSet(present, rewardChain);
            }
        }
        return (reqByChain, areaChains);
    }

    /// <summary>Generates the sharded index modules + router from loaded data.</summary>
    public GeneratedUsesIndex Generate(
        DataService data, AreasService areas, WikiMappingCache? mapping,
        string sourceItemsCreatedAt, string sourceAreasCreatedAt, string mmwtVersion,
        int shardCount = 4)
    {
        var (reqByChain, areaChains) = BuildIndexData(
            areas.Areas,
            it => { var cn = data.ResolveChainDisplayNameFromItemType(it, mapping); return string.IsNullOrEmpty(cn) ? null : cn; },
            it =>
            {
                // Mirrors Lua resolveItem: r.level = mapping.level (if present) else raw item level.
                if (mapping != null && mapping.Mappings.TryGetValue(it, out var e) && e.Level is int lv) return lv;
                return data.ItemLevels.TryGetValue(it, out var jl) ? jl : 1;
            });
        return EmitShards(reqByChain, areaChains, sourceItemsCreatedAt, sourceAreasCreatedAt, mmwtVersion, shardCount);
    }

    /// <summary>Emits the sharded index modules + areaChains module + router from raw index data.
    /// Decoupled from <see cref="DataService"/> so the full emission is unit-testable.</summary>
    public GeneratedUsesIndex EmitShards(
        SortedDictionary<string, List<ReqRow>> reqByChain,
        SortedDictionary<string, SortedSet<string>> areaChains,
        string sourceItemsCreatedAt, string sourceAreasCreatedAt, string mmwtVersion,
        int shardCount = 4)
    {
        int rowCount = reqByChain.Values.Sum(v => v.Count);

        // Per-chain serialized line + its first letter bucket.
        var chainLine = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (chain, rows) in reqByChain)
            chainLine[chain] = EmitChainLine(chain, rows);

        // Distinct first-letter buckets in sorted order.
        char Bucket(string name)
        {
            var c = name.Length > 0 ? char.ToUpperInvariant(name[0]) : '#';
            return (c >= 'A' && c <= 'Z') ? c : '#';
        }
        var sizeByLetter = new SortedDictionary<char, long>();
        var chainsByLetter = new SortedDictionary<char, List<string>>();
        foreach (var (chain, line) in chainLine)
        {
            var b = Bucket(chain);
            sizeByLetter[b] = sizeByLetter.GetValueOrDefault(b) + Encoding.UTF8.GetByteCount(line);
            if (!chainsByLetter.TryGetValue(b, out var list)) chainsByLetter[b] = list = new List<string>();
            list.Add(chain);
        }
        var letters = sizeByLetter.Keys.ToList();
        var letterSizes = letters.Select(l => sizeByLetter[l]).ToList();

        // Partition contiguous letters into `shardCount` groups minimising the largest group size.
        var cuts = BalancedContiguousCuts(letterSizes, Math.Min(shardCount, letters.Count));

        var shards = new List<IndexShard>();
        var upperBounds = new List<char>();
        int groupStart = 0;
        for (int g = 0; g < cuts.Count + 1; g++)
        {
            int groupEnd = g < cuts.Count ? cuts[g] : letters.Count; // exclusive
            var groupLetters = letters.GetRange(groupStart, groupEnd - groupStart);
            int num = g + 1;
            var sb = new StringBuilder();
            AppendHeader(sb, sourceAreasCreatedAt, mmwtVersion, sourceItemsCreatedAt, sourceAreasCreatedAt);
            sb.Append("return {\n  reqByChain = {\n");
            foreach (var letter in groupLetters)
                foreach (var chain in chainsByLetter[letter])
                    sb.Append("    ").Append(chainLine[chain]).Append('\n');
            sb.Append("  },\n}\n");
            shards.Add(new IndexShard(num, groupLetters[0], groupLetters[^1],
                $"{BaseTitle}/{num}", sb.ToString()));
            if (g < cuts.Count) upperBounds.Add(groupLetters[^1]);
            groupStart = groupEnd;
        }

        var areaChainsLua = EmitAreaChains(areaChains, upperBounds, sourceItemsCreatedAt, sourceAreasCreatedAt, mmwtVersion);
        var routerLua = EmitRouter(upperBounds);

        return new GeneratedUsesIndex(shards, areaChainsLua, routerLua, upperBounds,
            reqByChain.Count, rowCount, areaChains.Count);
    }

    // ── Lua emission ──────────────────────────────────────────────────

    private static string LuaStr(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string EmitChainLine(string chain, List<ReqRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(LuaStr(chain)).Append("] = {");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = rows[i];
            sb.Append("{area=").Append(LuaStr(r.Area))
              .Append(",level=").Append(r.Level)
              .Append(",amount=").Append(r.Amount)
              .Append(",item=").Append(LuaStr(r.ItemType)).Append('}');
        }
        sb.Append("},");
        return sb.ToString();
    }

    private static string EmitAreaChains(
        SortedDictionary<string, SortedSet<string>> areaChains,
        IReadOnlyList<char> shardUpperBounds,
        string sourceItems, string sourceAreas, string mmwtVersion)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, sourceAreas, mmwtVersion, sourceItems, sourceAreas);
        sb.Append("return {\n");
        // Shard router bounds: Module:Items reads these to route a chain to its UsesIndex/N shard.
        // Co-located with /Areas (which GetAllItemUses already loads) so the dynamic boundaries
        // travel WITH the index — a hardcoded router in Module:Items would silently drift on regen.
        sb.Append("  shardBounds = {");
        for (int i = 0; i < shardUpperBounds.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(LuaStr(shardUpperBounds[i].ToString()));
        }
        sb.Append("},\n");
        sb.Append("  areaChains = {\n");
        foreach (var (area, chains) in areaChains)
        {
            sb.Append("    [").Append(LuaStr(area)).Append("] = {");
            bool first = true;
            foreach (var chain in chains)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(LuaStr(chain));
            }
            sb.Append("},\n");
        }
        sb.Append("  },\n}\n");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string createdAt, string mmwtVersion,
        string sourceItems, string sourceAreas)
    {
        sb.Append("-- CreatedAt: ").Append(createdAt).Append('\n');
        sb.Append("-- mmwtVersion: ").Append(mmwtVersion).Append('\n');
        sb.Append("-- sourceItems: ").Append(sourceItems).Append('\n');
        sb.Append("-- sourceAreas: ").Append(sourceAreas).Append('\n');
    }

    /// <summary>Emits the Lua <c>shardOf(chain)</c> router from the per-shard upper-bound letters.</summary>
    public static string EmitRouter(IReadOnlyList<char> upperBounds)
    {
        var sb = new StringBuilder();
        sb.Append("local function shardOf(chain)\n");
        sb.Append("\tlocal c = string.upper(string.sub(chain, 1, 1))\n");
        for (int i = 0; i < upperBounds.Count; i++)
        {
            sb.Append(i == 0 ? "\tif" : "\telseif");
            sb.Append(" c <= \"").Append(upperBounds[i]).Append("\" then return ").Append(i + 1).Append('\n');
        }
        sb.Append("\telse return ").Append(upperBounds.Count + 1).Append(" end\n");
        sb.Append("end\n");
        return sb.ToString();
    }

    // ── Balanced contiguous partition ─────────────────────────────────

    /// <summary>
    /// Splits a sequence of sizes into <paramref name="k"/> contiguous groups minimising the
    /// largest group sum (classic split-array DP). Returns the exclusive cut indices (k-1 of them).
    /// </summary>
    public static List<int> BalancedContiguousCuts(IReadOnlyList<long> sizes, int k)
    {
        int n = sizes.Count;
        if (k <= 1 || n == 0) return new List<int>();
        if (k >= n) return Enumerable.Range(1, n - 1).ToList(); // one letter per group, capped

        var prefix = new long[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + sizes[i];
        long Sum(int a, int b) => prefix[b] - prefix[a]; // [a, b)

        // dp[i][j] = min largest-sum splitting first i letters into j groups; back[i][j] = chosen cut.
        var dp = new long[n + 1, k + 1];
        var back = new int[n + 1, k + 1];
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= k; j++) dp[i, j] = long.MaxValue;
        dp[0, 0] = 0;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= Math.Min(k, i); j++)
            {
                for (int m = j - 1; m < i; m++)
                {
                    if (dp[m, j - 1] == long.MaxValue) continue;
                    long cand = Math.Max(dp[m, j - 1], Sum(m, i));
                    if (cand < dp[i, j]) { dp[i, j] = cand; back[i, j] = m; }
                }
            }
        }
        // Backtrack the k-1 cut points.
        var cuts = new List<int>();
        int cur = n, grp = k;
        while (grp > 1)
        {
            int m = back[cur, grp];
            cuts.Add(m);
            cur = m; grp--;
        }
        cuts.Reverse();
        return cuts;
    }
}
