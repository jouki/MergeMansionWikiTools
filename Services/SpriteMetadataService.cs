using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;
using static MergeMansionWikiTools.Services.AssetExtractionService;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Loads sprite metadata extracted from Unity asset bundles and matches
/// sprites to chain items for automatic index prediction.
/// </summary>
internal static class SpriteMetadataService
{
    private static AtlasData? _cache;
    private static string? _cachePath;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Resolves image_atlas_data.json path from the export directory.
    /// The file lives one level above Export - PNGs (in the version directory).
    /// </summary>
    private static string GetAtlasDataPath(string exportDir)
    {
        var parent = Path.GetDirectoryName(exportDir)!;
        return Path.Combine(parent, "image_atlas_data.json");
    }

    /// <summary>
    /// Loads the combined image atlas data (sprites, skin mappings, pool tag mapping)
    /// from image_atlas_data.json. Results are cached until the path changes.
    /// </summary>
    private static AtlasData LoadAtlasData(string exportDir)
    {
        var path = GetAtlasDataPath(exportDir);

        if (_cache != null && _cachePath == path)
            return _cache;

        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            _cache = JsonSerializer.Deserialize<AtlasData>(json, _jsonOpts)
                ?? new AtlasData(new List<SpriteInfo>(), new List<SkinMapping>());
            _cachePath = path;
            var poolCount = _cache.PoolTagMapping?.Count ?? 0;
            AppLogger.Info($"Loaded image_atlas_data.json ({_cache.Sprites.Count} sprites, {_cache.SkinMappings.Count} skin mappings, {poolCount} pool tags) from {path}");
            return _cache;
        }

        // Fallback: legacy separate files
        var sprites = new List<SpriteInfo>();
        var skins = new List<SkinMapping>();
        Dictionary<string, string>? poolTags = null;

        // Legacy: atlas_data.json (old name)
        var legacyAtlasPath = Path.Combine(Path.GetDirectoryName(exportDir)!, "atlas_data.json");
        if (File.Exists(legacyAtlasPath))
        {
            var legacyData = JsonSerializer.Deserialize<AtlasData>(File.ReadAllText(legacyAtlasPath), _jsonOpts);
            if (legacyData != null)
            {
                sprites = legacyData.Sprites;
                skins = legacyData.SkinMappings;
                poolTags = legacyData.PoolTagMapping;
                AppLogger.Info($"Loaded legacy atlas_data.json ({sprites.Count} sprites, {skins.Count} skin mappings)");
            }
        }
        else
        {
            // Legacy: separate files in Export - PNGs
            var legacySpritePath = Path.Combine(exportDir, "sprite_metadata.json");
            if (File.Exists(legacySpritePath))
            {
                sprites = JsonSerializer.Deserialize<List<SpriteInfo>>(File.ReadAllText(legacySpritePath), _jsonOpts)
                    ?? new List<SpriteInfo>();
                AppLogger.Info($"Loaded {sprites.Count} sprite entries from legacy {legacySpritePath}");
            }

            var legacySkinPath = Path.Combine(exportDir, "skin_mapping.json");
            if (File.Exists(legacySkinPath))
            {
                skins = JsonSerializer.Deserialize<List<SkinMapping>>(File.ReadAllText(legacySkinPath), _jsonOpts)
                    ?? new List<SkinMapping>();
                AppLogger.Info($"Loaded {skins.Count} skin mapping entries from legacy {legacySkinPath}");
            }
        }

