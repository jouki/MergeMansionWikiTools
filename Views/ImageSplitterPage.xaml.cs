using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

internal class SplitterImage
{
    public string FilePath { get; set; } = "";
    public BitmapImage? Thumbnail { get; set; }
    public List<(Rectangle Full, Rectangle Main)> DetectedObjects { get; set; } = new();
    public int DetectedColumns { get; set; }
}

internal class ImageCluster
{
    public List<SplitterImage> Images { get; set; } = new();
    public string IndexText { get; set; } = "";
    public int NameSourceIndex { get; set; } // index into Images for output filename
}

public partial class ImageSplitterPage : UserControl
{
    private readonly MainWindow _main;
    private readonly List<string> _lastGeneratedFiles = new();

    // ── Multi-image state ──
    private const int MaxColumnsPerRow = 15;
    private readonly List<SplitterImage> _allImages = new();
    private readonly List<ImageCluster> _clusters = new();
    private ImageCluster? _selectedCluster;

    // ── Clipboard monitoring ──
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private DispatcherTimer? _clipboardTimer;
    private uint _lastClipboardSeq;

    // ── Thumbnail drag state ──
    private SplitterImage? _dragSource;
    private bool _isDraggingThumb;

    public ImageSplitterPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            Wpf.Ui.Controls.InfoBar.IsOpenProperty, typeof(Wpf.Ui.Controls.InfoBar));
        dpd?.AddValueChanged(infoBar, (_, _) => { if (!infoBar.IsOpen) HideClipboardAdd(); });
    }

    // ══════════════════════════════════════════════════════════════
    //  PAGE DRAG & DROP (file drops on the whole page)
    // ══════════════════════════════════════════════════════════════

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (_isDraggingThumb)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (_isDraggingThumb) return;

        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        var imageFiles = files.Where(f => IsImageFile(f)).ToArray();
        if (imageFiles.Length == 0) return;

        AddImages(imageFiles);
    }

    private static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tiff" or ".webp";
    }

    // ══════════════════════════════════════════════════════════════
    //  MULTI-IMAGE MANAGEMENT
    // ══════════════════════════════════════════════════════════════

    private void AddImages(string[] paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            if (_allImages.Any(i => i.FilePath == path)) continue; // skip duplicates

            var si = new SplitterImage { FilePath = path };

            // Load thumbnail
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelHeight = 80;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();
                si.Thumbnail = bmp;
            }
            catch { continue; }

            // Detect objects
            try
            {
                using var img = Image.Load<Rgba32>(path);
                si.DetectedObjects = DetectObjects(img);
                var ordered = OrderObjects(si.DetectedObjects);
                // Count columns: max objects in any row
                if (ordered.Count > 0)
                {
                    var rows = ordered.GroupBy(o => o.Full.Top / 60);
                    si.DetectedColumns = rows.Max(g => g.Count());
                }
                else
                {
                    si.DetectedColumns = 0;
                }
            }
            catch
            {
                si.DetectedObjects = new();
                si.DetectedColumns = 0;
            }

            _allImages.Add(si);

            // Create a single-image cluster
            var cluster = new ImageCluster { Images = { si } };
            _clusters.Add(cluster);
        }

        // Select the last added cluster if nothing selected
        if (_selectedCluster == null && _clusters.Count > 0)
            SelectCluster(_clusters.Last());
        else
            ShowPreviewForSelection();

        BuildThumbnailStrip();

        var total = paths.Length;
        HideClipboardAdd();
        infoBar.Message = total == 1
            ? $"Loaded: {System.IO.Path.GetFileName(paths[0])}"
            : $"Loaded {total} images.";
        infoBar.Severity = InfoBarSeverity.Informational;
        infoBar.IsOpen = true;
        btnOpenOptimize.Visibility = Visibility.Collapsed;
    }

    public void AddClipboardImage(BitmapSource bmpSource)
    {
        // Save clipboard image to temp file, then add
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MergeMansionWikiTools");
        Directory.CreateDirectory(tempDir);
        var tempPath = System.IO.Path.Combine(tempDir, $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmpSource));
        using (var fs = new FileStream(tempPath, FileMode.Create))
            encoder.Save(fs);

        AddImages(new[] { tempPath });
    }

    private void RemoveImage(SplitterImage img)
    {
        // Find its cluster
        var cluster = _clusters.FirstOrDefault(c => c.Images.Contains(img));
        if (cluster != null)
        {
            cluster.Images.Remove(img);

            // Clamp NameSourceIndex after removal
            if (cluster.Images.Count > 0 && cluster.NameSourceIndex >= cluster.Images.Count)
                cluster.NameSourceIndex = cluster.Images.Count - 1;

            if (cluster.Images.Count == 0)
            {
                _clusters.Remove(cluster);
                if (_selectedCluster == cluster)
                {
                    _selectedCluster = _clusters.FirstOrDefault();
                    if (_selectedCluster != null)
                        SelectCluster(_selectedCluster);
                }
            }
        }

        _allImages.Remove(img);

        if (_allImages.Count == 0)
        {
            _selectedCluster = null;
            imgPreview.Source = null;
            txtPlaceholder.Visibility = Visibility.Visible;
            cmbNameSource.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowPreviewForSelection();
            UpdateNameSourceDropdown();
        }

        BuildThumbnailStrip();
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        _allImages.Clear();
        _clusters.Clear();
        _selectedCluster = null;

        imgPreview.Source = null;
        txtPlaceholder.Visibility = Visibility.Visible;
        thumbnailStripBorder.Visibility = Visibility.Collapsed;
        cmbNameSource.Visibility = Visibility.Collapsed;
        inputIndices.Text = "";
        btnOpenOptimize.Visibility = Visibility.Collapsed;
        infoBar.IsOpen = false;
    }

    // ══════════════════════════════════════════════════════════════
    //  LINKING / UNLINKING
    // ══════════════════════════════════════════════════════════════

    private bool LinkImages(SplitterImage target, SplitterImage source)
    {
        if (target == source) return false;

        var targetCluster = _clusters.First(c => c.Images.Contains(target));
        var sourceCluster = _clusters.First(c => c.Images.Contains(source));
        if (targetCluster == sourceCluster) return false;

        // Merge: append source images to target cluster
        targetCluster.Images.AddRange(sourceCluster.Images);
        targetCluster.IndexText = ""; // clear since mapping changes
        _clusters.Remove(sourceCluster);

        SelectCluster(targetCluster);
        BuildThumbnailStrip();
        return true;
    }

    private void UnlinkAt(ImageCluster cluster, int bondIndex)
    {
        if (bondIndex < 0 || bondIndex >= cluster.Images.Count - 1) return;

        var leftImages = cluster.Images.Take(bondIndex + 1).ToList();
        var rightImages = cluster.Images.Skip(bondIndex + 1).ToList();

        cluster.Images = leftImages;
        cluster.IndexText = "";

        var newCluster = new ImageCluster { Images = rightImages };
        var idx = _clusters.IndexOf(cluster);
        _clusters.Insert(idx + 1, newCluster);

        SelectCluster(cluster);
        BuildThumbnailStrip();
    }

    private void AutoLinkAll()
    {
        if (_clusters.Count <= 1) return;

        var first = _clusters[0];
        for (int i = _clusters.Count - 1; i >= 1; i--)
        {
            var c = _clusters[i];
            first.Images.AddRange(c.Images);
            _clusters.RemoveAt(i);
        }
        first.IndexText = "";
        SelectCluster(first);
        BuildThumbnailStrip();
    }

    // ══════════════════════════════════════════════════════════════
    //  SELECTION & PREVIEW
    // ══════════════════════════════════════════════════════════════

    private void SelectCluster(ImageCluster cluster)
    {
        // Save current index text
        if (_selectedCluster != null)
            _selectedCluster.IndexText = inputIndices.Text;

        _selectedCluster = cluster;
        inputIndices.Text = cluster.IndexText;

        ShowPreviewForSelection();
        UpdateNameSourceDropdown();
        HighlightSelectedThumbnails();
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
            // Single image — show its thumbnail/full preview
            imgPreview.Source = _selectedCluster.Images[0].Thumbnail;
        }
        else
        {
            // Multi-image cluster — build combined preview
            BuildCombinedPreview(_selectedCluster);
        }
    }

    /// <summary>
    /// Packs images into rows using the soft MaxColumnsPerRow limit.
    /// Returns list of rows, each row is a list of images that fit side-by-side.
    /// </summary>
    private static List<List<SplitterImage>> ComputeRowLayout(ImageCluster cluster)
    {
        var rows = new List<List<SplitterImage>>();
        var currentRow = new List<SplitterImage>();
        int currentCols = 0;

        foreach (var si in cluster.Images)
        {
            // If this image alone exceeds limit, give it its own row
            // Otherwise check if adding it would overflow
            if (currentRow.Count > 0 && currentCols + si.DetectedColumns > MaxColumnsPerRow)
            {
                rows.Add(currentRow);
                currentRow = new List<SplitterImage>();
                currentCols = 0;
            }
            currentRow.Add(si);
            currentCols += si.DetectedColumns;
        }
        if (currentRow.Count > 0)
            rows.Add(currentRow);

        return rows;
    }

    private void BuildCombinedPreview(ImageCluster cluster)
    {
        try
        {
            // Load bitmaps
            var bitmapCache = new Dictionary<string, BitmapImage>();
            foreach (var si in cluster.Images)
            {
                if (bitmapCache.ContainsKey(si.FilePath)) continue;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(si.FilePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();
                bitmapCache[si.FilePath] = bmp;
            }

            // Pack into rows using soft column limit
            var rows = ComputeRowLayout(cluster);

            // Calculate row pixel dimensions
            int maxRowWidth = 0;
            var rowDims = new List<(int w, int h)>();
            foreach (var row in rows)
            {
                int w = row.Sum(si => bitmapCache[si.FilePath].PixelWidth);
                int h = row.Max(si => bitmapCache[si.FilePath].PixelHeight);
                rowDims.Add((w, h));
                maxRowWidth = Math.Max(maxRowWidth, w);
            }
            int totalHeight = rowDims.Sum(d => d.h);

            if (maxRowWidth == 0 || totalHeight == 0) return;

            // Render — overflow rows are right-aligned
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double y = 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    double x = maxRowWidth - rowDims[r].w; // right-align
                    foreach (var si in rows[r])
                    {
                        var bmp = bitmapCache[si.FilePath];
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

        // Default: file with most detected objects
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
            _selectedCluster.NameSourceIndex = cmbNameSource.SelectedIndex;
    }

    // ══════════════════════════════════════════════════════════════
    //  THUMBNAIL STRIP
    // ══════════════════════════════════════════════════════════════

    private void BuildThumbnailStrip()
    {
        thumbnailPanel.Children.Clear();

        if (_allImages.Count == 0)
        {
            thumbnailStripBorder.Visibility = Visibility.Collapsed;
            return;
        }

        thumbnailStripBorder.Visibility = Visibility.Visible;

        foreach (var cluster in _clusters)
        {
            for (int i = 0; i < cluster.Images.Count; i++)
            {
                var si = cluster.Images[i];
                var isSelected = _selectedCluster == cluster;

                // Thumbnail card
                var thumbBorder = new Border
                {
                    Width = 88,
                    Height = 88,
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    BorderBrush = isSelected
                        ? (Brush)FindResource("AccentFillColorDefaultBrush")
                        : (Brush)FindResource("CardStrokeColorDefaultBrush"),
                    Background = (Brush)FindResource("SubtleFillColorSecondaryBrush"),
                    Margin = new Thickness(2),
                    Cursor = Cursors.Hand,
                    ClipToBounds = true,
                    Tag = si
                };

                var grid = new Grid();

                // Image
                var img = new System.Windows.Controls.Image
                {
                    Source = si.Thumbnail,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(4)
                };
                grid.Children.Add(img);

                // Object count badge (bottom-left)
                var countBadge = new Border
                {
                    Background = (Brush)FindResource("SubtleFillColorTertiaryBrush"),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(4)
                };
                countBadge.Child = new System.Windows.Controls.TextBlock
                {
                    Text = $"{si.DetectedObjects.Count}",
                    FontSize = 9,
                    Foreground = (Brush)FindResource("TextFillColorSecondaryBrush")
                };
                grid.Children.Add(countBadge);

                // Remove button (top-right)
                var removeBtn = new System.Windows.Controls.Button
                {
                    Content = "×",
                    Width = 18,
                    Height = 18,
                    FontSize = 11,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 2, 0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                    Cursor = Cursors.Hand,
                    Tag = si
                };
                removeBtn.Click += (s, _) =>
                {
                    var target = (SplitterImage)((FrameworkElement)s!).Tag;
                    RemoveImage(target);
                };
                grid.Children.Add(removeBtn);

                thumbBorder.Child = grid;

                // Click to select
                thumbBorder.MouseLeftButtonDown += (s, _) =>
                {
                    var target = (SplitterImage)((FrameworkElement)s!).Tag;
                    var c = _clusters.First(cl => cl.Images.Contains(target));
                    SelectCluster(c);
                };

                // Drag source
                thumbBorder.MouseMove += ThumbBorder_MouseMove;
                thumbBorder.AllowDrop = true;
                thumbBorder.DragOver += ThumbBorder_DragOver;
                thumbBorder.Drop += ThumbBorder_Drop;

                thumbnailPanel.Children.Add(thumbBorder);

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
                    linkIcon.Child = new System.Windows.Controls.TextBlock
                    {
                        Text = "🔗",
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    linkIcon.MouseLeftButtonDown += (_, _) => UnlinkAt(bondCluster, bondIndex);
                    thumbnailPanel.Children.Add(linkIcon);
                }
            }

            // Separator between clusters
            var sep = new Border
            {
                Width = 1,
                Height = 60,
                Background = (Brush)FindResource("CardStrokeColorDefaultBrush"),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            thumbnailPanel.Children.Add(sep);
        }

        // Remove trailing separator
        if (thumbnailPanel.Children.Count > 0 &&
            thumbnailPanel.Children[thumbnailPanel.Children.Count - 1] is Border lastBorder &&
            lastBorder.Width == 1)
        {
            thumbnailPanel.Children.RemoveAt(thumbnailPanel.Children.Count - 1);
        }
    }

    private void HighlightSelectedThumbnails()
    {
        foreach (var child in thumbnailPanel.Children)
        {
            if (child is Border b && b.Tag is SplitterImage si)
            {
                var cluster = _clusters.FirstOrDefault(c => c.Images.Contains(si));
                bool isSelected = cluster == _selectedCluster;
                b.BorderThickness = new Thickness(isSelected ? 2 : 1);
                b.BorderBrush = isSelected
                    ? (Brush)FindResource("AccentFillColorDefaultBrush")
                    : (Brush)FindResource("CardStrokeColorDefaultBrush");
            }
        }
    }

    // ── Thumbnail drag & drop (linking) ──

    private System.Windows.Point _thumbDragStart;

    private void ThumbBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var border = (Border)sender;
        var pos = e.GetPosition(border);

        if (_dragSource == null)
        {
            _thumbDragStart = pos;
            _dragSource = (SplitterImage)border.Tag;
            return;
        }

        if (Math.Abs(pos.X - _thumbDragStart.X) < 8 && Math.Abs(pos.Y - _thumbDragStart.Y) < 8)
            return;

        _isDraggingThumb = true;
        var data = new DataObject("SplitterImage", _dragSource);
        DragDrop.DoDragDrop(border, data, DragDropEffects.Link);
        _isDraggingThumb = false;
        _dragSource = null;
    }

    private void ThumbBorder_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("SplitterImage"))
            e.Effects = DragDropEffects.Link;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ThumbBorder_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent("SplitterImage")) return;

        var source = (SplitterImage)e.Data.GetData("SplitterImage")!;
        var target = (SplitterImage)((Border)sender).Tag;

        if (source != target)
            LinkImages(target, source);
    }

    // ══════════════════════════════════════════════════════════════
    //  SPLIT PROCESSING
    // ══════════════════════════════════════════════════════════════

    private void InputIndices_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            ProcessSplit();
        }
    }

    private void BtnProcess_Click(object sender, RoutedEventArgs e) => ProcessSplit();

    private void ProcessSplit()
    {
        // Save current index text
        if (_selectedCluster != null)
            _selectedCluster.IndexText = inputIndices.Text;

        if (_selectedCluster == null || _selectedCluster.Images.Count == 0)
        {
            // Legacy single-file fallback: check if there's any image at all
            if (_allImages.Count == 0) return;
            _selectedCluster = _clusters.FirstOrDefault();
            if (_selectedCluster == null) return;
        }

        var suffixes = inputIndices.Text.Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (suffixes.Length == 0) return;

        HideClipboardAdd();
        infoBar.Message = "Processing...";
        infoBar.Severity = InfoBarSeverity.Informational;
        infoBar.IsOpen = true;
        btnOpenOptimize.Visibility = Visibility.Collapsed;

        Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

        _lastGeneratedFiles.Clear();

        try
        {
            // Gather all objects from all images in the cluster
            var allOrdered = new List<(Rectangle Full, Rectangle Main, string SourcePath)>();
            int expectedCount = suffixes.Length;

            foreach (var si in _selectedCluster.Images)
            {
                using var img = Image.Load<Rgba32>(si.FilePath);
                var objects = DetectObjects(img);
                var ordered = OrderObjects(objects);

                // Apply expected-count merging per image when cluster has one image
                if (_selectedCluster.Images.Count == 1 && ordered.Count > expectedCount)
                    ordered = MergeToExpectedCount(ordered, expectedCount);

                foreach (var obj in ordered)
                    allOrdered.Add((obj.Full, obj.Main, si.FilePath));
            }

            if (allOrdered.Count > suffixes.Length)
            {
                infoBar.Message = $"Error: Not enough indexes ({suffixes.Length}) for {allOrdered.Count} objects.";
                infoBar.Severity = InfoBarSeverity.Error;
                return;
            }

            // Determine output dir and name from the name source
            var nameSourceImg = _selectedCluster.Images.Count > 1
                ? _selectedCluster.Images[Math.Clamp(_selectedCluster.NameSourceIndex, 0, _selectedCluster.Images.Count - 1)]
                : _selectedCluster.Images[0];
            string dir = System.IO.Path.GetDirectoryName(nameSourceImg.FilePath)!;
            string name = System.IO.Path.GetFileNameWithoutExtension(nameSourceImg.FilePath);
            bool singleObject = allOrdered.Count == 1;

            // We need to load source images for cropping — cache them
            var imageCache = new Dictionary<string, Image<Rgba32>>();

            try
            {
                foreach (var sourcePath in allOrdered.Select(o => o.SourcePath).Distinct())
                {
                    if (!imageCache.ContainsKey(sourcePath))
                        imageCache[sourcePath] = Image.Load<Rgba32>(sourcePath);
                }

                for (int i = 0; i < allOrdered.Count; i++)
                {
                    var obj = allOrdered[i];
                    var sourceImage = imageCache[obj.SourcePath];
                    int size = GetCanvasSize(obj.Full.Width, obj.Full.Height);

                    using var canvas = new Image<Rgba32>(size, size);
                    float cx = (obj.Main.Left + obj.Main.Right + 1) / 2f;
                    float cy = (obj.Main.Top + obj.Main.Bottom + 1) / 2f;
                    int px = (int)Math.Round(size / 2f + 1.0f - (cx - obj.Full.Left));
                    int py = (int)Math.Round(size / 2f + 1.0f - (cy - obj.Full.Top));

                    using (var crop = sourceImage.Clone(x => x.Crop(obj.Full)))
                        canvas.Mutate(x => x.DrawImage(crop, new Point(px, py), 1f));

                    string fullPath = singleObject
                        ? nameSourceImg.FilePath
                        : System.IO.Path.Combine(dir, $"{name}{suffixes[i].PadLeft(2, '0')}.png");

                    using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        canvas.SaveAsPng(fs);

                    _lastGeneratedFiles.Add(fullPath);
                }
            }
            finally
            {
                foreach (var img in imageCache.Values)
                    img.Dispose();
            }

            infoBar.Message = $"Done! {allOrdered.Count} icons saved.";
            infoBar.Severity = InfoBarSeverity.Success;
            btnOpenOptimize.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            infoBar.Message = ex.Message;
            infoBar.Severity = InfoBarSeverity.Error;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  OBJECT DETECTION — Flood Fill + Smart Merge (v2)
    // ══════════════════════════════════════════════════════════════

    private const int AlphaThreshold = 5;
    private const int MainAlphaThreshold = 80;
    private const int MinCellArea = 400;

    private static List<(Rectangle Full, Rectangle Main)> OrderObjects(List<(Rectangle Full, Rectangle Main)> objects)
    {
        if (objects.Count <= 1) return objects;

        // Sort by Y center
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

        return rows.SelectMany(r => r.OrderBy(o => o.Full.Left)).ToList();
    }

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

        return list;
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

    /// <summary>Merges closest object pairs until count matches expected, using multi-factor scoring.</summary>
    private static List<(Rectangle Full, Rectangle Main)> MergeToExpectedCount(
        List<(Rectangle Full, Rectangle Main)> objects, int expectedCount)
    {
        if (objects.Count <= expectedCount) return objects;

        var list = new List<(Rectangle Full, Rectangle Main)>(objects);

        while (list.Count > expectedCount)
        {
            var ordered = OrderObjects(list);
            list = ordered;

            // Compute medians
            var widths = list.Select(o => (double)o.Full.Width).OrderBy(w => w).ToList();
            var areas = list.Select(o => (double)(o.Full.Width * o.Full.Height)).OrderBy(a => a).ToList();
            double medianW = widths[widths.Count / 2];
            double medianArea = areas[areas.Count / 2];

            // Assign rows (same logic as OrderObjects)
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

            // Score ALL pairs in same row (bbox overlap is dominant signal)
            double bestScore = double.MinValue;
            int bestA = -1, bestB = -1;

            foreach (var row in rows)
            {
                for (int ri = 0; ri < row.Count; ri++)
                    for (int rj = ri + 1; rj < row.Count; rj++)
                    {
                        int ai = row[ri], bi = row[rj];
                        var a = list[ai]; var b = list[bi];

                        // BBox overlap area (dominant signal — parts of same item overlap)
                        int xOvlp = Math.Max(0, Math.Min(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width) - Math.Max(a.Full.Left, b.Full.Left));
                        int yOvlp = Math.Max(0, Math.Min(a.Full.Top + a.Full.Height, b.Full.Top + b.Full.Height) - Math.Max(a.Full.Top, b.Full.Top));
                        double overlapArea = xOvlp * yOvlp;
                        double smallerArea = Math.Min((double)a.Full.Width * a.Full.Height, (double)b.Full.Width * b.Full.Height);
                        double bboxOverlapRatio = smallerArea > 0 ? overlapArea / smallerArea : 0;

                        // Edge-to-edge gap
                        double edgeGap = Math.Max(0, Math.Max(a.Full.Left, b.Full.Left) - Math.Min(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width));

                        // Fragment analysis
                        double areaA = a.Full.Width * a.Full.Height;
                        double areaB = b.Full.Width * b.Full.Height;
                        bool fragA = areaA < 0.5 * medianArea;
                        bool fragB = areaB < 0.5 * medianArea;
                        double fragmentScore = (fragA && fragB) ? 2.0 : (fragA || fragB) ? -0.5 : 0.0;

                        // Merged size close to median
                        double mergedW = Math.Max(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width) - Math.Min(a.Full.Left, b.Full.Left);
                        double sizeScore = 1.0 / (1.0 + Math.Abs(mergedW - medianW) / Math.Max(medianW, 1));

                        // Proximity
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

            if (bestA < 0 || bestB < 0) break; // safety

            // Merge: union bboxes
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

            // Remove higher index first, then lower
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

    // ══════════════════════════════════════════════════════════════
    //  CLIPBOARD MONITORING
    // ══════════════════════════════════════════════════════════════

    private static readonly string _clipboardLogPath =
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clipboard.log");

    private static void ClipLog(string msg)
    {
        try { File.AppendAllText(_clipboardLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { /* ignore */ }
    }

    public void StartClipboardMonitor()
    {
        if (_clipboardTimer != null) return;
        _lastClipboardSeq = GetClipboardSequenceNumber();
        try { File.WriteAllText(_clipboardLogPath, ""); } catch { } // clear log on start
        ClipLog($"Monitor STARTED, initial seq={_lastClipboardSeq}");
        _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clipboardTimer.Tick += ClipboardMonitor_Tick;
        _clipboardTimer.Start();
    }

    public void StopClipboardMonitor()
    {
        ClipLog("Monitor STOPPED");
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

    private static bool ClipboardHasImage()
    {
        return Clipboard.ContainsImage()
            || Clipboard.ContainsData(DataFormats.Bitmap)
            || Clipboard.ContainsData(DataFormats.Dib)
            || GetClipboardImageFiles() != null;
    }

    private void ClipboardMonitor_Tick(object? sender, EventArgs e)
    {
        try
        {
            var seq = GetClipboardSequenceNumber();
            if (seq == _lastClipboardSeq) return;

            ClipLog($"Seq changed: {_lastClipboardSeq} → {seq}");

            // Check for copied image files first (Ctrl+C on .png in Explorer)
            var imageFiles = GetClipboardImageFiles();
            bool hasBitmap = Clipboard.ContainsImage()
                          || Clipboard.ContainsData(DataFormats.Bitmap)
                          || Clipboard.ContainsData(DataFormats.Dib);

            ClipLog($"hasBitmap={hasBitmap}, imageFiles={imageFiles?.Length ?? 0}");

            if (!hasBitmap && imageFiles == null)
            {
                _lastClipboardSeq = seq;
                ClipLog("No image — skipped");
                return;
            }

            if (_main.Settings.ClipboardAutoAdd)
            {
                _lastClipboardSeq = seq;
                if (imageFiles != null)
                {
                    ClipLog($"AutoAdd: {imageFiles.Length} file(s)");
                    AddImages(imageFiles);
                }
                else
                {
                    var bmp = Clipboard.GetImage();
                    ClipLog($"AutoAdd bitmap: {(bmp != null ? $"{bmp.PixelWidth}x{bmp.PixelHeight}" : "null")}");
                    if (bmp != null) AddClipboardImage(bmp);
                }
            }
            else
            {
                _lastClipboardSeq = seq;
                int count = imageFiles?.Length ?? 1;
                ClipLog($"Notification: {count} image(s) detected");
                ShowClipboardNotification(count);
            }
        }
        catch (Exception ex)
        {
            ClipLog($"Exception: {ex.Message}");
        }
    }

    // ── Ctrl+V paste ──

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

    private void BtnClipboardAdd_Click(object sender, RoutedEventArgs e)
    {
        HideClipboardAdd();
        PasteFromClipboard();
    }

    /// <summary>Called from MainWindow.PreviewKeyDown when IS page is active.</summary>
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
            var imageFiles = GetClipboardImageFiles();
            if (imageFiles != null)
            {
                _lastClipboardSeq = GetClipboardSequenceNumber();
                AddImages(imageFiles);
                return;
            }

            // Bitmap data (Print Screen, copy from editor)
            if (Clipboard.ContainsImage() || Clipboard.ContainsData(DataFormats.Bitmap) || Clipboard.ContainsData(DataFormats.Dib))
            {
                var bmp = Clipboard.GetImage();
                if (bmp != null)
                {
                    _lastClipboardSeq = GetClipboardSequenceNumber();
                    AddClipboardImage(bmp);
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

    // ══════════════════════════════════════════════════════════════
    //  OPTIMIZATION & WIKI UPLOAD (unchanged)
    // ══════════════════════════════════════════════════════════════

    private void BtnOpenOptimize_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = _main.Settings.TinifyApiKey;
        var apiKey2 = _main.Settings.TinifyApiKey2;
        var optWin = new OptimizationWindow(_lastGeneratedFiles, apiKey, apiKey2);
        optWin.Owner = Window.GetWindow(this);
        optWin.ShowDialog();
    }
}
