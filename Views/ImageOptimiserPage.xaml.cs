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

// ── Data models ──

internal enum DetectionSource { Algorithm, Atlas }

internal class OptimiserImage
{
    public string FilePath { get; set; } = "";
    public BitmapImage? Thumbnail { get; set; }
    public bool IsScissorsActive { get; set; }
    /// <summary>When true, split output keeps the crop's original aspect ratio instead of centering it on
    /// a square (1:1) canvas. Default false = the 1:1 canvas rule applies (the standard behavior).</summary>
    public bool KeepAspectRatio { get; set; }
    /// <summary>Cached source pixel dimensions (read once via Image.Identify) for the aspect-ratio label.</summary>
    public (int W, int H)? Dimensions { get; set; }
    public bool IsSplit { get; set; }
    public bool IsOptimized { get; set; }
    public List<string> SplitResultFiles { get; set; } = new();
    public List<(Rectangle Full, Rectangle Main)> DetectedObjects { get; set; } = new();
    public List<(Rectangle Full, Rectangle Main)> RawDetectedObjects { get; set; } = new();
    public int DetectedColumns { get; set; } = 1;
    public string? DetectedChainName { get; set; }
    /// <summary>
    /// Per-detected-object rotation correction in degrees (CW) from atlas rotate flags.
    /// Built during prediction. Index corresponds to ordered detected objects.
    /// </summary>
    public float[]? ObjectRotations { get; set; }

    // ── Per-source detection objects ──
    public List<(Rectangle Full, Rectangle Main)>? AlgorithmObjects { get; set; }
    /// <summary>Flood-fill results BEFORE MergeColumnStacks (more objects, no column merging).</summary>
    public List<(Rectangle Full, Rectangle Main)>? UnmergedAlgorithmObjects { get; set; }
    public List<(Rectangle Full, Rectangle Main)>? AtlasObjects { get; set; }
    public DetectionSource DefaultDetectionSource { get; set; } = DetectionSource.Algorithm;
    /// <summary>Per-object source override (indexed by ordered position). Null = use default for all.</summary>
    public DetectionSource[]? PerObjectDetectionSource { get; set; }
}

internal class OptimiserCluster
{
    public List<OptimiserImage> Images { get; } = new();
    public int NameSourceIndex { get; set; } // which image's DetectedChainName to use
    public string IndexText { get; set; } = ""; // per-cluster index input text
    public string LastSplitIndices { get; set; } = ""; // normalized indices used for the last split
    public bool UseChainName { get; set; } // use Chain ConfigKey for output filenames instead of source filename
}

public partial class ImageOptimiserPage : UserControl
{
    private readonly MainWindow _main;
    private readonly List<OptimiserCluster> _clusters = new();
    private OptimiserCluster? _selectedCluster;
    private OptimiserImage? _selectedImage;

    private IEnumerable<OptimiserImage> AllImages => _clusters.SelectMany(c => c.Images);

    // ── Optimization tracking ──
    private readonly HashSet<string> _optimizedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressIndexReset;
    private readonly Debouncer _overlayDebounce = new(TimeSpan.FromMilliseconds(200));

    private const int MaxColumnsPerRow = 15;

    public ImageOptimiserPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        _main.WikiVerifiedChanged += OnWikiVerifiedChanged;
        _main.TinifyApiKeyChanged += OnTinifyApiKeyChanged;

