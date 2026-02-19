using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks.Dataflow;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class ImageExtractorPage : UserControl
{
    private readonly MainWindow _main;
    private static readonly HttpClient _http = new();

    public ImageExtractorPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        UpdateExpAutoPathLabel();
    }

    // ── Drag & Drop ─────────────────────────────────────────────────

    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileApproachDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        if (path != null && File.Exists(path))
            SetFileApproach(path);
    }

    private void DlCustomOutputDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        if (path != null && Directory.Exists(path))
            txtDlCustomOutput.Text = path;
    }

    private void ExpSourceDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        if (path != null && Directory.Exists(path))
            SetExpSource(path);
    }

    private void ExpCustomOutputDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        if (path != null && Directory.Exists(path))
            txtExpCustomOutput.Text = path;
    }

    private static string? GetDroppedPath(DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        return files?.Length > 0 ? files[0] : null;
    }

    // ── Browse ───────────────────────────────────────────────────────

    private void BrowseFileApproach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select .txt URL list",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };
        var basePath = _main.Settings.ImageExporterBasePath;
        if (!string.IsNullOrEmpty(basePath) && Directory.Exists(basePath))
            dlg.InitialDirectory = basePath;
        if (dlg.ShowDialog() == true) SetFileApproach(dlg.FileName);
    }

    private void BrowseDlCustomOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFolder("Select custom download output folder", null);
        if (path != null) txtDlCustomOutput.Text = path;
    }

    private void BrowseExpSource_Click(object sender, RoutedEventArgs e)
    {
        var startPath = string.IsNullOrEmpty(_main.Settings.ImageExporterBasePath)
            ? null : _main.Settings.ImageExporterBasePath;
        var path = BrowseFolder("Select source folder with PNG files", startPath);
        if (path != null) SetExpSource(path);
    }

    private void BrowseExpCustomOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFolder("Select custom export output folder", null);
        if (path != null) txtExpCustomOutput.Text = path;
    }

    // ── Path setters ─────────────────────────────────────────────────

    private void SetFileApproach(string filePath)
    {
        txtFileApproach.Text = filePath;
        UpdateDlAutoPath(filePath);
    }

    private void SetExpSource(string folderPath)
    {
        txtExpSource.Text = folderPath;
        UpdateExpAutoPathLabel();
    }

    private void UpdateDlAutoPath(string? txtFilePath = null)
    {
        txtFilePath ??= txtFileApproach?.Text?.Trim();
        if (!string.IsNullOrEmpty(txtFilePath) && File.Exists(txtFilePath))
            txtDlAutoPath.Text = ComputeDownloadDir(txtFilePath);
        else
            txtDlAutoPath.Text = "";
    }

    private void UpdateExpAutoPathLabel()
    {
        var source = NormalizeDir(txtExpSource?.Text);
        // Note: XAML Run after this one already appends ' - PNGs"', so we only set the folder name
        runExpAutoPath.Text = !string.IsNullOrEmpty(source)
            ? Path.GetFileName(source)
            : "{source}";
    }

    // ── Output mode toggles ──────────────────────────────────────────

    private void DlOutputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (dlCustomOutputRow == null) return;
        dlCustomOutputRow.Visibility = rbDlCustom.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExpOutputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (expCustomOutputRow == null) return;
        expCustomOutputRow.Visibility = rbExpCustom.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Bundle Downloader ────────────────────────────────────────────

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        var txtPath = txtFileApproach?.Text?.Trim();
        if (string.IsNullOrEmpty(txtPath) || !File.Exists(txtPath))
        {
            ShowDlInfo("Select a .txt URL list file first.", InfoBarSeverity.Error);
            return;
        }

        var urls = File.ReadAllLines(txtPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .ToList();

        if (urls.Count == 0) { ShowDlInfo("No URLs found in the file.", InfoBarSeverity.Warning); return; }

        var outDir = rbDlCustom.IsChecked == true && !string.IsNullOrEmpty(txtDlCustomOutput.Text)
            ? txtDlCustomOutput.Text.Trim()
            : ComputeDownloadDir(txtPath);

        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { ShowDlInfo($"Cannot create output folder: {ex.Message}", InfoBarSeverity.Error); return; }

        btnDownload.IsEnabled = false;
        txtDlProgress.Text = $"Starting — {urls.Count} URLs...";
        dlInfoBar.IsOpen = false;

        int done = 0, errors = 0;
        var progress = new Progress<string>(msg => txtDlProgress.Text = msg);

        var block = new ActionBlock<string>(async url =>
        {
            var fileName = Path.GetFileName(url.Split('?')[0]);
            if (string.IsNullOrEmpty(fileName)) fileName = $"file_{Guid.NewGuid():N}";
            try
            {
                var data = await _http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(Path.Combine(outDir, fileName), data);
                var d = Interlocked.Increment(ref done);
                ((IProgress<string>)progress).Report($"Downloaded {d} / {urls.Count}...");
            }
            catch { Interlocked.Increment(ref errors); }
        }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 4 });

        foreach (var url in urls) block.Post(url);
        block.Complete();
        await block.Completion;

        btnDownload.IsEnabled = true;
        txtDlProgress.Text = "";

        ShowDlInfo(
            errors > 0
                ? $"Done! {done} downloaded, {errors} error(s).\n→ {outDir}"
                : $"Done! {done} file(s) downloaded.\n→ {outDir}",
            errors > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private static string ComputeDownloadDir(string txtFilePath)
    {
        var parentDir = Path.GetDirectoryName(txtFilePath) ?? "";
        var folderName = Path.GetFileName(parentDir);
        return Path.Combine(parentDir, $"Downloaded_bundles_{folderName}");
    }

    // ── Image Exporter ───────────────────────────────────────────────

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var sourceDir = NormalizeDir(txtExpSource.Text);
        if (!Directory.Exists(sourceDir))
        {
            ShowExpInfo("Select a valid source folder first.", InfoBarSeverity.Error);
            return;
        }

        var outDir = GetExportOutputDir(sourceDir);
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { ShowExpInfo($"Cannot create output folder: {ex.Message}", InfoBarSeverity.Error); return; }

        btnExport.IsEnabled = false;
        txtExpProgress.Text = "Scanning for PNG files...";
        expInfoBar.IsOpen = false;

        int done = 0, errors = 0;
        var progress = new Progress<string>(msg => txtExpProgress.Text = msg);

        await Task.Run(() =>
        {
            var allPngs = Directory.GetFiles(sourceDir, "*.png", SearchOption.AllDirectories);

            // Pre-scan duplicates
            var byName = allPngs
                .GroupBy(p => Path.GetFileName(p)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            ((IProgress<string>)progress).Report($"Found {allPngs.Length} PNG files, copying...");

            foreach (var png in allPngs)
            {
                try
                {
                    var fileName = Path.GetFileName(png);
                    var isDuplicate = byName[fileName].Count > 1;

                    string targetName;
                    if (isDuplicate)
                    {
                        var suffix = GetDuplicateSuffix(png, sourceDir);
                        var baseName = Path.GetFileNameWithoutExtension(fileName);
                        targetName = string.IsNullOrEmpty(suffix) ? fileName : $"{baseName}_{suffix}.png";
                    }
                    else
                    {
                        targetName = fileName;
                    }

                    // Avoid collision in output
                    var targetPath = Path.Combine(outDir, targetName);
                    int counter = 1;
                    while (File.Exists(targetPath))
                    {
                        counter++;
                        targetPath = Path.Combine(outDir,
                            $"{Path.GetFileNameWithoutExtension(targetName)}_{counter}.png");
                    }

                    File.Copy(png, targetPath, overwrite: false);
                    var d = Interlocked.Increment(ref done);
                    ((IProgress<string>)progress).Report($"Copied {d} / {allPngs.Length}...");
                }
                catch { Interlocked.Increment(ref errors); }
            }
        });

        btnExport.IsEnabled = true;
        txtExpProgress.Text = "";

        ShowExpInfo(
            errors > 0
                ? $"Done! {done} PNGs copied, {errors} error(s).\n→ {outDir}"
                : $"Done! {done} PNG(s) copied.\n→ {outDir}",
            errors > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private string GetExportOutputDir(string sourceDir)
    {
        if (rbExpCustom.IsChecked == true && !string.IsNullOrEmpty(txtExpCustomOutput.Text))
            return txtExpCustomOutput.Text.Trim();

        var sourceName = Path.GetFileName(sourceDir);
        var parent = Path.GetDirectoryName(sourceDir) ?? sourceDir;
        return Path.Combine(parent, $"{sourceName} - PNGs");
    }

    /// <summary>
    /// Walks up from filePath toward sourceRoot, returns the name of the deepest
    /// ancestor folder whose name contains '_'. Used to create a suffix for duplicate filenames.
    /// </summary>
    private static string GetDuplicateSuffix(string filePath, string sourceRoot)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (dir != null && !dir.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (name?.Contains('_') == true) return name;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetFileName(Path.GetDirectoryName(filePath) ?? "") ?? "";
    }

    // ── InfoBar ──────────────────────────────────────────────────────

    private void ShowDlInfo(string message, InfoBarSeverity severity)
    {
        dlInfoBar.Message = message;
        dlInfoBar.Severity = severity;
        dlInfoBar.IsOpen = true;
    }

    private void ShowExpInfo(string message, InfoBarSeverity severity)
    {
        expInfoBar.Message = message;
        expInfoBar.Severity = severity;
        expInfoBar.IsOpen = true;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Trims trailing directory separators so Path.GetFileName works correctly.</summary>
    private static string NormalizeDir(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? BrowseFolder(string description, string? initialDir)
    {
        var dlg = new OpenFolderDialog { Title = description };
        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
            dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}
