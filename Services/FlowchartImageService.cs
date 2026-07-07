using System.IO;
using MergeMansionWikiTools.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static MergeMansionWikiTools.Services.AssetExtractionService;
using Rectangle = SixLabors.ImageSharp.Rectangle;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Extracts and crops item icons from atlas sprite sheets for use in flowcharts.
/// Delegates all detection, prediction, and cropping to shared ImageProcessingService
/// (identical logic to Image Optimiser).
/// </summary>
internal static class FlowchartImageService
{
    /// <summary>
    /// For a set of itemTypes, extracts their icons from atlas sprite sheets.
    /// Returns a mapping of itemType → base64 PNG string.
    /// First checks processedImagesDir (shared with Image Optimiser) for existing cropped files.
    /// If not found there, crops from atlas and saves to processedImagesDir.
    /// Uses identical prediction + rotation logic as Image Optimiser.
    /// </summary>
    public static Dictionary<string, string> ExtractItemIcons(
        IEnumerable<string> itemTypes,
        DataService ds,
        string exportDir,
        string? processedImagesDir)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // Group items by chain to avoid processing the same atlas multiple times
        var byChain = new Dictionary<string, List<(string itemType, int level)>>(StringComparer.Ordinal);
        foreach (var itemType in itemTypes.Distinct(StringComparer.Ordinal))
        {
            var chainKey = ds.ResolveChainKeyFromItemType(itemType);
            var level = DataService.GetLevelFromItemType(itemType);
            if (level <= 0)
            {
                AppLogger.Info($"[FLOWCHART-IMG] Item '{itemType}': level={level}, skipped (no numeric suffix)");
                continue;
            }

            if (!byChain.TryGetValue(chainKey, out var list))
            {
                list = new List<(string, int)>();
                byChain[chainKey] = list;
            }
            list.Add((itemType, level));
        }

        if (byChain.Count == 0) return result;

        if (processedImagesDir != null)
            Directory.CreateDirectory(processedImagesDir);

        // Load atlas metadata
        var allSprites = SpriteMetadataService.Load(exportDir);
        var allSkinMappings = SpriteMetadataService.LoadSkinMappings(exportDir);
        var searchDirs = new[] { exportDir, Path.Combine(exportDir, "Assembled") };

