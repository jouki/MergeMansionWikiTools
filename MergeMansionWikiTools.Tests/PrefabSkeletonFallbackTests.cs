using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// poolTagMapping resolves a PoolTag to a PREFAB name, but skinMappings are keyed by the SPINE
/// SKELETON name. For most items the two names are equal, so matching by name works — but for ~22
/// pool tags they differ, sometimes completely (ItemPlayerLevelChest's skeleton is DailyBox) and
/// sometimes through a typo in the asset (ItemSkyscraper → ItemScyscraper). Those chains rendered
/// no images at all: the lookup found zero skin mappings and gave up.
///
/// prefabSkeletonMap (read from the prefab's SkeletonGraphic component during extraction) closes
/// that gap. It is a pure FALLBACK: it may only be consulted when the prefab name resolves to
/// nothing on its own, so every chain that works today keeps resolving exactly as before.
/// </summary>
public class PrefabSkeletonFallbackTests : IDisposable
{
    private readonly string _root;
    private readonly string _exportDir;

    public PrefabSkeletonFallbackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mmwt_prefabskel_" + Guid.NewGuid().ToString("N")[..8]);
        _exportDir = Path.Combine(_root, "Export - PNGs");
        Directory.CreateDirectory(_exportDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteAtlas(Dictionary<string, string> poolTags,
        Dictionary<string, string>? prefabSkeletons,
        params string[] skeletonNames)
    {
        var skins = new List<AssetExtractionService.SkinMapping>();
        foreach (var s in skeletonNames)
            skins.Add(new AssetExtractionService.SkinMapping(s, "1", s + "_sprite", 0, 0, 0, 1, 1, "Item"));

        var data = new AssetExtractionService.AtlasData(
            new List<AssetExtractionService.SpriteInfo>(), skins, poolTags, prefabSkeletons);

        File.WriteAllText(Path.Combine(_root, "image_atlas_data.json"),
            JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        SpriteMetadataService.InvalidateCache();
    }

    [Fact]
    public void Prefab_name_that_is_a_skeleton_resolves_unchanged()
    {
        WriteAtlas(new() { ["ToolsItem"] = "ItemTools" }, new() { ["ItemTools"] = "SomethingElse" },
            "ItemTools", "SomethingElse");

        // The prefab name IS a skeleton, so the map must NOT be consulted — this is the
        // no-regression guarantee for the ~1167 pool tags that already work.
        Assert.Equal("ItemTools", SpriteMetadataService.ResolveSkeletonForPoolTag("ToolsItem", _exportDir));
    }

    [Fact]
    public void Unresolvable_prefab_name_falls_back_to_the_prefab_skeleton_map()
    {
        WriteAtlas(new() { ["PlayerLevelChestItem"] = "ItemPlayerLevelChest" },
            new() { ["ItemPlayerLevelChest"] = "DailyBox" },
            "DailyBox");

        Assert.Equal("DailyBox",
            SpriteMetadataService.ResolveSkeletonForPoolTag("PlayerLevelChestItem", _exportDir));
    }

    [Fact]
    public void Ui_suffix_is_stripped_before_the_map_is_consulted()
    {
        // ItemRadioUI → ItemRadio already resolves today; the map must not override it.
        WriteAtlas(new() { ["RadioItem"] = "ItemRadioUI" }, new() { ["ItemRadioUI"] = "Wrong" },
            "ItemRadio", "Wrong");

        Assert.Equal("ItemRadio", SpriteMetadataService.ResolveSkeletonForPoolTag("RadioItem", _exportDir));
    }

    [Fact]
    public void Map_is_keyed_by_the_raw_prefab_name_even_when_it_carries_a_ui_suffix()
    {
        // ItemSkyscraper-UI's skeleton is ItemScyscraper (typo in the asset). Stripping "-UI"
        // gives ItemSkyscraper, which is not a skeleton, so the map must still be found —
        // and it is keyed by the prefab name as the extractor saw it.
        WriteAtlas(new() { ["SkyscraperItem"] = "ItemSkyscraper-UI" },
            new() { ["ItemSkyscraper-UI"] = "ItemScyscraper" },
            "ItemScyscraper");

        Assert.Equal("ItemScyscraper",
            SpriteMetadataService.ResolveSkeletonForPoolTag("SkyscraperItem", _exportDir));
    }

    [Fact]
    public void Without_a_map_the_old_behaviour_is_kept()
    {
        // Older image_atlas_data.json files have no prefabSkeletonMap at all — the resolver must
        // still return the (stripped) prefab name rather than null.
        WriteAtlas(new() { ["PlayerLevelChestItem"] = "ItemPlayerLevelChest" }, null, "DailyBox");

        Assert.Equal("ItemPlayerLevelChest",
            SpriteMetadataService.ResolveSkeletonForPoolTag("PlayerLevelChestItem", _exportDir));
    }

    [Fact]
    public void Unknown_pool_tag_still_returns_null()
    {
        WriteAtlas(new() { ["ToolsItem"] = "ItemTools" }, new() { ["X"] = "Y" }, "ItemTools");

        Assert.Null(SpriteMetadataService.ResolveSkeletonForPoolTag("NoSuchTag", _exportDir));
    }
}
