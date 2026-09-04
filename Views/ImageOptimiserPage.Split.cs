using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MergeMansionWikiTools.Helpers;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Wpf.Ui.Controls;
using Point = SixLabors.ImageSharp.Point;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using Image = SixLabors.ImageSharp.Image;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// ImageOptimiserPage — split domain: level/index input handling, sprite metadata
/// prediction (Map Levels), output directory resolution, and the split pipeline
/// (ProcessSplit) that crops detected objects into individual item images.
/// </summary>
public partial class ImageOptimiserPage
{
    // ── Output folder redirect (for images from export dir) ──
    private string? _lastOutputDir;
    private string? _customSplitOutputDir; // session-persistent custom output folder

    // ══════════════════════════════════════════════════════════════
    //  SPLIT (for scissors-active images)
    // ══════════════════════════════════════════════════════════════

    private void InputIndices_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressIndexReset) return;

        // Refresh overlay labels — debounced (heavy redraw), fires 200 ms after last keystroke
        _overlayDebounce.Trigger(UpdateDetectionOverlay);

        if (_selectedCluster == null || !_selectedCluster.Images.Any(i => i.IsSplit)) return;

        // Compare normalized tokens — only reset if the actual indices changed
        var tokens = ImageSplitLogic.ParseIndexTokens(inputIndices.Text);
        var normalized = string.Join(" ", tokens);

        if (normalized == _selectedCluster.LastSplitIndices) return;

        // Indices changed — invalidate previous split and remove old files from optimization tracking
        foreach (var oi in _selectedCluster.Images)
        {
            if (oi.IsSplit)
            {
                foreach (var f in oi.SplitResultFiles)
                    _optimizedFiles.Remove(f);
                oi.IsSplit = false;
                oi.IsOptimized = false;
                oi.SplitResultFiles.Clear();
            }
        }
        _selectedCluster.LastSplitIndices = "";
        UpdateUploadButtonState();
    }

    private void InputIndices_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            // In single-line mode, Enter triggers split.
            // In multi-line mode (after Map Levels or manual editing), Enter adds a new line.
            if (!inputIndices.Text.Contains('\n'))
            {
                e.Handled = true;
                ProcessSplit();
            }
        }
    }

    private void BtnSplit_Click(object sender, RoutedEventArgs e) => ProcessSplit(loadExistingIfFound: false);

    private void BtnSplitDropdown_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // Use Chain Name checkbox
        var chk = new CheckBox
        {
            Content = "Use Chain Name",
            IsChecked = _selectedCluster?.UseChainName == true,
            Margin = new Thickness(0, 2, 8, 2),
        };
        var tipPanel = new System.Windows.Controls.StackPanel { MaxWidth = 300 };
        tipPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Use this when the image contains items from multiple chains.",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        tipPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Saves as {ConfigKey}{Level}.png instead of {SourceFile}{Level}.png.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.7
        });
        chk.ToolTip = tipPanel;
        ToolTipService.SetInitialShowDelay(chk, 0);
        chk.Checked += (_, _) => { if (_selectedCluster != null) _selectedCluster.UseChainName = true; };
        chk.Unchecked += (_, _) => { if (_selectedCluster != null) _selectedCluster.UseChainName = false; };
        menu.Items.Add(chk);
        menu.Items.Add(new System.Windows.Controls.Separator());

        // Split To… folder picker
        var splitToItem = new System.Windows.Controls.MenuItem { Header = "Split To…" };
        splitToItem.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select output folder for split images",
                InitialDirectory = _customSplitOutputDir
                    ?? System.IO.Path.GetDirectoryName(_selectedImage?.FilePath) ?? ""
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                _customSplitOutputDir = dlg.FolderName;
                ProcessSplit(overrideOutputDir: dlg.FolderName);
            }
        };
        menu.Items.Add(splitToItem);
        menu.PlacementTarget = btnSplitDropdown;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    // ── Sprite Metadata Prediction ──

    private void UpdatePredictButtonVisibility()
    {
        // Show predict button when scissors are active — works with or without chain
        bool show = indexInputPanel.Visibility == Visibility.Visible;
        btnPredictIndices.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewMargins();
    }

    /// <summary>
    /// Resolves the export directory for the current APK version (where exported PNGs live).
    /// </summary>
    private string? GetExportDir()
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
            return null;
        var dir = System.IO.Path.Combine(basePath, version, "Export - PNGs");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Returns the Processed Images directory in the workspace root.
    /// Creates it if it doesn't exist. Falls back to Export - Items if workspace not set.
    /// </summary>
    internal string? GetProcessedImagesDir()
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrEmpty(basePath)) return null;
        var dir = System.IO.Path.Combine(basePath, "Processed Images");
        if (!System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Returns the output directory for split/optimize results.
    /// Priority: Processed Images (workspace root) → Export - Items (sibling) → source dir.
    /// </summary>
    private (string dir, bool redirected) GetOutputDir(string sourceFilePath)
    {
        // Priority 1: Processed Images in workspace root
        var processedDir = GetProcessedImagesDir();
        if (processedDir != null)
            return (processedDir, true);

        // Priority 2: Export - Items (sibling of Export - PNGs)
        var sourceDir = System.IO.Path.GetDirectoryName(sourceFilePath)!;
        var exportDir = GetExportDir();

        if (exportDir != null)
        {
            var normSource = System.IO.Path.GetFullPath(sourceDir).TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var normExport = System.IO.Path.GetFullPath(exportDir).TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            if (string.Equals(normSource, normExport, StringComparison.OrdinalIgnoreCase))
            {
                var parent = System.IO.Path.GetDirectoryName(normExport)!;
                var outputDir = System.IO.Path.Combine(parent, "Export - Items");
                return (outputDir, true);
            }
        }

        return (sourceDir, false);
    }

    private void BtnPredictIndices_Click(object sender, RoutedEventArgs e)
    {
        RunPrediction(showWarnings: true);
    }

    /// <summary>
    /// Core prediction logic. Returns true if prediction was applied.
    /// When showWarnings is false (auto-predict), silently skips when conditions aren't met.
    /// Prefers deterministic Spine skin mapping; falls back to heuristic matching.
    /// </summary>
    private bool RunPrediction(bool showWarnings)
    {
        if (_selectedCluster == null) return false;

        var scissorsImages = _selectedCluster.Images.Where(i => i.IsScissorsActive).ToList();
        if (scissorsImages.Count == 0)
        {
            AppLogger.Info($"[PREDICT] skipped — no scissors-active image (chain '{_activeChain?.ConfigKey ?? "-"}', "
                + $"{_selectedCluster.Images.Count} image(s) in cluster)");
            return false;
        }
        AppLogger.Info($"[PREDICT] start: image '{System.IO.Path.GetFileName(scissorsImages[0].FilePath)}', "
            + $"chain '{_activeChain?.ConfigKey ?? "-"}' ({_activeChain?.Items.Count ?? 0} items), "
            + $"{scissorsImages[0].DetectedObjects.Count} detected object(s)");

        var imageFileName = System.IO.Path.GetFileNameWithoutExtension(scissorsImages[0].FilePath);

        var exportDir = GetExportDir();
        if (exportDir == null)
        {
            if (showWarnings)
            {
                infoBar.Message = "Set Image Exporter base path and APK version in Settings first.";
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.IsOpen = true;
            }
            return false;
        }

        var allSprites = SpriteMetadataService.Load(exportDir);
        var allSkinMappings = SpriteMetadataService.LoadSkinMappings(exportDir);

        if (allSprites.Count == 0)
        {
            if (showWarnings)
            {
                infoBar.Message = "image_atlas_data.json not found. Re-extract textures from APK.";
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.IsOpen = true;
            }
            return false;
        }

        var textureSprites = SpriteMetadataService.GetSpritesForImage(allSprites, imageFileName, scissorsImages[0].FilePath);

        if (textureSprites.Count > 0)
        {
            // Offer chain link via suggestion banner only on manual Map Levels click
            if (showWarnings && _activeChain == null)
            {
                var detectedChain = FindChainForTexture(imageFileName, allSkinMappings);
                if (detectedChain != null)
                    ShowChainSuggestion(detectedChain);
            }

            PredictFromSpriteMetadata(textureSprites, allSkinMappings, imageFileName);
            return true;
        }

        // No sprite metadata — offer chain link via suggestion banner only on manual click
        if (showWarnings && _activeChain == null)
        {
            var detectedChain = FindChainForTexture(imageFileName, allSkinMappings);
            if (detectedChain != null)
                ShowChainSuggestion(detectedChain);
        }

        // Sequential prediction if chain is active
        if (_activeChain != null)
        {
            var activeImg = scissorsImages[0];
            var objectCount = activeImg.DetectedObjects.Count;
            if (objectCount == 0) return false;

            var chainItems = _activeChain.Items.OrderBy(i => i.Level).ToList();
            var parts = new List<string>();
            for (int i = 0; i < objectCount; i++)
                parts.Add(i < chainItems.Count ? chainItems[i].Level.ToString() : "-");

            inputIndices.Text = string.Join(" ", parts);
            _selectedCluster.IndexText = inputIndices.Text;
            RememberAutoPrediction();

            if (showWarnings)
            {
                infoBar.Message = $"No sprite metadata for '{imageFileName}'. Sequential prediction. Verify manually.";
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.IsOpen = true;
            }
            return true;
        }

        if (showWarnings)
        {
            infoBar.Message = $"No sprite metadata for '{imageFileName}'.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
        }
        return false;
    }

    private void PredictFromSpriteMetadata(
        List<AssetExtractionService.SpriteInfo> textureSprites,
        List<AssetExtractionService.SkinMapping> allSkinMappings,
        string textureName)
    {
        // Sort sprites in same order as PredictIndices (Unity Y desc, X asc)
        var orderedSprites = ImageSplitLogic.OrderSpritesUnity(textureSprites);

        // Step 1: Build sprite index → level mapping
        // Prefer deterministic skin mapping; fall back to heuristic
        int[] indices;
        bool deterministic = false;
        if (_activeChain != null)
        {
            var chainItems = _activeChain.Items.OrderBy(i => i.Level).ToList();

            // Try deterministic prediction first
            var deterministicResult = SpriteMetadataService.PredictIndicesFromSkinMapping(
                textureSprites, chainItems, allSkinMappings, textureName);

            if (deterministicResult != null && deterministicResult.Any(l => l > 0))
            {
                indices = deterministicResult;
                deterministic = true;

                // Hybrid: fill unmatched sprites with heuristic matching
                if (indices.Any(l => l == 0))
                {
                    var heuristicIndices = SpriteMetadataService.PredictIndices(textureSprites, chainItems);
                    var hybridUsed = new HashSet<int>(indices.Where(l => l > 0));
                    for (int i = 0; i < indices.Length; i++)
                    {
                        if (indices[i] == 0 && heuristicIndices[i] > 0 && !hybridUsed.Contains(heuristicIndices[i]))
                        {
                            indices[i] = heuristicIndices[i];
                            hybridUsed.Add(heuristicIndices[i]);
                        }
                    }
                }
            }
            else
            {
                // Fall back to heuristic matching
                indices = SpriteMetadataService.PredictIndices(textureSprites, chainItems);
            }
        }
        else
        {
            // No chain: extract levels from trailing number in sprite names
            indices = new int[orderedSprites.Count];
            for (int i = 0; i < orderedSprites.Count; i++)
            {
                var name = orderedSprites[i].Name;
                int j = name.Length - 1;
                while (j >= 0 && char.IsDigit(name[j])) j--;
                if (j < name.Length - 1 && int.TryParse(name[(j + 1)..], out var level))
                    indices[i] = level;
                else
                    indices[i] = i + 1;
            }
        }

        // Step 2: Get detected objects — merge to sprite count if over-detected
        var activeImg = _selectedCluster!.Images.FirstOrDefault(i => i.IsScissorsActive);
        if (activeImg == null || activeImg.DetectedObjects.Count == 0)
        {
            var fallbackParts = indices.Select(l => l > 0 ? l.ToString() : "-").ToList();
            inputIndices.Text = string.Join(" ", fallbackParts);
            _selectedCluster.IndexText = inputIndices.Text;
            RememberAutoPrediction();
            infoBar.Message = $"Sprite metadata: {indices.Count(l => l > 0)}/{indices.Length} matched (no detection overlay).";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
            return;
        }

        // Step 3: Get image height for coordinate conversion (Unity Y → image Y)
        int imageHeight;
        try
        {
            var imgInfo = Image.Identify(activeImg.FilePath);
            imageHeight = imgInfo.Height;
        }
        catch
        {
            imageHeight = (int)orderedSprites.Max(s => s.RectY + s.RectHeight);
        }

        // Use sprite positions from game data as detected objects.
        // The image_atlas_data.json has exact positions for every sprite — more reliable
        // than image-based flood fill which can merge adjacent sprites.
        // Set both RawDetectedObjects and DetectedObjects so UpdateDetectionOverlay
        // uses the sprite-based positions as its baseline (it resets to RawDetectedObjects).
        var spriteObjects = ImageSplitLogic.SpriteObjectsFromSprites(orderedSprites, imageHeight);
        activeImg.AtlasObjects = spriteObjects;
        // Store atlas for detection source toggle; flood-fill remains default for crop.
        if (activeImg.DetectedObjects.Count == 0)
        {
            activeImg.RawDetectedObjects = spriteObjects;
            activeImg.DetectedObjects = spriteObjects;
        }

        // Step 4: Map atlas sprites to levels, deduplicate, write indices.
        // Iterate over atlas sprites (exact positions from game data) for complete coverage.
        // Deduplicate: skip sprites whose level was already output (handles textures
        // where each item has multiple sprites, e.g. front+back views).
        // Flood-fill remains the detection/crop method — overlay adapts via merge/expand.
        var orderedAtlas = OrderObjects(spriteObjects);
        var parts = new List<string>();
        var rotationsList = new List<float>();
        var keptPositions = new List<(Rectangle Full, Rectangle Main)>();
        int matched = 0;
        var usedSpriteIndices = new HashSet<int>();

        for (int objIdx = 0; objIdx < orderedAtlas.Count; objIdx++)
        {
            var obj = orderedAtlas[objIdx];
            var objCenterX = obj.Full.Left + obj.Full.Width / 2.0;
            var objCenterY = obj.Full.Top + obj.Full.Height / 2.0;

            // Find nearest unmatched sprite
            int bestIdx = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < orderedSprites.Count; i++)
            {
                if (usedSpriteIndices.Contains(i)) continue;
                var s = orderedSprites[i];
                var dx = objCenterX - (s.RectX + s.RectWidth / 2.0);
                var dy = objCenterY - (imageHeight - s.RectY - s.RectHeight / 2.0);
                var dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; bestIdx = i; }
            }

            if (bestIdx < 0) continue;
            usedSpriteIndices.Add(bestIdx);

            int level = indices[bestIdx];

            // Dedup by OVERLAP, not by level. A sprite that overlaps an already-kept one is a
            // secondary view (front/back/shadow) of the SAME item → skip it. But distinct,
            // non-overlapping sprites keep their own slot EVEN when they share a level number —
            // in a multi-chain atlas different items map to different sprites at the same level
            // (e.g. CSE_SoloMilestone_Chest: two chests both "level 1"). The old level-dedup dropped
            // the second such sprite, so the index string lost a slot ("- 1" instead of "- 1 1").
            bool overlapsKept = false;
            foreach (var kept in keptPositions)
            {
                int oL = Math.Max(obj.Full.Left, kept.Full.Left);
                int oR = Math.Min(obj.Full.Left + obj.Full.Width, kept.Full.Left + kept.Full.Width);
                int oT = Math.Max(obj.Full.Top, kept.Full.Top);
                int oB = Math.Min(obj.Full.Top + obj.Full.Height, kept.Full.Top + kept.Full.Height);
                if (oR > oL && oB > oT)
                {
                    int overlapArea = (oR - oL) * (oB - oT);
                    int spriteArea = obj.Full.Width * obj.Full.Height;
                    if (spriteArea > 0 && (double)overlapArea / spriteArea > 0.3) { overlapsKept = true; break; }
                }
            }
            if (overlapsKept) continue;

            if (level > 0) { parts.Add(level.ToString()); matched++; }
            else parts.Add("-");

            rotationsList.Add(orderedSprites[bestIdx].Rotated ? 90f : 0f);
            keptPositions.Add(obj);
        }

        // Store rotation data for use during split
        activeImg.ObjectRotations = rotationsList.ToArray();

        // Format indices with row breaks matching visual layout (max 4 rows)
        var objectRows = keptPositions.Count > 0 ? GroupIntoRows(keptPositions) : new List<int> { parts.Count };
        if (objectRows.Count >= 2 && objectRows.Count <= 4)
        {
            var sb = new System.Text.StringBuilder();
            int idx = 0;
            for (int r = 0; r < objectRows.Count; r++)
            {
                if (r > 0) sb.Append('\n');
                for (int c = 0; c < objectRows[r]; c++)
                {
                    if (c > 0) sb.Append(' ');
                    sb.Append(parts[idx++]);
                }
            }
            inputIndices.Text = sb.ToString();
        }
        else
        {
            inputIndices.Text = string.Join(" ", parts);
        }
        _selectedCluster.IndexText = inputIndices.Text;
        RememberAutoPrediction();

        var chainInfo = _activeChain != null ? $" for chain '{_activeChain.ConfigKey}'" : "";
        var method = deterministic ? "skin mapping" : "heuristic";
        int dedupTotal = parts.Count;
        infoBar.Message = $"Prediction ({method}): {matched}/{dedupTotal} matched ({textureSprites.Count} sprites in atlas).";
        infoBar.Severity = matched == dedupTotal ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        infoBar.IsOpen = true;

        AppLogger.Info($"Prediction ({method}): {matched}/{dedupTotal}{chainInfo}");
    }

    /// <summary>
    /// Auto-predicts indices if they are empty (doesn't overwrite user input).
    /// </summary>
    /// <summary>Index text this page auto-predicted last, and the chain it was predicted FOR.</summary>
    private string? _autoPredictedText;
    private string? _autoPredictedChainKey;

    /// <summary>Marks the current index text as ours (auto-predicted) for the active chain.</summary>
    private void RememberAutoPrediction()
    {
        _autoPredictedText = _selectedCluster?.IndexText;
        _autoPredictedChainKey = _activeChain?.ConfigKey ?? "";
        AppLogger.Info($"[PREDICT] result '{_autoPredictedText}' for chain '{_autoPredictedChainKey}'");
    }

    private void TryAutoPredict()
    {
        if (_selectedCluster == null) return;

        // Don't overwrite existing input — EXCEPT our own auto-prediction made for a
        // DIFFERENT chain. Entry points differ in order: Item Chains sets the chain first
        // and loads the image after, while Prepare/Season Pass adds the file first, which
        // auto-enters chain mode by PoolTag and predicts there; the chain Prepare then hands
        // over would never drive the prediction (stale levels, e.g. "- - - 1").
        var chainKey = _activeChain?.ConfigKey ?? "";
        if (!ImageSplitLogic.ShouldAutoPredict(
                _selectedCluster.IndexText, _autoPredictedText, _autoPredictedChainKey, chainKey))
            return;
        if (!string.IsNullOrWhiteSpace(_selectedCluster.IndexText))
            AppLogger.Info($"[PREDICT] re-running: text '{_selectedCluster.IndexText}' was predicted for chain "
                + $"'{_autoPredictedChainKey}', active chain is now '{chainKey}'");

        var scissorsImages = _selectedCluster.Images.Where(i => i.IsScissorsActive).ToList();
        if (scissorsImages.Count == 0) return;

        RunPrediction(showWarnings: false);
    }

    private void ProcessSplit(string? overrideOutputDir = null, bool loadExistingIfFound = false, bool suppressExistingDialog = false)
    {
        if (_selectedCluster == null || _selectedCluster.Images.Count == 0) return;

        // Collect scissors-active images in the cluster
        var scissorsImages = _selectedCluster.Images.Where(oi => oi.IsScissorsActive).ToList();
        if (scissorsImages.Count == 0) return;

        var suffixes = ImageSplitLogic.ParseIndexTokens(inputIndices.Text);
        if (suffixes.Length == 0)
        {
            infoBar.Message = "Enter level values first.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
            return;
        }

        try
        {
            // Remove old split results from optimization tracking before re-splitting
            foreach (var oi in scissorsImages)
            {
                foreach (var f in oi.SplitResultFiles)
                    _optimizedFiles.Remove(f);
                oi.IsOptimized = false;
            }

            // Gather all objects from all scissors-active images in the cluster
            var allOrdered = new List<(Rectangle Full, Rectangle Main, string SourcePath, float Rotation, bool KeepAspectRatio)>();

            // Count non-skip suffixes for merge logic
            int nonSkipCount = suffixes.Count(s => s != "-");

            foreach (var oi in scissorsImages)
            {
                // Use stored DetectedObjects (sprite-based when image_atlas_data.json is available,
                // otherwise flood-fill based). Only re-detect as fallback.
                var objects = oi.DetectedObjects.Count > 0
                    ? oi.DetectedObjects
                    : DetectObjects(Image.Load<Rgba32>(oi.FilePath));
                var ordered = OrderObjects(objects);

                // Apply merge only when single image in cluster
                if (scissorsImages.Count == 1 && ordered.Count > suffixes.Length)
                    ordered = MergeToExpectedCount(ordered, suffixes.Length);

                // Pre-compute ordered alternative sources for per-object overrides
                bool hasBoth = oi.AlgorithmObjects != null && oi.AtlasObjects != null;
                List<(Rectangle Full, Rectangle Main)>? ordAlgo = null, ordAtlas = null;
                if (hasBoth)
                {
                    ordAlgo = OrderObjects(oi.AlgorithmObjects!);
                    ordAtlas = OrderObjects(oi.AtlasObjects!);
                }

                for (int j = 0; j < ordered.Count; j++)
                {
                    var obj = GetEffectiveObject(oi, j, ordered[j], ordAlgo, ordAtlas);
                    float rot = (oi.ObjectRotations != null && j < oi.ObjectRotations.Length)
                        ? oi.ObjectRotations[j] : 0f;
                    allOrdered.Add((obj.Full, obj.Main, oi.FilePath, rot, oi.KeepAspectRatio));
                }
            }

            if (allOrdered.Count > suffixes.Length)
            {
                infoBar.Message = $"Not enough levels ({suffixes.Length}) for {allOrdered.Count} objects.";
                infoBar.Severity = InfoBarSeverity.Error;
                infoBar.IsOpen = true;
                return;
            }

            // Output dir and name from the name source image (or Chain ConfigKey)
            var nameSourceImg = _selectedCluster.Images[
                Math.Clamp(_selectedCluster.NameSourceIndex, 0, _selectedCluster.Images.Count - 1)];
            string name;
            if (_selectedCluster.UseChainName)
            {
                // Resolve chain ConfigKey: Chain Mode first, then DetectedChainName
                var chainConfigKey = _activeChain?.ConfigKey
                    ?? nameSourceImg.DetectedChainName
                    ?? System.IO.Path.GetFileNameWithoutExtension(nameSourceImg.FilePath);
                name = chainConfigKey;
            }
            else
            {
                name = System.IO.Path.GetFileNameWithoutExtension(nameSourceImg.FilePath);
            }
            bool singleObject = nonSkipCount == 1;

            string dir;
            bool redirected;
            if (overrideOutputDir != null)
            {
                dir = overrideOutputDir;
                redirected = true;
            }
            else
            {
                (dir, redirected) = GetOutputDir(nameSourceImg.FilePath);
            }
            if (redirected)
                Directory.CreateDirectory(dir);

            // Check for existing processed files in output dir
            if (redirected)
            {
                var existingFiles = new List<string>();
                for (int i = 0; i < suffixes.Length; i++)
                {
                    if (suffixes[i] == "-") continue;
                    var expectedPath = System.IO.Path.Combine(dir, ImageSplitLogic.SplitFileName(name, suffixes[i]));
                    if (File.Exists(expectedPath))
                        existingFiles.Add(expectedPath);
                }

                if (existingFiles.Count > 0 && !loadExistingIfFound && !suppressExistingDialog)
                {
                    // Check if any have optimization marker
                    int optimizedCount = 0;
                    foreach (var f in existingFiles)
                    {
                        try
                        {
                            var bytes = File.ReadAllBytes(f);
                            if (OptimizationWindow.HasOptMarker(bytes))
                                optimizedCount++;
                        }
                        catch { /* ignore */ }
                    }

                    var existMsg = $"{existingFiles.Count} file(s) already exist in output folder.";
                    if (optimizedCount > 0)
                        existMsg += $"\n{optimizedCount} are already optimized (TinyPNG).";
                    existMsg += "\n\nLoad existing files or ignore and re-split?";

                    var msgBox = new Wpf.Ui.Controls.MessageBox
                    {
                        Title = "Existing Files Found",
                        Content = existMsg,
                        PrimaryButtonText = "Load Existing",
                        SecondaryButtonText = "Ignore & Split",
                        CloseButtonText = "Cancel",
                        MinWidth = 500,
                        Owner = Window.GetWindow(this)
                    };
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
                    var msgResult = msgBox.ShowDialogAsync().GetAwaiter().GetResult();

                    if (msgResult == Wpf.Ui.Controls.MessageBoxResult.None)
                        return; // Cancel

                    if (msgResult == Wpf.Ui.Controls.MessageBoxResult.Primary)
                    {
                        // Load existing files into the cluster
                        var loadedResults = new List<string>();
                        foreach (var oi in scissorsImages)
                            oi.SplitResultFiles.Clear();

                        for (int i = 0; i < suffixes.Length; i++)
                        {
                            if (suffixes[i] == "-") continue;
                            var expectedPath = System.IO.Path.Combine(dir, ImageSplitLogic.SplitFileName(name, suffixes[i]));
                            if (File.Exists(expectedPath))
                            {
                                loadedResults.Add(expectedPath);

                                // Check optimization marker
                                try
                                {
                                    var bytes = File.ReadAllBytes(expectedPath);
                                    if (OptimizationWindow.HasOptMarker(bytes))
                                        _optimizedFiles.Add(expectedPath);
                                }
                                catch { /* ignore */ }
                            }
                            else
                            {
                                // Missing file — split just this one from the atlas
                                // (simplified: re-split all missing)
                                break; // Fall through to normal split for missing files
                            }
                        }

                        if (loadedResults.Count >= nonSkipCount)
                        {
                            // All files loaded — mark as split + set optimization state
                            foreach (var oi in scissorsImages)
                            {
                                oi.SplitResultFiles.Clear();
                                oi.IsSplit = false;
                            }
                            scissorsImages[0].SplitResultFiles = loadedResults;
                            scissorsImages[0].IsSplit = true;
                            scissorsImages[0].IsOptimized = loadedResults.All(f => _optimizedFiles.Contains(f));
                            UpdateUploadButtonState();

                            var optCount = loadedResults.Count(f => _optimizedFiles.Contains(f));
                            infoBar.Message = $"Loaded {loadedResults.Count} existing files" +
                                (optCount > 0 ? $" ({optCount} optimized)." : ".");
                            infoBar.Severity = InfoBarSeverity.Success;
                            infoBar.IsOpen = true;
                            return;
                        }
                    }
                    // Secondary (Ignore & Split) → fall through to normal split
                }

                // Auto-load when called from "Load Existing" in Unsplit dialog
                if (loadExistingIfFound && existingFiles.Count > 0)
                {
                    var loadedResults = new List<string>();
                    foreach (var oi in scissorsImages)
                        oi.SplitResultFiles.Clear();

                    for (int i = 0; i < suffixes.Length; i++)
                    {
                        if (suffixes[i] == "-") continue;
                        var expectedPath = System.IO.Path.Combine(dir, ImageSplitLogic.SplitFileName(name, suffixes[i]));
                        if (File.Exists(expectedPath))
                        {
                            loadedResults.Add(expectedPath);
                            try
                            {
                                var bytes = File.ReadAllBytes(expectedPath);
                                if (OptimizationWindow.HasOptMarker(bytes))
                                    _optimizedFiles.Add(expectedPath);
                            }
                            catch { }
                        }
                    }

                    if (loadedResults.Count >= nonSkipCount)
                    {
                        foreach (var oi in scissorsImages)
                            oi.IsSplit = false;
                        scissorsImages[0].SplitResultFiles = loadedResults;
                        scissorsImages[0].IsSplit = true;
                        scissorsImages[0].IsOptimized = loadedResults.All(f => _optimizedFiles.Contains(f));
                        UpdateUploadButtonState();
                        return;
                    }
                    // Not all files found — fall through to normal split
                }
            }

            // Cache source images for cropping
            var imageCache = new Dictionary<string, Image<Rgba32>>();
            var allResultFiles = new List<string>();
            int skippedCount = 0;

            try
            {
                foreach (var sourcePath in allOrdered.Select(o => o.SourcePath).Distinct())
                {
                    if (!imageCache.ContainsKey(sourcePath))
                        imageCache[sourcePath] = Image.Load<Rgba32>(sourcePath);
                }

                for (int i = 0; i < allOrdered.Count; i++)
                {
                    // Skip objects where suffix is "-"
                    if (i < suffixes.Length && suffixes[i] == "-")
                    {
                        skippedCount++;
                        continue;
                    }

                    var obj = allOrdered[i];
                    var sourceImage = imageCache[obj.SourcePath];

                    string fullPath = singleObject && !redirected
                        ? nameSourceImg.FilePath
                        : System.IO.Path.Combine(dir, ImageSplitLogic.SplitFileName(name, suffixes[i]));

                    // Use shared CropAndSave (identical to FlowchartImageService)
                    ImageProcessingService.CropAndSave(sourceImage, obj.Full, obj.Main, fullPath, obj.Rotation, obj.KeepAspectRatio);

                    allResultFiles.Add(fullPath);
                }
            }
            finally
            {
                foreach (var img in imageCache.Values)
                    img.Dispose();
            }

            // Copy originals to output folder
            if (redirected)
            {
                foreach (var sourcePath in scissorsImages.Select(oi => oi.FilePath).Distinct())
                {
                    var destPath = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(sourcePath));
                    if (!string.Equals(System.IO.Path.GetFullPath(sourcePath),
                        System.IO.Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                    }
                }
            }

            // Mark all scissors-active images as split, store result files on name source
            foreach (var oi in scissorsImages)
            {
                oi.IsSplit = true;
                oi.SplitResultFiles.Clear();
            }
            nameSourceImg.SplitResultFiles = allResultFiles;
            _selectedCluster.LastSplitIndices = string.Join(" ", suffixes);

            var msg = $"Split into {allResultFiles.Count} images.";
            if (skippedCount > 0) msg += $" Skipped {skippedCount}.";
            if (redirected) msg += $" → {System.IO.Path.GetFileName(dir)}";
            infoBar.Message = msg;
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.IsOpen = true;

            _lastOutputDir = redirected ? dir : null;
            btnOpenOutputFolder.Visibility = redirected ? Visibility.Visible : Visibility.Collapsed;

            RebuildThumbnailStrip();
        }
        catch (Exception ex)
        {
            infoBar.Message = $"Split failed: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
    }

    private void BtnOpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputDir != null && System.IO.Directory.Exists(_lastOutputDir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastOutputDir) { UseShellExecute = true });
    }
}
