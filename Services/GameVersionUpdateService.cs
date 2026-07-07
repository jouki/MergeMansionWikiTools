namespace MergeMansionWikiTools.Services;

/// <summary>
/// Detection + orchestration helpers for the "new game version available" offer:
/// decides when to offer an update and finds a matching dump on Discord.
/// Pure decision logic lives here so it is unit-testable; network calls delegate
/// to ApkDownloadService / DiscordDumpDownloadService.
/// </summary>
internal static class GameVersionUpdateService
{
    /// <summary>
    /// True when <paramref name="latestVersion"/> is strictly newer than
    /// <paramref name="currentVersion"/> and the user has not skipped exactly that version.
    /// </summary>
    public static bool ShouldOfferUpdate(
        string currentVersion, string latestVersion, string lastDeclinedVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion) ||
            string.IsNullOrWhiteSpace(latestVersion))
            return false;

        if (ApkDownloadService.CompareVersions(latestVersion, currentVersion) <= 0)
            return false;

        if (!string.IsNullOrWhiteSpace(lastDeclinedVersion) &&
            string.Equals(latestVersion, lastDeclinedVersion, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Picks the newest Discord dump matching the given game version.
    /// <paramref name="dumpsNewestFirst"/> must be sorted newest-first
    /// (FetchAllDumpMessagesAsync already returns that order).
    /// </summary>
    public static DiscordDumpDownloadService.DiscordDumpInfo? PickDumpForVersion(
        IEnumerable<DiscordDumpDownloadService.DiscordDumpInfo> dumpsNewestFirst,
        string version,
        List<ApkDownloadService.ApkVersionInfo>? versions)
    {
        foreach (var dump in dumpsNewestFirst)
        {
            if (dump.AttachmentUrl == null) continue;
            var resolved = DiscordDumpDownloadService.ResolveDumpVersion(dump, versions);
            if (string.Equals(resolved, version, StringComparison.OrdinalIgnoreCase))
                return dump;
        }
        return null;
    }

    /// <summary>Result of a new-game-version check: the newest version + the full
    /// version list (needed later for date-matching Discord dumps).</summary>
    public record GameVersionCheckResult(
        ApkDownloadService.ApkVersionInfo Latest,
        List<ApkDownloadService.ApkVersionInfo> AllVersions);

    /// <summary>
    /// Fetches the available game versions and returns the newest one when it is
    /// worth offering (newer than current, not the version the user skipped).
    /// Returns null when there is nothing to offer.
    /// </summary>
    public static async Task<GameVersionCheckResult?> CheckForNewGameVersionAsync(
        string currentVersion, string lastDeclinedVersion, CancellationToken ct = default)
    {
        var versions = await ApkDownloadService.FetchAvailableVersionsAsync(ct);
        if (versions.Count == 0) return null;

        var latest = versions[0]; // FetchAvailableVersionsAsync sorts newest-first
        return ShouldOfferUpdate(currentVersion, latest.Version, lastDeclinedVersion)
            ? new GameVersionCheckResult(latest, versions)
            : null;
    }

    /// <summary>
    /// Fetches all dump messages from the Discord channel and returns the newest
    /// dump matching the given game version, or null when none exists.
    /// </summary>
    public static async Task<DiscordDumpDownloadService.DiscordDumpInfo?> FindDumpForVersionAsync(
        string botToken, string channelId, string version,
        List<ApkDownloadService.ApkVersionInfo>? versions,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dumps = await DiscordDumpDownloadService.FetchAllDumpMessagesAsync(
            botToken, channelId, progress, ct);
        return PickDumpForVersion(dumps, version, versions);
    }
}