        // Process each chain
        foreach (var (chainKey, items) in byChain)
        {
            try
            {
                var chain = ds.Chains.FirstOrDefault(c =>
                    string.Equals(c.ConfigKey, chainKey, StringComparison.Ordinal));

                // Fallback: chain was merged as alias — find the primary chain containing this ItemType
                if (chain == null)
                {
                    var sampleItemType = items[0].itemType;
                    chain = ds.Chains.FirstOrDefault(c =>
                        c.Items.Any(i => string.Equals(i.ItemType, sampleItemType, StringComparison.OrdinalIgnoreCase)));
                    if (chain != null)
                        AppLogger.Info($"[FLOWCHART-IMG] Chain '{chainKey}': resolved via alias to '{chain.ConfigKey}'");
                }

                if (chain == null)
                {
                    AppLogger.Info($"[FLOWCHART-IMG] Chain '{chainKey}': not found in DataService.Chains");
                    continue;
                }

                // Multi-texture chains (items span >1 atlas, e.g. InfiniteEnergy → UnlimitedEnergyA/C/D):
                // process each texture's items separately through the same prediction+crop path, so each
                // item is cropped from ITS own atlas. Files are named by DISPLAY level to avoid the
                // ItemType-suffix collapse (Small/Mid/Big all end _01).
                var itemTexRefs = AtlasStitcher.ResolveItemTextures(chain,
                    pt => SpriteMetadataService.ResolveSkeletonForPoolTag(pt, exportDir));
                if (AtlasStitcher.IsMultiTexture(itemTexRefs))
                {
                    var displayLevelOf = chain.Items
                        .Where(i => !string.IsNullOrEmpty(i.ItemType))
                        .GroupBy(i => i.ItemType!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Level, StringComparer.OrdinalIgnoreCase);
                    var texByItemType = itemTexRefs
                        .GroupBy(r => r.ItemType, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().TextureName, StringComparer.OrdinalIgnoreCase);

                    foreach (var texGroup in items
                        .Where(it => texByItemType.ContainsKey(it.itemType))
                        .GroupBy(it => texByItemType[it.itemType], StringComparer.OrdinalIgnoreCase))
                    {
                        ExtractGroupFromTexture(chain, texGroup.Key, texGroup.ToList(), displayLevelOf,
                            allSprites, allSkinMappings, searchDirs, processedImagesDir, result);
                    }
                    continue; // handled — skip the single-atlas path below
                }

                // Find atlas image file
                var atlasPath = FindAtlasImage(chain, exportDir, searchDirs, allSprites, allSkinMappings);
                if (atlasPath == null)
                {
                    AppLogger.Info($"[FLOWCHART-IMG] Chain '{chainKey}': no atlas image found");
                    continue;
                }

                var textureName = Path.GetFileNameWithoutExtension(atlasPath);
                AppLogger.Info($"[FLOWCHART-IMG] Chain '{chainKey}': atlas '{textureName}'");

                using var img = Image.Load<Rgba32>(atlasPath);

                // Use atlas-based prediction (identical to Image Optimiser)
                var textureSprites = SpriteMetadataService.GetSpritesForTexture(allSprites, textureName);
                var chainItems = chain.Items.OrderBy(i => i.Level).ToList();

                var levelMap = BuildLevelMap(img, textureSprites, allSkinMappings, textureName, chainKey, chainItems);
                if (levelMap == null)
                {
                    AppLogger.Info($"[FLOWCHART-IMG] Chain '{chainKey}': prediction failed");
                    continue;
                }

                // Crop and save each requested level
                foreach (var (itemType, level) in items)
                {
                    if (!levelMap.TryGetValue(level, out var entry)) continue;
                    if (processedImagesDir == null) continue;

                    var levelSuffix = level.ToString().PadLeft(2, '0');

                    // ConfigKey naming (preferred): {ConfigKey}{Level:00}.png
                    var configKeyName = $"{chain.ConfigKey}{levelSuffix}.png";
                    var configKeyPath = Path.Combine(processedImagesDir, configKeyName);

                    // Legacy naming (fallback): {TextureName}{Level:00}.png
                    var legacyName = $"{textureName}{levelSuffix}.png";
                    var legacyPath = Path.Combine(processedImagesDir, legacyName);

                    // Check existing files: ConfigKey first, then legacy (may be optimized)
                    if (File.Exists(configKeyPath))
                    {
                        result[itemType] = Convert.ToBase64String(File.ReadAllBytes(configKeyPath));
                        continue;
                    }
                    if (File.Exists(legacyPath))
                    {
                        // Materialize a ConfigKey-named copy so ConfigKey-only consumers (e.g. the
                        // Daily Trade Predictor's per-level icons) find it too — otherwise chains whose
                        // crops were saved under the legacy {TextureName}NN.png name render blank.
                        try { File.Copy(legacyPath, configKeyPath, overwrite: false); } catch { /* best effort */ }
                        result[itemType] = Convert.ToBase64String(File.ReadAllBytes(legacyPath));
                        continue;
                    }

                    // Not found — crop from atlas and save with ConfigKey name
                    ImageProcessingService.CropAndSave(img, entry.Full, entry.Main, configKeyPath, entry.Rotation);

                    if (File.Exists(configKeyPath))
                        result[itemType] = Convert.ToBase64String(File.ReadAllBytes(configKeyPath));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Info($"[FLOWCHART-IMG] Chain '{chainKey}' error: {ex.Message}");
            }
        }

        AppLogger.Info($"[FLOWCHART-IMG] Extracted {result.Count} item icons");
        return result;
    }

