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
            txtServerDetected.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
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
            txtServerDetected.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
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
        txtServerDetected.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
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
            SpriteMetadataService.InvalidateCache();
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
            txtExtractDetected.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
            txtExtractAutoPath.Text = $"Output: {Path.Combine(_detectedVersionDir, "Export - PNGs")}";
            btnExtractAssets.IsEnabled = true;
        }
        else if (string.IsNullOrWhiteSpace(_main.Settings.ImageExporterBasePath)
                 || !Directory.Exists(_main.Settings.ImageExporterBasePath))
        {
            txtExtractDetected.Text = "No workspace folder configured.";
            txtExtractDetected.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            panelNoExtractApk.Visibility = Visibility.Visible;
            txtExtractAutoPath.Text = "";
        }
        else
        {
            var version = _main.Settings.SelectedApkVersion;
            txtExtractDetected.Text = string.IsNullOrEmpty(version)
                ? "No APK/XAPK found in workspace."
                : $"No APK/XAPK found for version {version}.";
            txtExtractDetected.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
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
            SpriteMetadataService.InvalidateCache();
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

    // ── Minigame Icons ─────────────────────────────────────────────
    // Extracts minigame task icons (Dollhouse/Painting/Perfumery/Card/Book/SpyNotes,
    // plus any future themes) from Unity bundles. Themes are enumerated dynamically
    // from SharedGameConfig (see MinigameIconExtractor.DiscoverThemesFromConfig).
    private async void BtnExtractMinigameIcons_Click(object sender, RoutedEventArgs e)
    {
        if (_detectedVersionDir == null)
        {
            ShowMinigameIconsInfo("No game version detected. Select one in Settings first.", InfoBarSeverity.Error);
            return;
        }

        // Game Files path has two subfolders: APK/ (local APK bundles) and Server/ (addressables).
        var gameFilesRoot = Path.Combine(_detectedVersionDir, "Game Files");
        if (!Directory.Exists(Path.Combine(gameFilesRoot, "APK")) && !Directory.Exists(Path.Combine(gameFilesRoot, "Server")))
        {
            ShowMinigameIconsInfo($"Expected APK/ or Server/ under {gameFilesRoot}. Run 'Extract Images' first to populate bundles.", InfoBarSeverity.Error);
            return;
        }

        // Icons go straight into the shared "Processed Images" folder alongside other
        // wiki-ready PNGs (naming `Minigame {Theme}01.png` matches the wiki upload name).
        var workspace = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
            workspace = _detectedVersionDir;
        var outputDir = Path.Combine(workspace, "Processed Images");

        btnExtractMinigameIcons.IsEnabled = false;
        minigameIconsInfoBar.IsOpen = false;
        txtMinigameIconsProgress.Text = "Preparing...";

        try
        {
            var tpkPath = await AssetExtractionService.EnsureTpkAsync(
                workspace,
                status => Dispatcher.Invoke(() => txtMinigameIconsProgress.Text = status),
                default);

            // Dynamically discover themes + their HotspotType from SharedGameConfig. Falls back to
            // known set if config is not loaded in this session.
            var themeTypes = DiscoverThemeTypesFromMain();
            if (themeTypes.Count == 0)
            {
                themeTypes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Dollhouse"] = "IllustrationTask",
                    ["Painting"] = "IllustrationTask",
                    ["Perfumery"] = "IllustrationTask",
                    ["Card"] = "CardStack",
                    ["Book"] = "CardStack",
                    ["SpyNotes"] = "CardStack",
                };
                ShowMinigameIconsInfo("No config loaded — using known 26.03.01 theme set as fallback.", InfoBarSeverity.Warning);
            }
            var themes = themeTypes.Keys.ToList();

            txtMinigameIconsProgress.Text = $"Extracting {themes.Count} themes...";

            var result = await MinigameIconExtractor.ExtractAsync(
                gameFilesRoot, outputDir, tpkPath, themes,
                new Progress<string>(s => Dispatcher.Invoke(() => txtMinigameIconsProgress.Text = s)),
                themeTypes: themeTypes
            );

            txtMinigameIconsProgress.Text = "";
            var msg = $"Extracted {result.Extracted}/{themes.Count} icons → {outputDir}";
            if (result.Missing > 0)
                msg += $"\n{result.Missing} theme(s) missing.";

            // Show per-theme pick info so user can spot wrong-variant picks (e.g. white mask
            // instead of colored MapSpot icon) and cross-check against the atlas.
            var picks = result.Warnings.Where(w => w.StartsWith("[PICKED]")).ToList();
            if (picks.Count > 0)
                msg += "\n\n" + string.Join("\n", picks);
            var others = result.Warnings.Where(w => !w.StartsWith("[PICKED]")).ToList();
            if (others.Count > 0)
                msg += "\n\n" + string.Join("\n", others);

            txtMinigameIconsPath.Text = outputDir;

            var severity = result.Missing > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            ShowMinigameIconsInfo(msg, severity);
        }
        catch (Exception ex)
        {
            txtMinigameIconsProgress.Text = "";
            ShowMinigameIconsInfo($"Extraction failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnExtractMinigameIcons.IsEnabled = true;
        }
    }

    // ── Minigame Icons — Optimize via TinyPNG ─────────────────────────
    private void BtnOptimizeMinigameIcons_Click(object sender, RoutedEventArgs e)
    {
        var files = EnumerateMinigameIconFiles();
        if (files.Count == 0)
        {
            ShowMinigameIconsInfo("No minigame icon files found. Run Extract first.", InfoBarSeverity.Warning);
            return;
        }

        var apiKey = _main.Settings.TinifyApiKey;
        var apiKey2 = _main.Settings.TinifyApiKey2;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowMinigameIconsInfo("TinyPNG API key not set. Go to Settings.", InfoBarSeverity.Warning);
            return;
        }

        // Pre-check the UNOPTIMIZED files — they're what the user actually needs to run.
        var preChecked = new HashSet<string>(files.Where(f =>
        {
            try { return !OptimizationWindow.HasOptMarker(File.ReadAllBytes(f)); }
            catch { return true; }
        }), StringComparer.OrdinalIgnoreCase);

        var optWin = new OptimizationWindow(files, apiKey, apiKey2, preChecked)
        {
            Owner = Window.GetWindow(this)
        };
        optWin.ShowDialog();
        ShowMinigameIconsInfo($"Optimization window closed. Ready for upload.", InfoBarSeverity.Success);
    }

    // ── Minigame Icons — Upload to Fandom wiki ────────────────────────
    private async void BtnUploadMinigameIcons_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.Settings.WikiVerified)
        {
            ShowMinigameIconsInfo("Wiki account not verified. Go to Settings to log in.", InfoBarSeverity.Warning);
            return;
        }

        var files = EnumerateMinigameIconFiles();
        if (files.Count == 0)
        {
            ShowMinigameIconsInfo("No minigame icon files found. Run Extract first.", InfoBarSeverity.Warning);
            return;
        }

        // Block upload if ANY icon is not yet TinyPNG-optimized. The marker is inserted by
        // OptimizationWindow after compression — its absence means raw output from extractor.
        var unoptimized = files.Where(f =>
        {
            try { return !OptimizationWindow.HasOptMarker(File.ReadAllBytes(f)); }
            catch { return true; }
        }).Select(Path.GetFileName).ToList();
        if (unoptimized.Count > 0)
        {
            ShowMinigameIconsInfo(
                $"Upload blocked: {unoptimized.Count} icon(s) not optimized. Run 'Optimize' first.\n\n" +
                string.Join("\n", unoptimized),
                InfoBarSeverity.Warning);
            return;
        }

        btnUploadMinigameIcons.IsEnabled = false;
        minigameIconsInfoBar.IsOpen = false;
        txtMinigameIconsProgress.Text = "Authenticating...";

        int ok = 0, failed = 0;
        var errors = new List<string>();
        try
        {
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                _main.Settings.WikiUsername!, _main.Settings.WikiPassword!);
            var csrf = await WikiMappingService.GetCsrfTokenAsync(client);

            for (int i = 0; i < files.Count; i++)
            {
                var path = files[i];
                var wikiName = Path.GetFileName(path);
                txtMinigameIconsProgress.Text = $"Uploading {i + 1}/{files.Count}: {wikiName}...";
                try
                {
                    var result = await WikiMappingService.UploadFileAsync(
                        client, csrf, wikiName,
                        await File.ReadAllBytesAsync(path),
                        description: "{{Permission}}", ignoreWarnings: true);
                    if (result == "Success" || result == "Warning") ok++;
                    else { failed++; errors.Add($"{wikiName}: {result}"); }
                }
                catch (Exception ex)
                {
                    failed++;
                    // Treat "exact duplicate" as success — the file is already up to date.
                    if (ex.Message.Contains("exact duplicate", StringComparison.OrdinalIgnoreCase))
                    {
                        failed--;
                        ok++;
                    }
                    else
                    {
                        errors.Add($"{wikiName}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowMinigameIconsInfo($"Upload failed: {ex.Message}", InfoBarSeverity.Error);
            return;
        }
        finally
        {
            txtMinigameIconsProgress.Text = "";
            btnUploadMinigameIcons.IsEnabled = true;
        }

        var msg = $"Uploaded {ok}/{files.Count} icons.";
        if (failed > 0) msg += $"\n{failed} failed:\n" + string.Join("\n", errors);
        ShowMinigameIconsInfo(msg, failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private List<string> EnumerateMinigameIconFiles()
    {
        var workspace = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
            workspace = _detectedVersionDir;
        if (string.IsNullOrEmpty(workspace)) return new();
        var dir = Path.Combine(workspace, "Processed Images");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "Minigame*01.png")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> DiscoverThemesFromMain()
    {
        return DiscoverThemeTypesFromMain().Keys.OrderBy(s => s).ToList();
    }

    private Dictionary<string, string> DiscoverThemeTypesFromMain()
    {
        // DataService can hold a parsed SharedGameConfig reference when the app has run a dump.
        // If not available, fall through to empty dict (caller uses hardcoded fallback).
        try
        {
            var configProp = typeof(MainWindow).GetProperty("SharedGameConfig");
            var config = configProp?.GetValue(_main);
            if (config != null)
                return MinigameIconExtractor.DiscoverThemeTypesFromConfig(config);
        }
        catch { }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void ShowMinigameIconsInfo(string message, InfoBarSeverity severity)
    {
        minigameIconsInfoBar.Message = message;
        minigameIconsInfoBar.Severity = severity;
        minigameIconsInfoBar.IsOpen = true;
    }
}
