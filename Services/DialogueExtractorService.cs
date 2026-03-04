using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace MergeMansionWikiTools.Services;

public class DialogueExtractorService
{
    public record DialogueLine(string Name, string Text);

    // Button/UI text to filter out from OCR results
    private static readonly HashSet<string> IgnoredTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "skip", "continue", ">>", ">", "skip >>", "continue >",
    };

    private OcrEngine? _engine;

    private OcrEngine GetEngine()
    {
        return _engine ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR engine is not available. Ensure English language pack is installed.");
    }

    /// <summary>
    /// Extracts character name and dialogue text from a single image using Windows OCR.
    /// </summary>
    public async Task<DialogueLine> ExtractSingleAsync(byte[] imageData)
    {
        var engine = GetEngine();
        return await ExtractFromImage(engine, imageData);
    }

    /// <summary>
    /// Extracts character name and dialogue text from each image using Windows OCR.
    /// </summary>
    public async Task<List<DialogueLine>> ExtractAsync(List<byte[]> images)
    {
        if (images.Count == 0)
            throw new InvalidOperationException("No images to process.");

        var engine = GetEngine();
        var results = new List<DialogueLine>();

        foreach (var imageData in images)
        {
            var line = await ExtractFromImage(engine, imageData);
            results.Add(line);
        }

        return results;
    }

    private static async Task<DialogueLine> ExtractFromImage(OcrEngine engine, byte[] imageData)
    {
        // Decode image to SoftwareBitmap
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(imageData.AsBuffer());
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        // Run OCR
        var ocrResult = await engine.RecognizeAsync(softwareBitmap);

        var imageHeight = (double)softwareBitmap.PixelHeight;
        softwareBitmap.Dispose();

        if (ocrResult.Lines.Count == 0)
            return new DialogueLine("???", "[no text detected]");

        // Collect OCR lines in the dialogue area (bottom 35% of image)
        // Game layout: name label ~75-82%, dialogue box ~82-93%, buttons ~94-100%
        var dialogueLines = new List<(double Y, string Text)>();

        foreach (var ocrLine in ocrResult.Lines)
        {
            var lineY = ocrLine.Words[0].BoundingRect.Y;
            var relativeY = lineY / imageHeight;

            // Only consider lines in the bottom 35% of the image
            if (relativeY < 0.65) continue;

            var lineText = ocrLine.Text.Trim().Replace('\'', '\u2019');
            if (string.IsNullOrWhiteSpace(lineText)) continue;

            // Filter out button/UI text
            if (IsIgnoredText(lineText)) continue;

            dialogueLines.Add((lineY, lineText));
        }

        if (dialogueLines.Count == 0)
            return new DialogueLine("???", "[no dialogue detected]");

        // Sort by Y position (top to bottom)
        dialogueLines.Sort((a, b) => a.Y.CompareTo(b.Y));

        // Heuristic: the character name is typically a short single word/name
        // on the orange label, appearing before the longer dialogue text.
        // The first short line (1-3 words) is the name, the rest is dialogue.
        string name = "???";
        var dialogueTexts = new List<string>();

        bool nameFound = false;
        foreach (var (_, text) in dialogueLines)
        {
            if (!nameFound && text.Split(' ').Length <= 3 && text.Length <= 25)
            {
                name = text;
                nameFound = true;
            }
            else
            {
                dialogueTexts.Add(text);
            }
        }

        // If no short name was found, use the first line as name
        if (!nameFound && dialogueLines.Count >= 2)
        {
            name = dialogueLines[0].Text;
            dialogueTexts = dialogueLines.Skip(1).Select(l => l.Text).ToList();
        }
        else if (!nameFound && dialogueLines.Count == 1)
        {
            // Only one line detected — treat entire thing as dialogue
            return new DialogueLine("???", dialogueLines[0].Text);
        }

        var dialogue = string.Join(" ", dialogueTexts);
        return new DialogueLine(name, dialogue);
    }

    private static bool IsIgnoredText(string text)
    {
        // Exact match
        if (IgnoredTexts.Contains(text)) return true;

        // Partial match for button-like text
        var lower = text.ToLowerInvariant();
        if (lower.StartsWith("skip") || lower.StartsWith("continue"))
            return true;

        // Very short text with only symbols
        if (text.Length <= 2 && !char.IsLetter(text[0]))
            return true;

        return false;
    }

    /// <summary>
    /// Formats extracted dialogue lines into wiki markup.
    /// </summary>
    public static string FormatAsWikiDialogue(List<DialogueLine> lines)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            sb.Append($"'''{line.Name}''': {line.Text}");
            if (i < lines.Count - 1)
                sb.AppendLine().AppendLine();
        }
        return sb.ToString();
    }
}
