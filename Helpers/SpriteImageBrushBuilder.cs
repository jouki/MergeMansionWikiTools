using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MergeMansionWikiTools.Helpers;

/// <summary>
/// Shared sprite → thumbnail rendering used by the wikitext autocomplete popup and the Daily Trade
/// Predictor. Given a PNG path plus an optional atlas sub-rectangle (bottom-left Y origin) and a
/// rotation flag, returns a frozen <see cref="ImageBrush"/> cropped to that sprite. Single source of
/// truth for the crop/Y-flip/un-rotate logic so both consumers stay pixel-identical.
/// </summary>
public static class SpriteImageBrushBuilder
{
    /// <summary>Loads a BitmapImage at FULL resolution (atlas rects use absolute pixel coords, so we
    /// cannot decode-downscale). WPF caches BitmapImages by Uri, so repeat paths are cheap.</summary>
    public static BitmapSource? LoadBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new System.Uri(path, System.UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Builds a frozen ImageBrush cropped to <paramref name="atlasRect"/> (bottom-left Y,
    /// Y-flipped here using bitmap PixelHeight) and un-rotated when <paramref name="rotated"/> is set.
    /// A null rect uses the whole image (fit to box). Returns null when the image cannot load.</summary>
    public static ImageBrush? Build(string? imagePath, Rect? atlasRect, bool rotated)
    {
        var src = LoadBitmap(imagePath);
        if (src == null) return null;

        if (atlasRect.HasValue)
        {
            var ar = atlasRect.Value;
            double topY = src.PixelHeight - ar.Y - ar.Height;
            int x = (int)System.Math.Max(0, System.Math.Floor(ar.X));
            int y = (int)System.Math.Max(0, System.Math.Floor(topY));
            int w = (int)System.Math.Min(src.PixelWidth - x, System.Math.Ceiling(ar.Width));
            int h = (int)System.Math.Min(src.PixelHeight - y, System.Math.Ceiling(ar.Height));
            if (w > 0 && h > 0)
            {
                try
                {
                    var cropped = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
                    cropped.Freeze();
                    src = cropped;
                }
                catch { /* fall back to full image */ }
            }
        }

        if (rotated)
        {
            try
            {
                var r = new TransformedBitmap(src, new RotateTransform(90));
                r.Freeze();
                src = r;
            }
            catch { /* fall back to un-rotated */ }
        }

        return ToBrush(src);
    }

    /// <summary>Builds a frozen ImageBrush from a whole PNG (no crop). Used for per-level individual
    /// sprite exports and any image that is already a standalone thumbnail.</summary>
    public static ImageBrush? BuildWhole(string? imagePath)
    {
        var src = LoadBitmap(imagePath);
        return src == null ? null : ToBrush(src);
    }

    /// <summary>Builds a frozen ImageBrush from a CENTERED SQUARE crop of a (often non-square) image,
    /// sized by <paramref name="fractionalRect"/>.Width (0..1) of the shorter dimension. Used for area
    /// background thumbnails. Null when the image cannot load.</summary>
    public static ImageBrush? BuildFractional(string? imagePath, Rect fractionalRect)
    {
        var src = LoadBitmap(imagePath);
        if (src == null) return null;

        double side = System.Math.Min(src.PixelWidth, src.PixelHeight) * fractionalRect.Width;
        double cx = src.PixelWidth / 2.0;
        double cy = src.PixelHeight / 2.0;
        int x = (int)System.Math.Max(0, System.Math.Floor(cx - side / 2.0));
        int y = (int)System.Math.Max(0, System.Math.Floor(cy - side / 2.0));
        int w = (int)System.Math.Min(src.PixelWidth - x, System.Math.Ceiling(side));
        int h = (int)System.Math.Min(src.PixelHeight - y, System.Math.Ceiling(side));
        if (w > 0 && h > 0)
        {
            try
            {
                var cropped = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
                cropped.Freeze();
                src = cropped;
            }
            catch { /* fall back to full image */ }
        }
        return ToBrush(src);
    }

    private static ImageBrush ToBrush(BitmapSource src)
    {
        var brush = new ImageBrush(src)
        {
            Stretch = Stretch.Uniform,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };
        brush.Freeze();
        return brush;
    }
}
