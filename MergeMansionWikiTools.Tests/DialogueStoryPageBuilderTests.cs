using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class DialogueStoryPageBuilderTests
{
    private static DialogueScene Scene(string id, string task) => new()
    {
        Id = id, Area = "Attic", Task = task, Trigger = "task completed",
        Lines = { new DialogueLineInfo {
            Id = id + "_01", Text = "Hmm...", Speaker = "Maddie",
            Side = DialogueSide.Left, Left = "Maddie", Right = "Grandma",
            Expression = "Thinking", Variant = "Default" } },
    };

    [Fact]
    public void Page_wraps_scenes_in_a_tabber()
    {
        var wikitext = DialogueStoryPageBuilder.Build("Attic", new List<DialogueScene> { Scene("Attic14", "Look inside") });

        Assert.Contains("{{Tabber", wikitext);
        Assert.Contains("<tabber>", wikitext);
        Assert.Contains("</tabber>", wikitext);
        Assert.Contains("|-| Look inside =", wikitext);
    }

    [Fact]
    public void Every_scene_gets_a_stable_anchor_from_its_id()
    {
        var wikitext = DialogueStoryPageBuilder.Build("Attic", new List<DialogueScene> { Scene("Attic14", "Look inside") });

        Assert.Contains("{{Anchor|Attic14}}", wikitext);
    }

    [Fact]
    public void Line_is_rendered_through_the_dialogue_template()
    {
        var wikitext = DialogueStoryPageBuilder.Build("Attic", new List<DialogueScene> { Scene("Attic14", "Look inside") });

        Assert.Contains("{{DialogueLine|id=Attic14_01|speaker=Maddie|side=L|left=Maddie|right=Grandma|expr=Thinking|variant=Default|text=Hmm...}}", wikitext);
    }

    [Fact]
    public void Scene_without_a_task_falls_back_to_its_id_as_the_tab_name()
    {
        var scene = Scene("Attic15", task: null!);
        scene.Task = null;

        var wikitext = DialogueStoryPageBuilder.Build("Attic", new List<DialogueScene> { scene });

        Assert.Contains("|-| Attic15 =", wikitext);
    }

    [Fact]
    public void Scene_without_a_task_but_with_a_hotspot_uses_a_readable_name_not_the_raw_id()
    {
        // 6 realnych scen v dumpu nema Task, ale ma Hotspot ("CorridorUnlock" apod.) - "Attic99" by
        // ctenar nerozlousknul, "Corridor Unlock" ano
        var scene = Scene("Attic99", task: null!);
        scene.Task = null;
        scene.Hotspot = "CorridorUnlock";

        var wikitext = DialogueStoryPageBuilder.Build("Attic", new List<DialogueScene> { scene });

        Assert.Contains("|-| Corridor Unlock =", wikitext);
        Assert.DoesNotContain("|-| Attic99 =", wikitext);
    }

    [Fact]
    public void Duplicate_tab_names_within_the_same_page_are_disambiguated()
    {
        // "Plant vines" se v realnem dumpu opakuje 5x jen v Bludisti - beze zmeny by druha zalozka
        // se stejnym jmenem byla v <tabber> nedosazitelna
        var scenes = new List<DialogueScene> { Scene("Maze01", "Plant vines"), Scene("Maze02", "Plant vines") };

        var wikitext = DialogueStoryPageBuilder.Build("The Maze", scenes);

        Assert.Contains("|-| Plant vines =", wikitext);
        Assert.Contains("|-| Plant vines (2) =", wikitext);
    }

    [Fact]
    public void Scene_with_no_printable_line_is_skipped_entirely()
    {
        // 3 realne sceny v dumpu nemaji jedinou repliku s textem - beze zmeny by builder vyprodukoval
        // prazdnou zalozku (jen anchor, zadny DialogueLine)
        var silent = new DialogueScene
        {
            Id = "Attic50", Area = "Attic", Task = "Silent beat",
            Lines = { new DialogueLineInfo { Id = "Attic50_01", Text = "", Side = DialogueSide.None } },
        };
        var withContent = Scene("Attic14", "Look inside");

        var wikitext = DialogueStoryPageBuilder.Build("Attic", new List<DialogueScene> { silent, withContent });

        Assert.DoesNotContain("Silent beat", wikitext);
        Assert.DoesNotContain("{{Anchor|Attic50}}", wikitext);
        Assert.Contains("{{Anchor|Attic14}}", wikitext);
    }

    [Fact]
    public void Line_carries_its_own_id_so_an_archive_template_can_reference_it()
    {
        var wikitext = DialogueStoryPageBuilder.Build("Attic", new List<DialogueScene> { Scene("Attic14", "Look inside") });

        Assert.Contains("{{DialogueLine|id=Attic14_01|", wikitext);
    }
}
