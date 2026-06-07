using System.IO;
using System.Windows;
using MergeMansionWikiTools.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Pre-computed dataset for the wikitext autocomplete popup in TableGeneratorDialog.
/// Built ONCE per app-data-load (off-thread) by <see cref="AutocompleteDataService"/>;
/// reused by every dialog instance. Static across dialog lifetimes because the chain list,
/// max levels, atlas sprites, and area thumbnails don't change between dialog opens —
/// only per-dialog overrides (currentChainName / its image / tempArea) do.
/// </summary>
public record AutocompleteData(
    List<string> ChainNames,
    Dictionary<string, int> ChainMaxLevels,
    Dictionary<string, string> ChainImagePaths,
    Dictionary<string, Rect> ChainSpriteRects,
    HashSet<string> ChainSpriteRotated,
    List<string> AreaNames,
    Dictionary<string, string> AreaImagePaths,
    Dictionary<string, Rect> AreaFractionalRects);

/// <summary>
/// Builds the autocomplete dataset off-thread. Resolves: per-chain wiki names + max levels,
/// chain atlas sprites + rects (via PoolTag→Skeleton→SkinMapping, ConfigKey-as-skeleton,
/// sprite-name heuristic, direct PNG file, and biggest-area fallback), area background image
/// resolution with multi-strategy AreaId matching + AreaIconsLibrary bundle.
/// </summary>
public static class AutocompleteDataService
{
    /// <summary>Multi-strategy AreaId → Area_InfoPopupBg_{key}.png resolver. Mirrors
    /// <c>AreaFlowchartsPage.ResolveImageKey</c>: bundle → direct → strip-digits → suffix
    /// → prefix → possessive-s.</summary>
    public static string? ResolveAreaImageKey(string areaId,
        IReadOnlyDictionary<string, string> bgFiles,
        IReadOnlyDictionary<string, string>? bundleMapping)
    {
        if (string.IsNullOrEmpty(areaId)) return null;
        if (bundleMapping != null && bundleMapping.TryGetValue(areaId, out var bundleKey)
            && bgFiles.ContainsKey(bundleKey)) return bundleKey;
        if (bgFiles.ContainsKey(areaId)) return areaId;
        var stripped = areaId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (stripped.Length > 0 && stripped != areaId && bgFiles.ContainsKey(stripped)) return stripped;
        string? best = null;
        foreach (var img in bgFiles.Keys)
            if (img.Length >= 3 && areaId.EndsWith(img, StringComparison.OrdinalIgnoreCase)
                && (best == null || img.Length > best.Length)) best = img;
        if (best != null) return best;
        best = null;
        foreach (var img in bgFiles.Keys)
            if (areaId.Length >= 3 && img.EndsWith(areaId, StringComparison.OrdinalIgnoreCase)
                && (best == null || img.Length < best.Length)) best = img;
        if (best != null) return best;
        best = null;
        foreach (var img in bgFiles.Keys)
            if (img.Length >= 3 && areaId.StartsWith(img, StringComparison.OrdinalIgnoreCase)
                && (best == null || img.Length > best.Length)) best = img;
        if (best != null) return best;
        foreach (var img in bgFiles.Keys)
        {
            if (img.Length != areaId.Length + 1) continue;
            for (int i = 1; i < img.Length; i++)
            {
                if (char.ToLowerInvariant(img[i]) != 's') continue;
                if (string.Concat(img.AsSpan(0, i), img.AsSpan(i + 1))
                        .Equals(areaId, StringComparison.OrdinalIgnoreCase)) return img;
            }
        }
        return null;
    }

    /// <summary>Extracts the numeric level suffix from a sprite name (e.g. "..._12" → 12).</summary>
    private static int ExtractSpriteLevel(string spriteName)
    {
        int idx = spriteName.LastIndexOf('_');
        if (idx < 0 || idx + 1 >= spriteName.Length) return 0;
        return int.TryParse(spriteName.AsSpan(idx + 1), out var n) ? n : 0;
    }

