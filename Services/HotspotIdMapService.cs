using System.IO;
using System.Text.Json;
using GameLogic;
using GameLogic.Il2Cpp;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Keeps <see cref="HotspotIdNames"/> in sync with the game version being dumped so a game
/// update never again produces integer task Ids / missing descriptions in areas.json.
///
/// Flow (runs before every dump, ~1 s when cached, ~3 s when the XAPK must be opened):
///   1. Game version = <c>_DATA/game_version.txt</c> next to the config archive (written by
///      Pull from Phone), fallback = the APK version selected in Settings.
///   2. Cache hit: <c>_DATA/HotspotIds/&lt;version&gt;.json</c> → load, done.
///   3. Cache miss: find <c>&lt;workspace&gt;/&lt;version&gt;/*.xapk|*.apk</c> (the same file the Image
///      Extractor uses); if absent, download it via <see cref="ApkDownloadService"/>.
///   4. Read <c>global-metadata.dat</c> from the archive in memory, extract the HotspotId enum
///      (<see cref="Il2CppMetadataEnumReader"/>), write the cache, load.
/// Any failure is reported through <paramref name="progress"/> and the dump proceeds with the
/// compiled-enum fallback (exactly the pre-existing behaviour) — never a hard stop.
/// </summary>
public static class HotspotIdMapService
{
    public const string CacheDirName = "HotspotIds";
    public const string EnumName = "HotspotId";

    public record EnsureResult(bool Loaded, string? Version, int MemberCount, string Source, string? Warning);

    private sealed class CacheFile
    {
        public string? GameVersion { get; set; }
        public string? ApkFile { get; set; }
        public int MetadataVersion { get; set; }
        public string? CreatedAt { get; set; }
        public Dictionary<string, int> Members { get; set; } = new();
    }

    public static string CacheDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_DATA", CacheDirName);
    public static string CachePathFor(string version) => Path.Combine(CacheDir, $"{version}.json");

    /// <summary>Game version for the config archive: game_version.txt beside it, else the Settings selection.</summary>
    public static string? ResolveGameVersion(string? configPath, string? selectedApkVersion)
    {
        var fromData = AbGroupsService.ReadValueFile(
            AbGroupsService.ResolveDataFile(configPath, PhoneDetectionService.GameVersionFileName));
        if (!string.IsNullOrWhiteSpace(fromData)) return fromData.Trim();
        return string.IsNullOrWhiteSpace(selectedApkVersion) ? null : selectedApkVersion.Trim();
    }

    public static async Task<EnsureResult> EnsureLoadedAsync(
        string? configPath,
        string? workspaceBasePath,
        string? selectedApkVersion,
        IProgress<string>? progress = null,
        bool allowDownload = true,
        CancellationToken ct = default)
    {
        var version = ResolveGameVersion(configPath, selectedApkVersion);
        if (version == null)
        {
            const string w = "HotspotId map: game version unknown (no _DATA/game_version.txt, no APK version in Settings) — using compiled enum.";
            AppLogger.Warn(w);
            HotspotIdNames.Clear();
            return new EnsureResult(false, null, 0, "enum", w);
        }

        // 1. Cache
        var cachePath = CachePathFor(version);
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath));
                if (cached?.Members is { Count: > 0 })
                {
                    Load(cached.Members, version);
                    var msg = $"HotspotId map: {cached.Members.Count} members for v{version} (cached, metadata v{cached.MetadataVersion})";
                    progress?.Report(msg);
                    AppLogger.Info(msg);
                    return new EnsureResult(true, version, cached.Members.Count, "cache", null);
                }
                AppLogger.Warn($"HotspotId map cache {cachePath} is empty — rebuilding");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"HotspotId map cache {cachePath} unreadable ({ex.Message}) — rebuilding");
            }
        }

        // 2. APK / XAPK for this version
        string? apkPath = null;
        if (!string.IsNullOrWhiteSpace(workspaceBasePath) && Directory.Exists(workspaceBasePath))
        {
            var versionDir = Path.Combine(workspaceBasePath, version);
            if (Directory.Exists(versionDir))
                apkPath = CatalogParserService.FindApkInFolder(versionDir);

            if (apkPath == null && allowDownload)
            {
                progress?.Report($"HotspotId map: no APK for v{version} in workspace — downloading…");
                try
                {
                    var versions = await ApkDownloadService.FetchAvailableVersionsAsync(ct);
                    var info = versions.FirstOrDefault(v => v.Version == version);
                    if (info == null)
                    {
                        AppLogger.Warn($"HotspotId map: v{version} not offered by the APK source");
                    }
                    else
                    {
                        var (_, file) = await ApkDownloadService.DownloadVersionAsync(
                            workspaceBasePath, info, s => progress?.Report($"HotspotId map: {s}"), ct);
                        apkPath = file;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"HotspotId map: APK download failed: {ex.Message}");
                }
            }
        }

        if (apkPath == null || !File.Exists(apkPath))
        {
            var w = $"HotspotId map: no APK/XAPK for v{version} (workspace '{workspaceBasePath}') — using compiled enum; new-area tasks may dump as integer Ids.";
            AppLogger.Warn(w);
            progress?.Report(w);
            HotspotIdNames.Clear();
            return new EnsureResult(false, version, 0, "enum", w);
        }

        // 3. Parse metadata → cache → load
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (members, metaVersion) = await Task.Run(() =>
            {
                var bytes = Il2CppMetadataEnumReader.ExtractGlobalMetadata(apkPath);
                var v = Il2CppMetadataEnumReader.ReadVersion(bytes);
                return (Il2CppMetadataEnumReader.ReadEnum(bytes, EnumName), v);
            }, ct);

            var dict = new Dictionary<string, int>(members.Count);
            foreach (var m in members) dict[m.Name] = m.Value;

            Directory.CreateDirectory(CacheDir);
            var cache = new CacheFile
            {
                GameVersion = version,
                ApkFile = Path.GetFileName(apkPath),
                MetadataVersion = metaVersion,
                CreatedAt = DateTime.UtcNow.ToString("O"),
                Members = dict,
            };
            File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));

            Load(dict, version);
            var msg = $"HotspotId map: {dict.Count} members read from {Path.GetFileName(apkPath)} (metadata v{metaVersion}, {sw.ElapsedMilliseconds} ms) → cached";
            progress?.Report(msg);
            AppLogger.Info(msg);
            return new EnsureResult(true, version, dict.Count, "apk", null);
        }
        catch (Exception ex)
        {
            var w = $"HotspotId map: reading {Path.GetFileName(apkPath)} failed ({ex.GetType().Name}: {ex.Message}) — using compiled enum.";
            AppLogger.Error(w, ex);
            progress?.Report(w);
            HotspotIdNames.Clear();
            return new EnsureResult(false, version, 0, "enum", w);
        }
    }

    private static void Load(Dictionary<string, int> membersByName, string version)
    {
        HotspotIdNames.Load(
            membersByName.Select(kv => new KeyValuePair<int, string>(kv.Value, kv.Key)),
            $"v{version}");
    }
}
