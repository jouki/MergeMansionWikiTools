using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public partial class AreaFlowchartsDevPage : UserControl
{
    private readonly MainWindow _main;
    private List<LuaArea>? _areas;
    private bool _areasLoaded;

    public AreaFlowchartsDevPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
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

        try
        {
            var service = new AreasService();
            await service.LoadAsync(path);
            _areas = service.Areas;
            _areasLoaded = true;
            BuildAreaList();
        }
        catch (Exception ex)
        {
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

        foreach (var area in filtered)
        {
            var card = CreateAreaCard(area, hasFolder);
            areaListPanel.Children.Add(card);
        }
    }

    private Border CreateAreaCard(LuaArea area, bool hasFolder)
    {
        var border = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(6),
            BorderBrush = (System.Windows.Media.Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(16, 12, 16, 12)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Area name + task count
        var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var nameText = new TextBlock
        {
            Text = area.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush")
        };
        namePanel.Children.Add(nameText);

        var taskCount = area.Tasks.Count(t => t.Requirements.Count > 0);
        var metaText = new TextBlock
        {
            Text = $"{taskCount} tasks with requirements · {area.Tasks.Count} total",
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        namePanel.Children.Add(metaText);

        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);

        // Open button
        var btnOpen = new Wpf.Ui.Controls.Button
        {
            Content = "Open",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Tag = area
        };
        btnOpen.Click += BtnOpenSvg_Click;
        Grid.SetColumn(btnOpen, 1);
        grid.Children.Add(btnOpen);

        // Generate button (split when folder is configured)
        var genContainer = new Grid { Margin = new Thickness(8, 0, 0, 0) };
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

        if (hasFolder)
        {
            // Thin separator between main button and chevron
            var sep = new Border
            {
                Width = 1,
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 5, 0, 5)
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
                Content = new SymbolIcon { Symbol = SymbolRegular.ChevronDown24, FontSize = 12 }
            };
            btnChevron.Click += BtnGenerateMenu_Click;
            Grid.SetColumn(btnChevron, 2);
            genContainer.Children.Add(btnChevron);

            ApplySplitButtonStyle(btnGen, btnChevron);
        }

        Grid.SetColumn(genContainer, 2);
        grid.Children.Add(genContainer);

        // Check if SVG already exists — show Open button
        var outputPath = GetOutputFilePath(area);
        if (outputPath != null && File.Exists(outputPath))
            btnOpen.Visibility = Visibility.Visible;

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

            // Show Open button in the card
            ShowOpenButtonInCard(btn);
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
                await GenerateFlowchartAsync(area);
                count++;
            }

            ShowInfo($"Generated {count} flowcharts.\n\u2192 {outputDir}", InfoBarSeverity.Success, persistent: true);

            // Refresh list to show Open buttons
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

    /// <summary>
    /// Chevron click on per-area split button — shows "Save to..." context menu.
    /// </summary>
    private void BtnGenerateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn) return;
        var area = btn.Tag as LuaArea;

        var menu = new ContextMenu();
        var item = new System.Windows.Controls.MenuItem { Header = "Save to..." };
        item.Click += async (_, _) =>
        {
            if (area != null) await GenerateToCustomFolder(area, btn);
        };
        menu.Items.Add(item);

        // Align menu right-edge with the split button group (opens left)
        var target = btn.Parent as FrameworkElement ?? btn;
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Opened += (_, _) =>
            menu.HorizontalOffset = target.ActualWidth - menu.ActualWidth;
        menu.IsOpen = true;
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

        // Align menu right-edge with the split button group (opens left)
        var target = btnGenerateAllMenu.Parent as FrameworkElement ?? btnGenerateAllMenu;
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Opened += (_, _) =>
            menu.HorizontalOffset = target.ActualWidth - menu.ActualWidth;
        menu.IsOpen = true;
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

            var svg = await Task.Run(() => FlowchartService3.GenerateSvg(area, _main.DataService));
            if (string.IsNullOrEmpty(svg))
                throw new InvalidOperationException($"No tasks with requirements found in {area.DisplayName}.");

            await File.WriteAllTextAsync(outputPath, svg);
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

                var svg = await Task.Run(() => FlowchartService3.GenerateSvg(area, _main.DataService));
                if (!string.IsNullOrEmpty(svg))
                {
                    await File.WriteAllTextAsync(outputPath, svg);
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

        var svg = await Task.Run(() =>
            FlowchartService3.GenerateSvg(area, _main.DataService));

        if (string.IsNullOrEmpty(svg))
            throw new InvalidOperationException($"No tasks with requirements found in {area.DisplayName}.");

        await File.WriteAllTextAsync(outputPath, svg);

        // Debug mode: also save next to .exe
        if (_main.Settings.DebugMode)
            WriteDebugCopy(area, svg);
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

    private static string? BrowseOutputFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Select flowchart output folder" };
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

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetOutputDir();
        if (dir != null && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
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
        var path = _main.Settings.FlowchartOutputPath;
        return string.IsNullOrEmpty(path) ? null : path;
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
    /// Finds the Open button in the same card as the given Generate button and makes it visible.
    /// </summary>
    private static void ShowOpenButtonInCard(Wpf.Ui.Controls.Button genBtn)
    {
        // genBtn is inside genContainer Grid → inside card Grid → find Open button
        if (genBtn.Parent is Grid genContainer && genContainer.Parent is Grid cardGrid)
        {
            var openBtn = cardGrid.Children.OfType<Wpf.Ui.Controls.Button>()
                .FirstOrDefault(b => b.Content?.ToString() == "Open");
            if (openBtn != null) openBtn.Visibility = Visibility.Visible;
        }
    }

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
