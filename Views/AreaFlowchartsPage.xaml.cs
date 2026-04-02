using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Wpf.Ui.Appearance;
using static MergeMansionWikiTools.Services.UserStatsService;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public partial class AreaFlowchartsPage : UserControl
{
    private readonly MainWindow _main;
    private List<LuaArea>? _areas;
    private bool _areasLoaded;
    private string? _lastLoadedPath;
    private L1CostService? _l1CostService;
    private readonly SolidColorBrush _splitSepBrush;

    public AreaFlowchartsPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        _splitSepBrush = GetSplitSeparatorBrush();
        btnGenerateAllSep.Background = _splitSepBrush;
        ApplicationThemeManager.Changed += (_, _) => Dispatcher.InvokeAsync(RefreshSplitSeparatorColor);
        UpdateOutputPathDisplay();
        UpdateGenerateAllSplitVisibility();
        ApplySplitButtonStyle(btnGenerateAll, btnGenerateAllMenu);
        _ = TryLoadAreasAsync();

        // Auto-reload when areas.json path changes
        _main.AreasFileChanged += () => Dispatcher.InvokeAsync(async () =>
        {
            _areasLoaded = false;
            _areas = null;
            areaListPanel.Children.Clear();
            txtEmpty.Text = "No areas.json file configured. Set the path in Settings.";
            await TryLoadAreasAsync();
        });

        // Refresh output path when APK version changes (affects derived path)
        _main.ApkVersionChanged += () => Dispatcher.InvokeAsync(() =>
        {
            UpdateOutputPathDisplay();
            UpdateGenerateAllSplitVisibility();
            if (_areasLoaded) BuildAreaList();
        });
    }

    /// <summary>
    /// Called each time the user navigates to this page. Reloads areas if the path changed.
    /// </summary>
    public async void OnPageShown()
    {
        var currentPath = _main.Settings.AreasJsonPath;
        if (currentPath != _lastLoadedPath)
        {
            _areasLoaded = false;
            _areas = null;
            areaListPanel.Children.Clear();
            await TryLoadAreasAsync();
        }
        UpdateOutputPathDisplay();
    }

    // ── Area loading ─────────────────────────────────────────────────

    private async Task TryLoadAreasAsync()
    {
        var path = _main.Settings.AreasJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            btnGoToSettings.Visibility = Visibility.Visible;
            emptyPanel.Visibility = Visibility.Visible;
            return;
        }

        loadingPanel.Visibility = Visibility.Visible;
        try
        {
            var service = new AreasService();
            await service.LoadAsync(path);
            _areas = service.Areas
                .Where(a => a.DisplayName != "Story Event" && a.DisplayName != "Maddie Meets Mansion")
                .ToList();
            _areasLoaded = true;
            _lastLoadedPath = path;
            await DiscordFlowchartService.EnsureMappingLoadedAsync();
            loadingPanel.Visibility = Visibility.Collapsed;
            BuildAreaList();

            // TEMP: Dump area-related bundles for research
            _ = Task.Run(() =>
            {
                try
                {
                    AppLogger.Info("[DUMP] Starting bundle dump research...");
                    var basePath = _main.Settings.ImageExporterBasePath;
                    var version = _main.Settings.SelectedApkVersion;
                    AppLogger.Info($"[DUMP] basePath={basePath}, version={version}");
                    if (string.IsNullOrEmpty(basePath)) { AppLogger.Info("[DUMP] basePath empty"); return; }

                    // tpk: check version dir, then parent
                    var versionDir = !string.IsNullOrEmpty(version)
                        ? Path.Combine(basePath, version) : basePath;
                    var tpkPath = Path.Combine(versionDir, "classdata.tpk");
                    AppLogger.Info($"[DUMP] Trying tpk: {tpkPath} (exists={File.Exists(tpkPath)})");
                    if (!File.Exists(tpkPath))
                    {
                        tpkPath = Path.Combine(basePath, "classdata.tpk");
                        AppLogger.Info($"[DUMP] Trying tpk: {tpkPath} (exists={File.Exists(tpkPath)})");
                    }
                    if (!File.Exists(tpkPath)) { AppLogger.Info("[DUMP] classdata.tpk not found anywhere"); return; }

                    var apkDir = Path.Combine(versionDir, "Game Files", "APK");
                    AppLogger.Info($"[DUMP] APK dir: {apkDir} (exists={Directory.Exists(apkDir)})");
                    // Only dump AreaIconsLibrary — it has the mapping we need
                    var p = Path.Combine(apkDir, "scriptableobjectsareaiconslibrary_assets_all.bundle");
                    if (File.Exists(p))
                        AssetExtractionService.DumpBundleContents(p, tpkPath, maxFieldDepth: 7);
                }
                catch (Exception ex) { AppLogger.Info($"[DUMP] Error: {ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            loadingPanel.Visibility = Visibility.Collapsed;
            ShowInfo($"Failed to load areas: {ex.Message}", InfoBarSeverity.Error);
            txtEmpty.Text = "Failed to load areas.json.";
            btnGoToSettings.Visibility = Visibility.Collapsed;
            emptyPanel.Visibility = Visibility.Visible;
        }
    }

    // ── Area list building ───────────────────────────────────────────

    private void BuildAreaList()
    {
        areaListPanel.Children.Clear();

        if (_areas == null || _areas.Count == 0)
        {
            btnGoToSettings.Visibility = Visibility.Visible;
            emptyPanel.Visibility = Visibility.Visible;
            return;
        }

        emptyPanel.Visibility = Visibility.Collapsed;
        var search = txtSearch.Text?.Trim() ?? "";

        var filtered = string.IsNullOrEmpty(search)
            ? _areas
            : _areas.Where(a => a.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                a.InternalName.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .ToList();

        if (filtered.Count == 0)
        {
            txtEmpty.Text = "No areas match your search.";
            btnGoToSettings.Visibility = Visibility.Collapsed;
            emptyPanel.Visibility = Visibility.Visible;
            return;
        }

        var hasFolder = GetOutputDir() != null;

        var sorted = filtered
            .OrderBy(a => DiscordFlowchartService.GetOrderingIndex(a.DisplayName))
            .ToList();

        foreach (var area in sorted)
        {
            var card = CreateAreaCard(area, hasFolder);
            areaListPanel.Children.Add(card);
        }
    }

    private Border CreateAreaCard(LuaArea area, bool hasFolder)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(16, 12, 16, 12)
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Area image thumbnail (wiki-style crop)
        var imagePath = FindAreaImagePath(area);
        if (imagePath != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(imagePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 200;
                bmp.EndInit();
                bmp.Freeze();

                var imgContainer = new Border
                {
                    Width = 64,
                    ClipToBounds = true,
                    CornerRadius = new CornerRadius(6, 0, 0, 6), // match card corner radius
                    VerticalAlignment = VerticalAlignment.Stretch, // match card height
                    Margin = new Thickness(-16, -12, 12, -12), // flush with card left edge
                    Background = new ImageBrush(bmp)
                    {
                        Stretch = Stretch.UniformToFill
                    }
                };

                Grid.SetColumn(imgContainer, 0);
                grid.Children.Add(imgContainer);
            }
            catch { /* image load failed — card renders without thumbnail */ }
        }

        // Area name + task count
        var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var nameText = new TextBlock
        {
            Text = area.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        namePanel.Children.Add(nameText);

        var taskCount = L1CostService.CountTasksWithRequirements(area);
        // Lazy-init L1 cost service
        if (_l1CostService == null && _main.DataService != null)
            _l1CostService = new L1CostService(_main.DataService);
        var l1Total = _l1CostService?.ComputeAreaL1Cost(area) ?? 0;
        var l1Str = l1Total.ToString("N0"); // thousands separator
        var metaText = new TextBlock
        {
            Text = $"{taskCount} tasks · {l1Str} L1",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        metaText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        namePanel.Children.Add(metaText);

        Grid.SetColumn(namePanel, 1);
        grid.Children.Add(namePanel);

        // Open buttons (SVG + HTML)
        var openPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        var btnOpenSvg = new Wpf.Ui.Controls.Button
        {
            Content = "SVG",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Tag = area
        };
        btnOpenSvg.Click += BtnOpenSvg_Click;
        openPanel.Children.Add(btnOpenSvg);

        var btnOpenHtml = new Wpf.Ui.Controls.Button
        {
            Content = "HTML",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = area
        };
        btnOpenHtml.Click += BtnOpenHtml_Click;
        openPanel.Children.Add(btnOpenHtml);

        Grid.SetColumn(openPanel, 2);
        grid.Children.Add(openPanel);

        // Generate button (always split — chevron has "Save to..." + "Publish to Discord")
        var genContainer = new Grid { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        genContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        genContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        genContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var btnGen = new Wpf.Ui.Controls.Button
        {
            Content = "Generate",
            Appearance = ControlAppearance.Primary,
            Height = 32,
            Tag = area
        };
        btnGen.Click += BtnGenerate_Click;
        Grid.SetColumn(btnGen, 0);
        genContainer.Children.Add(btnGen);

        // Thin separator between main button and chevron
        var sep = new Border
        {
            Width = 1,
            Background = _splitSepBrush,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 1.5)
        };
        Grid.SetColumn(sep, 1);
        genContainer.Children.Add(sep);

        var btnChevron = new Wpf.Ui.Controls.Button
        {
            Appearance = ControlAppearance.Primary,
            Height = 32,
            Width = 28,
            Padding = new Thickness(0),
            Tag = area,
            Content = CreateAccentSymbolIcon(SymbolRegular.ChevronDown24, 12)
        };
        btnChevron.Click += BtnGenerateMenu_Click;
        Grid.SetColumn(btnChevron, 2);
        genContainer.Children.Add(btnChevron);

        ApplySplitButtonStyle(btnGen, btnChevron);

        Grid.SetColumn(genContainer, 3);
        grid.Children.Add(genContainer);

        // Check if SVG already exists — show Open button
        var outputPath = GetOutputFilePath(area);
        if (outputPath != null && File.Exists(outputPath))
            openPanel.Visibility = Visibility.Visible;

        border.Child = grid;
        return border;
    }

    // ── Generation ───────────────────────────────────────────────────

    private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not LuaArea area) return;

        // If no folder configured, ask user to pick one first
        if (GetOutputDir() == null)
        {
            var folder = BrowseOutputFolder();
            if (folder == null) return;
            SetOutputFolder(folder);
        }

        btn.IsEnabled = false;
        btn.Content = "Generating...";

        try
        {
            await GenerateFlowchartAsync(area);
            ShowInfo($"Generated flowchart for {area.DisplayName}.", InfoBarSeverity.Success);

            // Show Open button in the card + update global Open Folder visibility
            ShowOpenButtonInCard(btn);
            UpdateOutputPathDisplay();
        }
        catch (Exception ex)
        {
            ShowInfo($"Error generating {area.DisplayName}: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = "Generate";
        }
    }

    private async void BtnGenerateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_areas == null || _areas.Count == 0) return;

        // If no folder configured, ask user to pick one first
        if (GetOutputDir() == null)
        {
            var folder = BrowseOutputFolder();
            if (folder == null) return;
            SetOutputFolder(folder);
        }

        var outputDir = GetOutputDir()!;

        btnGenerateAll.IsEnabled = false;
        btnGenerateAll.Content = "Generating...";

        try
        {
            int count = 0;
            foreach (var area in _areas)
            {
                try
                {
                    await GenerateFlowchartAsync(area);
                    count++;
                }
                catch (InvalidOperationException)
                {
                    // Skip areas with no tasks/requirements (e.g. Gift Shop)
                }
            }

            ShowInfo($"Generated {count} flowcharts.\n\u2192 {outputDir}", InfoBarSeverity.Success, persistent: true);

            // Refresh list to show Open buttons + update global Open Folder visibility
            BuildAreaList();
            UpdateOutputPathDisplay();
        }
        catch (Exception ex)
        {
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnGenerateAll.IsEnabled = true;
            btnGenerateAll.Content = "Generate All";
        }
    }

    /// <summary>
    /// Chevron click on per-area split button — shows context menu with save + Discord options.
    /// </summary>
    private void BtnGenerateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn) return;
        var area = btn.Tag as LuaArea;

        var menu = new ContextMenu();

        if (GetOutputDir() != null)
        {
            var saveItem = new System.Windows.Controls.MenuItem { Header = "Save to..." };
            saveItem.Click += async (_, _) =>
            {
                if (area != null) await GenerateToCustomFolder(area, btn);
            };
            menu.Items.Add(saveItem);
        }

        var discordItem = new System.Windows.Controls.MenuItem { Header = "Publish to Discord" };
        discordItem.Click += async (_, _) =>
        {
            if (area != null) await PublishAreaToDiscordAsync(area);
        };
        menu.Items.Add(discordItem);

        var target = btn.Parent as FrameworkElement ?? btn;
        OpenSplitMenu(menu, target);
    }

    /// <summary>
    /// Chevron click on Generate All split button — shows "Save all to..." context menu.
    /// </summary>
    private void BtnGenerateAllMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var item = new System.Windows.Controls.MenuItem { Header = "Save all to..." };
        item.Click += async (_, _) => await GenerateAllToCustomFolder();
        menu.Items.Add(item);

        var target = btnGenerateAllMenu.Parent as FrameworkElement ?? btnGenerateAllMenu;
        OpenSplitMenu(menu, target);
    }

    private async Task GenerateToCustomFolder(LuaArea area, FrameworkElement chevronBtn)
    {
        var folder = BrowseOutputFolder();
        if (folder == null) return;

        // Find the Generate button (sibling in same genContainer grid)
        Wpf.Ui.Controls.Button? genBtn = null;
        if (chevronBtn.Parent is Grid genGrid)
            genBtn = genGrid.Children.OfType<Wpf.Ui.Controls.Button>().FirstOrDefault(b => b.Content?.ToString() == "Generate");

        if (genBtn != null) { genBtn.IsEnabled = false; genBtn.Content = "Generating..."; }

        try
        {
            var safeName = string.Join("_", area.DisplayName.Split(Path.GetInvalidFileNameChars()));
            var outputPath = Path.Combine(folder, $"{safeName}.svg");
            Directory.CreateDirectory(folder);

            var itemIcons = await Task.Run(() => ExtractItemIconsForArea(area, folder));

            var svg = await Task.Run(() => FlowchartService.GenerateSvg(area, _main.DataService, itemIcons: itemIcons));
            if (string.IsNullOrEmpty(svg))
                throw new InvalidOperationException($"No tasks with requirements found in {area.DisplayName}.");

            await File.WriteAllTextAsync(outputPath, svg);

            // Also generate Discord HTML version
            var html = await Task.Run(() => FlowchartService.GenerateSvg(area, _main.DataService, forDiscord: true, itemIcons: itemIcons, tokenIcons: LoadTokenIcons()));
            if (!string.IsNullOrEmpty(html))
                await File.WriteAllTextAsync(Path.Combine(folder, $"{safeName}.html"), html);

            if (_main.Settings.DebugMode) WriteDebugCopy(area, svg);
            ShowInfo($"Generated flowchart for {area.DisplayName}.\n\u2192 {folder}", InfoBarSeverity.Success);

            if (genBtn != null) ShowOpenButtonInCard(genBtn);
            MaybeUpdateDefaultFolder(folder);
        }
        catch (Exception ex)
        {
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (genBtn != null) { genBtn.IsEnabled = true; genBtn.Content = "Generate"; }
        }
    }

    private async Task GenerateAllToCustomFolder()
    {
        if (_areas == null || _areas.Count == 0) return;

        var folder = BrowseOutputFolder();
        if (folder == null) return;

        btnGenerateAll.IsEnabled = false;
        btnGenerateAll.Content = "Generating...";

        try
        {
            Directory.CreateDirectory(folder);
            int count = 0;
            foreach (var area in _areas)
            {
                var safeName = string.Join("_", area.DisplayName.Split(Path.GetInvalidFileNameChars()));
                var outputPath = Path.Combine(folder, $"{safeName}.svg");

                var itemIcons = await Task.Run(() => ExtractItemIconsForArea(area, folder));

                var svg = await Task.Run(() => FlowchartService.GenerateSvg(area, _main.DataService, itemIcons: itemIcons));
                if (!string.IsNullOrEmpty(svg))
                {
                    await File.WriteAllTextAsync(outputPath, svg);

                    // Also generate Discord HTML version
                    var html = await Task.Run(() => FlowchartService.GenerateSvg(area, _main.DataService, forDiscord: true, itemIcons: itemIcons, tokenIcons: LoadTokenIcons()));
                    if (!string.IsNullOrEmpty(html))
                        await File.WriteAllTextAsync(Path.Combine(folder, $"{safeName}.html"), html);

                    if (_main.Settings.DebugMode) WriteDebugCopy(area, svg);
                    count++;
                }
            }

            ShowInfo($"Generated {count} flowcharts.\n\u2192 {folder}", InfoBarSeverity.Success, persistent: true);
            MaybeUpdateDefaultFolder(folder);
            BuildAreaList();
        }
        catch (Exception ex)
        {
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnGenerateAll.IsEnabled = true;
            btnGenerateAll.Content = "Generate All";
        }
    }

    private async Task GenerateFlowchartAsync(LuaArea area)
    {
        var outputPath = GetOutputFilePath(area);
        if (outputPath == null)
            throw new InvalidOperationException("Output folder not configured.");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Extract item icons from atlases
        var itemIcons = await Task.Run(() => ExtractItemIconsForArea(area, dir!));

        // Generate wiki SVG (with icons)
        var svg = await Task.Run(() =>
            FlowchartService.GenerateSvg(area, _main.DataService, itemIcons: itemIcons));

        if (string.IsNullOrEmpty(svg))
            throw new InvalidOperationException($"No tasks with requirements found in {area.DisplayName}.");

        await File.WriteAllTextAsync(outputPath, svg);

        // Generate Discord HTML (interactive version, with icons)
        var html = await Task.Run(() =>
            FlowchartService.GenerateSvg(area, _main.DataService, forDiscord: true, itemIcons: itemIcons, tokenIcons: LoadTokenIcons()));

        if (!string.IsNullOrEmpty(html))
        {
            var htmlPath = Path.ChangeExtension(outputPath, ".html");
            await File.WriteAllTextAsync(htmlPath, html);
        }

        // Debug mode: also save next to .exe
        if (_main.Settings.DebugMode)
            WriteDebugCopy(area, svg);
    }

    /// <summary>
    /// Extracts item icons for all requirements in an area's tasks.
    /// Returns itemType → base64 PNG mapping. Saves cropped PNGs to Images/ subfolder.
    /// </summary>
    private Dictionary<string, string>? ExtractItemIconsForArea(LuaArea area, string outputDir)
    {
        try
        {
            var basePath = _main.Settings.ImageExporterBasePath;
            var version = _main.Settings.SelectedApkVersion;
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
                return null;

            var exportDir = Path.Combine(basePath, version, "Export - PNGs");
            if (!Directory.Exists(exportDir))
                return null;

            if (_main.DataService == null) return null;

            // Collect all unique itemTypes from area tasks (requirements + reward items)
            var itemTypes = area.Tasks
                .SelectMany(t => t.Requirements.Keys)
                .Concat(area.Tasks
                    .Select(t => t.ItemReward)
                    .Where(r => !string.IsNullOrEmpty(r))!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (itemTypes.Count == 0) return null;

            // Use shared Processed Images folder (same as Image Optimiser)
            var processedImagesDir = Path.Combine(basePath, "Processed Images");
            var icons = FlowchartImageService.ExtractItemIcons(
                itemTypes, _main.DataService, exportDir, processedImagesDir);

            return icons.Count > 0 ? icons : null;
        }
        catch (Exception ex)
        {
            AppLogger.Info($"[FLOWCHART] Icon extraction failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Token field → image file mapping for flowchart token slots.</summary>
    private static readonly (string Field, string FileName)[] TokenImageFiles =
    {
        ("SoloMilestoneHotspotValue", "TeatimeDelight_tea-token_96.png"),
        ("BoultonLeaguePoints",       "BLE_Shared_PointsIcon_96.png"),
        ("DigEventTaps",              "DE_Shared_Pickaxe_Image_96.png"),
        ("ClassicRacesSailPoints",    "HorizonCup_coin-token_96.png"),
        ("RollTheDiceToken",          "RollTheDice_Knife-token_96.png"),
    };

    private Dictionary<string, string>? _tokenIconsCache;

    /// <summary>Loads token PNG images as base64 strings. Searches known paths.</summary>
    private Dictionary<string, string> LoadTokenIcons()
    {
        if (_tokenIconsCache != null) return _tokenIconsCache;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // Try known search paths for token images
        var searchDirs = new List<string>();
        searchDirs.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Tokens"));
        // User's asset directory (relative to ImageExporterBasePath parent)
        var basePath = _main.Settings.ImageExporterBasePath;
        if (!string.IsNullOrEmpty(basePath))
        {
            var parent = Path.GetDirectoryName(basePath);
            if (parent != null)
                searchDirs.Add(Path.Combine(parent, "Assets", "Tokens"));
        }
        // Hardcoded fallback for known project layout
        searchDirs.Add(@"D:\_BACKUP_2.0\Adobe Photoshop - Savy\Merge Mansion\Assets\Tokens");

        string? tokenDir = null;
        foreach (var dir in searchDirs)
        {
            if (Directory.Exists(dir)) { tokenDir = dir; break; }
        }

        if (tokenDir != null)
        {
            foreach (var (field, fileName) in TokenImageFiles)
            {
                var path = Path.Combine(tokenDir, fileName);
                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    result[field] = Convert.ToBase64String(bytes);
                }
            }
        }

        _tokenIconsCache = result;
        return result;
    }

    private static void WriteDebugCopy(LuaArea area, string svg)
    {
        try
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var debugDir = Path.Combine(exeDir, "FlowCharts");
            Directory.CreateDirectory(debugDir);
            var safeName = string.Join("_", area.DisplayName.Split(Path.GetInvalidFileNameChars()));
            File.WriteAllText(Path.Combine(debugDir, $"{safeName}.svg"), svg);
        }
        catch { /* silent — debug copy is best-effort */ }
    }

    // ── Folder management ─────────────────────────────────────────────

    private string? BrowseOutputFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Select flowchart output folder" };
        var dir = GetOutputDir();
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            dlg.InitialDirectory = dir;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private void SetOutputFolder(string folder)
    {
        _main.Settings.FlowchartOutputPath = folder;
        _main.SaveSettings();
        UpdateOutputPathDisplay();
        UpdateGenerateAllSplitVisibility();

        // Rebuild list so per-area cards get chevron buttons
        if (_areasLoaded) BuildAreaList();
    }

    /// <summary>
    /// After "Save to..." to a custom folder, check if we should update the default.
    /// </summary>
    private void MaybeUpdateDefaultFolder(string newFolder)
    {
        var current = _main.Settings.FlowchartOutputPath;
        if (string.Equals(newFolder, current, StringComparison.OrdinalIgnoreCase)) return;

        if (_main.Settings.FlowchartRememberFolderChoice)
        {
            if (_main.Settings.FlowchartAutoUpdateFolder)
                SetOutputFolder(newFolder);
            return;
        }

        var dlg = new FolderUpdateDialog(newFolder) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            if (dlg.DontAskAgain)
            {
                _main.Settings.FlowchartRememberFolderChoice = true;
                _main.Settings.FlowchartAutoUpdateFolder = dlg.UpdateDefault;
                _main.SaveSettings();
            }

            if (dlg.UpdateDefault)
                SetOutputFolder(newFolder);
        }
    }

    private void UpdateGenerateAllSplitVisibility()
    {
        var vis = GetOutputDir() != null ? Visibility.Visible : Visibility.Collapsed;
        btnGenerateAllMenu.Visibility = vis;
        btnGenerateAllSep.Visibility = vis;
    }

    // ── Open SVG / folder ────────────────────────────────────────────

    private void BtnOpenSvg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not LuaArea area) return;
        var path = GetOutputFilePath(area);
        if (path != null && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void BtnOpenHtml_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not LuaArea area) return;
        var svgPath = GetOutputFilePath(area);
        if (svgPath == null) return;
        var htmlPath = Path.ChangeExtension(svgPath, ".html");
        if (File.Exists(htmlPath))
            Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true });
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetOutputDir();
        if (dir != null && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
    }

    // ── Discord publishing ───────────────────────────────────────────

    private async void BtnPublishDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (_areas == null || _areas.Count == 0)
        {
            ShowInfo("No areas loaded.", InfoBarSeverity.Warning);
            return;
        }

        var token = AppSettings.DefaultDiscordBotToken;
        var threadId = _main.Settings.FlowchartDiscordThreadId;

        btnPublishDiscord.IsEnabled = false;
        btnGenerateAll.IsEnabled = false;

        try
        {
            ShowInfo("Generating Discord flowcharts…", InfoBarSeverity.Informational);
            var svgs = new List<(LuaArea area, string svg)>();
            var outputDir = GetOutputDir();
            await Task.Run(() =>
            {
                foreach (var area in _areas)
                {
                    var itemIcons = outputDir != null ? ExtractItemIconsForArea(area, outputDir) : null;
                    var svg = FlowchartService.GenerateSvg(area, _main.DataService, forDiscord: true, itemIcons: itemIcons, tokenIcons: LoadTokenIcons());
                    if (!string.IsNullOrEmpty(svg))
                        svgs.Add((area, svg));
                }
            });

            if (svgs.Count == 0)
            {
                ShowInfo("No flowcharts generated (no areas with tasks).", InfoBarSeverity.Warning);
                return;
            }

            var progress = new Progress<string>(msg =>
            {
                infoBar.Message = msg;
                infoBar.Severity = InfoBarSeverity.Informational;
                infoBar.IsOpen = true;
            });

            var (created, updated, deleted, failed) = await DiscordFlowchartService.PublishAllAsync(
                token, threadId, svgs, progress);

            var parts = new List<string>();
            if (created > 0) parts.Add($"{created} created");
            if (updated > 0) parts.Add($"{updated} updated");
            if (deleted > 0) parts.Add($"{deleted} deleted");
            if (failed > 0) parts.Add($"{failed} failed");
            var summary = "Discord: " + string.Join(", ", parts);
            ShowInfo(summary, failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                persistent: true);
        }
        catch (Exception ex)
        {
            ShowInfo($"Discord publish error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnPublishDiscord.IsEnabled = true;
            btnGenerateAll.IsEnabled = true;
        }
    }

    private async Task PublishAreaToDiscordAsync(LuaArea area)
    {
        var token = AppSettings.DefaultDiscordBotToken;
        var threadId = _main.Settings.FlowchartDiscordThreadId;

        ShowInfo($"Publishing {area.DisplayName} to Discord…", InfoBarSeverity.Informational);

        try
        {
            var discordOutputDir = GetOutputDir();
            var itemIcons = discordOutputDir != null
                ? await Task.Run(() => ExtractItemIconsForArea(area, discordOutputDir))
                : null;

            var svg = await Task.Run(() =>
                FlowchartService.GenerateSvg(area, _main.DataService, forDiscord: true, itemIcons: itemIcons, tokenIcons: LoadTokenIcons()));

            if (string.IsNullOrEmpty(svg))
            {
                ShowInfo($"{area.DisplayName}: no tasks with requirements.", InfoBarSeverity.Warning);
                return;
            }

            var progress = new Progress<string>(msg =>
            {
                infoBar.Message = msg;
                infoBar.Severity = InfoBarSeverity.Informational;
                infoBar.IsOpen = true;
            });

            // Callback to generate SVG for any area (used when ordering needs fixing)
            Func<string, Task<string?>> generateSvg = async name =>
            {
                var match = _areas?.FirstOrDefault(a =>
                    a.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match == null) return null;
                var matchIcons = discordOutputDir != null
                    ? ExtractItemIconsForArea(match, discordOutputDir) : null;
                return await Task.Run(() =>
                    FlowchartService.GenerateSvg(match, _main.DataService, forDiscord: true, itemIcons: matchIcons, tokenIcons: LoadTokenIcons()));
            };

            var (success, wasUpdate, reordered) = await DiscordFlowchartService.PublishOneAsync(
                token, threadId, area.DisplayName, svg, generateSvg, progress);

            if (success)
            {
                var msg = $"{area.DisplayName}: {(wasUpdate ? "updated" : "created")} on Discord.";
                if (reordered > 0)
                    msg += $" {reordered} other message{(reordered > 1 ? "s" : "")} reordered.";
                ShowInfo(msg, InfoBarSeverity.Success);
            }
            else
                ShowInfo($"{area.DisplayName}: Discord publish failed.", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowInfo($"Discord error: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ── Search ───────────────────────────────────────────────────────

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_areasLoaded)
            BuildAreaList();
    }

    private void BtnGoToSettings_Click(object sender, RoutedEventArgs e)
    {
        _main.NavigateToSettingsHighlightAreas();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private string? GetOutputDir()
    {
        // Explicit path takes priority
        var path = _main.Settings.FlowchartOutputPath;
        if (!string.IsNullOrEmpty(path))
            return path;

        // Derive from workspace + version + "Flowcharts"
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (!string.IsNullOrEmpty(basePath) && !string.IsNullOrEmpty(version))
            return Path.Combine(basePath, version, "Flowcharts");

        return null;
    }

    private string? GetOutputFilePath(LuaArea area)
    {
        var dir = GetOutputDir();
        if (dir == null) return null;

        var safeName = string.Join("_", area.DisplayName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(dir, $"{safeName}.svg");
    }

    private void UpdateOutputPathDisplay()
    {
        var dir = GetOutputDir();
        if (dir != null)
        {
            txtOutputPath.Text = dir;
            btnOpenFolder.Visibility = Visibility.Visible;
        }
        else
        {
            txtOutputPath.Text = "Not configured — set in Settings";
            btnOpenFolder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Finds the Open panel in the same card as the given Generate button and makes it visible.
    /// </summary>
    private static void ShowOpenButtonInCard(Wpf.Ui.Controls.Button genBtn)
    {
        if (genBtn.Parent is Grid genContainer && genContainer.Parent is Grid cardGrid)
        {
            var openPanel = cardGrid.Children.OfType<StackPanel>().FirstOrDefault();
            if (openPanel != null) openPanel.Visibility = Visibility.Visible;
        }
    }

    private static SymbolIcon CreateAccentSymbolIcon(SymbolRegular symbol, double fontSize)
    {
        var icon = new SymbolIcon { Symbol = symbol, FontSize = fontSize };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        return icon;
    }

    /// <summary>
    /// Opens a split-button context menu with expand-down animation (no slide from above).
    /// </summary>
    private static void OpenSplitMenu(ContextMenu menu, FrameworkElement target)
    {
        // Disable built-in slide animation
        menu.Resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;

        // Custom expand-down: ScaleY 0→1 from top edge
        menu.RenderTransformOrigin = new Point(0.5, 0);
        menu.RenderTransform = new ScaleTransform(1, 0);

        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;

        menu.Opened += (_, _) =>
        {
            menu.HorizontalOffset = target.ActualWidth - menu.ActualWidth;

            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ((ScaleTransform)menu.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };

        menu.IsOpen = true;
    }

    /// <summary>
    /// Derives a separator color from the accent button color (slightly darker).
    /// </summary>
    private Color GetSplitSeparatorColor()
    {
        try
        {
            if (FindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
            {
                var c = accent.Color;
                return Color.FromRgb(
                    (byte)Math.Max(0, c.R - 35),
                    (byte)Math.Max(0, c.G - 35),
                    (byte)Math.Max(0, c.B - 35));
            }
        }
        catch { }
        return Color.FromArgb(0x30, 0, 0, 0);
    }

    private SolidColorBrush GetSplitSeparatorBrush() => new(GetSplitSeparatorColor());

    private void RefreshSplitSeparatorColor() => _splitSepBrush.Color = GetSplitSeparatorColor();

    // ── Split button styling ────────────────────────────────────────

    /// <summary>
    /// Makes two adjacent buttons look like a single merged split button:
    /// left button gets rounded-left corners, right button gets rounded-right.
    /// </summary>
    private static void ApplySplitButtonStyle(Wpf.Ui.Controls.Button leftBtn, Wpf.Ui.Controls.Button rightBtn)
    {
        leftBtn.Margin = new Thickness(0);
        rightBtn.Margin = new Thickness(0);

        leftBtn.Loaded += (_, _) => SetInternalCornerRadius(leftBtn, new CornerRadius(4, 0, 0, 4));
        rightBtn.Loaded += (_, _) => SetInternalCornerRadius(rightBtn, new CornerRadius(0, 4, 4, 0));
    }

    /// <summary>
    /// Finds the Border element inside a WPF UI Button's visual tree and sets its CornerRadius.
    /// </summary>
    private static void SetInternalCornerRadius(Control control, CornerRadius radius)
    {
        // WPF UI button template uses a Border — find it in the visual tree
        var border = FindVisualChild<Border>(control);
        if (border != null)
            border.CornerRadius = radius;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
                return result;
            var found = FindVisualChild<T>(child);
            if (found != null)
                return found;
        }
        return null;
    }

    // ── Area image lookup ───────────────────────────────────────────

    // Override only for AreaIds with no substring/prefix/suffix relation to image name
    private static readonly Dictionary<string, string> AreaImageOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SwimmingPool"] = "PoolArea",
        ["JapaneseGarden"] = "StoneGarden",
        ["DogArea"] = "RufusPark",
        ["AntiqueDealer"] = "TheGate",
        ["BeachRight"] = "BeachHouse",
        ["ShackExterior"] = "Beach",
        ["BeachBar"] = "BeachJuiceBar",
        ["MansionRightFiller"] = "SideFiller",
    };

    // Runtime cache: image key → full file path (rebuilt on directory change)
    private Dictionary<string, string>? _areaImageCache;
    private string? _areaImageCacheDir;
    private Dictionary<string, string>? _areaIconMapping; // from AreaIconsLibrary bundle

    /// <summary>
    /// Finds the area background image in the exported PNGs folder.
    /// Scans available Area_InfoPopupBg_*.png files and matches via
    /// multi-strategy resolution (override → direct → suffix → prefix → possessive).
    /// </summary>
    private string? FindAreaImagePath(LuaArea area)
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version)) return null;

        var exportDir = Path.Combine(basePath, version, "Export - PNGs");
        if (!Directory.Exists(exportDir)) return null;

        // Build/rebuild cache of available image keys → file paths
        if (_areaImageCache == null || !string.Equals(_areaImageCacheDir, exportDir, StringComparison.OrdinalIgnoreCase))
        {
            _areaImageCacheDir = exportDir;
            _areaImageCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(exportDir, "Area_Info*PopupBg_*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var idx = name.LastIndexOf("PopupBg_", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    _areaImageCache.TryAdd(name[(idx + 8)..], file);
            }

            // Load AreaId → sprite mapping from AreaIconsLibrary bundle (authoritative)
            if (_areaIconMapping == null)
            {
                var apkDir = Path.Combine(basePath, version, "Game Files", "APK");
                var tpkPath = Path.Combine(basePath, "classdata.tpk");
                if (!File.Exists(tpkPath))
                    tpkPath = Path.Combine(basePath, version, "classdata.tpk");
                if (Directory.Exists(apkDir) && File.Exists(tpkPath))
                    _areaIconMapping = AssetExtractionService.ExtractAreaIconMapping(apkDir, tpkPath);
            }
        }

        var imageKey = ResolveImageKey(area.AreaId);
        return imageKey != null && _areaImageCache.TryGetValue(imageKey, out var path) ? path : null;
    }

    /// <summary>
    /// Multi-strategy resolution: AreaId → image key in Area_InfoPopupBg_{key}.png.
    /// Handles prefix/suffix stripping, possessive 's', etc. automatically.
    /// </summary>
    private string? ResolveImageKey(string areaId)
    {
        if (_areaImageCache == null) return null;

        // 0. Bundle-extracted mapping (AreaIconsLibrary — authoritative)
        if (_areaIconMapping != null && _areaIconMapping.TryGetValue(areaId, out var bundleKey))
            if (_areaImageCache.ContainsKey(bundleKey)) return bundleKey;

        // 1. Explicit override (fallback for missing bundle data)
        if (AreaImageOverrides.TryGetValue(areaId, out var over))
            return _areaImageCache.ContainsKey(over) ? over : null;

        // 2. Direct match
        if (_areaImageCache.ContainsKey(areaId))
            return areaId;

        // 3. Strip trailing digits: "GardenRight20" → "GardenRight"
        var stripped = areaId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (stripped.Length > 0 && stripped != areaId && _areaImageCache.ContainsKey(stripped))
            return stripped;

        // 4. Image name is longest suffix of AreaId: "MansionPlaza" → "Plaza"
        string? best = null;
        foreach (var img in _areaImageCache.Keys)
            if (img.Length >= 3 && areaId.EndsWith(img, StringComparison.OrdinalIgnoreCase) &&
                (best == null || img.Length > best.Length))
                best = img;
        if (best != null) return best;

        // 5. AreaId is suffix of image name: "Hideout" → "SecretHideout"
        best = null;
        foreach (var img in _areaImageCache.Keys)
            if (areaId.Length >= 3 && img.EndsWith(areaId, StringComparison.OrdinalIgnoreCase) &&
                (best == null || img.Length < best.Length))
                best = img;
        if (best != null) return best;

        // 6. Image name is longest prefix of AreaId: "SaunaBurn" → "Sauna", "BBQArea" → "BBQ"
        best = null;
        foreach (var img in _areaImageCache.Keys)
            if (img.Length >= 3 && areaId.StartsWith(img, StringComparison.OrdinalIgnoreCase) &&
                (best == null || img.Length > best.Length))
                best = img;
        if (best != null) return best;

        // 7. Possessive 's': "DebRoom" → "DebsRoom", "GrandmaRoom" → "GrandmasRoom"
        foreach (var img in _areaImageCache.Keys)
        {
            if (img.Length != areaId.Length + 1) continue;
            for (int i = 1; i < img.Length; i++)
            {
                if (char.ToLowerInvariant(img[i]) != 's') continue;
                if (string.Concat(img.AsSpan(0, i), img.AsSpan(i + 1))
                        .Equals(areaId, StringComparison.OrdinalIgnoreCase))
                    return img;
            }
        }

        return null;
    }

    private void ShowInfo(string message, InfoBarSeverity severity, bool persistent = false)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;

        if (!persistent && severity != InfoBarSeverity.Error)
            _ = AutoCloseInfo();
    }

    private async Task AutoCloseInfo()
    {
        await Task.Delay(4000);
        infoBar.IsOpen = false;
    }
}
