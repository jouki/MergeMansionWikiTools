using System.IO;
using System.Net.Http;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Uploads area flowchart SVG files to Fandom wiki using the bot account.
/// Filenames are resolved via MediaWiki API `expandtemplates` using the wiki's own
/// `Module:Utils FormatFileName` function, so we always match what
/// `Template:Flowchart/Area` expects — no C# port, no drift risk.
/// </summary>
public static class WikiFlowchartUploadService
{
    private const string Api = "https://merge-mansion.fandom.com/api.php";
    private const string FilenameSeparator = "##MMWT_SEP##";

    /// <summary>
    /// Resolves wiki filenames for given area names in a SINGLE API call.
    /// Area names are trimmed (some JSON entries have stray leading spaces).
    /// Returns dict: trimmed area name → full wiki filename like "GiftShop_Flowchart.svg".
    /// </summary>
    public static async Task<Dictionary<string, string>> ResolveWikiFilenamesAsync(
        HttpClient client, IEnumerable<string> areaNames)
    {
        var names = areaNames.Select(n => n.Trim()).Distinct().ToList();
        if (names.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);

        // Build one big wikitext with SEPARATOR between each FormatFileName call.
        // Using named `name=` param so any funky area names (e.g., containing '=') still work.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append('\n').Append(FilenameSeparator).Append('\n');
            sb.Append("{{#Invoke:Utils|FormatFileName|name=")
              .Append(names[i])
              .Append("|suppressLevel=true|skipExtension=true}}");
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "expandtemplates",
            ["text"] = sb.ToString(),
            ["prop"] = "wikitext",
            ["format"] = "json",
        });
        var resp = await client.PostAsync(Api, form);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var expanded = doc.RootElement
            .GetProperty("expandtemplates")
            .GetProperty("wikitext")
            .GetString() ?? "";

        var parts = expanded.Split(FilenameSeparator, StringSplitOptions.None);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count && i < parts.Length; i++)
        {
            var fn = parts[i].Trim();
            if (!string.IsNullOrEmpty(fn))
                result[names[i]] = fn + "_Flowchart.svg";
        }
        return result;
    }

    /// <summary>
    /// Uploads a single SVG. Overwrites existing files (ignoreWarnings=true).
    /// </summary>
    public static async Task<(bool Success, string Result)> UploadOneAsync(
        HttpClient client, string csrfToken, string wikiFilename, byte[] svgBytes)
    {
        var result = await WikiMappingService.UploadFileAsync(
            client, csrfToken, wikiFilename, svgBytes,
            description: null, ignoreWarnings: true);
        bool ok = result == "Success" || result == "Warning";
        return (ok, result);
    }

    /// <summary>
    /// Uploads multiple flowcharts in one authenticated session.
    /// Performs ONE batch call to the wiki to resolve all filenames, then iterates uploads.
    /// Items: (areaDisplayName, svgBytes). Progress reports per-upload.
    /// </summary>
    public static async Task<(int Ok, int Failed, int Skipped)> UploadAllAsync(
        string username, string password,
        IReadOnlyList<(string AreaName, byte[] SvgBytes)> items,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        using var client = await WikiMappingService.CreateAuthenticatedClientAsync(username, password);
        var csrf = await WikiMappingService.GetCsrfTokenAsync(client);

        progress?.Report($"Resolving wiki filenames via MediaWiki API…");
        var filenameMap = await ResolveWikiFilenamesAsync(
            client, items.Select(x => x.AreaName));

        int ok = 0, failed = 0, skipped = 0;
        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (areaName, bytes) = items[i];
            var trimmed = areaName.Trim();

            if (bytes == null || bytes.Length == 0)
            {
                progress?.Report($"[{i + 1}/{items.Count}] {trimmed} — skipped (no SVG)");
                skipped++;
                continue;
            }

            if (!filenameMap.TryGetValue(trimmed, out var wikiFilename)
                || string.IsNullOrEmpty(wikiFilename))
            {
                progress?.Report($"[{i + 1}/{items.Count}] {trimmed} — skipped (filename resolve failed)");
                skipped++;
                continue;
            }

            progress?.Report($"[{i + 1}/{items.Count}] → {wikiFilename}");
            try
            {
                var (success, _) = await UploadOneAsync(client, csrf, wikiFilename, bytes);
                if (success) ok++;
                else failed++;
            }
            catch (Exception ex)
            {
                progress?.Report($"[{i + 1}/{items.Count}] {trimmed} FAILED: {ex.Message}");
                failed++;
            }

            await Task.Delay(500, ct);
        }

        return (ok, failed, skipped);
    }

    /// <summary>
    /// Reads an SVG file from disk. Tries trimmed name first, then original as fallback.
    /// Returns null if neither exists.
    /// </summary>
    public static byte[]? ReadSvgFromDisk(string directory, string areaDisplayName)
    {
        var trimmed = areaDisplayName.Trim();
        var pathTrimmed = Path.Combine(directory, trimmed + ".svg");
        if (File.Exists(pathTrimmed)) return File.ReadAllBytes(pathTrimmed);

        // Fallback: existing files may still have stray spaces from older generations
        var pathOriginal = Path.Combine(directory, areaDisplayName + ".svg");
        if (File.Exists(pathOriginal)) return File.ReadAllBytes(pathOriginal);

        return null;
    }
}
