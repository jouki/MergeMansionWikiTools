using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
/// ImageOptimiserPage — detection domain: overlay rendering, scissors mode,
/// detection source (algorithm/atlas) switching, per-object source toggle,
/// and thin delegates to the shared ImageProcessingService.
/// </summary>
public partial class ImageOptimiserPage
{
    // ── Object detection constants (delegated to ImageProcessingService) ──
    private const int AlphaThreshold = ImageProcessingService.AlphaThreshold;
    private const int MainAlphaThreshold = ImageProcessingService.MainAlphaThreshold;
    private const int MinCellArea = ImageProcessingService.MinCellArea;

    // ══════════════════════════════════════════════════════════════
    //  DETECTION OVERLAY
    // ══════════════════════════════════════════════════════════════

    private void ImgPreview_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDetectionOverlay();

    private void UpdateDetectionOverlay()
    {
        UpdateDetectionButtonColors();

        if (_selectedCluster == null) { detectionOverlay.Children.Clear(); return; }

        // Collect all scissors-active images in cluster that have detections
        var activeImages = _selectedCluster.Images
            .Where(oi => oi.IsScissorsActive && oi.DetectedObjects.Count > 0)
            .ToList();
        if (activeImages.Count == 0) { detectionOverlay.Children.Clear(); return; }

        // Schedule overlay drawing after layout pass (need actual sizes)
        Dispatcher.InvokeAsync(() =>
        {
            detectionOverlay.Children.Clear();
            if (_selectedCluster == null || imgPreview.Source == null) return;

            imgPreview.UpdateLayout();

            var bitmapSource = (BitmapSource)imgPreview.Source;
            double srcW = bitmapSource.PixelWidth;
            double srcH = bitmapSource.PixelHeight;
            double dispW = imgPreview.ActualWidth;
            double dispH = imgPreview.ActualHeight;

            if (dispW <= 0 || dispH <= 0 || srcW <= 0 || srcH <= 0) return;

            var imgOrigin = imgPreview.TransformToVisual(detectionOverlay)
                .Transform(new System.Windows.Point(0, 0));

            double scale = Math.Min(dispW / srcW, dispH / srcH);
            double renderedW = srcW * scale;
            double renderedH = srcH * scale;
            double baseOffsetX = imgOrigin.X + (dispW - renderedW) / 2;
            double baseOffsetY = imgOrigin.Y + (dispH - renderedH) / 2;

            // Parse current index input for labels
            var suffixes = ImageSplitLogic.ParseIndexTokens(inputIndices.Text);
            string[]? labels = suffixes.Length > 0 ? suffixes : null;

            // Real-time merge/expand: adapt DetectedObjects to suffix count
            if (suffixes.Length > 0)
            {
                foreach (var oi in activeImages)
                {
                    if (oi.RawDetectedObjects.Count > suffixes.Length)
                        oi.DetectedObjects = MergeToExpectedCount(oi.RawDetectedObjects, suffixes.Length);
                    else if (suffixes.Length > oi.RawDetectedObjects.Count
                             && oi.UnmergedAlgorithmObjects != null
                             && oi.UnmergedAlgorithmObjects.Count > oi.RawDetectedObjects.Count)
                    {
                        // User provided more indices than merged flood-fill detected —
                        // apply per-row merge: merges fragments within rows (pencil + book)
                        // but prevents cross-row merging that collapsed separate items.
                        var perRowMerged = MergeColumnStacksPerRow(oi.UnmergedAlgorithmObjects);
                        if (perRowMerged.Count > suffixes.Length)
                            perRowMerged = MergeToExpectedCount(perRowMerged, suffixes.Length);
                        oi.DetectedObjects = perRowMerged;
                    }
                    else
                        oi.DetectedObjects = oi.RawDetectedObjects;
                }
            }
            else
            {
                foreach (var oi in activeImages)
                    oi.DetectedObjects = oi.RawDetectedObjects;
            }

            if (_selectedCluster.Images.Count == 1)
            {
                // Single image — simple overlay
                DrawDetectionRects(_selectedCluster.Images[0], baseOffsetX, baseOffsetY, scale, labels, 0);
            }
            else
            {
                // Multi-image combined preview — compute each image's pixel offset
                var rows = ComputeRowLayout(_selectedCluster);

                // Pixel dimensions via header-only decode (cached in ThumbnailCache)
                var pixelDims = new Dictionary<string, (int w, int h)>();
                foreach (var oi in _selectedCluster.Images)
                {
                    if (pixelDims.ContainsKey(oi.FilePath)) continue;
                    pixelDims[oi.FilePath] = ThumbnailCache.GetPixelDimensions(oi.FilePath) ?? (0, 0);
                }

                int maxRowWidth = 0;
                var rowDims = new List<(int w, int h)>();
                foreach (var row in rows)
                {
                    int w = row.Sum(oi => pixelDims.GetValueOrDefault(oi.FilePath).w);
                    int h = row.Max(oi => pixelDims.GetValueOrDefault(oi.FilePath).h);
                    rowDims.Add((w, h));
                    maxRowWidth = Math.Max(maxRowWidth, w);
                }

                int labelOffset = 0;
                double pixelY = 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    double pixelX = maxRowWidth - rowDims[r].w; // right-align matching BuildCombinedPreview
                    foreach (var oi in rows[r])
                    {
                        var (pw, ph) = pixelDims.GetValueOrDefault(oi.FilePath);
                        if (oi.IsScissorsActive && oi.DetectedObjects.Count > 0)
                        {
                            double ox = baseOffsetX + pixelX * scale;
                            double oy = baseOffsetY + pixelY * scale;
                            DrawDetectionRects(oi, ox, oy, scale, labels, labelOffset);
                            labelOffset += OrderObjects(oi.DetectedObjects).Count;
                        }
                        pixelX += pw;
                    }
                    pixelY += rowDims[r].h;
                }
            }
            // Re-apply current visibility mode after rebuild
            if (_overlayVisMode != 0)
                ApplyOverlayVisibility();
        }, DispatcherPriority.Loaded);
    }

    private void DrawDetectionRects(OptimiserImage oi, double offsetX, double offsetY, double scale,
                                     string[]? labels = null, int labelOffset = 0)
    {
        var ordered = OrderObjects(oi.DetectedObjects);
        bool hasBothSources = oi.AlgorithmObjects != null && oi.AtlasObjects != null;

        // Pre-compute ordered alternative source lists for per-object toggle
        List<(Rectangle Full, Rectangle Main)>? orderedAlgo = null;
        List<(Rectangle Full, Rectangle Main)>? orderedAtlas = null;
        if (hasBothSources)
        {
            orderedAlgo = OrderObjects(oi.AlgorithmObjects!);
            orderedAtlas = OrderObjects(oi.AtlasObjects!);
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            // Get effective rectangle (may be overridden by per-object source)
            var obj = GetEffectiveObject(oi, i, ordered[i], orderedAlgo, orderedAtlas);

            // Determine detection source for this object
            DetectionSource objSource = oi.DefaultDetectionSource;
            if (oi.PerObjectDetectionSource != null && i < oi.PerObjectDetectionSource.Length)
                objSource = oi.PerObjectDetectionSource[i];

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = obj.Main.Width * scale,
                Height = obj.Main.Height * scale,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC0, 0xFF, 0x40, 0x40)),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 0xFF, 0x00, 0x00)),
                IsHitTestVisible = false
            };
            double left = offsetX + obj.Main.Left * scale;
            double top = offsetY + obj.Main.Top * scale;
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            detectionOverlay.Children.Add(rect);

            // Label
            int globalIdx = labelOffset + i;
            string? labelText = null;
            System.Windows.Media.Color labelColor = Colors.White;
            if (labels != null && globalIdx < labels.Length)
            {
                labelText = labels[globalIdx];
                labelColor = labelText == "-"
                    ? System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00)  // orange for skip
                    : Colors.White;
            }
            else if (_main.Settings.ShowDetectionIndices)
            {
                labelText = (i + 1).ToString();
                labelColor = System.Windows.Media.Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF); // dim
            }

            if (labelText == null) continue;

            // Build label content: [index text] + [source icon]
            var sourceColor = objSource == DetectionSource.Atlas
                ? System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0) // green for atlas
                : System.Windows.Media.Color.FromRgb(0x56, 0x9C, 0xD6); // blue for algorithm
            bool canToggle = hasBothSources;

            var labelPanel = new StackPanel { Orientation = Orientation.Horizontal };
            labelPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = labelText,
                Foreground = new SolidColorBrush(labelColor),
                FontSize = 12,
                FontWeight = FontWeights.Bold
            });

            // Always show source indicator icon
            labelPanel.Children.Add(new SymbolIcon
            {
                Symbol = objSource == DetectionSource.Atlas
                    ? SymbolRegular.DocumentData24
                    : SymbolRegular.BrainCircuit24,
                FontSize = 12,
                Foreground = new SolidColorBrush(canToggle ? sourceColor
                    : System.Windows.Media.Color.FromArgb(0x80, sourceColor.R, sourceColor.G, sourceColor.B)),
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            int capturedIndex = i;
            var capturedOi = oi;

            var tooltipText = objSource == DetectionSource.Atlas
                ? "Object Detection Method: Game Files"
                : "Object Detection Method: Algorithm";
            if (canToggle) tooltipText += "\nClick to Toggle";

            var labelBorder = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xAA, 0x00, 0x00, 0x00)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                IsHitTestVisible = canToggle,
                Cursor = canToggle ? System.Windows.Input.Cursors.Hand : null,
                ToolTip = tooltipText,
                Child = labelPanel
            };
            ToolTipService.SetInitialShowDelay(labelBorder, 0);

            if (canToggle)
            {
                labelBorder.MouseLeftButtonDown += (s, ev) =>
                {
                    ev.Handled = true;
                    ToggleObjectSource(capturedOi, capturedIndex);
                };
            }

            Canvas.SetLeft(labelBorder, left);
            Canvas.SetTop(labelBorder, top);
            detectionOverlay.Children.Add(labelBorder);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  SCISSORS TOGGLE
    // ══════════════════════════════════════════════════════════════

    private void ToggleScissors_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not OptimiserImage oi) return;
        e.Handled = true;

        oi.IsScissorsActive = !oi.IsScissorsActive;

        // Show/hide split controls based on whether ANY image in the cluster has scissors
        var cluster = _clusters.FirstOrDefault(c => c.Images.Contains(oi));
        if (cluster != null && cluster == _selectedCluster)
        {
            bool anyScissors = cluster.Images.Any(i => i.IsScissorsActive);
            indexInputPanel.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
            splitButtonGroup.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
            refreshButtonGroup.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
            btnToggleRects.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
            btnAspectRatio.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
            UpdateAspectRatioButton();
            UpdatePredictButtonVisibility();
            UpdatePreviewMargins();
        }

        // Update overlay if toggled image is in the displayed cluster
        if (_selectedCluster != null && _selectedCluster.Images.Contains(oi))
            UpdateDetectionOverlay();

        RebuildThumbnailStrip();
    }

    // ══════════════════════════════════════════════════════════════
    //  OVERLAY VISIBILITY / DETECTION SOURCE BUTTONS
    // ══════════════════════════════════════════════════════════════

    // 0 = all visible, 1 = labels only (no rects/source icons), 2 = all hidden
    private int _overlayVisMode;

    private void BtnToggleRects_Click(object sender, RoutedEventArgs e)
    {
        _overlayVisMode = (_overlayVisMode + 1) % 3;
        ApplyOverlayVisibility();
    }

    /// <summary>Toggles the selected image's output aspect-ratio mode: 1:1 square canvas (default) vs.
    /// keeping the crop's original aspect ratio. Per-image; new images always start at 1:1.</summary>
    private void BtnAspectRatio_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedImage == null) return;
        _selectedImage.KeepAspectRatio = !_selectedImage.KeepAspectRatio;
        UpdateAspectRatioButton();
    }

    /// <summary>Reflects the selected image's <see cref="OptimiserImage.KeepAspectRatio"/> on the toggle:
    /// "1:1" (square canvas) or the image's actual reduced integer ratio (keep original). Highlights
    /// the non-default state.</summary>
    private void UpdateAspectRatioButton()
    {
        bool keep = _selectedImage?.KeepAspectRatio == true;
        txtAspectRatio.Text = keep ? RatioLabelFor(_selectedImage!) : "1:1";
        btnAspectRatio.ToolTip = keep
            ? "Keeping original aspect ratio (no 1:1 canvas). Click for 1:1 square output."
            : "Output canvas: 1:1 square (default). Click to keep the original aspect ratio instead.";
        // Highlight the non-default (keep-AR) state in gold, matching the active-toggle convention.
        if (keep) txtAspectRatio.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
        else txtAspectRatio.ClearValue(ForegroundProperty);
    }

    /// <summary>The image's aspect ratio reduced to smallest integers ("16:9", "4:3", …). Reads the source
    /// dimensions once (header-only via Image.Identify) and caches them. Falls back to "AR" if unreadable.</summary>
    private static string RatioLabelFor(OptimiserImage oi)
    {
        if (oi.Dimensions == null)
        {
            try { var info = Image.Identify(oi.FilePath); oi.Dimensions = (info.Width, info.Height); }
            catch { return "AR"; }
        }
        var (w, h) = oi.Dimensions.Value;
        if (w <= 0 || h <= 0) return "AR";
        int g = Gcd(w, h);
        return $"{w / g}:{h / g}";
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { (a, b) = (b, a % b); }
        return a == 0 ? 1 : a;
    }

    private void ApplyOverlayVisibility()
    {
        if (_overlayVisMode == 2)
        {
            // All hidden
            detectionOverlay.Visibility = Visibility.Collapsed;
            iconToggleRects.Symbol = Wpf.Ui.Controls.SymbolRegular.EyeOff24;
            iconToggleRects.ClearValue(ForegroundProperty);
        }
        else
        {
            detectionOverlay.Visibility = Visibility.Visible;
            if (_overlayVisMode == 0)
            {
                // All visible
                iconToggleRects.Symbol = Wpf.Ui.Controls.SymbolRegular.Eye24;
                iconToggleRects.ClearValue(ForegroundProperty);
            }
            else
            {
                // Labels only — yellow eye to indicate partial visibility
                iconToggleRects.Symbol = Wpf.Ui.Controls.SymbolRegular.Eye24;
                iconToggleRects.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
            }
            foreach (UIElement child in detectionOverlay.Children)
            {
                if (child is System.Windows.Shapes.Rectangle rect)
                    rect.Visibility = _overlayVisMode == 0 ? Visibility.Visible : Visibility.Collapsed;
                else if (child is Border label)
                {
                    // In mode 1: hide source icons, keep label text
                    var panel = label.Child as StackPanel;
                    if (panel != null && panel.Children.Count > 1)
                        panel.Children[panel.Children.Count - 1].Visibility =
                            _overlayVisMode == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }
    }

    private void BtnDetectAlgorithm_Click(object sender, RoutedEventArgs e)
    {
        RefreshDetectionAlgorithm();
        UpdateDetectionButtonColors();
    }

    private void BtnDetectAtlas_Click(object sender, RoutedEventArgs e)
    {
        RefreshDetectionAtlas();
        UpdateDetectionButtonColors();
    }

    private void UpdateDetectionButtonColors()
    {
        var activeOi = _selectedCluster?.Images.FirstOrDefault(i => i.IsScissorsActive);
        var source = activeOi?.DefaultDetectionSource ?? DetectionSource.Algorithm;

        if (source == DetectionSource.Algorithm)
        {
            iconAlgorithm.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x56, 0x9C, 0xD6));
            iconAtlas.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorTertiaryBrush");
        }
        else
        {
            iconAlgorithm.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorTertiaryBrush");
            iconAtlas.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0));
        }
    }

    private void RefreshDetectionAlgorithm()
    {
        if (_selectedCluster == null) return;

        foreach (var oi in _selectedCluster.Images.Where(i => i.IsScissorsActive))
        {
            try
            {
                using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(oi.FilePath);
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

    private void RefreshDetectionAtlas()
    {
        if (_selectedCluster == null) return;

        var exportDir = GetExportDir();
        if (exportDir == null)
        {
            infoBar.Message = "Set Image Exporter base path and APK version in Settings first.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
            return;
        }

        var allSprites = SpriteMetadataService.Load(exportDir);
        if (allSprites.Count == 0)
        {
            infoBar.Message = "image_atlas_data.json not found. Re-extract textures from APK.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
            return;
        }

        bool anyUpdated = false;
        foreach (var oi in _selectedCluster.Images.Where(i => i.IsScissorsActive))
        {
            var imageFileName = System.IO.Path.GetFileNameWithoutExtension(oi.FilePath);
            var textureSprites = SpriteMetadataService.GetSpritesForTexture(allSprites, imageFileName);
            if (textureSprites.Count == 0) continue;

            // Get image height for coordinate conversion
            int imageHeight;
            try
            {
                var imgInfo = Image.Identify(oi.FilePath);
                imageHeight = imgInfo.Height;
            }
            catch { continue; }

            var spriteObjects = ImageSplitLogic.SpriteObjectsFromSprites(
                ImageSplitLogic.OrderSpritesUnity(textureSprites), imageHeight);

            oi.AtlasObjects = spriteObjects;
            oi.DefaultDetectionSource = DetectionSource.Atlas;
            oi.PerObjectDetectionSource = null;
            oi.RawDetectedObjects = spriteObjects;
            oi.DetectedObjects = spriteObjects;
            anyUpdated = true;
        }

        if (anyUpdated)
        {
            UpdateDetectionOverlay();
            infoBar.Message = "Detection updated from atlas data.";
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.IsOpen = true;
        }
        else
        {
            infoBar.Message = "No atlas data found for current image.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
        }
    }

    // ── Per-object detection source toggle ──

    /// <summary>
    /// Returns the effective rectangle for a given ordered object index, respecting per-object source overrides.
    /// Falls back to the default ordered object if no override or no alternative source.
    /// </summary>
    private static (Rectangle Full, Rectangle Main) GetEffectiveObject(
        OptimiserImage oi, int orderedIndex, (Rectangle Full, Rectangle Main) defaultObj,
        List<(Rectangle Full, Rectangle Main)>? orderedAlgo,
        List<(Rectangle Full, Rectangle Main)>? orderedAtlas)
    {
        if (oi.PerObjectDetectionSource == null || orderedAlgo == null || orderedAtlas == null)
            return defaultObj;
        if (orderedIndex < 0 || orderedIndex >= oi.PerObjectDetectionSource.Length)
            return defaultObj;
        if (orderedIndex >= orderedAlgo.Count || orderedIndex >= orderedAtlas.Count)
            return defaultObj;

        var source = oi.PerObjectDetectionSource[orderedIndex];
        return source == DetectionSource.Atlas ? orderedAtlas[orderedIndex] : orderedAlgo[orderedIndex];
    }

    private void ToggleObjectSource(OptimiserImage oi, int orderedIndex)
    {
        var algo = oi.AlgorithmObjects;
        var atlas = oi.AtlasObjects;
        if (algo == null || atlas == null) return;

        // Initialize per-object sources from current default if needed
        int count = OrderObjects(oi.DetectedObjects).Count;
        if (oi.PerObjectDetectionSource == null)
            oi.PerObjectDetectionSource = Enumerable.Repeat(oi.DefaultDetectionSource, count).ToArray();

        if (orderedIndex < 0 || orderedIndex >= oi.PerObjectDetectionSource.Length) return;

        oi.PerObjectDetectionSource[orderedIndex] = oi.PerObjectDetectionSource[orderedIndex] == DetectionSource.Algorithm
            ? DetectionSource.Atlas : DetectionSource.Algorithm;

        // Only redraw overlay — don't modify RawDetectedObjects/DetectedObjects
        UpdateDetectionOverlay();
    }

    // ══════════════════════════════════════════════════════════════
    //  OBJECT DETECTION — Flood Fill + Smart Merge
    // ══════════════════════════════════════════════════════════════

    /// <summary>Raw flood-fill detection without column merge. Delegates to shared ImageProcessingService.</summary>
    private static List<(Rectangle Full, Rectangle Main)> DetectObjectsRaw(Image<Rgba32> img)
        => ImageProcessingService.DetectObjectsRaw(img);

    private static List<(Rectangle Full, Rectangle Main)> DetectObjects(Image<Rgba32> img)
        => ImageProcessingService.DetectObjects(img);

    /// <summary>Delegates to shared ImageProcessingService.</summary>
    private static List<(Rectangle Full, Rectangle Main)> MergeColumnStacks(
        List<(Rectangle Full, Rectangle Main)> objects)
        => ImageProcessingService.MergeColumnStacks(objects);

    private static (Rectangle Full, Rectangle Main) FloodFill(Image<Rgba32> img, int sx, int sy, bool[,] v)
        => ImageProcessingService.FloodFill(img, sx, sy, v);

    private static List<(Rectangle Full, Rectangle Main)> OrderObjects(List<(Rectangle Full, Rectangle Main)> objects)
        => ImageProcessingService.OrderObjects(objects);

    private static List<List<(Rectangle Full, Rectangle Main)>> SplitIntoObjectRows(
        List<(Rectangle Full, Rectangle Main)> objects)
        => ImageProcessingService.SplitIntoObjectRows(objects);

    /// <summary>
    /// Applies MergeColumnStacks within each visual row independently.
    /// This merges fragments within a row (e.g. pencil + book) but prevents
    /// cross-row merging (bottom-row objects stay separate from top-row).
    /// </summary>
    private static List<(Rectangle Full, Rectangle Main)> MergeColumnStacksPerRow(
        List<(Rectangle Full, Rectangle Main)> objects)
        => ImageProcessingService.MergeColumnStacksPerRow(objects);

    /// <summary>
    /// Returns per-row object counts (e.g. [8, 2] for 8 items in first row, 2 in second).
    /// </summary>
    private static List<int> GroupIntoRows(List<(Rectangle Full, Rectangle Main)> orderedObjects)
    {
        var rows = SplitIntoObjectRows(orderedObjects);
        return rows.Select(r => r.Count).ToList();
    }

    private static List<(Rectangle Full, Rectangle Main)> MergeToExpectedCount(
        List<(Rectangle Full, Rectangle Main)> objects, int expectedCount)
        => ImageProcessingService.MergeToExpectedCount(objects, expectedCount);

    private static int GetCanvasSize(int w, int h)
        => ImageProcessingService.GetCanvasSize(w, h);
}
