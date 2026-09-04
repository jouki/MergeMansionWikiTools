using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Season Pass / Prepare opens the Image Optimiser by adding the file FIRST (which auto-enters
/// chain mode by PoolTag and predicts there) and hands over its own chain only afterwards, while
/// Item Chains sets the chain first. The auto-prediction must therefore be redone when the active
/// chain changes — otherwise the handed-over chain never drives it and stale levels stay
/// ("- - - 1" on Bubbling Brews). A text the user typed must never be overwritten.
/// </summary>
public class AutoPredictChainSwitchTests
{
    [Fact]
    public void Empty_box_always_predicts()
    {
        Assert.True(ImageSplitLogic.ShouldAutoPredict("", null, null, "ChainA"));
        Assert.True(ImageSplitLogic.ShouldAutoPredict("   ", "1 2", "ChainA", "ChainB"));
        Assert.True(ImageSplitLogic.ShouldAutoPredict(null, null, null, null));
    }

    [Fact]
    public void Our_prediction_is_redone_when_the_chain_changed()
        => Assert.True(ImageSplitLogic.ShouldAutoPredict("- - - 1", "- - - 1", "SP_Chest", "SP_AllHallowsEve2026_CollectableItems"));

    [Fact]
    public void Our_prediction_is_kept_for_the_same_chain()
        => Assert.False(ImageSplitLogic.ShouldAutoPredict("4 3 2 1", "4 3 2 1", "ChainA", "ChainA"));

    [Fact]
    public void User_edited_text_is_never_overwritten()
    {
        // typed over our prediction
        Assert.False(ImageSplitLogic.ShouldAutoPredict("1 2 3 4", "- - - 1", "ChainA", "ChainB"));
        // typed with nothing predicted before
        Assert.False(ImageSplitLogic.ShouldAutoPredict("1 2 3", null, null, "ChainA"));
    }

    [Fact]
    public void Leaving_chain_mode_counts_as_a_chain_change()
        => Assert.True(ImageSplitLogic.ShouldAutoPredict("4 3 2 1", "4 3 2 1", "ChainA", ""));
}
