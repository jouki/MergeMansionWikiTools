using System;
using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// UsesIndexService builds the precomputed area-requirement index (reqByChain + areaChains)
/// that replaces the full Areas scan in Module:Items.GetAllItemUses. Tests cover the index
/// build (requirements vs rewards), the whole-letter balanced sharding, and the router.
/// </summary>
public class UsesIndexServiceTests
{
    private static LuaTask Task(Dictionary<string, int> reqs, string? reward = null) =>
        new() { Requirements = reqs, ItemReward = reward };

    // ── BuildIndexData ────────────────────────────────────────────────

    [Fact]
    public void BuildIndexData_RequirementsGoToReqByChain_RewardsOnlyToPresence()
    {
        var areas = new List<LuaArea>
        {
            new() { DisplayName = "Attic", Tasks = { Task(new() { ["Item_A_01"] = 3, ["Item_A_02"] = 1 }, reward: "Item_B_01") } },
            new() { DisplayName = "Cinema", Tasks = { Task(new() { ["Item_A_01"] = 2 }) } },
        };
        string? ChainOf(string it) => it.StartsWith("Item_A") ? "Chain A" : it.StartsWith("Item_B") ? "Chain B" : null;
        int LevelOf(string it) => int.Parse(it[^2..]);

        var (reqByChain, areaChains) = new UsesIndexService().BuildIndexData(areas, ChainOf, LevelOf);

        // Requirements aggregate under their chain.
        Assert.Equal(3, reqByChain["Chain A"].Count);
        Assert.Contains(reqByChain["Chain A"], r => r.Area == "Attic" && r.Level == 1 && r.Amount == 3);
        Assert.Contains(reqByChain["Chain A"], r => r.Area == "Attic" && r.Level == 2 && r.Amount == 1);
        Assert.Contains(reqByChain["Chain A"], r => r.Area == "Cinema" && r.Level == 1 && r.Amount == 2);
        // A reward-only chain is NOT a requirement → not in reqByChain.
        Assert.False(reqByChain.ContainsKey("Chain B"));

        // Presence includes both requirement chains AND reward chains.
        Assert.Equal(new[] { "Chain A", "Chain B" }, areaChains["Attic"].ToArray());
        Assert.Equal(new[] { "Chain A" }, areaChains["Cinema"].ToArray());
    }

    [Fact]
    public void BuildIndexData_SkipsUnresolvableItemTypes()
    {
        var areas = new List<LuaArea>
        {
            new() { DisplayName = "Tomb", Tasks = { Task(new() { ["Known_01"] = 1, ["Unknown_01"] = 5 }) } },
        };
        var (reqByChain, areaChains) = new UsesIndexService().BuildIndexData(
            areas, it => it.StartsWith("Known") ? "Known Chain" : null, _ => 1);

        Assert.Single(reqByChain);
        Assert.True(reqByChain.ContainsKey("Known Chain"));
        Assert.Equal(new[] { "Known Chain" }, areaChains["Tomb"].ToArray());
    }

    [Fact]
    public void BuildIndexData_StateVariantChain_AlsoRegistersBaseChainInPresence()
    {
        // Mirrors Lua addChainToSet: "Construction Kit (Jammed)" must also register "Construction Kit"
        // so the Phase-4 producer-presence filter matches either the variant or the base.
        var areas = new List<LuaArea>
        {
            new() { DisplayName = "Factory", Tasks = { Task(new() { ["CK_Jammed_01"] = 1 }) } },
        };
        var (reqByChain, areaChains) = new UsesIndexService().BuildIndexData(
            areas, _ => "Construction Kit (Jammed)", _ => 1);

        Assert.Contains("Construction Kit (Jammed)", areaChains["Factory"]);
        Assert.Contains("Construction Kit", areaChains["Factory"]); // base name added
        // reqByChain stays keyed by the full (variant) chain name.
        Assert.True(reqByChain.ContainsKey("Construction Kit (Jammed)"));
        Assert.False(reqByChain.ContainsKey("Construction Kit"));
    }

    // ── BalancedContiguousCuts ────────────────────────────────────────

    [Fact]
    public void BalancedContiguousCuts_EqualSizes_SplitsEvenly()
    {
        var cuts = UsesIndexService.BalancedContiguousCuts(new long[] { 10, 10, 10, 10 }, 2);
        Assert.Equal(new[] { 2 }, cuts); // [0,1] | [2,3]

        var cuts4 = UsesIndexService.BalancedContiguousCuts(new long[] { 10, 10, 10, 10 }, 4);
        Assert.Equal(new[] { 1, 2, 3 }, cuts4);
    }

    [Fact]
    public void BalancedContiguousCuts_UnevenSizes_MinimisesLargestGroup()
    {
        // sizes: A=100, B=1, C=1, D=100  → best split into 2 is [A] | [B,C,D]=102  vs  [A,B,C]=102 | [D].
        // Either gives max 102; DP picks the first feasible. Just assert the largest group <= naive midpoint split.
        var sizes = new long[] { 100, 1, 1, 100 };
        var cuts = UsesIndexService.BalancedContiguousCuts(sizes, 2);
        Assert.Single(cuts);
        // verify the resulting max group sum is optimal (101, not 200 from a bad split)
        int cut = cuts[0];
        long left = sizes.Take(cut).Sum();
        long right = sizes.Skip(cut).Sum();
        Assert.True(Math.Max(left, right) <= 101, $"max group {Math.Max(left, right)} not minimal");
    }

