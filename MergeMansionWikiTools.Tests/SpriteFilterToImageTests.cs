using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Services;
using Xunit;
using static MergeMansionWikiTools.Services.AssetExtractionService;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// 26.07.01 image_atlas_data.json carries sprite records that share a texture NAME with an
/// exported atlas but come from another Unity texture (cross-bundle collision). They must not
/// take part in level prediction for that PNG.
/// </summary>
public class SpriteFilterToImageTests
{
    private static SpriteInfo Atlas(string name, float x, float y, float w, float h)
        => new(name, "Pantry_Jam", x, y, w, h);

    private static SpriteInfo Standalone(string name, float w, float h, string texture = "Pantry_Jam")
        => new(name, texture, 0, 0, w, h, CanvasWidth: w, CanvasHeight: h);

    [Fact]
    public void Drops_standalone_sprite_whose_canvas_is_not_the_image()
    {
        // Real Pantry_Jam.png is 296×104 with three 96×100 sprites; 'Pantry_Jam' 128×78 is foreign
        var sprites = new List<SpriteInfo>
        {
            Standalone("Pantry_Jam", 128, 78),
            Atlas("Pantry_Jam_3", 2, 2, 96, 100), Atlas("Pantry_Jam_1", 100, 2, 96, 100), Atlas("Pantry_Jam_2", 198, 2, 96, 100),
        };
        var kept = SpriteMetadataService.FilterToImage(sprites, 296, 104);
        Assert.Equal(new[] { "Pantry_Jam_3", "Pantry_Jam_1", "Pantry_Jam_2" }, kept.Select(s => s.Name));
    }

    [Fact]
    public void Drops_small_standalone_siblings_but_keeps_the_real_single_atlas_sprite()
    {
        // Pantry_Jar.png 128×136: real Pantry_Jar_1 (2,2,124×132) + three 57 px board sprites
        var sprites = new List<SpriteInfo>
        {
            Standalone("Pantry_Jar_01", 57, 81, "Pantry_Jar"), Standalone("Pantry_Jar_02", 56, 84, "Pantry_Jar"),
            Standalone("Pantry_Jar_03", 57, 93, "Pantry_Jar"),
            new("Pantry_Jar_1", "Pantry_Jar", 2, 2, 124, 132),
        };
        var kept = SpriteMetadataService.FilterToImage(sprites, 128, 136);
        var only = Assert.Single(kept);
        Assert.Equal("Pantry_Jar_1", only.Name);
    }

    [Fact]
    public void Keeps_full_canvas_sprite_that_matches_image_and_atlas_sprites_at_origin()
    {
        var sprites = new List<SpriteInfo>
        {
            Standalone("Whole", 200, 100),                // canvas == image → legit
            Atlas("AtOrigin", 0, 0, 50, 50),               // no canvas info → never judged foreign
        };
        Assert.Equal(2, SpriteMetadataService.FilterToImage(sprites, 200, 100).Count);
    }

    [Fact]
    public void Drops_sprites_outside_image_bounds()
    {
        var sprites = new List<SpriteInfo> { Atlas("In", 0, 0, 50, 50), Atlas("Out", 180, 0, 50, 50) };
        var kept = SpriteMetadataService.FilterToImage(sprites, 200, 100);
        Assert.Equal("In", Assert.Single(kept).Name);
    }

    [Fact]
    public void Never_drops_everything_and_never_touches_single_sprite_lists()
    {
        var allForeign = new List<SpriteInfo> { Standalone("A", 10, 10), Standalone("B", 20, 20) };
        Assert.Equal(2, SpriteMetadataService.FilterToImage(allForeign, 300, 300).Count);
        var single = new List<SpriteInfo> { Standalone("A", 10, 10) };
        Assert.Single(SpriteMetadataService.FilterToImage(single, 300, 300));
    }
}
