using Rectangle = SixLabors.ImageSharp.Rectangle;
using static MergeMansionWikiTools.Services.AssetExtractionService;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Pure split-related logic extracted from ImageOptimiserPage (no UI dependencies):
/// index token parsing, split output file naming, and atlas sprite → image-space
/// rectangle conversion. Detection/merge/crop math lives in ImageProcessingService
/// (single source of truth) — do not duplicate it here.
/// </summary>
internal static class ImageSplitLogic
{
    /// <summary>Separators accepted in the level/index input box.</summary>
    private static readonly char[] IndexSeparators = { ' ', ',', '\r', '\n' };

    /// <summary>
    /// Parses the level/index input text into tokens (e.g. "4 - 2 3 1" → ["4","-","2","3","1"]).
    /// Accepts spaces, commas and newlines as separators; empty entries are removed.
    /// </summary>
    /// <summary>
    /// Whether an auto-prediction may overwrite the index text that is already there.
    /// Yes when the box is empty, or when it still holds OUR last auto-prediction and that
    /// prediction was made for a different chain than the one now active.
    ///
    /// Entry points reach the Image Optimiser in different orders: Item Chains sets the chain
    /// first and loads the atlas after, while Prepare / Season Passes adds the file first —
    /// which auto-enters chain mode by PoolTag and predicts there — and only then hands over
    /// its own chain. Without this the handed-over chain could never drive the prediction and
    /// the stale levels stayed on screen (Bubbling Brews showed "- - - 1").
    /// A text the user typed (or edited) is never overwritten.
    /// </summary>
    public static bool ShouldAutoPredict(
        string? currentText, string? autoPredictedText, string? autoPredictedChainKey, string? activeChainKey)
    {
        if (string.IsNullOrWhiteSpace(currentText)) return true;
        if (autoPredictedText == null) return false;
        if (!string.Equals(currentText, autoPredictedText, StringComparison.Ordinal)) return false;
        return !string.Equals(autoPredictedChainKey ?? "", activeChainKey ?? "", StringComparison.Ordinal);
    }

    public static string[] ParseIndexTokens(string text)
        => text.Split(IndexSeparators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Builds the output file name for a split object:
    /// {baseName}{suffix padded to 2 digits}.png (e.g. "BeachChain" + "4" → "BeachChain04.png").
    /// </summary>
    public static string SplitFileName(string baseName, string suffix)
        => $"{baseName}{suffix.PadLeft(2, '0')}.png";

    /// <summary>
    /// Orders atlas sprites in the canonical prediction order: Unity Y descending, X ascending.
    /// Same ordering as ImageProcessingService.PredictFromSpriteMetadata step 1.
    /// </summary>
    // Row-tolerant reading order (visual rows top→bottom, within a row left→right). Delegates to the
    // single shared implementation so the IO's atlas objects, the level prediction, and the flood-fill
    // display all order sprites identically — otherwise a few px of Y difference between sprites in one
    // visual row would misalign the index string (e.g. CSE_SoloMilestone_Chest).
    public static List<SpriteInfo> OrderSpritesUnity(IEnumerable<SpriteInfo> sprites)
        => SpriteMetadataService.OrderSpritesReadingRows(sprites.ToList());

    /// <summary>
    /// Converts atlas sprite rects (Unity bottom-left origin) to image-space rectangles
    /// (top-left origin). Full and Main are identical for sprite-based objects.
    /// </summary>
    public static List<(Rectangle Full, Rectangle Main)> SpriteObjectsFromSprites(
        IEnumerable<SpriteInfo> sprites, int imageHeight)
        => sprites.Select(s =>
        {
            int x = (int)s.RectX;
            int y = imageHeight - (int)(s.RectY + s.RectHeight);
            int w = Math.Max(1, (int)s.RectWidth);
            int h = Math.Max(1, (int)s.RectHeight);
            var rect = new Rectangle(x, y, w, h);
            return (Full: rect, Main: rect);
        }).ToList();

    /// <summary>
    /// Finds the candidate whose Full rect overlaps the reference's Full rect the most
    /// (by intersection area). Null when nothing overlaps. Used by the per-object
    /// detection-source toggle: detection lists (merged DetectedObjects vs raw
    /// Algorithm/Atlas objects) can differ in count AND ordering, so the alternative
    /// box for an item must be found spatially — mapping by ordered index corrupted
    /// neighbouring items' boxes.
    /// </summary>
    public static (Rectangle Full, Rectangle Main)? PickBestOverlap(
        (Rectangle Full, Rectangle Main) reference,
        IReadOnlyList<(Rectangle Full, Rectangle Main)> candidates)
    {
        (Rectangle Full, Rectangle Main)? best = null;
        long bestArea = 0;
        foreach (var c in candidates)
        {
            var inter = Rectangle.Intersect(reference.Full, c.Full);
            long area = inter.IsEmpty ? 0 : (long)inter.Width * inter.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = c;
            }
        }
        return best;
    }
}