    /// <summary>
    /// Atlas sprites → {level: (Full, Main, Rotation)} via metadata prediction + flood-fill spatial
    /// matching (or flood-fill sequential fallback without metadata). Extracted verbatim from the
    /// single-atlas loop so the multi-texture path reuses the same logic. Null = prediction failed.
    /// </summary>
    private static Dictionary<int, (Rectangle Full, Rectangle Main, float Rotation)>? BuildLevelMap(
        Image<Rgba32> img, List<SpriteInfo> textureSprites, List<SkinMapping> allSkinMappings,
        string textureName, string chainKey, List<ParsedItem> chainItems)
    {
        Dictionary<int, (Rectangle Full, Rectangle Main, float Rotation)> levelMap;

        if (textureSprites.Count > 0)
        {
            // Step 1: Atlas-based prediction (Map Levels)
            var prediction = ImageProcessingService.PredictFromSpriteMetadata(
                textureSprites, allSkinMappings, textureName, chainItems, img.Height);

            if (prediction == null)
                return null;

            // Hybrid mapping:
            //   • LEVEL identification → from atlas sprite metadata (prediction.Indices
            //     + prediction.KeptPositions). Atlas tells us "position X = level N",
            //     reliable because skin mapping is deterministic.
            //   • CROP rect → from flood-fill detection. Flood finds tight pixel bounds
            //     of each sprite without the transparent padding that atlas rects often
            //     include, giving a cleaner crop with no oversize border.
            //
            // Previously the code paired atlas-derived levels with flood blobs by
            // POSITIONAL index (compactLevels[i] ↔ floodOrdered[i]). That breaks when
            // MergeColumnStacks collapses atlas slots into fewer flood blobs (or when
            // either side reorders): for Ready Blueprint, atlas slot 0 = L1 blueprint
            // paper, but floodOrdered[0] could be a pen because a vertically-aligned
            // pair of sprites got merged into a single blob shifted to a different
            // image-space slot.
            //
            // Fix: instead of positional 1:1, match by SPATIAL OVERLAP — for each
            // (level, atlas KeptPosition) pair, find the flood blob whose center is
            // inside the atlas rect (or, failing that, closest by distance). The flood
            // blob's tight bounds become the crop; the level comes from atlas.
            int expectedCount = prediction.Indices.Length;
            var floodOrdered = ImageProcessingService.AdaptDetectionCount(img, expectedCount);
            floodOrdered = ImageProcessingService.OrderObjects(floodOrdered);

            AppLogger.Info($"[FLOWCHART-IMG] Chain '{chainKey}': " +
                $"kept={prediction.KeptPositions.Count}, flood={floodOrdered.Count}, " +
                $"levels=[{string.Join(" ", prediction.Indices)}]");

            levelMap = new Dictionary<int, (Rectangle, Rectangle, float)>();
            var usedFlood = new HashSet<int>();
            int kpCount = Math.Min(prediction.KeptPositions.Count, prediction.Indices.Length);
            for (int i = 0; i < kpCount; i++)
            {
                int level = prediction.Indices[i];
                if (level <= 0 || levelMap.ContainsKey(level)) continue;
                float rot = i < prediction.Rotations.Length ? prediction.Rotations[i] : 0f;
                var atlasRect = prediction.KeptPositions[i].Full;
                var atlasCenterX = atlasRect.Left + atlasRect.Width / 2.0;
                var atlasCenterY = atlasRect.Top + atlasRect.Height / 2.0;

                // Find best flood blob: prefer the one whose center lies inside
                // atlasRect; if none, the one whose center is closest to atlasCenter.
                int bestIdx = -1;
                double bestDist = double.MaxValue;
                bool bestInside = false;
                for (int f = 0; f < floodOrdered.Count; f++)
                {
                    if (usedFlood.Contains(f)) continue;
                    var fr = floodOrdered[f].Full;
                    var fx = fr.Left + fr.Width / 2.0;
                    var fy = fr.Top + fr.Height / 2.0;
                    bool inside = fx >= atlasRect.Left && fx <= atlasRect.Right
                               && fy >= atlasRect.Top  && fy <= atlasRect.Bottom;
                    double dx = fx - atlasCenterX, dy = fy - atlasCenterY;
                    double dist = dx * dx + dy * dy;
                    // Inside-center beats any outside; otherwise nearest center wins.
                    if (inside && !bestInside) { bestInside = true; bestIdx = f; bestDist = dist; }
                    else if (inside == bestInside && dist < bestDist) { bestIdx = f; bestDist = dist; }
                }

                if (bestIdx >= 0)
                {
                    usedFlood.Add(bestIdx);
                    var flood = floodOrdered[bestIdx];
                    levelMap[level] = (flood.Full, flood.Main, rot);
                }
                else
                {
                    // No flood blob available — fall back to the atlas rect itself.
                    // Crop will include the transparent border but at least it's the right sprite.
                    levelMap[level] = (atlasRect, prediction.KeptPositions[i].Main, rot);
                }
            }
        }
        else
        {
            // No sprite metadata — flood-fill + sequential assignment
            var fallbackObjects = ImageProcessingService.DetectObjects(img);
            var fallbackOrdered = ImageProcessingService.OrderObjects(fallbackObjects);
            levelMap = new Dictionary<int, (Rectangle, Rectangle, float)>();
            int count = Math.Min(chainItems.Count, fallbackOrdered.Count);
            for (int i = 0; i < count; i++)
            {
                if (!levelMap.ContainsKey(chainItems[i].Level))
                    levelMap[chainItems[i].Level] = (fallbackOrdered[i].Full, fallbackOrdered[i].Main, 0f);
            }
        }

        return levelMap;
    }

