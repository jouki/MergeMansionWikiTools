using System.IO;
using System.IO.Compression;
using System.Net.Http;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MergeMansionWikiTools.Services;

internal static class AssetExtractionService
{
    public record ExtractionResult(
        int TotalBundles,
        int ProcessedBundles,
        int ExtractedTextures,
        int SkippedDuplicates,
        int FailedBundles,
        List<string> Warnings);

    public record BundleExtractionResult(int Textures, int Skipped, List<string> Warnings);

    private static readonly HttpClient _http = new();
    private static readonly object _fileLock = new();

    // ── TPK download ──────────────────────────────────────────────────

    public static async Task<string> EnsureTpkAsync(
        string workspaceDir,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var tpkPath = Path.Combine(workspaceDir, "classdata.tpk");
        if (File.Exists(tpkPath))
            return tpkPath;

        onStatus?.Invoke("Downloading classdata.tpk...");

        const string url = "https://nightly.link/AssetRipper/Tpk/workflows/type_tree_tpk/master/lz4_file.zip";
        var zipBytes = await _http.GetByteArrayAsync(url, ct);

        using var zipStream = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var tpkEntry = zip.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase));

        if (tpkEntry == null)
            throw new FileNotFoundException("No .tpk file found in downloaded archive.");

        Directory.CreateDirectory(workspaceDir);
        using var entryStream = tpkEntry.Open();
        using var fileStream = File.Create(tpkPath);
        await entryStream.CopyToAsync(fileStream, ct);

        onStatus?.Invoke("classdata.tpk downloaded.");
        return tpkPath;
    }

    // ── Bundle extraction from APK/XAPK ──────────────────────────────

    public static async Task<(string bundleDir, int count)> ExtractBundlesFromApkAsync(
        string apkPath,
        string outputDir,
        bool includeBuiltIn = false,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var ext = Path.GetExtension(apkPath).ToLowerInvariant();

        int count = 0;

        await Task.Run(() =>
        {
            using var outerStream = File.OpenRead(apkPath);
            using var outerZip = new ZipArchive(outerStream, ZipArchiveMode.Read);

            if (ext == ".xapk")
            {
                // XAPK: outer ZIP → UnityDataAssetPack.apk → assets/aa/Android/*.bundle
                var innerEntry = outerZip.Entries
                    .FirstOrDefault(e => e.Name.Equals("UnityDataAssetPack.apk", StringComparison.OrdinalIgnoreCase));

                if (innerEntry == null)
                    throw new FileNotFoundException("UnityDataAssetPack.apk not found inside the XAPK.");

                using var innerStream = innerEntry.Open();
                using var innerMs = new MemoryStream();
                innerStream.CopyTo(innerMs);
                innerMs.Position = 0;

                using var innerZip = new ZipArchive(innerMs, ZipArchiveMode.Read);
                count = ExtractBundleEntries(innerZip, outputDir, onStatus, ct);

                // Also check base APK for data.unity3d and default resources
                if (includeBuiltIn)
                {
                    var baseApk = outerZip.Entries
                        .FirstOrDefault(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                            && !e.Name.Equals("UnityDataAssetPack.apk", StringComparison.OrdinalIgnoreCase));

                    if (baseApk != null)
                    {
                        using var baseStream = baseApk.Open();
                        using var baseMs = new MemoryStream();
                        baseStream.CopyTo(baseMs);
                        baseMs.Position = 0;

                        using var baseZip = new ZipArchive(baseMs, ZipArchiveMode.Read);
                        count += ExtractSpecialEntries(baseZip, outputDir, ct);
                    }
                }
            }
            else
            {
                // Direct APK
                count = ExtractBundleEntries(outerZip, outputDir, onStatus, ct);
                if (includeBuiltIn)
                    count += ExtractSpecialEntries(outerZip, outputDir, ct);
            }
        }, ct);

        return (outputDir, count);
    }

    private static int ExtractBundleEntries(ZipArchive zip, string outputDir, Action<string>? onStatus, CancellationToken ct)
    {
        var bundleEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("assets/aa/Android/", StringComparison.OrdinalIgnoreCase)
                && e.FullName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int count = 0;
        foreach (var entry in bundleEntries)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(entry.FullName);
            var outPath = Path.Combine(outputDir, fileName);

            using var entryStream = entry.Open();
            using var fileStream = File.Create(outPath);
            entryStream.CopyTo(fileStream);
            count++;

            if (count % 50 == 0 || count == bundleEntries.Count)
                onStatus?.Invoke($"Extracting bundles... {count} / {bundleEntries.Count}");
        }

        return count;
    }

    private static int ExtractSpecialEntries(ZipArchive zip, string outputDir, CancellationToken ct)
    {
        int count = 0;
        string[] specialPaths =
        [
            "assets/bin/Data/data.unity3d",
            "assets/bin/Data/Resources/unity default resources"
        ];

        foreach (var sp in specialPaths)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals(sp, StringComparison.OrdinalIgnoreCase));

            if (entry == null) continue;

            var fileName = SanitizeFileName(Path.GetFileName(entry.FullName));
            var outPath = Path.Combine(outputDir, fileName);

            using var entryStream = entry.Open();
            using var fileStream = File.Create(outPath);
            entryStream.CopyTo(fileStream);
            count++;
        }

        return count;
    }

    // ── Texture extraction from a single bundle ───────────────────────

    public static BundleExtractionResult ExtractTexturesFromBundle(
        string bundlePath,
        string tpkPath,
        string outputDir,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        int textures = 0, skipped = 0;
        var bundleName = Path.GetFileName(bundlePath);

        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(tpkPath);

            BundleFileInstance bunInst;
            try
            {
                bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            }
            catch (Exception ex)
            {
                warnings.Add($"[LOAD] {bundleName}: {ex.Message}");
                return new BundleExtractionResult(0, 0, warnings);
            }

            // Iterate all assets files inside the bundle
            var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
            for (int i = 0; i < dirInfos.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                AssetsFileInstance? afileInst;
                try
                {
                    afileInst = am.LoadAssetsFileFromBundle(bunInst, i);
                }
                catch
                {
                    continue; // Not an assets file (e.g. .resS resource)
                }

                if (afileInst?.file == null)
                    continue;

                var afile = afileInst.file;

                try
                {
                    am.LoadClassDatabaseFromPackage(afile.Metadata.UnityVersion);
                }
                catch (Exception ex)
                {
                    warnings.Add($"[VERSION] {bundleName}: Unity {afile.Metadata.UnityVersion} — {ex.Message}");
                    continue;
                }

                // Build container path → pathId lookup for meaningful duplicate suffixes
                var containerMap = BuildContainerMap(afile, afileInst, am);

                var textureInfos = afile.GetAssetsOfType(AssetClassID.Texture2D);

                foreach (var texInfo in textureInfos)
                {
                    ct.ThrowIfCancellationRequested();

                    string texName = "?";
                    try
                    {
                        var baseField = am.GetBaseField(afileInst, texInfo);
                        var texFile = TextureFile.ReadTextureFile(baseField);
                        texName = texFile.m_Name ?? $"tex_{texInfo.PathId}";

                        if (texFile.m_Width <= 0 || texFile.m_Height <= 0)
                        {
                            warnings.Add($"[SKIP] {bundleName}/{texName}: zero size ({texFile.m_Width}x{texFile.m_Height})");
                            continue;
                        }

                        // Get texture data — try FillPictureData, then bundle fallback
                        byte[]? texData = null;
                        try { texData = texFile.FillPictureData(afileInst); } catch { }

                        if (texData == null || texData.Length == 0)
                        {
                            texFile.SetPictureDataFromBundle(bunInst);
                            texData = texFile.pictureData;
                        }

                        if (texData == null || texData.Length == 0)
                        {
                            warnings.Add($"[NODATA] {bundleName}/{texName}: no texture data (format={(TextureFormat)texFile.m_TextureFormat}, streaming={texFile.m_StreamData.path})");
                            continue;
                        }

                        // Decode to raw BGRA32 pixels
                        var rawPixels = texFile.DecodeTextureRaw(texData, useBgra: true);
                        if (rawPixels == null || rawPixels.Length == 0)
                        {
                            warnings.Add($"[DECODE] {bundleName}/{texName}: DecodeTextureRaw returned empty (format={(TextureFormat)texFile.m_TextureFormat}, {texFile.m_Width}x{texFile.m_Height})");
                            continue;
                        }

                        using var image = Image.LoadPixelData<Bgra32>(rawPixels, texFile.m_Width, texFile.m_Height);
                        image.Mutate(x => x.Flip(FlipMode.Vertical));

                        // Render to memory for duplicate check
                        byte[] pngBytes;
                        using (var ms = new MemoryStream())
                        {
                            image.SaveAsPng(ms);
                            pngBytes = ms.ToArray();
                        }

                        // Build unique path + save under lock to avoid parallel race
                        var safeFileName = SanitizeFileName(texName);
                        if (!safeFileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            safeFileName += ".png";

                        // Use container path for meaningful suffix (like AssetRipper folder names)
                        var suffix = containerMap.TryGetValue(texInfo.PathId, out var containerPath)
                            ? GetContainerSuffix(containerPath)
                            : bundleName;

                        string? outPath;
                        lock (_fileLock)
                        {
                            // Check if an identical file already exists (skip duplicates)
                            var basePath = Path.Combine(outputDir, safeFileName);
                            if (File.Exists(basePath) && IsFileIdentical(basePath, pngBytes))
                            {
                                skipped++;
                                outPath = null;
                            }
                            else
                            {
                                outPath = GetUniqueFilePath(outputDir, safeFileName, suffix);
                                File.Create(outPath).Dispose(); // reserve the path
                            }
                        }

                        if (outPath != null)
                        {
                            File.WriteAllBytes(outPath, pngBytes);
                            textures++;
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"[ERROR] {bundleName}/{texName}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            am.UnloadAll();
        }

        return new BundleExtractionResult(textures, skipped, warnings);
    }

    // ── Extract all bundles in parallel ──────────────────────────────

    public static async Task<ExtractionResult> ExtractAllTexturesAsync(
        string bundleDir,
        string tpkPath,
        string outputDir,
        Action<string, int, int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        var bundleFiles = Directory.GetFiles(bundleDir)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".bundle" || ext == ".unity3d" || ext == "";
            })
            .OrderBy(f => f)
            .ToArray();

        int processed = 0, totalTextures = 0, totalSkipped = 0, failed = 0;
        var allWarnings = new System.Collections.Concurrent.ConcurrentBag<string>();

        var maxParallel = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

        await Parallel.ForEachAsync(bundleFiles,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (bundlePath, token) =>
            {
                var bundleName = Path.GetFileName(bundlePath);
                var current = Interlocked.Increment(ref processed);
                var pct = (int)(current * 100.0 / bundleFiles.Length);
                onProgress?.Invoke(bundleName, current, bundleFiles.Length,
                    Volatile.Read(ref totalTextures));

                BundleExtractionResult result;
                try
                {
                    result = await Task.Run(() =>
                        ExtractTexturesFromBundle(bundlePath, tpkPath, outputDir, token), token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    allWarnings.Add($"[CRASH] {bundleName}: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                Interlocked.Add(ref totalTextures, result.Textures);
                Interlocked.Add(ref totalSkipped, result.Skipped);
                foreach (var w in result.Warnings)
                    allWarnings.Add(w);

                if (result.Warnings.Any(w => w.StartsWith("[LOAD]") || w.StartsWith("[ERROR]") || w.StartsWith("[VERSION]")))
                    Interlocked.Increment(ref failed);
            });

        // Write diagnostic log next to the exe
        var warningList = allWarnings.ToList();
        if (warningList.Count > 0)
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var logDir = Path.Combine(exeDir, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"extraction_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            await File.WriteAllLinesAsync(logPath, warningList, ct);
        }

        return new ExtractionResult(bundleFiles.Length, processed, totalTextures, totalSkipped, failed, warningList);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the AssetBundle's m_Container to build a pathId → container path map.
    /// Container paths look like: "assets/assetbundles/events/seasonpass/sp_generic/art/sprites/texture.png"
    /// </summary>
    private static Dictionary<long, string> BuildContainerMap(
        AssetsFile afile, AssetsFileInstance afileInst, AssetsManager am)
    {
        var map = new Dictionary<long, string>();
        try
        {
            var abInfos = afile.GetAssetsOfType(AssetClassID.AssetBundle);
            if (abInfos.Count == 0) return map;

            var abBase = am.GetBaseField(afileInst, abInfos[0]);
            var container = abBase["m_Container.Array"];
            if (container.IsDummy) return map;

            foreach (var pair in container.Children)
            {
                var path = pair[0].AsString; // key = container path
                var asset = pair[1]["asset"];
                var pathId = asset["m_PathID"].AsLong;
                if (pathId != 0 && !string.IsNullOrEmpty(path))
                    map[pathId] = path;
            }
        }
        catch { /* container not available — fall back to bundle name */ }
        return map;
    }

    /// <summary>
    /// Extracts a meaningful suffix from a Unity container path.
    /// Walks up the path segments looking for a name with '_' (same logic as GetDuplicateSuffix).
    /// E.g. "assets/assetbundles/events/seasonpass/sp_generic/art/sprites/tex.png" → "sp_generic"
    /// </summary>
    private static string GetContainerSuffix(string containerPath)
    {
        // Normalize separators and split
        var parts = containerPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Walk from the parent of the file upward, looking for a segment with '_'
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            if (parts[i].Contains('_'))
                return parts[i];
        }

        // Fallback: use the immediate parent folder
        return parts.Length >= 2 ? parts[^2] : containerPath;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }

    private static string GetUniqueFilePath(string dir, string fileName, string bundleName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
            return path;

        // Try with bundle suffix (truncated if path would be too long)
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var safeBundleName = SanitizeFileName(Path.GetFileNameWithoutExtension(bundleName));
        safeBundleName = TruncateSuffix(dir, baseName, safeBundleName, ext);

        var withBundle = Path.Combine(dir, $"{baseName}_{safeBundleName}{ext}");
        if (!File.Exists(withBundle))
            return withBundle;

        // Numeric suffix
        int counter = 2;
        string numbered;
        do
        {
            numbered = Path.Combine(dir, $"{baseName}_{safeBundleName}_{counter}{ext}");
            counter++;
        } while (File.Exists(numbered));

        return numbered;
    }

    /// <summary>
    /// Truncates the bundle suffix so the full path stays under MAX_PATH (260).
    /// If truncation is needed, keeps the first portion + an 8-char hash for uniqueness.
    /// </summary>
    private static string TruncateSuffix(string dir, string baseName, string suffix, string ext)
    {
        // dir\ + baseName + _ + suffix + _99 + ext  must fit in 259 chars
        const int maxPath = 259;
        const int counterReserve = 4; // "_99" + safety margin
        var overhead = dir.Length + 1 + baseName.Length + 1 + ext.Length + counterReserve; // dir\base_suffix_99.ext
        var maxSuffix = maxPath - overhead;

        if (maxSuffix >= suffix.Length)
            return suffix;

        // Need to truncate — keep first portion + 8 char hash of original for uniqueness
        const int hashLen = 8;
        var keepLen = Math.Max(0, maxSuffix - hashLen - 1); // -1 for the underscore before hash
        var hash = suffix.GetHashCode().ToString("x8");
        return keepLen > 0
            ? $"{suffix[..keepLen]}_{hash}"
            : hash;
    }

    /// <summary>
    /// Fast check: file size must match, then byte-level comparison.
    /// </summary>
    private static bool IsFileIdentical(string existingPath, byte[] newBytes)
    {
        var info = new FileInfo(existingPath);
        if (info.Length != newBytes.Length)
            return false;

        var existing = File.ReadAllBytes(existingPath);
        return existing.AsSpan().SequenceEqual(newBytes);
    }
}
