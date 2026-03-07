using System;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public enum UploadConflictChoice
{
    Force,
    ForceAll,
    Skip,
    SkipAll,
    Cancel
}

public partial class UploadConflictDialog : FluentWindow
{
    private static readonly HttpClient Http = new();
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x4C, 0xC2, 0x4C));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xE8, 0x4C, 0x4C));

    public UploadConflictChoice Choice { get; private set; } = UploadConflictChoice.Cancel;

    private readonly bool _isPartOfSplit;
    private readonly string? _currentUser;
    private int _newWidth, _newHeight;
    private long _newSize;

    public UploadConflictDialog(string filename, int remaining, string? localFilePath = null, bool isPartOfSplit = false, string? currentUser = null)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        _isPartOfSplit = isPartOfSplit;
        _currentUser = currentUser;

        txtMessage.Text = $"{filename} already exists on the wiki.";
        txtRemaining.Text = remaining > 0
            ? $"{remaining} file(s) remaining after this one."
            : "This is the last file.";

        // Load local (new) image + metadata
        if (localFilePath != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(localFilePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();
                imgNew.Source = bmp;

                _newWidth = bmp.PixelWidth;
                _newHeight = bmp.PixelHeight;
                _newSize = new System.IO.FileInfo(localFilePath).Length;

                SetMetaInlines(txtNewMeta, _newWidth, _newHeight, _newSize,
                    dimensionColor: null, sizeColor: null); // colors set after wiki data arrives
            }
            catch { /* no preview */ }
        }

        if (!string.IsNullOrEmpty(_currentUser))
            txtNewAuthor.Text = $"by {_currentUser}";

        // Fetch wiki (old) image async
        _ = FetchWikiImageAsync(filename);
    }

    private async System.Threading.Tasks.Task FetchWikiImageAsync(string filename)
    {
        try
        {
            var apiUrl = "https://merge-mansion.fandom.com/api.php" +
                         $"?action=query&titles=File:{Uri.EscapeDataString(filename)}" +
                         "&prop=imageinfo&iiprop=url|size|user&format=json";
            var json = await Http.GetStringAsync(apiUrl);
            var doc = JsonDocument.Parse(json);

            string? imageUrl = null;
            string? wikiUser = null;
            int wikiWidth = 0, wikiHeight = 0;
            long wikiSize = 0;

            foreach (var page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
            {
                if (page.Value.TryGetProperty("imageinfo", out var ii) && ii.GetArrayLength() > 0)
                {
                    var info = ii[0];
                    imageUrl = info.GetProperty("url").GetString();
                    if (info.TryGetProperty("width", out var w)) wikiWidth = w.GetInt32();
                    if (info.TryGetProperty("height", out var h)) wikiHeight = h.GetInt32();
                    if (info.TryGetProperty("size", out var s)) wikiSize = s.GetInt64();
                    if (info.TryGetProperty("user", out var u)) wikiUser = u.GetString();
                    break;
                }
            }

            if (imageUrl == null) { Dispatcher.Invoke(() => ringOld.Visibility = Visibility.Collapsed); return; }

            // Force original format to avoid CDN serving WebP (which loses alpha in WPF)
            var separator = imageUrl.Contains('?') ? "&" : "?";
            var fetchUrl = imageUrl + separator + "format=original";
            var bytes = await Http.GetByteArrayAsync(fetchUrl);
            Dispatcher.Invoke(() =>
            {
                try
                {
                    BitmapSource bmp;
                    var ms = new System.IO.MemoryStream(bytes);
                    var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];
                    // Ensure alpha channel is present
                    if (frame.Format.BitsPerPixel < 32 || !frame.Format.ToString().Contains("Bgra"))
                    {
                        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                        converted.Freeze();
                        bmp = converted;
                    }
                    else
                    {
                        frame.Freeze();
                        bmp = frame;
                    }
                    imgOld.Source = bmp;

                    var ow = wikiWidth > 0 ? wikiWidth : bmp.PixelWidth;
                    var oh = wikiHeight > 0 ? wikiHeight : bmp.PixelHeight;
                    var osz = wikiSize > 0 ? wikiSize : bytes.Length;

                    // Old: dimension color based on 1:1 ratio (only for split items)
                    Brush? oldDimColor = _isPartOfSplit ? (ow == oh ? GreenBrush : RedBrush) : null;

                    SetMetaInlines(txtOldMeta, ow, oh, osz,
                        dimensionColor: oldDimColor, sizeColor: null);

                    // Now update both sides with size comparison colors
                    Brush? oldSizeColor = null, newSizeColor = null;
                    if (_newSize > 0 && osz > 0)
                    {
                        if (_newSize < osz)
                        {
                            newSizeColor = GreenBrush;
                            oldSizeColor = RedBrush;
                        }
                        else if (_newSize > osz)
                        {
                            newSizeColor = RedBrush;
                            oldSizeColor = GreenBrush;
                        }
                    }

                    SetMetaInlines(txtOldMeta, ow, oh, osz,
                        dimensionColor: oldDimColor, sizeColor: oldSizeColor);

                    // New: dimension color based on 1:1 ratio (only for split items)
                    Brush? newDimColor = _isPartOfSplit ? (_newWidth == _newHeight ? GreenBrush : RedBrush) : null;

                    SetMetaInlines(txtNewMeta, _newWidth, _newHeight, _newSize,
                        dimensionColor: newDimColor, sizeColor: newSizeColor);

                    if (!string.IsNullOrEmpty(wikiUser))
                        txtOldAuthor.Text = $"by {wikiUser}";
                }
                catch { /* failed to decode */ }
                ringOld.Visibility = Visibility.Collapsed;
            });
        }
        catch
        {
            Dispatcher.Invoke(() => ringOld.Visibility = Visibility.Collapsed);
        }
    }

    private static void SetMetaInlines(System.Windows.Controls.TextBlock tb, int w, int h, long size,
        Brush? dimensionColor, Brush? sizeColor)
    {
        tb.Inlines.Clear();

        var dimRun = new Run($"{w}×{h}");
        if (dimensionColor != null)
            dimRun.Foreground = dimensionColor;
        else
            dimRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
        tb.Inlines.Add(dimRun);

        var sepRun = new Run("  ·  ");
        sepRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
        tb.Inlines.Add(sepRun);

        var sizeRun = new Run(FormatSize(size));
        if (sizeColor != null)
            sizeRun.Foreground = sizeColor;
        else
            sizeRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
        tb.Inlines.Add(sizeRun);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    private void BtnForce_Click(object sender, RoutedEventArgs e)    { Choice = UploadConflictChoice.Force;    Close(); }
    private void BtnForceAll_Click(object sender, RoutedEventArgs e) { Choice = UploadConflictChoice.ForceAll; Close(); }
    private void BtnSkip_Click(object sender, RoutedEventArgs e)     { Choice = UploadConflictChoice.Skip;     Close(); }
    private void BtnSkipAll_Click(object sender, RoutedEventArgs e)  { Choice = UploadConflictChoice.SkipAll;  Close(); }
    private void BtnCancel_Click(object sender, RoutedEventArgs e)   { Choice = UploadConflictChoice.Cancel;   Close(); }
}
