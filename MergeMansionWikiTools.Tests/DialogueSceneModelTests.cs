using MergeMansionWikiTools.Models;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class DialogueSceneModelTests
{
    [Fact]
    public void Line_defaults_are_safe_for_rendering()
    {
        var line = new DialogueLineInfo { Id = "Attic14_01", Text = "Hmm..." };

        Assert.Equal(DialogueSide.None, line.Side);       // nikdo nemluví, dokud se neurčí
        Assert.Null(line.Speaker);
        Assert.Equal("Default", line.Expression);          // šablona musí mít co vykreslit
        Assert.Equal("Default", line.Variant);
    }

    [Fact]
    public void Scene_knows_whether_it_belongs_to_an_area_or_an_event()
    {
        var area = new DialogueScene { Id = "Attic14", Area = "Attic" };
        var ev = new DialogueScene { Id = "CBE_X_Intro", Event = "Murder at the Mansion" };
        var orphan = new DialogueScene { Id = "Bonus_01" };

        Assert.True(area.IsAreaStory);
        Assert.False(ev.IsAreaStory);
        Assert.True(ev.IsEventStory);
        Assert.False(orphan.IsAreaStory);
        Assert.False(orphan.IsEventStory);
    }
}