        // Hide clipboard Add button when InfoBar is closed
        var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            Wpf.Ui.Controls.InfoBar.IsOpenProperty, typeof(Wpf.Ui.Controls.InfoBar));
        dpd?.AddValueChanged(infoBar, (_, _) => { if (!infoBar.IsOpen) HideClipboardAdd(); });

        UpdateOptimizeButtonState();
        UpdateUploadButtonState();

        // Lifecycle gate: clipboard polling timer (500 ms) běží jen když je stránka viditelná.
        // Výjimka: ClipboardMonitorGlobal — monitor má běžet i na ostatních stránkách.
        // Start je idempotentní a resetuje _lastClipboardSeq, takže obsah zkopírovaný
        // mimo stránku se po návratu nezpracuje zpětně (stejně jako dosud).
        // MainWindow navíc volá Start/Stop při navigaci (pokrývá vypnutí global togglu
        // v Settings, kdy se viditelnost této stránky nemění) — obojí je záměrně.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                StartClipboardMonitor();
            else if (!_main.Settings.ClipboardMonitorGlobal)
                StopClipboardMonitor();
        };
    }

    private void OnWikiVerifiedChanged() => UpdateUploadButtonState();
    private void OnTinifyApiKeyChanged() => UpdateOptimizeButtonState();

    private void UpdateOptimizeButtonState()
    {
        bool hasKey = !string.IsNullOrWhiteSpace(_main.Settings.TinifyApiKey);
        btnOptimizeAll.IsEnabled = hasKey;
        btnOptimizeAll.ToolTip = hasKey ? null : "Set your TinyPNG API key in Settings first";
    }

    private void UpdateUploadButtonState()
    {
        bool anyOptimized = _optimizedFiles.Count > 0 || AllImages.Any(i => i.IsOptimized);
        if (!anyOptimized)
        {
            btnUploadWiki.IsEnabled = false;
            btnUploadWiki.ToolTip = "Optimize images first";
        }
        else if (!_main.Settings.WikiVerified)
        {
            btnUploadWiki.IsEnabled = false;
            btnUploadWiki.ToolTip = "Wiki bot not configured. Set up credentials in Settings.";
        }
        else
        {
            btnUploadWiki.IsEnabled = true;
            btnUploadWiki.ToolTip = "Upload optimised images to the wiki";
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  DRAG & DROP
    // ══════════════════════════════════════════════════════════════

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (_isDraggingThumb)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (_isDraggingThumb) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var imageFiles = files.Where(f =>
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (imageFiles.Length == 0) return;
        AddImages(imageFiles);
    }

    // ══════════════════════════════════════════════════════════════
    //  ADD IMAGES
    // ══════════════════════════════════════════════════════════════

    private void AddImages(string[] paths)
    {
        foreach (var path in paths)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            if (AllImages.Any(img => string.Equals(
                System.IO.Path.GetFullPath(img.FilePath), fullPath, StringComparison.OrdinalIgnoreCase)))
                continue;

            var thumb = LoadThumbnail(path, 80);
            if (thumb == null) continue;

            var oi = new OptimiserImage
            {
                FilePath = path,
                Thumbnail = thumb,
                DetectedChainName = TryMatchChainName(path)
            };

            // Run object detection
            try
            {
                using var img = Image.Load<Rgba32>(path);
                oi.UnmergedAlgorithmObjects = DetectObjectsRaw(img);
                oi.RawDetectedObjects = MergeColumnStacks(oi.UnmergedAlgorithmObjects);
                oi.AlgorithmObjects = oi.RawDetectedObjects;
                oi.DetectedObjects = oi.RawDetectedObjects;
                var ordered = OrderObjects(oi.DetectedObjects);
                if (ordered.Count > 0)
                {
                    var rows = ordered.GroupBy(o => o.Full.Top / 60);
                    oi.DetectedColumns = rows.Max(g => g.Count());
                }

                // Try atlas detection from sprite metadata
                var expDir = GetExportDir();
                if (expDir != null)
                {
                    var allSprites = SpriteMetadataService.Load(expDir);
                    var texName = System.IO.Path.GetFileNameWithoutExtension(path);
                    var textureSprites = SpriteMetadataService.GetSpritesForTexture(allSprites, texName);
                    if (textureSprites.Count > 0)
                    {
                        var spriteObjects = ImageSplitLogic.SpriteObjectsFromSprites(
                            ImageSplitLogic.OrderSpritesUnity(textureSprites), img.Height);

                        oi.AtlasObjects = spriteObjects;
                    }
                }

                // Auto-enable scissors when objects detected (atlas: always, algorithm: >1)
                if (ordered.Count > 1 || (ordered.Count == 1 && oi.AtlasObjects != null))
                    oi.IsScissorsActive = true;
            }
            catch { /* detection failed — non-critical */ }

            // Create a single-image cluster
            var cluster = new OptimiserCluster();
            cluster.Images.Add(oi);

            // Auto-enable UseChainName if texture is shared by multiple chains
            if (IsMultiChainTexture(path))
                cluster.UseChainName = true;

            _clusters.Add(cluster);
        }

        // Auto-chain: if 3+ new images were added, offer to link them via InfoBar
        if (paths.Length >= 3)
        {
            var newClusters = _clusters
                .Where(c => c.Images.Count == 1 && paths.Contains(c.Images[0].FilePath, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (newClusters.Count >= 3)
                ShowAutoLinkOffer(newClusters);
        }

        RebuildThumbnailStrip();
        if (_selectedImage == null && _clusters.Count > 0)
            SelectImage(_clusters[0].Images[0]);

        bool hasImages = _clusters.Count > 0;
        thumbnailStripBorder.Visibility = hasImages ? Visibility.Visible : Visibility.Collapsed;
        txtPlaceholder.Visibility = hasImages ? Visibility.Collapsed : Visibility.Visible;

        // Auto-enter chain mode: if a single atlas image was added and chain detected via PoolTag
        if (_activeChain == null && paths.Length == 1)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(paths[0]);
            var exportDir = GetExportDir();
            if (exportDir != null)
            {
                var poolTag = SpriteMetadataService.ResolvePoolTagForTexture(name, exportDir);
                if (poolTag != null)
                {
                    var chain = _main.DataService?.Chains?.FirstOrDefault(c =>
                        string.Equals(c.PoolTag, poolTag, StringComparison.OrdinalIgnoreCase));
                    if (chain != null)
                    {
                        // Select the new image so EnterChainMode operates on it
                        var newOi = AllImages.FirstOrDefault(i =>
                            i.FilePath.Equals(paths[0], StringComparison.OrdinalIgnoreCase));
                        if (newOi != null) SelectImage(newOi);

                        AppLogger.Info($"Auto chain mode: '{name}' → PoolTag '{poolTag}' → chain '{chain.ConfigKey}'");
                        EnterChainMode(chain);
                    }
                }

                // Fallback: if no chain entered via PoolTag, try sprite metadata detection
                if (_activeChain == null)
                {
                    var allSkinMappings = SpriteMetadataService.LoadSkinMappings(exportDir);
                    var detectedChain = FindChainForTexture(name, allSkinMappings);
                    if (detectedChain != null)
                        ShowChainSuggestion(detectedChain);
                }
            }
        }
    }

    private static BitmapImage? LoadThumbnail(string path, int decodeHeight)
        => ThumbnailCache.GetByHeight(path, decodeHeight);

    // ══════════════════════════════════════════════════════════════
    //  THUMBNAIL STRIP
    // ══════════════════════════════════════════════════════════════

    // ── Thumbnail drag state ──
    private OptimiserImage? _dragSource;
    private bool _isDraggingThumb;
    private System.Windows.Point _thumbDragStart;
    private Border? _insertionLine;

    private void RebuildThumbnailStrip()
    {
        thumbnailPanel.Children.Clear();

        foreach (var cluster in _clusters)
        {
            var clusterIsSelected = cluster == _selectedCluster;

            for (int i = 0; i < cluster.Images.Count; i++)
            {
                var oi = cluster.Images[i];

                // Card container
                var card = new Border
                {
                    Width = 88,
                    Height = 88,
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(clusterIsSelected ? 2 : 1),
                    BorderBrush = clusterIsSelected
                        ? (Brush)FindResource("AccentFillColorDefaultBrush")
                        : (Brush)FindResource("CardStrokeColorDefaultBrush"),
                    Background = (Brush)FindResource("SubtleFillColorSecondaryBrush"),
                    Margin = new Thickness(2),
                    Cursor = Cursors.Hand,
                    Tag = oi,
                    ClipToBounds = true
                };

                var grid = new Grid();
                card.Child = grid;

                // Thumbnail image
                var img = new System.Windows.Controls.Image
                {
                    Source = oi.Thumbnail,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                grid.Children.Add(img);

                // Scissors toggle (bottom-right)
                var scissorsBorder = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 2, 2),
                    Background = oi.IsScissorsActive
                        ? (Brush)FindResource("AccentFillColorDefaultBrush")
                        : new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0, 0, 0)),
                    Cursor = Cursors.Hand,
                    Tag = oi,
                    ToolTip = "Toggle split mode",
                };
                ToolTipService.SetInitialShowDelay(scissorsBorder, 0);

                var scissorsIcon = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = SymbolRegular.Cut24,
                    FontSize = 12,
                    Foreground = Brushes.White
                };
                scissorsBorder.Child = scissorsIcon;
                scissorsBorder.MouseLeftButtonDown += ToggleScissors_Click;
                grid.Children.Add(scissorsBorder);

                // Name source indicator (small accent dot on the name-source image)
                if (cluster.Images.Count > 1 && i == cluster.NameSourceIndex)
                {
                    var nameSourceDot = new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(4),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(4, 0, 0, 4),
                        Background = (Brush)FindResource("AccentFillColorDefaultBrush")
                    };
                    grid.Children.Add(nameSourceDot);
                }

                // Remove button (top-right)
                var removeBorder = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 2, 0),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xA0, 0, 0, 0)),
                    Cursor = Cursors.Hand,
                    Tag = oi,
                    Visibility = Visibility.Collapsed
                };

                var removeIcon = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = SymbolRegular.Dismiss16,
                    FontSize = 10,
                    Foreground = Brushes.White
                };
                removeBorder.Child = removeIcon;
                removeBorder.MouseLeftButtonDown += RemoveImage_Click;
                grid.Children.Add(removeBorder);

                // Optimized indicator (top-left green check)
                if (oi.IsOptimized || oi.IsSplit)
                {
                    var checkBorder = new Border
                    {
                        Width = 18,
                        Height = 18,
                        CornerRadius = new CornerRadius(9),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(2, 2, 0, 0),
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50))
                    };
                    var checkIcon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = SymbolRegular.Checkmark16,
                        FontSize = 10,
                        Foreground = Brushes.White
                    };
                    checkBorder.Child = checkIcon;
                    grid.Children.Add(checkBorder);
                }

                // Show/hide remove on hover
                card.MouseEnter += (_, _) => removeBorder.Visibility = Visibility.Visible;
                card.MouseLeave += (_, _) => removeBorder.Visibility = Visibility.Collapsed;

                // Click to select (clear drag state to prevent stale _dragSource causing accidental links)
                card.MouseLeftButtonDown += (s, e) =>
                {
                    _dragSource = null;
                    if (s is Border b && b.Tag is OptimiserImage oimg)
                    {
                        SelectImage(oimg);
                        e.Handled = true;
                    }
                };

                // Drag source + drop target
                card.MouseMove += ThumbBorder_MouseMove;
                card.AllowDrop = true;
                card.DragOver += ThumbBorder_DragOver;
                card.DragLeave += ThumbBorder_DragLeave;
                card.Drop += ThumbBorder_Drop;

                thumbnailPanel.Children.Add(card);

                // Link icon between images in same cluster
                if (i < cluster.Images.Count - 1)
                {
                    var bondIndex = i;
                    var bondCluster = cluster;
                    var linkIcon = new Border
                    {
                        Width = 22,
                        Height = 22,
                        CornerRadius = new CornerRadius(11),
                        Background = (Brush)FindResource("AccentFillColorDefaultBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = Cursors.Hand,
                        ToolTip = "Click to unlink",
                        Margin = new Thickness(-2, 0, -2, 0)
                    };
                    ToolTipService.SetInitialShowDelay(linkIcon, 0);
                    linkIcon.Child = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = SymbolRegular.Link24,
                        FontSize = 11,
                        Foreground = Brushes.White
                    };
                    linkIcon.MouseLeftButtonDown += (_, _) => UnlinkAt(bondCluster, bondIndex);
                    thumbnailPanel.Children.Add(linkIcon);
                }
            }

            // Separator between clusters (reorder drop target + hover link button)
            var clusterIdx = _clusters.IndexOf(cluster);
            var sep = new Border
            {
                Width = 24,
                MinHeight = 88,
                Background = Brushes.Transparent,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                AllowDrop = true,
                Tag = cluster
            };

            var sepGrid = new Grid();
            sep.Child = sepGrid;

            // Thin line (default state)
            var sepLine = new Border
            {
                Width = 1,
                Height = 60,
                Background = (Brush)FindResource("CardStrokeColorDefaultBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            sepGrid.Children.Add(sepLine);

            // Link button (shown on hover, hidden during drag)
            if (clusterIdx < _clusters.Count - 1)
            {
                var leftCluster = cluster;
                var rightCluster = _clusters[clusterIdx + 1];
                var linkBtn = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = (Brush)FindResource("SubtleFillColorTertiaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    Visibility = Visibility.Collapsed,
                    ToolTip = "Link clusters"
                };
                ToolTipService.SetInitialShowDelay(linkBtn, 0);
                linkBtn.Child = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = SymbolRegular.Link24,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextFillColorSecondaryBrush")
                };
                linkBtn.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    MergeClusters(leftCluster, rightCluster);
                };
                sepGrid.Children.Add(linkBtn);

                sep.MouseEnter += (_, _) =>
                {
                    if (!_isDraggingThumb)
                    {
                        sepLine.Visibility = Visibility.Collapsed;
                        linkBtn.Visibility = Visibility.Visible;
                    }
                };
                sep.MouseLeave += (_, _) =>
                {
                    sepLine.Visibility = Visibility.Visible;
                    linkBtn.Visibility = Visibility.Collapsed;
                };
            }

            sep.DragOver += SepBorder_DragOver;
            sep.DragLeave += SepBorder_DragLeave;
            sep.Drop += SepBorder_Drop;
            thumbnailPanel.Children.Add(sep);
        }

        // Remove trailing separator
        if (thumbnailPanel.Children.Count > 0 &&
            thumbnailPanel.Children[^1] is Border lastBorder &&
            lastBorder.Tag is OptimiserCluster)
        {
            thumbnailPanel.Children.RemoveAt(thumbnailPanel.Children.Count - 1);
        }
    }

    private void SelectImage(OptimiserImage oi)
    {
        // Save current cluster's index text before switching
        if (_selectedCluster != null)
            _selectedCluster.IndexText = inputIndices.Text;

        _selectedImage = oi;
        var newCluster = _clusters.FirstOrDefault(c => c.Images.Contains(oi));
        _selectedCluster = newCluster;

        // Restore new cluster's index text (suppress reset — not a user edit)
        _suppressIndexReset = true;
        inputIndices.Text = newCluster?.IndexText ?? "";
        _suppressIndexReset = false;

        ShowPreviewForSelection();

        // Update split controls visibility — show if ANY image in the cluster has scissors active
        bool anyScissors = newCluster?.Images.Any(i => i.IsScissorsActive) == true;
        indexInputPanel.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
        splitButtonGroup.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
        refreshButtonGroup.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
        btnToggleRects.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
        btnAspectRatio.Visibility = anyScissors ? Visibility.Visible : Visibility.Collapsed;
        UpdateAspectRatioButton();
        UpdatePredictButtonVisibility();
        UpdatePreviewMargins();

        // Update detection overlay
        UpdateDetectionOverlay();

        // Auto-predict if enabled and indices are empty
        TryAutoPredict();

        // Update name source dropdown
        UpdateNameSourceDropdown();

        // Refresh thumbnail strip selection highlight
        RebuildThumbnailStrip();
    }

    private void ShowPreviewForSelection()
    {
        if (_selectedCluster == null || _selectedCluster.Images.Count == 0)
        {
            imgPreview.Source = null;
            txtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        txtPlaceholder.Visibility = Visibility.Collapsed;

        if (_selectedCluster.Images.Count == 1)
        {
            // Single image — show full preview (full-size: decode bez cache, soubor se nezamyká)
            try
            {
                var bmp = ThumbnailCache.FromBytes(File.ReadAllBytes(_selectedCluster.Images[0].FilePath), 0);
                imgPreview.Source = bmp ?? (BitmapSource?)_selectedCluster.Images[0].Thumbnail;
            }
            catch
            {
                imgPreview.Source = _selectedCluster.Images[0].Thumbnail;
            }
        }
        else
        {
            // Multi-image cluster — build combined preview
            BuildCombinedPreview(_selectedCluster);
        }
    }

    private static List<List<OptimiserImage>> ComputeRowLayout(OptimiserCluster cluster)
    {
        var rows = new List<List<OptimiserImage>>();
        var currentRow = new List<OptimiserImage>();
        int currentCols = 0;

        foreach (var oi in cluster.Images)
        {
            int cols = Math.Max(oi.DetectedColumns, 1);
            if (currentRow.Count > 0 && currentCols + cols > MaxColumnsPerRow)
            {
                rows.Add(currentRow);
                currentRow = new List<OptimiserImage>();
                currentCols = 0;
            }
            currentRow.Add(oi);
            currentCols += cols;
        }
        if (currentRow.Count > 0)
            rows.Add(currentRow);

        return rows;
    }

    private void BuildCombinedPreview(OptimiserCluster cluster)
    {
        try
        {
            // Full-size decodes bez globální cache (atlasy mohou být velké; per-call dictionary
            // dedupuje v rámci jedné kompozice). StreamSource → žádný file lock.
            var bitmapCache = new Dictionary<string, BitmapImage>();
            foreach (var oi in cluster.Images)
            {
                if (bitmapCache.ContainsKey(oi.FilePath)) continue;
                bitmapCache[oi.FilePath] = ThumbnailCache.FromBytes(File.ReadAllBytes(oi.FilePath), 0)
                    ?? throw new IOException($"Failed to decode {oi.FilePath}");
            }

            var rows = ComputeRowLayout(cluster);

            int maxRowWidth = 0;
            var rowDims = new List<(int w, int h)>();
            foreach (var row in rows)
            {
                int w = row.Sum(oi => bitmapCache[oi.FilePath].PixelWidth);
                int h = row.Max(oi => bitmapCache[oi.FilePath].PixelHeight);
                rowDims.Add((w, h));
                maxRowWidth = Math.Max(maxRowWidth, w);
            }
            int totalHeight = rowDims.Sum(d => d.h);

            if (maxRowWidth == 0 || totalHeight == 0) return;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double y = 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    double x = maxRowWidth - rowDims[r].w; // right-align overflow rows
                    foreach (var oi in rows[r])
                    {
                        var bmp = bitmapCache[oi.FilePath];
                        dc.DrawImage(bmp, new Rect(x, y, bmp.PixelWidth, bmp.PixelHeight));
                        x += bmp.PixelWidth;
                    }
                    y += rowDims[r].h;
                }
            }

            var rtb = new RenderTargetBitmap(maxRowWidth, totalHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();

            imgPreview.Source = rtb;
        }
        catch
        {
            imgPreview.Source = cluster.Images[0].Thumbnail;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  NAME SOURCE DROPDOWN
    // ══════════════════════════════════════════════════════════════

    private void UpdateNameSourceDropdown()
    {
        if (_selectedCluster == null || _selectedCluster.Images.Count <= 1)
        {
            cmbNameSource.Visibility = Visibility.Collapsed;
            return;
        }

        cmbNameSource.Visibility = Visibility.Visible;
        cmbNameSource.SelectionChanged -= CmbNameSource_SelectionChanged;
        cmbNameSource.Items.Clear();

        for (int i = 0; i < _selectedCluster.Images.Count; i++)
        {
            var fname = System.IO.Path.GetFileName(_selectedCluster.Images[i].FilePath);
            cmbNameSource.Items.Add(fname);
        }

        // Default: pick file with most detected objects
        if (_selectedCluster.NameSourceIndex < 0 || _selectedCluster.NameSourceIndex >= _selectedCluster.Images.Count)
        {
            _selectedCluster.NameSourceIndex = 0;
            int maxObj = 0;
            for (int i = 0; i < _selectedCluster.Images.Count; i++)
            {
                if (_selectedCluster.Images[i].DetectedObjects.Count > maxObj)
                {
                    maxObj = _selectedCluster.Images[i].DetectedObjects.Count;
                    _selectedCluster.NameSourceIndex = i;
                }
            }
        }

        cmbNameSource.SelectedIndex = _selectedCluster.NameSourceIndex;
        cmbNameSource.SelectionChanged += CmbNameSource_SelectionChanged;
    }

    private void CmbNameSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedCluster != null && cmbNameSource.SelectedIndex >= 0)
        {
            _selectedCluster.NameSourceIndex = cmbNameSource.SelectedIndex;
            RebuildThumbnailStrip(); // refresh dot indicator
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  REMOVE / CLEAR
    // ══════════════════════════════════════════════════════════════

    private void RemoveImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not OptimiserImage oi) return;
        e.Handled = true;

        // Find and remove from cluster
        var cluster = _clusters.FirstOrDefault(c => c.Images.Contains(oi));
        if (cluster != null)
        {
            cluster.Images.Remove(oi);
            if (cluster.Images.Count > 0 && cluster.NameSourceIndex >= cluster.Images.Count)
                cluster.NameSourceIndex = cluster.Images.Count - 1;
            if (cluster.Images.Count == 0)
                _clusters.Remove(cluster);
        }

        if (_selectedImage == oi)
        {
            var firstImage = AllImages.FirstOrDefault();
            if (firstImage != null)
                SelectImage(firstImage);
            else
            {
                _selectedImage = null;
                _selectedCluster = null;
                imgPreview.Source = null;
                txtPlaceholder.Visibility = Visibility.Visible;
                indexInputPanel.Visibility = Visibility.Collapsed;
                splitButtonGroup.Visibility = Visibility.Collapsed;
                refreshButtonGroup.Visibility = Visibility.Collapsed;
                btnToggleRects.Visibility = Visibility.Collapsed;
                btnAspectRatio.Visibility = Visibility.Collapsed;
                detectionOverlay.Children.Clear();
                UpdatePreviewMargins();
            }
        }

        thumbnailStripBorder.Visibility = _clusters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildThumbnailStrip();
    }

    private void ThumbnailScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private List<OptimiserCluster>? _pendingAutoLink;

    private void ShowAutoLinkOffer(List<OptimiserCluster> newClusters)
    {
        _pendingAutoLink = newClusters;
        txtAutoLinkMessage.Text = $"{newClusters.Count} images added. Link them into one chain?";
        autoLinkBanner.Visibility = Visibility.Visible;
        UpdatePreviewMargins();
    }

    private void BtnAutoLink_Click(object sender, RoutedEventArgs e)
    {
        // Chain suggestion mode — enter chain mode for the suggested chain
        if (_pendingChainSuggestion != null)
        {
            var chain = _pendingChainSuggestion;
            _pendingChainSuggestion = null;
            autoLinkBanner.Visibility = Visibility.Collapsed;
            UpdatePreviewMargins();
            UpdateDetectionOverlay();
            EnterChainMode(chain);
            return;
        }

        // Multi-image auto-link mode
        if (_pendingAutoLink == null || _pendingAutoLink.Count < 2) return;

        var target = _pendingAutoLink[0];
        for (int i = 1; i < _pendingAutoLink.Count; i++)
        {
            if (!_clusters.Contains(_pendingAutoLink[i])) continue;
            target.Images.AddRange(_pendingAutoLink[i].Images);
            _clusters.Remove(_pendingAutoLink[i]);
        }
        _pendingAutoLink = null;
        autoLinkBanner.Visibility = Visibility.Collapsed;
        UpdatePreviewMargins();
        UpdateDetectionOverlay();
        RebuildThumbnailStrip();
        SelectImage(target.Images[0]);
    }

    private void BtnAutoLinkDismiss_Click(object sender, RoutedEventArgs e)
    {
        _pendingAutoLink = null;
        _pendingChainSuggestion = null;
        autoLinkBanner.Visibility = Visibility.Collapsed;
        UpdatePreviewMargins();
        UpdateDetectionOverlay();
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e) => ClearAll();

    public void AddFileFromPath(string filePath)
    {
        if (System.IO.File.Exists(filePath))
            AddImages(new[] { filePath });
    }

    private Action<List<string>>? _mysteryReturnCallback;

    /// <summary>When true, AddImages will use Algorithm detection instead of Atlas.</summary>
    public bool ForceAlgorithmDetection { get; set; }

    /// <summary>
    /// Configures the Image Optimiser to show "Back to Mysteries" instead of "Upload to Wiki",
    /// sets Algorithm as default detection, and registers a callback for returning split results.
    /// </summary>
    public void SetMysteryReturnMode(Action<List<string>> onComplete, string label = "Back to Mysteries")
    {
        _mysteryReturnCallback = onComplete;
        btnUploadWiki.Content = label;
        btnUploadWiki.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowLeft24 };

        // Force Algorithm detection — deferred to run after any atlas refresh
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var cluster in _clusters)
                foreach (var oi in cluster.Images)
                {
                    oi.DefaultDetectionSource = DetectionSource.Algorithm;
                    oi.PerObjectDetectionSource = null;
                    if (oi.AlgorithmObjects != null)
                        oi.DetectedObjects = oi.AlgorithmObjects;
                }
            UpdateDetectionOverlay();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void ClearAll()
    {
        _clusters.Clear();
        _selectedImage = null;
        _selectedCluster = null;
        _optimizedFiles.Clear();
        thumbnailPanel.Children.Clear();
        thumbnailStripBorder.Visibility = Visibility.Collapsed;
        imgPreview.Source = null;
        txtPlaceholder.Visibility = Visibility.Visible;
        indexInputPanel.Visibility = Visibility.Collapsed;
        splitButtonGroup.Visibility = Visibility.Collapsed;
        refreshButtonGroup.Visibility = Visibility.Collapsed;
        btnToggleRects.Visibility = Visibility.Collapsed;
        btnAspectRatio.Visibility = Visibility.Collapsed;
        btnPredictIndices.Visibility = Visibility.Collapsed;
        detectionOverlay.Children.Clear();
        autoLinkBanner.Visibility = Visibility.Collapsed;
        _pendingAutoLink = null;
        _pendingChainSuggestion = null;
        _suppressIndexReset = true;
        inputIndices.Text = "";
        _suppressIndexReset = false;
        UpdateUploadButtonState();
        UpdatePreviewMargins();
    }

    // ══════════════════════════════════════════════════════════════
    //  LINKING / UNLINKING
    // ══════════════════════════════════════════════════════════════

    private bool LinkImages(OptimiserImage target, OptimiserImage source)
    {
        if (target == source) return false;

        var targetCluster = _clusters.FirstOrDefault(c => c.Images.Contains(target));
        var sourceCluster = _clusters.FirstOrDefault(c => c.Images.Contains(source));
        if (targetCluster == null || sourceCluster == null || targetCluster == sourceCluster) return false;

        // Merge: append source images to target cluster
        targetCluster.Images.AddRange(sourceCluster.Images);
        _clusters.Remove(sourceCluster);

        // If in chain mode, activate scissors on newly linked images with detected objects
        if (_activeChain != null)
        {
            foreach (var oi in targetCluster.Images)
            {
                if (oi.DetectedObjects.Count > 0 && !oi.IsScissorsActive)
                    oi.IsScissorsActive = true;
            }
        }

        SelectImage(target);
        RebuildThumbnailStrip();
        return true;
    }

    private void MergeClusters(OptimiserCluster left, OptimiserCluster right)
    {
        if (!_clusters.Contains(left) || !_clusters.Contains(right)) return;

        left.Images.AddRange(right.Images);
        _clusters.Remove(right);

        SelectImage(left.Images[0]);
        RebuildThumbnailStrip();
    }

    private void UnlinkAt(OptimiserCluster cluster, int bondIndex)
    {
        if (bondIndex < 0 || bondIndex >= cluster.Images.Count - 1) return;

        var rightImages = cluster.Images.Skip(bondIndex + 1).ToList();
        cluster.Images.RemoveRange(bondIndex + 1, rightImages.Count);

        var newCluster = new OptimiserCluster();
        foreach (var img in rightImages) newCluster.Images.Add(img);

        var idx = _clusters.IndexOf(cluster);
        _clusters.Insert(idx + 1, newCluster);

        // Clamp name source index
        if (cluster.NameSourceIndex >= cluster.Images.Count)
            cluster.NameSourceIndex = cluster.Images.Count - 1;

        SelectImage(cluster.Images[0]);
        RebuildThumbnailStrip();
    }

    // ── Thumbnail drag & drop (reorder edges + link center) ──

    private enum DropZone { None, ReorderBefore, Link, ReorderAfter }
    private Border? _lastHighlightedThumb;

    private DropZone GetDropZone(Border border, DragEventArgs e)
    {
        var pos = e.GetPosition(border);
        double ratio = pos.X / border.ActualWidth;

        // Wider reorder edges (40%) for images inside a multi-image cluster
        double edge = 0.10;
        if (border.Tag is OptimiserImage oi)
        {
            var cluster = _clusters.FirstOrDefault(c => c.Images.Contains(oi));
            if (cluster != null && cluster.Images.Count > 1)
                edge = 0.25;
        }

        if (ratio < edge) return DropZone.ReorderBefore;
        if (ratio > 1.0 - edge) return DropZone.ReorderAfter;
        return DropZone.Link;
    }

    // ── Insertion indicator (vertical accent line between thumbnails) ──

    private void ShowInsertionIndicator(FrameworkElement anchor, bool afterAnchor)
    {
        if (_insertionLine == null)
        {
            _insertionLine = new Border
            {
                Width = 3,
                Height = 80,
                CornerRadius = new CornerRadius(1.5),
                Background = (Brush)FindResource("AccentFillColorDefaultBrush"),
                IsHitTestVisible = false
            };
        }

        if (!insertionCanvas.Children.Contains(_insertionLine))
            insertionCanvas.Children.Add(_insertionLine);

        _insertionLine.Visibility = Visibility.Visible;

        // Compute X position relative to thumbnailPanel (the Canvas' coordinate space)
        var anchorPos = anchor.TransformToVisual(thumbnailPanel).Transform(new System.Windows.Point(0, 0));
        double x = afterAnchor ? anchorPos.X + anchor.ActualWidth + 1 : anchorPos.X - 4;

        Canvas.SetLeft(_insertionLine, x);
        Canvas.SetTop(_insertionLine, (anchor.ActualHeight - 80) / 2);
    }

    private void HideInsertionIndicator()
    {
        if (_insertionLine != null)
            _insertionLine.Visibility = Visibility.Collapsed;
    }

    // ── Drag initiation ──

    private void ThumbBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var border = (Border)sender;
        var pos = e.GetPosition(border);

        if (_dragSource == null)
        {
            _thumbDragStart = pos;
            _dragSource = (OptimiserImage)border.Tag;
            return;
        }

        if (Math.Abs(pos.X - _thumbDragStart.X) < 8 && Math.Abs(pos.Y - _thumbDragStart.Y) < 8)
            return;

        _isDraggingThumb = true;
        var data = new DataObject("OptimiserImage", _dragSource);
        DragDrop.DoDragDrop(border, data, DragDropEffects.Move | DragDropEffects.Link);
        _isDraggingThumb = false;
        _dragSource = null;
        ClearDropHighlight();
        HideInsertionIndicator();
    }

    // ── Thumbnail DragOver / DragLeave / Drop ──

    private void ThumbBorder_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("OptimiserImage"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var border = (Border)sender;
        var source = (OptimiserImage)e.Data.GetData("OptimiserImage")!;
        var target = (OptimiserImage)border.Tag;

        if (source == target)
        {
            e.Effects = DragDropEffects.None;
            ClearDropHighlight();
            HideInsertionIndicator();
            e.Handled = true;
            return;
        }

        var zone = GetDropZone(border, e);
        e.Effects = zone == DropZone.Link ? DragDropEffects.Link : DragDropEffects.Move;

        // Visual feedback
        ClearDropHighlight();

        if (zone == DropZone.Link)
        {
            HideInsertionIndicator();
            _lastHighlightedThumb = border;
            border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x80, 0x80, 0xFF));
        }
        else
        {
            ShowInsertionIndicator(border, zone == DropZone.ReorderAfter);
        }

        e.Handled = true;
    }

    private void ThumbBorder_DragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        HideInsertionIndicator();
    }

    private void ClearDropHighlight()
    {
        if (_lastHighlightedThumb == null) return;
        var b = _lastHighlightedThumb;
        _lastHighlightedThumb = null;

        bool isSelected = b.Tag is OptimiserImage oi &&
                          _clusters.FirstOrDefault(c => c.Images.Contains(oi)) == _selectedCluster;
        b.BorderBrush = isSelected
            ? (Brush)FindResource("AccentFillColorDefaultBrush")
            : (Brush)FindResource("CardStrokeColorDefaultBrush");
        b.Background = (Brush)FindResource("SubtleFillColorSecondaryBrush");
    }

    private void ThumbBorder_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearDropHighlight();
        HideInsertionIndicator();
        if (!e.Data.GetDataPresent("OptimiserImage")) return;

        var source = (OptimiserImage)e.Data.GetData("OptimiserImage")!;
        var target = (OptimiserImage)((Border)sender).Tag;
        if (source == target) return;

        var zone = GetDropZone((Border)sender, e);

        if (zone == DropZone.Link)
        {
            LinkImages(target, source);
        }
        else
        {
            var sourceCluster = _clusters.FirstOrDefault(c => c.Images.Contains(source));
            var targetCluster = _clusters.FirstOrDefault(c => c.Images.Contains(target));
            if (sourceCluster == null || targetCluster == null) return;

            if (sourceCluster == targetCluster && sourceCluster.Images.Count > 1)
            {
                // Reorder individual image within its own cluster
                int targetIdx = sourceCluster.Images.IndexOf(target);
                if (zone == DropZone.ReorderAfter) targetIdx++;
                ReorderImageInCluster(sourceCluster, source, targetIdx);
            }
            else if (sourceCluster.Images.Count > 1 && AreClustersAdjacent(sourceCluster, targetCluster))
            {
                // Adjacent cluster — reorder image to the edge of its own cluster
                int srcIdx = _clusters.IndexOf(sourceCluster);
                int tgtIdx = _clusters.IndexOf(targetCluster);
                int newIdx = tgtIdx > srcIdx
                    ? sourceCluster.Images.Count - 1  // target is to the right → move to end
                    : 0;                               // target is to the left → move to start
                ReorderImageInCluster(sourceCluster, source, newIdx);
            }
            else
            {
                // Distant — move entire cluster
                ReorderCluster(sourceCluster, targetCluster, zone == DropZone.ReorderAfter);
            }
        }
    }

    // ── Separator DragOver / DragLeave / Drop ──

    private void SepBorder_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("OptimiserImage"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        if (sender is Border sep)
            ShowInsertionIndicator(sep, false);
        e.Handled = true;
    }

    private void SepBorder_DragLeave(object sender, DragEventArgs e)
    {
        HideInsertionIndicator();
    }

    private void SepBorder_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideInsertionIndicator();
        if (!e.Data.GetDataPresent("OptimiserImage")) return;

        var source = (OptimiserImage)e.Data.GetData("OptimiserImage")!;
        var sourceCluster = _clusters.FirstOrDefault(c => c.Images.Contains(source));
        if (sourceCluster == null) return;

        if (sender is not FrameworkElement el || el.Tag is not OptimiserCluster afterCluster) return;

        if (sourceCluster.Images.Count > 1)
        {
            // Separator is after 'afterCluster'. The next cluster is at afterCluster index + 1.
            int afterIdx = _clusters.IndexOf(afterCluster);
            int srcIdx = _clusters.IndexOf(sourceCluster);

            if (sourceCluster == afterCluster)
            {
                // Separator right after source cluster — move image to end
                ReorderImageInCluster(sourceCluster, source, sourceCluster.Images.Count - 1);
                return;
            }
            if (afterIdx + 1 < _clusters.Count && _clusters[afterIdx + 1] == sourceCluster)
            {
                // Separator right before source cluster — move image to start
                ReorderImageInCluster(sourceCluster, source, 0);
                return;
            }
        }

        if (sourceCluster == afterCluster) return;
        ReorderCluster(sourceCluster, afterCluster, after: true);
    }

    private void ReorderCluster(OptimiserCluster source, OptimiserCluster target, bool after)
    {
        _clusters.Remove(source);
        int targetIdx = _clusters.IndexOf(target);
        int insertIdx = after ? targetIdx + 1 : targetIdx;
        _clusters.Insert(insertIdx, source);

        RebuildThumbnailStrip();
    }

    private void ReorderImageInCluster(OptimiserCluster cluster, OptimiserImage image, int newIndex)
    {
        int oldIndex = cluster.Images.IndexOf(image);
        if (oldIndex < 0 || oldIndex == newIndex) return;

        cluster.Images.RemoveAt(oldIndex);
        if (newIndex > oldIndex) newIndex--;
        newIndex = Math.Clamp(newIndex, 0, cluster.Images.Count);
        cluster.Images.Insert(newIndex, image);

        // Adjust name source index if it was pointing to the moved image
        if (cluster.NameSourceIndex == oldIndex)
            cluster.NameSourceIndex = newIndex;
        else if (oldIndex < cluster.NameSourceIndex && newIndex >= cluster.NameSourceIndex)
            cluster.NameSourceIndex--;
        else if (oldIndex > cluster.NameSourceIndex && newIndex <= cluster.NameSourceIndex)
            cluster.NameSourceIndex++;

        RebuildThumbnailStrip();
        ShowPreviewForSelection();
        UpdateDetectionOverlay();
    }

    private bool AreClustersAdjacent(OptimiserCluster a, OptimiserCluster b)
    {
        int idxA = _clusters.IndexOf(a);
        int idxB = _clusters.IndexOf(b);
        return Math.Abs(idxA - idxB) == 1;
    }

    // ── Dynamic preview margins based on banner visibility ──

    private void UpdatePreviewMargins()
    {
        double top = autoLinkBanner.Visibility == Visibility.Visible ? 48 : 15;
        bool anyBottomButton = refreshButtonGroup.Visibility == Visibility.Visible
                            || btnPredictIndices.Visibility == Visibility.Visible;
        double bottom = anyBottomButton ? 48 : 15;
        var newMargin = new Thickness(15, top, 15, bottom);
        if (imgPreview.Margin != newMargin)
        {
            imgPreview.Margin = newMargin;
            // Margin change shifts the image position — redraw overlay to match
            UpdateDetectionOverlay();
        }
    }
}