    // ── Router ────────────────────────────────────────────────────────

    [Fact]
    public void EmitRouter_ProducesContiguousLetterBranches()
    {
        var router = UsesIndexService.EmitRouter(new[] { 'D', 'G', 'R' });
        Assert.Contains("if c <= \"D\" then return 1", router);
        Assert.Contains("elseif c <= \"G\" then return 2", router);
        Assert.Contains("elseif c <= \"R\" then return 3", router);
        Assert.Contains("else return 4 end", router);
    }

    // ── EmitShards (whole letters + router consistency) ───────────────

    [Fact]
    public void EmitShards_WholeLettersPerShard_AndRouterMapsEachChain()
    {
        var reqByChain = new SortedDictionary<string, List<UsesIndexService.ReqRow>>(StringComparer.Ordinal)
        {
            ["Apple"] = new() { new("Attic", 1, 2, "Apple_01") },
            ["Avocado"] = new() { new("Attic", 1, 1, "Avocado_01") },
            ["Banana"] = new() { new("Cinema", 2, 3, "Banana_02") },
            ["Mango"] = new() { new("Tomb", 1, 1, "Mango_01") },
            ["Zebra"] = new() { new("Zoo", 1, 1, "Zebra_01") },
        };
        var areaChains = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        var gen = new UsesIndexService().EmitShards(reqByChain, areaChains, "2026-01-01", "2026-01-02", "v9.9.9", shardCount: 2);

        // Two shards, contiguous whole-letter ranges, no overlap.
        Assert.Equal(2, gen.Shards.Count);
        Assert.True(gen.Shards[0].LastLetter < gen.Shards[1].FirstLetter,
            "letters must not span shards");

        // Each chain's shard (per the router boundaries) actually contains that chain's data.
        var bounds = gen.ShardUpperBounds;
        int ShardOf(string chain)
        {
            char c = char.ToUpperInvariant(chain[0]);
            for (int i = 0; i < bounds.Count; i++) if (c <= bounds[i]) return i + 1;
            return bounds.Count + 1;
        }
        foreach (var chain in reqByChain.Keys)
        {
            int sh = ShardOf(chain);
            Assert.Contains("[\"" + chain + "\"]", gen.Shards[sh - 1].Lua);
        }

        // Rows serialized correctly + header stamps present.
        Assert.Contains("{area=\"Attic\",level=1,amount=2,item=\"Apple_01\"}", gen.Shards[ShardOf("Apple") - 1].Lua);
        Assert.Contains("-- sourceItems: 2026-01-01", gen.Shards[0].Lua);
        Assert.Contains("-- sourceAreas: 2026-01-02", gen.Shards[0].Lua);
        Assert.Equal(5, gen.ChainCount);
        Assert.Equal(5, gen.RowCount);
    }

    [Fact]
    public void EmitShards_AreaChainsModule_ListsPresentChains()
    {
        var reqByChain = new SortedDictionary<string, List<UsesIndexService.ReqRow>>(StringComparer.Ordinal)
        {
            ["Apple"] = new() { new("Attic", 1, 1, "Apple_01") },
        };
        var areaChains = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            ["Attic"] = new(StringComparer.Ordinal) { "Apple", "Banana" },
        };
        var gen = new UsesIndexService().EmitShards(reqByChain, areaChains, "i", "a", "v");
        Assert.Contains("areaChains", gen.AreaChainsLua);
        Assert.Contains("[\"Attic\"] = {\"Apple\",\"Banana\"}", gen.AreaChainsLua);
        Assert.Equal(1, gen.AreaCount);
        // Single shard (one letter 'A') → no router boundaries.
        Assert.Contains("shardBounds = {}", gen.AreaChainsLua);
    }

    [Fact]
    public void EmitShards_AreaChainsModule_CarriesShardBounds()
    {
        // Five letters across 2 shards → exactly one boundary letter, co-located in /Areas.
        var reqByChain = new SortedDictionary<string, List<UsesIndexService.ReqRow>>(StringComparer.Ordinal)
        {
            ["Apple"] = new() { new("Attic", 1, 1, "Apple_01") },
            ["Banana"] = new() { new("Attic", 1, 1, "Banana_01") },
            ["Mango"] = new() { new("Tomb", 1, 1, "Mango_01") },
            ["Zebra"] = new() { new("Zoo", 1, 1, "Zebra_01") },
        };
        var areaChains = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var gen = new UsesIndexService().EmitShards(reqByChain, areaChains, "i", "a", "v", shardCount: 2);

        Assert.Single(gen.ShardUpperBounds);
        char b = gen.ShardUpperBounds[0];
        Assert.Contains($"shardBounds = {{\"{b}\"}}", gen.AreaChainsLua);
    }
}
