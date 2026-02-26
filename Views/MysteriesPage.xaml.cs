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
    private bool _loaded;

    public MysteriesPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        _itemMapping = MysteryService.LoadMapping();

        // Use pre-loaded MysteryService from MainWindow (loaded during splash screen)
        TryUsePreloaded();

        // Auto-reload when events.json path changes
        _main.EventsFileChanged += () => Dispatcher.InvokeAsync(() =>
        {
            _loaded = false;
            _mysteryService = null;
            mysteryListPanel.Children.Clear();
            TryUsePreloaded();
        });
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
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                Margin = new Thickness(4, 20, 0, 0)
            };
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
                    Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                    Margin = new Thickness(0, lastYear == null ? 0 : 16, 0, 8)
                };
                mysteryListPanel.Children.Add(header);

                // Separator line
                var line = new Border
                {
                    Height = 1,
                    Background = (Brush)FindResource("DividerStrokeColorDefaultBrush"),
                    Margin = new Thickness(0, 0, 0, 10)
                };
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
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(16, 12, 16, 12)
        };

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
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        nameRow.Children.Add(nameText);

        // Type badge
        var typeBadge = new Border
        {
            Background = mystery.MysteryType == MysteryType.Pet
                ? (Brush)FindResource("AccentFillColorDefaultBrush")
                : (Brush)FindResource("SubtleFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var typeBadgeText = new TextBlock
        {
            Text = mystery.MysteryType == MysteryType.Pet ? "Pet" : "Standard",
            FontSize = 11,
            Foreground = mystery.MysteryType == MysteryType.Pet
                ? (Brush)FindResource("TextOnAccentFillColorPrimaryBrush")
                : (Brush)FindResource("TextFillColorSecondaryBrush")
        };
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
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        infoPanel.Children.Add(metaText);

        // Status line (wiki status indicators — 3-state: green/yellow/red)
        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var pageState = mystery.WikiStatus.EventPageState;
        if (pageState != WikiCheckState.Unknown)
            statusPanel.Children.Add(CreateStatusIndicator("Page", pageState));

        var rewardState = mystery.WikiStatus.RewardTemplateState;
        if (rewardState != WikiCheckState.Unknown)
        {
            var tmplText = !string.IsNullOrEmpty(mystery.WikiStatus.MatchingVariant)
                ? $"Rewards ({mystery.WikiStatus.MatchingVariant})"
                : "Rewards";
            statusPanel.Children.Add(CreateStatusIndicator(tmplText, rewardState));
        }

        var itemState = mystery.WikiStatus.EventItemPageState;
        if (itemState != WikiCheckState.Unknown)
            statusPanel.Children.Add(CreateStatusIndicator("Item", itemState));

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

        var btnGenerate = new Wpf.Ui.Controls.Button
        {
            Content = "Generate Rewards",
            Appearance = ControlAppearance.Primary,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Tag = mystery
        };
        btnGenerate.Click += BtnGenerateRewards_Click;
        btnPanel.Children.Add(btnGenerate);

        var btnEventPage = new Wpf.Ui.Controls.Button
        {
            Content = "Event Page",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = mystery
        };
        btnEventPage.Click += BtnGenerateEventPage_Click;
        btnPanel.Children.Add(btnEventPage);

        var btnItemPage = new Wpf.Ui.Controls.Button
        {
            Content = "Item Page",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = mystery
        };
        btnItemPage.Click += BtnGenerateItemPage_Click;
        btnPanel.Children.Add(btnItemPage);

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
        await TryLoadAsync();
        ShowInfo("Mysteries reloaded.", InfoBarSeverity.Success);
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
                _mysteryService.Mysteries, _main.DataService);

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

    private void BtnGenerateRewards_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not MysteryEvent mystery) return;

        var dialog = new MysteryGeneratorDialog(_main, mystery, _itemMapping, MysteryGeneratorMode.Rewards);
        dialog.Owner = Window.GetWindow(this);
        dialog.Show();
    }

    private void BtnGenerateEventPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not MysteryEvent mystery) return;

        var dialog = new MysteryGeneratorDialog(_main, mystery, _itemMapping, MysteryGeneratorMode.EventPage);
        dialog.Owner = Window.GetWindow(this);
        dialog.Show();
    }

    private void BtnGenerateItemPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not MysteryEvent mystery) return;

        var dialog = new MysteryGeneratorDialog(_main, mystery, _itemMapping, MysteryGeneratorMode.EventItemPage);
        dialog.Owner = Window.GetWindow(this);
        dialog.Show();
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
