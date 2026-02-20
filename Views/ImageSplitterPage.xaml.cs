using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wpf.Ui.Controls;
using Point = SixLabors.ImageSharp.Point;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using Image = SixLabors.ImageSharp.Image;

namespace MergeMansionWikiTools.Views;

public partial class ImageSplitterPage : UserControl
{
    private readonly MainWindow _main;
    private string? _sourcePath;
    private readonly List<string> _lastGeneratedFiles = new();

    public ImageSplitterPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
    }

    private void Page_DragOver(object sender, DragEventArgs e) =>
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

    private void Page_Drop(object sender, DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files?.Length > 0)
        {
            _sourcePath = files[0];
            LoadPreviewImage(_sourcePath);
        }
    }

    private void LoadPreviewImage(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();

            imgPreview.Source = bitmap;
            txtPlaceholder.Visibility = Visibility.Collapsed;

            infoBar.Message = $"Loaded: {System.IO.Path.GetFileName(path)}";
            infoBar.Severity = InfoBarSeverity.Informational;
            infoBar.IsOpen = true;

            btnOpenOptimize.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            infoBar.Message = $"Error loading image: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
    }

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
        if (string.IsNullOrEmpty(_sourcePath)) return;

        var suffixes = inputIndices.Text.Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (suffixes.Length == 0) return;

        infoBar.Message = "Processing...";
        infoBar.Severity = InfoBarSeverity.Informational;
        infoBar.IsOpen = true;
        btnOpenOptimize.Visibility = Visibility.Collapsed;
        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));

        _lastGeneratedFiles.Clear();

        try
        {
            using (var image = Image.Load<Rgba32>(_sourcePath))
            {
                var objects = DetectObjects(image);
                var ordered = objects
                    .OrderBy(o => o.Full.Top)
                    .GroupBy(o => o.Full.Top / 60)
                    .SelectMany(g => g.OrderBy(o => o.Full.Left))
                    .ToList();

                if (ordered.Count > suffixes.Length)
                {
                    infoBar.Message = $"Error: Not enough indexes ({suffixes.Length}) for {ordered.Count} objects.";
                    infoBar.Severity = InfoBarSeverity.Error;
                    return;
                }

                string dir = System.IO.Path.GetDirectoryName(_sourcePath)!;
                string name = System.IO.Path.GetFileNameWithoutExtension(_sourcePath);
                bool singleObject = ordered.Count == 1;

                for (int i = 0; i < ordered.Count; i++)
                {
                    var obj = ordered[i];
                    int size = GetCanvasSize(obj.Full.Width, obj.Full.Height);

                    using (var canvas = new Image<Rgba32>(size, size))
                    {
                        float cx = (obj.Main.Left + obj.Main.Right + 1) / 2f;
                        float cy = (obj.Main.Top + obj.Main.Bottom + 1) / 2f;

                        int px = (int)Math.Round(size / 2f + 1.0f - (cx - obj.Full.Left));
                        int py = (int)Math.Round(size / 2f + 1.0f - (cy - obj.Full.Top));

                        using (var crop = image.Clone(x => x.Crop(obj.Full)))
                        {
                            canvas.Mutate(x => x.DrawImage(crop, new Point(px, py), 1f));
                        }

                        // Single object → overwrite original; multiple → use suffix
                        string fullPath = singleObject
                            ? _sourcePath
                            : System.IO.Path.Combine(dir, $"{name}{suffixes[i].PadLeft(2, '0')}.png");

                        using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            canvas.SaveAsPng(fs);
                        }

                        _lastGeneratedFiles.Add(fullPath);
                    }
                }

                infoBar.Message = $"Done! {ordered.Count} icons saved.";
                infoBar.Severity = InfoBarSeverity.Success;
                btnOpenOptimize.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            infoBar.Message = ex.Message;
            infoBar.Severity = InfoBarSeverity.Error;
        }
    }

    private List<(Rectangle Full, Rectangle Main)> DetectObjects(Image<Rgba32> img)
    {
        // Small images (< 130x130) are treated as a single sprite
        if (img.Width < 130 && img.Height < 130)
            return new List<(Rectangle, Rectangle)> { (new Rectangle(0, 0, img.Width, img.Height), new Rectangle(0, 0, img.Width, img.Height)) };

        var visited = new bool[img.Width, img.Height];
        var list = new List<(Rectangle, Rectangle)>();

        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                if (!visited[x, y] && img[x, y].A > 10)
                {
                    var r = FloodFill(img, x, y, visited);
                    if (r.f.Width * r.f.Height > 200)
                        list.Add((r.f, r.m));
                }
            }

        return list;
    }

    private (Rectangle f, Rectangle m) FloodFill(Image<Rgba32> img, int sx, int sy, bool[,] v)
    {
        int x1 = sx, x2 = sx, y1 = sy, y2 = sy;
        int mx1 = int.MaxValue, mx2 = int.MinValue, my1 = int.MaxValue, my2 = int.MinValue;

        var q = new Queue<Point>();
        q.Enqueue(new Point(sx, sy));
        v[sx, sy] = true;

        bool hasMain = false;

        while (q.Count > 0)
        {
            var p = q.Dequeue();

            x1 = Math.Min(x1, p.X);
            x2 = Math.Max(x2, p.X);
            y1 = Math.Min(y1, p.Y);
            y2 = Math.Max(y2, p.Y);

            if (img[p.X, p.Y].A >= 80)
            {
                mx1 = Math.Min(mx1, p.X);
                mx2 = Math.Max(mx2, p.X);
                my1 = Math.Min(my1, p.Y);
                my2 = Math.Max(my2, p.Y);
                hasMain = true;
            }

            foreach (var d in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
            {
                int nx = p.X + d.Item1;
                int ny = p.Y + d.Item2;

                if (nx >= 0 && nx < img.Width && ny >= 0 && ny < img.Height && !v[nx, ny] && img[nx, ny].A > 10)
                {
                    v[nx, ny] = true;
                    q.Enqueue(new Point(nx, ny));
                }
            }
        }

        var full = new Rectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
        var main = hasMain
            ? new Rectangle(mx1, my1, mx2 - mx1 + 1, my2 - my1 + 1)
            : full;

        return (full, main);
    }

    private static int GetCanvasSize(int w, int h)
    {
        int m = Math.Max(w, h);
        int[] s = { 96, 100, 105, 110, 115, 120, 128, 132, 136, 142, 148, 154, 160, 164, 172, 180, 188, 192, 196, 208, 216, 224, 240, 256, 512, 768, 1024 };
        return s.FirstOrDefault(x => x >= m) == 0 ? 256 : s.First(x => x >= m);
    }

    private void BtnOpenOptimize_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = _main.Settings.TinifyApiKey;
        var apiKey2 = _main.Settings.TinifyApiKey2;
        var optWin = new OptimizationWindow(_lastGeneratedFiles, apiKey, apiKey2);
        optWin.Owner = Window.GetWindow(this);
        optWin.ShowDialog();
    }
}
