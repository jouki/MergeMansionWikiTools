using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MergeMansionWikiTools.Services;

/// <summary>One portrait to upload: the name it will get on the wiki, and where the source PNG sits locally.</summary>
public record PortraitUpload(string WikiName, string LocalPath);

/// <summary>
/// Portraits for the dialogue template. The wiki convention is `&lt;Character&gt; &lt;Expression&gt;.png`
/// (for example `Julius Thinking.png`); the outfit variant is appended after the expression. Files come
/// from `Codex/_cache/portraits/`, where they are named `&lt;CharacterId&gt;__&lt;Expression&gt;.png`.
/// This class only plans what to upload; the actual transfer goes through the existing
/// <see cref="WikiFlowchartUploadService.UploadOneAsync"/> (same MediaWiki upload call, different filenames).
/// </summary>
public static class PortraitUploadService
{
    /// <summary>
    /// Builds the wiki file name for one local portrait. `localFileName` still carries the in-game
    /// character id (e.g. `AntiqueDealer`); `displayName` is the wiki name for that character
    /// (e.g. `Julius`) and is supplied by the caller — this class does not know the id-to-wiki mapping.
    /// </summary>
    public static string WikiFileName(string localFileName, string displayName, string? variant = null)
    {
        var stem = Path.GetFileNameWithoutExtension(localFileName);
        var expression = stem.Contains("__", StringComparison.Ordinal)
            ? stem[(stem.IndexOf("__", StringComparison.Ordinal) + 2)..]
            : "Default";
        var suffix = string.IsNullOrEmpty(variant) || variant == "Default" ? "" : " " + variant;
        return $"{displayName} {expression}{suffix}.png";
    }

    /// <summary>
    /// `local` maps the already-resolved wiki file name to its local source path (see
    /// <see cref="WikiFileName"/>). Files whose wiki name is already in `existingOnWiki` are skipped.
    /// </summary>
    public static List<PortraitUpload> PlanUploads(
        IReadOnlyDictionary<string, string> local, IReadOnlySet<string> existingOnWiki)
        => local.Where(kv => !existingOnWiki.Contains(kv.Key))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new PortraitUpload(kv.Key, kv.Value))
                .ToList();
}
