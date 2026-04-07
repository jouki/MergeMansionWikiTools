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

public partial class MysteryDecorationUploadDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainWindow _main;
    private readonly MysteryEvent _mystery;
    private List<DetectedDecorationFile> _detectedFiles = new();
    private readonly Dictionary<DetectedDecorationFile, System.Windows.Controls.CheckBox> _checkboxes = new();
    private readonly HashSet<DetectedDecorationFile> _optimizedFiles = new();

    /// <summary>Callback to refresh the mystery list after image upload changes status.</summary>
    public Action? OnStatusChanged { get; set; }

    public MysteryDecorationUploadDialog(MainWindow main, MysteryEvent mystery)
    {
        _main = main;
        _mystery = mystery;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        txtTitle.Text = $"Images — {mystery.Name}";
        txtMysteryName.Text = mystery.Name;
        var dateStr = mystery.StartDate?.ToString("MMM d, yyyy") ?? "Unknown";
        var typeStr = mystery.MysteryType == MysteryType.Pet ? "Pet" : "Standard";
        txtMysteryDetail.Text = $"{typeStr} · {dateStr}";

        // Auto-scan on load
        Loaded += (_, _) => _ = AutoScanAsync();
    }

    private async Task AutoScanAsync()
    {
        // Resolve export path
        var basePath = _main.Settings.ImageExporterBasePath;
        var apkVersion = _main.Settings.SelectedApkVersion;
        var exportDir = MysteryWikiService.ResolveExportPngsDir(basePath, apkVersion);

        if (string.IsNullOrEmpty(exportDir))
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            pnlEmpty.Visibility = Visibility.Visible;
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(apkVersion))
                txtEmptyMessage.Text = "Export path not configured.\nSet Workspace and Game Version in Settings.";
            else
                txtEmptyMessage.Text = $"No exported images found for v{apkVersion}.\nRun Image Extractor to extract game assets,\nor select a version that has been exported.";
            return;
        }

        txtExportPath.Text = exportDir;

        try
        {
            _detectedFiles = await Task.Run(() =>
                MysteryWikiService.DetectDecorationFiles(
                    exportDir, _mystery.ProgressionEventId, _mystery.WikiImageName,
                    _mystery.MysteryType == MysteryType.Pet, _mystery));

            pnlLoading.Visibility = Visibility.Collapsed;

            if (_detectedFiles.Count == 0)
            {
                pnlEmpty.Visibility = Visibility.Visible;
                txtEmptyMessage.Text = $"No image files found for {_mystery.ProgressionEventId}\nin {exportDir}";
                return;
            }

            // Check wiki existence for uploadable files
            var uploadable = _detectedFiles.Where(f => f.Category != "EventItem").ToList();
            if (uploadable.Count > 0)
            {
                var wikiFilenames = uploadable.Select(f => $"File:{f.WikiFilename}").ToList();
                var existMap = await MysteryWikiService.CheckPagesExistAsync(wikiFilenames);
                foreach (var file in uploadable)
                    file.ExistsOnWiki = existMap.GetValueOrDefault($"File:{file.WikiFilename}", false);
            }

            // Mark files with pre-existing optimization marker as optimized
            foreach (var f in _detectedFiles)
                if (f.OptimizedSize.HasValue)
                    _optimizedFiles.Add(f);

            // Check if split event item files already exist in Processed Images
            await CheckExistingSplitEventItemAsync();

            BuildFileList();
            scrollFiles.Visibility = Visibility.Visible;
            btnOptimize.IsEnabled = true;
            btnUpload.IsEnabled = true;

            var uploadableCount = _detectedFiles.Count(f => f.Category != "EventItem");
            var preOptimized = _optimizedFiles.Count;
            var msg = $"Found {_detectedFiles.Count} files ({uploadableCount} uploadable)";
            if (preOptimized > 0)
                msg += $", {preOptimized} already optimized";
            ShowInfo(msg + ".", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            pnlEmpty.Visibility = Visibility.Visible;
            txtEmptyMessage.Text = $"Scan failed: {ex.Message}";
        }
    }

    private void BuildFileList()
    {
        pnlFiles.Children.Clear();

        // Save previous checked state before clearing
        var prevChecked = new Dictionary<DetectedDecorationFile, bool>();
        foreach (var (f, cb) in _checkboxes)
            prevChecked[f] = cb.IsChecked == true;
        _checkboxes.Clear();

        // Select All header — event wired AFTER all rows are built (below)
        var selectAllCb = new System.Windows.Controls.CheckBox
        {
            Content = "Select All",
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 12
        };
        selectAllCb.SetResourceReference(System.Windows.Controls.CheckBox.ForegroundProperty, "TextFillColorSecondaryBrush");
        pnlFiles.Children.Add(selectAllCb);

        // Check if we have split event items → add group header
        var splitItems = _detectedFiles.Where(f => f.Category.StartsWith("Event Item Lv")).ToList();
        bool splitGroupHeaderAdded = false;

        foreach (var file in _detectedFiles)
        {
            // Add split group header before first event item level
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
                    Text = $"Event Item ({splitItems.Count} files): {_mystery.EventItemName ?? "Unknown"}",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
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
                // Event Item — no checkbox, "Open in Image Optimiser" button instead
                // Empty column 0 for alignment
                var spacer = new Border { Width = 24 };
                Grid.SetColumn(spacer, 0);
                grid.Children.Add(spacer);
            }
            else
            {
                // Preserve previous checked state on rebuild, default to checked for new files
                bool wasChecked = prevChecked.TryGetValue(file, out var prev)
                    ? prev
                    : file.ExistsOnWiki != true;

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
                        Source = bi,
                        Width = 48, Height = 48,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        ToolTip = "Click to open image",
                        Tag = file.SourcePath
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
            catch { /* Skip thumbnail on error */ }

            // Info panel
            var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            bool isSplitItem = file.Category.StartsWith("Event Item Lv");
            var displayName = isEventItem
                ? Path.GetFileName(file.SourcePath)
                : isSplitItem
                    ? $"{file.Category.Replace("Event Item ", "")}  {file.WikiFilename}"
                    : file.WikiFilename;

            var fileNameText = new TextBlock
            {
                Text = displayName,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            fileNameText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            infoPanel.Children.Add(fileNameText);

            var sourceInfo = $"{file.Category} · {Path.GetFileName(file.SourcePath)}";
            if (_optimizedFiles.Contains(file) && file.OptimizedSize.HasValue)
                sourceInfo += $" · {file.OptimizedSize.Value / 1024.0:F1} KB";
            var sourceText = new TextBlock
            {
                Text = sourceInfo,
                FontSize = 11
            };
            sourceText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            infoPanel.Children.Add(sourceText);

            Grid.SetColumn(infoPanel, 2);
            grid.Children.Add(infoPanel);

            // Right side: status badge or action button
            if (isEventItem)
            {
                // "Open in Image Optimiser" button
                var btnOptimiser = new Wpf.Ui.Controls.Button
                {
                    Content = "Image Optimiser",
                    Appearance = ControlAppearance.Secondary,
                    Height = 32,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    Tag = file.SourcePath
                };
                btnOptimiser.Click += BtnOpenInOptimiser_Click;
                Grid.SetColumn(btnOptimiser, 3);
                grid.Children.Add(btnOptimiser);
            }
            else
            {
                var statusBadge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                var statusText = new TextBlock { FontSize = 11 };
                bool isOptimized = _optimizedFiles.Contains(file);
                if (isOptimized && file.ExistsOnWiki == true)
                {
                    // Both optimized and on wiki — use blue/accent tint
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

        // Wire Select All events AFTER all rows exist
        selectAllCb.Checked += (_, _) =>
        {
            foreach (var (f, cb) in _checkboxes)
                if (f.Category != "EventItem") cb.IsChecked = true;
        };
        selectAllCb.Unchecked += (_, _) =>
        {
            foreach (var (_, cb) in _checkboxes)
                cb.IsChecked = false;
        };

        // Auto-set Select All based on current state
        var uploadable = _checkboxes.Where(kv => kv.Key.Category != "EventItem").ToList();
        if (uploadable.Count > 0 && uploadable.All(kv => kv.Value.IsChecked == true))
            selectAllCb.IsChecked = true;
    }

    private async void BtnOptimize_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = _main.Settings.TinifyApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowInfo("TinyPNG API key not set. Go to Settings → Tools.", InfoBarSeverity.Warning);
            return;
        }

        var toOptimize = _detectedFiles.Where(f =>
            f.Category != "EventItem" && !_optimizedFiles.Contains(f) &&
            _checkboxes.TryGetValue(f, out var cb) && cb.IsChecked == true).ToList();

        if (toOptimize.Count == 0)
        {
            ShowInfo("No files to optimize.", InfoBarSeverity.Warning);
            return;
        }

        btnOptimize.IsEnabled = false;
        btnUpload.IsEnabled = false;
        int optimized = 0;

        ShowInfo($"Optimizing {toOptimize.Count} files via TinyPNG...", InfoBarSeverity.Informational);

        foreach (var file in toOptimize)
        {
            try
            {
                var data = await File.ReadAllBytesAsync(file.SourcePath);
                var originalSize = data.Length;

                TinifyAPI.Tinify.Key = apiKey;
                var result = await (await TinifyAPI.Tinify.FromBuffer(data)).ToBuffer();

                // Only save if compression is meaningful (>10% reduction)
                if (result.Length < originalSize * 0.9)
                {
                    // Add optimization marker + save
                    var marked = OptimizationWindow.InsertOptMarker(result);
                    await File.WriteAllBytesAsync(file.SourcePath, marked);
                    file.OptimizedSize = marked.Length;
                }
                else
                {
                    // Already optimal — add marker to original
                    if (!OptimizationWindow.HasOptMarker(data))
                    {
                        var marked = OptimizationWindow.InsertOptMarker(data);
                        await File.WriteAllBytesAsync(file.SourcePath, marked);
                    }
                    file.OptimizedSize = new FileInfo(file.SourcePath).Length;
                }

                _optimizedFiles.Add(file);
                optimized++;
            }
            catch (TinifyAPI.AccountException) when (!string.IsNullOrWhiteSpace(_main.Settings.TinifyApiKey2))
            {
                try
                {
                    var data = await File.ReadAllBytesAsync(file.SourcePath);
                    TinifyAPI.Tinify.Key = _main.Settings.TinifyApiKey2;
                    var result = await (await TinifyAPI.Tinify.FromBuffer(data)).ToBuffer();
                    await File.WriteAllBytesAsync(file.SourcePath, result);
                    file.OptimizedSize = result.Length;
                    _optimizedFiles.Add(file);
                    optimized++;
                }
                catch (Exception ex2)
                {
                    AppLogger.Warn($"TinyPNG failed for {file.WikiFilename}: {ex2.Message}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"TinyPNG failed for {file.WikiFilename}: {ex.Message}");
            }
        }

        btnOptimize.IsEnabled = true;
        btnUpload.IsEnabled = _optimizedFiles.Count > 0;
        BuildFileList(); // Refresh to show optimized sizes

        ShowInfo($"Optimized {optimized}/{toOptimize.Count} files.", InfoBarSeverity.Success);
    }

    /// <summary>
    /// Checks if split event item images already exist in Processed Images.
    /// If found, replaces the EventItem entry with individual split entries.
    /// </summary>
    private async Task CheckExistingSplitEventItemAsync()
    {
        var eventItemEntry = _detectedFiles.FirstOrDefault(f => f.Category == "EventItem");
        if (eventItemEntry == null) return;

        var eventItemName = _mystery.EventItemName;
        if (string.IsNullOrEmpty(eventItemName)) return;

        // Check Processed Images folder for split files (level 1-4 typical for event items)
        var processedDir = MysteryWikiService.ResolveExportPngsDir(
            _main.Settings.ImageExporterBasePath, _main.Settings.SelectedApkVersion);
        var workspaceDir = _main.Settings.ImageExporterBasePath;

        string? searchDir = null;
        if (!string.IsNullOrEmpty(workspaceDir))
        {
            var pd = Path.Combine(workspaceDir, "Processed Images");
            if (Directory.Exists(pd)) searchDir = pd;
        }
        if (searchDir == null) return;

        // Search for wiki-named files (e.g., AntiqueGoBoard01.png)
        var foundSplits = new List<(string path, int level, string wikiName)>();
        for (int lvl = 1; lvl <= 10; lvl++)
        {
            var wikiName = MysteryWikiService.FormatFileName(eventItemName, lvl);
            var filePath = Path.Combine(searchDir, wikiName);
            if (File.Exists(filePath))
                foundSplits.Add((filePath, lvl, wikiName));
            else
                break;
        }

        if (foundSplits.Count == 0) return;

        // Replace EventItem entry with individual split entries
        _detectedFiles.Remove(eventItemEntry);

        foreach (var (path, level, wikiName) in foundSplits)
        {
            var file = new DetectedDecorationFile
            {
                SourcePath = path,
                WikiFilename = wikiName,
                Category = $"Event Item Lv{level:D2}",
            };

            // Check optimization marker
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                if (OptimizationWindow.HasOptMarker(bytes))
                {
                    file.OptimizedSize = bytes.Length;
                    _optimizedFiles.Add(file);
                }
            }
            catch { }

            _detectedFiles.Add(file);
        }

        // Check wiki existence for split entries
        var wikiNames = foundSplits.Select(s => $"File:{s.wikiName}").ToList();
        var existMap = await MysteryWikiService.CheckPagesExistAsync(wikiNames);
        foreach (var f in _detectedFiles.Where(f => f.Category.StartsWith("Event Item Lv")))
            f.ExistsOnWiki = existMap.GetValueOrDefault($"File:{f.WikiFilename}", false);
    }

    private void BtnOpenInOptimiser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not string filePath) return;

        // Navigate to Image Optimiser in mystery return mode
        Hide();
        _main.NavigateToImageOptimiserForMystery(filePath, splitResults =>
        {
            _main.Dispatcher.InvokeAsync(async () =>
            {
                if (splitResults.Count > 0)
                {
                    // Replace EventItem entry with individual split results
                    _detectedFiles.RemoveAll(f => f.Category == "EventItem");

                    var eventItemName = _mystery.EventItemName ?? _mystery.Name;

                    // Sort split results by the suffix number in filename to get correct level order
                    var sorted = splitResults
                        .Select(p => (Path: p, Suffix: ExtractTrailingNumber(Path.GetFileNameWithoutExtension(p))))
                        .OrderBy(x => x.Suffix)
                        .ToList();

                    for (int i = 0; i < sorted.Count; i++)
                    {
                        var level = sorted[i].Suffix > 0 ? sorted[i].Suffix : i + 1;
                        var wikiName = MysteryWikiService.FormatFileName(eventItemName, level);

                        // Copy/rename to wiki filename in Processed Images
                        var processedDir = _main.Settings.ImageExporterBasePath;
                        string finalPath = sorted[i].Path;
                        if (!string.IsNullOrEmpty(processedDir))
                        {
                            var pd = Path.Combine(processedDir, "Processed Images");
                            if (Directory.Exists(pd))
                            {
                                var destPath = Path.Combine(pd, wikiName);
                                try
                                {
                                    File.Copy(sorted[i].Path, destPath, overwrite: true);
                                    finalPath = destPath;
                                }
                                catch { }
                            }
                        }

                        var file = new DetectedDecorationFile
                        {
                            SourcePath = finalPath,
                            WikiFilename = wikiName,
                            Category = $"Event Item Lv{level:D2}",
                        };

                        // Check optimization marker
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(splitResults[i]);
                            if (OptimizationWindow.HasOptMarker(bytes))
                            {
                                file.OptimizedSize = bytes.Length;
                                _optimizedFiles.Add(file);
                            }
                        }
                        catch { }

                        _detectedFiles.Add(file);
                    }

                    // Check wiki existence for new entries
                    var newWikiNames = splitResults.Select((_, i) =>
                        $"File:{MysteryWikiService.FormatFileName(eventItemName, i + 1)}").ToList();
                    var existMap = await MysteryWikiService.CheckPagesExistAsync(newWikiNames);
                    foreach (var f in _detectedFiles.Where(f => f.Category.StartsWith("Event Item Lv")))
                        f.ExistsOnWiki = existMap.GetValueOrDefault($"File:{f.WikiFilename}", false);

                    BuildFileList();
                    ShowInfo($"Loaded {splitResults.Count} split event item images.", InfoBarSeverity.Success);
                }

                // Navigate back to Mysteries page and show dialog
                _main.NavigateToPage(6); // Mysteries page index
                Show();
            });
        });
    }

    private async void BtnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.Settings.WikiVerified)
        {
            ShowInfo("Wiki account not verified. Go to Settings → Wiki to log in.", InfoBarSeverity.Warning);
            return;
        }

        // Get all checked uploadable files (regardless of optimization state)
        var selected = _detectedFiles.Where(f =>
            f.Category != "EventItem" &&
            _checkboxes.TryGetValue(f, out var cb) && cb.IsChecked == true).ToList();

        if (selected.Count == 0)
        {
            ShowInfo("No files selected for upload.", InfoBarSeverity.Warning);
            return;
        }

        // ── Prepare TinyPNG optimize function ──
        var apiKey = _main.Settings.TinifyApiKey;
        var apiKey2 = _main.Settings.TinifyApiKey2;
        Func<byte[], Task<byte[]>> optimizeFunc = async (data) =>
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("TinyPNG API key not set.");
            TinifyAPI.Tinify.Key = apiKey;
            try { return await (await TinifyAPI.Tinify.FromBuffer(data)).ToBuffer(); }
            catch (TinifyAPI.AccountException) when (!string.IsNullOrWhiteSpace(apiKey2))
            {
                TinifyAPI.Tinify.Key = apiKey2;
                return await (await TinifyAPI.Tinify.FromBuffer(data)).ToBuffer();
            }
        };

        // ── Per-file wizard dialog flow ──
        btnUpload.IsEnabled = false;
        btnOptimize.IsEnabled = false;
        int uploaded = 0;
        int skipped = 0;
        int failed = 0;
        bool forceAll = false;
        bool skipAll = false;
        bool confirmAll = false;
        bool optimizeAll = false;

        ShowInfo($"Processing {selected.Count} files...", InfoBarSeverity.Informational);

        try
        {
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                _main.Settings.WikiUsername, _main.Settings.WikiPassword);

            var csrfJson = await client.GetStringAsync(
                "https://merge-mansion.fandom.com/api.php?action=query&meta=tokens&format=json");
            var csrfToken = System.Text.Json.JsonDocument.Parse(csrfJson).RootElement
                .GetProperty("query").GetProperty("tokens")
                .GetProperty("csrftoken").GetString()!;

            for (int idx = 0; idx < selected.Count; idx++)
            {
                var file = selected[idx];
                try
                {
                    if (!File.Exists(file.SourcePath)) { failed++; continue; }

                    bool needsOptimize = !_optimizedFiles.Contains(file);
                    var remaining = selected.Count - idx - 1;

                    // Count remaining unoptimized files for Force button visibility
                    bool hasUnoptimizedRemaining = selected.Skip(idx + 1)
                        .Any(f => !_optimizedFiles.Contains(f));

                    if (needsOptimize && !optimizeAll)
                    {
                        // Wizard dialog: Optimize → then morphs to Confirm/Conflict
                        var wizardDlg = UploadConflictDialog.CreateOptimizeWizard(
                            file.WikiFilename, remaining, file.SourcePath,
                            optimizeFunc, _main.Settings.WikiUsername,
                            hasUnoptimizedRemaining: hasUnoptimizedRemaining);
                        wizardDlg.Owner = this;
                        wizardDlg.ShowDialog();

                        // Wizard may have optimized the file regardless of final choice
                        // (user clicks Optimize → morphs → then Skip). Update state.
                        try
                        {
                            var postBytes = File.ReadAllBytes(file.SourcePath);
                            if (OptimizationWindow.HasOptMarker(postBytes))
                            {
                                _optimizedFiles.Add(file);
                                file.OptimizedSize = postBytes.Length;
                            }
                        }
                        catch { }

                        switch (wizardDlg.Choice)
                        {
                            case UploadConflictChoice.Force:
                                break;
                            case UploadConflictChoice.ForceAll:
                                forceAll = true;
                                confirmAll = true;
                                break;
                            case UploadConflictChoice.ForceAllOptimize:
                                optimizeAll = true;
                                forceAll = true;
                                confirmAll = true;
                                break;
                            case UploadConflictChoice.Skip: skipped++; continue;
                            case UploadConflictChoice.SkipAll: skipAll = true; skipped++; continue;
                            case UploadConflictChoice.Cancel: goto done;
                        }
                    }
                    else if (needsOptimize && optimizeAll)
                    {
                        // "Upload All (Force)" — optimize silently then upload
                        var data = await File.ReadAllBytesAsync(file.SourcePath);
                        var result = await optimizeFunc(data);
                        var marked = OptimizationWindow.InsertOptMarker(result);
                        await File.WriteAllBytesAsync(file.SourcePath, marked);
                        _optimizedFiles.Add(file);
                        file.OptimizedSize = marked.Length;
                    }
                    else if (needsOptimize && !optimizeAll)
                    {
                        // "Upload All" (no Force) — skip unoptimized files
                        skipped++;
                        continue;
                    }

                    // Already optimized → check wiki existence for conflict/confirm
                    if (!forceAll && !confirmAll)
                    {
                        // Re-check wiki existence
                        var existMap = await MysteryWikiService.CheckPagesExistAsync(
                            new[] { $"File:{file.WikiFilename}" });
                        file.ExistsOnWiki = existMap.GetValueOrDefault($"File:{file.WikiFilename}", false)
                            || existMap.GetValueOrDefault($"File:{file.WikiFilename.Replace('_', ' ')}", false);

                        if (file.ExistsOnWiki == true)
                        {
                            if (skipAll) { skipped++; continue; }

                            // Only show conflict if wizard didn't already handle it
                            if (!needsOptimize || optimizeAll)
                            {
                                var dlg = new UploadConflictDialog(
                                    file.WikiFilename, remaining, file.SourcePath,
                                    false, _main.Settings.WikiUsername)
                                { Owner = this };
                                dlg.ShowDialog();

                                switch (dlg.Choice)
                                {
                                    case UploadConflictChoice.Force: break;
                                    case UploadConflictChoice.ForceAll: forceAll = true; break;
                                    case UploadConflictChoice.Skip: skipped++; continue;
                                    case UploadConflictChoice.SkipAll: skipAll = true; skipped++; continue;
                                    case UploadConflictChoice.Cancel: goto done;
                                }
                            }
                        }
                        else if (!needsOptimize)
                        {
                            // New file, already optimized → confirm dialog
                            var confirmDlg = UploadConflictDialog.CreateNewFileDialog(
                                file.WikiFilename, remaining, file.SourcePath,
                                _main.Settings.WikiUsername);
                            confirmDlg.ShowForceOptimizeButton(hasUnoptimizedRemaining);
                            confirmDlg.Owner = this;
                            confirmDlg.ShowDialog();

                            if (confirmDlg.Choice == UploadConflictChoice.ForceAll)
                                confirmAll = true;
                            else if (confirmDlg.Choice == UploadConflictChoice.ForceAllOptimize)
                            {
                                optimizeAll = true;
                                forceAll = true;
                                confirmAll = true;
                            }
                            else if (confirmDlg.Choice != UploadConflictChoice.Force)
                                goto done;
                        }
                    }

                    var fileBytes = await File.ReadAllBytesAsync(file.SourcePath);
                    string? description = file.ExistsOnWiki != true ? "{{Permission}}" : null;

                    await WikiMappingService.UploadFileAsync(
                        client, csrfToken, file.WikiFilename, fileBytes,
                        description: description, ignoreWarnings: true);

                    uploaded++;
                    Increment(s => s.MysteryPagesPublished++);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Upload failed for {file.WikiFilename}: {ex.Message}");
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"Authentication failed: {ex.Message}", InfoBarSeverity.Error);
            btnUpload.IsEnabled = true;
            return;
        }

        done:
        btnUpload.IsEnabled = true;
        btnOptimize.IsEnabled = true;

        // Re-check wiki existence and refresh UI after upload
        if (uploaded > 0)
        {
            var allWikiNames = _detectedFiles
                .Where(f => f.Category != "EventItem" && !string.IsNullOrEmpty(f.WikiFilename))
                .Select(f => $"File:{f.WikiFilename}").ToList();
            if (allWikiNames.Count > 0)
            {
                var existMap = await MysteryWikiService.CheckPagesExistAsync(allWikiNames);
                foreach (var f in _detectedFiles.Where(f => !string.IsNullOrEmpty(f.WikiFilename)))
                    f.ExistsOnWiki = existMap.GetValueOrDefault($"File:{f.WikiFilename}", false);
            }
            BuildFileList();

            // Update mystery image count in cache
            _mystery.WikiStatus.ImagesExistOnWiki = _detectedFiles
                .Count(f => f.Category != "EventItem" && f.ExistsOnWiki == true);
            MysteryWikiService.UpdateSingleMysteryCache(_mystery);
            OnStatusChanged?.Invoke();
        }

        var parts = new List<string>();
        if (uploaded > 0) parts.Add($"{uploaded} uploaded");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (failed > 0) parts.Add($"{failed} failed");
        ShowInfo(string.Join(" · ", parts),
            failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    // ── Image preview with drag + zoom ────────────────────────

    private double _zoomLevel = 1.0;
    private int _imgNativeW, _imgNativeH;
    private Point _dragStart;
    private double _dragStartX, _dragStartY;
    private bool _isDragging;
    private bool _didDrag;

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

            Dispatcher.InvokeAsync(() => CenterAndFitPreview(),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch { /* ignore */ }
    }

    private void CenterAndFitPreview()
    {
        double availW = previewCanvas.ActualWidth;
        double availH = previewCanvas.ActualHeight;
        if (availW <= 0 || availH <= 0 || _imgNativeW <= 0) return;

        // Auto-fit: zoom down in 25% steps
        _zoomLevel = 1.0;
        while (_zoomLevel > 0.25 &&
               (_imgNativeW * _zoomLevel > availW || _imgNativeH * _zoomLevel > availH))
            _zoomLevel -= 0.125;

        ApplyZoom();

        // Center
        Canvas.SetLeft(previewImage, (availW - previewImage.Width) / 2);
        Canvas.SetTop(previewImage, (availH - previewImage.Height) / 2);
        UpdatePreviewInfo();
    }

    private void ApplyZoom()
    {
        previewImage.Width = _imgNativeW * _zoomLevel;
        previewImage.Height = _imgNativeH * _zoomLevel;
    }

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (previewOverlay.Visibility == Visibility.Visible && previewImage.Source != null)
            CenterAndFitPreview();
    }

    private void UpdatePreviewInfo()
    {
        var pct = (int)(_zoomLevel * 100);
        txtPreviewInfo.Text = $"{_imgNativeW}\u00D7{_imgNativeH} \u00B7 {pct}% \u00B7 Scroll to zoom \u00B7 Drag to pan";
    }

    private void PreviewOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDragging = true;
        _didDrag = false;
        _dragStart = e.GetPosition(previewCanvas);
        _dragStartX = Canvas.GetLeft(previewImage);
        _dragStartY = Canvas.GetTop(previewImage);
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
        double dx = pos.X - _dragStart.X;
        double dy = pos.Y - _dragStart.Y;
        if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) _didDrag = true;
        Canvas.SetLeft(previewImage, _dragStartX + dx);
        Canvas.SetTop(previewImage, _dragStartY + dy);
    }

    private void PreviewOverlay_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        bool wasDragging = _isDragging;
        _isDragging = false;
        previewOverlay.Cursor = System.Windows.Input.Cursors.Arrow;
        previewOverlay.ReleaseMouseCapture();

        // Close only on click (no drag) outside the image
        if (wasDragging && !_didDrag)
        {
            var imgLeft = Canvas.GetLeft(previewImage);
            var imgTop = Canvas.GetTop(previewImage);
            if (double.IsNaN(imgLeft)) imgLeft = 0;
            if (double.IsNaN(imgTop)) imgTop = 0;
            var pos = e.GetPosition(previewCanvas);
            bool onImage = pos.X >= imgLeft && pos.Y >= imgTop
                && pos.X <= imgLeft + previewImage.Width
                && pos.Y <= imgTop + previewImage.Height;
            if (!onImage) ClosePreview();
        }
    }

    private void PreviewClose_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ClosePreview();
        e.Handled = true;
    }

    private void ClosePreview()
    {
        previewOverlay.Visibility = Visibility.Collapsed;
        previewImage.Source = null;
        _isDragging = false;
    }

    private void PreviewScroll_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        var oldZoom = _zoomLevel;
        _zoomLevel += e.Delta > 0 ? 0.125 : -0.125;
        _zoomLevel = Math.Max(0.25, Math.Min(4.0, _zoomLevel));

        // Zoom towards cursor
        var cursorPos = e.GetPosition(previewCanvas);
        double left = Canvas.GetLeft(previewImage);
        double top = Canvas.GetTop(previewImage);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        double ratio = _zoomLevel / oldZoom;
        double newLeft = cursorPos.X - (cursorPos.X - left) * ratio;
        double newTop = cursorPos.Y - (cursorPos.Y - top) * ratio;

        ApplyZoom();
        Canvas.SetLeft(previewImage, newLeft);
        Canvas.SetTop(previewImage, newTop);
        UpdatePreviewInfo();
    }

    // ── Helpers ────────────────────────────────────────────────

    private static int ExtractTrailingNumber(string name)
    {
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i])) i--;
        if (i < name.Length - 1 && int.TryParse(name[(i + 1)..], out var num))
            return num;
        return 0;
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
