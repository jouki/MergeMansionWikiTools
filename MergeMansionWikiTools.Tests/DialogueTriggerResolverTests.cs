using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class DialogueTriggerResolverTests
{
    private static string WriteAreas(object areas)
    {
        var p = Path.Combine(Path.GetTempPath(), $"areas_{System.Guid.NewGuid():N}.json");
        File.WriteAllText(p, JsonSerializer.Serialize(new { Data = areas }));
        return p;
    }

    private static string WriteEvents(object libraries)
    {
        var p = Path.Combine(Path.GetTempPath(), $"events_{System.Guid.NewGuid():N}.json");
        File.WriteAllText(p, JsonSerializer.Serialize(new { Data = libraries }));
        return p;
    }

    [Fact]
    public void TriggerDialogue_on_a_hotspot_assigns_area_and_task()
    {
        var areas = WriteAreas(new[] { new {
            AreaId = "Attic", Name = "Attic",
            HotspotsRefs = new[] { new {
                Id = "AtticClean", Description = "Clean dirt",
                CompletionActions = new[] { new { TriggerDialogue = new {
                    StoryDefinitionId = "Attic14", DialogItems = new Dictionary<string,string> { ["Attic14_01"] = "x" } } } },
                AppearActions = new object[0], FinalizationActions = new object[0] } } } });
        var scenes = new List<DialogueScene> { new() { Id = "Attic14" } };

        DialogueTriggerResolver.Apply(scenes, areas, eventsJsonPath: null);

        Assert.Equal("Attic", scenes[0].Area);
        Assert.Equal("Clean dirt", scenes[0].Task);
        Assert.Equal("task completed", scenes[0].Trigger);
    }

    [Fact]
    public void TriggerCutscene_is_a_scene_too()
    {
        var areas = WriteAreas(new[] { new {
            AreaId = "Attic", Name = "Attic",
            HotspotsRefs = new[] { new {
                Id = "AtticDirt", Description = "Clean dirt",
                CompletionActions = new[] { new { TriggerCutscene = new { CutsceneId = "Attic15" } } },
                AppearActions = new object[0], FinalizationActions = new object[0] } } } });
        var scenes = new List<DialogueScene> { new() { Id = "Attic15" } };

        DialogueTriggerResolver.Apply(scenes, areas, eventsJsonPath: null);

        Assert.Equal("Attic", scenes[0].Area);
        Assert.Equal("Clean dirt", scenes[0].Task);
    }

    [Fact]
    public void Raw_hotspot_title_and_stray_whitespace_are_cleaned_into_a_display_name()
    {
        // 4 realne oblasti v dumpu maji nepouzitelne surove jmeno ("HotspotTitle_GreatHall",
        // " Walk-in Closet" s vedouci mezerou) - resolver ted pouziva stejnou ocistu jako Lua
        // mapping (AreaOrderingService.BuildDisplayName), takze builder nedostane cervenej odkaz
        var areas = WriteAreas(new[] {
            new {
                AreaId = "GreatHall", Name = "HotspotTitle_GreatHall",
                HotspotsRefs = new[] { new {
                    Id = "HallClean", Description = "Sweep floor",
                    CompletionActions = new[] { new { TriggerDialogue = new {
                        StoryDefinitionId = "Hall01", DialogItems = new Dictionary<string,string> { ["Hall01_01"] = "x" } } } },
                    AppearActions = new object[0], FinalizationActions = new object[0] } } },
            new {
                AreaId = "WalkInCloset", Name = " Walk-in Closet",
                HotspotsRefs = new[] { new {
                    Id = "ClosetClean", Description = "Tidy up",
                    CompletionActions = new[] { new { TriggerDialogue = new {
                        StoryDefinitionId = "Closet01", DialogItems = new Dictionary<string,string> { ["Closet01_01"] = "x" } } } },
                    AppearActions = new object[0], FinalizationActions = new object[0] } } },
        });
        var scenes = new List<DialogueScene> { new() { Id = "Hall01" }, new() { Id = "Closet01" } };

        DialogueTriggerResolver.Apply(scenes, areas, eventsJsonPath: null);

        Assert.Equal("Great Hall", scenes[0].Area);
        Assert.Equal("Walk-in Closet", scenes[1].Area);
    }

    [Fact]
    public void Scene_nothing_triggers_stays_unassigned()
    {
        var areas = WriteAreas(new object[0]);
        var scenes = new List<DialogueScene> { new() { Id = "Bonus_Tutorial" } };

        DialogueTriggerResolver.Apply(scenes, areas, eventsJsonPath: null);

        Assert.Null(scenes[0].Area);
        Assert.Null(scenes[0].Event);
    }

    [Fact]
    public void Scene_is_assigned_to_its_event_by_id_prefix()
    {
        var events = WriteEvents(new Dictionary<string, object>
        {
            ["Progressions"] = new[]
            {
                new { Name = "Holiday Mystery", ProgressionEventId = "SP_XmasMystery2024" },
            },
        });
        var scenes = new List<DialogueScene> { new() { Id = "SP_XmasMystery2024_Intro_Dialogue" } };

        DialogueTriggerResolver.Apply(scenes, areasJsonPath: null, events);

        Assert.Equal("Holiday Mystery", scenes[0].Event);
        Assert.Equal("part of the event", scenes[0].Trigger);
    }

    [Fact]
    public void Scene_goes_to_the_more_specific_event_when_one_id_prefixes_another()
    {
        // "CBE_MaddieInParis" je striktní prefix "CBE_MaddieInParis2025" — scéna z ročníku 2025
        // nesmí skončit na obecnějším (starším) eventu jen proto, že se v souboru objevil dřív
        var events = WriteEvents(new Dictionary<string, object>
        {
            ["CollectibleBoards"] = new[]
            {
                new { Name = "Maddie In Paris", CollectibleBoardEventId = "CBE_MaddieInParis" },
                new { Name = "Maddie In Paris 2025", CollectibleBoardEventId = "CBE_MaddieInParis2025" },
            },
        });
        var scenes = new List<DialogueScene> { new() { Id = "CBE_MaddieInParis2025_EiffelTower" } };

        DialogueTriggerResolver.Apply(scenes, areasJsonPath: null, events);

        Assert.Equal("Maddie In Paris 2025", scenes[0].Event);
    }

    // ── Area-id fallback (last resort for a scene with no hotspot and no event) ────────────

    [Fact]
    public void Scene_with_no_hotspot_is_guessed_from_its_own_area_id_prefix()
    {
        // zadny hotspot na tuhle scenu neodkazuje (HotspotsRefs je prazdne), ale samotne Id
        // zacina AreaId oblasti - poslední záchrana ji tam přiřadí jako odhad
        var areas = WriteAreas(new[] { new {
            AreaId = "MusicRoom", Name = "Music Room", HotspotsRefs = new object[0] } });
        var scenes = new List<DialogueScene> { new() { Id = "MusicRoom_Bonus_03" } };

        DialogueTriggerResolver.Apply(scenes, areas, eventsJsonPath: null);

        Assert.Equal("Music Room", scenes[0].Area);
        Assert.Null(scenes[0].Task);
        Assert.Null(scenes[0].Hotspot);
        // odhad se musi dat od skutecneho triggeru rozeznat
        Assert.Contains("inferred", scenes[0].Trigger);
    }

    [Fact]
    public void Area_id_shorter_than_4_chars_never_produces_a_guess()
    {
        // kratky/obecny AreaId by mohl sednout nahodou na spoustu scen - proto se preskakuje
        // (realny dump nema AreaId kratsi nez 4 znaky - "Maze"/"Tomb" - proto je to hranice)
        var areas = WriteAreas(new[] { new {
            AreaId = "Spa", Name = "Spa", HotspotsRefs = new object[0] } });
        var scenes = new List<DialogueScene> { new() { Id = "SpaBonus_01" } };

        DialogueTriggerResolver.Apply(scenes, areas, eventsJsonPath: null);

        Assert.Null(scenes[0].Area);
    }

    [Fact]
    public void Area_id_guess_picks_the_longer_more_specific_area_on_overlap()
    {
        // "MusicRoom" je striktni prefix "MusicRoomExtra" - stejne pravidlo jako u eventu:
        // delsi/specifictejsi AreaId musi vyhrat, jinak by scena skoncila u sirsi (spatne) oblasti
        var areas = WriteAreas(new[] {
            new { AreaId = "MusicRoom", Name = "Music Room", HotspotsRefs = new object[0] },
            new { AreaId = "MusicRoomExtra", Name = "Music Room Extra Wing", HotspotsRefs = new object[0] },
        });
        var scenes = new List<DialogueScene> { new() { Id = "MusicRoomExtra_Bonus_01" } };

        DialogueTriggerResolver.Apply(scenes, areas, eventsJsonPath: null);

        Assert.Equal("Music Room Extra Wing", scenes[0].Area);
    }

    [Fact]
    public void Area_id_guess_ignores_letter_case()
    {
        // realny dump neni v pripadech dusledny mezi AreaId a Id scen, ktere do te oblasti patri -
        // "DanceFloor" (oblast) vs "Dancefloor_02" (scena, male f) - citlive porovnani by tohle
        // (a cele oblasti jako Music Room/"MusicianRoom") tise vyradilo z posledni zachrany
        var areas = WriteAreas(new[] { new {
            AreaId = "DanceFloor", Name = "Dance Floor", HotspotsRefs = new object[0] } });
        var scenes = new List<DialogueScene> { new() { Id = "Dancefloor_02" } };

        DialogueTriggerResolver.Apply(scenes, areas, eventsJsonPath: null);

        Assert.Equal("Dance Floor", scenes[0].Area);
    }

    [Fact]
    public void Real_event_match_wins_over_an_area_id_guess()
    {
        // scena, jejiz Id zacina jak AreaId oblasti, tak ID eventu - realny match z dumpu (event)
        // musi vyhrat nad pouhym odhadem podle prefixu Id
        var areas = WriteAreas(new[] { new {
            AreaId = "Kitchen", Name = "Kitchen", HotspotsRefs = new object[0] } });
        var events = WriteEvents(new Dictionary<string, object>
        {
            ["Progressions"] = new[] { new { Name = "Kitchen Party", ProgressionEventId = "KitchenParty2026" } },
        });
        var scenes = new List<DialogueScene> { new() { Id = "KitchenParty2026_Intro" } };

        DialogueTriggerResolver.Apply(scenes, areas, events);

        Assert.Equal("Kitchen Party", scenes[0].Event);
        Assert.Null(scenes[0].Area);
    }
}