    /// <summary>
    /// Crops the given items from ONE specific texture (used for multi-texture chains, where each
    /// item lives in its own atlas). Same prediction+flood-fill as the single-atlas path via
    /// <see cref="BuildLevelMap"/>; output files are named by DISPLAY level
    /// ({ConfigKey}{displayLevel:00}.png) so same-suffix items don't collapse onto one file.
    /// </summary>
    private static void ExtractGroupFromTexture(
        ParsedChain chain, string textureName, List<(string itemType, int level)> items,
        Dictionary<string, int> displayLevelOf,
        List<SpriteInfo> allSprites, List<SkinMapping> allSkinMappings, string[] searchDirs,
        string? processedImagesDir, Dictionary<string, string> result)
    {
        string? atlasPath = null;
        foreach (var dir in searchDirs)
        {
            var p = Path.Combine(dir, $"{textureName}.png");
            if (File.Exists(p)) { atlasPath = p; break; }
        }
        if (atlasPath == null)
        {
            AppLogger.Info($"[FLOWCHART-IMG] Chain '{chain.ConfigKey}' texture '{textureName}': PNG not found");
            return;
        }

        try
        {
            using var img = Image.Load<Rgba32>(atlasPath);
            var textureSprites = SpriteMetadataService.GetSpritesForTexture(allSprites, textureName);
            var chainItems = chain.Items.OrderBy(i => i.Level).ToList();

            var levelMap = BuildLevelMap(img, textureSprites, allSkinMappings, textureName, chain.ConfigKey, chainItems);
            if (levelMap == null || levelMap.Count == 0)
            {
                AppLogger.Info($"[FLOWCHART-IMG] Chain '{chain.ConfigKey}' texture '{textureName}': no level map");
                return;
            }

            foreach (var (itemType, atlasLevel) in items)
            {
                int displayLevel = displayLevelOf.TryGetValue(itemType, out var dl) ? dl : 0;
                if (displayLevel <= 0 || processedImagesDir == null) continue;

                // Which levelMap entry holds this item's sprite: the atlas-level key when present
                // (prediction indexed by GetLevelFromItemType), else the display level, else — when
                // the texture holds a single sprite (typical for per-duration atlases like
                // UnlimitedEnergyA/C/D) — that sole entry.
                (Rectangle Full, Rectangle Main, float Rotation) entry;
                if (levelMap.TryGetValue(atlasLevel, out var e1)) entry = e1;
                else if (levelMap.TryGetValue(displayLevel, out var e2)) entry = e2;
                else if (levelMap.Count == 1) entry = levelMap.Values.First();
                else continue;

                var outName = $"{chain.ConfigKey}{displayLevel.ToString().PadLeft(2, '0')}.png";
                var outPath = Path.Combine(processedImagesDir, outName);
                if (!File.Exists(outPath))
                    ImageProcessingService.CropAndSave(img, entry.Full, entry.Main, outPath, entry.Rotation);
                if (File.Exists(outPath))
                    result[itemType] = Convert.ToBase64String(File.ReadAllBytes(outPath));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info($"[FLOWCHART-IMG] Chain '{chain.ConfigKey}' texture '{textureName}' error: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the atlas PNG file for a chain using the same priority as Image Optimiser.
    /// </summary>
    private static string? FindAtlasImage(
        ParsedChain chain, string exportDir, string[] searchDirs,
        List<SpriteInfo> allSprites, List<SkinMapping> allSkinMappings)
    {
        var candidates = new List<string>();

        // Priority 1: PoolConfig mapping
        if (!string.IsNullOrEmpty(chain.PoolTag))
        {
            var texName = SpriteMetadataService.ResolveSkeletonForPoolTag(chain.PoolTag, exportDir);
            if (texName != null) candidates.Add($"{texName}.png");
        }

        // Priority 2: ItemType → SpriteName in skin mappings
        var itemTypeTexture = SpriteMetadataService.FindTextureForChainFromItemTypes(
            allSkinMappings, chain.Items.ToList());
        if (itemTypeTexture != null) candidates.Add($"{itemTypeTexture}.png");

        // Priority 3: SkinName mapping
        var skinTexture = SpriteMetadataService.FindTextureForChainFromSkinMapping(
            allSkinMappings, chain.Items.ToList());
        if (skinTexture != null) candidates.Add($"{skinTexture}.png");

        // Priority 4: CamelCase suffix heuristic
        if (allSprites.Count > 0)
        {
            var matchedSprites = SpriteMetadataService.FindSpritesForChain(
                allSprites, chain.ConfigKey, exportDir);
            if (matchedSprites.Count > 0)
                candidates.Add($"{matchedSprites[0].TextureName}.png");
        }

        // Priority 5: Item{ConfigKey}.png pattern
        candidates.Add($"Item{chain.ConfigKey}.png");
        if (chain.MergedFromConfigKeys != null)
            foreach (var mk in chain.MergedFromConfigKeys)
                candidates.Add($"Item{mk}.png");

        // Deduplicate
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate)) continue;

            foreach (var dir in searchDirs)
            {
                var fullPath = Path.Combine(dir, candidate);
                if (File.Exists(fullPath)) return fullPath;

                // Check suffix-renamed files
                var baseName = Path.GetFileNameWithoutExtension(candidate);
                var suffixed = Directory.GetFiles(dir, $"{baseName}_*.png").FirstOrDefault();
                if (suffixed != null) return suffixed;
            }
        }

        return null;
    }

    /// <summary>
    /// Computes a tight bounding box of high-alpha pixels (alpha > 80) within a region.
    /// This gives the "Main" rect matching Image Optimiser's flood-fill Main detection,
    /// but computed locally per sprite — no global flood-fill needed.
    /// </summary>
    private static Rectangle ComputeMainRect(Image<Rgba32> img, Rectangle full)
    {
        int minX = full.Right, minY = full.Bottom;
        int maxX = full.Left - 1, maxY = full.Top - 1;

        int top = Math.Max(0, full.Top);
        int bottom = Math.Min(img.Height, full.Top + full.Height);
        int left = Math.Max(0, full.Left);
        int right = Math.Min(img.Width, full.Left + full.Width);

        for (int py = top; py < bottom; py++)
        {
            for (int px = left; px < right; px++)
            {
                if (img[px, py].A > ImageProcessingService.MainAlphaThreshold)
                {
                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    if (py < minY) minY = py;
                    if (py > maxY) maxY = py;
                }
            }
        }

        // No high-alpha pixels found — fall back to full rect
        if (maxX < minX || maxY < minY) return full;

        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
