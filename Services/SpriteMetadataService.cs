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
    /// Resolves atlas_data.json path from the export directory.
    /// The file lives one level above Export - PNGs (in the version directory).
    /// </summary>
    private static string GetAtlasDataPath(string exportDir)
    {
        var parent = Path.GetDirectoryName(exportDir)!;
        return Path.Combine(parent, "atlas_data.json");
    }

    /// <summary>
    /// Loads the combined atlas data (sprites + skin mappings) from atlas_data.json.
    /// Results are cached until the path changes.
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
            AppLogger.Info($"Loaded atlas_data.json ({_cache.Sprites.Count} sprites, {_cache.SkinMappings.Count} skin mappings) from {path}");
            return _cache;
        }

        // Fallback: legacy separate files in Export - PNGs
        var sprites = new List<SpriteInfo>();
        var skins = new List<SkinMapping>();

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

        _cache = new AtlasData(sprites, skins);
        _cachePath = path;
        return _cache;
    }

    /// <summary>
    /// Loads sprite metadata from atlas_data.json.
    /// </summary>
    public static List<SpriteInfo> Load(string exportDir) => LoadAtlasData(exportDir).Sprites;

    /// <summary>
    /// Loads skin mappings from atlas_data.json.
    /// </summary>
    public static List<SkinMapping> LoadSkinMappings(string exportDir) => LoadAtlasData(exportDir).SkinMappings;

    /// <summary>
    /// Invalidates the cache so next Load() re-reads from disk.
    /// </summary>
    public static void InvalidateCache()
    {
        _cache = null;
        _cachePath = null;
        _poolTagMap = null;
        _poolTagMapPath = null;
    }

    /// <summary>
    /// Finds all sprites belonging to a given texture name.
    /// </summary>
    public static List<SpriteInfo> GetSpritesForTexture(List<SpriteInfo> all, string textureName)
    {
        return all.Where(s => string.Equals(s.TextureName, textureName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static Dictionary<string, string>? _poolTagMap;
    private static string? _poolTagMapPath;

    /// <summary>
    /// Loads the pool_tag_to_prefab_mapping.json from the version directory.
    /// This file is extracted from the game's GameObjectPoolConfig ScriptableObject
    /// and provides the definitive PoolTag → prefab/skeleton name mapping.
    /// </summary>
    private static Dictionary<string, string> LoadPoolTagMapping(string exportDir)
    {
        var parent = Path.GetDirectoryName(exportDir)!;
        var path = Path.Combine(parent, "pool_tag_to_prefab_mapping.json");

        if (_poolTagMap != null && _poolTagMapPath == path)
            return _poolTagMap;

        _poolTagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _poolTagMapPath = path;

        if (!File.Exists(path))
        {
            AppLogger.Info($"pool_tag_to_prefab_mapping.json not found at {path}");
            return _poolTagMap;
        }

        var json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (raw != null)
        {
            foreach (var kv in raw)
                _poolTagMap[kv.Key] = kv.Value;
        }
        AppLogger.Info($"Loaded pool_tag_to_prefab_mapping.json ({_poolTagMap.Count} entries)");
        return _poolTagMap;
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
            // Strip "-UI" suffix (some prefabs have it, textures don't)
            var textureName = prefabName.EndsWith("-UI", StringComparison.OrdinalIgnoreCase)
                ? prefabName[..^3]
                : prefabName;
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

            if (string.Equals(prefabName, textureName, StringComparison.OrdinalIgnoreCase))
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

        // Try finding a texture whose name contains any CamelCase part of the ConfigKey
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

        // Sort sprites in reading order: Y desc (top first), X asc (left first)
        var ordered = sprites
            .OrderByDescending(s => s.RectY)
            .ThenBy(s => s.RectX)
            .ToList();

        var results = new int[ordered.Count];

        // Build reverse lookup: sprite name → SkinName
        var spriteToSkin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in relevantMappings)
        {
            spriteToSkin.TryAdd(m.SpriteName, m.SkinName);
        }

        // Build SkinName → chain item Level lookup
        var skinToLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in chainItems)
        {
            if (!string.IsNullOrEmpty(item.SkinName))
                skinToLevel.TryAdd(item.SkinName, item.Level);
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

        if (bySkeletonMatch != null)
        {
            AppLogger.Info($"Skin mapping → texture '{bySkeletonMatch.Skeleton}' " +
                $"({bySkeletonMatch.MatchCount}/{chainSkinNames.Count} skins matched, " +
                $"skeleton has {bySkeletonMatch.TotalSkins} skins)");
            return bySkeletonMatch.Skeleton;
        }

        return null;
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
