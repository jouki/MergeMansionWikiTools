using System.IO;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Resolves the "Export - PNGs" directory for the currently selected APK version. Single source of
/// truth for a lookup that used to be copy-pasted per page (<c>EventsPage.ResolveExportDir</c>,
/// <c>ClueCollectionPage.ResolveExportDir</c>) — a third near-identical copy was about to be added for
/// the Dialogues updater (Task 11), so it moved here instead.
/// </summary>
public static class ImageExportPathService
{
    /// <summary>Directory with exported PNGs for the given APK version, or null when it is not set up
    /// (base path/version missing, or the folder was never extracted).</summary>
    public static string? Resolve(string? basePath, string? version)
    {
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version)) return null;
        var dir = Path.Combine(basePath, version, "Export - PNGs");
        return Directory.Exists(dir) ? dir : null;
    }
}
