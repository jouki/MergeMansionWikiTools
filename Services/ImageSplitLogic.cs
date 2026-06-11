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
    public static List<SpriteInfo> OrderSpritesUnity(IEnumerable<SpriteInfo> sprites)
        => sprites
            .OrderByDescending(s => s.RectY)
            .ThenBy(s => s.RectX)
            .ToList();

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
}