        // Legacy: separate pool_tag_to_prefab_mapping.json
        if (poolTags == null)
        {
            var legacyPoolPath = Path.Combine(Path.GetDirectoryName(exportDir)!, "pool_tag_to_prefab_mapping.json");
            if (File.Exists(legacyPoolPath))
            {
                poolTags = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(legacyPoolPath));
                AppLogger.Info($"Loaded {poolTags?.Count ?? 0} entries from legacy pool_tag_to_prefab_mapping.json");
            }
        }

        if (sprites.Count > 0 || skins.Count > 0 || poolTags != null)
        {
            _cache = new AtlasData(sprites, skins, poolTags);
            _cachePath = path;
            return _cache;
        }

        // Fallback: find newest sibling version with atlas data
        var versionDir = Path.GetDirectoryName(exportDir);
        var baseDir = versionDir != null ? Path.GetDirectoryName(versionDir) : null;
        if (baseDir != null && Directory.Exists(baseDir))
        {
            var siblingVersions = Directory.GetDirectories(baseDir)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
            foreach (var sibDir in siblingVersions)
            {
                if (sibDir == versionDir) continue;
                var sibAtlasPath = Path.Combine(sibDir, "image_atlas_data.json");
                if (!File.Exists(sibAtlasPath)) continue;

                var json = File.ReadAllText(sibAtlasPath);
                _cache = JsonSerializer.Deserialize<AtlasData>(json, _jsonOpts)
                    ?? new AtlasData(new List<SpriteInfo>(), new List<SkinMapping>());
                _cachePath = path; // cache keyed to the ORIGINAL path so re-entry works
                var pc = _cache.PoolTagMapping?.Count ?? 0;
                AppLogger.Info($"Loaded image_atlas_data.json from fallback version {Path.GetFileName(sibDir)} ({_cache.Sprites.Count} sprites, {_cache.SkinMappings.Count} skins, {pc} pool tags)");
                return _cache;
            }
        }

        _cache = new AtlasData(sprites, skins, poolTags);
        _cachePath = path;
        return _cache;
    }

    /// <summary>
    /// Loads sprite metadata from image_atlas_data.json.
    /// </summary>
    public static List<SpriteInfo> Load(string exportDir) => LoadAtlasData(exportDir).Sprites;

    /// <summary>Returns the full AtlasData (sprites + skin mappings + pool tag mapping).
    /// Use when you need authoritative chain → skeleton → sprite resolution rather than
    /// pure sprite-name heuristics.</summary>
    public static AtlasData LoadFull(string exportDir) => LoadAtlasData(exportDir);

    /// <summary>
    /// Loads skin mappings from image_atlas_data.json.
    /// </summary>
    public static List<SkinMapping> LoadSkinMappings(string exportDir) => LoadAtlasData(exportDir).SkinMappings;

    /// <summary>
    /// Invalidates the cache so next Load() re-reads from disk.
    /// </summary>
    public static void InvalidateCache()
    {
        _cache = null;
        _cachePath = null;
    }

    /// <summary>
    /// Finds all sprites belonging to a given texture name.
    /// </summary>
    public static List<SpriteInfo> GetSpritesForTexture(List<SpriteInfo> all, string textureName)
    {
        return all.Where(s => string.Equals(s.TextureName, textureName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Loads the PoolTag → prefab name mapping from image_atlas_data.json.
    /// This data is extracted from the game's GameObjectPoolConfig ScriptableObject
    /// and provides the definitive PoolTag → prefab/skeleton name mapping.
    /// </summary>
    private static Dictionary<string, string> LoadPoolTagMapping(string exportDir)
    {
        var data = LoadAtlasData(exportDir);
        if (data.PoolTagMapping != null && data.PoolTagMapping.Count > 0)
            return data.PoolTagMapping;
        return new Dictionary<string, string>();
    }

    /// <summary>
    /// Resolves a chain's PoolTag to its texture/skeleton name using the deterministic
    /// PoolConfig mapping from the game's startup_scenes_all.bundle.
    /// Falls back to direct PoolTag if no mapping file is available.
    /// </summary>
    public static string? ResolveSkeletonForPoolTag(string poolTag, string exportDir)
    {
        if (string.IsNullOrEmpty(poolTag))
            return null;

        var mapping = LoadPoolTagMapping(exportDir);
        if (mapping.TryGetValue(poolTag, out var prefabName))
        {
            // Strip UI suffix variants: "-UI" (e.g. ItemMakeupTools-UI) or "UI" (e.g. ItemGardenToolsUI)
            var textureName = prefabName;
            if (textureName.EndsWith("-UI", StringComparison.OrdinalIgnoreCase))
                textureName = textureName[..^3];
            else if (textureName.Length > 2
                && textureName.EndsWith("UI", StringComparison.Ordinal)
                && char.IsLower(textureName[^3]))
                textureName = textureName[..^2];
            AppLogger.Info($"PoolTag '{poolTag}' → '{textureName}' [PoolConfig]");
            return textureName;
        }

        // No mapping found — try PoolTag directly as texture name
        AppLogger.Info($"PoolTag '{poolTag}' not in PoolConfig mapping");
        return null;
    }

    /// <summary>
    /// Reverse lookup: given a texture/prefab name, finds the PoolTag that maps to it.
    /// Used to detect which chain an image belongs to when loaded by filename.
    /// </summary>
    public static string? ResolvePoolTagForTexture(string textureName, string exportDir)
    {
        if (string.IsNullOrEmpty(textureName))
            return null;

        var mapping = LoadPoolTagMapping(exportDir);
        foreach (var kv in mapping)
        {
            var prefabName = kv.Value;
            // Strip "-UI" suffix for comparison (same as forward lookup)
            if (prefabName.EndsWith("-UI", StringComparison.OrdinalIgnoreCase))
                prefabName = prefabName[..^3];

            // Exact match or suffix-renamed match (e.g. "Mansion2023_Tools_prefabsmansion2023_assets_all")
            if (string.Equals(prefabName, textureName, StringComparison.OrdinalIgnoreCase)
                || textureName.StartsWith(prefabName + "_", StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }

        return null;
    }

    /// <summary>
    /// Tries to find sprites for a chain by looking for textures named "Item{ConfigKey}".
    /// Falls back to partial matching if exact doesn't work.
    /// </summary>
    public static List<SpriteInfo> FindSpritesForChain(List<SpriteInfo> all, string chainConfigKey, string exportDir)
    {
        var uniqueTextures = all.Select(s => s.TextureName).Distinct().ToList();

        // Try "Item{ConfigKey}" pattern (most common for item chain atlases)
        var itemTexName = $"Item{chainConfigKey}";
        var result = GetSpritesForTexture(all, itemTexName);
        if (result.Count > 0) return result;

        // Try just the ConfigKey
        result = GetSpritesForTexture(all, chainConfigKey);
        if (result.Count > 0) return result;

        // Split CamelCase ConfigKey into parts and try "Item" + last part
        // E.g., "MaintenanceTools" → parts ["Maintenance", "Tools"] → try "ItemTools"
        var parts = SplitCamelCase(chainConfigKey);
        if (parts.Count > 1)
        {
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var suffix = string.Concat(parts.Skip(i));
                var candidate = $"Item{suffix}";
                result = GetSpritesForTexture(all, candidate);
                if (result.Count > 0) return result;
            }
        }

        // Try finding a texture whose name ends with the ConfigKey
        var endsWith = uniqueTextures.FirstOrDefault(t =>
            t.EndsWith(chainConfigKey, StringComparison.OrdinalIgnoreCase));
        if (endsWith != null)
        {
            result = GetSpritesForTexture(all, endsWith);
            if (result.Count > 0) return result;
        }

        // Try finding a texture whose name contains any CamelCase part of the ConfigKey (Item prefix)
        if (parts.Count > 1)
        {
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var suffix = string.Concat(parts.Skip(i));
                var match = uniqueTextures.FirstOrDefault(t =>
                    t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && t.StartsWith("Item", StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    result = GetSpritesForTexture(all, match);
                    if (result.Count > 0) return result;
                }
            }
        }

        return new List<SpriteInfo>();
    }

    /// <summary>
    /// Splits a CamelCase string into individual words.
    /// E.g., "MaintenanceTools" → ["Maintenance", "Tools"]
    /// </summary>
    private static List<string> SplitCamelCase(string input)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 1; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]) && !char.IsUpper(input[i - 1]))
            {
                parts.Add(input[start..i]);
                start = i;
            }
        }
        parts.Add(input[start..]);
        return parts;
    }

    /// <summary>
    /// Deterministic prediction using Spine skin mappings.
    /// Chain item SkinName → skeleton skin → sprite name → atlas position → reading order index.
    /// Returns an array of level numbers in sprite reading order (top→bottom, left→right),
    /// with 0 for unmatched sprites. Returns null if no skin mappings are available for this texture.
    /// </summary>
    /// <summary>Reading-order sort matching the flood-fill display (<see cref="ImageProcessingService"/>
    /// OrderObjects/SplitIntoObjectRows): visual rows top→bottom, within a row left→right, with a
    /// row tolerance so a few px of Y difference doesn't split one visual row. Works in atlas Unity
    /// coords (RectY up), so rows go by RectY descending (top of image first).</summary>
    internal static List<SpriteInfo> OrderSpritesReadingRows(List<SpriteInfo> sprites)
    {
        if (sprites.Count <= 1) return sprites;
        var byCenter = sprites.OrderByDescending(s => s.RectY + s.RectHeight / 2.0).ToList();
        var rows = new List<List<SpriteInfo>>();
        var cur = new List<SpriteInfo> { byCenter[0] };
        for (int i = 1; i < byCenter.Count; i++)
        {
            double prevCenter = cur[^1].RectY + cur[^1].RectHeight / 2.0;
            double currCenter = byCenter[i].RectY + byCenter[i].RectHeight / 2.0;
            double gap = prevCenter - currCenter; // descending → prev ≥ curr
            double threshold = System.Math.Max(cur[^1].RectHeight, byCenter[i].RectHeight) / 2.0;
            if (gap > threshold) { rows.Add(cur); cur = new List<SpriteInfo>(); }
            cur.Add(byCenter[i]);
        }
        rows.Add(cur);
        return rows.SelectMany(r => r.OrderBy(s => s.RectX)).ToList();
    }

    public static int[]? PredictIndicesFromSkinMapping(
        List<SpriteInfo> sprites, List<ParsedItem> chainItems,
        List<SkinMapping> allSkinMappings, string textureName)
    {
        // Find skin mappings for this skeleton/texture
        // The skeleton name matches the texture name (e.g., "ItemTools" skeleton → "ItemTools" atlas)
        var relevantMappings = allSkinMappings
            .Where(m => string.Equals(m.SkeletonName, textureName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (relevantMappings.Count == 0)
        {
            AppLogger.Info($"No skin mappings found for texture '{textureName}'");
            return null;
        }

        AppLogger.Info($"Found {relevantMappings.Count} skin mappings for texture '{textureName}'");

        // Order sprites the SAME way the flood-fill display does (OrderObjects/SplitIntoObjectRows):
        // group into visual rows by vertical-center gap (≤ half the taller sprite = same row), rows
        // top→bottom (Unity RectY desc = top of image first), within a row left→right (RectX asc).
        // A few px of Y difference must NOT reorder sprites that are visually in the same row — e.g.
        // CSE_SoloMilestone_Chest has RectY 2 vs 14, and pure "RectY desc, RectX asc" pushed the
        // left-most chest to the end, so the index string pointed at the wrong slot.
        var ordered = OrderSpritesReadingRows(sprites);

        var results = new int[ordered.Count];

        // Build reverse lookup: sprite name → SkinName
        var spriteToSkin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in relevantMappings)
        {
            spriteToSkin.TryAdd(m.SpriteName, m.SkinName);
        }

        // Build SkinName → chain item Level lookup. Two guards for over-inclusive wiki merges (many
        // same-named boxes → one chain, e.g. "Teatime Reward Box" = primary CSE_SoloMilestone_Chest1
        // + 8 aliases, where one alias maps to a DIFFERENT chest sprite):
        //   1. Per level, prefer the PRIMARY (non-alias) items — the wiki's canonical item defines the
        //      chain's sprite. Aliases are alternative names that may point at other sprites; fall back
        //      to them only when a level has no primary item.
        //   2. Within the chosen pool, the MAJORITY SkinName per level wins; a strict minority sprite
        //      belongs to a different box → dashed. Ties are kept in full so genuine same-level
        //      variants (e.g. Flower Bed L6 A/B/C, 1 item each) don't lose sprites.
        var skinToLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var levelGroup in chainItems
            .Where(i => !string.IsNullOrEmpty(i.SkinName))
            .GroupBy(i => i.Level))
        {
            var pool = levelGroup.Where(i => !i.IsAlias).ToList();
            if (pool.Count == 0) pool = levelGroup.ToList();
            var counts = pool
                .GroupBy(i => i.SkinName!, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Skin: g.Key, Count: g.Count()))
                .ToList();
            int maxCount = counts.Max(c => c.Count);
            foreach (var c in counts.Where(c => c.Count == maxCount))
                skinToLevel[c.Skin] = levelGroup.Key;
        }

        int matched = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var sprite = ordered[i];

            // Deterministic path: sprite name → skin name → chain item level
            if (spriteToSkin.TryGetValue(sprite.Name, out var skinName) &&
                skinToLevel.TryGetValue(skinName, out var level))
            {
                results[i] = level;
                matched++;
                AppLogger.Info($"Sprite '{sprite.Name}' → skin '{skinName}' → level {level} [deterministic]");
            }
            else
            {
                AppLogger.Info($"Sprite '{sprite.Name}' → skin '{skinName ?? "?"}' → no chain item match");
            }
        }

        AppLogger.Info($"Deterministic prediction: {matched}/{ordered.Count} sprites matched");
        return matched > 0 ? results : null;
    }

    /// <summary>
    /// Finds the skeleton/texture name for a chain using skin mappings.
    /// Looks for a skeleton whose skin names match the chain items' SkinNames.
    /// This is the deterministic way to find which texture belongs to a chain.
    /// </summary>
    public static string? FindTextureForChainFromSkinMapping(
        List<SkinMapping> allSkinMappings, List<ParsedItem> chainItems)
    {
        if (allSkinMappings.Count == 0 || chainItems.Count == 0)
            return null;

        // Collect SkinNames from chain items
        var chainSkinNames = chainItems
            .Where(i => !string.IsNullOrEmpty(i.SkinName))
            .Select(i => i.SkinName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (chainSkinNames.Count == 0)
            return null;

        // Numeric-only SkinNames ("1", "2", "3"...) are shared by nearly all chains —
        // they cannot reliably identify a texture. Skip deterministic lookup in that case.
        if (chainSkinNames.All(s => s.All(char.IsDigit)))
            return null;

        // Group skin mappings by skeleton, find the skeleton with most matching skins
        var bySkeletonMatch = allSkinMappings
            .GroupBy(m => m.SkeletonName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Skeleton = g.Key,
                MatchCount = g.Count(m => chainSkinNames.Contains(m.SkinName)),
                TotalSkins = g.Select(m => m.SkinName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            })
            .Where(x => x.MatchCount > 0)
            .OrderByDescending(x => x.MatchCount)
            .ThenBy(x => Math.Abs(x.TotalSkins - chainSkinNames.Count)) // prefer matching skin count
            .FirstOrDefault();

        // Require at least 30% of the chain's named skins to match — prevents a single
        // coincidental skin name from selecting a completely unrelated skeleton.
        if (bySkeletonMatch != null
            && bySkeletonMatch.MatchCount >= Math.Max(2, (int)Math.Ceiling(chainSkinNames.Count * 0.3)))
        {
            AppLogger.Info($"Skin mapping → texture '{bySkeletonMatch.Skeleton}' " +
                $"({bySkeletonMatch.MatchCount}/{chainSkinNames.Count} skins matched, " +
                $"skeleton has {bySkeletonMatch.TotalSkins} skins)");
            return bySkeletonMatch.Skeleton;
        }

        return null;
    }

    /// <summary>
    /// Deterministic texture lookup via ItemType → SpriteName in skin mappings.
    /// Each chain item has an ItemType (e.g. "MaintenanceTools_01"). The skin mapping
    /// image_atlas_data.json records which Spine skeleton (= texture) each sprite belongs to:
    ///   {skeletonName: "ItemTools", spriteName: "MaintenanceTools_01"}
    /// Matching ItemType against SpriteName gives the skeleton = texture name.
    /// This is 100% from game data — no heuristics.
    /// </summary>
    public static string? FindTextureForChainFromItemTypes(
        List<SkinMapping> allSkinMappings, List<ParsedItem> chainItems)
    {
        if (allSkinMappings.Count == 0 || chainItems.Count == 0)
            return null;

        var itemTypes = chainItems
            .Where(i => !string.IsNullOrEmpty(i.ItemType))
            .Select(i => i.ItemType!)
            .ToList();

        if (itemTypes.Count == 0)
            return null;

        // Match each ItemType against skin mapping SpriteName (exact or prefix match)
        // e.g. ItemType "MaintenanceTools_01" matches SpriteName "MaintenanceTools_01"
        var best = allSkinMappings
            .Where(m => itemTypes.Any(t =>
                string.Equals(m.SpriteName, t, StringComparison.OrdinalIgnoreCase) ||
                m.SpriteName.StartsWith(t + "_", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith(m.SpriteName + "_", StringComparison.OrdinalIgnoreCase) ||
                // Also match against AttachmentKey (item type before atlas-region "name" override)
                (!string.IsNullOrEmpty(m.AttachmentKey) && (
                    string.Equals(m.AttachmentKey, t, StringComparison.OrdinalIgnoreCase) ||
                    m.AttachmentKey.StartsWith(t + "_", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(m.AttachmentKey + "_", StringComparison.OrdinalIgnoreCase)))))
            .GroupBy(m => m.SkeletonName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Skeleton: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        if (best.Count > 0)
        {
            AppLogger.Info($"ItemType mapping → texture '{best.Skeleton}' ({best.Count} item types matched)");
            return best.Skeleton;
        }

        return null;
    }

    /// <summary>
    /// Resolves the exported PNG (base name, no extension) that actually holds a
    /// skeleton's item sprites. PoolConfig gives UNITY texture names, but the extractor
    /// may have stored the texture under a suffixed filename (cross-bundle name
    /// collisions, e.g. "LS_Common_HoodedWarbler" → "LS_Common_HoodedWarbler_LS_Shared_2.png").
    /// Sprite metadata records the actual exported file per sprite, so
    /// skeleton → skin sprites → sprite.TextureName gives the real file even when
    /// "{skeleton}.png" does not exist on disk. Returns null when the skeleton has no
    /// skin sprites or they all claim the plain skeleton name anyway.
    /// </summary>
    public static string? ResolveExportedFileForSkeleton(string exportDir, string skeletonName)
    {
        var skins = LoadSkinMappings(exportDir);
        var spriteNames = skins
            .Where(m => string.Equals(m.SkeletonName, skeletonName, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.SpriteName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (spriteNames.Count == 0) return null;

        var sprites = Load(exportDir);
        return sprites
            .Where(s => spriteNames.Contains(s.Name)
                && !string.Equals(s.TextureName, skeletonName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.TextureName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// Reverse lookup: given a texture/skeleton name, find chains that use it.
    /// Returns the SkinNames defined in the skeleton, which can be matched against chain items.
    /// </summary>
    public static HashSet<string> GetSkinNamesForTexture(List<SkinMapping> allSkinMappings, string textureName)
    {
        return allSkinMappings
            .Where(m => string.Equals(m.SkeletonName, textureName, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.SkinName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Given sprites from an atlas and chain items, predicts the reading-order indices.
    /// Uses heuristic matching as fallback when deterministic skin mapping is not available.
    /// Returns an array of level numbers in sprite reading order (top→bottom, left→right),
    /// with 0 for unmatched sprites.
    /// </summary>
    public static int[] PredictIndices(List<SpriteInfo> sprites, List<ParsedItem> chainItems)
    {
        // Sort sprites in reading order: by Y descending (Unity Y is bottom-up), then X ascending
        var ordered = sprites
            .OrderByDescending(s => s.RectY)  // top first (highest Y = top in Unity coords)
            .ThenBy(s => s.RectX)             // left first
            .ToList();

        var results = new int[ordered.Count];
        var usedLevels = new HashSet<int>();

        for (int i = 0; i < ordered.Count; i++)
        {
            var sprite = ordered[i];
            var match = MatchSpriteToItem(sprite.Name, chainItems, usedLevels);
            if (match != null)
            {
                results[i] = match.Level;
                usedLevels.Add(match.Level);
                AppLogger.Info($"Sprite '{sprite.Name}' → {match.Name} (level {match.Level}) [heuristic]");
            }
            else
            {
                AppLogger.Info($"Sprite '{sprite.Name}' → no match [heuristic]");
            }
        }

        return results;
    }

    /// <summary>
    /// Tries multiple strategies to match a sprite name to a chain item.
    /// </summary>
    private static ParsedItem? MatchSpriteToItem(string spriteName, List<ParsedItem> items, HashSet<int> usedLevels)
    {
        var available = items.Where(i => !usedLevels.Contains(i.Level)).ToList();
        if (available.Count == 0) return null;

        // Strategy 1: Exact match on ItemType
        var exact = available.FirstOrDefault(i =>
            string.Equals(i.ItemType, spriteName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Strategy 2: Exact match on SkinName
        var skinMatch = available.FirstOrDefault(i =>
            !string.IsNullOrEmpty(i.SkinName) &&
            string.Equals(i.SkinName, spriteName, StringComparison.OrdinalIgnoreCase));
        if (skinMatch != null) return skinMatch;

        // Strategy 3: Sprite name contains ItemType or vice versa
        var contains = available.FirstOrDefault(i =>
            spriteName.Contains(i.ItemType, StringComparison.OrdinalIgnoreCase) ||
            i.ItemType.Contains(spriteName, StringComparison.OrdinalIgnoreCase));
        if (contains != null) return contains;

        // Strategy 4: Extract trailing level number from sprite name (e.g., "Mansion2023_Tools_3" → level 3)
        var (spriteBase, spriteLevel) = ParseNameAndLevel(spriteName);
        if (spriteLevel > 0)
        {
            var byLevel = available.FirstOrDefault(i => i.Level == spriteLevel &&
                i.ItemType.StartsWith(spriteBase, StringComparison.OrdinalIgnoreCase));
            if (byLevel != null) return byLevel;

            var byLevelOnly = available.FirstOrDefault(i => i.Level == spriteLevel);
            if (byLevelOnly != null) return byLevelOnly;
        }

        // Strategy 5: Sprite name contains SkinName or vice versa
        var skinContains = available.FirstOrDefault(i =>
            !string.IsNullOrEmpty(i.SkinName) &&
            !(i.SkinName.Length <= 2 && i.SkinName.All(char.IsDigit)) &&
            (spriteName.Contains(i.SkinName, StringComparison.OrdinalIgnoreCase) ||
             i.SkinName.Contains(spriteName, StringComparison.OrdinalIgnoreCase)));
        if (skinContains != null) return skinContains;

        return null;
    }

    /// <summary>
    /// Extracts base name and trailing level number from a sprite name.
    /// E.g., "MaintenanceTools_05" → ("MaintenanceTools", 5)
    /// E.g., "Lamp03" → ("Lamp", 3)
    /// </summary>
    private static (string baseName, int level) ParseNameAndLevel(string name)
    {
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i]))
            i--;

        if (i < name.Length - 1 && i >= 0)
        {
            var numStr = name[(i + 1)..];
            if (int.TryParse(numStr, out var level))
            {
                var baseName = name[..(i + 1)].TrimEnd('_', '-', ' ');
                return (baseName, level);
            }
        }

        return (name, 0);
    }
}
