using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;
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

    // ── Clipboard monitoring ──
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private DispatcherTimer? _clipboardTimer;
    private uint _lastClipboardSeq;

    // ── Chain mode state ──
    private ParsedChain? _activeChain;
    private string? _resolvedFilenameBase;

    // ── Output folder redirect (for images from export dir) ──
    private string? _lastOutputDir;
    private string? _customSplitOutputDir; // session-persistent custom output folder

    // ── Object detection constants (delegated to ImageProcessingService) ──
    private const int AlphaThreshold = ImageProcessingService.AlphaThreshold;
    private const int MainAlphaThreshold = ImageProcessingService.MainAlphaThreshold;
    private const int MinCellArea = ImageProcessingService.MinCellArea;
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
                        var spriteObjects = textureSprites
                            .OrderByDescending(s => s.RectY)
                            .ThenBy(s => s.RectX)
                            .Select(s =>
                            {
                                int x = (int)s.RectX;
                                int y = img.Height - (int)(s.RectY + s.RectHeight);
                                int w = Math.Max(1, (int)s.RectWidth);
                                int h = Math.Max(1, (int)s.RectHeight);
                                var rect = new Rectangle(x, y, w, h);
                                return (Full: rect, Main: rect);
                            }).ToList();

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
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelHeight = decodeHeight;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

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
            // Single image — show full preview
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(_selectedCluster.Images[0].FilePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                imgPreview.Source = bmp;
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
            var bitmapCache = new Dictionary<string, BitmapImage>();
            foreach (var oi in cluster.Images)
            {
                if (bitmapCache.ContainsKey(oi.FilePath)) continue;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(oi.FilePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();
                bitmapCache[oi.FilePath] = bmp;
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
            var suffixes = inputIndices.Text
                .Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
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

                // Build bitmap cache for pixel dimensions
                var pixelDims = new Dictionary<string, (int w, int h)>();
                foreach (var oi in _selectedCluster.Images)
                {
                    if (pixelDims.ContainsKey(oi.FilePath)) continue;
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(oi.FilePath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        pixelDims[oi.FilePath] = (bmp.PixelWidth, bmp.PixelHeight);
                    }
                    catch { pixelDims[oi.FilePath] = (0, 0); }
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
            UpdatePredictButtonVisibility();
            UpdatePreviewMargins();
        }

        // Update overlay if toggled image is in the displayed cluster
        if (_selectedCluster != null && _selectedCluster.Images.Contains(oi))
            UpdateDetectionOverlay();

        RebuildThumbnailStrip();
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

    // ── Chain suggestion (from Map Indices) ──
    private ParsedChain? _pendingChainSuggestion;

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

    // ══════════════════════════════════════════════════════════════
    //  SPLIT (for scissors-active images)
    // ══════════════════════════════════════════════════════════════

    private void InputIndices_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressIndexReset) return;

        // Refresh overlay labels on every keystroke
        UpdateDetectionOverlay();

        if (_selectedCluster == null || !_selectedCluster.Images.Any(i => i.IsSplit)) return;

        // Compare normalized tokens — only reset if the actual indices changed
        var tokens = inputIndices.Text
            .Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
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
        var item = new System.Windows.Controls.MenuItem { Header = "Split To…" };
        item.Click += (_, _) =>
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
        menu.Items.Add(item);
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
        if (scissorsImages.Count == 0) return false;

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

        var textureSprites = SpriteMetadataService.GetSpritesForTexture(allSprites, imageFileName);

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
        var orderedSprites = textureSprites
            .OrderByDescending(s => s.RectY)
            .ThenBy(s => s.RectX)
            .ToList();

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
        var spriteObjects = orderedSprites.Select(s =>
        {
            int x = (int)s.RectX;
            int y = imageHeight - (int)(s.RectY + s.RectHeight);
            int w = Math.Max(1, (int)s.RectWidth);
            int h = Math.Max(1, (int)s.RectHeight);
            var rect = new Rectangle(x, y, w, h);
            return (Full: rect, Main: rect);
        }).ToList();
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
        var usedLevels = new HashSet<int>();
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
            if (level > 0 && usedLevels.Contains(level))
                continue; // Skip duplicate — same item, different sprite (e.g. front+back)

            if (level > 0)
            {
                usedLevels.Add(level);
                parts.Add(level.ToString());
                matched++;
            }
            else
            {
                // Unmatched sprite — check if it overlaps with an already-kept sprite.
                // If so, it's a secondary view (back/shadow) of the same item → skip.
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
                        if (spriteArea > 0 && (double)overlapArea / spriteArea > 0.3)
                        {
                            overlapsKept = true;
                            break;
                        }
                    }
                }
                if (overlapsKept) continue;

                parts.Add("-");
            }

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
    private void TryAutoPredict()
    {
        if (_selectedCluster == null) return;

        // Don't overwrite existing user input
        if (!string.IsNullOrWhiteSpace(_selectedCluster.IndexText)) return;

        var scissorsImages = _selectedCluster.Images.Where(i => i.IsScissorsActive).ToList();
        if (scissorsImages.Count == 0) return;

        RunPrediction(showWarnings: false);
    }

    /// <summary>
    /// Finds a chain whose items use the given texture, using deterministic skin mapping first.
    /// Falls back to heuristic ConfigKey matching if no skin mapping is available.
    /// </summary>
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

    // 0 = all visible, 1 = labels only (no rects/source icons), 2 = all hidden
    private int _overlayVisMode;

    private void BtnToggleRects_Click(object sender, RoutedEventArgs e)
    {
        _overlayVisMode = (_overlayVisMode + 1) % 3;
        ApplyOverlayVisibility();
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

            var orderedSprites = textureSprites
                .OrderByDescending(s => s.RectY)
                .ThenBy(s => s.RectX)
                .ToList();

            var spriteObjects = orderedSprites.Select(s =>
            {
                int x = (int)s.RectX;
                int y = imageHeight - (int)(s.RectY + s.RectHeight);
                int w = Math.Max(1, (int)s.RectWidth);
                int h = Math.Max(1, (int)s.RectHeight);
                var rect = new Rectangle(x, y, w, h);
                return (Full: rect, Main: rect);
            }).ToList();

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

    private void ProcessSplit(string? overrideOutputDir = null, bool loadExistingIfFound = false)
    {
        if (_selectedCluster == null || _selectedCluster.Images.Count == 0) return;

        // Collect scissors-active images in the cluster
        var scissorsImages = _selectedCluster.Images.Where(oi => oi.IsScissorsActive).ToList();
        if (scissorsImages.Count == 0) return;

        var suffixes = inputIndices.Text.Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
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
            var allOrdered = new List<(Rectangle Full, Rectangle Main, string SourcePath, float Rotation)>();

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
                    allOrdered.Add((obj.Full, obj.Main, oi.FilePath, rot));
                }
            }

            if (allOrdered.Count > suffixes.Length)
            {
                infoBar.Message = $"Not enough levels ({suffixes.Length}) for {allOrdered.Count} objects.";
                infoBar.Severity = InfoBarSeverity.Error;
                infoBar.IsOpen = true;
                return;
            }

            // Output dir and name from the name source image
            var nameSourceImg = _selectedCluster.Images[
                Math.Clamp(_selectedCluster.NameSourceIndex, 0, _selectedCluster.Images.Count - 1)];
            string name = System.IO.Path.GetFileNameWithoutExtension(nameSourceImg.FilePath);
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
                    var expectedPath = System.IO.Path.Combine(dir, $"{name}{suffixes[i].PadLeft(2, '0')}.png");
                    if (File.Exists(expectedPath))
                        existingFiles.Add(expectedPath);
                }

                if (existingFiles.Count > 0 && !loadExistingIfFound)
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
                            var expectedPath = System.IO.Path.Combine(dir, $"{name}{suffixes[i].PadLeft(2, '0')}.png");
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
                        var expectedPath = System.IO.Path.Combine(dir, $"{name}{suffixes[i].PadLeft(2, '0')}.png");
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

                    if (loadedResults.Count > 0)
                    {
                        foreach (var oi in scissorsImages)
                            oi.IsSplit = false;
                        scissorsImages[0].SplitResultFiles = loadedResults;
                        scissorsImages[0].IsSplit = true;
                        scissorsImages[0].IsOptimized = loadedResults.All(f => _optimizedFiles.Contains(f));
                        UpdateUploadButtonState();
                        return;
                    }
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
                        : System.IO.Path.Combine(dir, $"{name}{suffixes[i].PadLeft(2, '0')}.png");

                    // Use shared CropAndSave (identical to FlowchartImageService)
                    ImageProcessingService.CropAndSave(sourceImage, obj.Full, obj.Main, fullPath, obj.Rotation);

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



    // ══════════════════════════════════════════════════════════════
    //  OPTIMIZE ALL (TinyPNG)
    // ══════════════════════════════════════════════════════════════

    private async void BtnOptimizeAll_Click(object sender, RoutedEventArgs e)
    {
        // Save current cluster's index text before checking
        if (_selectedCluster != null)
            _selectedCluster.IndexText = inputIndices.Text;

        // Check if any cluster has actual indices set but hasn't been split yet
        var unsplitClusters = _clusters.Where(c =>
            c.IndexText.Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length > 0 &&
            c.Images.Any(i => i.IsScissorsActive) &&
            !c.Images.Any(i => i.IsSplit)).ToList();

        if (unsplitClusters.Count > 0)
        {
            // Check if existing processed files are available for any unsplit cluster
            bool hasExisting = false;
            int existingCount = 0;
            int existingOptCount = 0;
            var processedDir = GetProcessedImagesDir();

            if (processedDir != null)
            {
                foreach (var cluster in unsplitClusters)
                {
                    var nameIdx = Math.Clamp(cluster.NameSourceIndex, 0, cluster.Images.Count - 1);
                    var clName = System.IO.Path.GetFileNameWithoutExtension(cluster.Images[nameIdx].FilePath);
                    var suffArr = cluster.IndexText.Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var suf in suffArr)
                    {
                        if (suf == "-") continue;
                        var path = System.IO.Path.Combine(processedDir, $"{clName}{suf.PadLeft(2, '0')}.png");
                        if (File.Exists(path))
                        {
                            hasExisting = true;
                            existingCount++;
                            try
                            {
                                if (OptimizationWindow.HasOptMarker(File.ReadAllBytes(path)))
                                    existingOptCount++;
                            }
                            catch { }
                        }
                    }
                }
            }

            string content = "You have entered split levels but haven't split the images yet.\n\n";
            if (hasExisting)
                content += $"{existingCount} existing file(s) found in Processed Images" +
                    (existingOptCount > 0 ? $" ({existingOptCount} optimized)" : "") +
                    ".\n\n";
            content += "Do you want to split them and proceed to optimisation?";

            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Unsplit images detected",
                Content = content,
                PrimaryButtonText = hasExisting ? "Load Existing" : "Split & proceed",
                SecondaryButtonText = hasExisting ? "Split & proceed" : "Skip",
                CloseButtonText = "Cancel",
                MinWidth = 500,
                Owner = Window.GetWindow(this)
            };
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
            var result = await msgBox.ShowDialogAsync();

            bool doSplit = false;
            bool doLoad = false;

            if (hasExisting)
            {
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary) doLoad = true;
                else if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary) doSplit = true;
                else return; // Cancel
            }
            else
            {
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary) doSplit = true;
                else if (result == Wpf.Ui.Controls.MessageBoxResult.None) return;
                // Secondary = Skip → fall through to optimization
            }

            if (doLoad && processedDir != null)
            {
                // Load existing files from Processed Images (skip inner dialog)
                var savedImage = _selectedImage;
                var savedCluster = _selectedCluster;

                _suppressIndexReset = true;
                foreach (var cluster in unsplitClusters)
                {
                    _selectedCluster = cluster;
                    _selectedImage = cluster.Images.FirstOrDefault();
                    inputIndices.Text = cluster.IndexText;
                    ProcessSplit(loadExistingIfFound: true);
                }
                _suppressIndexReset = false;

                if (savedImage != null && savedCluster != null && _clusters.Contains(savedCluster))
                    SelectImage(savedImage);
                else if (_clusters.Count > 0)
                    SelectImage(_clusters[0].Images[0]);
            }
            else if (doSplit)
            {
                var savedImage = _selectedImage;
                var savedCluster = _selectedCluster;

                _suppressIndexReset = true;
                foreach (var cluster in unsplitClusters)
                {
                    _selectedCluster = cluster;
                    _selectedImage = cluster.Images.FirstOrDefault();
                    inputIndices.Text = cluster.IndexText;
                    ProcessSplit();
                }
                _suppressIndexReset = false;

                if (savedImage != null && savedCluster != null && _clusters.Contains(savedCluster))
                    SelectImage(savedImage);
                else if (_clusters.Count > 0)
                    SelectImage(_clusters[0].Images[0]);
            }
            // else: Skip → fall through to optimization
        }

        // Collect files to optimize: iterate by cluster to avoid duplicate merged images
        // If source is from export dir and not yet split, copy to output dir first
        var filesToOptimize = new List<string>();
        bool anyRedirected = false;
        foreach (var cluster in _clusters)
        {
            var clusterSplitFiles = new List<string>();
            bool hasSplit = false;
            foreach (var oi in cluster.Images)
            {
                if (oi.IsSplit && oi.SplitResultFiles.Count > 0)
                {
                    clusterSplitFiles.AddRange(oi.SplitResultFiles);
                    hasSplit = true;
                }
            }

            if (hasSplit && clusterSplitFiles.Count > 0)
            {
                filesToOptimize.AddRange(clusterSplitFiles);
            }
            else
            {
                foreach (var oi in cluster.Images)
                {
                    var (outDir, redirected) = GetOutputDir(oi.FilePath);
                    if (redirected)
                    {
                        anyRedirected = true;
                        Directory.CreateDirectory(outDir);
                        var destPath = System.IO.Path.Combine(outDir,
                            System.IO.Path.GetFileName(oi.FilePath));
                        File.Copy(oi.FilePath, destPath, overwrite: true);
                        filesToOptimize.Add(destPath);
                    }
                    else
                    {
                        filesToOptimize.Add(oi.FilePath);
                    }
                }
            }
        }

        if (filesToOptimize.Count == 0) return;

        var apiKey = _main.Settings.TinifyApiKey;
        var apiKey2 = _main.Settings.TinifyApiKey2;

        var optWin = new OptimizationWindow(filesToOptimize, apiKey, apiKey2)
        {
            Owner = Window.GetWindow(this)
        };
        optWin.ShowDialog();

        // Track which individual files were optimized
        var optimizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in optWin.Files)
        {
            if (f.IsOptimized)
                optimizedPaths.Add(f.Path);
        }
        _optimizedFiles.UnionWith(optimizedPaths);

        if (optimizedPaths.Count > 0)
        {
            // Mark images as optimized if all their files are optimized
            foreach (var oi in AllImages)
            {
                if (oi.IsSplit && oi.SplitResultFiles.Count > 0)
                    oi.IsOptimized = oi.SplitResultFiles.All(f => _optimizedFiles.Contains(f));
                else
                    oi.IsOptimized = _optimizedFiles.Contains(oi.FilePath);
            }

            Increment(s => s.ImagesOptimized += optimizedPaths.Count);

            var optMsg = $"Optimised {optimizedPaths.Count} images.";
            if (anyRedirected)
            {
                var outDir = filesToOptimize.Select(f => System.IO.Path.GetDirectoryName(f)!)
                    .FirstOrDefault(d => d.EndsWith("Export - Items", StringComparison.OrdinalIgnoreCase));
                if (outDir != null)
                {
                    _lastOutputDir = outDir;
                    btnOpenOutputFolder.Visibility = Visibility.Visible;
                    optMsg += $" → {System.IO.Path.GetFileName(outDir)}";
                }
            }
            infoBar.Message = optMsg;
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.IsOpen = true;

            // Refresh thumbnails
            foreach (var oi in AllImages)
            {
                var newThumb = LoadThumbnail(oi.FilePath, 80);
                if (newThumb != null) oi.Thumbnail = newThumb;
            }
            RebuildThumbnailStrip();
        }

        UpdateUploadButtonState();
    }

    // ══════════════════════════════════════════════════════════════
    //  CHAIN MODE
    // ══════════════════════════════════════════════════════════════

    private string? _suggestedImagePath;

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

    // ══════════════════════════════════════════════════════════════
    //  UPLOAD TO WIKI
    // ══════════════════════════════════════════════════════════════

    private void BtnUploadWiki_Click(object sender, RoutedEventArgs e)
    {
        // Mystery return mode — collect split results and return
        if (_mysteryReturnCallback != null)
        {
            var resultFiles = new List<string>();
            foreach (var cluster in _clusters)
                foreach (var oi in cluster.Images)
                    if (oi.IsSplit && oi.SplitResultFiles.Count > 0)
                        resultFiles.AddRange(oi.SplitResultFiles);

            if (resultFiles.Count == 0)
            {
                infoBar.Message = "No split images to return. Split images first.";
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.IsOpen = true;
                return;
            }

            var callback = _mysteryReturnCallback;
            _mysteryReturnCallback = null;
            // Restore button
            btnUploadWiki.Content = "Upload to Wiki";
            btnUploadWiki.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowUpload24 };

            callback(resultFiles);
            return;
        }

        if (!_main.Settings.WikiVerified)
        {
            infoBar.Message = "Wiki bot not configured. Set up credentials in Settings.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
            return;
        }

        if (_clusters.Count == 0) return;

        // Collect upload items: group by cluster; merged cluster split results share one group
        var uploadItems = new List<WikiUploadItem>();
        foreach (var cluster in _clusters)
        {
            // Determine the cluster's name source image
            var nameSourceIdx = Math.Clamp(cluster.NameSourceIndex, 0, cluster.Images.Count - 1);
            var nameSourceImg = cluster.Images[nameSourceIdx];
            var chainName = _activeChain?.DisplayName;
            var clusterChainName = chainName ?? nameSourceImg.DetectedChainName;
            var clusterGroupPath = nameSourceImg.FilePath; // shared SplitGroupSourcePath for the whole cluster

            // Collect all split result files across the cluster
            var clusterSplitFiles = new List<string>();
            bool hasSplit = false;
            foreach (var oi in cluster.Images)
            {
                if (oi.IsSplit && oi.SplitResultFiles.Count > 0)
                {
                    clusterSplitFiles.AddRange(oi.SplitResultFiles);
                    hasSplit = true;
                }
            }

            if (hasSplit && clusterSplitFiles.Count > 0)
            {
                // All split results from this cluster form ONE group
                foreach (var splitPath in clusterSplitFiles)
                {
                    uploadItems.Add(new WikiUploadItem
                    {
                        FilePath = splitPath,
                        DetectedChainName = clusterChainName,
                        IsPartOfSplitGroup = true,
                        SplitGroupSourcePath = clusterGroupPath,
                        IsOptimized = _optimizedFiles.Contains(splitPath)
                    });
                }
            }
            else if (cluster.Images.Count > 1)
            {
                // Multi-image cluster (linked): treat as group
                foreach (var oi in cluster.Images)
                {
                    uploadItems.Add(new WikiUploadItem
                    {
                        FilePath = oi.FilePath,
                        DetectedChainName = chainName ?? clusterChainName ?? oi.DetectedChainName,
                        IsPartOfSplitGroup = true,
                        SplitGroupSourcePath = clusterGroupPath,
                        IsOptimized = _optimizedFiles.Contains(oi.FilePath)
                    });
                }
            }
            else
            {
                // Single image: individual row
                var oi = cluster.Images[0];
                uploadItems.Add(new WikiUploadItem
                {
                    FilePath = oi.FilePath,
                    DetectedChainName = chainName ?? oi.DetectedChainName,
                    IsPartOfSplitGroup = false,
                    IsOptimized = _optimizedFiles.Contains(oi.FilePath)
                });
            }
        }

        var dialog = new WikiUploadDialog(_main, uploadItems)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();

        if (dialog.UploadedCount > 0)
        {
            infoBar.Message = $"Uploaded {dialog.UploadedCount} images to wiki.";
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.IsOpen = true;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  CLIPBOARD MONITORING
    // ══════════════════════════════════════════════════════════════

    public void StartClipboardMonitor()
    {
        if (_clipboardTimer != null) return;
        _lastClipboardSeq = GetClipboardSequenceNumber();
        _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clipboardTimer.Tick += ClipboardMonitor_Tick;
        _clipboardTimer.Start();
    }

    public void StopClipboardMonitor()
    {
        _clipboardTimer?.Stop();
        _clipboardTimer = null;
    }

    /// <summary>Returns clipboard image file paths (FileDrop with image extensions), or null.</summary>
    private static string[]? GetClipboardImageFiles()
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        var files = Clipboard.GetFileDropList();
        var images = new List<string>();
        foreach (string? f in files)
            if (f != null && IsImageFile(f) && File.Exists(f))
                images.Add(f);
        return images.Count > 0 ? images.ToArray() : null;
    }

    private void ClipboardMonitor_Tick(object? sender, EventArgs e)
    {
        try
        {
            var seq = GetClipboardSequenceNumber();
            if (seq == _lastClipboardSeq) return;

            // Check for copied image files first (Ctrl+C on .png in Explorer)
            var imageFiles = GetClipboardImageFiles();
            bool hasBitmap = Clipboard.ContainsImage()
                          || Clipboard.ContainsData(DataFormats.Bitmap)
                          || Clipboard.ContainsData(DataFormats.Dib);

            if (!hasBitmap && imageFiles == null)
            {
                _lastClipboardSeq = seq;
                return;
            }

            if (_main.Settings.ClipboardAutoAdd)
            {
                _lastClipboardSeq = seq;
                if (imageFiles != null)
                {
                    AddImages(imageFiles);
                }
                else
                {
                    var bmp = Clipboard.GetImage();
                    if (bmp != null)
                    {
                        // Save clipboard image to temp file, then add
                        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MergeMansionWikiTools");
                        Directory.CreateDirectory(tempDir);
                        var tempPath = System.IO.Path.Combine(tempDir, $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bmp));
                        using (var fs = new FileStream(tempPath, FileMode.Create))
                            encoder.Save(fs);

                        AddImages(new[] { tempPath });
                    }
                }
            }
            else
            {
                _lastClipboardSeq = seq;
                int count = imageFiles?.Length ?? 1;
                ShowClipboardNotification(count);
            }
        }
        catch
        {
            // Clipboard access can throw — silently ignore
        }
    }

    private void ShowClipboardNotification(int count)
    {
        infoBar.Content = null;
        infoBar.Message = count == 1
            ? "Image detected in clipboard — press Ctrl+V or click Add."
            : $"{count} images detected in clipboard — press Ctrl+V or click Add.";
        infoBar.Severity = InfoBarSeverity.Informational;
        infoBar.IsOpen = true;
        btnClipboardAdd.Visibility = Visibility.Visible;
    }

    private void HideClipboardAdd()
    {
        btnClipboardAdd.Visibility = Visibility.Collapsed;
    }

    private void BtnOpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputDir != null && System.IO.Directory.Exists(_lastOutputDir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastOutputDir) { UseShellExecute = true });
    }

    private void BtnClipboardAdd_Click(object sender, RoutedEventArgs e)
    {
        HideClipboardAdd();
        PasteFromClipboard();
    }

    // ══════════════════════════════════════════════════════════════
    //  CTRL+V PASTE
    // ══════════════════════════════════════════════════════════════

    /// <summary>Called from MainWindow.PreviewKeyDown when Optimiser page is active.</summary>
    public bool HandleCtrlV()
    {
        // Don't intercept when typing in the indices TextBox
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return false;

        HideClipboardAdd();
        PasteFromClipboard();
        return true;
    }

    private void PasteFromClipboard()
    {
        try
        {
            // Copied image files (Ctrl+C on .png in Explorer)
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var images = new List<string>();
                foreach (string? f in files)
                    if (f != null && IsImageFile(f) && File.Exists(f))
                        images.Add(f);

                if (images.Count > 0)
                {
                    _lastClipboardSeq = GetClipboardSequenceNumber();
                    AddImages(images.ToArray());
                    return;
                }
            }

            // Bitmap data (Print Screen, copy from editor)
            if (Clipboard.ContainsImage() || Clipboard.ContainsData(DataFormats.Bitmap) || Clipboard.ContainsData(DataFormats.Dib))
            {
                var bmp = Clipboard.GetImage();
                if (bmp != null)
                {
                    _lastClipboardSeq = GetClipboardSequenceNumber();

                    // Save clipboard image to temp file, then add
                    var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MergeMansionWikiTools");
                    Directory.CreateDirectory(tempDir);
                    var tempPath = System.IO.Path.Combine(tempDir, $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs = new FileStream(tempPath, FileMode.Create))
                        encoder.Save(fs);

                    AddImages(new[] { tempPath });
                    return;
                }
            }

            infoBar.Message = "No image found in clipboard.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            infoBar.Message = $"Failed to paste: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
    }

    private static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tiff" or ".webp";
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
