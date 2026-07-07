using System.Collections.Generic;
using MergeMansionWikiTools.Services;
using Xunit;
using Rectangle = SixLabors.ImageSharp.Rectangle;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for ImageSplitLogic.PickBestOverlap — the spatial lookup used by the per-object
/// detection-source toggle in the Image Optimiser. Bug it fixes: the toggle used to map
/// objects across detection lists (merged DetectedObjects vs raw Algorithm/Atlas objects)
/// by ORDERED INDEX; with chain-mode merge-to-expected-count the lists have different
/// counts/ordering, so toggling one item corrupted NEIGHBOURING items' boxes.
/// </summary>
public class PickBestOverlapTests
{
    private static (Rectangle Full, Rectangle Main) Obj(int x, int y, int w, int h)
        => (new Rectangle(x, y, w, h), new Rectangle(x, y, w, h));

    [Fact]
    public void Picks_candidate_with_largest_overlap()
    {
        var reference = Obj(100, 0, 80, 80);
        var candidates = new List<(Rectangle Full, Rectangle Main)>
        {
            Obj(0, 0, 60, 60),      // no overlap
            Obj(95, 5, 70, 70),     // big overlap ← expected
            Obj(160, 0, 40, 40),    // small overlap (20 px wide)
        };

        var picked = ImageSplitLogic.PickBestOverlap(reference, candidates);
        Assert.NotNull(picked);
        Assert.Equal(95, picked!.Value.Full.X);
    }

    [Fact]
    public void Returns_null_when_nothing_overlaps()
    {
        var reference = Obj(1000, 1000, 50, 50);
        var candidates = new List<(Rectangle Full, Rectangle Main)> { Obj(0, 0, 60, 60) };
        Assert.Null(ImageSplitLogic.PickBestOverlap(reference, candidates));
    }

    [Fact]
    public void Returns_null_for_empty_candidates()
        => Assert.Null(ImageSplitLogic.PickBestOverlap(
            Obj(0, 0, 10, 10), new List<(Rectangle Full, Rectangle Main)>()));
}