    /// <summary>
    /// Flood-fills the PNG and returns the BIGGEST connected component as a Rect in the
    /// bottom-left Y convention the autocomplete converter expects (it re-flips at render time
    /// via topY = PixelHeight - Y - Height). Used for multi-sprite CBE atlases where JSON rects
    /// don't match the re-packed exported PNG layout — max-level merge items are the largest
    /// component, so biggest-box reliably picks them. Cached per path. Returns null on failure.
    /// </summary>
    private static Rect? GetBiggestFloodFillBox(string pngPath, Dictionary<string, Rect?> cache)
    {
        if (cache.TryGetValue(pngPath, out var cached)) return cached;
        Rect? result = null;
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(pngPath);
            int imgH = img.Height;
            var boxes = ImageProcessingService.DetectObjectsRaw(img);
            if (boxes.Count > 0)
            {
                var biggest = boxes
                    .OrderByDescending(b => b.Full.Width * b.Full.Height)
                    .First().Full;
                // Convert top-left image coords → bottom-left so the converter's Y-flip restores
                // the original top-left position. (converter: topY = H - Y - Height = biggest.Top)
                double yBl = imgH - biggest.Top - biggest.Height;
                result = new Rect(biggest.Left, yBl, biggest.Width, biggest.Height);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info($"[AUTOCOMPLETE-THUMBS] flood-fill failed for '{pngPath}': {ex.Message}");
        }
        cache[pngPath] = result;
        return result;
    }

