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
        Dictionary<string, string> TextureFileMap,
        /// <summary>Spine atlas base names found in this bundle (e.g. "ItemTools").</summary>
        HashSet<string> SpineAtlasNames,
        /// <summary>Skeleton candidates NOT matched in this bundle — for cross-bundle matching.</summary>
        List<(string Name, string Script)> UnmatchedSkeletons);

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
        bool Rotated = false,
        // ── Unity Sprite metadata (extracted from Sprite asset) ────────────
        // Native canvas size (Sprite.m_Rect width/height) — what the sprite
        // would be as a standalone texture before atlas packing.
        float? CanvasWidth = null,
        float? CanvasHeight = null,
        // Pivot (Sprite.m_Pivot) — normalized 0..1; typically (0.5, 0.5).
        float? PivotX = null,
        float? PivotY = null,
        // Atlas tag (Sprite.m_AtlasTags[0]) — SpriteAtlas asset this sprite belongs to.
        string? AtlasTag = null,
        // Pixels per unit (Sprite.m_PixelsToUnits) — Unity standard 100.
        float? PixelsPerUnit = null,
        // 9-slice border (Sprite.m_Border) — left, bottom, right, top.
        float? BorderLeft = null,
        float? BorderBottom = null,
        float? BorderRight = null,
        float? BorderTop = null,
        // ── SpriteAtlas-specific (extracted from SpriteAtlas.SpriteAtlasData) ─
        // Trim offset within atlas: when non-zero, the sprite was trimmed during
        // packing and Rect doesn't include the full visual content's transparent
        // margin. In Merge Mansion atlases this is consistently (0,0).
        float? TextureRectOffsetX = null,
        float? TextureRectOffsetY = null);

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
        float ScaleY = 1,
        string AttachmentKey = ""); // original attachment key before "name" path override

    /// <summary>
    /// Combined image atlas data file: sprites, skin mappings, and PoolTag→prefab mapping.
    /// Saved as image_atlas_data.json in the version directory (parent of Export - PNGs).
    /// </summary>
    public record AtlasData(
        List<SpriteInfo> Sprites,
        List<SkinMapping> SkinMappings,
        Dictionary<string, string>? PoolTagMapping = null);

    private static readonly HttpClient _http = HttpClients.Default;
    private static readonly object _fileLock = new();
    /// <summary>Guards image_atlas_data.json read-merge-write so concurrent extractions don't overwrite each other.</summary>
    private static readonly SemaphoreSlim _atlasSaveLock = new(1, 1);
    /// <summary>Maps baseName (without suffix) → container suffix for the FIRST extracted file.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        _firstFileSuffixMap = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Base names (e.g. "Mansion2023_Tools") where a real naming conflict was detected —
    /// i.e. GetUniqueFilePath had to add a suffix because a different file with the same name already existed.
    /// Only these should trigger FixInconsistentDuplicateNames rename logic.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        _conflictBaseNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clears per-extraction naming caches (<see cref="_firstFileSuffixMap"/>, <see cref="_conflictBaseNames"/>).
    /// Must be called at the start of each extraction run — the caches are only meaningful
    /// within a single run (written in ExtractTexturesFromBundle, read in FixInconsistentDuplicateNames)
    /// and would otherwise accumulate stale entries across extractions.
    /// </summary>
    public static void ResetNamingCaches()
    {
        _firstFileSuffixMap.Clear();
        _conflictBaseNames.Clear();
    }

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
            // Cooperative cancel — return the partial count instead of throwing
            if (ct.IsCancellationRequested) break;
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
            if (ct.IsCancellationRequested) break;
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
        // Accumulated across all asset files in this bundle (for cross-bundle matching)
        var bundleSpineAtlasNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundleUnmatchedSkeletons = new List<(string Name, string Script)>();

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
                return new BundleExtractionResult(0, 0, warnings, sprites, skinMappings, textureFileMap,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);
            }

            // Iterate all assets files inside the bundle
            var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
            for (int i = 0; i < dirInfos.Count; i++)
            {
                // Cooperative cancel — finish with whatever this bundle produced so far
                // (finally + per-bundle sprite remap still run); no exception-driven flow
                if (ct.IsCancellationRequested) break;

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
                    if (ct.IsCancellationRequested) break;

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
                        string? identicalMatch = null;
                        lock (_fileLock)
                        {
                            // Check if an identical file already exists (skip duplicates).
                            // Look at ALL existing variants (base + _<suffix> + _<suffix>_N) —
                            // not just basePath. Without this, repeated re-extractions accumulate
                            // bit-identical duplicates (e.g. MainHubBadgeArt_X_2.png, _3.png ...)
                            // because the original IsFileIdentical only checked basePath.
                            var basePath = Path.Combine(outputDir, safeFileName);
                            var baseNameOnly2 = Path.GetFileNameWithoutExtension(safeFileName);
                            var ext2 = Path.GetExtension(safeFileName);
                            outPath = null;
                            try
                            {
                                var existingVariants = Directory.GetFiles(outputDir,
                                    $"{baseNameOnly2}*{ext2}");
                                foreach (var existing in existingVariants)
                                {
                                    if (IsFileIdentical(existing, pngBytes))
                                    {
                                        skipped++;
                                        outPath = null;
                                        identicalMatch = existing;
                                        goto skipWrite;
                                    }
                                }
                            }
                            catch { /* directory access failed — fall through to write */ }

                            // No existing file matches → reserve a unique path for write.
                            outPath = GetUniqueFilePath(outputDir, safeFileName, suffix);
                            File.Create(outPath).Dispose(); // reserve the path
                            skipWrite:;

                            // Store suffix for every file (even first/plain) for post-processing rename
                            var baseNameOnly = Path.GetFileNameWithoutExtension(safeFileName);
                            var safeSuffix = SanitizeFileName(Path.GetFileNameWithoutExtension(suffix));
                            _firstFileSuffixMap.TryAdd(baseNameOnly, safeSuffix);

                            // Track real naming conflicts: when GetUniqueFilePath added a suffix
                            // (outPath differs from the plain basePath) this is a true conflict —
                            // same Unity texture name found in multiple bundles with different content.
                            if (outPath != null
                                && !string.Equals(outPath, Path.Combine(outputDir, safeFileName),
                                    StringComparison.OrdinalIgnoreCase))
                                _conflictBaseNames[baseNameOnly] = true;
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
                            // Duplicate skipped — map to the ACTUAL file the identical
                            // content lives in. The match may be a suffixed variant
                            // (Name_LS_Shared_2.png), not the plain base name — mapping
                            // to safeFileName here used to point sprites at a file that
                            // may belong to a DIFFERENT same-named texture.
                            textureFileMap[texName] = Path.GetFileNameWithoutExtension(
                                identicalMatch ?? safeFileName);
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

                            // Read rect (= native canvas size for standalone sprite asset)
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

                            // Extract richer Sprite metadata (pivot, border, atlas tag, etc.)
                            float? pivotX = null, pivotY = null;
                            float? bL = null, bB = null, bR = null, bT = null;
                            float? ppu = null;
                            string? atlasTag = null;
                            try { var pv = baseField["m_Pivot"]; pivotX = pv["x"].AsFloat; pivotY = pv["y"].AsFloat; } catch { }
                            try { var br = baseField["m_Border"]; bL = br["x"].AsFloat; bB = br["y"].AsFloat; bR = br["z"].AsFloat; bT = br["w"].AsFloat; } catch { }
                            try { ppu = baseField["m_PixelsToUnits"].AsFloat; } catch { }
                            try
                            {
                                var tagsArr = baseField["m_AtlasTags.Array"];
                                if (!tagsArr.IsDummy && tagsArr.Children.Count > 0)
                                    atlasTag = tagsArr.Children[0].AsString;
                            }
                            catch { }

                            sprites.Add(new SpriteInfo(
                                spriteName, textureName, rx, ry, rw, rh,
                                Rotated: false,
                                CanvasWidth: rw, CanvasHeight: rh,
                                PivotX: pivotX, PivotY: pivotY,
                                AtlasTag: atlasTag,
                                PixelsPerUnit: ppu,
                                BorderLeft: bL, BorderBottom: bB, BorderRight: bR, BorderTop: bT));
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

                            var atlasRects = new List<(float x, float y, float w, float h, string texName, float offX, float offY)>();

                            foreach (var entry in mapField.Children)
                            {
                                try
                                {
                                    var value = entry[1]; // SpriteAtlasData
                                    var textureRect = value["textureRect"];
                                    float offX = 0f, offY = 0f;
                                    try { var off = value["textureRectOffset"]; offX = off["x"].AsFloat; offY = off["y"].AsFloat; } catch { }

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
                                        entryTexName,
                                        offX, offY));
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

                            // Create SpriteInfo entries — each sprite has its own texture page.
                            // textureRectOffset captured for completeness; in MM atlases it's (0,0).
                            int count = Math.Min(spriteNames.Count, atlasRects.Count);
                            for (int j = 0; j < count; j++)
                            {
                                var (rx, ry, rw, rh, texName, offX, offY) = atlasRects[j];
                                if (rw > 0 && rh > 0)
                                {
                                    var effectiveTex = !string.IsNullOrEmpty(texName) ? texName : fallbackTexName;
                                    sprites.Add(new SpriteInfo(
                                        spriteNames[j], effectiveTex, rx, ry, rw, rh,
                                        Rotated: false,
                                        TextureRectOffsetX: offX, TextureRectOffsetY: offY));
                                }
                            }

                            // Extra rects without names → auto-generated names
                            for (int j = count; j < atlasRects.Count; j++)
                            {
                                var (rx, ry, rw, rh, texName, offX, offY) = atlasRects[j];
                                if (rw > 0 && rh > 0)
                                {
                                    var effectiveTex = !string.IsNullOrEmpty(texName) ? texName : fallbackTexName;
                                    sprites.Add(new SpriteInfo(
                                        $"{atlasName}_{j}", effectiveTex, rx, ry, rw, rh,
                                        Rotated: false,
                                        TextureRectOffsetX: offX, TextureRectOffsetY: offY));
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
                    var unmatchedSkeletons = new List<(string Name, string Script)>();
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
                        else
                        {
                            unmatchedSkeletons.Add((skelName, skelScript));
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

                    // Accumulate for cross-bundle matching
                    foreach (var name in spineAtlasNames)
                        bundleSpineAtlasNames.Add(name);
                    bundleUnmatchedSkeletons.AddRange(unmatchedSkeletons);
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

        // Resolve sprite TextureNames to actual exported filenames WHILE the bundle
        // scope still makes the name unambiguous. Unity texture names collide ACROSS
        // bundles (e.g. two different textures both named "LS_Common_HoodedWarbler":
        // the item texture in lsdefaultitemslocal and a spine canvas elsewhere) — the
        // later global name-keyed remap can only keep one winner, so sprites from the
        // losing bundle ended up pointing at the wrong (or a nonexistent) file. Within
        // ONE bundle the name→file mapping is exact. Sprites whose texture lives in
        // another bundle (cross-bundle PPtr / inferred names) stay untouched and fall
        // through to the global remap as before.
        for (int i = 0; i < sprites.Count; i++)
        {
            if (textureFileMap.TryGetValue(sprites[i].TextureName, out var exportedFile)
                && !string.Equals(exportedFile, sprites[i].TextureName, StringComparison.Ordinal))
            {
                sprites[i] = sprites[i] with { TextureName = exportedFile };
            }
        }

        return new BundleExtractionResult(textures, skipped, warnings, sprites, skinMappings, textureFileMap,
            bundleSpineAtlasNames, bundleUnmatchedSkeletons);
    }

    // ── Extract GameObjectPoolConfig (PoolTag → prefab name) ─────────

    /// Recursively searches all string values in a field tree, collecting paths where the value
    /// contains <paramref name="searchTerm"/>. Used for pool config diagnostics.
    private static void SearchFieldForString(
        AssetTypeValueField field, string searchTerm, string path,
        List<string> results, int maxDepth)
    {
        if (field.IsDummy || maxDepth < 0) return;
        if (field.TypeName == "string")
        {
            try
            {
                var val = field.AsString;
                if (val != null && val.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                    results.Add($"{path}={val}");
            }
            catch { }
            return;
        }
        foreach (var child in field.Children)
            SearchFieldForString(child, searchTerm, path + "/" + child.FieldName, results, maxDepth - 1);
    }

    /// <summary>
    /// Reads startup_scenes_all.bundle and extracts the GameObjectPoolConfig mapping:
    /// PoolTag → prefab/skeleton name (e.g. "MaintenanceTools" → "Mansion2023_Tools").
    /// Returns the mapping dictionary to be included in image_atlas_data.json.
    /// </summary>
    public static Dictionary<string, string> ExtractPoolTagMapping(string bundleDir, string tpkPath, string outputDir)
    {
        // The PoolConfig MonoBehaviour lives in startup_scenes_all.bundle.
        // It has a "pools" array where each entry contains:
        //   itemTag: string (the PoolTag, e.g. "MaintenanceTools")
        //   prefabRef: AssetReference with a Path field (e.g. ".../Mansion2023_Tools.prefab")
        var startupPath = Path.Combine(bundleDir, "startup_scenes_all.bundle");
        if (!File.Exists(startupPath))
        {
            AppLogger.Info("[POOL] startup_scenes_all.bundle not found — skipping pool mapping");
            return new Dictionary<string, string>();
        }

        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(tpkPath);
            var bunInst = am.LoadBundleFile(startupPath, unpackIfPacked: true);

            var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
            for (int fi = 0; fi < dirInfos.Count; fi++)
            {
                AssetsFileInstance? afileInst;
                try { afileInst = am.LoadAssetsFileFromBundle(bunInst, fi); }
                catch { continue; }
                if (afileInst?.file == null) continue;

                am.LoadClassDatabaseFromPackage(afileInst.file.Metadata.UnityVersion);

                var monos = afileInst.file.GetAssetsOfType(AssetClassID.MonoBehaviour);
                foreach (var monoInfo in monos)
                {
                    AssetTypeValueField? bf;
                    try { bf = am.GetBaseField(afileInst, monoInfo); }
                    catch { continue; }
                    if (bf == null || bf.IsDummy) continue;

                    // Check if this MonoBehaviour is the PoolConfig (has "pools" array with "itemTag")
                    var poolsField = bf["pools"];
                    if (poolsField.IsDummy) continue;

                    var poolsArr = poolsField["Array"];
                    if (poolsArr.IsDummy || poolsArr.Children.Count == 0) continue;

                    // Verify structure: first entry must have "itemTag" string field
                    var firstEntry = poolsArr.Children[0];
                    var tagCheck = firstEntry["itemTag"];
                    if (tagCheck.IsDummy) continue;

                    var monoName = bf["m_Name"]?.AsString ?? "(unnamed)";
                    AppLogger.Info($"[POOL] Found PoolConfig '{monoName}' with {poolsArr.Children.Count} entries");

                    foreach (var entry in poolsArr.Children)
                    {
                        try
                        {
                            var itemTag = entry["itemTag"]?.AsString ?? "";
                            if (string.IsNullOrEmpty(itemTag)) continue;

                            // prefabRef is an AssetReference — try multiple field access patterns
                            var prefabRef = entry["prefabRef"];
                            if (prefabRef.IsDummy) continue;

                            string prefabName = "";

                            // Pattern 1: AssetReference with nested string fields (Path, m_AssetGUID, etc.)
                            // Walk all children looking for a string that looks like an asset path
                            foreach (var refChild in prefabRef.Children)
                            {
                                if (refChild.TypeName != "string") continue;
                                var val = refChild.AsString ?? "";
                                if (val.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                                {
                                    prefabName = Path.GetFileNameWithoutExtension(val);
                                    break;
                                }
                            }

                            // Pattern 2: If no .prefab path found, check for a direct path/name string
                            if (string.IsNullOrEmpty(prefabName))
                            {
                                foreach (var refChild in prefabRef.Children)
                                {
                                    if (refChild.TypeName != "string") continue;
                                    var val = refChild.AsString ?? "";
                                    if (!string.IsNullOrEmpty(val) && val.Length > 2
                                        && !val.All(c => char.IsDigit(c) || c == '-'))
                                    {
                                        // Use the last path segment as prefab name
                                        var segments = val.Split('/');
                                        prefabName = segments[^1];
                                        if (prefabName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                                            prefabName = prefabName[..^7];
                                        break;
                                    }
                                }
                            }

                            // Pattern 3: prefabRef is a PPtr — resolve via GetExtAsset
                            if (string.IsNullOrEmpty(prefabName) && prefabRef.TypeName.StartsWith("PPtr"))
                            {
                                try
                                {
                                    var ext = am.GetExtAsset(afileInst, prefabRef);
                                    if (ext.baseField != null)
                                        prefabName = ext.baseField["m_Name"]?.AsString ?? "";
                                }
                                catch { }
                            }

                            if (string.IsNullOrEmpty(prefabName)) continue;

                            // Strip -UI suffix (visual-only variant)
                            if (prefabName.EndsWith("-UI", StringComparison.OrdinalIgnoreCase))
                                prefabName = prefabName[..^3];
                            else if (prefabName.EndsWith("UI", StringComparison.OrdinalIgnoreCase)
                                && prefabName.Length > 2
                                && char.IsUpper(prefabName[^3]))
                                prefabName = prefabName[..^2];

                            mapping.TryAdd(itemTag, prefabName);
                        }
                        catch { }
                    }

                    AppLogger.Info($"[POOL] Extracted {mapping.Count} pool entries from '{monoName}'");
                    var samples = mapping.Take(5).Select(kv => $"{kv.Key} → {kv.Value}");
                    AppLogger.Info($"[POOL] Sample: {string.Join(", ", samples)}");
                }
            }
        }
        finally { am.UnloadAll(); }

        AppLogger.Info($"[POOL] Extracted {mapping.Count} pool tag mappings");
        return mapping;
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
        ResetNamingCaches();

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
        // For cross-bundle Spine atlas↔skeleton matching
        var globalSpineAtlasNames = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var globalUnmatchedSkeletons = new System.Collections.Concurrent.ConcurrentBag<(string Name, string Script)>();

        var maxParallel = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

        // Cancellation is cooperative throughout (no CancellationToken in ParallelOptions,
        // no throw): a fired token makes in-flight bundles finish early with partial
        // results and queued bundles fall through immediately — the loop then completes
        // normally and post-processing runs over whatever was extracted. The caller
        // checks the token to report "cancelled" instead of "done".
        await Parallel.ForEachAsync(bundleFiles,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel },
            async (bundlePath, _) =>
            {
                if (ct.IsCancellationRequested) return;

                var bundleName = Path.GetFileName(bundlePath);
                var current = Interlocked.Increment(ref processed);
                var pct = (int)(current * 100.0 / bundleFiles.Length);
                onProgress?.Invoke(bundleName, current, bundleFiles.Length,
                    Volatile.Read(ref totalTextures));

                BundleExtractionResult result;
                try
                {
                    result = await Task.Run(() =>
                        ExtractTexturesFromBundle(bundlePath, tpkPath, outputDir, ct));
                }
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
                foreach (var name in result.SpineAtlasNames)
                    globalSpineAtlasNames.TryAdd(name, true);
                foreach (var skel in result.UnmatchedSkeletons)
                    globalUnmatchedSkeletons.Add(skel);

                if (result.Warnings.Any(w => w.StartsWith("[LOAD]") || w.StartsWith("[ERROR]") || w.StartsWith("[VERSION]")))
                    Interlocked.Increment(ref failed);
            });

        // Cross-bundle Spine matching: skeletons not resolved locally (atlas was in a different bundle)
        var locallyMatchedNames = allSkinMappings.Select(m => m.SkeletonName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (skelName, skelScript) in globalUnmatchedSkeletons)
        {
            if (locallyMatchedNames.Contains(skelName)) continue; // already matched same-bundle
            if (!globalSpineAtlasNames.ContainsKey(skelName)) continue; // no atlas found anywhere
            try
            {
                var mappings = ParseSpineSkeletonSkins(skelName, skelScript);
                if (mappings.Count > 0)
                {
                    foreach (var m in mappings) allSkinMappings.Add(m);
                    AppLogger.Info($"[SPINE] Cross-bundle skeleton '{skelName}': {mappings.Count} skin→sprite mappings");
                }
            }
            catch { /* skip malformed skeleton */ }
        }

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

        // Extract GameObjectPoolConfig (PoolTag → prefab name) — done once, not per-bundle
        var poolTagMapping = ExtractPoolTagMapping(bundleDir, tpkPath, outputDir);

        // Post-process: fix inconsistent naming where first file has no suffix but duplicates do
        // e.g., Popup_Shared_Art.png + Popup_Shared_Art_SP_FerretPet2025.png → rename first to include suffix
        var fileRenames = FixInconsistentDuplicateNames(outputDir, globalTextureMap);

        var spriteList = allSprites.ToList();

        // Sprites resolved per-bundle already carry exported filenames — follow any
        // post-process file renames so they keep pointing at existing files.
        if (fileRenames.Count > 0)
        {
            for (int i = 0; i < spriteList.Count; i++)
            {
                if (fileRenames.TryGetValue(spriteList[i].TextureName, out var renamed))
                    spriteList[i] = spriteList[i] with { TextureName = renamed };
            }
        }

        // Fallback for sprites whose texture lives in ANOTHER bundle (cross-bundle PPtr /
        // inferred names): replace Unity texture m_Name with exported PNG filename.
        // Name-keyed and first-wins, so inherently ambiguous for colliding names — the
        // per-bundle resolution in ExtractTexturesFromBundle handles those exactly.
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
        // Save combined image atlas data — merge with any existing file so concurrent extractions don't overwrite each other.
        // Pattern: lock → read existing → union sprites + skin mappings + pool tags → write → unlock.
        var skinMapList = allSkinMappings.ToList();
        if (spriteList.Count > 0 || skinMapList.Count > 0 || poolTagMapping.Count > 0)
        {
            var parentDir = Path.GetDirectoryName(outputDir)!;
            var atlasDataPath = Path.Combine(parentDir, "image_atlas_data.json");
            await _atlasSaveLock.WaitAsync(ct);
            try
            {
                // Read existing data (may have been written by a concurrent extraction)
                if (File.Exists(atlasDataPath))
                {
                    try
                    {
                        var existing = JsonSerializer.Deserialize<AtlasData>(
                            await File.ReadAllTextAsync(atlasDataPath, ct),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (existing != null)
                        {
                            // Cross-RUN rename fixup: this run's post-process may have renamed
                            // files WRITTEN BY A PREVIOUS RUN (e.g. APK run exports the plain
                            // "LS_Common_HoodedWarbler.png", the later server-files run collides
                            // on the same texture name and renames the plain file away). The
                            // previous run's sprite entries arrive here via the "existing" merge
                            // branch, so the rename map must be applied to THEM too — otherwise
                            // they keep pointing at a file that no longer exists.
                            if (fileRenames.Count > 0 && existing.Sprites != null)
                            {
                                for (int i = 0; i < existing.Sprites.Count; i++)
                                {
                                    if (fileRenames.TryGetValue(existing.Sprites[i].TextureName, out var rn))
                                        existing.Sprites[i] = existing.Sprites[i] with { TextureName = rn };
                                }
                            }

                            // Union sprites by (name, textureName) — FRESH WINS, fall back to existing.
                            // A sprite has two entries in atlas data: atlas-member (textureName=atlas)
                            // and standalone (textureName=name). Both must coexist, so the dedup key
                            // is (name, textureName), not name alone. Fresh entries always replace
                            // existing matches — otherwise schema additions (like v0.20.45's pivot,
                            // border, canvasWidth/Height) never propagate because old entries shadow
                            // newer-format ones with the same name+texture.
                            int freshCount = spriteList.Count;
                            var freshKeys = spriteList
                                .Select(s => (s.Name, s.TextureName))
                                .ToHashSet();
                            int keptFromExisting = existing.Sprites.Count(s => !freshKeys.Contains((s.Name, s.TextureName)));
                            spriteList = spriteList
                                .Concat(existing.Sprites.Where(s => !freshKeys.Contains((s.Name, s.TextureName))))
                                .ToList();
                            int replacedByFresh = (existing.Sprites.Count + freshCount) - spriteList.Count;
                            AppLogger.Info($"Atlas data merge: {freshCount} fresh + {keptFromExisting} kept-from-existing (replaced {replacedByFresh} stale entries from previous extractions)");

                            // Union skin mappings by (SkeletonName, SkinName) — FRESH WINS, fall back to existing.
                            var freshSkinKeys = skinMapList
                                .Select(m => (m.SkeletonName, m.SkinName))
                                .ToHashSet();
                            skinMapList = skinMapList
                                .Concat(existing.SkinMappings.Where(m => !freshSkinKeys.Contains((m.SkeletonName, m.SkinName))))
                                .ToList();

                            // Merge pool tag mapping — new extraction wins (overwrite existing keys)
                            if (existing.PoolTagMapping != null && poolTagMapping.Count == 0)
                                poolTagMapping = new Dictionary<string, string>(existing.PoolTagMapping, StringComparer.OrdinalIgnoreCase);
                            else if (existing.PoolTagMapping != null)
                            {
                                foreach (var kv in existing.PoolTagMapping)
                                    poolTagMapping.TryAdd(kv.Key, kv.Value);
                            }
                        }
                    }
                    catch { /* Corrupt existing file — overwrite */ }
                }

                var atlasData = new AtlasData(spriteList, skinMapList, poolTagMapping.Count > 0 ? poolTagMapping : null);
                var json = JsonSerializer.Serialize(atlasData, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                await File.WriteAllTextAsync(atlasDataPath, json, ct);
                AppLogger.Info($"Saved image_atlas_data.json ({spriteList.Count} sprites, {skinMapList.Count} skin mappings, {poolTagMapping.Count} pool tags) to {atlasDataPath}");
            }
            finally
            {
                _atlasSaveLock.Release();
            }
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
            var atlasPath = Path.Combine(Path.GetDirectoryName(outputDir)!, "image_atlas_data.json");
            if (File.Exists(atlasPath) && Directory.Exists(assembledDir))
            {
                var atlasJson = File.ReadAllText(atlasPath);
                var atlasData = System.Text.Json.JsonSerializer.Deserialize<AtlasData>(atlasJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var mapping = atlasData?.PoolTagMapping;
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
                            var attachmentKey = attachment.Name; // item type e.g. "MaintenanceTools_01"
                            var spriteName = attachmentKey;
                            float offsetX = 0, offsetY = 0, rotation = 0, scaleX = 1, scaleY = 1;

                            if (attachment.Value.ValueKind == JsonValueKind.Object)
                            {
                                if (attachment.Value.TryGetProperty("name", out var nameOverride) &&
                                    nameOverride.ValueKind == JsonValueKind.String)
                                    spriteName = nameOverride.GetString()!; // atlas region e.g. "Mansion2023_Tools_01"

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
                                offsetX, offsetY, rotation, scaleX, scaleY, attachmentKey));
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
                            var attachmentKey = attachment.Name;
                            var spriteName = attachmentKey;
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
                                offsetX, offsetY, rotation, scaleX, scaleY, attachmentKey));
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
    /// Returns the applied file renames (old base name → new base name) so the caller
    /// can fix up sprite records that already carry exported filenames.
    /// </summary>
    private static Dictionary<string, string> FixInconsistentDuplicateNames(string outputDir,
        System.Collections.Concurrent.ConcurrentDictionary<string, string> textureFileMap)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

                // Only rename if a real naming conflict was recorded for this base name.
                // Without this guard, files like "Mansion2023_Tools_Drill_On.png" (an original
                // Unity texture name that merely starts with "Mansion2023_Tools_") would falsely
                // trigger a rename of "Mansion2023_Tools.png".
                if (!_conflictBaseNames.ContainsKey(baseName)) continue;

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
                        renames[baseName] = sib;
                        // Update textureFileMap: ALL entries pointing to baseName
                        // (multiple Unity names can share one file via identical content)
                        foreach (var kv in textureFileMap.Where(kv =>
                                string.Equals(kv.Value, baseName, StringComparison.OrdinalIgnoreCase)).ToList())
                            textureFileMap[kv.Key] = sib;
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

                renames[baseName] = candidate;
                // Update textureFileMap: ALL entries pointing to baseName
                foreach (var kv in textureFileMap.Where(kv =>
                        string.Equals(kv.Value, baseName, StringComparison.OrdinalIgnoreCase)).ToList())
                    textureFileMap[kv.Key] = candidate;
                pngFiles.Remove(baseName);
                pngFiles.Add(candidate);

                AppLogger.Info($"FixDuplicateNames: renamed {baseName}.png → {candidate}.png");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"FixInconsistentDuplicateNames failed: {ex.Message}");
        }
        return renames;
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

    // ── Area icon mapping extraction ──────────────────────────────────

    /// <summary>
    /// Extracts AreaId → sprite name mapping from AreaIconsLibrary bundle.
    /// Returns e.g. { "MansionRightFiller" → "SideFiller", "SwimmingPool" → "PoolArea", ... }
    /// </summary>
    public static Dictionary<string, string> ExtractAreaIconMapping(string bundleDir, string tpkPath)
    {
        var bundlePath = Path.Combine(bundleDir, "scriptableobjectsareaiconslibrary_assets_all.bundle");
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(bundlePath))
        {
            AppLogger.Info("[AREA-ICONS] AreaIconsLibrary bundle not found");
            return mapping;
        }

        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(tpkPath);
            var bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;

            for (int fi = 0; fi < dirInfos.Count; fi++)
            {
                AssetsFileInstance? afileInst;
                try { afileInst = am.LoadAssetsFileFromBundle(bunInst, fi); }
                catch { continue; }
                if (afileInst?.file == null) continue;

                try { am.LoadClassDatabaseFromPackage(afileInst.file.Metadata.UnityVersion); }
                catch { continue; }

                // Find MonoBehaviour named "AreaIconsLibrary"
                foreach (var info in afileInst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    try
                    {
                        var bf = am.GetBaseField(afileInst, info);
                        if (bf == null || bf.IsDummy) continue;

                        var nameField = bf["m_Name"];
                        if (nameField.IsDummy || nameField.AsString != "AreaIconsLibrary") continue;

                        var arrField = bf["areaSpritesMapping"]?["Array"];
                        if (arrField == null || arrField.IsDummy) continue;

                        foreach (var entry in arrField.Children)
                        {
                            var areaId = entry["areaId"]?.AsString;
                            var spriteName = entry["sprites"]?["BannerIconRef"]?["m_SubObjectName"]?.AsString;
                            if (string.IsNullOrEmpty(areaId) || string.IsNullOrEmpty(spriteName)) continue;

                            // Strip "Area_InfoPopupBg_" prefix to get the image key
                            const string prefix = "Area_InfoPopupBg_";
                            var imageKey = spriteName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                ? spriteName[prefix.Length..] : spriteName;

                            mapping[areaId] = imageKey;
                        }

                        AppLogger.Info($"[AREA-ICONS] Extracted {mapping.Count} area→sprite mappings from AreaIconsLibrary");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info($"[AREA-ICONS] Error reading bundle: {ex.Message}");
        }
        finally { am.UnloadAll(); }

        return mapping;
    }

    // ── Bundle diagnostic dump ─────────────────────────────────────────

    /// <summary>
    /// Dumps all assets and their field structures from a bundle to AppLogger.
    /// Used for research/debugging — discovers field layout of unknown bundles.
    /// </summary>
    public static void DumpBundleContents(string bundlePath, string tpkPath, int maxFieldDepth = 3)
    {
        if (!File.Exists(bundlePath))
        {
            AppLogger.Info($"[DUMP] Bundle not found: {bundlePath}");
            return;
        }

        AppLogger.Info($"[DUMP] === Dumping: {Path.GetFileName(bundlePath)} ===");
        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(tpkPath);
            var bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
            AppLogger.Info($"[DUMP] {dirInfos.Count} entries in bundle");

            for (int fi = 0; fi < dirInfos.Count; fi++)
            {
                AssetsFileInstance? afileInst;
                try { afileInst = am.LoadAssetsFileFromBundle(bunInst, fi); }
                catch { continue; }
                if (afileInst?.file == null) continue;

                try { am.LoadClassDatabaseFromPackage(afileInst.file.Metadata.UnityVersion); }
                catch { continue; }

                var afile = afileInst.file;
                AppLogger.Info($"[DUMP]   File[{fi}]: {dirInfos[fi].Name}, {afile.AssetInfos.Count} assets, Unity {afile.Metadata.UnityVersion}");

                foreach (var info in afile.AssetInfos)
                {
                    try
                    {
                        var bf = am.GetBaseField(afileInst, info);
                        if (bf == null || bf.IsDummy) continue;

                        string name = "(unnamed)";
                        var nameField = bf["m_Name"];
                        if (!nameField.IsDummy) name = nameField.AsString ?? "(null)";

                        AppLogger.Info($"[DUMP]     Asset: TypeId={info.TypeId} PathId={info.PathId} Name=\"{name}\"");

                        // Dump field tree recursively
                        DumpFieldTree(bf, "        ", maxFieldDepth, 0);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Info($"[DUMP]     Asset TypeId={info.TypeId} PathId={info.PathId} — error: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info($"[DUMP] Error reading bundle: {ex.Message}");
        }
        finally
        {
            am.UnloadAll();
        }
        AppLogger.Info($"[DUMP] === Done: {Path.GetFileName(bundlePath)} ===");
    }

    private static void DumpFieldTree(AssetTypeValueField field, string indent, int maxDepth, int depth)
    {
        if (depth >= maxDepth) return;

        foreach (var child in field.Children)
        {
            try
            {
                string fname = child.FieldName ?? "?";
                string ftype = child.TypeName ?? "?";
                string fval = "";

                if (ftype == "string")
                    fval = $" = \"{child.AsString ?? ""}\"";
                else if (ftype == "int" || ftype == "SInt32" || ftype == "UInt32")
                    fval = $" = {child.AsInt}";
                else if (ftype == "SInt64" || ftype == "UInt64")
                    fval = $" = {child.AsLong}";
                else if (ftype == "float")
                    fval = $" = {child.AsFloat}";
                else if (ftype == "bool")
                    fval = $" = {(child.AsBool ? "true" : "false")}";
                else if (child.Children.Count > 0)
                    fval = $" ({child.Children.Count} children)";

                AppLogger.Info($"[DUMP] {indent}{fname}: {ftype}{fval}");

                if (child.Children.Count > 0 && child.Children.Count <= 200)
                    DumpFieldTree(child, indent + "  ", maxDepth, depth + 1);
                else if (child.Children.Count > 200)
                    AppLogger.Info($"[DUMP] {indent}  ... ({child.Children.Count} children, truncated)");
            }
            catch { }
        }
    }
}
