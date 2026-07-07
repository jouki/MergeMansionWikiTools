using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>AtlasStitcher maps chain items to their atlas textures via each item's PoolTag → the
/// game's PoolTag→texture map, and detects chains whose items span multiple textures (e.g.
/// InfiniteEnergy: LimitedItemInfiniteEnergyA/B/C → UnlimitedEnergyD/C/A).</summary>
public class AtlasStitcherTests
{
    private static ParsedChain Chain(string cfg, params (int lvl, string itemType, string poolTag)[] items)
    {
        var chain = new ParsedChain { ConfigKey = cfg };
        foreach (var i in items)
            chain.Items.Add(new ParsedItem { Level = i.lvl, ItemType = i.itemType, PoolTag = i.poolTag });
        return chain;
    }

    private static System.Func<string, string?> Map(Dictionary<string, string> m) =>
        pt => m.TryGetValue(pt, out var v) ? v : null;

    [Fact]
    public void ResolveItemTextures_MapsEachItemToItsOwnTexture_ViaPoolTag()
    {
        // Real InfiniteEnergy data: each duration item carries its own PoolTag → its own atlas.
        var chain = Chain("InfiniteEnergy",
            (1, "InfiniteEnergySmall_01", "LimitedItemInfiniteEnergyC"),
            (2, "InfiniteEnergyMid_01", "LimitedItemInfiniteEnergyB"),
            (3, "InfiniteEnergyBig_01", "LimitedItemInfiniteEnergyA"));
        var poolTagToTexture = Map(new()
        {
            ["LimitedItemInfiniteEnergyA"] = "UnlimitedEnergyD",
            ["LimitedItemInfiniteEnergyB"] = "UnlimitedEnergyC",
            ["LimitedItemInfiniteEnergyC"] = "UnlimitedEnergyA",
        });

        var refs = AtlasStitcher.ResolveItemTextures(chain, poolTagToTexture);

        Assert.Equal(3, refs.Count);
        Assert.Equal("UnlimitedEnergyA", refs.Single(r => r.Level == 1).TextureName);
        Assert.Equal("UnlimitedEnergyC", refs.Single(r => r.Level == 2).TextureName);
        Assert.Equal("UnlimitedEnergyD", refs.Single(r => r.Level == 3).TextureName);
        Assert.True(AtlasStitcher.IsMultiTexture(refs));
        // Distinct textures ordered by ascending level: A (L1), C (L2), D (L3).
        Assert.Equal(new[] { "UnlimitedEnergyA", "UnlimitedEnergyC", "UnlimitedEnergyD" },
            AtlasStitcher.DistinctTextures(refs));
    }

    [Fact]
    public void ResolveItemTextures_SingleTexture_IsNotMulti()
    {
        // Normal chain: every item shares one PoolTag → one texture.
        var chain = Chain("Detergent",
            (1, "Detergent_01", "Detergent"), (2, "Detergent_02", "Detergent"), (8, "Detergent_08", "Detergent"));
        var poolTagToTexture = Map(new() { ["Detergent"] = "ItemDetergent" });

        var refs = AtlasStitcher.ResolveItemTextures(chain, poolTagToTexture);

        Assert.Equal(3, refs.Count);
        Assert.All(refs, r => Assert.Equal("ItemDetergent", r.TextureName));
        Assert.False(AtlasStitcher.IsMultiTexture(refs));
        Assert.Single(AtlasStitcher.DistinctTextures(refs));
    }

    [Fact]
    public void ResolveItemTextures_ItemWithUnresolvablePoolTag_IsSkipped()
    {
        var chain = Chain("X", (1, "X_01", "PoolX"), (2, "Y_02", "PoolUnknown"), (3, "Z_03", ""));
        var poolTagToTexture = Map(new() { ["PoolX"] = "ItemX" });

        var refs = AtlasStitcher.ResolveItemTextures(chain, poolTagToTexture);

        Assert.Single(refs);
        Assert.Equal(1, refs[0].Level);
        Assert.Equal("ItemX", refs[0].TextureName);
    }
}
