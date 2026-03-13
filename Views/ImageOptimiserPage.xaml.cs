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

    // ── Object detection constants ──
    private const int AlphaThreshold = 5;
    private const int MainAlphaThreshold = 80;
    private const int MinCellArea = 400;
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
                oi.RawDetectedObjects = DetectObjects(img);
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
                        oi.DefaultDetectionSource = DetectionSource.Atlas;
                        oi.RawDetectedObjects = spriteObjects;
                        oi.DetectedObjects = spriteObjects;
                        ordered = OrderObjects(spriteObjects);
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

            // Real-time merge: adapt DetectedObjects to suffix count
            if (suffixes.Length > 0)
            {
                foreach (var oi in activeImages)
                {
                    if (oi.RawDetectedObjects.Count > suffixes.Length)
                        oi.DetectedObjects = MergeToExpectedCount(oi.RawDetectedObjects, suffixes.Length);
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
            e.Handled = true;
            ProcessSplit();
        }
    }

    private void BtnSplit_Click(object sender, RoutedEventArgs e) => ProcessSplit();

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
    /// Returns the output directory for split/optimize results.
    /// If the source file is in the export directory, redirects to a sibling "Export - Items" folder.
    /// Otherwise returns the source file's own directory.
    /// </summary>
    private (string dir, bool redirected) GetOutputDir(string sourceFilePath)
    {
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
                infoBar.Message = "atlas_data.json not found. Re-extract textures from APK.";
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
        // The atlas_data.json has exact positions for every sprite — more reliable
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
        activeImg.DefaultDetectionSource = DetectionSource.Atlas;
        activeImg.PerObjectDetectionSource = null;
        activeImg.RawDetectedObjects = spriteObjects;
        activeImg.DetectedObjects = spriteObjects;

        var orderedObjects = OrderObjects(activeImg.DetectedObjects);

        // Step 4: Map each sprite-based object to its level via the indices array.
        // orderedSprites (Unity Y desc, X asc) and orderedObjects (image Y asc, X asc)
        // have equivalent ordering since imageY = imageHeight - unityY.
        // Match by position to handle any rounding differences in row detection.
        var parts = new List<string>();
        var rotations = new float[orderedObjects.Count];
        int matched = 0;
        var usedSpriteIndices = new HashSet<int>();

        for (int objIdx = 0; objIdx < orderedObjects.Count; objIdx++)
        {
            var obj = orderedObjects[objIdx];
            var objCenterX = obj.Full.Left + obj.Full.Width / 2.0;
            var objCenterY = obj.Full.Top + obj.Full.Height / 2.0;

            int bestIdx = -1;
            double bestDist = double.MaxValue;

            for (int i = 0; i < orderedSprites.Count; i++)
            {
                if (usedSpriteIndices.Contains(i)) continue;
                var s = orderedSprites[i];

                var spriteCenterX = s.RectX + s.RectWidth / 2.0;
                var spriteCenterY = imageHeight - s.RectY - s.RectHeight / 2.0;

                var dx = objCenterX - spriteCenterX;
                var dy = objCenterY - spriteCenterY;
                var dist = dx * dx + dy * dy;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                usedSpriteIndices.Add(bestIdx);
                if (indices[bestIdx] > 0)
                {
                    parts.Add(indices[bestIdx].ToString());
                    matched++;
                }
                else
                {
                    parts.Add("-");
                }

                // If sprite was rotated in atlas (rotate: true), it needs 90° CW correction when split
                if (orderedSprites[bestIdx].Rotated)
                    rotations[objIdx] = 90f;
            }
            else
            {
                parts.Add("-");
            }
        }

        // Store rotation data for use during split
        activeImg.ObjectRotations = rotations;

        // Format indices with row breaks matching visual layout (max 4 rows)
        var objectRows = GroupIntoRows(orderedObjects);
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
        infoBar.Message = $"Prediction ({method}): {matched}/{orderedObjects.Count} matched ({textureSprites.Count} sprites in atlas).";
        infoBar.Severity = matched == orderedObjects.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        infoBar.IsOpen = true;

        AppLogger.Info($"Prediction ({method}): {matched}/{orderedObjects.Count}{chainInfo}");
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
                var objects = DetectObjects(img);
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
            infoBar.Message = "atlas_data.json not found. Re-extract textures from APK.";
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

    private void ProcessSplit(string? overrideOutputDir = null)
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
                // Use stored DetectedObjects (sprite-based when atlas_data.json is available,
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

                    using var crop = sourceImage.Clone(x => x.Crop(obj.Full));

                    // Apply atlas rotation correction if sprite was stored rotated
                    bool isRotated = obj.Rotation != 0f;
                    if (isRotated)
                        crop.Mutate(x => x.Rotate(obj.Rotation));

                    int size = GetCanvasSize(crop.Width, crop.Height);
                    using var canvas = new Image<Rgba32>(size, size);

                    int px, py;
                    if (isRotated)
                    {
                        // After rotation, center the crop on canvas
                        px = (size - crop.Width) / 2;
                        py = (size - crop.Height) / 2;
                    }
                    else
                    {
                        // Use Main rect for precise centering (center of mass)
                        float cx = (obj.Main.Left + obj.Main.Right + 1) / 2f;
                        float cy = (obj.Main.Top + obj.Main.Bottom + 1) / 2f;
                        px = (int)Math.Round(size / 2f + 1.0f - (cx - obj.Full.Left));
                        py = (int)Math.Round(size / 2f + 1.0f - (cy - obj.Full.Top));
                    }

                    canvas.Mutate(x => x.DrawImage(crop, new Point(px, py), 1f));

                    string fullPath = singleObject && !redirected
                        ? nameSourceImg.FilePath
                        : System.IO.Path.Combine(dir, $"{name}{suffixes[i].PadLeft(2, '0')}.png");

                    using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        canvas.SaveAsPng(fs);

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
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Unsplit images detected",
                Content = "You have entered split levels but haven't split the images yet.\n\nDo you want to split them and proceed to optimisation?",
                PrimaryButtonText = "Split & proceed",
                SecondaryButtonText = "Skip",
                CloseButtonText = "Cancel",
                Owner = Window.GetWindow(this)
            };
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
            var result = await msgBox.ShowDialogAsync();

            if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                // Remember current selection to restore after splitting
                var savedImage = _selectedImage;
                var savedCluster = _selectedCluster;

                // Split each unsplit cluster
                _suppressIndexReset = true;
                foreach (var cluster in unsplitClusters)
                {
                    _selectedCluster = cluster;
                    _selectedImage = cluster.Images.FirstOrDefault();
                    inputIndices.Text = cluster.IndexText;
                    ProcessSplit();
                }
                _suppressIndexReset = false;

                // Restore original selection
                if (savedImage != null && savedCluster != null && _clusters.Contains(savedCluster))
                    SelectImage(savedImage);
                else if (_clusters.Count > 0)
                    SelectImage(_clusters[0].Images[0]);

                // Fall through to optimization below
            }
            else if (result == Wpf.Ui.Controls.MessageBoxResult.None)
            {
                return;
            }
            // Primary=split+proceed, Secondary=skip to optimize, None(Close)=abort
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
                    var objects = DetectObjects(img);
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

        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
            return;

        var exportDir = System.IO.Path.Combine(basePath, version, "Export - PNGs");
        if (!Directory.Exists(exportDir))
            return;

        // Build candidate filenames (highest priority first)
        var candidates = new List<string>();

        // Primary: resolve via PoolTag → prefab name (from game's PoolConfig)
        if (!string.IsNullOrEmpty(chain.PoolTag))
        {
            var textureName = SpriteMetadataService.ResolveSkeletonForPoolTag(chain.PoolTag, exportDir);
            if (textureName != null)
                candidates.Add($"{textureName}.png");
        }

        // Fallback: Item{ConfigKey}.png pattern
        candidates.Add($"Item{chain.ConfigKey}.png");

        // Also try merged keys
        if (chain.MergedFromConfigKeys != null)
            foreach (var mk in chain.MergedFromConfigKeys)
                candidates.Add($"Item{mk}.png");

        // Search in Export - PNGs/ and also in Assembled/ subfolder (Spine-rendered icons)
        var searchDirs = new[] { exportDir, System.IO.Path.Combine(exportDir, "Assembled") };
        foreach (var candidate in candidates)
        {
            foreach (var dir in searchDirs)
            {
                var fullPath = System.IO.Path.Combine(dir, candidate);
                if (!File.Exists(fullPath)) continue;

                // Skip if this image (or same filename from another folder) is already loaded
                var candidateFileName = System.IO.Path.GetFileName(fullPath);
                if (AllImages.Any(img => string.Equals(
                    System.IO.Path.GetFileName(img.FilePath),
                    candidateFileName,
                    StringComparison.OrdinalIgnoreCase)))
                    return;

                // Auto-load the image directly (skip suggestion banner)
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

    private static List<(Rectangle Full, Rectangle Main)> DetectObjects(Image<Rgba32> img)
    {
        if (img.Width < 130 && img.Height < 130)
            return new List<(Rectangle, Rectangle)> { (new Rectangle(0, 0, img.Width, img.Height), new Rectangle(0, 0, img.Width, img.Height)) };

        var visited = new bool[img.Width, img.Height];
        var list = new List<(Rectangle Full, Rectangle Main)>();

        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                if (!visited[x, y] && img[x, y].A > AlphaThreshold)
                {
                    var r = FloodFill(img, x, y, visited);
                    if (r.Full.Width * r.Full.Height >= MinCellArea)
                        list.Add(r);
                }
            }

        return MergeColumnStacks(list);
    }

    /// <summary>
    /// Merges vertically stacked objects that share the same horizontal column.
    /// Sprite sheets typically arrange items in a single row; multiple objects
    /// stacked vertically in one column are almost always parts of the same item
    /// (e.g. separate kebab skewers with a transparent gap between them).
    /// </summary>
    private static List<(Rectangle Full, Rectangle Main)> MergeColumnStacks(
        List<(Rectangle Full, Rectangle Main)> objects)
    {
        if (objects.Count <= 1) return objects;

        // Union-Find: group objects sharing >40% horizontal overlap
        int n = objects.Count;
        var parent = Enumerable.Range(0, n).ToArray();

        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Unite(int a, int b) { parent[Find(a)] = Find(b); }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                int overlapLeft = Math.Max(objects[i].Full.Left, objects[j].Full.Left);
                int overlapRight = Math.Min(objects[i].Full.Left + objects[i].Full.Width,
                                            objects[j].Full.Left + objects[j].Full.Width);
                int overlap = Math.Max(0, overlapRight - overlapLeft);
                int narrower = Math.Min(objects[i].Full.Width, objects[j].Full.Width);
                if (narrower > 0 && (double)overlap / narrower > 0.4)
                {
                    // Safety: only merge objects that are vertically close (parts of the same item).
                    // Items in different sprite-sheet rows share horizontal overlap but have large vertical gaps.
                    int iBot = objects[i].Full.Top + objects[i].Full.Height;
                    int jBot = objects[j].Full.Top + objects[j].Full.Height;
                    int vertGap = Math.Max(0, Math.Max(objects[i].Full.Top, objects[j].Full.Top)
                                             - Math.Min(iBot, jBot));
                    double avgW = (objects[i].Full.Width + objects[j].Full.Width) / 2.0;
                    if (vertGap <= avgW * 0.5)
                        Unite(i, j);
                }
            }

        // Build column groups
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.ContainsKey(root)) groups[root] = new();
            groups[root].Add(i);
        }

        // Only merge if majority of columns are single-object
        int singleCount = groups.Values.Count(g => g.Count == 1);
        if (singleCount * 2 <= groups.Count) return objects; // ≤50% single → likely a grid, don't merge

        // Merge multi-object columns
        var result = new List<(Rectangle Full, Rectangle Main)>();
        foreach (var group in groups.Values)
        {
            if (group.Count == 1)
            {
                result.Add(objects[group[0]]);
                continue;
            }

            // Union of all Full and Main rectangles in the group
            int fL = int.MaxValue, fT = int.MaxValue, fR = int.MinValue, fB = int.MinValue;
            int mL = int.MaxValue, mT = int.MaxValue, mR = int.MinValue, mB = int.MinValue;
            foreach (int idx in group)
            {
                var (full, main) = objects[idx];
                fL = Math.Min(fL, full.Left); fT = Math.Min(fT, full.Top);
                fR = Math.Max(fR, full.Left + full.Width); fB = Math.Max(fB, full.Top + full.Height);
                mL = Math.Min(mL, main.Left); mT = Math.Min(mT, main.Top);
                mR = Math.Max(mR, main.Left + main.Width); mB = Math.Max(mB, main.Top + main.Height);
            }
            result.Add((new Rectangle(fL, fT, fR - fL, fB - fT),
                         new Rectangle(mL, mT, mR - mL, mB - mT)));
        }

        return result;
    }

    private static (Rectangle Full, Rectangle Main) FloodFill(Image<Rgba32> img, int sx, int sy, bool[,] v)
    {
        int x1 = sx, x2 = sx, y1 = sy, y2 = sy;
        int mx1 = int.MaxValue, mx2 = int.MinValue, my1 = int.MaxValue, my2 = int.MinValue;
        bool hasMain = false;

        var q = new Queue<Point>();
        q.Enqueue(new Point(sx, sy));
        v[sx, sy] = true;

        while (q.Count > 0)
        {
            var p = q.Dequeue();
            x1 = Math.Min(x1, p.X); x2 = Math.Max(x2, p.X);
            y1 = Math.Min(y1, p.Y); y2 = Math.Max(y2, p.Y);

            if (img[p.X, p.Y].A >= MainAlphaThreshold)
            {
                mx1 = Math.Min(mx1, p.X); mx2 = Math.Max(mx2, p.X);
                my1 = Math.Min(my1, p.Y); my2 = Math.Max(my2, p.Y);
                hasMain = true;
            }

            foreach (var (dx, dy) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
            {
                int nx = p.X + dx, ny = p.Y + dy;
                if (nx >= 0 && nx < img.Width && ny >= 0 && ny < img.Height
                    && !v[nx, ny] && img[nx, ny].A > AlphaThreshold)
                {
                    v[nx, ny] = true;
                    q.Enqueue(new Point(nx, ny));
                }
            }
        }

        var full = new Rectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
        var main = hasMain ? new Rectangle(mx1, my1, mx2 - mx1 + 1, my2 - my1 + 1) : full;
        return (full, main);
    }

    private static List<(Rectangle Full, Rectangle Main)> OrderObjects(List<(Rectangle Full, Rectangle Main)> objects)
    {
        if (objects.Count <= 1) return objects;
        var rows = SplitIntoObjectRows(objects);
        return rows.SelectMany(r => r.OrderBy(o => o.Full.Left)).ToList();
    }

    /// <summary>
    /// Splits detected objects into visual rows based on Y positions.
    /// </summary>
    private static List<List<(Rectangle Full, Rectangle Main)>> SplitIntoObjectRows(
        List<(Rectangle Full, Rectangle Main)> objects)
    {
        var sorted = objects.OrderBy(o => o.Full.Top + o.Full.Height / 2.0).ToList();
        var rows = new List<List<(Rectangle Full, Rectangle Main)>>();
        var currentRow = new List<(Rectangle Full, Rectangle Main)> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            double prevCenter = currentRow.Last().Full.Top + currentRow.Last().Full.Height / 2.0;
            double currCenter = sorted[i].Full.Top + sorted[i].Full.Height / 2.0;
            double gap = currCenter - prevCenter;
            double threshold = Math.Max(currentRow.Last().Full.Height, sorted[i].Full.Height) / 2.0;

            if (gap > threshold)
            {
                rows.Add(currentRow);
                currentRow = new List<(Rectangle Full, Rectangle Main)>();
            }
            currentRow.Add(sorted[i]);
        }
        rows.Add(currentRow);
        return rows;
    }

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
    {
        if (objects.Count <= expectedCount) return objects;

        var list = new List<(Rectangle Full, Rectangle Main)>(objects);

        while (list.Count > expectedCount)
        {
            var ordered = OrderObjects(list);
            list = ordered;

            var widths = list.Select(o => (double)o.Full.Width).OrderBy(w => w).ToList();
            var areas = list.Select(o => (double)(o.Full.Width * o.Full.Height)).OrderBy(a => a).ToList();
            double medianW = widths[widths.Count / 2];
            double medianArea = areas[areas.Count / 2];

            var rows = new List<List<int>>();
            var currentRow = new List<int> { 0 };

            for (int i = 1; i < list.Count; i++)
            {
                double prevCenter = list[currentRow.Last()].Full.Top + list[currentRow.Last()].Full.Height / 2.0;
                double currCenter = list[i].Full.Top + list[i].Full.Height / 2.0;
                double gap = currCenter - prevCenter;
                double thresh = Math.Max(list[currentRow.Last()].Full.Height, list[i].Full.Height) / 2.0;

                if (gap > thresh)
                {
                    rows.Add(currentRow);
                    currentRow = new List<int>();
                }
                currentRow.Add(i);
            }
            rows.Add(currentRow);

            double bestScore = double.MinValue;
            int bestA = -1, bestB = -1;

            foreach (var row in rows)
            {
                for (int ri = 0; ri < row.Count; ri++)
                    for (int rj = ri + 1; rj < row.Count; rj++)
                    {
                        int ai = row[ri], bi = row[rj];
                        var a = list[ai]; var b = list[bi];

                        int xOvlp = Math.Max(0, Math.Min(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width) - Math.Max(a.Full.Left, b.Full.Left));
                        int yOvlp = Math.Max(0, Math.Min(a.Full.Top + a.Full.Height, b.Full.Top + b.Full.Height) - Math.Max(a.Full.Top, b.Full.Top));
                        double overlapArea = xOvlp * yOvlp;
                        double smallerArea = Math.Min((double)a.Full.Width * a.Full.Height, (double)b.Full.Width * b.Full.Height);
                        double bboxOverlapRatio = smallerArea > 0 ? overlapArea / smallerArea : 0;

                        double edgeGap = Math.Max(0, Math.Max(a.Full.Left, b.Full.Left) - Math.Min(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width));

                        double areaA = a.Full.Width * a.Full.Height;
                        double areaB = b.Full.Width * b.Full.Height;
                        bool fragA = areaA < 0.5 * medianArea;
                        bool fragB = areaB < 0.5 * medianArea;
                        double fragmentScore = (fragA && fragB) ? 2.0 : (fragA || fragB) ? -0.5 : 0.0;

                        double mergedW = Math.Max(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width) - Math.Min(a.Full.Left, b.Full.Left);
                        double sizeScore = 1.0 / (1.0 + Math.Abs(mergedW - medianW) / Math.Max(medianW, 1));

                        double gapScore = 1.0 / (1.0 + edgeGap);

                        double score = 10.0 * bboxOverlapRatio + 3.0 * fragmentScore + 2.0 * sizeScore + 1.0 * gapScore;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestA = ai;
                            bestB = bi;
                        }
                    }
            }

            if (bestA < 0 || bestB < 0) break;

            var objA = list[bestA]; var objB = list[bestB];
            int fLeft = Math.Min(objA.Full.Left, objB.Full.Left);
            int fTop = Math.Min(objA.Full.Top, objB.Full.Top);
            int fRight = Math.Max(objA.Full.Left + objA.Full.Width, objB.Full.Left + objB.Full.Width);
            int fBot = Math.Max(objA.Full.Top + objA.Full.Height, objB.Full.Top + objB.Full.Height);
            var mergedFull = new Rectangle(fLeft, fTop, fRight - fLeft, fBot - fTop);

            int mLeft = Math.Min(objA.Main.Left, objB.Main.Left);
            int mTop = Math.Min(objA.Main.Top, objB.Main.Top);
            int mRight = Math.Max(objA.Main.Left + objA.Main.Width, objB.Main.Left + objB.Main.Width);
            int mBot = Math.Max(objA.Main.Top + objA.Main.Height, objB.Main.Top + objB.Main.Height);
            var mergedMain = new Rectangle(mLeft, mTop, mRight - mLeft, mBot - mTop);

            list.RemoveAt(bestB);
            list.RemoveAt(bestA);
            list.Add((mergedFull, mergedMain));
        }

        return OrderObjects(list);
    }

    private static int GetCanvasSize(int w, int h)
    {
        int m = Math.Max(w, h);
        int[] s = { 96, 100, 105, 110, 115, 120, 128, 132, 136, 142, 148, 154, 160, 164, 172, 180, 188, 192, 196, 208, 216, 224, 240, 256, 512, 768, 1024 };
        return s.FirstOrDefault(x => x >= m) == 0 ? 256 : s.First(x => x >= m);
    }
}
