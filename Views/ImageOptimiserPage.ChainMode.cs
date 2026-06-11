using System.IO;
using System.Windows;
using MergeMansionWikiTools.Helpers;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Point = SixLabors.ImageSharp.Point;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using Image = SixLabors.ImageSharp.Image;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// ImageOptimiserPage — chain mode domain: entering/exiting chain mode, chain image
/// suggestion, chain ↔ texture matching (skin mappings, PoolTag, heuristics) and
/// chain name resolution (Levenshtein).
/// </summary>
public partial class ImageOptimiserPage
{
    // ── Chain mode state ──
    private ParsedChain? _activeChain;
    private string? _resolvedFilenameBase;

    // ── Chain suggestion (from Map Indices) ──
    private ParsedChain? _pendingChainSuggestion;

    private string? _suggestedImagePath;

    private void ShowChainSuggestion(ParsedChain chain)
    {
        _pendingChainSuggestion = chain;
        _pendingAutoLink = null;
        txtAutoLinkMessage.Text = $"Detected chain: {chain.DisplayName} ({chain.Items.Count} items)";
        btnAutoLink.Content = "Link";
        autoLinkBanner.Visibility = Visibility.Visible;
        UpdatePreviewMargins();
        AppLogger.Info($"Chain suggestion: '{chain.ConfigKey}' from Map Indices");
    }

    /// <summary>
    /// Finds a chain whose items use the given texture, using deterministic skin mapping first.
    /// Falls back to heuristic ConfigKey matching if no skin mapping is available.
    /// </summary>
    /// <summary>
    /// Detects if multiple different chains reference the same atlas image file.
    /// Uses PoolConfig mapping (PoolTag → TextureName) to find all chains that map to this texture.
    /// </summary>
    private bool IsMultiChainTexture(string filePath)
    {
        var chains = _main.DataService?.Chains;
        if (chains == null || chains.Count == 0) return false;

        var exportDir = GetExportDir();
        if (exportDir == null) return false;

        var textureName = System.IO.Path.GetFileNameWithoutExtension(filePath);

        // Find all chains whose PoolTag resolves to this texture name
        // Exclude chains tagged "Test" (dev/placeholder chains with reused PoolTags)
        var matchedChains = new List<string>();
        foreach (var chain in chains)
        {
            if (string.IsNullOrEmpty(chain.PoolTag)) continue;
            if (chain.HasTestTag) continue;

            var resolved = SpriteMetadataService.ResolveSkeletonForPoolTag(chain.PoolTag, exportDir);
            if (resolved != null && string.Equals(resolved, textureName, StringComparison.OrdinalIgnoreCase))
                matchedChains.Add(chain.ConfigKey);
        }

        if (matchedChains.Count > 1)
            AppLogger.Info($"[MULTI-CHAIN] '{textureName}' shared by: {string.Join(", ", matchedChains)}");

        return matchedChains.Count > 1;
    }

    private ParsedChain? FindChainForTexture(string textureName,
        List<AssetExtractionService.SkinMapping>? skinMappings = null)
    {
        var chains = _main.DataService?.Chains;
        if (chains == null || chains.Count == 0) return null;

        // Strategy 1: Deterministic — use skin mappings to find which chain's SkinNames
        // match the skins defined in this skeleton/texture.
        // Only useful when skin names are non-numeric (e.g., actual item names).
        // Numeric skin names ("1","2","3"...) are shared by nearly all chains — useless for detection.
        if (skinMappings != null && skinMappings.Count > 0)
        {
            var textureSkinNames = SpriteMetadataService.GetSkinNamesForTexture(skinMappings, textureName);
            bool allNumeric = textureSkinNames.All(s => s.All(char.IsDigit));

            if (textureSkinNames.Count > 0 && !allNumeric)
            {
                ParsedChain? bestChain = null;
                int bestMatchCount = 0;

                foreach (var chain in chains)
                {
                    var chainSkinNames = chain.Items
                        .Where(i => !string.IsNullOrEmpty(i.SkinName))
                        .Select(i => i.SkinName!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var matchCount = chainSkinNames.Count(s => textureSkinNames.Contains(s));
                    if (matchCount > bestMatchCount)
                    {
                        bestMatchCount = matchCount;
                        bestChain = chain;
                    }
                }

                if (bestChain != null && bestMatchCount >= 2)
                {
                    AppLogger.Info($"Deterministic chain detection: '{bestChain.ConfigKey}' " +
                        $"({bestMatchCount} skin matches for texture '{textureName}')");
                    return bestChain;
                }
            }
        }

        // Strategy 2: Reverse PoolTag lookup — texture name → PoolTag → chain
        var exportDir = GetExportDir();
        if (exportDir != null)
        {
            var matchedPoolTag = SpriteMetadataService.ResolvePoolTagForTexture(textureName, exportDir);
            if (matchedPoolTag != null)
            {
                var match = chains.FirstOrDefault(c =>
                    string.Equals(c.PoolTag, matchedPoolTag, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    AppLogger.Info($"Reverse PoolTag match: '{match.ConfigKey}' (texture '{textureName}' → PoolTag '{matchedPoolTag}')");
                    return match;
                }
            }
        }

        // Strategy 3: Heuristic — prefix stripping and ConfigKey matching
        var candidates = new List<string> { textureName };
        foreach (var prefix in new[] { "Hideout", "Mansion2023_", "Mansion_", "Item", "Event" })
        {
            if (textureName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && textureName.Length > prefix.Length)
                candidates.Add(textureName[prefix.Length..]);
        }

        ParsedChain? best = null;
        int bestScore = 0;

        foreach (var chain in chains)
        {
            var configKey = chain.ConfigKey;
            if (string.IsNullOrEmpty(configKey)) continue;

            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, configKey, StringComparison.OrdinalIgnoreCase))
                    return chain;

                if (configKey.Contains(candidate, StringComparison.OrdinalIgnoreCase) && candidate.Length > bestScore)
                {
                    best = chain;
                    bestScore = candidate.Length;
                }
                if (candidate.Contains(configKey, StringComparison.OrdinalIgnoreCase) && configKey.Length > bestScore)
                {
                    best = chain;
                    bestScore = configKey.Length;
                }
            }
        }

