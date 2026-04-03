using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Data passed from ImageOptimiserPage into the dialog for each file to upload.
/// </summary>
public class WikiUploadItem
{
    public string FilePath { get; set; } = "";
    public string? DetectedChainName { get; set; }
    public bool IsPartOfSplitGroup { get; set; }
    public string? SplitGroupSourcePath { get; set; }
    public bool IsOptimized { get; set; }
}

/// <summary>
/// Internal per-row state managed by the dialog.
/// </summary>
internal class UploadRow
{
    public string FilePath { get; set; } = "";
    public string? ChainName { get; set; }
    public string? ResolvedWikiFilename { get; set; }
    public bool FileExistsOnWiki { get; set; }
    public bool ForceUpdate { get; set; }
    public bool SuppressLevel { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsChecking { get; set; }
    public bool IsPartOfSplit { get; set; }

    // UI references
    public Wpf.Ui.Controls.TextBox? ChainNameTextBox { get; set; }
    public Popup? SuggestPopup { get; set; }
    public ListBox? SuggestList { get; set; }
    public TextBlock? FilenameLabel { get; set; }
    public Wpf.Ui.Controls.SymbolIcon? ExistsIcon { get; set; }
    public CheckBox? ForceCheckBox { get; set; }
    public CheckBox? EnabledCheckBox { get; set; }
    public Border? CardBorder { get; set; }
    public Grid? ContentGrid { get; set; }
}

public partial class WikiUploadDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly List<WikiUploadItem> _items;
    private readonly List<UploadRow> _rows = new();

    // ── Pre-built data for instant autocomplete ──
    private readonly string[] _allNames;
    private readonly string[] _allNamesLower;
    private readonly Dictionary<string, ParsedChain> _chainByName;
    private const int MaxSuggestions = 30;

    private bool _suppressTextChanged;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _resolveCts;
    private CancellationTokenSource? _uploadCts;
    private bool _isUploading;

    public int UploadedCount { get; private set; }

