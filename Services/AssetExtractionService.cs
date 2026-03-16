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
    /// <summary>Maps baseName (without suffix) → container suffix for the FIRST extracted file.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        _firstFileSuffixMap = new(StringComparer.OrdinalIgnoreCase);

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

                            // Store suffix for every file (even first/plain) for post-processing rename
                            var baseNameOnly = Path.GetFileNameWithoutExtension(safeFileName);
                            var safeSuffix = SanitizeFileName(Path.GetFileNameWithoutExtension(suffix));
                            _firstFileSuffixMap.TryAdd(baseNameOnly, safeSuffix);
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

                            var atlasRects = new List<(float x, float y, float w, float h, string texName)>();

                            foreach (var entry in mapField.Children)
                            {
                                try
                                {
                                    var value = entry[1]; // SpriteAtlasData
                                    var textureRect = value["textureRect"];

                                    // Resolve texture name PER ENTRY (each sprite can be on a different atlas page)
                                    string entryTexName = "";
                                    try
                                    {
                                        var texPPtr = value["texture"];
                                        var fid = texPPtr["m_FileID"].AsInt;
                                        var pid = texPPtr["m_PathID"].AsLong;
                                        if (fid == 0 && texNameMap.TryGetValue(pid, out var localTexName))
                                            entryTexName = localTexName;
                                        else
                                        {
                                            var ext = am.GetExtAsset(afileInst, texPPtr);
                                            if (ext.baseField != null)
                                                entryTexName = ext.baseField["m_Name"].AsString ?? "";
                                        }
                                    }
                                    catch { }

                                    atlasRects.Add((
                                        textureRect["x"].AsFloat,
                                        textureRect["y"].AsFloat,
                                        textureRect["width"].AsFloat,
                                        textureRect["height"].AsFloat,
                                        entryTexName));
                                }
                                catch (Exception ex) { AppLogger.Info($"[ATLAS] Map entry read failed: {ex.GetType().Name}: {ex.Message}"); }
                            }

                            // Determine fallback texture name (most common non-empty entry)
                            var fallbackTexName = atlasRects
                                .Where(r => !string.IsNullOrEmpty(r.texName))
                                .GroupBy(r => r.texName)
                                .OrderByDescending(g => g.Count())
                                .FirstOrDefault()?.Key ?? atlasName;

                            AppLogger.Info($"[ATLAS] '{atlasName}': {atlasRects.Count} valid rects, fallbackTex='{fallbackTexName}'");
                            if (atlasRects.Count == 0) continue;

                            // Create SpriteInfo entries — each sprite has its own texture page
                            int count = Math.Min(spriteNames.Count, atlasRects.Count);
                            for (int j = 0; j < count; j++)
                            {
                                var (rx, ry, rw, rh, texName) = atlasRects[j];
                                if (rw > 0 && rh > 0)
                                {
                                    var effectiveTex = !string.IsNullOrEmpty(texName) ? texName : fallbackTexName;
                                    sprites.Add(new SpriteInfo(spriteNames[j], effectiveTex, rx, ry, rw, rh));
                                }
                            }

                            // Extra rects without names → auto-generated names
                            for (int j = count; j < atlasRects.Count; j++)
                            {
                                var (rx, ry, rw, rh, texName) = atlasRects[j];
                                if (rw > 0 && rh > 0)
                                {
                                    var effectiveTex = !string.IsNullOrEmpty(texName) ? texName : fallbackTexName;
                                    sprites.Add(new SpriteInfo($"{atlasName}_{j}", effectiveTex, rx, ry, rw, rh));
                                }
                            }

                            AppLogger.Info($"SpriteAtlas '{atlasName}': {Math.Max(count, atlasRects.Count)} sprites → texture '{fallbackTexName}' (names={spriteNames.Count}, rects={atlasRects.Count})");
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
                    var spineAtlasData = new Dictionary<string, (string Text, string TextureName)>(StringComparer.OrdinalIgnoreCase);
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

                            bool isAtlas = false;
                            string atlasBaseName = assetName;

                            if (assetName.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
                            {
                                atlasBaseName = assetName[..^6];
                                isAtlas = true;
                            }
                            else if (LooksLikeSpineAtlas(scriptData))
                            {
                                isAtlas = true;
                            }

                            if (isAtlas)
                            {
                                spineAtlasNames.Add(atlasBaseName);

                                var spineSprites = ParseSpineAtlas(scriptData);
                                if (spineSprites.Count > 0)
                                {
                                    sprites.AddRange(spineSprites);
                                    var texName = spineSprites[0].TextureName;
                                    spineAtlasData[atlasBaseName] = (scriptData, texName);
                                    AppLogger.Info($"[SPINE] '{assetName}': {spineSprites.Count} sprites → texture '{texName}'");
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

                    // Pass 2: parse skeleton JSONs + save raw Spine files for icon rendering
                    var spineRawDir = Path.Combine(outputDir, "_SpineRaw");
                    foreach (var (skelName, skelScript) in skeletonCandidates)
                    {
                        // Parse skin mappings for same-name skeletons (existing behavior)
                        if (spineAtlasNames.Contains(skelName))
                        {
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

                        // Save skeleton JSON for rendering if it's a multi-slot assembly
                        // (single-slot skeletons don't need compositing — the extracted sprite IS the icon)
                        if (spineAtlasData.Count > 0 && skelScript.Contains("\"bones\""))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(skelScript);
                                var root = doc.RootElement;
                                if (root.TryGetProperty("bones", out _) && root.TryGetProperty("slots", out var slotsArr))
                                {
                                    // Count slots that have a default attachment (= visible layers)
                                    int visibleSlots = 0;
                                    foreach (var slot in slotsArr.EnumerateArray())
                                        if (slot.TryGetProperty("attachment", out _))
                                            visibleSlots++;

                                    // Only save multi-slot skeletons that need assembly
                                    if (visibleSlots > 1)
                                    {
                                        Directory.CreateDirectory(spineRawDir);
                                        File.WriteAllText(Path.Combine(spineRawDir, $"{skelName}.skel"), skelScript);
                                    }
                                }
                            }
                            catch { /* Not valid JSON — skip */ }
                        }
                    }

                    // Save atlas files for rendering
                    if (spineAtlasData.Count > 0)
                    {
                        Directory.CreateDirectory(spineRawDir);
                        foreach (var (atlasName, (atlasText, textureName)) in spineAtlasData)
                        {
                            File.WriteAllText(Path.Combine(spineRawDir, $"{atlasName}.atlas"), atlasText);
                            // Record atlas→texture mapping
                            File.WriteAllText(Path.Combine(spineRawDir, $"{atlasName}.atlas.tex"),
                                textureName);
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
        _firstFileSuffixMap.Clear();

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

        // Post-process: fix inconsistent naming where first file has no suffix but duplicates do
        // e.g., Popup_Shared_Art.png + Popup_Shared_Art_SP_FerretPet2025.png → rename first to include suffix
        FixInconsistentDuplicateNames(outputDir, globalTextureMap);

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

        // ── Render Spine skeleton icons (on background thread) ─────────
        var spineRawDir = Path.Combine(outputDir, "_SpineRaw");
        if (Directory.Exists(spineRawDir))
        {
            onProgress?.Invoke("Assembling Spine icons...", bundleFiles.Length, bundleFiles.Length, totalTextures);
            var assembled = await Task.Run(() => RenderSpineIcons(spineRawDir, outputDir, onProgress, ct), ct);
            if (assembled > 0)
                onProgress?.Invoke($"Assembled {assembled} Spine icons", bundleFiles.Length, bundleFiles.Length, totalTextures);

            // Clean up raw Spine files — no longer needed after rendering
            try { Directory.Delete(spineRawDir, recursive: true); }
            catch { /* ignore — non-critical */ }
        }

        return new ExtractionResult(bundleFiles.Length, processed, totalTextures, totalSkipped, failed, warningList);
    }

    // ── Spine Icon Rendering ─────────────────────────────────────────

    /// <summary>
    /// Renders Spine skeleton icons from raw atlas+skeleton files saved during extraction.
    /// Each skeleton is rendered with its matching atlas and saved to Assembled/ subfolder.
    /// </summary>
    private static int RenderSpineIcons(string spineRawDir, string outputDir,
        Action<string, int, int, int>? onProgress, CancellationToken ct)
    {
        var assembledDir = Path.Combine(outputDir, "Assembled");
        var atlasFiles = Directory.GetFiles(spineRawDir, "*.atlas");
        var skelFiles = Directory.GetFiles(spineRawDir, "*.skel");
        if (atlasFiles.Length == 0 || skelFiles.Length == 0) return 0;

        AppLogger.Info($"[SPINE] Rendering pass: {atlasFiles.Length} atlas(es), {skelFiles.Length} skeleton(s)");

        // Load atlas data + pre-cache region names (avoid re-parsing per skeleton)
        var atlases = new Dictionary<string, (string Text, string TexturePath, HashSet<string> Regions)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var atlasFile in atlasFiles)
        {
            var atlasName = Path.GetFileNameWithoutExtension(atlasFile);
            var atlasText = File.ReadAllText(atlasFile);
            var texFile = atlasFile + ".tex";
            var textureName = File.Exists(texFile) ? File.ReadAllText(texFile).Trim() : atlasName;
            var texturePath = Path.Combine(outputDir, textureName + ".png");
            if (!File.Exists(texturePath))
            {
                AppLogger.Info($"[SPINE] Texture not found for atlas '{atlasName}': {texturePath}");
                continue;
            }
            var regions = SpineRenderService.GetAtlasRegionNames(atlasText);
            atlases[atlasName] = (atlasText, texturePath, regions);
        }

        if (atlases.Count == 0) return 0;

        // Match each skeleton to an atlas by sprite name overlap
        var skelAtlasMap = new Dictionary<string, string>(); // skel file → atlas name
        foreach (var skelFile in skelFiles)
        {
            if (ct.IsCancellationRequested) return 0;
            var skelName = Path.GetFileNameWithoutExtension(skelFile);
            try
            {
                var skelJson = File.ReadAllText(skelFile);
                var spriteNames = ExtractSkelSpriteNames(skelJson);
                if (spriteNames.Count == 0) continue;

                string? bestAtlas = null;
                int bestOverlap = 0;
                foreach (var (atlasName, (_, _, regions)) in atlases)
                {
                    int overlap = spriteNames.Count(s => regions.Contains(s));
                    if (overlap > bestOverlap) { bestOverlap = overlap; bestAtlas = atlasName; }
                }

                if (bestAtlas != null && bestOverlap > 0)
                    skelAtlasMap[skelFile] = bestAtlas;
            }
            catch (Exception ex)
            {
                AppLogger.Info($"[SPINE] Match failed for '{skelName}': {ex.Message}");
            }
        }

        if (skelAtlasMap.Count == 0) return 0;
        int totalToRender = skelAtlasMap.Count;
        AppLogger.Info($"[SPINE] Matched {totalToRender} skeletons to atlases, rendering...");
        onProgress?.Invoke($"Assembling Spine icons (0/{totalToRender})...", 0, totalToRender, 0);

        Directory.CreateDirectory(assembledDir);
        int rendered = 0;

        // Group skeletons by atlas to share loaded texture
        foreach (var group in skelAtlasMap.GroupBy(kv => kv.Value))
        {
            if (ct.IsCancellationRequested) break;
            var (atlasText, texturePath, _) = atlases[group.Key];

            Image<Rgba32>? texture = null;
            try
            {
                texture = Image.Load<Rgba32>(texturePath);

                foreach (var (skelFile, _) in group)
                {
                    if (ct.IsCancellationRequested) break;
                    var skelName = Path.GetFileNameWithoutExtension(skelFile);
                    var outPath = Path.Combine(assembledDir, skelName + ".png");
                    if (File.Exists(outPath)) { rendered++; continue; }

                    try
                    {
                        var skelJson = File.ReadAllText(skelFile);
                        using var icon = SpineRenderService.RenderIcon(skelJson, atlasText, texture, "Select")
                                      ?? SpineRenderService.RenderIcon(skelJson, atlasText, texture, "Idle");
                        if (icon != null)
                        {
                            icon.SaveAsPng(outPath);
                            rendered++;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Info($"[SPINE] Render failed '{skelName}': {ex.GetType().Name}: {ex.Message}");
                    }

                    onProgress?.Invoke($"Assembling Spine icons ({rendered}/{totalToRender}) — {skelName}",
                        rendered, totalToRender, rendered);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Info($"[SPINE] Failed to load texture for atlas '{group.Key}': {ex.Message}");
            }
            finally { texture?.Dispose(); }
        }

        // Create prefab-name aliases so Image Optimiser can find rendered icons by prefab name
        // Skeleton files: "FactoryBoulevard_ConcreteMixer_01" → prefab: "FactoryBoulevard_ConcreteMixer01Dirty"
        int aliases = 0;
        try
        {
            var mappingPath = Path.Combine(Path.GetDirectoryName(outputDir)!, "pool_tag_to_prefab_mapping.json");
            if (File.Exists(mappingPath) && Directory.Exists(assembledDir))
            {
                var mappingJson = File.ReadAllText(mappingPath);
                var mapping = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson);
                if (mapping != null)
                {
                    // Build lookup: normalized skeleton name → rendered PNG path
                    var renderedFiles = Directory.GetFiles(assembledDir, "*.png");
                    var skelLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in renderedFiles)
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        // Normalize: remove underscore before digits ("_01" → "01")
                        var normalized = System.Text.RegularExpressions.Regex.Replace(name, @"_(?=\d)", "");
                        skelLookup[normalized] = f;
                    }

                    foreach (var kv in mapping)
                    {
                        var prefabName = kv.Value;
                        // Strip "-UI" suffix (same as SpriteMetadataService)
                        if (prefabName.EndsWith("-UI", StringComparison.OrdinalIgnoreCase))
                            prefabName = prefabName[..^3];

                        var aliasPath = Path.Combine(assembledDir, prefabName + ".png");
                        if (File.Exists(aliasPath)) continue;

                        // Find matching skeleton: normalized skeleton name must be a prefix of prefab name
                        foreach (var (normalizedSkel, sourcePath) in skelLookup)
                        {
                            if (prefabName.StartsWith(normalizedSkel, StringComparison.OrdinalIgnoreCase)
                                && prefabName.Length > normalizedSkel.Length)
                            {
                                File.Copy(sourcePath, aliasPath);
                                aliases++;
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info($"[SPINE] Alias creation error: {ex.Message}");
        }

        AppLogger.Info($"[SPINE] Rendered {rendered} icons to Assembled/ ({aliases} prefab aliases)");
        return rendered;
    }

    /// <summary>Extracts sprite/attachment names from a Spine skeleton JSON for atlas matching.</summary>
    private static HashSet<string> ExtractSkelSpriteNames(string skelJson)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(skelJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("skins", out var skins)) return names;

            foreach (var skin in skins.EnumerateArray())
            {
                if (!skin.TryGetProperty("attachments", out var atts)) continue;
                foreach (var slot in atts.EnumerateObject())
                    foreach (var att in slot.Value.EnumerateObject())
                    {
                        // Use "path" or "name" if present, else the attachment key
                        if (att.Value.TryGetProperty("path", out var p))
                            names.Add(p.GetString()!);
                        else if (att.Value.TryGetProperty("name", out var n))
                            names.Add(n.GetString()!);
                        else
                            names.Add(att.Name);
                    }
            }
        }
        catch { }
        return names;
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
    /// <summary>
    /// Post-extraction fix: if a file "X.png" exists AND files "X_suffix.png" also exist,
    /// the plain "X.png" was the first extracted duplicate and needs a suffix too.
    /// Finds the correct suffix from the textureFileMap and renames.
    /// </summary>
    private static void FixInconsistentDuplicateNames(string outputDir,
        System.Collections.Concurrent.ConcurrentDictionary<string, string> textureFileMap)
    {
        try
        {
            var pngFiles = Directory.GetFiles(outputDir, "*.png")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var baseName in pngFiles.ToList())
            {
                // Skip files that already have a suffix pattern (contain _ after the base)
                // We're looking for plain names like "Popup_Shared_Art" that have
                // sibling files like "Popup_Shared_Art_SP_FerretPet2025"

                var plainPath = Path.Combine(outputDir, baseName + ".png");
                if (!File.Exists(plainPath)) continue;

                // Find files that start with baseName + "_" (these are the suffixed duplicates)
                var suffixedSiblings = pngFiles
                    .Where(f => f.Length > baseName.Length + 1
                        && f.StartsWith(baseName + "_", StringComparison.OrdinalIgnoreCase)
                        && f != baseName)
                    .ToList();

                if (suffixedSiblings.Count == 0) continue;

                // Plain file exists alongside suffixed siblings — needs its own suffix
                var plainBytes = File.ReadAllBytes(plainPath);

                // Check if plain is identical to any sibling (true duplicate → delete)
                bool isDuplicate = false;
                foreach (var sib in suffixedSiblings)
                {
                    var sibPath = Path.Combine(outputDir, sib + ".png");
                    if (File.Exists(sibPath) && IsFileIdentical(sibPath, plainBytes))
                    {
                        File.Delete(plainPath);
                        isDuplicate = true;
                        // Update textureFileMap: find any entry pointing to baseName
                        var key = textureFileMap.FirstOrDefault(kv =>
                            string.Equals(kv.Value, baseName, StringComparison.OrdinalIgnoreCase)).Key;
                        if (key != null) textureFileMap[key] = sib;
                        AppLogger.Info($"FixDuplicateNames: deleted duplicate {baseName}.png (identical to {sib}.png)");
                        break;
                    }
                }
                if (isDuplicate) continue;

                // Not identical — rename to include the correct container suffix
                string candidate;
                if (_firstFileSuffixMap.TryGetValue(baseName, out var savedSuffix)
                    && !string.IsNullOrEmpty(savedSuffix))
                {
                    // Use the container suffix that was saved during extraction
                    candidate = $"{baseName}_{savedSuffix}";
                    // Ensure uniqueness
                    if (pngFiles.Contains(candidate))
                    {
                        int num = 2;
                        while (pngFiles.Contains($"{candidate}_{num}")) num++;
                        candidate = $"{candidate}_{num}";
                    }
                }
                else
                {
                    // Fallback: numeric suffix
                    int num = 0;
                    do { num++; candidate = $"{baseName}_{num}"; }
                    while (pngFiles.Contains(candidate));
                }

                var newPath = Path.Combine(outputDir, candidate + ".png");
                File.Move(plainPath, newPath);

                // Update textureFileMap
                var texKey = textureFileMap.FirstOrDefault(kv =>
                    string.Equals(kv.Value, baseName, StringComparison.OrdinalIgnoreCase)).Key;
                if (texKey != null) textureFileMap[texKey] = candidate;
                pngFiles.Remove(baseName);
                pngFiles.Add(candidate);

                AppLogger.Info($"FixDuplicateNames: renamed {baseName}.png → {candidate}.png");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"FixInconsistentDuplicateNames failed: {ex.Message}");
        }
    }

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
