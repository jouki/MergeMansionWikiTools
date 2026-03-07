using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MergeMansionWikiTools.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public partial class DialogueMakerPage : UserControl
{
    private readonly MainWindow _main;
    private readonly DialogueExtractorService _extractor = new();
    private readonly List<ImageEntry> _images = new();
    private readonly SemaphoreSlim _ocrLock = new(1, 1);
    private string? _fullOutput;
    private int _nextIndex = 1;

    // Drag & drop state
    private Point _dragStartPoint;
    private bool _isDragging;
    private bool _mouseDownTracked;
    private ImageEntry? _dragEntry;
    private Window? _dragWindow;
    private Border? _insertionIndicator;
    private int _pendingInsertIndex = -1;

    // P/Invoke for drag visual positioning
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private class ImageEntry
    {
        public int OriginalIndex { get; set; }
        public string FileName { get; }
        public byte[] Data { get; }
        public DialogueExtractorService.DialogueLine? Result { get; set; }
        public Grid Container { get; set; } = null!;
        public Border ImageBorder { get; set; } = null!;
        public TextBlock NameLabel { get; set; } = null!;

        public ImageEntry(string fileName, byte[] data, int originalIndex)
        {
            FileName = fileName;
            Data = data;
            OriginalIndex = originalIndex;
        }
    }

    public DialogueMakerPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        UpdateImageCount();

        // Pre-create insertion indicator on drag canvas
        _insertionIndicator = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _insertionIndicator.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        Canvas.SetZIndex(_insertionIndicator, 100);
        dragCanvas.Children.Add(_insertionIndicator);
    }

    // ── Keyboard shortcut: Ctrl+V paste ──

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            PasteFromClipboard();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Drop zone (file drop from Explorer) ──

    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            dropZone.BorderBrush = (Brush)FindResource("AccentFillColorDefaultBrush");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        dropZone.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        dropZone.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorSecondaryBrush");

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddFiles(files);
        }
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        BrowseImages();
    }

    private void BrowseImages()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select dialogue screenshots",
            Filter = "Image files (*.jpg;*.jpeg;*.png;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.gif;*.webp|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            AddFiles(dlg.FileNames);
        }
    }

    private void PasteFromClipboard()
    {
        byte[]? imageBytes = null;

        if (Clipboard.ContainsImage())
        {
            try
            {
                if (Clipboard.GetData("PNG") is MemoryStream pngStream)
                    imageBytes = pngStream.ToArray();
            }
            catch { }

            if (imageBytes == null)
            {
                try
                {
                    var bitmapSource = Clipboard.GetImage();
                    if (bitmapSource != null)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                        using var ms = new MemoryStream();
                        encoder.Save(ms);
                        imageBytes = ms.ToArray();
                    }
                }
                catch (COMException) { }
            }

            if (imageBytes != null)
            {
                var name = $"clipboard_{DateTime.Now:HHmmss}.png";
                AddImageAndProcess(name, imageBytes);
                return;
            }
        }

        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files != null)
                AddFiles(files.Cast<string>().ToArray());
        }
    }

    private void AddFiles(string[] paths)
    {
        foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!SupportedExtensions.Contains(ext)) continue;
            if (!File.Exists(path)) continue;

            var data = File.ReadAllBytes(path);
            var fileName = Path.GetFileName(path);
            AddImageAndProcess(fileName, data);
        }
    }

    private void AddImageAndProcess(string fileName, byte[] data)
    {
        var entry = new ImageEntry(fileName, data, _nextIndex++);
        _images.Add(entry);
        AddThumbnail(entry);
        UpdateImageCount();

        _ = ProcessImageAsync(entry);
    }

    // ── Live OCR processing ──

    private async Task ProcessImageAsync(ImageEntry entry)
    {
        await _ocrLock.WaitAsync();
        try
        {
            entry.Result = await _extractor.ExtractSingleAsync(entry.Data);

            if (!_images.Contains(entry)) return;

            entry.NameLabel.Text = $"{entry.OriginalIndex} \u00B7 {entry.Result.Name}";
            entry.NameLabel.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            Increment(s => s.DialoguesExtracted++);
        }
        catch (Exception ex)
        {
            if (!_images.Contains(entry)) return;

            entry.Result = new DialogueExtractorService.DialogueLine("???", $"[error: {ex.Message}]");
            entry.NameLabel.Text = $"{entry.OriginalIndex} \u00B7 \u26A0";
            entry.NameLabel.ToolTip = ex.Message;
        }
        finally
        {
            _ocrLock.Release();
        }

        RegenerateOutput();
        UpdateImageCount();
    }

    private List<(int charIndex, int originalIndex)>? _entryPositions;

    private void RegenerateOutput()
    {
        indexCanvas.Children.Clear();

        var results = _images
            .Where(i => i.Result != null)
            .ToList();

        if (results.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            _entryPositions = new List<(int, int)>();

            for (int i = 0; i < results.Count; i++)
            {
                _entryPositions.Add((sb.Length, results[i].OriginalIndex));
                sb.Append($"'''{results[i].Result!.Name}''': {results[i].Result.Text}");
                sb.AppendLine();
                if (i < results.Count - 1)
                    sb.AppendLine();
            }

            txtOutput.Text = sb.ToString();
            _fullOutput = sb.ToString();
            txtOutput.Visibility = Visibility.Visible;
            btnCopy.IsEnabled = true;
            txtPlaceholder.Visibility = Visibility.Collapsed;

            // Position index labels after layout completes
            Dispatcher.InvokeAsync(PositionIndexLabels, System.Windows.Threading.DispatcherPriority.Background);
        }
        else
        {
            txtOutput.Text = "";
            txtOutput.Visibility = Visibility.Collapsed;
            _fullOutput = null;
            _entryPositions = null;
            btnCopy.IsEnabled = false;
            txtPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void PositionIndexLabels()
    {
        indexCanvas.Children.Clear();
        if (_entryPositions == null) return;

        foreach (var (charIdx, origIdx) in _entryPositions)
        {
            var rect = txtOutput.GetRectFromCharacterIndex(charIdx);
            if (rect.IsEmpty) continue;

            var label = new TextBlock
            {
                Text = origIdx.ToString(),
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                Width = 22,
                TextAlignment = TextAlignment.Right,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

            Canvas.SetTop(label, rect.Top);
            Canvas.SetLeft(label, 0);
            indexCanvas.Children.Add(label);
        }
    }

    // ── Thumbnail management ──

    private void AddThumbnail(ImageEntry entry)
    {
        var img = new System.Windows.Controls.Image
        {
            Width = 64,
            Height = 64,
            Stretch = Stretch.UniformToFill,
            Source = LoadThumbnail(entry.Data),
            ToolTip = entry.FileName,
            Cursor = Cursors.Hand,
        };

        var removeBtn = new System.Windows.Controls.Button
        {
            Content = "\u00D7",
            FontSize = 10,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -2, -2, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush")
        };

        var imageBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Child = img
        };

        var imageGrid = new Grid { Width = 68, Height = 68 };
        imageGrid.Children.Add(imageBorder);
        imageGrid.Children.Add(removeBtn);

        var nameLabel = new TextBlock
        {
            Text = $"{entry.OriginalIndex} \u00B7 ...",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 76,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush")
        };

        var container = new Grid
        {
            Margin = new Thickness(0, 0, 8, 8)
        };
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(imageGrid, 0);
        Grid.SetRow(nameLabel, 1);
        container.Children.Add(imageGrid);
        container.Children.Add(nameLabel);

        // ── Drag source + click-to-preview ──
        imageGrid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement fe && IsChildOfButton(fe))
            {
                _mouseDownTracked = false;
                return;
            }
            _dragStartPoint = e.GetPosition(null);
            _isDragging = false;
            _mouseDownTracked = true;
        };

        imageGrid.PreviewMouseMove += (_, e) =>
        {
            if (!_mouseDownTracked || e.LeftButton != MouseButtonState.Pressed || _isDragging) return;

            var pos = e.GetPosition(null);
            var diff = _dragStartPoint - pos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;
                _mouseDownTracked = false;
                StartDrag(entry);
            }
        };

        imageGrid.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_mouseDownTracked && !_isDragging)
            {
                _main.ShowImageOverlay(entry.Data);
            }
            _mouseDownTracked = false;
        };

        removeBtn.Click += (_, _) => RemoveImage(entry);

        entry.Container = container;
        entry.ImageBorder = imageBorder;
        entry.NameLabel = nameLabel;

        imageList.Items.Add(container);
    }

    private static bool IsChildOfButton(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is System.Windows.Controls.Button) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // ── Drag & drop with visual ──

    private void StartDrag(ImageEntry entry)
    {
        _dragEntry = entry;

        // Create floating drag window
        _dragWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            Width = 56,
            Height = 56,
            Content = new Border
            {
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Opacity = 0.8,
                Child = new System.Windows.Controls.Image
                {
                    Source = LoadThumbnail(entry.Data),
                    Width = 48,
                    Height = 48,
                    Stretch = Stretch.UniformToFill
                }
            }
        };
        _dragWindow.Show();

        // Make window click-through
        var dragHwnd = new WindowInteropHelper(_dragWindow).Handle;
        var style = GetWindowLong(dragHwnd, GWL_EXSTYLE);
        SetWindowLong(dragHwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT);

        // Dim the dragged card
        entry.Container.Opacity = 0.3;

        // Derive DPI scale from actual rendering coordinates (most reliable method).
        // PointToScreen returns physical screen pixels; the delta gives physical-pixels-per-DIP.
        var p0 = _main.PointToScreen(new Point(0, 0));
        var px = _main.PointToScreen(new Point(1, 0));
        var py = _main.PointToScreen(new Point(0, 1));
        double scaleX = px.X - p0.X;  // physical pixels per DIP, e.g. 1.5 at 150%
        double scaleY = py.Y - p0.Y;

        // Attach GiveFeedback to update drag window position.
        // GetCursorPos returns physical pixels; Window.Left/Top expects DIPs.
        void handleFeedback(object s, GiveFeedbackEventArgs e)
        {
            if (_dragWindow != null)
            {
                GetCursorPos(out var pt);
                _dragWindow.Left = pt.X / scaleX - 28;
                _dragWindow.Top = pt.Y / scaleY - 28;
            }
        }

        entry.Container.GiveFeedback += handleFeedback;

        var data = new DataObject("DialogueImageEntry", entry);
        DragDrop.DoDragDrop(entry.Container, data, DragDropEffects.Move);

        // Cleanup after drag
        entry.Container.GiveFeedback -= handleFeedback;
        entry.Container.Opacity = 1.0;
        _dragWindow?.Close();
        _dragWindow = null;
        _dragEntry = null;
        _isDragging = false;
        HideInsertionIndicator();
    }

    // ── Drag over / drop on image list wrapper (gap-based insertion) ──

    private void ImageListWrapper_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("DialogueImageEntry"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourceEntry = (ImageEntry)e.Data.GetData("DialogueImageEntry")!;
        e.Effects = DragDropEffects.Move;

        var cursorPos = e.GetPosition(dragCanvas);
        var (insertIdx, indicatorX, indicatorY, indicatorH) = FindNearestGap(sourceEntry, cursorPos);
        _pendingInsertIndex = insertIdx;

        if (insertIdx >= 0 && indicatorH > 0)
            ShowInsertionIndicator(indicatorX, indicatorY, indicatorH);
        else
            HideInsertionIndicator();

        e.Handled = true;
    }

    private void ImageListWrapper_DragLeave(object sender, DragEventArgs e)
    {
        HideInsertionIndicator();
        _pendingInsertIndex = -1;
    }

    private void ImageListWrapper_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("DialogueImageEntry")) return;

        var sourceEntry = (ImageEntry)e.Data.GetData("DialogueImageEntry")!;

        // Recalculate on drop to get the final position
        var cursorPos = e.GetPosition(dragCanvas);
        var (insertIdx, _, _, _) = FindNearestGap(sourceEntry, cursorPos);

        if (insertIdx < 0) { e.Handled = true; return; }

        int sourceIdx = _images.IndexOf(sourceEntry);
        if (sourceIdx < 0) { e.Handled = true; return; }

        // Remove source
        _images.RemoveAt(sourceIdx);
        imageList.Items.RemoveAt(sourceIdx);

        // Adjust insert index after removal
        int adjustedIdx = insertIdx > sourceIdx ? insertIdx - 1 : insertIdx;
        adjustedIdx = Math.Clamp(adjustedIdx, 0, _images.Count);

        // Insert at new position
        _images.Insert(adjustedIdx, sourceEntry);
        imageList.Items.Insert(adjustedIdx, sourceEntry.Container);

        HideInsertionIndicator();
        RegenerateOutput();
        e.Handled = true;
    }

    /// <summary>
    /// Finds the nearest insertion gap among visible cards (excluding the source).
    /// Returns (insertIndex, indicatorX, indicatorY, indicatorHeight).
    /// Insert index is the position in _images where the source should be inserted.
    /// </summary>
    private (int index, double x, double y, double h) FindNearestGap(ImageEntry source, Point cursor)
    {
        // Collect visible cards (excluding the dragged source) with their positions
        var cards = new List<(int listIndex, Rect bounds)>();
        for (int i = 0; i < _images.Count; i++)
        {
            if (_images[i] == source) continue;
            var c = _images[i].Container;
            if (c.ActualWidth == 0) continue;
            var topLeft = c.TranslatePoint(new Point(0, 0), dragCanvas);
            cards.Add((i, new Rect(topLeft, new Size(c.ActualWidth, c.ActualHeight))));
        }

        if (cards.Count == 0)
            return (0, 0, 0, 0);

        // Build gap positions (between/around cards)
        var gaps = new List<(int insertIndex, double x, double y, double h)>();

        for (int i = 0; i < cards.Count; i++)
        {
            var (listIdx, bounds) = cards[i];

            // Gap before this card (left edge)
            if (i == 0 || Math.Abs(cards[i].bounds.Y - cards[i - 1].bounds.Y) > 10)
            {
                // First card on this row: gap at left edge
                gaps.Add((listIdx, bounds.Left - 2, bounds.Top + 2, bounds.Height - 4));
            }
            else
            {
                // Mid-row gap: between previous card's right edge and this card's left edge
                double gapX = (cards[i - 1].bounds.Right + bounds.Left) / 2;
                gaps.Add((listIdx, gapX, bounds.Top + 2, bounds.Height - 4));
            }
        }

        // Gap after last card
        var last = cards[^1];
        gaps.Add((_images.Count, last.bounds.Right + 2, last.bounds.Top + 2, last.bounds.Height - 4));

        // Find nearest gap
        double bestDist = double.MaxValue;
        var bestGap = gaps[0];

        foreach (var gap in gaps)
        {
            double gapCenterY = gap.y + gap.h / 2;
            double dist = Math.Abs(cursor.X - gap.x) + Math.Abs(cursor.Y - gapCenterY) * 0.3;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestGap = gap;
            }
        }

        return bestGap;
    }

    private void ShowInsertionIndicator(double x, double y, double height)
    {
        if (_insertionIndicator == null) return;
        Canvas.SetLeft(_insertionIndicator, x - 1.5);
        Canvas.SetTop(_insertionIndicator, y);
        _insertionIndicator.Height = height;
        _insertionIndicator.Visibility = Visibility.Visible;
    }

    private void HideInsertionIndicator()
    {
        if (_insertionIndicator != null)
            _insertionIndicator.Visibility = Visibility.Collapsed;
        _pendingInsertIndex = -1;
    }

    // ── Remove, Clear, Copy ──

    private void RemoveImage(ImageEntry entry)
    {
        int idx = _images.IndexOf(entry);
        if (idx >= 0)
        {
            var removedIndex = entry.OriginalIndex;
            var removedName = entry.Result?.Name ?? entry.FileName;
            _images.RemoveAt(idx);
            imageList.Items.RemoveAt(idx);
            RenumberIndices();
            UpdateImageCount();
            RegenerateOutput();
            ShowInfo($"Removed image {removedIndex} ({removedName})", InfoBarSeverity.Informational, autoClose: false);
        }
    }

    /// <summary>
    /// Renumbers all images sequentially (1, 2, 3, ...) and updates their labels.
    /// Called after image removal.
    /// </summary>
    private void RenumberIndices()
    {
        for (int i = 0; i < _images.Count; i++)
        {
            _images[i].OriginalIndex = i + 1;
            if (_images[i].Result != null)
                _images[i].NameLabel.Text = $"{i + 1} \u00B7 {_images[i].Result!.Name}";
            else
                _images[i].NameLabel.Text = $"{i + 1} \u00B7 ...";
        }
        _nextIndex = _images.Count + 1;
    }

    private static BitmapImage LoadThumbnail(byte[] data)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(data);
        bi.DecodePixelWidth = 128;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private void UpdateImageCount()
    {
        var count = _images.Count;
        var processed = _images.Count(i => i.Result != null);

        if (count == 0)
            txtImageCount.Text = "No images";
        else if (processed < count)
            txtImageCount.Text = $"{count} image{(count != 1 ? "s" : "")} \u00B7 {processed} processed";
        else
            txtImageCount.Text = $"{count} image{(count != 1 ? "s" : "")}";

        btnClear.IsEnabled = count > 0;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _images.Clear();
        imageList.Items.Clear();
        _nextIndex = 1;
        UpdateImageCount();
        txtOutput.Text = "";
        txtOutput.Visibility = Visibility.Collapsed;
        indexCanvas.Children.Clear();
        _fullOutput = null;
        _entryPositions = null;
        btnCopy.IsEnabled = false;
        txtPlaceholder.Visibility = Visibility.Visible;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_fullOutput == null) return;

        Clipboard.SetDataObject(_fullOutput, true);
        _main.ShowStatus("Dialogue copied to clipboard.", InfoBarSeverity.Success);
    }

    private void ShowInfo(string message, InfoBarSeverity severity, bool autoClose = true)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;

        if (autoClose)
            _ = AutoCloseInfo();
    }

    private async Task AutoCloseInfo()
    {
        await Task.Delay(4000);
        infoBar.IsOpen = false;
    }
}
