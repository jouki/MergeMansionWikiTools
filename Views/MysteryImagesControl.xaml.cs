using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Reusable control for mystery image management.
/// Displays detected images, supports optimize + upload workflow.
/// Used in both MysteryGeneratorDialog (Images tab) and standalone MysteryDecorationUploadDialog.
/// </summary>
public partial class MysteryImagesControl : UserControl
{
    private MainWindow? _main;
    private MysteryEvent? _mystery;
    private List<DetectedDecorationFile> _detectedFiles = new();
    private readonly Dictionary<DetectedDecorationFile, System.Windows.Controls.CheckBox> _checkboxes = new();
    private readonly HashSet<DetectedDecorationFile> _optimizedFiles = new();
    private bool _initialized;

    public MysteryImagesControl()
    {
        InitializeComponent();
    }

    /// <summary>Initialize and auto-scan for the given mystery.</summary>
    public void Initialize(MainWindow main, MysteryEvent mystery)
    {
        if (_initialized) return;
        _initialized = true;
        _main = main;
        _mystery = mystery;
        _ = AutoScanAsync();
    }

    private async Task AutoScanAsync()
    {
        if (_main == null || _mystery == null) return;

        var exportDir = MysteryWikiService.ResolveExportPngsDir(
            _main.Settings.ImageExporterBasePath, _main.Settings.SelectedApkVersion);

        if (string.IsNullOrEmpty(exportDir))
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            pnlEmpty.Visibility = Visibility.Visible;
            txtEmptyMessage.Text = "Export path not configured.\nSet Image Exporter base path and APK version in Settings.";
            return;
        }

        txtExportPath.Text = exportDir;

        try
        {
            _detectedFiles = await Task.Run(() =>
                MysteryWikiService.DetectDecorationFiles(
                    exportDir, _mystery.ProgressionEventId, _mystery.Name,
                    _mystery.MysteryType == MysteryType.Pet, _mystery));

            pnlLoading.Visibility = Visibility.Collapsed;

            if (_detectedFiles.Count == 0)
            {
                pnlEmpty.Visibility = Visibility.Visible;
                txtEmptyMessage.Text = $"No image files found for {_mystery.ProgressionEventId}\nin {exportDir}";
                return;
            }

            // Check wiki existence
            var uploadable = _detectedFiles.Where(f => f.Category != "EventItem").ToList();
            if (uploadable.Count > 0)
            {
                var wikiFilenames = uploadable.Select(f => $"File:{f.WikiFilename}").ToList();
                var existMap = await MysteryWikiService.CheckPagesExistAsync(wikiFilenames);
                foreach (var file in uploadable)
                    file.ExistsOnWiki = existMap.GetValueOrDefault($"File:{file.WikiFilename}", false);
            }

            foreach (var f in _detectedFiles)
                if (f.OptimizedSize.HasValue)
                    _optimizedFiles.Add(f);

            await CheckExistingSplitEventItemAsync();

            BuildFileList();
            scrollFiles.Visibility = Visibility.Visible;
            btnOptimize.IsEnabled = true;
            btnUpload.IsEnabled = true;

            var uploadableCount = _detectedFiles.Count(f => f.Category != "EventItem");
            var preOptimized = _optimizedFiles.Count;
            var msg = $"Found {_detectedFiles.Count} files ({uploadableCount} uploadable)";
            if (preOptimized > 0) msg += $", {preOptimized} already optimized";
            ShowInfo(msg + ".", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            pnlEmpty.Visibility = Visibility.Visible;
            txtEmptyMessage.Text = $"Scan failed: {ex.Message}";
        }
    }

    // ── File list building ─────────────────────────────────────

