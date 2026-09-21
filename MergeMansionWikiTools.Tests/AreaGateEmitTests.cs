using System.Collections.Generic;
using System.Text.Json;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Area unlock/tease gates travel from areas.json into Module:Datatable/Areas so the wiki's
/// [[Areas]] overview table can be rendered by Lua instead of by hand. The parser keeps the raw
/// game ids; a second pass resolves AreaCompleted → display name and HotspotCompleted →
/// (owning area, task index) so Lua never has to scan 10k tasks. The emit writes only the
/// fields that exist (The Grand Drive has no gate at all → no line).
/// </summary>
public class AreaGateEmitTests
{
    private static JsonElement Area(string json) => JsonDocument.Parse(json).RootElement;

    // ── ParseGate ──

    [Fact]
    public void ParseGate_reads_level_item_itemSeen_area_and_date()
    {
        var el = Area("""
            {"UnlockRequirements": [
                {"TimeNeeded": {"StartInclusive": "2024-06-16T08:00:00.001"}},
                {"LevelNeeded": {"Min": 30}},
                {"ItemNeededAndConsumed": "SecretSociety_Gem_01"},
                {"ItemSeen": {"ItemRef": "LoveStory_11", "Requirement": 0}},
                {"AreaCompleted": "LandingRoom"}]}
            """);

        var g = AreasService.ParseGate(el, "UnlockRequirements")!;

        Assert.Equal("LandingRoom", g.AreaCompletedId);
        Assert.Equal(30, g.Level);
        Assert.Equal("SecretSociety_Gem_01", g.Item);
        Assert.Equal("LoveStory_11", g.ItemSeen);
        Assert.Equal("16.06.2024", g.Date);
        Assert.False(g.Impossible);
    }

    [Fact]
    public void ParseGate_reads_hotspot_and_impossible_in_both_json_shapes()
    {
        var hs = AreasService.ParseGate(Area("""{"TeaseRequirements": [{"HotspotCompleted": "StudySwapRugDa6"}]}"""), "TeaseRequirements")!;
        Assert.Equal("StudySwapRugDa6", hs.Hotspot);

        var arr = AreasService.ParseGate(Area("""{"UnlockRequirements": ["Impossible"]}"""), "UnlockRequirements")!;
        Assert.True(arr.Impossible);

        var str = AreasService.ParseGate(Area("""{"UnlockRequirements": "Impossible"}"""), "UnlockRequirements")!;
        Assert.True(str.Impossible);
    }

    [Fact]
    public void ParseGate_returns_null_for_empty_or_missing_list()
    {
        Assert.Null(AreasService.ParseGate(Area("""{"UnlockRequirements": []}"""), "UnlockRequirements"));
        Assert.Null(AreasService.ParseGate(Area("""{}"""), "UnlockRequirements"));
    }

    // ── ResolveGateLinks ──

    [Fact]
    public void ResolveGateLinks_maps_area_id_and_hotspot_to_owning_area_and_task_index()
    {
        var sideEntrance = new LuaArea
        {
            AreaId = "MansionSideEntrance", DisplayName = "Side Entrance",
            Tasks = { new LuaTask { Id = "MansionSideEntrancePrepareFlowerBed3C7", Index = 45 } }
        };
        var rufus = new LuaArea
        {
            AreaId = "DogArea", DisplayName = "Rufus' Park",
            Unlock = new AreaGate { Level = 30 },
            Tease = new AreaGate { Hotspot = "MansionSideEntrancePrepareFlowerBed3C7" }
        };
        var study = new LuaArea
        {
            AreaId = "Study", DisplayName = "Study",
            Unlock = new AreaGate { AreaCompletedId = "DogArea" }
        };

        AreasService.ResolveGateLinks(new List<LuaArea> { sideEntrance, rufus, study });

        Assert.Equal("Side Entrance", rufus.Tease!.HotspotArea);
        Assert.Equal(45, rufus.Tease.HotspotTask);
        Assert.Equal("Rufus' Park", study.Unlock!.AreaCompleted);
        Assert.Null(rufus.Unlock!.AreaCompleted);
    }

    [Fact]
    public void ResolveGateLinks_leaves_unknown_ids_unresolved()
    {
        var a = new LuaArea { AreaId = "X", DisplayName = "X", Tease = new AreaGate { Hotspot = "Nope" }, Unlock = new AreaGate { AreaCompletedId = "Ghost" } };
        AreasService.ResolveGateLinks(new List<LuaArea> { a });
        Assert.Null(a.Tease!.HotspotArea);
        Assert.Null(a.Tease.HotspotTask);
        Assert.Null(a.Unlock!.AreaCompleted);
    }

    // ── Lua emit ──

    private static string Emit(LuaArea area)
        => new LuaGeneratorService().GenerateAreaChunks(new List<LuaArea> { area }, null)[0].Lua;

    [Fact]
    public void Emit_writes_unlock_and_tease_with_only_present_fields()
    {
        var lua = Emit(new LuaArea
        {
            AreaId = "DogArea", DisplayName = "Rufus' Park", InternalName = "Rufus' Park",
            Unlock = new AreaGate { Level = 30 },
            Tease = new AreaGate { Hotspot = "MansionSideEntrancePrepareFlowerBed3C7", HotspotArea = "Side Entrance", HotspotTask = 45 }
        });

        Assert.Contains("\t\tunlock = {level = 30},\n", lua);
        Assert.Contains("\t\ttease = {area = \"Side Entrance\", task = 45, hotspot = \"MansionSideEntrancePrepareFlowerBed3C7\"},\n", lua);
    }

    [Fact]
    public void Emit_writes_area_item_itemSeen_date_and_impossible()
    {
        var lua = Emit(new LuaArea
        {
            AreaId = "SecretSociety", DisplayName = "Secret Society", InternalName = "Secret Society", ReleaseDate = "16.06.2024",
            Unlock = new AreaGate { AreaCompletedId = "LandingRoom", AreaCompleted = "Landing Room", Item = "SecretSociety_Gem_01", ItemSeen = "LoveStory_11", Impossible = true },
            Tease = new AreaGate { Date = "09.06.2024" }
        });

        Assert.Contains("\t\tunlock = {area = \"Landing Room\", item = \"SecretSociety_Gem_01\", itemSeen = \"LoveStory_11\", impossible = true},\n", lua);
        Assert.Contains("\t\ttease = {date = \"09.06.2024\"},\n", lua);
    }

    [Fact]
    public void Emit_omits_gate_lines_when_there_is_no_gate()
    {
        var lua = Emit(new LuaArea { AreaId = "Driveway", DisplayName = "The Grand Drive", InternalName = "The Grand Drive" });
        Assert.DoesNotContain("unlock =", lua);
        Assert.DoesNotContain("tease =", lua);
    }
}
