using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Verifies AreasService.FindAreasForTemporaryChain resolves the area for the Infobox Section
/// intro sentence in both cases: a CONSUMED temp item (own type appears in a hotspot requirement)
/// and a REWARD temp item (own type never required — instead granted as a hotspot reward). The
/// reward case is the Messy Shelf regression: Old Messy Shelf is rewarded by the Attic's "Remove
/// Boxes" task, so the intro must read "... used on the Main Board in {{Area|Attic}}".
/// </summary>
public class TemporaryAreaResolutionTests
{
    private static LuaArea AreaRequiring(string id, params string[] requiredItemTypes)
    {
        var task = new LuaTask { Id = id + "_T1" };
        foreach (var t in requiredItemTypes) task.Requirements[t] = 1;
        return new LuaArea { AreaId = id, DisplayName = id, Tasks = { task } };
    }

    private static LuaArea AreaRewarding(string id, string rewardItemType) =>
        new() { AreaId = id, DisplayName = id, Tasks = { new LuaTask { Id = id + "_T1", ItemReward = rewardItemType } } };

    private static ParsedChain Chain(ParsedItem item) => new() { Items = { item } };

    [Fact]
    public void Reward_OwnTypeNotRequired_ResolvesAreaViaReward()
    {
        // Messy Shelf: own type Attic_MessyShelf_01 is never a requirement; it is a task reward.
        var shelf = new ParsedItem { ItemType = "Attic_MessyShelf_01", Level = 1, IsTemporary = true };
        var areas = new List<LuaArea> { AreaRewarding("Attic", "Attic_MessyShelf_01") };

        var match = AreasService.FindAreasForTemporaryChain(Chain(shelf), areas);

        Assert.Single(match);
        Assert.Equal("Attic", match[0].AreaId);
    }

    [Fact]
    public void Consumed_OwnTypeRequired_ResolvesAreaDirectly()
    {
        var snacks = new ParsedItem { ItemType = "Cinema_Snacks_01", Level = 1, IsTemporary = true };
        var areas = new List<LuaArea> { AreaRequiring("Cinema", "Cinema_Snacks_01") };

        var match = AreasService.FindAreasForTemporaryChain(Chain(snacks), areas);

        Assert.Single(match);
        Assert.Equal("Cinema", match[0].AreaId);
    }

    [Fact]
    public void NeitherRequiredNorRewarded_ReturnsEmpty()
    {
        var orphan = new ParsedItem { ItemType = "Foo_Bar_01", Level = 1, IsTemporary = true };
        var areas = new List<LuaArea> { AreaRewarding("Attic", "Attic_MessyShelf_01") };

        Assert.Empty(AreasService.FindAreasForTemporaryChain(Chain(orphan), areas));
    }
}
