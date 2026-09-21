using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;
using static MergeMansionWikiTools.Services.AssetExtractionService;

namespace MergeMansionWikiTools.Tests;

public class PortraitVariantResolverTests
{
    private static SpriteInfo Sprite(string name, string texture)
        => new(name, texture, 0f, 0f, 512f, 512f);

    [Theory]
    [InlineData("MaddieDefault", "MaddieDefault", "Default")]                       // holý bundle
    [InlineData("MaddieDefault", "MaddieDefault_Default", "Default")]               // starší export
    [InlineData("MaddieDefault", "MaddieDefault_Winter2023_2", "Default")]          // _2 twin sezónního = plain
    [InlineData("MaddieDefault", "MaddieDefault_Winter2023", "Winter2023")]         // skutečný sezónní skin
    [InlineData("BabylonDefault", "BabylonDefault_Clean", "Default")]               // _Clean je kanonický vzhled
    [InlineData("JuliusDefault", "JuliusDefault_Golf", "Golf")]                     // nesezónní outfit
    [InlineData("JuliusDefault", "JuliusDefault_Golf_2", "Default")]                // _2 twin nesezónního outfitu = taky plain
    [InlineData("JuliusDefault", "JuliusDefault_Zebra", "Zebra")]                   // neznámý outfit NEsmí být Default
    [InlineData("MaddieDefault", "", "Default")]                                    // chybějící textura
    public void VariantFromTexture_follows_the_codex_rules(string sprite, string texture, string expected)
        => Assert.Equal(expected, PortraitVariantResolver.VariantFromTexture(sprite, texture));

    [Fact]
    public void Resolve_prefers_the_plain_bundle_over_a_seasonal_skin()
    {
        var sprites = new List<SpriteInfo>
        {
            Sprite("MaddieThinking", "MaddieThinking_Winter2023"),
            Sprite("MaddieThinking", "MaddieThinking_Winter2023_2"),
        };

        Assert.Equal("Default", PortraitVariantResolver.Resolve(sprites, "Maddie", "Thinking"));
    }

    [Fact]
    public void Resolve_falls_back_to_a_seasonal_skin_when_no_plain_bundle_exists()
    {
        // žádná plain textura, ale dvě odlišné sezónní — je z čeho vybírat, takže pravidlo jediné
        // textury se neuplatní a vyhraje nejlepší dostupná
        var sprites = new List<SpriteInfo>
        {
            Sprite("RoddyAngry", "RoddyAngry_Halloween"),
            Sprite("RoddyAngry", "RoddyAngry_Xmas"),
        };

        Assert.Equal("Halloween", PortraitVariantResolver.Resolve(sprites, "Roddy", "Angry"));
    }

    [Fact]
    public void Resolve_returns_default_for_a_character_the_atlas_does_not_know()
        => Assert.Equal("Default", PortraitVariantResolver.Resolve(new List<SpriteInfo>(), "Nobody", "Default"));

    [Fact]
    public void Resolve_treats_the_only_texture_as_the_character_s_canonical_look()
    {
        // Julius má v atlasu jedinou texturu JuliusThinking_Beard — nemá vousy jako "kostým",
        // prostě je má; bez druhé textury není z čeho vybírat variantu.
        var sprites = new List<SpriteInfo> { Sprite("JuliusThinking", "JuliusThinking_Beard") };

        Assert.Equal("Default", PortraitVariantResolver.Resolve(sprites, "Julius", "Thinking"));
    }

    [Fact]
    public void Resolve_still_names_the_outfit_when_a_second_texture_is_available()
    {
        // jakmile existuje druhá textura, je co porovnávat — pravidlo pro osamocenou texturu
        // se nesmí spustit jen proto, že v atlasu jsou dva záznamy
        var sprites = new List<SpriteInfo>
        {
            Sprite("JuliusThinking", "JuliusThinking_Beard"),
            Sprite("JuliusThinking", "JuliusThinking_Golf"),
        };

        Assert.Equal("Beard", PortraitVariantResolver.Resolve(sprites, "Julius", "Thinking"));
    }