    /// <summary>Heavy build. Run via Task.Run from caller — touches disk + parses JSON.</summary>
    public static AutocompleteData Build(
        DataService data,
        WikiMappingCache? wikiMapping,
        IReadOnlyList<LuaArea>? areas,
        string? imageDir,
        string? imgBase,
        string? apkVer)
    {
        var probe = new WikiTableGenerator(data, wikiMapping);
        var names = new List<string>(data.Chains.Count);
        var maxLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var imagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var spriteRects = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        var spriteRotated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int hits = 0, misses = 0;
        int hitsPoolTag = 0, hitsSkeleton = 0, hitsPrefix = 0, hitsDirect = 0, hitsHeuristic = 0;
        string? firstMissExample = null;
        // Cache: PNG path → biggest flood-fill box (bottom-left convention). Null when no box.
        var floodFillCache = new Dictionary<string, Rect?>(StringComparer.OrdinalIgnoreCase);

        var atlasFull = imageDir != null
            ? SpriteMetadataService.LoadFull(imageDir)
            : new AssetExtractionService.AtlasData(new List<AssetExtractionService.SpriteInfo>(),
                new List<AssetExtractionService.SkinMapping>());
        var atlasSprites = atlasFull.Sprites;
        var skinMappings = atlasFull.SkinMappings;
        var poolTagMapping = atlasFull.PoolTagMapping ?? new Dictionary<string, string>();
        var spriteByName = new Dictionary<string, AssetExtractionService.SpriteInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in atlasSprites) spriteByName.TryAdd(s.Name, s);
        var skinsBySkeleton = skinMappings
            .GroupBy(sm => sm.SkeletonName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var c in data.Chains)
        {
            if (c.HasTestTag) continue;
            var nm = probe.GetWikiChainName(c);
            if (string.IsNullOrEmpty(nm)) continue;
            names.Add(nm);
            int maxLvl = c.Items.Count > 0 ? c.Items.Max(i => i.Level) : 0;
            if (maxLvl > 0) maxLevels[nm] = maxLvl;
            if (imageDir != null)
            {
                var fileName = $"{c.ConfigKey}.png";
                var fullPath = Path.Combine(imageDir, fileName);
                List<AssetExtractionService.SpriteInfo>? chainSpritesEarly = null;

                string strategy = "none";
                if (!string.IsNullOrEmpty(c.PoolTag)
                    && poolTagMapping.TryGetValue(c.PoolTag, out var skeletonFromPool)
                    && skinsBySkeleton.TryGetValue(skeletonFromPool, out var skinsForChain))
                {
                    chainSpritesEarly = skinsForChain
                        .Select(sm => spriteByName.TryGetValue(sm.SpriteName, out var sp) ? sp : null)
                        .Where(s => s != null)
                        .Cast<AssetExtractionService.SpriteInfo>()
                        .ToList();
                    if (chainSpritesEarly.Count > 0) { strategy = "pooltag"; hitsPoolTag++; }
                }
                if ((chainSpritesEarly == null || chainSpritesEarly.Count == 0)
                    && skinsBySkeleton.TryGetValue(c.ConfigKey, out var skinsByCfg))
                {
                    chainSpritesEarly = skinsByCfg
                        .Select(sm => spriteByName.TryGetValue(sm.SpriteName, out var sp) ? sp : null)
                        .Where(s => s != null)
                        .Cast<AssetExtractionService.SpriteInfo>()
                        .ToList();
                    if (chainSpritesEarly.Count > 0) { strategy = "skeleton"; hitsSkeleton++; }
                }
                if ((chainSpritesEarly == null || chainSpritesEarly.Count == 0) && atlasSprites.Count > 0)
                {
                    chainSpritesEarly = atlasSprites
                        .Where(s => s.Name.StartsWith($"{c.ConfigKey}_", StringComparison.OrdinalIgnoreCase)
                                 || s.Name.StartsWith($"Item{c.ConfigKey}_", StringComparison.OrdinalIgnoreCase)
                                 || s.Name.Equals(c.ConfigKey, StringComparison.OrdinalIgnoreCase)
                                 || s.Name.Equals($"Item{c.ConfigKey}", StringComparison.OrdinalIgnoreCase)
                                 || System.Text.RegularExpressions.Regex.IsMatch(s.Name,
                                     $@"^Item{System.Text.RegularExpressions.Regex.Escape(c.ConfigKey)}\d+$",
                                     System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                                 || System.Text.RegularExpressions.Regex.IsMatch(s.Name,
                                     $@"^{System.Text.RegularExpressions.Regex.Escape(c.ConfigKey)}\d+$",
                                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        .ToList();
                    if (chainSpritesEarly.Count > 0 && strategy == "none") { strategy = "prefix"; hitsPrefix++; }
                }
                if (chainSpritesEarly != null && chainSpritesEarly.Count > 0)
                {
                    var atlasFileName = $"{chainSpritesEarly[0].TextureName}.png";
                    var atlasFullPath = Path.Combine(imageDir, atlasFileName);
                    if (File.Exists(atlasFullPath))
                    {
                        fileName = atlasFileName;
                        fullPath = atlasFullPath;
                    }
                }
                if (File.Exists(fullPath) && strategy == "none") { strategy = "direct"; hitsDirect++; }
                if (!File.Exists(fullPath) && atlasSprites.Count > 0 && (chainSpritesEarly == null || chainSpritesEarly.Count == 0))
                {
                    chainSpritesEarly = SpriteMetadataService.FindSpritesForChain(atlasSprites, c.ConfigKey, imageDir);
                    if (chainSpritesEarly.Count > 0)
                    {
                        var atlasFileName = $"{chainSpritesEarly[0].TextureName}.png";
                        var atlasFullPath = Path.Combine(imageDir, atlasFileName);
                        if (File.Exists(atlasFullPath))
                        {
                            fileName = atlasFileName;
                            fullPath = atlasFullPath;
                            strategy = "heuristic"; hitsHeuristic++;
                        }
                    }
                }

                if (File.Exists(fullPath))
                {
                    imagePaths[nm] = fullPath;
                    hits++;
                    if (atlasSprites.Count > 0)
                    {
                        var chainSprites = chainSpritesEarly ?? SpriteMetadataService.FindSpritesForChain(atlasSprites, c.ConfigKey, imageDir);
                        if (chainSprites.Count > 0)
                        {
                            // Max-level sprite via ParsedItem.SkinName (same approach as Image Optimiser)
                            AssetExtractionService.SpriteInfo? maxSprite = null;
                            var maxItem = c.Items.OrderByDescending(i => i.Level).FirstOrDefault();
                            string maxSpriteStrat = "none";
                            if (maxItem != null && !string.IsNullOrEmpty(maxItem.SkinName))
                            {
                                string? skelName = null;
                                if (!string.IsNullOrEmpty(c.PoolTag)
                                    && poolTagMapping.TryGetValue(c.PoolTag, out var skelByPool))
                                    skelName = skelByPool;
                                else if (skinsBySkeleton.ContainsKey(c.ConfigKey))
                                    skelName = c.ConfigKey;
                                if (skelName != null && skinsBySkeleton.TryGetValue(skelName, out var skinsForMax))
                                {
                                    var match = skinsForMax.FirstOrDefault(sm =>
                                        sm.SkinName.Equals(maxItem.SkinName, StringComparison.OrdinalIgnoreCase));
                                    if (match != null && spriteByName.TryGetValue(match.SpriteName, out var sp))
                                    { maxSprite = sp; maxSpriteStrat = "skinName"; }
                                }
                            }
                            if (maxSprite == null
                                && !string.IsNullOrEmpty(c.PoolTag)
                                && poolTagMapping.TryGetValue(c.PoolTag, out var skel)
                                && skinsBySkeleton.TryGetValue(skel, out var skinsForLvl))
                            {
                                var maxSkin = skinsForLvl
                                    .Where(sm => int.TryParse(sm.SkinName, out _))
                                    .OrderByDescending(sm => int.Parse(sm.SkinName))
                                    .FirstOrDefault();
                                if (maxSkin != null && spriteByName.TryGetValue(maxSkin.SpriteName, out var sp))
                                { maxSprite = sp; maxSpriteStrat = "numericMax"; }
                            }
                            if (maxSprite == null)
                            {
                                maxSprite = chainSprites
                                    .OrderByDescending(s => s.RectWidth * s.RectHeight)
                                    .ThenByDescending(s => ExtractSpriteLevel(s.Name))
                                    .First();
                                maxSpriteStrat = "biggest";
                            }
                            if (c.ConfigKey.Contains("FishingTool", StringComparison.OrdinalIgnoreCase)
                                || c.DisplayName.Contains("Fishing Tools", StringComparison.OrdinalIgnoreCase))
                            {
                                AppLogger.Info($"[MAX-SPRITE-DBG] chain='{c.ConfigKey}' pool='{c.PoolTag}' "
                                    + $"maxLvl={maxItem?.Level} maxSkinName='{maxItem?.SkinName}' "
                                    + $"strategy='{maxSpriteStrat}' picked='{maxSprite?.Name}' "
                                    + $"({maxSprite?.RectWidth}x{maxSprite?.RectHeight}) "
                                    + $"chainSpritesCount={chainSprites.Count}");
                            }
                            spriteRects[nm] = new Rect(
                                maxSprite!.RectX, maxSprite.RectY,
                                maxSprite.RectWidth, maxSprite.RectHeight);
                            if (maxSprite.Rotated) spriteRotated.Add(nm);

                            // CRITICAL multi-sprite-atlas correction:
                            // For CBE event atlases (many levels share one PNG, e.g. Fishing
                            // Tools) the JSON rects describe the ORIGINAL Unity packing — but the
                            // exported PNG is RE-PACKED into a different layout, so rect-crop yields
                            // the wrong region. Detected empirically: flood-fill the export PNG and
                            // take the BIGGEST component (max-level merge items are visually largest).
                            // Single-sprite chains (chainSprites.Count == 1) keep the trivially-
                            // correct rect crop. Cached per-PNG so shared atlases flood-fill once.
                            if (chainSprites.Count > 1)
                            {
                                var ffBox = GetBiggestFloodFillBox(fullPath, floodFillCache);
                                if (ffBox.HasValue)
                                {
                                    spriteRects[nm] = ffBox.Value;
                                    spriteRotated.Remove(nm);  // export PNG already upright
                                }
                            }
                        }
                    }
                }
                else
                {
                    misses++;
                    if (firstMissExample == null) firstMissExample = $"{nm} → '{fileName}' (not found in {imageDir})";
                }
            }
        }
        AppLogger.Info($"[AUTOCOMPLETE-THUMBS] resolved {hits} thumbnails ({hitsPoolTag} pooltag, {hitsSkeleton} skeleton, {hitsPrefix} prefix, {hitsDirect} direct, {hitsHeuristic} heuristic), {misses} misses. First miss: {firstMissExample ?? "(none)"}");

        // Areas
        var areaNames = areas?.Select(a => a.DisplayName).ToList() ?? new List<string>();
        var areaImagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int areaHits = 0, areaMisses = 0;
        string? firstAreaMiss = null;
        if (imageDir != null && areas != null)
        {
            var bgFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(imageDir, "Area_Info*PopupBg_*.png"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    int idx = name.LastIndexOf("PopupBg_", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) bgFiles.TryAdd(name[(idx + 8)..], f);
                }
            }
            catch { /* IO transient */ }

            Dictionary<string, string>? areaIconMapping = null;
            try
            {
                var apkDir = !string.IsNullOrEmpty(imgBase) && !string.IsNullOrEmpty(apkVer)
                    ? Path.Combine(imgBase, apkVer, "Game Files", "APK") : null;
                string? tpkPath = null;
                if (!string.IsNullOrEmpty(imgBase))
                {
                    var t1 = Path.Combine(imgBase, "classdata.tpk");
                    var t2 = !string.IsNullOrEmpty(apkVer) ? Path.Combine(imgBase, apkVer, "classdata.tpk") : null;
                    if (File.Exists(t1)) tpkPath = t1;
                    else if (t2 != null && File.Exists(t2)) tpkPath = t2;
                }
                if (apkDir != null && Directory.Exists(apkDir) && tpkPath != null)
                    areaIconMapping = AssetExtractionService.ExtractAreaIconMapping(apkDir, tpkPath);
            }
            catch (Exception ex) { AppLogger.Info($"[AUTOCOMPLETE-AREAS] bundle load failed: {ex.Message}"); }

            foreach (var a in areas)
            {
                var key = ResolveAreaImageKey(a.AreaId, bgFiles, areaIconMapping);
                if (key != null && bgFiles.TryGetValue(key, out var p))
                {
                    areaImagePaths[a.DisplayName] = p;
                    areaHits++;
                }
                else
                {
                    areaMisses++;
                    if (firstAreaMiss == null) firstAreaMiss = $"{a.DisplayName} (AreaId='{a.AreaId}', Internal='{a.InternalName}')";
                }
            }
        }
        AppLogger.Info($"[AUTOCOMPLETE-AREAS] resolved {areaHits} area thumbnails, {areaMisses} misses. First miss: {firstAreaMiss ?? "(none)"}");

        var areaFractionalRects = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in areaImagePaths)
        {
            const double sidePct = 0.90;
            areaFractionalRects[name] = new Rect(0, 0, sidePct, sidePct);
        }

        // Sort + dedupe names for autocomplete consumer convenience
        names = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Areas: prefer wiki ordering (Module:Datatable/Areas/Mapping) when available — same
        // ordering used by Area Flowcharts page. Cache is populated by EnsureMappingLoadedAsync
        // (kicked off earlier in StartAutocompletePrewarm). When wiki cache misses, the helper
        // returns double.MaxValue so unknown areas fall to the end; tie-break alphabetical.
        areaNames = areaNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => DiscordFlowchartService.GetOrderingIndex(n))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AutocompleteData(
            names, maxLevels, imagePaths, spriteRects, spriteRotated,
            areaNames, areaImagePaths, areaFractionalRects);
    }

    /// <summary>Resolves the image directory using the same fallback candidates as
    /// TableGeneratorDialog. Returns null when none exists.</summary>
    public static string? ResolveImageDir(string? imgBase, string? apkVer)
    {
        if (string.IsNullOrEmpty(imgBase)) return null;
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(apkVer)) candidates.Add(Path.Combine(imgBase, apkVer, "Export - PNGs"));
        candidates.Add(Path.Combine(imgBase, "Processed Images"));
        candidates.Add(imgBase);
        foreach (var p in candidates) if (Directory.Exists(p)) return p;
        return null;
    }
}
