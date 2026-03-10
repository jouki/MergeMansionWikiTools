using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
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

    public record BundleExtractionResult(
        int Textures, int Skipped, List<string> Warnings,
        List<SpriteInfo> Sprites,
        List<SkinMapping> SkinMappings,
        Dictionary<string, string> TextureFileMap);

    /// <summary>
    /// Metadata for a single sprite within a texture atlas.
    /// </summary>
    public record SpriteInfo(
        string Name,
        string TextureName,
        float RectX,
        float RectY,
        float RectWidth,
        float RectHeight,
        bool Rotated = false);

    /// <summary>
    /// Spine skeleton skin → sprite mapping.
    /// The game's deterministic connection: SkinName (from chain item) → SpriteName (in atlas).
    /// </summary>
    public record SkinMapping(
        string SkeletonName,
        string SkinName,
        string SpriteName,
        float OffsetX = 0,
        float OffsetY = 0,
        float Rotation = 0,
        float ScaleX = 1,
        float ScaleY = 1);

    /// <summary>
    /// Combined atlas data file: sprites + skin mappings in a single JSON.
    /// Saved as atlas_data.json in the version directory (parent of Export - PNGs).
    /// </summary>
    public record AtlasData(
        List<SpriteInfo> Sprites,
        List<SkinMapping> SkinMappings);

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
        var sprites = new List<SpriteInfo>();
        var skinMappings = new List<SkinMapping>();
        var textureFileMap = new Dictionary<string, string>(); // Unity m_Name → exported filename (without ext)
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
                return new BundleExtractionResult(0, 0, warnings, sprites, skinMappings, textureFileMap);
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

                            // Record textureName → exported filename mapping
                            var exportedName = Path.GetFileNameWithoutExtension(outPath);
                            textureFileMap[texName] = exportedName;
                        }
                        else if (!textureFileMap.ContainsKey(texName))
                        {
                            // Duplicate skipped — map to the base name (already exists)
                            textureFileMap[texName] = Path.GetFileNameWithoutExtension(safeFileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"[ERROR] {bundleName}/{texName}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Build pathId → texture name map (shared by Sprite + SpriteAtlas extraction)
                var texNameMap = new Dictionary<long, string>();
                foreach (var ti in textureInfos)
                {
                    try
                    {
                        var bf = am.GetBaseField(afileInst, ti);
                        var name = bf["m_Name"].AsString;
                        if (!string.IsNullOrEmpty(name))
                            texNameMap[ti.PathId] = name;
                    }
                    catch { }
                }

                // ── Extract Sprite metadata ──
                try
                {
                    var spriteInfos = afile.GetAssetsOfType(AssetClassID.Sprite);
                    foreach (var si in spriteInfos)
                    {
                        try
                        {
                            var baseField = am.GetBaseField(afileInst, si);
                            var spriteName = baseField["m_Name"].AsString;
                            if (string.IsNullOrEmpty(spriteName)) continue;

                            // Read rect (position in atlas)
                            var rect = baseField["m_Rect"];
                            var rx = rect["x"].AsFloat;
                            var ry = rect["y"].AsFloat;
                            var rw = rect["width"].AsFloat;
                            var rh = rect["height"].AsFloat;

                            // Resolve texture name from PPtr
                            string textureName;
                            var texPPtr = baseField["m_RD"]["texture"];
                            var texFileId = texPPtr["m_FileID"].AsInt;
                            var texPathId = texPPtr["m_PathID"].AsLong;

                            if (texFileId == 0 && texNameMap.TryGetValue(texPathId, out var localName))
                            {
                                // Same file — direct lookup
                                textureName = localName;
                            }
                            else
                            {
                                // Cross-bundle reference — try GetExtAsset
                                textureName = "";
                                try
                                {
                                    var extAsset = am.GetExtAsset(afileInst, texPPtr);
                                    if (extAsset.baseField != null)
                                        textureName = extAsset.baseField["m_Name"].AsString ?? "";
                                }
                                catch { }

                                // Fallback: infer texture name from sprite name (strip trailing _NN)
                                if (string.IsNullOrEmpty(textureName))
                                    textureName = InferTextureFromSpriteName(spriteName);
                            }

                            sprites.Add(new SpriteInfo(spriteName, textureName, rx, ry, rw, rh));
                        }
                        catch { /* sprite metadata read failed — non-critical */ }
                    }
                }
                catch { /* Sprite type not available in this bundle — fine */ }

                // ── Extract SpriteAtlas metadata (atlas-packed textures like Mansion2023_*) ──
                try
                {
                    var atlasInfos = afile.GetAssetsOfType(AssetClassID.SpriteAtlas);
                    if (atlasInfos.Count > 0)
                        AppLogger.Info($"[ATLAS] {bundleName}: found {atlasInfos.Count} SpriteAtlas asset(s)");

                    foreach (var atlasAssetInfo in atlasInfos)
                    {
                        try
                        {
                            var baseField = am.GetBaseField(afileInst, atlasAssetInfo);
                            var atlasName = baseField["m_Name"].AsString;
                            if (string.IsNullOrEmpty(atlasName)) continue;

                            // Read packed sprite names
                            var namesField = baseField["m_PackedSpriteNamesToIndex.Array"];
                            var spriteNames = new List<string>();
                            if (!namesField.IsDummy)
                            {
                                foreach (var nameChild in namesField.Children)
                                    spriteNames.Add(nameChild.AsString);
                            }
                            AppLogger.Info($"[ATLAS] '{atlasName}': {spriteNames.Count} packed sprite names, namesField.IsDummy={namesField.IsDummy}");

                            // Read render data map entries (rects + atlas texture)
                            var mapField = baseField["m_RenderDataMap.Array"];
                            AppLogger.Info($"[ATLAS] '{atlasName}': renderDataMap.IsDummy={mapField.IsDummy}, children={(!mapField.IsDummy ? mapField.Children.Count : 0)}");
                            if (mapField.IsDummy || mapField.Children.Count == 0) continue;

                            string atlasTextureName = "";
                            var atlasRects = new List<(float x, float y, float w, float h)>();

                            foreach (var entry in mapField.Children)
                            {
                                try
                                {
                                    var value = entry[1]; // SpriteAtlasData
                                    var textureRect = value["textureRect"];
                                    atlasRects.Add((
                                        textureRect["x"].AsFloat,
                                        textureRect["y"].AsFloat,
                                        textureRect["width"].AsFloat,
                                        textureRect["height"].AsFloat));

                                    // Resolve atlas texture name from first valid entry
                                    if (string.IsNullOrEmpty(atlasTextureName))
                                    {
                                        var texPPtr = value["texture"];
                                        var fid = texPPtr["m_FileID"].AsInt;
                                        var pid = texPPtr["m_PathID"].AsLong;
                                        if (fid == 0 && texNameMap.TryGetValue(pid, out var localTexName))
                                            atlasTextureName = localTexName;
                                        else
                                        {
                                            try
                                            {
                                                var ext = am.GetExtAsset(afileInst, texPPtr);
                                                if (ext.baseField != null)
                                                    atlasTextureName = ext.baseField["m_Name"].AsString ?? "";
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch (Exception ex) { AppLogger.Info($"[ATLAS] Map entry read failed: {ex.GetType().Name}: {ex.Message}"); }
                            }

                            AppLogger.Info($"[ATLAS] '{atlasName}': {atlasRects.Count} valid rects, textureName='{atlasTextureName}'");
                            if (atlasRects.Count == 0) continue;
                            if (string.IsNullOrEmpty(atlasTextureName))
                                atlasTextureName = atlasName; // Fallback: use atlas asset name

                            // Create SpriteInfo entries — correlate names with rects by index
                            int count = Math.Min(spriteNames.Count, atlasRects.Count);
                            for (int j = 0; j < count; j++)
                            {
                                var (rx, ry, rw, rh) = atlasRects[j];
                                if (rw > 0 && rh > 0)
                                    sprites.Add(new SpriteInfo(spriteNames[j], atlasTextureName, rx, ry, rw, rh));
                            }

                            // Extra rects without names → auto-generated names
                            for (int j = count; j < atlasRects.Count; j++)
                            {
                                var (rx, ry, rw, rh) = atlasRects[j];
                                if (rw > 0 && rh > 0)
                                    sprites.Add(new SpriteInfo($"{atlasName}_{j}", atlasTextureName, rx, ry, rw, rh));
                            }

                            AppLogger.Info($"SpriteAtlas '{atlasName}': {Math.Max(count, atlasRects.Count)} sprites → texture '{atlasTextureName}' (names={spriteNames.Count}, rects={atlasRects.Count})");
                        }
                        catch (Exception ex) { AppLogger.Info($"[ATLAS] Read failed: {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
                catch (Exception ex) { AppLogger.Info($"[ATLAS] {bundleName}: GetAssetsOfType(SpriteAtlas) failed: {ex.GetType().Name}: {ex.Message}"); }

                // ── Extract Spine TextAssets (atlas + skeleton JSON for skin mapping) ──
                try
                {
                    var textAssets = afile.GetAssetsOfType(AssetClassID.TextAsset);
                    var spineAtlasNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var skeletonCandidates = new List<(string Name, string Script)>();

                    // Pass 1: process .atlas files, collect skeleton candidates
                    foreach (var textAssetInfo in textAssets)
                    {
                        try
                        {
                            var baseField = am.GetBaseField(afileInst, textAssetInfo);
                            var assetName = baseField["m_Name"].AsString ?? "";
                            var scriptData = baseField["m_Script"].AsString;
                            if (string.IsNullOrEmpty(scriptData)) continue;

                            if (assetName.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
                            {
                                var atlasBaseName = assetName[..^6];
                                spineAtlasNames.Add(atlasBaseName);

                                var spineSprites = ParseSpineAtlas(scriptData);
                                if (spineSprites.Count > 0)
                                {
                                    sprites.AddRange(spineSprites);
                                    AppLogger.Info($"[SPINE] '{assetName}': {spineSprites.Count} sprites → texture '{spineSprites[0].TextureName}'");
                                }
                            }
                            else if (LooksLikeSpineAtlas(scriptData))
                            {
                                // Some bundles store Spine atlas TextAssets without ".atlas" extension
                                spineAtlasNames.Add(assetName);

                                var spineSprites = ParseSpineAtlas(scriptData);
                                if (spineSprites.Count > 0)
                                {
                                    sprites.AddRange(spineSprites);
                                    AppLogger.Info($"[SPINE] '{assetName}' (no .atlas ext): {spineSprites.Count} sprites → texture '{spineSprites[0].TextureName}'");
                                }
                            }
                            else
                            {
                                skeletonCandidates.Add((assetName, scriptData));
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Info($"[SPINE] TextAsset read failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    // Pass 2: parse skeleton JSONs for known atlas names → skin→sprite mapping
                    foreach (var (skelName, skelScript) in skeletonCandidates)
                    {
                        if (!spineAtlasNames.Contains(skelName)) continue;

                        try
                        {
                            var mappings = ParseSpineSkeletonSkins(skelName, skelScript);
                            if (mappings.Count > 0)
                            {
                                skinMappings.AddRange(mappings);
                                AppLogger.Info($"[SPINE] Skeleton '{skelName}': {mappings.Count} skin→sprite mappings");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Info($"[SPINE] Skeleton parse failed for '{skelName}': {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Info($"[SPINE] {bundleName}: GetAssetsOfType(TextAsset) failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            am.UnloadAll();
        }

        return new BundleExtractionResult(textures, skipped, warnings, sprites, skinMappings, textureFileMap);
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
        var allSprites = new System.Collections.Concurrent.ConcurrentBag<SpriteInfo>();
        var allSkinMappings = new System.Collections.Concurrent.ConcurrentBag<SkinMapping>();
        var globalTextureMap = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

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
                foreach (var s in result.Sprites)
                    allSprites.Add(s);
                foreach (var m in result.SkinMappings)
                    allSkinMappings.Add(m);
                foreach (var kv in result.TextureFileMap)
                    globalTextureMap.TryAdd(kv.Key, kv.Value);

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

        // Post-process sprites: replace Unity texture m_Name with exported PNG filename
        var spriteList = allSprites.ToList();
        if (globalTextureMap.Count > 0)
        {
            for (int i = 0; i < spriteList.Count; i++)
            {
                var s = spriteList[i];
                if (globalTextureMap.TryGetValue(s.TextureName, out var exportedName)
                    && exportedName != s.TextureName)
                {
                    spriteList[i] = s with { TextureName = exportedName };
                }
            }
        }
        // Save combined atlas data (sprites + skin mappings) one level above Export - PNGs
        var skinMapList = allSkinMappings.ToList();
        if (spriteList.Count > 0 || skinMapList.Count > 0)
        {
            var parentDir = Path.GetDirectoryName(outputDir)!;
            var atlasDataPath = Path.Combine(parentDir, "atlas_data.json");
            var atlasData = new AtlasData(spriteList, skinMapList);
            var json = JsonSerializer.Serialize(atlasData, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(atlasDataPath, json, ct);
            AppLogger.Info($"Saved atlas_data.json ({spriteList.Count} sprites, {skinMapList.Count} skin mappings) to {atlasDataPath}");
        }

        return new ExtractionResult(bundleFiles.Length, processed, totalTextures, totalSkipped, failed, warningList);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Infers the texture name from a sprite name by stripping the trailing _NN suffix.
    /// E.g., "ItemTools_5" → "ItemTools", "RewardChests_2" → "RewardChests"
    /// </summary>
    private static string InferTextureFromSpriteName(string spriteName)
    {
        var lastUnderscore = spriteName.LastIndexOf('_');
        if (lastUnderscore > 0 && lastUnderscore < spriteName.Length - 1)
        {
            var suffix = spriteName[(lastUnderscore + 1)..];
            if (suffix.All(char.IsDigit))
                return spriteName[..lastUnderscore];
        }
        return spriteName; // No numeric suffix — sprite name IS the texture name
    }

    /// <summary>
    /// Parses a Spine atlas text file and returns SpriteInfo entries.
    /// Spine atlas format: texture filename, size line, format/filter/repeat lines,
    /// Detects if a TextAsset's content looks like a Spine atlas file.
    /// Spine atlas format: first non-blank line ends with .png/.jpg, followed by "size:" header.
    /// </summary>
    private static bool LooksLikeSpineAtlas(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var lines = content.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(line)) continue;
            return line.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || line.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Parses a Spine atlas text file into SpriteInfo records.
    /// Format: texture page header (filename.png, size, format, filter, repeat),
    /// then per-sprite: name, rotate, xy, size, orig, offset, index.
    /// Spine Y is top-down; we convert to Unity bottom-up coordinates.
    /// </summary>
    private static List<SpriteInfo> ParseSpineAtlas(string atlasText)
    {
        var result = new List<SpriteInfo>();
        var lines = atlasText.Split('\n');

        string? textureName = null;
        int textureHeight = 0;
        int lineIdx = 0;

        while (lineIdx < lines.Length)
        {
            var line = lines[lineIdx].TrimEnd('\r');

            // Skip empty lines
            if (string.IsNullOrWhiteSpace(line))
            {
                lineIdx++;
                continue;
            }

            // A line that doesn't start with whitespace and ends with .png/.jpg is a texture filename
            if (!char.IsWhiteSpace(line[0]) && (line.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                              || line.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
            {
                textureName = Path.GetFileNameWithoutExtension(line.Trim());
                textureHeight = 0;
                lineIdx++;

                // Read header lines (size, format, filter, repeat)
                while (lineIdx < lines.Length)
                {
                    var headerLine = lines[lineIdx].TrimEnd('\r').Trim();
                    if (string.IsNullOrWhiteSpace(headerLine)) break;

                    if (headerLine.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                    {
                        var sizeParts = headerLine["size:".Length..].Trim().Split(',');
                        if (sizeParts.Length >= 2 && int.TryParse(sizeParts[1].Trim(), out var h))
                            textureHeight = h;
                    }

                    // format, filter, repeat — skip
                    if (headerLine.StartsWith("format:", StringComparison.OrdinalIgnoreCase)
                        || headerLine.StartsWith("filter:", StringComparison.OrdinalIgnoreCase)
                        || headerLine.StartsWith("repeat:", StringComparison.OrdinalIgnoreCase)
                        || headerLine.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                    {
                        lineIdx++;
                        continue;
                    }

                    break; // Non-header line — must be a sprite name
                }
                continue;
            }

            // Sprite entry: name on unindented line, properties on indented lines
            if (textureName != null && !char.IsWhiteSpace(line[0]))
            {
                var spriteName = line.Trim();
                bool rotate = false;
                int spriteX = 0, spriteY = 0, spriteW = 0, spriteH = 0;
                lineIdx++;

                // Read indented property lines
                while (lineIdx < lines.Length)
                {
                    var propLine = lines[lineIdx].TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(propLine) || !char.IsWhiteSpace(propLine[0]))
                        break;

                    var prop = propLine.Trim();
                    if (prop.StartsWith("rotate:", StringComparison.OrdinalIgnoreCase))
                    {
                        rotate = prop["rotate:".Length..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (prop.StartsWith("xy:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = prop["xy:".Length..].Trim().Split(',');
                        if (parts.Length >= 2)
                        {
                            int.TryParse(parts[0].Trim(), out spriteX);
                            int.TryParse(parts[1].Trim(), out spriteY);
                        }
                    }
                    else if (prop.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = prop["size:".Length..].Trim().Split(',');
                        if (parts.Length >= 2)
                        {
                            int.TryParse(parts[0].Trim(), out spriteW);
                            int.TryParse(parts[1].Trim(), out spriteH);
                        }
                    }

                    lineIdx++;
                }

                // Handle rotation: in Spine, rotated sprites have swapped width/height in the atlas
                // Store the original rotate flag for use during split (to undo atlas rotation)
                if (rotate)
                    (spriteW, spriteH) = (spriteH, spriteW);

                if (spriteW > 0 && spriteH > 0)
                {
                    // Convert Spine Y (top-down) to Unity Y (bottom-up)
                    float unityY = textureHeight > 0
                        ? textureHeight - spriteY - spriteH
                        : spriteY;

                    result.Add(new SpriteInfo(spriteName, textureName, spriteX, unityY, spriteW, spriteH, rotate));
                }

                continue;
            }

            lineIdx++;
        }

        return result;
    }

    /// <summary>
    /// Parses a Spine skeleton JSON to extract skin→sprite mappings.
    /// Handles both Spine 3.x (skins as object {"skinName": {...}}) and
    /// Spine 4.x (skins as array [{"name":"skinName","attachments":{...}}]).
    /// Each skin maps a SkinName to one or more sprite attachment names.
    /// Also extracts transform data (x, y, rotation, scaleX, scaleY) from attachments.
    /// </summary>
    private static List<SkinMapping> ParseSpineSkeletonSkins(string skeletonName, string json)
    {
        var mappings = new List<SkinMapping>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("skins", out var skins))
                return mappings;

            if (skins.ValueKind == JsonValueKind.Array)
            {
                // Spine 4.x format
                foreach (var skin in skins.EnumerateArray())
                {
                    var skinName = skin.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrEmpty(skinName) || skinName == "default")
                        continue;

                    if (!skin.TryGetProperty("attachments", out var attachments))
                        continue;

                    foreach (var slot in attachments.EnumerateObject())
                    {
                        foreach (var attachment in slot.Value.EnumerateObject())
                        {
                            var spriteName = attachment.Name;
                            float offsetX = 0, offsetY = 0, rotation = 0, scaleX = 1, scaleY = 1;

                            if (attachment.Value.ValueKind == JsonValueKind.Object)
                            {
                                if (attachment.Value.TryGetProperty("name", out var nameOverride) &&
                                    nameOverride.ValueKind == JsonValueKind.String)
                                    spriteName = nameOverride.GetString()!;

                                if (attachment.Value.TryGetProperty("x", out var xProp))
                                    offsetX = xProp.GetSingle();
                                if (attachment.Value.TryGetProperty("y", out var yProp))
                                    offsetY = yProp.GetSingle();
                                if (attachment.Value.TryGetProperty("rotation", out var rotProp))
                                    rotation = rotProp.GetSingle();
                                if (attachment.Value.TryGetProperty("scaleX", out var sxProp))
                                    scaleX = sxProp.GetSingle();
                                if (attachment.Value.TryGetProperty("scaleY", out var syProp))
                                    scaleY = syProp.GetSingle();
                            }

                            mappings.Add(new SkinMapping(skeletonName, skinName!, spriteName,
                                offsetX, offsetY, rotation, scaleX, scaleY));
                        }
                    }
                }
            }
            else if (skins.ValueKind == JsonValueKind.Object)
            {
                // Spine 3.x format
                foreach (var skin in skins.EnumerateObject())
                {
                    var skinName = skin.Name;
                    if (skinName == "default")
                        continue;

                    foreach (var slot in skin.Value.EnumerateObject())
                    {
                        foreach (var attachment in slot.Value.EnumerateObject())
                        {
                            var spriteName = attachment.Name;
                            float offsetX = 0, offsetY = 0, rotation = 0, scaleX = 1, scaleY = 1;

                            if (attachment.Value.ValueKind == JsonValueKind.Object)
                            {
                                if (attachment.Value.TryGetProperty("name", out var nameOverride) &&
                                    nameOverride.ValueKind == JsonValueKind.String)
                                    spriteName = nameOverride.GetString()!;

                                if (attachment.Value.TryGetProperty("x", out var xProp))
                                    offsetX = xProp.GetSingle();
                                if (attachment.Value.TryGetProperty("y", out var yProp))
                                    offsetY = yProp.GetSingle();
                                if (attachment.Value.TryGetProperty("rotation", out var rotProp))
                                    rotation = rotProp.GetSingle();
                                if (attachment.Value.TryGetProperty("scaleX", out var sxProp))
                                    scaleX = sxProp.GetSingle();
                                if (attachment.Value.TryGetProperty("scaleY", out var syProp))
                                    scaleY = syProp.GetSingle();
                            }

                            mappings.Add(new SkinMapping(skeletonName, skinName, spriteName,
                                offsetX, offsetY, rotation, scaleX, scaleY));
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            AppLogger.Warn($"Failed to parse Spine skeleton JSON for '{skeletonName}': {ex.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Unexpected error parsing skeleton '{skeletonName}': {ex.Message}");
        }

        if (mappings.Count > 0)
            AppLogger.Info($"Extracted {mappings.Count} skin mappings from skeleton '{skeletonName}'");

        return mappings;
    }

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
