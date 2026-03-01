using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks.Dataflow;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class ImageExtractorPage : UserControl
{
    private readonly MainWindow _main;
    private static readonly HttpClient _http = new();

    private string? _detectedApkPath;
    private string? _detectedVersionDir;
    private string? _detectedVersion;

    private CancellationTokenSource? _serverCts;
    private CancellationTokenSource? _extractCts;

    public ImageExtractorPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        DetectVersionFolder();
        DetectServerState();
        DetectExtractApkState();
        _main.ApkVersionChanged += OnApkVersionChanged;
    }

    private void OnApkVersionChanged()
    {
        DetectVersionFolder();
        DetectServerState();
        DetectExtractApkState();
    }

    private void BtnGoToApkSettings_Click(object sender, RoutedEventArgs e)
    {
        _main.NavigateToSettingsHighlightApk();
    }

    private void BtnOpenExportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_detectedVersionDir == null) return;
        var exportDir = Path.Combine(_detectedVersionDir, "Export - PNGs");
        if (Directory.Exists(exportDir))
            Process.Start(new ProcessStartInfo(exportDir) { UseShellExecute = true });
    }

    // ── Version folder detection ──────────────────────────────────────

    private void DetectVersionFolder()
    {
        _detectedApkPath = null;
        _detectedVersionDir = null;
        _detectedVersion = null;

        var basePath = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            return;

        var savedVersion = _main.Settings.SelectedApkVersion;
        string? versionDir = null;

        if (!string.IsNullOrEmpty(savedVersion))
        {
            var specificDir = Path.Combine(basePath, savedVersion);
            if (Directory.Exists(specificDir))
                versionDir = specificDir;
        }
        else
        {
            versionDir = Directory.GetDirectories(basePath)
                .OrderByDescending(d => Path.GetFileName(d))
                .FirstOrDefault();
        }

        if (versionDir == null)
            return;

        _detectedVersionDir = versionDir;
        _detectedVersion = Path.GetFileName(versionDir);
        _detectedApkPath = CatalogParserService.FindApkInFolder(versionDir);

        // Show "Open Export Folder" if it already exists
        var exportDir = Path.Combine(versionDir, "Export - PNGs");
        btnOpenExportFolder.Visibility = Directory.Exists(exportDir)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Extract Assets from Server ────────────────────────────────────

    private void DetectServerState()
    {
        panelNoServerApk.Visibility = Visibility.Collapsed;
        btnServerExtract.IsEnabled = false;
        txtServerAutoPath.Text = "";

        if (_detectedVersionDir == null)
        {
            if (string.IsNullOrWhiteSpace(_main.Settings.ImageExporterBasePath)
                || !Directory.Exists(_main.Settings.ImageExporterBasePath))
                txtServerDetected.Text = "No workspace folder configured.";
            else
                txtServerDetected.Text = "No version folder found.";
            txtServerDetected.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
            panelNoServerApk.Visibility = Visibility.Visible;
            return;
        }

        var catalogPath = Path.Combine(_detectedVersionDir, "catalog.bin");
        var hasCatalog = File.Exists(catalogPath);
        var apkPath = CatalogParserService.FindApkInFolder(_detectedVersionDir);

        if (!hasCatalog && apkPath == null)
        {
            var version = _main.Settings.SelectedApkVersion;
            txtServerDetected.Text = string.IsNullOrEmpty(version)
                ? "No APK or catalog.bin found."
                : $"No APK or catalog.bin found for version {version}.";
            txtServerDetected.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
            panelNoServerApk.Visibility = Visibility.Visible;
            return;
        }

        var source = hasCatalog ? "catalog.bin" : "APK";
        var status = $"{source} found (v{_detectedVersion}). Will download bundles from server and extract images.";

        // Show cached bundle count
        var downloadDir = Path.Combine(_detectedVersionDir, "Game Files", "Server");
        if (Directory.Exists(downloadDir))
        {
            var cachedCount = Directory.GetFiles(downloadDir).Length;
            if (cachedCount > 0)
                status += $" ({cachedCount:N0} bundles cached)";
        }

        txtServerDetected.Text = status;
        txtServerDetected.Foreground = (Brush)FindResource("SystemFillColorSuccessBrush");
        txtServerAutoPath.Text = $"Output: {Path.Combine(_detectedVersionDir, "Export - PNGs")}";
        btnServerExtract.IsEnabled = true;
    }

    private void BtnCancelServer_Click(object sender, RoutedEventArgs e)
    {
        _serverCts?.Cancel();
    }

    private async void BtnServerExtract_Click(object sender, RoutedEventArgs e)
    {
        if (_detectedVersionDir == null)
        {
            ShowServerInfo("No version folder found.", InfoBarSeverity.Error);
            return;
        }

        var versionDir = _detectedVersionDir;
        var outputDir = Path.Combine(versionDir, "Export - PNGs");

        _serverCts = new CancellationTokenSource();
        var ct = _serverCts.Token;

        btnServerExtract.IsEnabled = false;
        btnCancelServer.Visibility = Visibility.Visible;
        serverInfoBar.IsOpen = false;

        int dlDownloaded = 0, dlCached = 0, dlErrors = 0;

        try
        {
            // ── Phase 1: Ensure catalog.bin ──
            var catalogPath = Path.Combine(versionDir, "catalog.bin");
            if (!File.Exists(catalogPath))
            {
                var apkPath = CatalogParserService.FindApkInFolder(versionDir);
                if (apkPath == null)
                {
                    ShowServerInfo("No APK or catalog.bin found.", InfoBarSeverity.Error);
                    return;
                }
                txtServerProgress.Text = "Extracting catalog.bin from APK...";
                catalogPath = await Task.Run(() =>
                    CatalogParserService.ExtractCatalogFromApk(apkPath, versionDir), ct);
            }

            // ── Phase 2: Parse URLs ──
            txtServerProgress.Text = "Parsing catalog URLs...";
            var catalogResult = await Task.Run(() => CatalogParserService.ExtractUrls(catalogPath), ct);

            if (catalogResult.Urls.Count == 0)
            {
                ShowServerInfo("No download URLs found in catalog.", InfoBarSeverity.Warning);
                return;
            }

            // ── Phase 3: Download bundles (with skip logic) ──
            var downloadDir = Path.Combine(versionDir, "Game Files", "Server");
            Directory.CreateDirectory(downloadDir);

            // Determine which bundles are already cached
            var existingFiles = new HashSet<string>(
                Directory.GetFiles(downloadDir).Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase);

            var toDownload = new List<string>();
            foreach (var url in catalogResult.Urls)
            {
                var fileName = Path.GetFileName(url.Split('?')[0]);
                if (string.IsNullOrEmpty(fileName) || !existingFiles.Contains(fileName))
                    toDownload.Add(url);
                else
                    dlCached++;
            }

            if (toDownload.Count > 0)
            {
                var total = toDownload.Count;
                txtServerProgress.Text = $"Downloading bundles... 0% (0/{total}, {dlCached:N0} cached)";

                var block = new ActionBlock<string>(async url =>
                {
                    var fileName = Path.GetFileName(url.Split('?')[0]);
                    if (string.IsNullOrEmpty(fileName)) fileName = $"file_{Guid.NewGuid():N}";
                    try
                    {
                        var data = await _http.GetByteArrayAsync(url, ct);
                        await File.WriteAllBytesAsync(Path.Combine(downloadDir, fileName), data, ct);
                        var d = Interlocked.Increment(ref dlDownloaded);
                        var pct = (int)(d * 100.0 / total);
                        Dispatcher.Invoke(() =>
                            txtServerProgress.Text = $"Downloading bundles... {pct}% ({d}/{total}, {dlCached:N0} cached)");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { Interlocked.Increment(ref dlErrors); }
                }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct });

                foreach (var url in toDownload) block.Post(url);
                block.Complete();
                await block.Completion;

                if (dlErrors > 0)
                    txtServerProgress.Text = $"Downloaded {dlDownloaded} bundles ({dlErrors} errors, {dlCached:N0} cached). Extracting textures...";
                else
                    txtServerProgress.Text = $"Downloaded {dlDownloaded} bundles ({dlCached:N0} cached). Extracting textures...";
            }
            else
            {
                txtServerProgress.Text = $"All {dlCached:N0} bundles cached. Extracting textures...";
            }

            // ── Phase 4: Ensure TPK ──
            var workspace = _main.Settings.ImageExporterBasePath;
            if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
                workspace = versionDir;

            var tpkPath = await AssetExtractionService.EnsureTpkAsync(
                workspace,
                status => Dispatcher.Invoke(() => txtServerProgress.Text = status),
                ct);

            // ── Phase 5: Extract textures ──
            Directory.CreateDirectory(outputDir);

            var result = await AssetExtractionService.ExtractAllTexturesAsync(
                downloadDir,
                tpkPath,
                outputDir,
                (bundleName, current, total, textures) =>
                    Dispatcher.Invoke(() =>
                    {
                        var pct = (int)(current * 100.0 / total);
                        txtServerProgress.Text = $"Extracting: {pct}% ({current}/{total} bundles, {textures:N0} textures) — {bundleName}";
                    }),
                ct);

            // ── Done ──
            txtServerProgress.Text = "";
            var msg = $"Done! {result.ExtractedTextures:N0} textures from {result.ProcessedBundles} bundles.";
            if (result.SkippedDuplicates > 0)
                msg += $" {result.SkippedDuplicates:N0} duplicate(s) skipped.";
            if (dlCached > 0)
                msg += $" {dlCached:N0} cached (skipped download).";
            msg += $"\n→ {outputDir}";
            if (dlErrors > 0)
                msg += $"\n{dlErrors} download error(s).";
            if (result.FailedBundles > 0)
                msg += $"\n{result.FailedBundles} bundle(s) had extraction errors.";

            var severity = (dlErrors > 0 || result.FailedBundles > 0) ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            ShowServerInfo(msg, severity);

            // Refresh detection (cached count + export folder may have changed)
            DetectServerState();
            btnOpenExportFolder.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            txtServerProgress.Text = "";
            ShowServerInfo("Cancelled. Partial results may have been saved.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            txtServerProgress.Text = "";
            ShowServerInfo($"Failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnServerExtract.IsEnabled = true;
            btnCancelServer.Visibility = Visibility.Collapsed;
            _serverCts = null;
        }
    }

    private void ShowServerInfo(string message, InfoBarSeverity severity)
    {
        serverInfoBar.Message = message;
        serverInfoBar.Severity = severity;
        serverInfoBar.IsOpen = true;
    }

    // ── Extract Assets from APK ───────────────────────────────────────

    private void DetectExtractApkState()
    {
        panelNoExtractApk.Visibility = Visibility.Collapsed;
        btnExtractAssets.IsEnabled = false;

        var apkPath = _detectedApkPath;
        if (apkPath == null && _detectedVersionDir != null)
            apkPath = CatalogParserService.FindApkInFolder(_detectedVersionDir);

        if (apkPath != null && _detectedVersionDir != null)
        {
            _detectedApkPath = apkPath;
            txtExtractDetected.Text = $"APK auto-detected (v{_detectedVersion}).";
            txtExtractDetected.Foreground = (Brush)FindResource("SystemFillColorSuccessBrush");
            txtExtractAutoPath.Text = $"Output: {Path.Combine(_detectedVersionDir, "Export - PNGs")}";
            btnExtractAssets.IsEnabled = true;
        }
        else if (string.IsNullOrWhiteSpace(_main.Settings.ImageExporterBasePath)
                 || !Directory.Exists(_main.Settings.ImageExporterBasePath))
        {
            txtExtractDetected.Text = "No workspace folder configured.";
            txtExtractDetected.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
            panelNoExtractApk.Visibility = Visibility.Visible;
            txtExtractAutoPath.Text = "";
        }
        else
        {
            var version = _main.Settings.SelectedApkVersion;
            txtExtractDetected.Text = string.IsNullOrEmpty(version)
                ? "No APK/XAPK found in workspace."
                : $"No APK/XAPK found for version {version}.";
            txtExtractDetected.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
            panelNoExtractApk.Visibility = Visibility.Visible;
            txtExtractAutoPath.Text = "";
        }
    }

    private void BtnCancelExtract_Click(object sender, RoutedEventArgs e)
    {
        _extractCts?.Cancel();
    }

    private async void BtnExtractAssets_Click(object sender, RoutedEventArgs e)
    {
        if (_detectedApkPath == null || _detectedVersionDir == null)
        {
            ShowExtractInfo("No APK found. Select a game version in Settings.", InfoBarSeverity.Error);
            return;
        }

        var apkPath = _detectedApkPath;
        var versionDir = _detectedVersionDir;
        var outputDir = Path.Combine(versionDir, "Export - PNGs");

        // Persistent bundle dir (not temp)
        var bundleDir = Path.Combine(versionDir, "Game Files", "APK");

        var workspace = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
            workspace = versionDir;

        _extractCts = new CancellationTokenSource();
        var ct = _extractCts.Token;

        btnExtractAssets.IsEnabled = false;
        btnCancelExtract.Visibility = Visibility.Visible;
        extractInfoBar.IsOpen = false;
        txtExtractProgress.Text = "Preparing...";

        try
        {
            // 1. Ensure TPK
            var tpkPath = await AssetExtractionService.EnsureTpkAsync(
                workspace,
                status => Dispatcher.Invoke(() => txtExtractProgress.Text = status),
                ct);

            // 2. Extract bundles to persistent dir
            Directory.CreateDirectory(bundleDir);
            var includeBuiltIn = _main.Settings.ExtractIncludeBuiltIn;
            var (_, bundleCount) = await AssetExtractionService.ExtractBundlesFromApkAsync(
                apkPath,
                bundleDir,
                includeBuiltIn,
                status => Dispatcher.Invoke(() => txtExtractProgress.Text = status),
                ct);

            if (bundleCount == 0)
            {
                ShowExtractInfo("No asset bundles found in the APK.", InfoBarSeverity.Warning);
                return;
            }

            // 3. Extract textures
            Directory.CreateDirectory(outputDir);

            var result = await AssetExtractionService.ExtractAllTexturesAsync(
                bundleDir,
                tpkPath,
                outputDir,
                (bundleName, current, total, textures) =>
                    Dispatcher.Invoke(() =>
                    {
                        var pct = (int)(current * 100.0 / total);
                        txtExtractProgress.Text = $"{pct}% ({current}/{total} bundles, {textures:N0} textures) — {bundleName}";
                    }),
                ct);

            // 4. Show results
            txtExtractProgress.Text = "";
            var msg = $"Done! {result.ExtractedTextures:N0} textures extracted from {result.ProcessedBundles} bundles.";
            if (result.SkippedDuplicates > 0)
                msg += $" {result.SkippedDuplicates:N0} duplicate(s) skipped.";
            msg += $"\n→ {outputDir}";
            if (result.FailedBundles > 0)
                msg += $"\n{result.FailedBundles} bundle(s) had errors.";

            var severity = result.FailedBundles > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            ShowExtractInfo(msg, severity);
            btnOpenExportFolder.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            txtExtractProgress.Text = "";
            ShowExtractInfo("Extraction cancelled. Partial results may have been saved.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            txtExtractProgress.Text = "";
            ShowExtractInfo($"Extraction failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnExtractAssets.IsEnabled = true;
            btnCancelExtract.Visibility = Visibility.Collapsed;
            _extractCts = null;
        }
    }

    private void ShowExtractInfo(string message, InfoBarSeverity severity)
    {
        extractInfoBar.Message = message;
        extractInfoBar.Severity = severity;
        extractInfoBar.IsOpen = true;
    }
}
