using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// ImageOptimiserPage — clipboard domain: clipboard polling monitor (file drops and
/// bitmaps), the "Add" notification flow, and Ctrl+V paste handling.
/// </summary>
public partial class ImageOptimiserPage
{
    // ── Clipboard monitoring ──
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private DispatcherTimer? _clipboardTimer;
    private uint _lastClipboardSeq;

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
}
