using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;
using static MergeMansionWikiTools.Services.AssetExtractionService;

namespace MergeMansionWikiTools.Tests;

/// <summary>PredictIndicesFromSkinMapping must order sprites the SAME way the flood-fill display does
/// (OrderObjects/SplitIntoObjectRows: visual rows top→bottom, within a row left→right). Otherwise the
/// index string points at the wrong visual slot even when the sprite choice is correct.</summary>
public class PredictIndicesTests
{
    [Fact]
    public void OrdersSpritesInOneVisualRowLeftToRight_DespiteSmallYDifference()
    {
        // Real CSE_SoloMilestone_Chest layout: 3 chests on ONE visual row (RectY 14/14/2 — a few px
        // apart relative to ~120px height), the item uses SkinName "1" → Chest_1 (rightmost, x=282).
        // Correct: level 1 on the LAST (rightmost) slot → index array [0, 0, 1] → string "- - 1".
        const string tex = "CSE_SoloMilestone_Chest";
        var sprites = new List<SpriteInfo>
        {
            new("CSE_SoloMilestone_Chest_1", tex, 282, 14, 136, 116),
            new("CSE_SoloMilestone_Chest_3", tex, 148, 14, 132, 116),
            new("CSE_SoloMilestone_Chest_2", tex, 2, 2, 144, 128),
        };
        var skins = new List<SkinMapping>
        {
            new(tex, "1", "CSE_SoloMilestone_Chest_1"),
            new(tex, "2", "CSE_SoloMilestone_Chest_2"),
            new(tex, "3", "CSE_SoloMilestone_Chest_3"),
        };
        var items = new List<ParsedItem> { new() { Level = 1, SkinName = "1" } };

        var result = SpriteMetadataService.PredictIndicesFromSkinMapping(sprites, items, skins, tex);

        Assert.NotNull(result);
        Assert.Equal(new[] { 0, 0, 1 }, result);
    }

    [Fact]
    public void OverInclusiveMerge_MinoritySpriteAtSameLevelIsDashed()
    {
        // "Teatime Reward Box" = wiki-merge of 9 same-named reward boxes, all "level 1": 8 items map
        // to sprite "1" (Chest_1, rightmost), 1 stray maps to sprite "3" (Chest_3, middle). The stray
        // is a different box wrongly merged → dash it. Only the majority sprite gets level 1 → [0,0,1]
        // → "- - 1" in reading order (Chest_2, Chest_3, Chest_1).
        const string tex = "CSE_SoloMilestone_Chest";
        var sprites = new List<SpriteInfo>
        {
            new("CSE_SoloMilestone_Chest_1", tex, 282, 14, 136, 116),
            new("CSE_SoloMilestone_Chest_3", tex, 148, 14, 132, 116),
            new("CSE_SoloMilestone_Chest_2", tex, 2, 2, 144, 128),
        };
        var skins = new List<SkinMapping>
        {
            new(tex, "1", "CSE_SoloMilestone_Chest_1"),
            new(tex, "2", "CSE_SoloMilestone_Chest_2"),
            new(tex, "3", "CSE_SoloMilestone_Chest_3"),
        };
        var items = new List<ParsedItem>();
        for (int i = 0; i < 8; i++) items.Add(new ParsedItem { Level = 1, SkinName = "1" });
        items.Add(new ParsedItem { Level = 1, SkinName = "3" }); // the stray outlier

        var result = SpriteMetadataService.PredictIndicesFromSkinMapping(sprites, items, skins, tex);

        Assert.Equal(new[] { 0, 0, 1 }, result);
    }

    [Fact]
    public void PrimaryItemBeatsAliasMajority()
    {
        // The PRIMARY (non-alias) item defines the chain's sprite even when aliases outnumber it and
        // map to a different sprite (real "Teatime Reward Box": primary Chest1 → sprite "1", aliases
        // include one mapping to sprite "3"). Only the primary's sprite gets the level.
        const string tex = "Box";
        var sprites = new List<SpriteInfo>
        {
            new("Box_1", tex, 10, 10, 50, 50),   // primary's sprite (left)
            new("Box_2", tex, 100, 10, 50, 50),  // aliases' sprite (right)
        };
        var skins = new List<SkinMapping> { new(tex, "1", "Box_1"), new(tex, "2", "Box_2") };
        var items = new List<ParsedItem> { new() { Level = 1, SkinName = "1", IsAlias = false } };
        for (int i = 0; i < 5; i++) items.Add(new ParsedItem { Level = 1, SkinName = "2", IsAlias = true });

        var result = SpriteMetadataService.PredictIndicesFromSkinMapping(sprites, items, skins, tex);

        Assert.Equal(new[] { 1, 0 }, result); // Box_1 (primary) = level 1, Box_2 (alias-only) = dash
    }

    [Fact]
    public void SameLevelVariants_TieKeepsAll()
    {
        // Genuine variants at ONE level (e.g. Flower Bed L6 A/B/C): 1 item each → no majority → keep
        // all three, so variant chains don't lose sprites.
        const string tex = "FlowerBed";
        var sprites = new List<SpriteInfo>
        {
            new("FlowerBed_A", tex, 10, 10, 50, 50),
            new("FlowerBed_B", tex, 70, 10, 50, 50),
            new("FlowerBed_C", tex, 130, 10, 50, 50),
        };
        var skins = new List<SkinMapping>
        {
            new(tex, "A", "FlowerBed_A"), new(tex, "B", "FlowerBed_B"), new(tex, "C", "FlowerBed_C"),
        };
        var items = new List<ParsedItem>
        {
            new() { Level = 6, SkinName = "A" }, new() { Level = 6, SkinName = "B" }, new() { Level = 6, SkinName = "C" },
        };

        var result = SpriteMetadataService.PredictIndicesFromSkinMapping(sprites, items, skins, tex);

        Assert.Equal(new[] { 6, 6, 6 }, result); // reading order left→right: A, B, C
    }

    [Fact]
    public void SeparateRowsStayInReadingOrder()
    {
        // Two clear rows (Y 200 vs 10), two columns each. Reading order: top row L→R, then bottom row.
        const string tex = "Grid";
        var sprites = new List<SpriteInfo>
        {
            new("Grid_topL", tex, 10, 200, 50, 50),
            new("Grid_topR", tex, 100, 200, 50, 50),
            new("Grid_botL", tex, 10, 10, 50, 50),
            new("Grid_botR", tex, 100, 10, 50, 50),
        };
        var skins = new List<SkinMapping>
        {
            new(tex, "1", "Grid_topL"), new(tex, "2", "Grid_topR"),
            new(tex, "3", "Grid_botL"), new(tex, "4", "Grid_botR"),
        };
        var items = new List<ParsedItem>
        {
            new() { Level = 1, SkinName = "1" }, new() { Level = 2, SkinName = "2" },
            new() { Level = 3, SkinName = "3" }, new() { Level = 4, SkinName = "4" },
        };

        var result = SpriteMetadataService.PredictIndicesFromSkinMapping(sprites, items, skins, tex);

        // Top row (Y=200, higher Unity Y = top of image) first, L→R: 1,2 ; then bottom row: 3,4.
        Assert.Equal(new[] { 1, 2, 3, 4 }, result);
    }
}