    public WikiUploadDialog(MainWindow main, List<WikiUploadItem> items)
    {
        _main = main;
        _items = items;

        // Pre-build lookup structures
        var chains = main.DataService?.Chains ?? new List<ParsedChain>();
        _chainByName = new Dictionary<string, ParsedChain>(StringComparer.OrdinalIgnoreCase);
        var nameSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chains)
        {
            nameSet.Add(c.DisplayName);
            _chainByName.TryAdd(c.DisplayName, c);
        }
        _allNames = nameSet.ToArray();
        _allNamesLower = _allNames.Select(n => n.ToLowerInvariant()).ToArray();

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        BuildUploadRows();
    }

    // ══════════════════════════════════════════════════════════════
    //  BUILD UI ROWS
    // ══════════════════════════════════════════════════════════════

    private void BuildUploadRows()
    {
        uploadRowsPanel.Children.Clear();
        _rows.Clear();

        // Group split items by their source path
        var groups = new List<List<WikiUploadItem>>();
        var currentGroup = new List<WikiUploadItem>();
        string? currentSource = null;

        foreach (var item in _items)
        {
            if (item.IsPartOfSplitGroup)
            {
                if (item.SplitGroupSourcePath != currentSource)
                {
                    if (currentGroup.Count > 0) groups.Add(currentGroup);
                    currentGroup = new List<WikiUploadItem>();
                    currentSource = item.SplitGroupSourcePath;
                }
                currentGroup.Add(item);
            }
            else
            {
                if (currentGroup.Count > 0) groups.Add(currentGroup);
                currentGroup = new List<WikiUploadItem> { item };
                currentSource = null;
                groups.Add(currentGroup);
                currentGroup = new List<WikiUploadItem>();
            }
        }
        if (currentGroup.Count > 0) groups.Add(currentGroup);

        foreach (var group in groups)
        {
            if (group.Count == 1 && !group[0].IsPartOfSplitGroup)
                BuildSingleRow(group[0]);
            else
                BuildSplitGroupRows(group);
        }
    }

    private void BuildSingleRow(WikiUploadItem item)
    {
        var row = new UploadRow
        {
            FilePath = item.FilePath,
            ChainName = item.DetectedChainName,
            IsEnabled = item.IsOptimized
        };
        _rows.Add(row);

        var card = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        row.CardBorder = card;

        var grid = new Grid();
        row.ContentGrid = grid;
        // Col: 0=enable checkbox, 1=thumb, 2=chain autocomplete, 3=wiki filename, 4=status+suppress
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });          // enable
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });       // thumbnail
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // chain name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // wiki filename
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });          // status

        if (item.IsOptimized)
        {
            // Enable checkbox
            var enableCheck = new CheckBox
            {
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = row
            };
            enableCheck.Checked += (_, _) => SetRowEnabled(row, true);
            enableCheck.Unchecked += (_, _) => SetRowEnabled(row, false);
            row.EnabledCheckBox = enableCheck;
            Grid.SetColumn(enableCheck, 0);
            grid.Children.Add(enableCheck);
        }
        else
        {
            // Warning icon for non-optimized files
            var warnIcon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = SymbolRegular.Warning24,
                FontSize = 18,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 10, 0),
                ToolTip = "This image must be optimised before uploading to the wiki"
            };
            ToolTipService.SetInitialShowDelay(warnIcon, 0);
            Grid.SetColumn(warnIcon, 0);
            grid.Children.Add(warnIcon);
        }

        // Thumbnail
        var thumb = LoadThumbnail(item.FilePath, 40);
        var img = new System.Windows.Controls.Image
        {
            Source = thumb,
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(img, 1);
        grid.Children.Add(img);

        // Chain name TextBox + autocomplete popup
        var acGrid = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(acGrid, 2);

        var txtChain = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Chain name...",
            VerticalAlignment = VerticalAlignment.Center,
            Tag = row
        };
        row.ChainNameTextBox = txtChain;
        txtChain.TextChanged += TxtChain_TextChanged;
        txtChain.PreviewKeyDown += TxtChain_PreviewKeyDown;
        acGrid.Children.Add(txtChain);

        var (popup, listBox) = CreateAutocompletePopup(txtChain);
        row.SuggestPopup = popup;
        row.SuggestList = listBox;
        acGrid.Children.Add(popup);

        grid.Children.Add(acGrid);

        // Wiki filename label
        var filenameLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            Text = "—",
            Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(filenameLabel, 3);
        row.FilenameLabel = filenameLabel;
        grid.Children.Add(filenameLabel);

        // Status panel: exists icon + suppress level + force update
        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(statusPanel, 4);

        var existsIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = SymbolRegular.Circle24,
            FontSize = 16,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            Margin = new Thickness(0, 0, 6, 0)
        };
        row.ExistsIcon = existsIcon;
        statusPanel.Children.Add(existsIcon);

        var forceCheck = new CheckBox
        {
            Content = "Force",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Overwrite existing file on wiki"
        };
        ToolTipService.SetInitialShowDelay(forceCheck, 0);
        forceCheck.Checked += (_, _) => row.ForceUpdate = true;
        forceCheck.Unchecked += (_, _) => row.ForceUpdate = false;
        row.ForceCheckBox = forceCheck;
        statusPanel.Children.Add(forceCheck);

        var suppressCheck = new CheckBox
        {
            Content = "No level",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Upload without level suffix (e.g. MagicFlower.png instead of MagicFlower01.png)",
            Tag = row
        };
        ToolTipService.SetInitialShowDelay(suppressCheck, 0);
        suppressCheck.Checked += (_, _) => { row.SuppressLevel = true; _ = ResolveAndCheckAsync(row); };
        suppressCheck.Unchecked += (_, _) => { row.SuppressLevel = false; _ = ResolveAndCheckAsync(row); };
        statusPanel.Children.Add(suppressCheck);

        grid.Children.Add(statusPanel);
        card.Child = grid;
        uploadRowsPanel.Children.Add(card);

        // Grey out non-optimized rows
        if (!item.IsOptimized)
        {
            foreach (UIElement child in grid.Children)
            {
                if (Grid.GetColumn(child) > 0) // skip col 0 (warning icon)
                {
                    child.Opacity = 0.35;
                    child.IsHitTestVisible = false;
                }
            }
        }

        // Pre-fill detected chain name
        if (!string.IsNullOrEmpty(item.DetectedChainName))
        {
            _suppressTextChanged = true;
            txtChain.Text = item.DetectedChainName;
            _suppressTextChanged = false;
            _ = ResolveAndCheckAsync(row);
        }
    }

    private void BuildSplitGroupRows(List<WikiUploadItem> groupItems)
    {
        // Sort by level (last 2 digits of filename before extension)
        groupItems.Sort((a, b) =>
        {
            var nameA = System.IO.Path.GetFileNameWithoutExtension(a.FilePath);
            var nameB = System.IO.Path.GetFileNameWithoutExtension(b.FilePath);
            int lvA = (nameA.Length >= 2 && char.IsDigit(nameA[^1]) && char.IsDigit(nameA[^2]))
                ? int.Parse(nameA[^2..]) : 0;
            int lvB = (nameB.Length >= 2 && char.IsDigit(nameB[^1]) && char.IsDigit(nameB[^2]))
                ? int.Parse(nameB[^2..]) : 0;
            return lvA.CompareTo(lvB);
        });

        // Header card with shared chain name autocomplete + group-level suppress toggle
        var headerCard = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 2)
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // group enable
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // label
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // autocomplete
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // suppress level

        // Group-level enable checkbox
        var groupEnableCheck = new CheckBox
        {
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Enable/disable all files in this group"
        };
        ToolTipService.SetInitialShowDelay(groupEnableCheck, 0);
        Grid.SetColumn(groupEnableCheck, 0);
        headerGrid.Children.Add(groupEnableCheck);

        var label = new TextBlock
        {
            Text = $"Split group ({groupItems.Count} files):",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(label, 1);
        headerGrid.Children.Add(label);

        // Shared autocomplete
        var acGrid = new Grid();
        Grid.SetColumn(acGrid, 2);

        // Create a "header row" that all sub-rows share
        var headerRow = new UploadRow
        {
            ChainName = groupItems[0].DetectedChainName
        };

        var txtChain = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Chain name for all...",
            VerticalAlignment = VerticalAlignment.Center,
            Tag = headerRow
        };
        headerRow.ChainNameTextBox = txtChain;
        acGrid.Children.Add(txtChain);

        var (popup, listBox) = CreateAutocompletePopup(txtChain);
        headerRow.SuggestPopup = popup;
        headerRow.SuggestList = listBox;
        acGrid.Children.Add(popup);

        headerGrid.Children.Add(acGrid);

        // Group-level suppress level checkbox
        var groupSuppressCheck = new CheckBox
        {
            Content = "No level (all)",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Upload all files in this group without level suffix"
        };
        ToolTipService.SetInitialShowDelay(groupSuppressCheck, 0);
        Grid.SetColumn(groupSuppressCheck, 3);
        headerGrid.Children.Add(groupSuppressCheck);

        headerCard.Child = headerGrid;
        uploadRowsPanel.Children.Add(headerCard);

        // Sub-rows for each split file
        var subRows = new List<UploadRow>();
        var perRowChainNameCols = new List<ColumnDefinition>();
        var perRowChainNameGrids = new List<Grid>();

        foreach (var item in groupItems)
        {
            var row = new UploadRow
            {
                FilePath = item.FilePath,
                ChainName = item.DetectedChainName,
                IsEnabled = item.IsOptimized,
                IsPartOfSplit = true
            };
            _rows.Add(row);
            subRows.Add(row);

            var subCard = new Border
            {
                Background = (Brush)FindResource("SubtleFillColorSecondaryBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(20, 0, 0, 2) // indented
            };
            row.CardBorder = subCard;

            var subGrid = new Grid();
            row.ContentGrid = subGrid;
            // Col: 0=enable, 1=level badge, 2=thumb, 3=individual chain name (hidden), 4=filename, 5=status
            subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: enable
            subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1: level badge
            subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) }); // 2: thumb
            var chainNameCol = new ColumnDefinition { Width = GridLength.Auto }; // 3: individual chain name (hidden by default)
            subGrid.ColumnDefinitions.Add(chainNameCol);
            perRowChainNameCols.Add(chainNameCol);
            subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 4: filename
            subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 5: exists

            if (item.IsOptimized)
            {
                // Enable checkbox
                var enableCheck = new CheckBox
                {
                    IsChecked = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Tag = row
                };
                enableCheck.Checked += (_, _) => SetRowEnabled(row, true);
                enableCheck.Unchecked += (_, _) => SetRowEnabled(row, false);
                row.EnabledCheckBox = enableCheck;
                Grid.SetColumn(enableCheck, 0);
                subGrid.Children.Add(enableCheck);
            }
            else
            {
                // Warning icon for non-optimized files
                var warnIcon = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = SymbolRegular.Warning24,
                    FontSize = 16,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 8, 0),
                    ToolTip = "This image must be optimised before uploading to the wiki"
                };
                ToolTipService.SetInitialShowDelay(warnIcon, 0);
                Grid.SetColumn(warnIcon, 0);
                subGrid.Children.Add(warnIcon);
            }

            // Level badge from filename
            var fileName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
            var levelText = "";
            if (fileName.Length >= 2 && char.IsDigit(fileName[^1]) && char.IsDigit(fileName[^2]))
                levelText = $"Lv{fileName[^2..]}";

            var lvBadge = new TextBlock
            {
                Text = levelText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 40,
                Margin = new Thickness(0, 0, 4, 0)
            };
            Grid.SetColumn(lvBadge, 1);
            subGrid.Children.Add(lvBadge);

            // Small thumbnail
            var thumb = LoadThumbnail(item.FilePath, 30);
            var thumbImg = new System.Windows.Controls.Image
            {
                Source = thumb,
                Width = 36,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(thumbImg, 2);
            subGrid.Children.Add(thumbImg);

            // Individual chain name TextBox + autocomplete (hidden by default, shown in NoLevel mode)
            var subAcGrid = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(subAcGrid, 3);
            perRowChainNameGrids.Add(subAcGrid);

            var subTxtChain = new Wpf.Ui.Controls.TextBox
            {
                PlaceholderText = "Chain name...",
                VerticalAlignment = VerticalAlignment.Center,
                Tag = row
            };
            row.ChainNameTextBox = subTxtChain;
            subAcGrid.Children.Add(subTxtChain);

            var (subPopup, subListBox) = CreateAutocompletePopup(subTxtChain);
            row.SuggestPopup = subPopup;
            row.SuggestList = subListBox;
            subAcGrid.Children.Add(subPopup);

            subTxtChain.TextChanged += (_, _) =>
            {
                if (_suppressTextChanged) return;
                HandleAutocompleteTextChanged(subTxtChain, row);
                row.ChainName = subTxtChain.Text?.Trim();
                _ = ResolveWithDebounceAsync(row);
            };
            subTxtChain.PreviewKeyDown += TxtChain_PreviewKeyDown;

            subGrid.Children.Add(subAcGrid);

            // Wiki filename
            var fnLabel = new TextBlock
            {
                Text = "—",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(fnLabel, 4);
            row.FilenameLabel = fnLabel;
            subGrid.Children.Add(fnLabel);

            // Status panel: exists icon + force checkbox
            var subStatusPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(subStatusPanel, 5);

            var existsIcon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = SymbolRegular.Circle24,
                FontSize = 14,
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.ExistsIcon = existsIcon;
            subStatusPanel.Children.Add(existsIcon);

            var subForceCheck = new CheckBox
            {
                Content = "Force",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                ToolTip = "Overwrite existing file on wiki"
            };
            ToolTipService.SetInitialShowDelay(subForceCheck, 0);
            subForceCheck.Checked += (_, _) => row.ForceUpdate = true;
            subForceCheck.Unchecked += (_, _) => row.ForceUpdate = false;
            row.ForceCheckBox = subForceCheck;
            subStatusPanel.Children.Add(subForceCheck);

            subGrid.Children.Add(subStatusPanel);
            subCard.Child = subGrid;
            uploadRowsPanel.Children.Add(subCard);

            // Grey out non-optimized rows
            if (!item.IsOptimized)
            {
                foreach (UIElement child in subGrid.Children)
                {
                    if (Grid.GetColumn(child) > 0) // skip col 0 (warning icon)
                    {
                        child.Opacity = 0.35;
                        child.IsHitTestVisible = false;
                    }
                }
            }
        }

        // Group-level enable toggle → checks/unchecks all sub-rows
        groupEnableCheck.Checked += (_, _) =>
        {
            foreach (var sr in subRows)
            {
                if (sr.EnabledCheckBox != null) sr.EnabledCheckBox.IsChecked = true;
            }
        };
        groupEnableCheck.Unchecked += (_, _) =>
        {
            foreach (var sr in subRows)
            {
                if (sr.EnabledCheckBox != null) sr.EnabledCheckBox.IsChecked = false;
            }
        };

        // Group-level suppress toggle → show/hide individual chain name editing
        groupSuppressCheck.Checked += (_, _) =>
        {
            for (int i = 0; i < subRows.Count; i++)
            {
                subRows[i].SuppressLevel = true;

                // Show individual chain name TextBox
                perRowChainNameGrids[i].Visibility = Visibility.Visible;
                perRowChainNameCols[i].Width = new GridLength(1, GridUnitType.Star);

                // Pre-fill with header chain name if sub-row TextBox is empty
                if (subRows[i].ChainNameTextBox != null
                    && string.IsNullOrEmpty(subRows[i].ChainNameTextBox!.Text))
                {
                    _suppressTextChanged = true;
                    subRows[i].ChainNameTextBox!.Text = txtChain.Text;
                    _suppressTextChanged = false;
                    subRows[i].ChainName = txtChain.Text?.Trim();
                }

                _ = ResolveAndCheckAsync(subRows[i]);
            }
        };
        groupSuppressCheck.Unchecked += (_, _) =>
        {
            for (int i = 0; i < subRows.Count; i++)
            {
                subRows[i].SuppressLevel = false;

                // Hide individual chain name TextBox
                perRowChainNameGrids[i].Visibility = Visibility.Collapsed;
                perRowChainNameCols[i].Width = GridLength.Auto;

                // Revert to shared header chain name
                subRows[i].ChainName = txtChain.Text?.Trim();
                _ = ResolveAndCheckAsync(subRows[i]);
            }
        };

        // When header chain name changes, resolve all sub-rows (only when NoLevel is off)
        txtChain.TextChanged += (_, _) =>
        {
            // Only suppress autocomplete popup, always propagate to sub-rows
            if (!_suppressTextChanged)
                HandleAutocompleteTextChanged(txtChain, headerRow);

            if (groupSuppressCheck.IsChecked != true)
            {
                var chainName = txtChain.Text?.Trim();
                foreach (var sr in subRows)
                    sr.ChainName = chainName;

                _ = ResolveWithDebounceAsync(subRows.ToArray());
            }
        };
        txtChain.PreviewKeyDown += TxtChain_PreviewKeyDown;

        // Pre-fill
        if (!string.IsNullOrEmpty(groupItems[0].DetectedChainName))
        {
            _suppressTextChanged = true;
            txtChain.Text = groupItems[0].DetectedChainName;
            _suppressTextChanged = false;
            foreach (var sr in subRows)
            {
                sr.ChainName = groupItems[0].DetectedChainName;
                _ = ResolveAndCheckAsync(sr);
            }
        }

        // Add spacing after group
        uploadRowsPanel.Children.Add(new Border { Height = 6 });
    }

    // ══════════════════════════════════════════════════════════════
    //  ENABLE/DISABLE ROW
    // ══════════════════════════════════════════════════════════════

    private void SetRowEnabled(UploadRow row, bool enabled)
    {
        row.IsEnabled = enabled;
        if (row.ContentGrid != null)
        {
            // Grey out all children except the checkbox itself
            foreach (UIElement child in row.ContentGrid.Children)
            {
                if (child == row.EnabledCheckBox) continue;
                child.Opacity = enabled ? 1.0 : 0.35;
                child.IsHitTestVisible = enabled;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  AUTOCOMPLETE
    // ══════════════════════════════════════════════════════════════

    private (Popup popup, ListBox listBox) CreateAutocompletePopup(Wpf.Ui.Controls.TextBox target)
    {
        var listBox = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 200
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
        listBox.PreviewMouseLeftButtonUp += SuggestList_MouseUp;

        var innerBorder = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = (Brush)FindResource("ControlElevationBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0, 4, 0, 4),
            Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 3,
                Opacity = 0.18,
                Direction = 270,
                Color = Colors.Black
            },
            Child = listBox
        };

        var outerBorder = new Border
        {
            Background = (Brush)FindResource("SolidBackgroundFillColorBaseBrush"),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 0, 0),
            Child = innerBorder
        };

        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = outerBorder
        };

        popup.SetBinding(FrameworkElement.WidthProperty,
            new System.Windows.Data.Binding("ActualWidth") { Source = target });

        return (popup, listBox);
    }

    private void TxtChain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        if (sender is not Wpf.Ui.Controls.TextBox txt || txt.Tag is not UploadRow row) return;

        HandleAutocompleteTextChanged(txt, row);

        row.ChainName = txt.Text?.Trim();
        _ = ResolveWithDebounceAsync(row);
    }

    /// <summary>Debounce wiki filename resolution by 500ms to avoid partial-name lookups while typing.</summary>
    private async Task ResolveWithDebounceAsync(params UploadRow[] rows)
    {
        _resolveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resolveCts = cts;

        try
        {
            await Task.Delay(500, cts.Token);
            foreach (var row in rows)
                _ = ResolveAndCheckAsync(row);
        }
        catch (TaskCanceledException) { /* debounce cancelled — newer keystroke takes over */ }
    }

    private void HandleAutocompleteTextChanged(Wpf.Ui.Controls.TextBox txt, UploadRow row)
    {
        var query = txt.Text?.Trim() ?? "";

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        var names = _allNames;
        var namesLower = _allNamesLower;
        var popup = row.SuggestPopup;
        var listBox = row.SuggestList;
        if (popup == null || listBox == null) return;

        if (query.Length == 0)
        {
            listBox.ItemsSource = names.Length <= MaxSuggestions
                ? names
                : names.AsSpan(0, MaxSuggestions).ToArray();
            popup.IsOpen = true;
            return;
        }

        var queryLower = query.ToLowerInvariant();
        Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;

            var prefixMatches = new List<string>();
            var containsMatches = new List<string>();

            for (int i = 0; i < namesLower.Length; i++)
            {
                if (token.IsCancellationRequested) return;

                if (namesLower[i].StartsWith(queryLower))
                    prefixMatches.Add(names[i]);
                else if (namesLower[i].Contains(queryLower))
                    containsMatches.Add(names[i]);

                if (prefixMatches.Count + containsMatches.Count >= MaxSuggestions)
                    break;
            }

            if (token.IsCancellationRequested) return;

            var results = new List<string>(prefixMatches.Count + containsMatches.Count);
            results.AddRange(prefixMatches);
            var remaining = MaxSuggestions - results.Count;
            if (remaining > 0 && containsMatches.Count > 0)
                results.AddRange(containsMatches.Take(remaining));

            if (token.IsCancellationRequested) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested) return;
                listBox.ItemsSource = results;
                popup.IsOpen = results.Count > 0;
            });
        }, token);
    }

    private void TxtChain_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.TextBox txt) return;

        UploadRow? row = txt.Tag as UploadRow;
        if (row == null) return;

        var popup = row.SuggestPopup;
        var listBox = row.SuggestList;
        if (popup == null || listBox == null || !popup.IsOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                if (listBox.Items.Count > 0)
                {
                    listBox.SelectedIndex = Math.Min(listBox.SelectedIndex + 1, listBox.Items.Count - 1);
                    listBox.ScrollIntoView(listBox.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (listBox.SelectedIndex > 0)
                {
                    listBox.SelectedIndex--;
                    listBox.ScrollIntoView(listBox.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Tab:
                if (listBox.SelectedItem is string selected)
                    AcceptSuggestion(txt, row, selected);
                else if (listBox.Items.Count == 1 && listBox.Items[0] is string only)
                    AcceptSuggestion(txt, row, only);
                popup.IsOpen = false;
                e.Handled = e.Key == Key.Enter;
                break;

            case Key.Escape:
                popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SuggestList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is not string selected) return;

        var popup = FindParentPopup(lb);
        if (popup?.PlacementTarget is Wpf.Ui.Controls.TextBox txt && txt.Tag is UploadRow row)
        {
            AcceptSuggestion(txt, row, selected);
            popup.IsOpen = false;
        }
    }

    private static Popup? FindParentPopup(DependencyObject child)
    {
        var current = child;
        while (current != null)
        {
            if (current is Popup p) return p;
            current = VisualTreeHelper.GetParent(current)
                      ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void AcceptSuggestion(Wpf.Ui.Controls.TextBox txt, UploadRow row, string name)
    {
        _suppressTextChanged = true;
        txt.Text = name;
        txt.CaretIndex = name.Length;
        _suppressTextChanged = false;

        row.ChainName = name;
        _ = ResolveAndCheckAsync(row);
    }

    // ══════════════════════════════════════════════════════════════
    //  WIKI FILENAME RESOLUTION + EXISTENCE CHECK
    // ══════════════════════════════════════════════════════════════

    private async Task ResolveAndCheckAsync(UploadRow row)
    {
        var chainName = row.ChainName;
        if (string.IsNullOrWhiteSpace(chainName))
        {
            row.ResolvedWikiFilename = null;
            UpdateRowUI(row);
            return;
        }

        row.IsChecking = true;
        UpdateRowUI(row);

        try
        {
            if (row.SuppressLevel)
            {
                // With suppressLevel, FormatFileName returns e.g. "MagicFlower.png"
                var filename = await WikiMappingService.ResolveWikiFilenameAsync(chainName, suppressLevel: true);
                if (string.IsNullOrEmpty(filename))
                {
                    row.ResolvedWikiFilename = null;
                    row.IsChecking = false;
                    UpdateRowUI(row);
                    return;
                }

                row.ResolvedWikiFilename = filename;
            }
            else
            {
                // Normal: resolve base, then apply level from local filename
                var fullFilename = await WikiMappingService.ResolveWikiFilenameAsync(chainName);
                if (string.IsNullOrEmpty(fullFilename))
                {
                    row.ResolvedWikiFilename = null;
                    row.IsChecking = false;
                    UpdateRowUI(row);
                    return;
                }

                // fullFilename is like "MagicFlower01.png" — strip trailing "01.png" to get base
                string filenameBase;
                if (fullFilename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && fullFilename.Length > 6)
                    filenameBase = fullFilename[..^6]; // strip "01.png"
                else
                    filenameBase = fullFilename[..^4]; // strip ".png"

                // Extract level from local filename (last 2 digits before extension)
                var localName = System.IO.Path.GetFileNameWithoutExtension(row.FilePath);
                string level = "01";
                if (localName.Length >= 2 && char.IsDigit(localName[^1]) && char.IsDigit(localName[^2]))
                    level = localName[^2..];

                row.ResolvedWikiFilename = $"{filenameBase}{level}.png";
            }

            // Check existence
            var exists = await WikiMappingService.CheckPageExistsAsync($"File:{row.ResolvedWikiFilename}");
            row.FileExistsOnWiki = exists;
            row.IsChecking = false;
            UpdateRowUI(row);
        }
        catch
        {
            row.ResolvedWikiFilename = null;
            row.IsChecking = false;
            UpdateRowUI(row);
        }
    }

    private void UpdateRowUI(UploadRow row)
    {
        Dispatcher.Invoke(() =>
        {
            if (row.FilenameLabel != null)
            {
                row.FilenameLabel.Text = row.IsChecking ? "Checking..."
                    : row.ResolvedWikiFilename ?? "—";
            }

            if (row.ExistsIcon != null)
            {
                if (row.IsChecking)
                {
                    row.ExistsIcon.Symbol = SymbolRegular.ArrowSync24;
                    row.ExistsIcon.Foreground = (Brush)FindResource("TextFillColorTertiaryBrush");
                }
                else if (row.ResolvedWikiFilename == null)
                {
                    row.ExistsIcon.Symbol = SymbolRegular.Circle24;
                    row.ExistsIcon.Foreground = (Brush)FindResource("TextFillColorTertiaryBrush");
                }
                else if (row.FileExistsOnWiki)
                {
                    row.ExistsIcon.Symbol = SymbolRegular.Warning24;
                    row.ExistsIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00));
                    row.ExistsIcon.ToolTip = "This file already exists on the wiki — uploading will update the existing version";
                    ToolTipService.SetInitialShowDelay(row.ExistsIcon, 0);
                }
                else
                {
                    row.ExistsIcon.Symbol = SymbolRegular.AddCircle24;
                    row.ExistsIcon.Foreground = (Brush)FindResource("AccentFillColorDefaultBrush");
                }
            }

            // Show Force checkbox only when file exists on wiki
            if (row.ForceCheckBox != null)
            {
                row.ForceCheckBox.Visibility = row.FileExistsOnWiki && !row.IsChecking
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        });
    }

    // ══════════════════════════════════════════════════════════════
    //  UPLOAD
    // ══════════════════════════════════════════════════════════════

    private async void BtnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.Settings.WikiVerified)
        {
            infoBar.Message = "Wiki bot not configured. Set up credentials in Settings.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
            return;
        }

        // Collect uploadable rows (enabled + resolved filename)
        var toUpload = _rows
            .Where(r => r.IsEnabled)
            .Where(r => !string.IsNullOrEmpty(r.ResolvedWikiFilename))
            .ToList();

        if (toUpload.Count == 0)
        {
            infoBar.Message = "Nothing to upload. Set chain names for the images, or check that items are enabled.";
            infoBar.Severity = InfoBarSeverity.Informational;
            infoBar.IsOpen = true;
            return;
        }

        btnUpload.IsEnabled = false;
        progressBar.Visibility = Visibility.Visible;
        progressBar.Maximum = toUpload.Count;
        progressBar.Value = 0;

        _isUploading = true;
        _uploadCts = new CancellationTokenSource();
        var ct = _uploadCts.Token;

        int uploaded = 0;
        int skipped = 0;
        bool forceAll = false;
        bool skipAll = false;

        try
        {
            var settings = _main.Settings;
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                settings.WikiUsername, settings.WikiPassword);
            var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

            for (int idx = 0; idx < toUpload.Count; idx++)
            {
                if (ct.IsCancellationRequested) break;

                var row = toUpload[idx];
                var wikiFilename = row.ResolvedWikiFilename!;
                int remaining = toUpload.Count - idx - 1;

                infoBar.Message = $"Uploading {wikiFilename} ({idx + 1}/{toUpload.Count})...";
                infoBar.Severity = InfoBarSeverity.Informational;
                infoBar.IsOpen = true;
                await Task.Yield();

                // Handle conflicts: file already exists on wiki
                if (row.FileExistsOnWiki && !forceAll && !row.ForceUpdate)
                {
                    if (skipAll)
                    {
                        skipped++;
                        progressBar.Value = uploaded + skipped;
                        continue;
                    }

                    // Show conflict dialog
                    EnsureWindowVisible(this);
                    var dlg = new UploadConflictDialog(wikiFilename, remaining, row.FilePath, row.IsPartOfSplit, _main.Settings.WikiUsername)
                        { Owner = this };
                    dlg.ShowDialog();

                    switch (dlg.Choice)
                    {
                        case UploadConflictChoice.Force:
                            // Upload this one with force
                            break;
                        case UploadConflictChoice.ForceAll:
                            forceAll = true;
                            break;
                        case UploadConflictChoice.Skip:
                            skipped++;
                            progressBar.Value = uploaded + skipped;
                            continue;
                        case UploadConflictChoice.SkipAll:
                            skipAll = true;
                            skipped++;
                            progressBar.Value = uploaded + skipped;
                            continue;
                        default: // Cancel
                            goto endUpload;
                    }
                }

                try
                {
                    var fileData = await File.ReadAllBytesAsync(row.FilePath, ct);
                    bool ignore = forceAll || row.FileExistsOnWiki;
                    // Set file description page with {{Permission}} license for new uploads
                    string? description = !row.FileExistsOnWiki ? "{{Permission}}" : null;
                    await WikiMappingService.UploadFileAsync(client, csrfToken, wikiFilename, fileData,
                        description: description, ignoreWarnings: ignore);
                    uploaded++;
                }
                catch (WikiMappingService.WikiUploadWarningException)
                {
                    // File exists (not yet detected) — force upload
                    try
                    {
                        var fileData = await File.ReadAllBytesAsync(row.FilePath, ct);
                        await WikiMappingService.UploadFileAsync(client, csrfToken, wikiFilename, fileData,
                            ignoreWarnings: true);
                        uploaded++;
                    }
                    catch (Exception ex) when (ex.Message.Contains("exact duplicate"))
                    {
                        skipped++;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex.Message.Contains("exact duplicate"))
                {
                    skipped++;
                }
                progressBar.Value = uploaded + skipped;
            }
            endUpload:

            var msg = ct.IsCancellationRequested ? "Upload cancelled. " : "";
            msg += $"Uploaded {uploaded} images.";
            if (skipped > 0) msg += $" Skipped {skipped}.";
            infoBar.Message = msg;
            infoBar.Severity = ct.IsCancellationRequested ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            infoBar.IsOpen = true;

            UploadedCount = uploaded;
            if (uploaded > 0)
                Increment(s => s.WikiImagesUploaded += uploaded);
        }
        catch (OperationCanceledException)
        {
            var msg = $"Upload cancelled. Uploaded {uploaded} images.";
            if (skipped > 0) msg += $" Skipped {skipped}.";
            infoBar.Message = msg;
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
            UploadedCount = uploaded;
        }
        catch (Exception ex)
        {
            infoBar.Message = $"Upload failed: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
        finally
        {
            _isUploading = false;
            _uploadCts = null;
            progressBar.Visibility = Visibility.Collapsed;
            btnUpload.IsEnabled = true;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isUploading)
        {
            e.Cancel = true;
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Upload in progress",
                Content = "An upload is currently in progress. Do you want to cancel it?",
                PrimaryButtonText = "Cancel Upload",
                CloseButtonText = "Continue Uploading",
                MinWidth = 400,
                Owner = this
            };
            ApplicationThemeManager.Apply(msgBox);
            var result = await msgBox.ShowDialogAsync();
            if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                _uploadCts?.Cancel();
                _isUploading = false;
                Close();
            }
            return;
        }
        base.OnClosing(e);
    }

    private static void EnsureWindowVisible(Window? window)
    {
        if (window == null) return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    // ══════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════

    private static BitmapImage? LoadThumbnail(string path, int decodeHeight)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelHeight = decodeHeight;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