    private void BuildFileList()
    {
        pnlFiles.Children.Clear();

        var prevChecked = new Dictionary<DetectedDecorationFile, bool>();
        foreach (var (f, cb) in _checkboxes)
            prevChecked[f] = cb.IsChecked == true;
        _checkboxes.Clear();

        // Select All
        var selectAllCb = new System.Windows.Controls.CheckBox
        {
            Content = "Select All", IsChecked = false,
            Margin = new Thickness(0, 0, 0, 8), FontSize = 12
        };
        selectAllCb.SetResourceReference(System.Windows.Controls.CheckBox.ForegroundProperty, "TextFillColorSecondaryBrush");
        pnlFiles.Children.Add(selectAllCb);

        var splitItems = _detectedFiles.Where(f => f.Category.StartsWith("Event Item Lv")).ToList();
        bool splitGroupHeaderAdded = false;

        foreach (var file in _detectedFiles)
        {
            if (!splitGroupHeaderAdded && file.Category.StartsWith("Event Item Lv") && splitItems.Count > 0)
            {
                splitGroupHeaderAdded = true;
                var groupHeader = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 8, 0, 0)
                };
                groupHeader.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                var headerText = new TextBlock
                {
                    Text = $"Event Item ({splitItems.Count} files): {_mystery?.EventItemName ?? "Unknown"}",
                    FontSize = 12, FontWeight = FontWeights.SemiBold
                };
                headerText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                groupHeader.Child = headerText;
                pnlFiles.Children.Add(groupHeader);
            }

            bool isEventItem = file.Category == "EventItem";

