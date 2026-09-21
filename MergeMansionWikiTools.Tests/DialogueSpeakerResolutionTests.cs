using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class DialogueSpeakerResolutionTests
{
    private static string WriteDump(params object[] lines)
        => WriteDump(characterNames: null, lines);

    private static string WriteDump(Dictionary<string, string>? characterNames, params object[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dlg_{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Data = new { Dialogues = lines, CharacterNames = characterNames } }));
        return path;
    }

    private static object Line(string id, string? text, string left, string right,
        bool leftSpeaks, bool rightSpeaks, string leftState = "NoChange", string rightState = "NoChange",
        string? leftDisplayName = null, string? rightDisplayName = null)
        => new
        {
            DialogItemId = id, Text = text, LeftCharacter = left, RightCharacter = right,
            LeftSpeaks = leftSpeaks, RightSpeaks = rightSpeaks,
            LeftCharacterState = leftState, RightCharacterState = rightState,
            LeftCharacterDisplayName = leftDisplayName, RightCharacterDisplayName = rightDisplayName,
        };

    [Fact]
    public void NoChange_inherits_the_character_from_the_previous_line()
    {
        var path = WriteDump(
            Line("S_01", "First", "Maddie", "None", true, false, "Surprised"),
            Line("S_02", "Second", "NoChange", "None", true, false));

        var scenes = new DialogueService().BuildScenes(path);
        var lines = scenes.Single(s => s.Id == "S").Lines;

        Assert.Equal("Maddie", lines[1].Speaker);
        Assert.Equal(DialogueSide.Left, lines[1].Side);
        Assert.Equal("Surprised", lines[1].Expression);   // stav se dědí stejně jako postava
    }

    [Fact]
    public void Opening_line_without_a_flag_belongs_to_the_next_real_speaker()
    {
        // Attic14.2: scéna navazuje na předchozí, obě strany jsou NoChange a nikdo zatím nemluvil
        var path = WriteDump(
            Line("S_01", "Now... let's open this door.", "NoChange", "NoChange", false, false),
            Line("S_02", "Oh, wow!", "Maddie", "NoChange", true, false, "Surprised"));

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.Equal("Maddie", lines[0].Speaker);
    }

    [Fact]
    public void Line_without_text_is_kept_as_a_silent_beat()
    {
        var path = WriteDump(Line("S_01", null, "Maddie", "None", false, false));

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.Empty(lines[0].Text);
        Assert.Equal(DialogueSide.None, lines[0].Side);
    }

    [Fact]
    public void Scene_without_any_concrete_speaker_stays_on_side_none()
    {
        // 71 takových scén je v reálném dumpu — nikdy se v nich neobjeví konkrétní postava,
        // takže `null == null` nesmí replice omylem přiřadit DialogueSide.Right.
        var path = WriteDump(
            Line("S_01", "Hmm.", "NoChange", "NoChange", false, false),
            Line("S_02", "What is this?", "NoChange", "NoChange", false, false));

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.All(lines, l =>
        {
            Assert.Equal(DialogueSide.None, l.Side);
            Assert.Null(l.Speaker);
        });
    }

    [Fact]
    public void State_from_one_dump_does_not_leak_into_the_next_call()
    {
        // _rightState nesmí být instanční pole (DialogueService je cachovaná AsyncDataCache instance).
        // Dump 1 nastaví vpravo konkrétní postavu s konkrétním výrazem — jistý zdroj případného úniku.
        var pathWithUrsula = WriteDump(
            Line("U_01", "Get out of my house.", "None", "Ursula", false, true, "NoChange", "Angry"));

        // Dump 2 je jiný soubor s jinou scénou; první (a jediná) replika je vpravo NoChange
        // a nemá před sebou nic, z čeho by mohla dědit — na téže instanci nesmí zdědit Ursulu/Angry.
        var pathWithoutAntecedent = WriteDump(
            Line("V_01", "Where am I?", "NoChange", "NoChange", false, false));

        var service = new DialogueService();
        service.BuildScenes(pathWithUrsula);
        var lines = service.BuildScenes(pathWithoutAntecedent).Single().Lines;

        Assert.NotEqual("Ursula", lines[0].Speaker);
        Assert.NotEqual("Angry", lines[0].Expression);
    }

    [Fact]
    public void Speaker_identifier_resolves_to_the_wiki_display_name()
    {
        // "AntiqueDealer" je surovy identifikator z dat hry - na wiki uz postava vystupuje jako
        // Julius (stejna mapa jako cesta pro stranky zahad, viz DialogueService.CharacterDisplayNames)
        var path = WriteDump(Line("S_01", "Ah, a fine find indeed.", "AntiqueDealer", "None", true, false));

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.Equal("Julius", lines[0].Speaker);
    }

    [Fact]
    public void Per_entry_dump_display_name_wins_over_the_hardcoded_map()
    {
        // stejna kaskada jako u cesty pro zahady: dump rika u Mystery Machine "CB-01" (nebo "???"),
        // natvrdo mapa "Mystery Machine" - dump vyhrava, jinak by se tatáž postava jmenovala jinak
        // na dvou ruznych typech stranek
        var path = WriteDump(Line("S_01", "Beep boop.", "MysteryMachine", "None", true, false,
            leftDisplayName: "CB-01"));

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.Equal("CB-01", lines[0].Speaker);
    }

    [Fact]
    public void CharacterNames_table_from_the_dump_wins_over_the_hardcoded_map()
    {
        var characterNames = new Dictionary<string, string> { ["MysteryMachine"] = "???" };
        var path = WriteDump(characterNames, Line("S_01", "Beep boop.", "MysteryMachine", "None", true, false));

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.Equal("???", lines[0].Speaker);
    }

    [Fact]
    public void Grandma_falls_back_to_Ursula_when_the_dump_has_nothing_useful()
    {
        // dump pro babicku bud nic nema, nebo vraci jen technicky nazev shodny se surovym id (ten
        // se preskakuje) - musi propadnout az na natvrdo mapu, ktera dava "Ursula" (ne "Grandma")
        var characterNames = new Dictionary<string, string> { ["Grandma"] = "Grandma" };   // technicky, shodne s id
        var path = WriteDump(characterNames, Line("S_01", "Get out of my house.", "Grandma", "None", true, false));

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.Equal("Ursula", lines[0].Speaker);
    }

    [Fact]
    public void Dog_falls_back_to_Rufus_when_the_dump_has_nothing_useful()
    {
        var path = WriteDump(Line("S_01", "Woof!", "Dog", "None", true, false));

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.Equal("Rufus", lines[0].Speaker);
    }

    [Fact]
    public void Lines_with_the_same_order_keep_their_original_json_order()
    {
        // Malé pole (do ~16 prvků) .NET interně řadí insertion sortem, který je u shodných klíčů
        // stabilní i bez opravy — proto 30 replik se stejným ParseOrder (vedoucí nuly v id), aby se
        // nestabilita introsortu nad prahem měla šanci projevit.
        const int count = 30;
        var dump = Enumerable.Range(0, count)
            .Select(i => Line($"S_{new string('0', i + 1)}1", $"Line{i:00}", "Maddie", "None", true, false))
            .ToArray();
        var path = WriteDump(dump);

        var lines = new DialogueService().BuildScenes(path).Single().Lines;

        Assert.Equal(Enumerable.Range(0, count).Select(i => $"Line{i:00}"), lines.Select(l => l.Text));
    }
}
