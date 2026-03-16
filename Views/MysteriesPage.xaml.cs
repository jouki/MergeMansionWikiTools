using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public partial class MysteriesPage : UserControl
{
    private readonly MainWindow _main;
    private MysteryService? _mysteryService;
    private MysteryItemMapping? _itemMapping;
    private DialogueService? _dialogueService;
    private bool _loaded;

    public MysteriesPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        _itemMapping = MysteryService.LoadMapping();

        // Use pre-loaded MysteryService from MainWindow (loaded during splash screen)
        TryUsePreloaded();

        // Load pet display names from Pets.json
        MysteryWikiService.LoadPetDisplayNames(
            _main.Settings.ImageExporterBasePath, _main.Settings.SelectedApkVersion);

        // Auto-load dialogues.json if it exists alongside events.json
        _ = TryLoadDialoguesAsync();

        // Auto-reload when events.json path changes
        _main.EventsFileChanged += () => Dispatcher.InvokeAsync(() =>
        {
            _loaded = false;
            _mysteryService = null;
            _dialogueService = null;
            mysteryListPanel.Children.Clear();
            TryUsePreloaded();
            _ = TryLoadDialoguesAsync();
        });
    }

    private async Task TryLoadDialoguesAsync()
    {
        // Search for dialogues.json in multiple locations
        var candidates = new List<string>();

        // 1. Same directory as events.json
        var eventsPath = _main.Settings.EventsJsonPath;
        if (!string.IsNullOrEmpty(eventsPath))
        {
            var dir = Path.GetDirectoryName(eventsPath);
            if (!string.IsNullOrEmpty(dir))
                candidates.Add(Path.Combine(dir, "dialogues.json"));
        }

        // 2. Dumper output directory
        if (!string.IsNullOrEmpty(_main.Settings.DumperOutputPath))
            candidates.Add(Path.Combine(_main.Settings.DumperOutputPath, "dialogues.json"));

        // 3. APK version Dump folder
        if (!string.IsNullOrEmpty(_main.Settings.ImageExporterBasePath)
            && !string.IsNullOrEmpty(_main.Settings.SelectedApkVersion))
        {
            var dumpDir = Path.Combine(_main.Settings.ImageExporterBasePath,
                _main.Settings.SelectedApkVersion, "Dump");
            candidates.Add(Path.Combine(dumpDir, "dialogues.json"));
        }

        AppLogger.Info($"DialogueService: searching {candidates.Count} candidates: {string.Join(", ", candidates)}");
        var dialoguesPath = candidates.FirstOrDefault(File.Exists);
        if (dialoguesPath == null)
        {
            AppLogger.Warn("DialogueService: dialogues.json not found in any candidate path");
            return;
        }

        try
        {
            _dialogueService = new DialogueService();
            await _dialogueService.LoadAsync(dialoguesPath);
            AppLogger.Info($"DialogueService loaded from: {dialoguesPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to load dialogues.json: {ex.Message}");
            _dialogueService = null;
        }
    }

    // ── Loading ───────────────────────────────────────────────────

    private void TryUsePreloaded()
    {
        if (_main.MysteryService != null && _main.MysteryService.Mysteries.Count > 0)
        {
            _mysteryService = _main.MysteryService;

            // Apply item mapping overrides (MainWindow doesn't know about user overrides)
            if (_main.DataService != null)
                _mysteryService.ResolveRewardItems(_main.DataService, _main.WikiMapping, _itemMapping);

            _loaded = true;
            emptyState.Visibility = Visibility.Collapsed;
            BuildMysteryList();
            return;
        }

        // Fallback: load from scratch if MainWindow didn't pre-load
        _ = TryLoadAsync();
    }

    private async Task TryLoadAsync()
    {
        var path = _main.Settings.EventsJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            emptyState.Visibility = Visibility.Visible;
            txtSummary.Text = "";
            return;
        }

        emptyState.Visibility = Visibility.Collapsed;

        try
        {
            _mysteryService = new MysteryService();
            await _mysteryService.LoadAsync(path);

            // Resolve items if DataService is available
            if (_main.DataService != null)
            {
                _mysteryService.ResolveEventItems(_main.DataService);
                _mysteryService.ResolveRewardItems(_main.DataService, _main.WikiMapping, _itemMapping);
            }

            _main.MysteryService = _mysteryService;
            _loaded = true;
            BuildMysteryList();
        }
        catch (Exception ex)
        {
            ShowInfo($"Failed to load events.json: {ex.Message}", InfoBarSeverity.Error);
            emptyState.Visibility = Visibility.Visible;
        }
    }

    // ── List building ─────────────────────────────────────────────

    private void BuildMysteryList()
    {
        mysteryListPanel.Children.Clear();

        if (_mysteryService == null || _mysteryService.Mysteries.Count == 0)
        {
            emptyState.Visibility = Visibility.Visible;
            txtSummary.Text = "";
            return;
        }

        emptyState.Visibility = Visibility.Collapsed;

        var search = txtSearch.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(search)
            ? _mysteryService.Mysteries
            : _mysteryService.Mysteries
                .Where(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            (m.EventItemName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
                            m.ProgressionEventId.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

        // Summary
        var total = _mysteryService.Mysteries.Count;
        var standard = _mysteryService.Mysteries.Count(m => m.MysteryType == MysteryType.Standard);
        var pet = _mysteryService.Mysteries.Count(m => m.MysteryType == MysteryType.Pet);
        txtSummary.Text = $"{total} mysteries · {standard} Standard · {pet} Pet" +
                          (filtered.Count != total ? $" · {filtered.Count} shown" : "");

        if (filtered.Count == 0)
        {
            var noResults = new TextBlock
            {
                Text = "No mysteries match your search.",
                FontSize = 13,
                Margin = new Thickness(4, 20, 0, 0)
            };
            noResults.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            mysteryListPanel.Children.Add(noResults);
            return;
        }

        // Group by year with separators
        int? lastYear = null;
        foreach (var mystery in filtered)
        {
            int? year = mystery.StartDate?.Year;
            if (year != lastYear)
            {
                var header = new TextBlock
                {
                    Text = year?.ToString() ?? "Unknown date",
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, lastYear == null ? 0 : 16, 0, 8)
                };
                header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                mysteryListPanel.Children.Add(header);

                // Separator line
                var line = new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
                mysteryListPanel.Children.Add(line);

                lastYear = year;
            }

            var card = CreateMysteryCard(mystery);
            mysteryListPanel.Children.Add(card);
        }
    }

    private Border CreateMysteryCard(MysteryEvent mystery)
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: info
        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // Name + type badge row
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        var nameText = new TextBlock
        {
            Text = mystery.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        nameRow.Children.Add(nameText);

        // Type badge
        var typeBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        typeBadge.SetResourceReference(Border.BackgroundProperty,
            mystery.MysteryType == MysteryType.Pet
                ? "AccentFillColorDefaultBrush"
                : "SubtleFillColorSecondaryBrush");
        var typeBadgeText = new TextBlock
        {
            Text = mystery.MysteryType == MysteryType.Pet ? "Pet" : "Standard",
            FontSize = 11
        };
        typeBadgeText.SetResourceReference(TextBlock.ForegroundProperty,
            mystery.MysteryType == MysteryType.Pet
                ? "TextOnAccentFillColorPrimaryBrush"
                : "TextFillColorSecondaryBrush");
        typeBadge.Child = typeBadgeText;
        nameRow.Children.Add(typeBadge);

        infoPanel.Children.Add(nameRow);

        // Meta line: date + event item
        var dateStr = mystery.StartDate?.ToString("MMM d, yyyy") ?? "Unknown date";
        var itemStr = mystery.EventItemName ?? "Unknown item";
        var metaText = new TextBlock
        {
            Text = $"{dateStr} · Event Item: {itemStr}",
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        metaText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        infoPanel.Children.Add(metaText);

        // Status line (wiki status indicators — 3-state: green/yellow/red)
        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var pageState = mystery.WikiStatus.EventPageState;
        if (pageState != WikiCheckState.Unknown)
        {
            var pageInd = CreateStatusIndicator("Page", pageState);
            pageInd.Cursor = System.Windows.Input.Cursors.Hand;
            pageInd.Tag = (mystery, MysteryDiffScope.EventPage);
            pageInd.MouseLeftButtonDown += StatusIndicator_Click;
            ToolTipService.SetInitialShowDelay(pageInd, 0);
            pageInd.ToolTip = "Click to diff Event Page";
            statusPanel.Children.Add(pageInd);
        }

        var rewardState = mystery.WikiStatus.RewardTemplateState;
        if (rewardState != WikiCheckState.Unknown)
        {
            var tmplText = !string.IsNullOrEmpty(mystery.WikiStatus.MatchingVariant)
                ? $"Rewards ({mystery.WikiStatus.MatchingVariant})"
                : "Rewards";
            var rewardInd = CreateStatusIndicator(tmplText, rewardState);
            rewardInd.Cursor = System.Windows.Input.Cursors.Hand;
            rewardInd.Tag = (mystery, MysteryDiffScope.Rewards);
            rewardInd.MouseLeftButtonDown += StatusIndicator_Click;
            ToolTipService.SetInitialShowDelay(rewardInd, 0);
            rewardInd.ToolTip = "Click to diff Rewards";
            statusPanel.Children.Add(rewardInd);
        }

        var itemState = mystery.WikiStatus.EventItemPageState;
        if (itemState != WikiCheckState.Unknown)
        {
            var itemInd = CreateStatusIndicator("Item", itemState);
            itemInd.Cursor = System.Windows.Input.Cursors.Hand;
            itemInd.Tag = (mystery, MysteryDiffScope.EventItemPage);
            itemInd.MouseLeftButtonDown += StatusIndicator_Click;
            ToolTipService.SetInitialShowDelay(itemInd, 0);
            itemInd.ToolTip = "Click to diff Event Item Page";
            statusPanel.Children.Add(itemInd);
        }

        // Images indicator
        var imagesState = mystery.WikiStatus.ImagesState;
        if (imagesState != WikiCheckState.Unknown)
        {
            var imagesLabel = mystery.WikiStatus.ImagesExistOnWiki > 0
                ? $"Images ({mystery.WikiStatus.ImagesExistOnWiki}/{mystery.WikiStatus.ImagesTotalExpected})"
                : "Images";
            var imagesInd = CreateStatusIndicator(imagesLabel, imagesState);
            imagesInd.Cursor = System.Windows.Input.Cursors.Hand;
            imagesInd.Tag = (mystery, MysteryDiffScope.EventPage); // opens Prepare on Images tab
            imagesInd.MouseLeftButtonDown += (s, _) =>
            {
                if (s is not Border b) return;
                var (m, _) = ((MysteryEvent, MysteryDiffScope))b.Tag;
                var dlg = new MysteryGeneratorDialog(_main, m, _itemMapping,
                    MysteryGeneratorMode.Images, _dialogueService);
                dlg.Owner = Window.GetWindow(this);
                dlg.Show();
            };
            ToolTipService.SetInitialShowDelay(imagesInd, 0);
            imagesInd.ToolTip = "Click to open Images";
            statusPanel.Children.Add(imagesInd);
        }

        if (statusPanel.Children.Count > 0)
            infoPanel.Children.Add(statusPanel);

        Grid.SetColumn(infoPanel, 0);
        grid.Children.Add(infoPanel);

        // Right: action buttons
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // "Prepare" button — opens the unified mystery editor dialog
        var btnPrepare = new Wpf.Ui.Controls.Button
        {
            Content = "Prepare",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Tag = mystery
        };
        btnPrepare.Click += BtnPrepare_Click;
        btnPanel.Children.Add(btnPrepare);

        // Update Wiki button (accent)
        if (_main.Settings.WikiVerified)
        {
            var btnUpdateWiki = new Wpf.Ui.Controls.Button
            {
                Content = "Update Wiki",
                Appearance = ControlAppearance.Primary,
                Height = 32,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = mystery
            };
            btnUpdateWiki.Click += BtnUpdateWiki_Click;
            btnPanel.Children.Add(btnUpdateWiki);
        }

        Grid.SetColumn(btnPanel, 1);
        grid.Children.Add(btnPanel);

        border.Child = grid;
        return border;
    }

    private static Border CreateStatusIndicator(string label, WikiCheckState state)
    {
        // Green = match, Yellow = mismatch, Red = missing
        var (bg, fg, symbol) = state switch
        {
            WikiCheckState.Match => (
                new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xA0, 0x00)),
                new SolidColorBrush(Color.FromRgb(0x30, 0xC0, 0x30)),
                "\u2713 "),  // ✓
            WikiCheckState.Mismatch => (
                new SolidColorBrush(Color.FromArgb(0x30, 0xC0, 0x90, 0x00)),
                new SolidColorBrush(Color.FromRgb(0xD0, 0xA0, 0x20)),
                "\u26A0 "),  // ⚠
            _ => (
                new SolidColorBrush(Color.FromArgb(0x30, 0xD0, 0x00, 0x00)),
                new SolidColorBrush(Color.FromRgb(0xD0, 0x40, 0x40)),
                "\u2717 ")   // ✗
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 6, 0),
            Background = bg
        };

        var text = new TextBlock { FontSize = 10, Foreground = fg };
        text.Inlines.Add(new System.Windows.Documents.Run(symbol));
        text.Inlines.Add(new System.Windows.Documents.Run(label));

        border.Child = text;
        return border;
    }

    // ── Event handlers ────────────────────────────────────────────

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded) BuildMysteryList();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _mysteryService = null;
        mysteryListPanel.Children.Clear();

        // Clear status cache so everything gets re-checked
        MysteryWikiService.ClearStatusCache();

        await TryLoadAsync();
        ShowInfo("Mysteries reloaded. Click 'Check Wiki Status' to re-check all labels.", InfoBarSeverity.Success);
    }

    private async void BtnCheckWiki_Click(object sender, RoutedEventArgs e)
    {
        if (_mysteryService == null || _mysteryService.Mysteries.Count == 0)
        {
            ShowInfo("No mysteries loaded.", InfoBarSeverity.Warning);
            return;
        }

        btnCheckWiki.IsEnabled = false;
        ShowInfo("Checking wiki status...", InfoBarSeverity.Informational, autoClose: false);

        try
        {
            // Full batch check: page existence + template comparison
            await MysteryWikiService.CheckAllMysteryStatusAsync(
                _mysteryService.Mysteries, _main.DataService, _dialogueService);

            BuildMysteryList();
            ShowInfo("Wiki status checked.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Wiki check failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnCheckWiki.IsEnabled = true;
        }
    }

    private void BtnPrepare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not MysteryEvent mystery) return;

        var dialog = new MysteryGeneratorDialog(_main, mystery, _itemMapping,
            MysteryGeneratorMode.EventPage, _dialogueService);
        dialog.Owner = Window.GetWindow(this);
        dialog.Show();
    }

    private void StatusIndicator_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        var (mystery, scope) = ((MysteryEvent, MysteryDiffScope))border.Tag;

        // Open the Prepare dialog on the matching tab
        var mode = scope switch
        {
            MysteryDiffScope.Rewards => MysteryGeneratorMode.Rewards,
            MysteryDiffScope.EventPage => MysteryGeneratorMode.EventPage,
            MysteryDiffScope.EventItemPage => MysteryGeneratorMode.EventItemPage,
            _ => MysteryGeneratorMode.Rewards
        };

        var dialog = new MysteryGeneratorDialog(_main, mystery, _itemMapping, mode, _dialogueService);
        dialog.Owner = Window.GetWindow(this);
        dialog.Show();
    }

    private async void BtnUpdateWiki_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not MysteryEvent mystery) return;

        if (!_main.Settings.WikiVerified)
        {
            ShowInfo("Wiki account not verified.", InfoBarSeverity.Warning);
            return;
        }

        // Show preview/confirmation dialog
        btn.IsEnabled = false;
        ShowInfo("Fetching preview...", InfoBarSeverity.Informational, autoClose: false);

        try
        {
            var preview = await MysteryWikiService.PreviewWikiUpdatesAsync(mystery);

            var result = System.Windows.MessageBox.Show(
                preview + "\nProceed with updates?",
                "Update Wiki Pages — Confirm",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.OK)
            {
                btn.IsEnabled = true;
                infoBar.IsOpen = false;
                return;
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"Preview failed: {ex.Message}", InfoBarSeverity.Error);
            btn.IsEnabled = true;
            return;
        }

        ShowInfo("Updating wiki pages...", InfoBarSeverity.Informational, autoClose: false);

        var results = new List<string>();
        try
        {
            // Update main page
            try
            {
                var mainResult = await MysteryWikiService.UpdateMainPageAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword,
                    mystery.Name, mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name,
                    mystery.StartDate);
                results.Add($"Main page: {mainResult}");
            }
            catch (Exception ex) { results.Add($"Main page: {ex.Message}"); }

            // Update Mystery table
            try
            {
                var tableResult = await MysteryWikiService.UpdateMysteryPageTableAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword, mystery);
                results.Add($"Mystery page: {tableResult}");
            }
            catch (Exception ex) { results.Add($"Mystery page: {ex.Message}"); }

            // Update Module:Datatable/Various
            try
            {
                var moduleResult = await MysteryWikiService.UpdateMysteryTableAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword, mystery);
                results.Add($"Module: {moduleResult}");
            }
            catch (Exception ex) { results.Add($"Module: {ex.Message}"); }

            ShowInfo(string.Join(" | ", results), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Update failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
    {
        _main.NavigateToSettingsHighlightEvents();
    }

    // ── Status ────────────────────────────────────────────────────

    private void ShowInfo(string message, InfoBarSeverity severity, bool autoClose = true)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;

        if (autoClose && severity != InfoBarSeverity.Error)
            _ = AutoCloseInfo();
    }

    private async Task AutoCloseInfo()
    {
        await Task.Delay(4000);
        infoBar.IsOpen = false;
    }
}
