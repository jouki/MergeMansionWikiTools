using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Experimental: Uses Claude Haiku to identify game items in cropped images
/// and predict their level/index within a chain.
/// </summary>
internal static class ImagePredictionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// For each cropped image, asks Claude Haiku which item from the candidate list it depicts.
    /// Returns an array of predicted item names (same length as <paramref name="croppedImages"/>).
    /// Null entries mean "could not identify".
    /// </summary>
    public static async Task<string?[]> PredictItemsAsync(
        string apiKey,
        List<byte[]> croppedImages,
        List<string> candidateNames,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new string?[croppedImages.Count];
        var semaphore = new SemaphoreSlim(4); // max 4 concurrent API calls
        var tasks = new List<Task>();

        for (int i = 0; i < croppedImages.Count; i++)
        {
            var idx = i;
            var imageBytes = croppedImages[idx];

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    results[idx] = await IdentifySingleImageAsync(apiKey, imageBytes, candidateNames, ct);
                }
                catch
                {
                    results[idx] = null;
                }
                finally
                {
                    semaphore.Release();
                    progress?.Report(idx);
                }
            }, ct));
        }

        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<string?> IdentifySingleImageAsync(
        string apiKey,
        byte[] imageBytes,
        List<string> candidateNames,
        CancellationToken ct)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var candidateList = string.Join("\n", candidateNames.Select((n, i) => $"- {n}"));

        var requestBody = new
        {
            model = "claude-haiku-4-5-20241022",
            max_tokens = 100,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = "image/png",
                                data = base64
                            }
                        },
                        new
                        {
                            type = "text",
                            text = $"This is a single game item sprite from the mobile game Merge Mansion. " +
                                   $"Look at the image and determine which item from the following list it depicts. " +
                                   $"Respond with ONLY the exact item name from the list, nothing else.\n\n" +
                                   $"Candidate items:\n{candidateList}"
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await Http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            AppLogger.Warn($"Anthropic API error {response.StatusCode}: {responseText}");
            return null;
        }

        // Parse response: content[0].text
        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        if (root.TryGetProperty("content", out var contentArr) &&
            contentArr.ValueKind == JsonValueKind.Array &&
            contentArr.GetArrayLength() > 0)
        {
            var text = contentArr[0].GetProperty("text").GetString()?.Trim();
            if (text != null)
            {
                // Find best match from candidates (exact or closest)
                var exact = candidateNames.FirstOrDefault(c =>
                    string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;

                // Fuzzy: check if response contains a candidate name
                var contained = candidateNames.FirstOrDefault(c =>
                    text.Contains(c, StringComparison.OrdinalIgnoreCase));
                if (contained != null) return contained;

                // Reverse: check if a candidate contains the response
                var reverse = candidateNames.FirstOrDefault(c =>
                    c.Contains(text, StringComparison.OrdinalIgnoreCase));
                if (reverse != null) return reverse;

                AppLogger.Info($"Prediction response '{text}' didn't match any candidate");
            }
        }

        return null;
    }

    /// <summary>
    /// Crops a detected object region from an image file and returns PNG bytes.
    /// </summary>
    public static byte[] CropRegionToPng(string imagePath, Rectangle region)
    {
        using var img = Image.Load<Rgba32>(imagePath);

        // Clamp region to image bounds
        var x = Math.Max(0, region.X);
        var y = Math.Max(0, region.Y);
        var w = Math.Min(region.Width, img.Width - x);
        var h = Math.Min(region.Height, img.Height - y);

        img.Mutate(ctx => ctx.Crop(new Rectangle(x, y, w, h)));

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }
}
