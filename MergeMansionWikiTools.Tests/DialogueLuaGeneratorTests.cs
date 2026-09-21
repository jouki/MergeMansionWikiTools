using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class DialogueLuaGeneratorTests
{
    private static DialogueScene Scene(string id, string? area = null, string? ev = null) => new()
    {
        Id = id, Area = area, Event = ev, Trigger = "task completed", Task = "Look inside",
        Lines = { new DialogueLineInfo {
            Id = id + "_01", Text = "Hmm...", Speaker = "Maddie",
            Side = DialogueSide.Left, Left = "Maddie", Expression = "Thinking", Variant = "Default" } },
    };

    [Fact]
    public void Main_story_and_events_go_to_separate_chunks()
    {
        var result = new LuaGeneratorService().GenerateDialogueChunks(
            new List<DialogueScene> { Scene("Attic14", area: "Attic"), Scene("CBE_X_Intro", ev: "Murder at the Mansion") }, "2026-09-21");

        Assert.Contains(result.MainChunks, c => c.Lua.Contains("Attic14"));
        Assert.DoesNotContain(result.MainChunks, c => c.Lua.Contains("CBE_X_Intro"));
        Assert.Contains(result.EventChunks, c => c.Lua.Contains("CBE_X_Intro"));
    }

    [Fact]
    public void Line_keeps_speaker_side_expression_and_variant()
    {
        var lua = new LuaGeneratorService()
            .GenerateDialogueChunks(new List<DialogueScene> { Scene("Attic14", area: "Attic") }, null)
            .MainChunks.Single().Lua;

        Assert.Contains("speaker = \"Maddie\"", lua);
        Assert.Contains("side = \"L\"", lua);
        Assert.Contains("expr = \"Thinking\"", lua);
        Assert.Contains("variant = \"Default\"", lua);
    }

    [Fact]
    public void Quotes_and_backslashes_are_escaped()
    {
        var scene = Scene("X", area: "Attic");
        scene.Lines[0].Text = "She said \"no\" \\ left";

        var lua = new LuaGeneratorService().GenerateDialogueChunks(new List<DialogueScene> { scene }, null)
            .MainChunks.Single().Lua;

        Assert.Contains("\\\"no\\\"", lua);
        Assert.DoesNotContain("\" \\ left", lua);
    }

    [Fact]
    public void Unassigned_scenes_land_in_their_own_module()
    {
        var result = new LuaGeneratorService()
            .GenerateDialogueChunks(new List<DialogueScene> { Scene("Bonus_01") }, null);

        Assert.Empty(result.MainChunks);
        Assert.Contains(result.UnassignedChunks, c => c.Lua.Contains("Bonus_01"));
    }

    [Fact]
    public void Embedded_line_breaks_stay_valid_lua()
    {
        var scene = Scene("Y", area: "Attic");
        scene.Lines[0].Text = "First line\nSecond line\r\nThird";

        var lua = new LuaGeneratorService().GenerateDialogueChunks(new List<DialogueScene> { scene }, null)
            .MainChunks.Single().Lua;

        // A real newline inside a Lua double-quoted string is a syntax error — it must come out as
        // the literal two-character escape sequence "\n", never as an actual line break.
        Assert.Contains("First line\\nSecond line\\nThird", lua);
        Assert.DoesNotContain("First line\nSecond", lua);
    }
}
