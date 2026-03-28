using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
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
    /// <summary>Upload all remaining + optimize unoptimized first.</summary>
    ForceAllOptimize,
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
    private string? _currentUser;
    private readonly string? _localFilePath;
    private string _filename;
    private int _newWidth, _newHeight;
    private long _newSize;

    // Wizard state
    private Func<byte[], Task<byte[]>>? _optimizeFunc;
    private bool _isOptimizeMode;

    // ── Standard conflict constructor (existing behavior) ──────

    public UploadConflictDialog(string filename, int remaining,
        string? localFilePath = null, bool isPartOfSplit = false, string? currentUser = null)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        _isPartOfSplit = isPartOfSplit;
        _currentUser = currentUser;
        _localFilePath = localFilePath;
        _filename = filename;

        txtMessage.Text = $"{filename} already exists on the wiki.";
        txtRemaining.Text = remaining > 0
            ? $"{remaining} file(s) remaining after this one."
            : "This is the last file.";

        LoadLocalImage(localFilePath);

        if (!string.IsNullOrEmpty(_currentUser))
            txtNewAuthor.Text = $"by {_currentUser}";

        _ = FetchWikiImageAsync(filename);
    }

    /// <summary>Private constructor for factory methods.</summary>
    private UploadConflictDialog(string filename, string? localFilePath)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        _filename = filename;
        _localFilePath = localFilePath;
        LoadLocalImage(localFilePath);
    }

    // ── Factory: new file confirm (single preview) ──────────────

    public static UploadConflictDialog CreateNewFileDialog(
        string filename, int remaining, string localFilePath, string? currentUser = null)
    {
        var dlg = new UploadConflictDialog(filename, localFilePath);
        dlg._currentUser = currentUser;
        dlg.txtMessage.Text = $"Upload new file: {filename}";
        dlg.txtRemaining.Text = remaining > 0
            ? $"{remaining} file(s) remaining after this one."
            : "This is the last file.";
        if (!string.IsNullOrEmpty(currentUser))
            dlg.txtNewAuthor.Text = $"by {currentUser}";
        dlg.SetSinglePreviewMode("Confirm Upload", "New (local)",
            "Confirm and Continue", "Upload All");
        return dlg;
    }

    // ── Factory: optimize wizard (morphs after optimization) ────

    /// <summary>
    /// Creates a wizard dialog that starts in optimize mode, then morphs to
    /// confirm (new file) or conflict (existing file) after optimization completes.
    /// The dialog stays open through the entire flow.
    /// </summary>
    private bool _hasUnoptimizedRemaining;

    public static UploadConflictDialog CreateOptimizeWizard(
        string filename, int remaining, string localFilePath,
        Func<byte[], Task<byte[]>> optimizeFunc,
        string? currentUser = null, bool hasUnoptimizedRemaining = false)
    {
        var dlg = new UploadConflictDialog(filename, localFilePath);
        dlg._optimizeFunc = optimizeFunc;
        dlg._isOptimizeMode = true;
        dlg._currentUser = currentUser;
        dlg._hasUnoptimizedRemaining = hasUnoptimizedRemaining;
        dlg.txtMessage.Text = $"{filename} is not optimized.";
        dlg.txtRemaining.Text = "Wiki images must be optimized via TinyPNG before upload.";
        dlg.SetSinglePreviewMode("Optimization Required", "Not optimized",
            "Optimize", "Optimize All");
        return dlg;
    }

    // ── Layout mode switching ────────────────────────────────────

    /// <summary>Shows the "Upload All (Force)" button when there are unoptimized files remaining.</summary>
    public void ShowForceOptimizeButton(bool show)
    {
        btnConfirmAllForce.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSinglePreviewMode(string title, string previewLabel,
        string confirmText, string confirmAllText)
    {
        Title = title;
        dialogTitleBar.Title = title;

        pnlOld.Visibility = Visibility.Collapsed;
        iconArrow.Visibility = Visibility.Collapsed;
        colOld.Width = new GridLength(0);
        colArrow.Width = new GridLength(0);

        txtNewLabel.Text = previewLabel;

        pnlConflictButtons.Visibility = Visibility.Collapsed;
        pnlConfirmButtons.Visibility = Visibility.Visible;
        btnConfirm.Content = confirmText;
        btnConfirmAll.Content = confirmAllText;

        Width = 420;
    }

    private void MorphToConflictMode()
    {
        Title = "Upload Conflict";
        dialogTitleBar.Title = "Upload Conflict";
        txtMessage.Text = $"{_filename} already exists on the wiki.";
        txtNewLabel.Text = "New (local)";

        if (!string.IsNullOrEmpty(_currentUser))
            txtNewAuthor.Text = $"by {_currentUser}";

        pnlOld.Visibility = Visibility.Visible;
        iconArrow.Visibility = Visibility.Visible;
        colOld.Width = new GridLength(1, GridUnitType.Star);
        colArrow.Width = GridLength.Auto;

        // Keep confirm buttons (with Upload All Force) but relabel for conflict
        btnConfirm.Content = "Force upload";
        btnConfirmAll.Content = "Force upload (all)";
        ShowForceOptimizeButton(_hasUnoptimizedRemaining);

        Width = 620;

        _ = FetchWikiImageAsync(_filename);
    }

    private void MorphToConfirmMode()
    {
        Title = "Confirm Upload";
        dialogTitleBar.Title = "Confirm Upload";
        txtMessage.Text = $"Upload new file: {_filename}";
        txtNewLabel.Text = "New (local)";

        if (!string.IsNullOrEmpty(_currentUser))
            txtNewAuthor.Text = $"by {_currentUser}";

        btnConfirm.Content = "Confirm and Continue";
        btnConfirmAll.Content = "Upload All";
        btnConfirm.IsEnabled = true;
        btnConfirmAll.IsEnabled = true;
        ShowForceOptimizeButton(_hasUnoptimizedRemaining);
    }

    // ── Image loading ───────────────────────────────────────────

    private void LoadLocalImage(string? localFilePath)
    {
        if (localFilePath == null) return;
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
            _newSize = new FileInfo(localFilePath).Length;

            SetMetaInlines(txtNewMeta, _newWidth, _newHeight, _newSize,
                dimensionColor: null, sizeColor: null);
        }
        catch { /* no preview */ }
    }

    private void ReloadLocalImage()
    {
        if (_localFilePath == null) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_localFilePath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            imgNew.Source = bmp;

            _newWidth = bmp.PixelWidth;
            _newHeight = bmp.PixelHeight;
            _newSize = new FileInfo(_localFilePath).Length;

            SetMetaInlines(txtNewMeta, _newWidth, _newHeight, _newSize,
                dimensionColor: null, sizeColor: null);
        }
        catch { /* ignore */ }
    }

    // ── Wiki image fetch ────────────────────────────────────────

    private async Task FetchWikiImageAsync(string filename)
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

                    Brush? oldDimColor = _isPartOfSplit ? (ow == oh ? GreenBrush : RedBrush) : null;

                    // Compare formatted sizes for color (not raw bytes)
                    Brush? oldSizeColor = null, newSizeColor = null;
                    if (_newSize > 0 && osz > 0)
                    {
                        var newFormatted = FormatSize(_newSize);
                        var oldFormatted = FormatSize(osz);
                        if (newFormatted != oldFormatted)
                        {
                            if (_newSize < osz)
                            { newSizeColor = GreenBrush; oldSizeColor = RedBrush; }
                            else
                            { newSizeColor = RedBrush; oldSizeColor = GreenBrush; }
                        }
                    }

                    SetMetaInlines(txtOldMeta, ow, oh, osz,
                        dimensionColor: oldDimColor, sizeColor: oldSizeColor);

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

    // ── Helpers ──────────────────────────────────────────────────

    private static void SetMetaInlines(System.Windows.Controls.TextBlock tb, int w, int h, long size,
        Brush? dimensionColor, Brush? sizeColor)
    {
        tb.Inlines.Clear();

        var dimRun = new Run($"{w}\u00D7{h}");
        if (dimensionColor != null)
            dimRun.Foreground = dimensionColor;
        else
            dimRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
        tb.Inlines.Add(dimRun);

        var sepRun = new Run("  \u00B7  ");
        sepRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
        tb.Inlines.Add(sepRun);

        var sizeRun = new Run(FormatSize(size));
        if (sizeColor != null)
            sizeRun.Foreground = sizeColor;
        else
            sizeRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorPrimaryBrush");
        tb.Inlines.Add(sizeRun);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    // ── Button handlers ─────────────────────────────────────────

    private async void BtnForce_Click(object sender, RoutedEventArgs e)
    {
        if (_isOptimizeMode)
        {
            await RunOptimizationAndMorphAsync(allMode: false);
            return;
        }
        Choice = UploadConflictChoice.Force;
        Close();
    }

    private async void BtnForceAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isOptimizeMode)
        {
            await RunOptimizationAndMorphAsync(allMode: true);
            return;
        }
        Choice = UploadConflictChoice.ForceAll;
        Close();
    }

    private void BtnForceAllOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_isOptimizeMode)
        {
            _ = RunOptimizationAndMorphAsync(allMode: true);
            return;
        }
        Choice = UploadConflictChoice.ForceAllOptimize;
        Close();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        Choice = UploadConflictChoice.Skip;
        Close();
    }

    private void BtnSkipAll_Click(object sender, RoutedEventArgs e)
    {
        Choice = UploadConflictChoice.SkipAll;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = UploadConflictChoice.Cancel;
        Close();
    }

    // ── Wizard: optimization → morph ────────────────────────────

    private async Task RunOptimizationAndMorphAsync(bool allMode)
    {
        if (_optimizeFunc == null || _localFilePath == null) return;

        // Disable buttons, show progress
        btnConfirm.IsEnabled = false;
        btnConfirmAll.IsEnabled = false;
        txtMessage.Text = $"Optimizing {_filename}...";
        txtNewLabel.Text = "Optimizing...";

        try
        {
            var data = await File.ReadAllBytesAsync(_localFilePath);
            var result = await _optimizeFunc(data);

            // Add optimization marker + save
            var marked = Views.OptimizationWindow.InsertOptMarker(result);
            await File.WriteAllBytesAsync(_localFilePath, marked);

            // Reload preview with optimized image
            ReloadLocalImage();
            txtNewLabel.Text = "Optimized";

            _isOptimizeMode = false;

            if (allMode)
            {
                // "Optimize All" → signal caller to auto-optimize remaining files
                Choice = UploadConflictChoice.ForceAllOptimize;
                Close();
                return;
            }

            // Check wiki existence
            bool existsOnWiki = await CheckWikiExistenceAsync(_filename);

            if (existsOnWiki)
            {
                // Morph to conflict mode (dual preview)
                txtRemaining.Text = "";
                MorphToConflictMode();
            }
            else
            {
                // Morph to confirm mode (single preview, new file)
                txtRemaining.Text = "";
                MorphToConfirmMode();
            }
        }
        catch (Exception ex)
        {
            txtMessage.Text = $"Optimization failed: {ex.Message}";
            btnConfirm.IsEnabled = true;
            btnConfirmAll.IsEnabled = true;
        }
    }

    private static async Task<bool> CheckWikiExistenceAsync(string filename)
    {
        try
        {
            var result = await Services.MysteryWikiService.CheckPagesExistAsync(
                new[] { $"File:{filename}" });
            return result.GetValueOrDefault($"File:{filename}", false)
                || result.GetValueOrDefault($"File:{filename.Replace('_', ' ')}", false);
        }
        catch { return false; }
    }
}