        if (bestScore >= 4) return best;

        return null;
    }

    // ══════════════════════════════════════════════════════════════
    //  CHAIN MODE
    // ══════════════════════════════════════════════════════════════

    public void EnterChainMode(ParsedChain chain)
    {
        _activeChain = chain;
        _resolvedFilenameBase = null;

        chainModeBanner.Visibility = Visibility.Visible;
        txtChainName.Text = chain.DisplayName;
        txtChainItemCount.Text = $"{chain.Items.Count} items in chain";

        // Start resolving wiki filename base in background
        _ = ResolveChainFilenameAsync(chain.DisplayName);

        // Try to find matching atlas image in Export folder
        TrySuggestChainImage(chain);

        // Enable scissors on images with detected objects (even single objects for 1:1 crop)
        if (_selectedCluster != null)
        {
            bool changed = false;
            foreach (var oi in _selectedCluster.Images)
            {
                if (oi.DetectedObjects.Count > 0 && !oi.IsScissorsActive)
                {
                    oi.IsScissorsActive = true;
                    changed = true;
                }
            }
            if (changed)
            {
                bool anyScissors = _selectedCluster.Images.Any(i => i.IsScissorsActive);
                indexInputPanel.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
                splitButtonGroup.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
                refreshButtonGroup.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
                btnToggleRects.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
                RebuildThumbnailStrip();
            }
        }

        UpdatePredictButtonVisibility();

        // Auto-enable UseChainName for multi-chain atlases
        if (_selectedCluster != null && !_selectedCluster.UseChainName)
        {
            foreach (var oi in _selectedCluster.Images)
            {
                if (IsMultiChainTexture(oi.FilePath))
                {
                    _selectedCluster.UseChainName = true;
                    break;
                }
            }
        }

        // Auto-predict if images are already loaded
        TryAutoPredict();

        // Refresh detection overlay (sprite positions may have changed)
        UpdateDetectionOverlay();
    }

    private void BtnExitChainMode_Click(object sender, RoutedEventArgs e)
    {
        _activeChain = null;
        _resolvedFilenameBase = null;
        chainModeBanner.Visibility = Visibility.Collapsed;
        txtDetectionMethod.Visibility = Visibility.Collapsed;
        DismissSuggestion();
        UpdatePredictButtonVisibility();
        UpdatePreviewMargins();

        // Re-run flood fill detection to recalculate rectangles (sprite-based positions
        // from chain mode may no longer be relevant)
        if (_selectedCluster != null)
        {
            foreach (var oi in _selectedCluster.Images.Where(i => i.IsScissorsActive))
            {
                try
                {
                    using var img = Image.Load<Rgba32>(oi.FilePath);
                    var rawObjects = DetectObjectsRaw(img);
                    var objects = MergeColumnStacks(rawObjects);
                    oi.UnmergedAlgorithmObjects = rawObjects;
                    oi.AlgorithmObjects = objects;
                    oi.DefaultDetectionSource = DetectionSource.Algorithm;
                    oi.PerObjectDetectionSource = null;
                    oi.RawDetectedObjects = objects;
                    oi.DetectedObjects = objects;
                    oi.ObjectRotations = null;
                }
                catch { /* detection failed — non-critical */ }
            }
            UpdateDetectionOverlay();
        }
    }

    private void TrySuggestChainImage(ParsedChain chain)
    {
        _suggestedImagePath = null;
        imageSuggestionBanner.Visibility = Visibility.Collapsed;
        txtDetectionMethod.Visibility = Visibility.Collapsed;

        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
            return;

        var exportDir = System.IO.Path.Combine(basePath, version, "Export - PNGs");
        if (!Directory.Exists(exportDir))
            return;

        var searchDirs = new[] { exportDir, System.IO.Path.Combine(exportDir, "Assembled") };

        // Build candidate filenames with detection method labels (highest priority first).
        // Each entry: (filename, method label for debug).
        var candidates = new List<(string FileName, string Method)>();

        // ── Priority 1: PoolConfig mapping (deterministic, from game data) ──
        // PoolConfig MonoBehaviour in startup_scenes_all.bundle maps PoolTag → prefab name.
        // e.g. PoolTag "MaintenanceTools" → prefab "Mansion2023_Tools" → Mansion2023_Tools.png
        if (!string.IsNullOrEmpty(chain.PoolTag))
        {
            var textureName = SpriteMetadataService.ResolveSkeletonForPoolTag(chain.PoolTag, exportDir);
            if (textureName != null)
                candidates.Add(($"{textureName}.png", "PoolConfig"));
        }

        var allSkinMappings = SpriteMetadataService.LoadSkinMappings(exportDir);

        // ── Priority 2: ItemType → SpriteName in skin mappings → skeleton ──
        var itemTypeTexture = SpriteMetadataService.FindTextureForChainFromItemTypes(
            allSkinMappings, chain.Items.ToList());
        if (itemTypeTexture != null)
            candidates.Add(($"{itemTypeTexture}.png", "ItemType skin mapping"));

        // ── Priority 3: SkinName mapping (reliable for non-numeric named skins) ──
        var skinTexture = SpriteMetadataService.FindTextureForChainFromSkinMapping(
            allSkinMappings, chain.Items.ToList());
        if (skinTexture != null)
            candidates.Add(($"{skinTexture}.png", "SkinName mapping"));

        // ── Priority 4: CamelCase suffix heuristic (fallback) ──
        if (candidates.Count == 0)
        {
            var allSprites = SpriteMetadataService.Load(exportDir);
            if (allSprites.Count > 0)
            {
                var matchedSprites = SpriteMetadataService.FindSpritesForChain(
                    allSprites, chain.ConfigKey, exportDir);
                if (matchedSprites.Count > 0)
                {
                    var texName = matchedSprites[0].TextureName;
                    candidates.Add(($"{texName}.png", "CamelCase heuristic"));
                }
            }
        }

        // ── Priority 5: Item{ConfigKey}.png pattern + merged keys ──
        candidates.Add(($"Item{chain.ConfigKey}.png", "Item{{ConfigKey}} pattern"));
        if (chain.MergedFromConfigKeys != null)
            foreach (var mk in chain.MergedFromConfigKeys)
                candidates.Add(($"Item{mk}.png", "Item{{MergedKey}} pattern"));

        // Deduplicate candidates (keep first occurrence = highest priority)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueCandidates = new List<(string FileName, string Method)>();
        foreach (var c in candidates)
        {
            if (seen.Add(c.FileName))
                uniqueCandidates.Add(c);
        }

        foreach (var (candidate, method) in uniqueCandidates)
        {
            foreach (var dir in searchDirs)
            {
                var fullPath = System.IO.Path.Combine(dir, candidate);
                if (!File.Exists(fullPath))
                {
                    if (!Directory.Exists(dir)) continue;
                    // Extractor may have suffix-renamed the file (e.g. due to naming conflict)
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(candidate);
                    var suffixed = Directory.GetFiles(dir, $"{baseName}_*.png").FirstOrDefault();
                    if (suffixed == null) continue;
                    fullPath = suffixed;
                }

                // Skip if already loaded
                var candidateFileName = System.IO.Path.GetFileName(fullPath);
                if (AllImages.Any(img => string.Equals(
                    System.IO.Path.GetFileName(img.FilePath),
                    candidateFileName,
                    StringComparison.OrdinalIgnoreCase)))
                    return;

                if (_main.Settings.DebugMode)
                {
                    AppLogger.Info($"[IMAGE] Chain '{chain.ConfigKey}': loaded '{candidateFileName}' via {method}");
                    txtDetectionMethod.Text = $"Image: {candidateFileName}  ·  Method: {method}";
                    txtDetectionMethod.Visibility = Visibility.Visible;
                }

                AddImages(new[] { fullPath });
                return;
            }
        }
    }

    private void BtnSuggestionLoad_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_suggestedImagePath) || !File.Exists(_suggestedImagePath))
            return;

        var path = _suggestedImagePath;
        DismissSuggestion();
        AddImages(new[] { path });
        ProcessSplit();
    }

    private void BtnSuggestionDismiss_Click(object sender, RoutedEventArgs e) => DismissSuggestion();

    private void DismissSuggestion()
    {
        _suggestedImagePath = null;
        imageSuggestionBanner.Visibility = Visibility.Collapsed;
    }

    private async Task ResolveChainFilenameAsync(string chainName)
    {
        try
        {
            var filename = await WikiMappingService.ResolveWikiFilenameAsync(chainName);
            if (filename != null)
            {
                // Strip "01.png" to get the base
                if (filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && filename.Length > 6)
                    _resolvedFilenameBase = filename[..^6];
                else
                    _resolvedFilenameBase = filename[..^4];
            }
        }
        catch { /* non-critical */ }
    }

    private static string SplitCamelCase(string input)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            if (i > 0 && char.IsUpper(input[i]) && !char.IsUpper(input[i - 1]))
                sb.Append(' ');
            else if (i > 1 && char.IsUpper(input[i]) && char.IsUpper(input[i - 1])
                     && i + 1 < input.Length && char.IsLower(input[i + 1]))
                sb.Append(' ');
            sb.Append(input[i]);
        }
        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════
    //  CHAIN NAME MATCHING (Levenshtein)
    // ══════════════════════════════════════════════════════════════

    private string? TryMatchChainName(string filePath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(filePath);

        // Try sprite metadata or PoolTag-based matching (most accurate for atlas textures)
        var exportDir = GetExportDir();
        if (exportDir != null)
        {
            var allSprites = SpriteMetadataService.Load(exportDir);
            var allSkinMappings = SpriteMetadataService.LoadSkinMappings(exportDir);
            if (allSprites.Count > 0)
            {
                var textureSprites = SpriteMetadataService.GetSpritesForTexture(allSprites, name);
                if (textureSprites.Count > 0)
                {
                    var matchedChain = FindChainForTexture(name, allSkinMappings);
                    if (matchedChain != null)
                        return matchedChain.DisplayName;
                }
            }

            // Reverse PoolTag lookup: texture name → PoolTag → chain
            var matchedPoolTag = SpriteMetadataService.ResolvePoolTagForTexture(name, exportDir);
            if (matchedPoolTag != null)
            {
                var poolTagChains = _main.DataService?.Chains;
                var match = poolTagChains?.FirstOrDefault(c =>
                    string.Equals(c.PoolTag, matchedPoolTag, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match.DisplayName;
            }
        }

        if (name.StartsWith("Item", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
            name = name.Substring(4);

        // Strip trailing digits (level numbers like "01", "02")
        var cleanedName = name;
        while (cleanedName.Length > 0 && char.IsDigit(cleanedName[^1]))
            cleanedName = cleanedName[..^1];

        // Try Levenshtein matching against loaded chains
        var chains = _main.DataService?.Chains;
        if (chains != null && chains.Count > 0 && cleanedName.Length > 0)
        {
            var candidates = new List<string> { cleanedName };
            for (int idx = cleanedName.IndexOf('_'); idx >= 0 && idx < cleanedName.Length - 1; idx = cleanedName.IndexOf('_', idx + 1))
                candidates.Add(cleanedName.Substring(idx + 1));

            ParsedChain? best = null;
            double bestSim = 0;

            foreach (var chain in chains)
            {
                var keys = new List<string>();
                if (!string.IsNullOrEmpty(chain.ConfigKey))
                    keys.Add(chain.ConfigKey);
                if (chain.MergedFromConfigKeys != null)
                    keys.AddRange(chain.MergedFromConfigKeys);

                foreach (var key in keys)
                {
                    foreach (var candidate in candidates)
                    {
                        double sim = 1.0 - (double)LevenshteinDistance(candidate.ToLowerInvariant(), key.ToLowerInvariant())
                                     / Math.Max(candidate.Length, key.Length);
                        if (sim > bestSim)
                        {
                            bestSim = sim;
                            best = chain;
                        }
                    }
                }
            }

            if (best != null && bestSim >= 0.75)
                return best.DisplayName;
        }

        // Fallback: use cleaned filename (without extension, trailing digits, or "Item" prefix)
        if (cleanedName.Length > 0)
            return cleanedName;

        // Last resort: original filename without extension
        return name.Length > 0 ? name : null;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(
                Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                d[i - 1, j - 1] + cost);
        }

        return d[a.Length, b.Length];
    }
}
