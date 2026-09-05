// OcrHarness — raw Windows OCR over a folder of screenshots, one JSON line per image.
//   OcrHarness.exe <imagesRoot> <out.jsonl> [--workers N]
// Output line: {"file": "<relative path>", "w": W, "h": H, "lines": [{"t": text, "x": X, "y": Y, "w": W, "h": H}, ...]}
// Resumable: files already present in out.jsonl are skipped. Images larger than the engine's
// MaxImageDimension are downscaled for recognition and the boxes scaled back to original pixels.
// The speaker/text heuristics live in Python (Codex/build/discord_ocr.py) — this tool only recognises.
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: OcrHarness <imagesRoot> <out.jsonl> [--workers N]");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var outPath = Path.GetFullPath(args[1]);
var workers = 4;
var wi = Array.IndexOf(args, "--workers");
if (wi >= 0 && wi + 1 < args.Length) workers = int.Parse(args[wi + 1]);

var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Where(f => exts.Contains(Path.GetExtension(f)))
    .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();

var done = new HashSet<string>(StringComparer.Ordinal);
if (File.Exists(outPath))
{
    foreach (var line in File.ReadLines(outPath))
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("file", out var f)) done.Add(f.GetString() ?? "");
        }
        catch { /* half-written last line from an interrupted run: ignore */ }
    }
}
var todo = files.Where(f => !done.Contains(f)).ToList();
Console.WriteLine($"images: {files.Count}, already done: {done.Count}, to do: {todo.Count}, workers: {workers}");

// One OcrEngine can run only one RecognizeAsync at a time and async continuations hop threads,
// so a ThreadLocal engine is NOT enough — keep a pool and check an engine out per image.
var enginePool = new ConcurrentBag<OcrEngine>();
OcrEngine RentEngine() => enginePool.TryTake(out var e) ? e
    : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
      ?? OcrEngine.TryCreateFromUserProfileLanguages()
      ?? throw new InvalidOperationException("Windows OCR engine not available (install the English language pack).");

var results = new BlockingCollection<string>(boundedCapacity: 256);
var writer = Task.Run(() =>
{
    using var sw = new StreamWriter(outPath, append: true, new UTF8Encoding(false));
    foreach (var line in results.GetConsumingEnumerable())
    {
        sw.WriteLine(line);
        sw.Flush();
    }
});

var started = DateTime.UtcNow;
var counter = 0;
var jsonOpts = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = workers }, async (rel, ct) =>
{
    var full = Path.Combine(root, rel);
    object record;
    var engine = RentEngine();
    try
    {
        record = await Recognize(engine, full, rel);
    }
    catch (Exception ex)
    {
        record = new { file = rel, error = ex.GetType().Name + ": " + ex.Message };
    }
    finally
    {
        enginePool.Add(engine);
    }
    results.Add(JsonSerializer.Serialize(record, jsonOpts), ct);
    var n = Interlocked.Increment(ref counter);
    if (n % 200 == 0 || n == todo.Count)
    {
        var el = (DateTime.UtcNow - started).TotalMinutes;
        Console.WriteLine($"{n}/{todo.Count}  {el:F1} min  ({n / Math.Max(el, 0.01):F0}/min)");
    }
});
results.CompleteAdding();
await writer;
Console.WriteLine("done");
return 0;

static async Task<object> Recognize(OcrEngine engine, string path, string rel)
{
    var bytes = await File.ReadAllBytesAsync(path);
    using var stream = new InMemoryRandomAccessStream();
    await stream.WriteAsync(bytes.AsBuffer());
    stream.Seek(0);
    var decoder = await BitmapDecoder.CreateAsync(stream);
    var w = (int)decoder.PixelWidth;
    var h = (int)decoder.PixelHeight;

    // Windows OCR refuses images above MaxImageDimension (1500 on most builds): downscale, scale boxes back.
    var max = (int)OcrEngine.MaxImageDimension;
    var scale = 1.0;
    SoftwareBitmap bitmap;
    if (Math.Max(w, h) > max)
    {
        scale = (double)max / Math.Max(w, h);
        var transform = new BitmapTransform { ScaledWidth = (uint)Math.Max(1, w * scale), ScaledHeight = (uint)Math.Max(1, h * scale) };
        bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    }
    else
    {
        bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
    OcrResult result;
    using (bitmap)
    {
        result = await engine.RecognizeAsync(bitmap);
    }
    var lines = new List<object>(result.Lines.Count);
    foreach (var line in result.Lines)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
        foreach (var word in line.Words)
        {
            var r = word.BoundingRect;
            x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
            x1 = Math.Max(x1, r.X + r.Width); y1 = Math.Max(y1, r.Y + r.Height);
        }
        lines.Add(new
        {
            t = line.Text,
            x = (int)Math.Round(x0 / scale), y = (int)Math.Round(y0 / scale),
            w = (int)Math.Round((x1 - x0) / scale), h = (int)Math.Round((y1 - y0) / scale),
        });
    }
    return new { file = rel, w, h, lines };
}