    [Fact]
    public void Resolve_treats_a_numbered_twin_of_a_non_seasonal_outfit_as_plain()
    {
        // MaddieJoyous_Default se v jiné verzi atlasu exportuje jako MaddieJoyous_Tourist_2 — fyzicky
        // tatáž obyčejná textura, jen přejmenovaná sdíleným základem s jiným bundlem; "Tourist" v tom
        // jméně nic neznamená, číslo na konci platí bez ohledu na to, jestli zbytek jména zní sezónně
        var sprites = new List<SpriteInfo>
        {
            Sprite("MaddieJoyous", "MaddieJoyous_Tourist"),
            Sprite("MaddieJoyous", "MaddieJoyous_Tourist_2"),
        };

        Assert.Equal("Default", PortraitVariantResolver.Resolve(sprites, "Maddie", "Joyous"));
    }

    [Fact]
    public void Resolve_finds_a_sprite_named_with_an_underscore_between_character_and_expression()
    {
        // 98 sprite jmen v atlasu (napr. u Voyance) maji mezi postavou a vyrazem podtrzitko misto
        // spojeni primo za sebou. Puvodni hledani zkousi jen "VoyanceThinking", ktere v atlasu
        // vubec neexistuje, takze by na nezmenenem kodu tenhle test selhal s vysledkem "Default".
        var sprites = new List<SpriteInfo>
        {
            Sprite("Voyance_Thinking", "Voyance_Thinking_Halloween"),
            Sprite("Voyance_Thinking", "Voyance_Thinking_Xmas"),
        };

        Assert.Equal("Halloween", PortraitVariantResolver.Resolve(sprites, "Voyance", "Thinking"));
    }

    [Fact]
    public void Resolve_falls_back_to_the_raw_game_identifier_when_the_wiki_name_is_not_in_the_atlas()
    {
        // atlas zna obchodnika se starozitnostmi jen pod hernim id "AntiqueDealer" - jmeno pro wiki
        // "Julius" v nem vubec neni. Puvodni hledani neumelo herni id zkusit vubec, takze by na
        // nezmenenem kodu tenhle test selhal s vysledkem "Default".
        var sprites = new List<SpriteInfo>
        {
            Sprite("AntiqueDealerThinking", "AntiqueDealerThinking_Beard"),
            Sprite("AntiqueDealerThinking", "AntiqueDealerThinking_Golf"),
        };

        Assert.Equal("Beard", PortraitVariantResolver.Resolve(sprites, "Julius", "Thinking", "AntiqueDealer"));
    }

    [Fact]
    public void More_precise_candidate_wins_when_both_the_wiki_name_and_the_game_id_exist_in_the_atlas()
    {
        // kdyz atlas zna JAK jmeno pro wiki, TAK herni id, musi vyhrat presnejsi kandidat (jmeno
        // pro wiki, bez podtrzitka). Puvodni signatura Resolve neprijimala herni id vubec, takze by
        // tenhle 4-parametrovy volani na nezmenenem kodu ani nezkompilovalo.
        var sprites = new List<SpriteInfo>
        {
            Sprite("JuliusThinking", "JuliusThinking_Beard"),
            Sprite("JuliusThinking", "JuliusThinking_Golf"),
            Sprite("AntiqueDealerThinking", "AntiqueDealerThinking_Halloween"),
            Sprite("AntiqueDealerThinking", "AntiqueDealerThinking_Xmas"),
        };

        Assert.Equal("Beard", PortraitVariantResolver.Resolve(sprites, "Julius", "Thinking", "AntiqueDealer"));
    }

    [Fact]
    public void Apply_fills_the_variant_on_every_line_that_has_a_speaker()
    {
        // stejná dvojice sezónních textur jako v Resolve_falls_back… — žádná plain, ale je z čeho vybírat
        var sprites = new List<SpriteInfo>
        {
            Sprite("RoddyAngry", "RoddyAngry_Halloween"),
            Sprite("RoddyAngry", "RoddyAngry_Xmas"),
        };
        var scene = new DialogueScene { Id = "S" };
        scene.Lines.Add(new DialogueLineInfo { Id = "S_01", Speaker = "Roddy", Expression = "Angry" });
        scene.Lines.Add(new DialogueLineInfo { Id = "S_02", Speaker = null, Expression = "Default" });
        var scenes = new List<DialogueScene> { scene };

        PortraitVariantResolver.ApplyTo(scenes, sprites);

        Assert.Equal("Halloween", scenes[0].Lines[0].Variant);
        Assert.Equal("Default", scenes[0].Lines[1].Variant);   // replika bez mluvčího zůstává výchozí
    }
}