            var row = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 4)
            };
            row.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (isEventItem)
            {
                var spacer = new Border { Width = 24 };
                Grid.SetColumn(spacer, 0);
                grid.Children.Add(spacer);
            }
            else
            {
                bool wasChecked = prevChecked.TryGetValue(file, out var prev) ? prev : file.ExistsOnWiki != true;
                var cb = new System.Windows.Controls.CheckBox
                {
                    IsChecked = wasChecked,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(cb, 0);
                grid.Children.Add(cb);
                _checkboxes[file] = cb;
            }

            // Thumbnail
            try
            {
                if (File.Exists(file.SourcePath))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(file.SourcePath);
                    bi.DecodePixelWidth = 48;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();

                    var img = new System.Windows.Controls.Image
                    {
                        Source = bi, Width = 48, Height = 48,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        ToolTip = "Click to preview", Tag = file.SourcePath
                    };
                    ToolTipService.SetInitialShowDelay(img, 0);
                    img.MouseLeftButtonDown += (s, _) =>
                    {
                        if (s is System.Windows.Controls.Image i && i.Tag is string path)
                            ShowPreview(path);
                    };
                    Grid.SetColumn(img, 1);
                    grid.Children.Add(img);
                }
            }
            catch { }

            // Info
            var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            bool isSplitItem = file.Category.StartsWith("Event Item Lv");
            var displayName = isEventItem ? Path.GetFileName(file.SourcePath)
                : isSplitItem ? $"{file.Category.Replace("Event Item ", "")}  {file.WikiFilename}"
                : file.WikiFilename;

            var fileNameText = new TextBlock
            {
                Text = displayName, FontSize = 12, FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            fileNameText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            infoPanel.Children.Add(fileNameText);

            var sourceInfo = $"{file.Category} · {Path.GetFileName(file.SourcePath)}";
            if (_optimizedFiles.Contains(file) && file.OptimizedSize.HasValue)
                sourceInfo += $" · {file.OptimizedSize.Value / 1024.0:F1} KB";
            var sourceText = new TextBlock { Text = sourceInfo, FontSize = 11 };
            sourceText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            infoPanel.Children.Add(sourceText);

            Grid.SetColumn(infoPanel, 2);
            grid.Children.Add(infoPanel);

            // Right side
            if (isEventItem)
            {
                var btnOpt = new Wpf.Ui.Controls.Button
                {
                    Content = "Image Optimiser", Appearance = ControlAppearance.Secondary,
                    Height = 32, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0), Tag = file.SourcePath
                };
                btnOpt.Click += BtnOpenInOptimiser_Click;
                Grid.SetColumn(btnOpt, 3);
                grid.Children.Add(btnOpt);
            }
            else
            {
                var statusBadge = new Border
                {
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
                };
                var statusText = new TextBlock { FontSize = 11 };
                bool isOptimized = _optimizedFiles.Contains(file);
                if (isOptimized && file.ExistsOnWiki == true)
                {
                    statusBadge.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x60, 0xA0, 0xE0));
                    statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0xB0, 0xF0));
                    statusText.Text = "\u2713 Ready · Exists";
                }
                else if (isOptimized)
                {
                    statusBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xA0, 0x00));
                    statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xC0, 0x30));
                    statusText.Text = "\u2713 Optimized";
                }
                else if (file.ExistsOnWiki == true)
                {
                    statusBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xC0, 0x90, 0x00));
                    statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xA0, 0x20));
                    statusText.Text = "Exists";
                }
                else
                {
                    statusBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xA0, 0x00));
                    statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xC0, 0x30));
                    statusText.Text = "New";
                }
                statusBadge.Child = statusText;
                Grid.SetColumn(statusBadge, 3);
                grid.Children.Add(statusBadge);
            }

            row.Child = grid;
            pnlFiles.Children.Add(row);
        }

        // Wire Select All AFTER rows
        selectAllCb.Checked += (_, _) =>
        { foreach (var (f, cb) in _checkboxes) if (f.Category != "EventItem") cb.IsChecked = true; };
        selectAllCb.Unchecked += (_, _) =>
        { foreach (var (_, cb) in _checkboxes) cb.IsChecked = false; };

        var uploadable = _checkboxes.Where(kv => kv.Key.Category != "EventItem").ToList();
        if (uploadable.Count > 0 && uploadable.All(kv => kv.Value.IsChecked == true))
            selectAllCb.IsChecked = true;
    }

    // ── Event Item split detection ─────────────────────────────

    private async Task CheckExistingSplitEventItemAsync()
    {
        if (_main == null || _mystery == null) return;
        var eventItemEntry = _detectedFiles.FirstOrDefault(f => f.Category == "EventItem");
        if (eventItemEntry == null) return;
        var eventItemName = _mystery.EventItemName;
        if (string.IsNullOrEmpty(eventItemName)) return;

        string? searchDir = null;
        var workspaceDir = _main.Settings.ImageExporterBasePath;
        if (!string.IsNullOrEmpty(workspaceDir))
        {
            var pd = Path.Combine(workspaceDir, "Processed Images");
            if (Directory.Exists(pd)) searchDir = pd;
        }
        if (searchDir == null) return;

        var foundSplits = new List<(string path, int level, string wikiName)>();
        for (int lvl = 1; lvl <= 10; lvl++)
        {
            var wikiName = MysteryWikiService.FormatFileName(eventItemName, lvl);
            var filePath = Path.Combine(searchDir, wikiName);
            if (File.Exists(filePath)) foundSplits.Add((filePath, lvl, wikiName));
            else break;
        }
        if (foundSplits.Count == 0) return;

        _detectedFiles.Remove(eventItemEntry);
        foreach (var (path, level, wikiName) in foundSplits)
        {
            var file = new DetectedDecorationFile
            {
                SourcePath = path, WikiFilename = wikiName,
                Category = $"Event Item Lv{level:D2}"
            };
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                if (OptimizationWindow.HasOptMarker(bytes))
                { file.OptimizedSize = bytes.Length; _optimizedFiles.Add(file); }
            }
            catch { }
            _detectedFiles.Add(file);
        }

        var wikiNames = foundSplits.Select(s => $"File:{s.wikiName}").ToList();
        var existMap = await MysteryWikiService.CheckPagesExistAsync(wikiNames);
        foreach (var f in _detectedFiles.Where(f => f.Category.StartsWith("Event Item Lv")))
            f.ExistsOnWiki = existMap.GetValueOrDefault($"File:{f.WikiFilename}", false);
    }

    // ── Buttons ────────────────────────────────────────────────

    private void BtnOpenInOptimiser_Click(object sender, RoutedEventArgs e)
    {
        if (_main == null || sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not string filePath) return;
        _main.NavigateToImageOptimiserWithFile(filePath);
        Window.GetWindow(this)?.Close();
    }

    private void BtnOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_main == null || _mystery == null) return;
        // Open full dialog for optimization workflow
        var dialog = new MysteryDecorationUploadDialog(_main, _mystery);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
        // Refresh after dialog closes
        _initialized = false;
        _detectedFiles.Clear();
        _checkboxes.Clear();
        _optimizedFiles.Clear();
        _ = AutoScanAsync();
    }

    private void BtnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_main == null || _mystery == null) return;
        var dialog = new MysteryDecorationUploadDialog(_main, _mystery);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
        _initialized = false;
        _detectedFiles.Clear();
        _checkboxes.Clear();
        _optimizedFiles.Clear();
        _ = AutoScanAsync();
    }

    // ── Image preview ──────────────────────────────────────────

    private double _zoomLevel = 1.0;
    private int _imgNativeW, _imgNativeH;
    private Point _dragStart;
    private double _dragStartX, _dragStartY;
    private bool _isDragging, _didDrag;

    private void ShowPreview(string filePath)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(filePath);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            previewImage.Source = bi;
            _imgNativeW = bi.PixelWidth;
            _imgNativeH = bi.PixelHeight;
            previewOverlay.Visibility = Visibility.Visible;
            Dispatcher.InvokeAsync(CenterAndFitPreview, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch { }
    }

    private void CenterAndFitPreview()
    {
        double availW = previewCanvas.ActualWidth, availH = previewCanvas.ActualHeight;
        if (availW <= 0 || availH <= 0 || _imgNativeW <= 0) return;
        _zoomLevel = 1.0;
        while (_zoomLevel > 0.25 && (_imgNativeW * _zoomLevel > availW || _imgNativeH * _zoomLevel > availH))
            _zoomLevel -= 0.125;
        previewImage.Width = _imgNativeW * _zoomLevel;
        previewImage.Height = _imgNativeH * _zoomLevel;
        Canvas.SetLeft(previewImage, (availW - previewImage.Width) / 2);
        Canvas.SetTop(previewImage, (availH - previewImage.Height) / 2);
        var pct = (int)(_zoomLevel * 100);
        txtPreviewInfo.Text = $"{_imgNativeW}\u00D7{_imgNativeH} \u00B7 {pct}%";
    }

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (previewOverlay.Visibility == Visibility.Visible && previewImage.Source != null)
            CenterAndFitPreview();
    }

    private void PreviewOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDragging = true; _didDrag = false;
        _dragStart = e.GetPosition(previewCanvas);
        _dragStartX = Canvas.GetLeft(previewImage); _dragStartY = Canvas.GetTop(previewImage);
        if (double.IsNaN(_dragStartX)) _dragStartX = 0;
        if (double.IsNaN(_dragStartY)) _dragStartY = 0;
        previewOverlay.Cursor = System.Windows.Input.Cursors.SizeAll;
        previewOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(previewCanvas);
        if (Math.Abs(pos.X - _dragStart.X) > 3 || Math.Abs(pos.Y - _dragStart.Y) > 3) _didDrag = true;
        Canvas.SetLeft(previewImage, _dragStartX + (pos.X - _dragStart.X));
        Canvas.SetTop(previewImage, _dragStartY + (pos.Y - _dragStart.Y));
    }

    private void PreviewOverlay_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        bool wasDragging = _isDragging;
        _isDragging = false;
        previewOverlay.Cursor = System.Windows.Input.Cursors.Arrow;
        previewOverlay.ReleaseMouseCapture();
        if (wasDragging && !_didDrag)
        {
            var imgLeft = Canvas.GetLeft(previewImage); var imgTop = Canvas.GetTop(previewImage);
            if (double.IsNaN(imgLeft)) imgLeft = 0; if (double.IsNaN(imgTop)) imgTop = 0;
            var pos = e.GetPosition(previewCanvas);
            bool onImage = pos.X >= imgLeft && pos.Y >= imgTop
                && pos.X <= imgLeft + previewImage.Width && pos.Y <= imgTop + previewImage.Height;
            if (!onImage) { previewOverlay.Visibility = Visibility.Collapsed; previewImage.Source = null; }
        }
    }

    private void PreviewClose_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        previewOverlay.Visibility = Visibility.Collapsed; previewImage.Source = null;
        e.Handled = true;
    }

    private void PreviewScroll_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        var oldZoom = _zoomLevel;
        _zoomLevel += e.Delta > 0 ? 0.125 : -0.125;
        _zoomLevel = Math.Max(0.25, Math.Min(4.0, _zoomLevel));
        var cursorPos = e.GetPosition(previewCanvas);
        double left = Canvas.GetLeft(previewImage), top = Canvas.GetTop(previewImage);
        if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
        double ratio = _zoomLevel / oldZoom;
        previewImage.Width = _imgNativeW * _zoomLevel;
        previewImage.Height = _imgNativeH * _zoomLevel;
        Canvas.SetLeft(previewImage, cursorPos.X - (cursorPos.X - left) * ratio);
        Canvas.SetTop(previewImage, cursorPos.Y - (cursorPos.Y - top) * ratio);
        var pct = (int)(_zoomLevel * 100);
        txtPreviewInfo.Text = $"{_imgNativeW}\u00D7{_imgNativeH} \u00B7 {pct}%";
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }
}
