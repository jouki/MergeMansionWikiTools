using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Centrální cache thumbnailů. Načítá přes StreamSource (žádný file lock na disku),
/// DecodePixelWidth/Height a Freeze() (cross-thread safe).
/// Cache klíč obsahuje LastWriteTimeUtc — po změně souboru na disku je entry
/// automaticky neplatná a obrázek se dekóduje znovu.
/// </summary>
public static class ThumbnailCache
{
    /// <summary>Cache klíč: (fullPath, decodeSize, byHeight, lastWriteTimeTicks).</summary>
    private readonly record struct CacheKey(string Path, int Decode, bool ByHeight, long WriteTimeTicks);

    private static readonly ConcurrentDictionary<CacheKey, BitmapImage> _cache = new();
    private static readonly ConcurrentDictionary<(string Path, long WriteTimeTicks), (int Width, int Height)> _dimensions = new();

    /// <summary>Cached load s DecodePixelWidth. decodeWidth ≤ 0 = full-size. Vrací null při chybě.</summary>
    public static BitmapImage? Get(string path, int decodeWidth)
        => GetCore(path, decodeWidth, byHeight: false);

    /// <summary>Cached load s DecodePixelHeight. decodeHeight ≤ 0 = full-size. Vrací null při chybě.</summary>
    public static BitmapImage? GetByHeight(string path, int decodeHeight)
        => GetCore(path, decodeHeight, byHeight: true);

    /// <summary>Decode z paměti BEZ cache (volatilní data — clipboard, extrahované bajty, full-size preview).</summary>
    public static BitmapImage? FromBytes(byte[] data, int decodeWidth)
    {
        try { return Decode(data, decodeWidth, byHeight: false); }
        catch { return null; }
    }

    /// <summary>
    /// Cached pixel rozměry BEZ plného decode — BitmapFrame s DelayCreation + CacheOption.None
    /// přečte jen metadata/header. Vrací null při chybě.
    /// </summary>
    public static (int Width, int Height)? GetPixelDimensions(string path)
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            if (_dimensions.TryGetValue((path, ticks), out var cached)) return cached;

            (int Width, int Height) dims;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                dims = (frame.PixelWidth, frame.PixelHeight);
            }

            EvictStaleDimensions(path, ticks);
            _dimensions[(path, ticks)] = dims;
            return dims;
        }
        catch { return null; }
    }

    /// <summary>Zahodí všechny cached entries (thumbnaily i rozměry).</summary>
    public static void Clear()
    {
        _cache.Clear();
        _dimensions.Clear();
    }

    private static BitmapImage? GetCore(string path, int decode, bool byHeight)
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            var key = new CacheKey(path, decode, byHeight, ticks);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var bmp = Decode(File.ReadAllBytes(path), decode, byHeight);

            EvictStaleThumbnails(path, ticks);
            _cache[key] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    private static BitmapImage Decode(byte[] data, int decode, bool byHeight)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(data);
        if (decode > 0)
        {
            if (byHeight) bmp.DecodePixelHeight = decode;
            else bmp.DecodePixelWidth = decode;
        }
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Odstraní entries stejné cesty se starším WriteTime — soubor byl přepsán (split/optimalizace).</summary>
    private static void EvictStaleThumbnails(string path, long currentTicks)
    {
        foreach (var key in _cache.Keys)
            if (key.WriteTimeTicks != currentTicks && string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase))
                _cache.TryRemove(key, out _);
    }

    private static void EvictStaleDimensions(string path, long currentTicks)
    {
        foreach (var key in _dimensions.Keys)
            if (key.WriteTimeTicks != currentTicks && string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase))
                _dimensions.TryRemove(key, out _);
    }
}
