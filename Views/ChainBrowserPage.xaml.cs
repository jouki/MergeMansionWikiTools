using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

// ── View model wrappers for data binding ─────────────────────────

public class ChainViewModel : INotifyPropertyChanged
{
    public ParsedChain Source { get; }
    private readonly MainWindow _main;

    public string DisplayName => Source.DisplayName;
    public string ConfigKey => Source.MergedFromConfigKeys is { Count: > 1 }
        ? string.Join(" + ", Source.MergedFromConfigKeys)
        : Source.ConfigKey;
    public string Summary => Source.Summary;
    public bool ShowConfigKey => Source.DisplayName != Source.ConfigKey;
    public bool IsNameFromWiki => Source.IsNameFromWiki;
    public bool ShowUploadButton => _main.Settings.WikiVerified;
    public bool ShowCompareButton => _main.HasVariantDirectories;
    public bool HasLevelCollisions => Source.HasLevelCollisions;

    private bool? _hasVariantDiffs;
    /// <summary>null = not yet scanned, true = has diffs, false = no diffs.</summary>
    public bool? HasVariantDiffs
    {
        get => _hasVariantDiffs;
        set
        {
            _hasVariantDiffs = value;
            PropertyChanged?.Invoke(this, new(nameof(HasVariantDiffs)));
            PropertyChanged?.Invoke(this, new(nameof(CompareButtonOpacity)));
            PropertyChanged?.Invoke(this, new(nameof(CompareButtonTooltip)));
        }
    }

    public double CompareButtonOpacity => _hasVariantDiffs switch
    {
        true => 1.0,
        false => 0.35,
        null => 0.6
    };

    public string CompareButtonTooltip => _hasVariantDiffs switch
    {
        true => "Compare with AB test groups (differences found)",
        false => "Compare with AB test groups (no differences)",
        null => "Compare with AB test groups"
    };

    private bool _showMergeCheckbox;
    public bool ShowMergeCheckbox
    {
        get => _showMergeCheckbox;
        set { _showMergeCheckbox = value; PropertyChanged?.Invoke(this, new(nameof(ShowMergeCheckbox))); }
    }

    private bool _isMergeChecked;
    public bool IsMergeChecked
    {
        get => _isMergeChecked;
        set { _isMergeChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsMergeChecked))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public List<ItemViewModel> Items { get; }

    /// <summary>Tracks which group keys have their group checkbox checked.</summary>
    public HashSet<string> CheckedGroups { get; } = new();

    /// <summary>Whether this chain has items from multiple source chains.</summary>
    public bool HasMultipleSources { get; }

    /// <summary>Whether any items are aliases (for showing Group Alias checkbox).</summary>
    public bool HasAliasItems { get; }

    /// <summary>Whether alias grouping checkbox should be visible.</summary>
    public bool ShowGroupAliasCheckbox => HasMultipleSources && HasAliasItems;

    private bool _allItemsChecked;
    /// <summary>Whether all items are currently checked (for Select All binding).</summary>
    public bool AllItemsChecked
    {
        get => _allItemsChecked;
        set { _allItemsChecked = value; PropertyChanged?.Invoke(this, new(nameof(AllItemsChecked))); }
    }

    private object _itemsView = null!;
    /// <summary>Current items view (plain list or grouped CollectionView).</summary>
    public object ItemsView
    {
        get => _itemsView;
        private set { _itemsView = value; PropertyChanged?.Invoke(this, new(nameof(ItemsView))); }
    }

    private bool _hasGrouping;
    /// <summary>Whether items are currently grouped (for margin binding).</summary>
    public bool HasGrouping
    {
        get => _hasGrouping;
        private set
        {
            _hasGrouping = value;
            PropertyChanged?.Invoke(this, new(nameof(HasGrouping)));
            PropertyChanged?.Invoke(this, new(nameof(ItemsMargin)));
        }
    }

    private bool _groupAliases = true;
    /// <summary>When true, alias items are collapsed into one "Aliases" group.</summary>
    public bool GroupAliases
    {
        get => _groupAliases;
        set
        {
            if (_groupAliases == value) return;
            _groupAliases = value;
            PropertyChanged?.Invoke(this, new(nameof(GroupAliases)));
            RebuildItemsView();
        }
    }

    /// <summary>Negative top margin to compensate for first group header spacing.</summary>
    public Thickness ItemsMargin => HasGrouping ? new Thickness(0, -14, 0, 0) : new Thickness(0);

    public ChainViewModel(ParsedChain source, MainWindow main)
    {
        Source = source;
        _main = main;
        Items = source.Items.Select(i => new ItemViewModel(i)).ToList();

        var distinctSources = Items.Select(i => i.SourceChainKey).Where(k => k.Length > 0).Distinct().Count();
        HasMultipleSources = distinctSources > 1;
        HasAliasItems = Items.Any(i => i.IsAlias);

        RebuildItemsView();
    }

    private void RebuildItemsView()
    {
        if (!HasMultipleSources)
        {
            HasGrouping = false;
            ItemsView = Items;
            return;
        }

        HasGrouping = true;

        if (GroupAliases && HasAliasItems)
        {
            // Remap CheckedGroups: individual alias keys → "Aliases" if all were checked
            var aliasKeys = Items.Where(i => i.IsAlias).Select(i => i.SourceChainKey).Distinct().ToList();
            if (aliasKeys.Count > 0 && aliasKeys.All(k => CheckedGroups.Contains(k)))
            {
                foreach (var k in aliasKeys) CheckedGroups.Remove(k);
                CheckedGroups.Add("Aliases");
            }
            else
            {
                foreach (var k in aliasKeys) CheckedGroups.Remove(k);
                CheckedGroups.Remove("Aliases");
            }

            var view = new ListCollectionView(Items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemViewModel.AliasGroupKey)));
            ItemsView = view;
        }
        else
        {
            // Remap CheckedGroups: "Aliases" → expand to individual alias SourceChainKeys
            if (CheckedGroups.Remove("Aliases"))
            {
                foreach (var k in Items.Where(i => i.IsAlias).Select(i => i.SourceChainKey).Distinct())
                    CheckedGroups.Add(k);
            }

            var view = new ListCollectionView(Items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemViewModel.SourceChainKey)));
            ItemsView = view;
        }
    }
}

public class ItemViewModel : INotifyPropertyChanged
{
    public ParsedItem Source { get; }

    public int Level => Source.Level;
    public string Name => Source.Name;
    public string ItemType => Source.ItemType;
    public string SourceChainKey => Source.SourceChainKey;
    public bool IsColliding => Source.IsColliding;
    public bool IsAlias => Source.IsAlias;
    public string? VariantLabel => Source.VariantLabel;

    /// <summary>Grouping key for alias mode: aliases → "Aliases", others → SourceChainKey.</summary>
    public string AliasGroupKey => Source.IsAlias ? "Aliases" : SourceChainKey;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
    }

    public string TypeBadge
    {
        get
        {
            var parts = new List<string>();
            if (Source.IsGenerator) parts.Add("GEN");
            if (Source.IsSpawner) parts.Add("SPAWN");
            if (Source.HasDecay) parts.Add("DECAY");
            return parts.Count > 0 ? string.Join(" ", parts) : "";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ItemViewModel(ParsedItem source) => Source = source;
}

// ── Page ─────────────────────────────────────────────────────────

public partial class ChainBrowserPage : UserControl
{
    private readonly MainWindow _main;
    private List<ChainViewModel> _allChains = new();
    private string _currentSearch = "";
    private ChainViewModel? _activeCheckChain;
    private bool _mergeMode;
    private readonly HashSet<ChainViewModel> _mergeSelection = new();
    private readonly HashSet<ChainViewModel> _mergeCheckChains = new();
    private SolidColorBrush _splitSepBrush;

    private static Brush HighlightBrush =>
        Application.Current.TryFindResource("AccentTextFillColorPrimaryBrush") as Brush
        ?? new SolidColorBrush(Color.FromRgb(0xCB, 0x9C, 0xFD));

    public ChainBrowserPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        _splitSepBrush = GetSplitSeparatorBrush();
        btnMergeModeSep.Background = _splitSepBrush;
        ApplicationThemeManager.Changed += (_, _) => Dispatcher.InvokeAsync(RefreshSplitSeparatorColor);
        ApplySplitButtonStyle(btnMergeMode, btnMergeModeHelp);

        // Restore persisted filter states
        var s = _main.Settings;
        chkGenerators.IsChecked = s.FilterGenerators;
        chkSpawners.IsChecked = s.FilterSpawners;
        chkProducts.IsChecked = s.FilterProducts;
        chkEvent.IsChecked = s.FilterEvent;
        chkNamed.IsChecked = s.FilterNamed;
        chkCollisions.IsChecked = s.FilterCollisions;
        UpdateFilterButtonText();

        if (_main.DataService != null)
            OnDataLoaded();

        _main.WikiVerifiedChanged += OnWikiVerifiedChanged;
    }

    private void OnWikiVerifiedChanged()
    {
        // Refresh upload button visibility (binding reads WikiVerified)
        lvChains.Items.Refresh();
    }

    public void OnDataLoaded()
    {
        if (_main.DataService == null) return;

        _allChains = _main.DataService.Chains
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ChainViewModel(c, _main))
            .ToList();

        ApplyFilter();

        emptyState.Visibility = Visibility.Collapsed;
        lvChains.Visibility = Visibility.Visible;

        // Fire-and-forget: scan all chains for variant diffs
        if (_main.HasVariantDirectories)
            _ = ScanVariantDiffsAsync();
    }

    private async Task ScanVariantDiffsAsync()
    {
        try
        {
            var basePath = _main.Settings.ChainItemOddsPath;
            var variants = VariantComparisonService.DiscoverVariants(basePath);
            if (variants.Count == 0) return;

            // Run heavy I/O + CPU parsing on thread pool, never blocks UI
            var keysWithDiffs = await Task.Run(() =>
                VariantComparisonService.ScanAllChainsForDiffsAsync(basePath, variants));

            // Update on UI thread (we're back on dispatcher after await)
            foreach (var vm in _allChains)
                vm.HasVariantDiffs = keysWithDiffs.Contains(vm.Source.ConfigKey);

            // Re-apply filter in case AB Diffs filter is active
            if (chkABDiffs?.IsChecked == true)
                ApplyFilter();
        }
        catch { /* ignore scan failures */ }
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ChkFilter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
        SaveFilterSettings();
        UpdateFilterButtonText();
    }

    private void FiltersButton_Click(object sender, RoutedEventArgs e)
    {
        popupFilters.IsOpen = !popupFilters.IsOpen;
    }

    private void FilterPopup_Opened(object? sender, EventArgs e)
    {
        filterPopupRoot.Focus();
    }

    private void FilterPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            popupFilters.IsOpen = false;
            e.Handled = true;
        }
    }

    private void SaveFilterSettings()
    {
        if (chkGenerators == null) return;
        var s = _main.Settings;
        s.FilterGenerators = chkGenerators.IsChecked == true;
        s.FilterSpawners = chkSpawners.IsChecked == true;
        s.FilterProducts = chkProducts.IsChecked == true;
        s.FilterEvent = chkEvent.IsChecked == true;
        s.FilterNamed = chkNamed.IsChecked == true;
        s.FilterCollisions = chkCollisions.IsChecked == true;
        _main.SaveSettings();
    }

    private void ApplyFilter()
    {
        using var _t = AppLogger.Timed("FilterChains");
        if (lvChains == null) return;

        var search = txtSearch?.Text?.Trim() ?? "";
        _currentSearch = search;

        IEnumerable<ChainViewModel> filtered = _allChains;

        // Text search
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(c =>
                c.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.ConfigKey.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Items.Any(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        // Category filters (combinable — if none checked, show all)
        bool fGen = chkGenerators?.IsChecked == true;
        bool fSpawn = chkSpawners?.IsChecked == true;
        bool fProd = chkProducts?.IsChecked == true;
        bool fEvent = chkEvent?.IsChecked == true;
        bool fNamed = chkNamed?.IsChecked == true;
        bool fCollisions = chkCollisions?.IsChecked == true;
        bool fABDiffs = chkABDiffs?.IsChecked == true;
        bool anyFilter = fGen || fSpawn || fProd || fEvent || fNamed || fCollisions || fABDiffs;

        if (anyFilter)
        {
            if (fGen) filtered = filtered.Where(c => c.Source.HasGenerators);
            if (fSpawn) filtered = filtered.Where(c => c.Source.HasSpawners);
            if (fProd) filtered = filtered.Where(c => !c.Source.HasGenerators && !c.Source.HasSpawners);
            if (fEvent) filtered = filtered.Where(c => c.Source.IsEventChain);
            if (fNamed) filtered = filtered.Where(c => c.Source.HasHumanReadableName);
            if (fCollisions) filtered = filtered.Where(c => c.Source.HasLevelCollisions);
            if (fABDiffs) filtered = filtered.Where(c => c.HasVariantDiffs == true);
        }

        var result = filtered.ToList();
        AppLogger.Info($"FilterChains: {result.Count}/{_allChains.Count} matches, setting ItemsSource...");
        lvChains.ItemsSource = result;
        txtChainCount.Text = $"{result.Count} / {_allChains.Count} chains";

        // Show hint when search matches exist but are hidden by filters
        if (anyFilter && !string.IsNullOrEmpty(search))
        {
            var searchOnly = _allChains.Where(c =>
                c.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.ConfigKey.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Items.Any(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
            var hiddenCount = searchOnly.Count() - result.Count;
            if (hiddenCount > 0)
            {
                txtFilterHint.Text = $"{hiddenCount} chain(s) match your search but are hidden by filters";
                filterHint.Visibility = Visibility.Visible;
            }
            else
            {
                filterHint.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            filterHint.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Called when each DisplayName TextBlock is loaded/recycled.
    /// Applies search highlighting using Inlines if there's an active search.
    /// </summary>
    private void TxtDisplayName_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        if (tb.Tag is not ChainViewModel vm) return;

        var search = _currentSearch;

        var displayName = vm.DisplayName;
        tb.Text = null; // Clear Text so Inlines take over
        tb.Inlines.Clear();

        // No active search — use single Run for consistent rendering height
        if (string.IsNullOrEmpty(search))
        {
            tb.Inlines.Add(new Run(displayName));
            return;
        }

        int searchLen = search.Length;
        int pos = 0;

        while (pos < displayName.Length)
        {
            int matchIdx = displayName.IndexOf(search, pos, StringComparison.OrdinalIgnoreCase);
            if (matchIdx < 0)
            {
                tb.Inlines.Add(new Run(displayName[pos..]));
                break;
            }

            // Text before match
            if (matchIdx > pos)
                tb.Inlines.Add(new Run(displayName[pos..matchIdx]));

            // Highlighted match
            tb.Inlines.Add(new Run(displayName[matchIdx..(matchIdx + searchLen)])
            {
                FontWeight = FontWeights.ExtraBold,
                Foreground = HighlightBrush
            });

            pos = matchIdx + searchLen;
        }
    }

    /// <summary>
    /// Called when each item Name TextBlock is loaded/recycled.
    /// Applies search highlighting using Inlines if there's an active search.
    /// Appends ItemType for colliding items so they can be distinguished.
    /// </summary>
    private void TxtItemName_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        if (tb.Tag is not ItemViewModel vm) return;

        var search = _currentSearch;
        var name = vm.Name;
        tb.Text = null;
        tb.Inlines.Clear();

        if (string.IsNullOrEmpty(search))
        {
            tb.Inlines.Add(new Run(name));
        }
        else
        {
            int searchLen = search.Length;
            int pos = 0;

            while (pos < name.Length)
            {
                int matchIdx = name.IndexOf(search, pos, StringComparison.OrdinalIgnoreCase);
                if (matchIdx < 0)
                {
                    tb.Inlines.Add(new Run(name[pos..]));
                    break;
                }

                if (matchIdx > pos)
                    tb.Inlines.Add(new Run(name[pos..matchIdx]));

                tb.Inlines.Add(new Run(name[matchIdx..(matchIdx + searchLen)])
                {
                    FontWeight = FontWeights.ExtraBold,
                    Foreground = HighlightBrush
                });

                pos = matchIdx + searchLen;
            }
        }

        // Subtle variant label (e.g. "Sauna") for multi-variant chain levels
        if (!string.IsNullOrEmpty(vm.VariantLabel))
        {
            var labelRun = new Run($"  {vm.VariantLabel}") { FontSize = 10 };
            labelRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
            tb.Inlines.Add(labelRun);
        }

        // Show original JSON name when item was renamed via wiki
        if (!string.IsNullOrEmpty(vm.Source.OriginalName)
            && vm.Source.OriginalName != vm.Name)
        {
            var rawRun = new Run($"  ({vm.Source.OriginalName})") { FontSize = 10 };
            rawRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
            tb.Inlines.Add(rawRun);
        }

    }

    private void FilterRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            var chk = border.Child as CheckBox;
            if (chk != null)
                chk.IsChecked = chk.IsChecked != true;
        }
    }

    private void UpdateFilterButtonText()
    {
        if (txtFiltersLabel == null) return;

        int count = 0;
        if (chkGenerators?.IsChecked == true) count++;
        if (chkSpawners?.IsChecked == true) count++;
        if (chkProducts?.IsChecked == true) count++;
        if (chkEvent?.IsChecked == true) count++;
        if (chkNamed?.IsChecked == true) count++;
        if (chkCollisions?.IsChecked == true) count++;
        if (chkABDiffs?.IsChecked == true) count++;

        txtFiltersLabel.Text = count > 0 ? $"Filters ({count})" : "Filters";
    }

    private async void RefreshWikiCache_Click(object sender, RoutedEventArgs e)
    {
        btnRefreshWiki.IsEnabled = false;
        try
        {
            await _main.RefreshWikiMappingAsync();
            _main.ShowStatus("Wiki cache refreshed successfully.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _main.ShowStatus($"Failed to refresh wiki cache: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnRefreshWiki.IsEnabled = true;
        }
    }

    private void UploadImages_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not ChainViewModel vm)
            return;

        _main.NavigateToImageOptimiserChainMode(vm.Source);
    }

    // ── Item selection & action bar ──────────────────────────────────

    private void ClearChecks(ChainViewModel vm)
    {
        foreach (var item in vm.Items)
            item.IsChecked = false;
        vm.CheckedGroups.Clear();
        vm.AllItemsChecked = false;
    }

    /// <summary>Walks up the visual tree from a given element to find the parent of type T.</summary>
    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        // Run/Inline elements are ContentElements, not Visuals — bridge via LogicalTreeHelper
        while (child is ContentElement ce)
            child = LogicalTreeHelper.GetParent(ce) ?? VisualTreeHelper.GetParent(ce);
        if (child == null) return null;

        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T found) return found;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    /// <summary>Walks down the visual tree to find the first descendant StackPanel with Tag="ActionBar".</summary>
    private static StackPanel? FindActionBar(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is StackPanel sp && sp.Tag is string tag && tag == "ActionBar")
                return sp;
            var found = FindActionBar(child);
            if (found != null) return found;
        }
        return null;
    }

    private void UpdateActionBar(ChainViewModel chainVm, StackPanel actionBar)
    {
        int count = chainVm.Items.Count(i => i.IsChecked);

        if (count == 0)
        {
            actionBar.Visibility = Visibility.Collapsed;
            return;
        }

        actionBar.Visibility = Visibility.Visible;

        // Update count text
        foreach (var child in actionBar.Children)
        {
            if (child is TextBlock tb)
            {
                tb.Text = $"{count} selected";
                break;
            }
        }

        // Set Level visible only for single item; Rename Item for 1+
        bool wikiVerified = _main.Settings.WikiVerified;
        foreach (var child in actionBar.Children)
        {
            if (child is Wpf.Ui.Controls.Button btn)
            {
                if (btn.Content is string content)
                {
                    if (content == "Set Level")
                    {
                        btn.Visibility = count == 1 ? Visibility.Visible : Visibility.Collapsed;
                        btn.IsEnabled = wikiVerified;
                        btn.ToolTip = wikiVerified ? null : "Wiki connection required";
                    }
                    else if (content is "Rename Item" or "Rename Items")
                    {
                        btn.Visibility = count >= 1 ? Visibility.Visible : Visibility.Collapsed;
                        btn.Content = count > 1 ? "Rename Items" : "Rename Item";
                        btn.IsEnabled = wikiVerified;
                        btn.ToolTip = wikiVerified ? null : "Wiki connection required";
                    }
                    else if (content == "Move Items")
                    {
                        btn.IsEnabled = wikiVerified;
                        btn.ToolTip = wikiVerified ? null : "Wiki connection required";
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sets the active check chain. In merge mode, allows multi-chain selections;
    /// in normal mode, clears previous chain's checks.
    /// </summary>
    private void SetActiveCheckChain(ChainViewModel chainVm)
    {
        if (_mergeMode)
        {
            _mergeCheckChains.Add(chainVm);
        }
        else
        {
            if (_activeCheckChain != null && _activeCheckChain != chainVm)
                ClearChecks(_activeCheckChain);
        }
        _activeCheckChain = chainVm;
    }

    private void GroupRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not StackPanel sp) return;

        // Don't toggle if the click was directly on the CheckBox
        if (e.OriginalSource is DependencyObject src && FindVisualParent<CheckBox>(src) != null)
            return;

        // Find the CheckBox inside this row and toggle it
        var cb = FindVisualChild<CheckBox>(sp);
        if (cb == null) return;

        cb.IsChecked = cb.IsChecked != true;

        // Reuse GroupCheckBox_Click logic
        var groupKey = cb.Tag as string ?? "";
        bool isChecked = cb.IsChecked == true;

        var expander = FindVisualParent<Expander>(sp);
        if (expander == null) return;
        var border = FindVisualParent<Border>(expander);
        var chainVm = border?.DataContext as ChainViewModel;
        if (chainVm == null) return;

        SetActiveCheckChain(chainVm);

        foreach (var item in GetGroupItems(chainVm, groupKey))
            item.IsChecked = isChecked;

        // Track group checkbox state
        if (isChecked)
            chainVm.CheckedGroups.Add(groupKey);
        else
            chainVm.CheckedGroups.Remove(groupKey);

        // Sync Select All
        chainVm.AllItemsChecked = chainVm.Items.Count > 0 && chainVm.Items.All(i => i.IsChecked);

        if (_mergeMode)
            UpdateMergeBar();
        else
        {
            var actionBar = FindActionBar(expander);
            if (actionBar != null)
                UpdateActionBar(chainVm, actionBar);
        }
    }

    private void GroupCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        var groupKey = cb.Tag as string ?? "";

        var expander = FindVisualParent<Expander>(cb);
        if (expander == null) return;
        var border = FindVisualParent<Border>(expander);
        var chainVm = border?.DataContext as ChainViewModel;
        if (chainVm == null) return;

        cb.IsChecked = chainVm.CheckedGroups.Contains(groupKey);
    }

    private void GroupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        bool isChecked = cb.IsChecked == true;
        var groupKey = cb.Tag as string ?? "";

        var expander = FindVisualParent<Expander>(cb);
        if (expander == null) return;
        var border = FindVisualParent<Border>(expander);
        var chainVm = border?.DataContext as ChainViewModel;
        if (chainVm == null) return;

        SetActiveCheckChain(chainVm);

        // Toggle items in this group only
        foreach (var item in GetGroupItems(chainVm, groupKey))
            item.IsChecked = isChecked;

        // Track group checkbox state for persistence across view rebuilds
        if (isChecked)
            chainVm.CheckedGroups.Add(groupKey);
        else
            chainVm.CheckedGroups.Remove(groupKey);

        // Sync Select All
        chainVm.AllItemsChecked = chainVm.Items.Count > 0 && chainVm.Items.All(i => i.IsChecked);

        if (_mergeMode)
            UpdateMergeBar();
        else
        {
            var actionBar = FindActionBar(expander);
            if (actionBar != null)
                UpdateActionBar(chainVm, actionBar);
        }
    }

    private void SelectAllItems_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        bool isChecked = cb.IsChecked == true;

        var expander = FindVisualParent<Expander>(cb);
        if (expander == null) return;
        var border = FindVisualParent<Border>(expander);
        var chainVm = border?.DataContext as ChainViewModel;
        if (chainVm == null) return;

        SetActiveCheckChain(chainVm);

        foreach (var item in chainVm.Items)
            item.IsChecked = isChecked;

        // Update all group checkboxes too
        if (isChecked)
        {
            foreach (var key in chainVm.Items.Select(i => chainVm.GroupAliases && chainVm.HasAliasItems
                ? i.AliasGroupKey : i.SourceChainKey).Distinct())
                chainVm.CheckedGroups.Add(key);
        }
        else
        {
            chainVm.CheckedGroups.Clear();
        }

        // Refresh group checkbox visuals
        var itemsControl = FindVisualChild<ItemsControl>(expander);
        if (itemsControl != null)
        {
            foreach (var groupCb in FindVisualChildren<CheckBox>(itemsControl)
                .Where(c => c.Tag is string))
                groupCb.IsChecked = isChecked;
        }

        if (_mergeMode)
            UpdateMergeBar();
        else
        {
            var actionBar = FindActionBar(expander);
            if (actionBar != null)
                UpdateActionBar(chainVm, actionBar);
        }
    }

    /// <summary>
    /// Syncs AllItemsChecked + CheckedGroups state based on actual item check states.
    /// Also refreshes group checkbox visuals in the visual tree.
    /// </summary>
    private void SyncCheckState(ChainViewModel chainVm, Expander? expander)
    {
        // Sync AllItemsChecked
        chainVm.AllItemsChecked = chainVm.Items.Count > 0 && chainVm.Items.All(i => i.IsChecked);

        // Sync CheckedGroups based on whether all items in each group are checked
        if (chainVm.HasGrouping)
        {
            bool aliasMode = chainVm.GroupAliases && chainVm.HasAliasItems;
            var groups = chainVm.Items
                .GroupBy(i => aliasMode ? i.AliasGroupKey : i.SourceChainKey)
                .Where(g => g.Key.Length > 0);

            foreach (var g in groups)
            {
                if (g.All(i => i.IsChecked))
                    chainVm.CheckedGroups.Add(g.Key);
                else
                    chainVm.CheckedGroups.Remove(g.Key);
            }

            // Refresh group checkbox visuals
            if (expander != null)
            {
                var itemsControl = FindVisualChild<ItemsControl>(expander);
                if (itemsControl != null)
                {
                    foreach (var groupCb in FindVisualChildren<CheckBox>(itemsControl)
                        .Where(c => c.Tag is string tag && tag.Length > 0))
                        groupCb.IsChecked = chainVm.CheckedGroups.Contains((string)groupCb.Tag);
                }
            }
        }
    }

    /// <summary>
    /// Gets items matching a group key, handling "Aliases" as a special case.
    /// </summary>
    private static IEnumerable<ItemViewModel> GetGroupItems(ChainViewModel chainVm, string groupKey)
    {
        return groupKey == "Aliases"
            ? chainVm.Items.Where(i => i.IsAlias)
            : chainVm.Items.Where(i => i.SourceChainKey == groupKey);
    }

    private void ItemRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.DataContext is not ItemViewModel itemVm) return;

        // Don't toggle if the click was directly on the CheckBox (it handles itself)
        if (e.OriginalSource is DependencyObject src && FindVisualParent<CheckBox>(src) != null)
            return;

        itemVm.IsChecked = !itemVm.IsChecked;

        // Reuse the same logic as ItemCheckBox_Click
        var expander = FindVisualParent<Expander>(grid);
        if (expander == null) return;
        var border = FindVisualParent<Border>(expander);
        var chainVm = border?.DataContext as ChainViewModel;
        if (chainVm == null) return;

        SetActiveCheckChain(chainVm);
        SyncCheckState(chainVm, expander);

        if (_mergeMode)
            UpdateMergeBar();
        else
        {
            var actionBar = FindActionBar(expander);
            if (actionBar != null)
                UpdateActionBar(chainVm, actionBar);
        }
    }

    private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not ItemViewModel itemVm) return;

        // Find the Expander → ChainViewModel
        var expander = FindVisualParent<Expander>(cb);
        if (expander == null) return;

        // The Expander's DataContext is set via the outer Border's DataContext (ChainViewModel)
        var border = FindVisualParent<Border>(expander);
        var chainVm = border?.DataContext as ChainViewModel;
        if (chainVm == null) return;

        SetActiveCheckChain(chainVm);
        SyncCheckState(chainVm, expander);

        if (_mergeMode)
            UpdateMergeBar();
        else
        {
            var actionBar = FindActionBar(expander);
            if (actionBar != null)
                UpdateActionBar(chainVm, actionBar);
        }
    }

    private void Expander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;

        var border = FindVisualParent<Border>(expander);
        var chainVm = border?.DataContext as ChainViewModel;
        if (chainVm == null) return;

        ClearChecks(chainVm);

        if (_activeCheckChain == chainVm)
            _activeCheckChain = null;

        // Hide action bar
        var actionBar = FindActionBar(expander);
        if (actionBar != null)
            actionBar.Visibility = Visibility.Collapsed;
    }

    private ChainViewModel? GetChainVmFromButton(object sender)
    {
        if (sender is not FrameworkElement fe) return null;
        var expander = FindVisualParent<Expander>(fe);
        if (expander == null) return null;
        var border = FindVisualParent<Border>(expander);
        return border?.DataContext as ChainViewModel;
    }

    private void BtnMoveItems_Click(object sender, RoutedEventArgs e)
    {
        var chainVm = GetChainVmFromButton(sender);
        if (chainVm == null) return;

        var checkedItems = chainVm.Items.Where(i => i.IsChecked).ToList();
        if (checkedItems.Count == 0) return;

        var dialog = new MoveItemsDialog(
            _main, chainVm.Source,
            checkedItems.Select(i => i.Source).ToList(),
            singleItemMode: false);
        dialog.Owner = Window.GetWindow(this);

        if (dialog.ShowDialog() == true)
        {
            // Refresh data after successful move
            _ = RefreshAfterWikiChange();
        }
    }

    private void BtnSetLevel_Click(object sender, RoutedEventArgs e)
    {
        var chainVm = GetChainVmFromButton(sender);
        if (chainVm == null) return;

        var checkedItems = chainVm.Items.Where(i => i.IsChecked).ToList();
        if (checkedItems.Count != 1) return;

        var dialog = new MoveItemsDialog(
            _main, chainVm.Source,
            checkedItems.Select(i => i.Source).ToList(),
            singleItemMode: true);
        dialog.Owner = Window.GetWindow(this);

        if (dialog.ShowDialog() == true)
        {
            _ = RefreshAfterWikiChange();
        }
    }

    private async void BtnRenameItem_Click(object sender, RoutedEventArgs e)
    {
        var chainVm = GetChainVmFromButton(sender);
        if (chainVm == null) return;

        var checkedItems = chainVm.Items.Where(i => i.IsChecked).ToList();
        if (checkedItems.Count == 0) return;

        if (checkedItems.Count == 1)
        {
            // Single rename
            var item = checkedItems[0].Source;
            var newName = ShowRenameDialog(item.Name, item.ItemType);
            if (newName == null) return;

            try
            {
                await WikiMappingService.PushItemNameAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword,
                    item.ItemType, newName);
                _main.ShowStatus($"Renamed {item.ItemType} → \"{newName}\"", InfoBarSeverity.Success);
                _ = RefreshAfterWikiChange();
            }
            catch (Exception ex)
            {
                _main.ShowStatus($"Rename failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }
        else
        {
            // Batch rename
            var renames = ShowBatchRenameDialog(checkedItems.Select(i => i.Source).ToList(), chainVm.Source);
            if (renames == null || renames.Count == 0) return;

            try
            {
                await WikiMappingService.PushItemNamesBatchAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword, renames);
                _main.ShowStatus($"Renamed {renames.Count} items", InfoBarSeverity.Success);
                _ = RefreshAfterWikiChange();
            }
            catch (Exception ex)
            {
                _main.ShowStatus($"Batch rename failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    /// <summary>
    /// Shows a batch rename dialog for multiple items.
    /// Returns list of (ItemType, NewName) pairs, or null if cancelled.
    /// </summary>
    private List<(string ItemType, string NewName)>? ShowBatchRenameDialog(List<ParsedItem> items, ParsedChain chain)
    {
        List<(string ItemType, string NewName)>? result = null;

        var window = new Wpf.Ui.Controls.FluentWindow
        {
            Title = "Rename Items",
            Width = 500,
            Height = Math.Min(240 + items.Count * 62, 600),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            MinWidth = 400, MinHeight = 280,
            Owner = Window.GetWindow(this),
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica,
        };
        ApplicationThemeManager.Apply(window);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Wpf.Ui.Controls.TitleBar { Title = "Rename Items", Height = 36 };
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        var contentPanel = new Grid { Margin = new Thickness(24, 10, 24, 20) };
        contentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // checkboxes
        contentPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // items
        contentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // buttons

        var textBoxes = new List<(string ItemType, Wpf.Ui.Controls.TextBox TextBox)>();

        // Find primary item name (non-alias at matching level)
        var primaryNameByLevel = chain.Items
            .Where(i => !i.IsAlias && !string.IsNullOrEmpty(i.Name) && !i.Name.StartsWith("Item_"))
            .ToDictionary(i => i.Level, i => i.Name);

        // Checkboxes panel
        var checkPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var chkSameName = new CheckBox
        {
            Content = "Use same name for all",
            ToolTip = "Set all items to the first item's name",
        };
        ToolTipService.SetInitialShowDelay(chkSameName, 150);

        var chkPrimaryName = new CheckBox
        {
            Content = "Use primary item names",
            ToolTip = "Set each item's name to the primary (non-alias) item's name at the same level",
            Margin = new Thickness(0, 4, 0, 0),
            IsEnabled = primaryNameByLevel.Count > 0,
        };
        ToolTipService.SetInitialShowDelay(chkPrimaryName, 150);

        checkPanel.Children.Add(chkSameName);
        checkPanel.Children.Add(chkPrimaryName);
        Grid.SetRow(checkPanel, 0);
        contentPanel.Children.Add(checkPanel);

        // Scrollable item list
        var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var itemsPanel = new StackPanel();

        foreach (var item in items)
        {
            var label = new TextBlock
            {
                Text = item.ItemType,
                FontSize = 11,
                Margin = new Thickness(0, textBoxes.Count > 0 ? 10 : 0, 0, 3),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

            var textBox = new Wpf.Ui.Controls.TextBox
            {
                Text = item.Name,
                PlaceholderText = "Item name...",
                FontSize = 13,
            };
            textBoxes.Add((item.ItemType, textBox));

            itemsPanel.Children.Add(label);
            itemsPanel.Children.Add(textBox);
        }

        scrollViewer.Content = itemsPanel;
        Grid.SetRow(scrollViewer, 1);
        contentPanel.Children.Add(scrollViewer);

        // Helper: disable/enable all textboxes
        void SetTextBoxesLocked(bool locked)
        {
            foreach (var (_, tb) in textBoxes)
                tb.IsEnabled = !locked;
        }

        // Live sync: typing in first textbox updates all others when checkbox is on
        if (textBoxes.Count > 0)
        {
            textBoxes[0].TextBox.TextChanged += (_, _) =>
            {
                if (chkSameName.IsChecked != true) return;
                var text = textBoxes[0].TextBox.Text;
                for (int i = 1; i < textBoxes.Count; i++)
                    textBoxes[i].TextBox.Text = text;
            };
        }

        // Wire "same name for all" checkbox
        chkSameName.Checked += (_, _) =>
        {
            chkPrimaryName.IsChecked = false;
            if (textBoxes.Count == 0) return;
            var firstName = textBoxes[0].TextBox.Text;
            for (int i = 1; i < textBoxes.Count; i++)
                textBoxes[i].TextBox.Text = firstName;
            for (int i = 1; i < textBoxes.Count; i++)
                textBoxes[i].TextBox.IsEnabled = false;
            if (textBoxes.Count > 0) textBoxes[0].TextBox.IsEnabled = true;
        };
        chkSameName.Unchecked += (_, _) => SetTextBoxesLocked(false);

        // Wire "use primary item names" checkbox
        chkPrimaryName.Checked += (_, _) =>
        {
            chkSameName.IsChecked = false;
            for (int i = 0; i < items.Count; i++)
            {
                if (primaryNameByLevel.TryGetValue(items[i].Level, out var primaryName))
                    textBoxes[i].TextBox.Text = primaryName;
            }
            SetTextBoxesLocked(true);
        };
        chkPrimaryName.Unchecked += (_, _) => SetTextBoxesLocked(false);

        // Buttons
        var btnSave = new Wpf.Ui.Controls.Button
        {
            Content = $"Save All ({items.Count})",
            Appearance = ControlAppearance.Primary,
            Padding = new Thickness(20, 6, 20, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var btnCancel = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Padding = new Thickness(20, 6, 20, 6),
        };
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttonPanel.Children.Add(btnSave);
        buttonPanel.Children.Add(btnCancel);
        Grid.SetRow(buttonPanel, 2);
        contentPanel.Children.Add(buttonPanel);

        Grid.SetRow(contentPanel, 1);
        grid.Children.Add(contentPanel);
        window.Content = grid;

        btnSave.Click += (_, _) =>
        {
            // Sync first textbox to all locked ones
            if (chkSameName.IsChecked == true && textBoxes.Count > 1)
            {
                var name = textBoxes[0].TextBox.Text;
                for (int i = 1; i < textBoxes.Count; i++)
                    textBoxes[i].TextBox.Text = name;
            }

            result = textBoxes
                .Select(t => (t.ItemType, t.TextBox.Text.Trim()))
                .Where(t => !string.IsNullOrEmpty(t.Item2))
                .ToList();
            window.DialogResult = true;
        };
        btnCancel.Click += (_, _) => window.DialogResult = false;
        window.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape) { window.DialogResult = false; ke.Handled = true; }
        };

        return window.ShowDialog() == true ? result : null;
    }

    /// <summary>
    /// Shows a simple rename dialog. Returns the new name, or null if cancelled.
    /// </summary>
    private string? ShowRenameDialog(string currentName, string itemType)
    {
        string? result = null;

        var window = new Wpf.Ui.Controls.FluentWindow
        {
            Title = "Rename Item",
            Width = 420,
            Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this),
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica,
        };
        ApplicationThemeManager.Apply(window);

        // --- Grid layout: Row 0 = TitleBar, Row 1 = content ---
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Wpf.Ui.Controls.TitleBar
        {
            Title = "Rename Item",
            Height = 36,
        };
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        // --- Content ---
        var contentPanel = new StackPanel { Margin = new Thickness(24, 10, 24, 20) };

        var label = new TextBlock
        {
            Text = itemType,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var textBox = new Wpf.Ui.Controls.TextBox
        {
            Text = currentName,
            PlaceholderText = "Item name...",
            FontSize = 14,
        };

        var btnSave = new Wpf.Ui.Controls.Button
        {
            Content = "Save",
            Appearance = ControlAppearance.Primary,
            Padding = new Thickness(20, 6, 20, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };

        var btnCancel = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Padding = new Thickness(20, 6, 20, 6),
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttonPanel.Children.Add(btnSave);
        buttonPanel.Children.Add(btnCancel);

        contentPanel.Children.Add(label);
        contentPanel.Children.Add(textBox);
        contentPanel.Children.Add(buttonPanel);

        Grid.SetRow(contentPanel, 1);
        grid.Children.Add(contentPanel);

        window.Content = grid;

        btnSave.Click += (_, _) =>
        {
            var name = textBox.Text.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                result = name;
                window.DialogResult = true;
            }
        };
        btnCancel.Click += (_, _) => window.DialogResult = false;

        textBox.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        // Enter = Save, Escape = Cancel
        window.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { btnSave.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { window.DialogResult = false; ke.Handled = true; }
        };

        return window.ShowDialog() == true ? result : null;
    }

    private async Task RefreshAfterWikiChange()
    {
        try
        {
            await _main.RefreshWikiMappingAsync();
        }
        catch (Exception ex)
        {
            _main.ShowStatus($"Failed to refresh: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ── Merge mode ────────────────────────────────────────────────────

    private void MergeMode_Click(object sender, RoutedEventArgs e)
    {
        _mergeMode = !_mergeMode;

        // Toggle checkbox visibility on all chains
        foreach (var vm in _allChains)
        {
            vm.ShowMergeCheckbox = _mergeMode;
            if (!_mergeMode) vm.IsMergeChecked = false;
        }

        // Toggle button appearance
        btnMergeMode.Appearance = _mergeMode
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        btnMergeModeHelp.Appearance = _mergeMode
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        txtMergeModeLabel.Text = _mergeMode ? "Exit Merge" : "Merge Mode";
        _splitSepBrush.Color = _mergeMode ? GetSplitSeparatorColor() : GetInactiveSeparatorColor();

        // Show/hide merge bar
        if (_mergeMode)
        {
            // Carry over item-level checks from normal mode
            if (_activeCheckChain != null && _activeCheckChain.Items.Any(i => i.IsChecked))
                _mergeCheckChains.Add(_activeCheckChain);

            mergeBar.Visibility = Visibility.Visible;
        }
        else
        {
            mergeBar.Visibility = Visibility.Collapsed;
            _mergeSelection.Clear();
            // Clear item-level checks from all tracked chains
            foreach (var c in _mergeCheckChains)
                ClearChecks(c);
            _mergeCheckChains.Clear();

            // Reset group checkbox visuals in visible items
            foreach (var groupCb in FindVisualChildren<CheckBox>(lvChains)
                .Where(c => c.Tag is string tag && tag.Length > 0))
                groupCb.IsChecked = false;
        }

        UpdateMergeBar();
    }

    private void MergeModeCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_mergeMode) MergeMode_Click(sender, e);
    }

    private void MergeSelectAll_Click(object sender, RoutedEventArgs e)
    {
        // Get currently visible (filtered) chains
        var visible = lvChains.ItemsSource as List<ChainViewModel>;
        if (visible == null || visible.Count == 0) return;

        // Toggle: if all visible are selected → deselect all, otherwise select all
        bool allSelected = visible.All(c => c.IsMergeChecked);

        foreach (var vm in visible)
        {
            vm.IsMergeChecked = !allSelected;
            if (!allSelected)
                _mergeSelection.Add(vm);
            else
                _mergeSelection.Remove(vm);
        }

        btnSelectAll.Content = allSelected ? "Select All" : "Deselect All";
        UpdateMergeBar();
    }

    private void MergeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not ChainViewModel vm) return;

        if (vm.IsMergeChecked)
            _mergeSelection.Add(vm);
        else
            _mergeSelection.Remove(vm);

        UpdateMergeBar();
    }

    /// <summary>
    /// Gets all chains with item-level checks (in merge mode: multiple chains, otherwise: just active).
    /// </summary>
    private IEnumerable<ChainViewModel> GetCheckedChains()
    {
        if (_mergeMode)
            return _mergeCheckChains.Where(c => c.Items.Any(i => i.IsChecked));
        if (_activeCheckChain != null)
            return [_activeCheckChain];
        return [];
    }

    /// <summary>
    /// Counts item-level checked source chain groups across all checked chains.
    /// </summary>
    private int GetItemLevelMergeGroupCount()
    {
        return GetCheckedChains()
            .SelectMany(c => c.Items)
            .Where(i => i.IsChecked)
            .Select(i => i.SourceChainKey)
            .Where(k => k.Length > 0)
            .Distinct()
            .Count();
    }

    /// <summary>
    /// Collects item-level merge selections as virtual ParsedChain objects grouped by SourceChainKey.
    /// </summary>
    private List<ChainViewModel> CollectItemLevelMergeChains()
    {
        var allChecked = GetCheckedChains()
            .SelectMany(c => c.Items)
            .Where(i => i.IsChecked)
            .ToList();

        if (allChecked.Count == 0) return new();

        var groups = allChecked
            .GroupBy(i => i.SourceChainKey)
            .Where(g => g.Key.Length > 0)
            .ToList();

        var result = new List<ChainViewModel>();
        foreach (var group in groups)
        {
            var virtualChain = new ParsedChain
            {
                ConfigKey = group.Key,
                DisplayName = group.Key,
                Items = group.Select(i => i.Source).ToList()
            };
            result.Add(new ChainViewModel(virtualChain, _main));
        }
        return result;
    }

    private void UpdateMergeBar()
    {
        int chainCount = _mergeSelection.Count;
        int itemGroupCount = GetItemLevelMergeGroupCount();
        int totalCount = chainCount + itemGroupCount;

        if (itemGroupCount > 0 && chainCount == 0)
            txtMergeCount.Text = $"{itemGroupCount} source chain{(itemGroupCount != 1 ? "s" : "")} selected (item-level)";
        else if (itemGroupCount > 0)
            txtMergeCount.Text = $"{chainCount} chain{(chainCount != 1 ? "s" : "")} + {itemGroupCount} source chain{(itemGroupCount != 1 ? "s" : "")} selected";
        else
            txtMergeCount.Text = $"{chainCount} chain{(chainCount != 1 ? "s" : "")} selected";

        btnMergeChains.IsEnabled = totalCount >= 2;

        // Update Select All label
        var visible = lvChains.ItemsSource as List<ChainViewModel>;
        if (visible != null && visible.Count > 0 && visible.All(c => c.IsMergeChecked))
            btnSelectAll.Content = "Deselect All";
        else
            btnSelectAll.Content = "Select All";
    }

    private async void BtnMergeChains_Click(object sender, RoutedEventArgs e)
    {
        // Collect chain-level selections
        var selected = _mergeSelection.OrderBy(c => c.DisplayName).ToList();

        // Collect item-level selections (source chain groups from expanded chain)
        var itemLevelChains = CollectItemLevelMergeChains();

        // Combine, avoiding duplicates by ConfigKey
        var existingKeys = new HashSet<string>(selected.Select(c => c.Source.ConfigKey));
        foreach (var ilc in itemLevelChains)
        {
            if (!existingKeys.Contains(ilc.Source.ConfigKey))
            {
                selected.Add(ilc);
                existingKeys.Add(ilc.Source.ConfigKey);
            }
        }

        int totalCount = selected.Count;
        if (totalCount < 2) return;

        // Pass parent chain name if item-level selections came from chains
        var checkedChains = GetCheckedChains().ToList();
        string? parentChainName = checkedChains.Count == 1 && itemLevelChains.Count > 0
            ? checkedChains[0].DisplayName
            : null;

        var dialog = new MergeChainsDialog(_main, selected, parentChainName);
        dialog.Owner = Window.GetWindow(this);

        if (dialog.ShowDialog() == true)
        {
            // Exit merge mode and refresh
            if (_mergeMode) MergeMode_Click(sender, e);
            await RefreshAfterWikiChange();
        }
    }

    // ── Split button styling ────────────────────────────────────────

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

    private static Color GetInactiveSeparatorColor() => Color.FromArgb(0x40, 0x80, 0x80, 0x80);

    private SolidColorBrush GetSplitSeparatorBrush() => new(GetInactiveSeparatorColor());

    private void RefreshSplitSeparatorColor() =>
        _splitSepBrush.Color = _mergeMode ? GetSplitSeparatorColor() : GetInactiveSeparatorColor();

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
        var border = FindVisualChild<Border>(control);
        if (border != null)
            border.CornerRadius = radius;
    }

    private void MergeModeHelp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MergeModeHelpDialog();
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }

    private void GenerateTable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not ChainViewModel vm)
            return;

        if (_main.DataService == null)
        {
            _main.ShowStatus("No data loaded.", InfoBarSeverity.Error);
            return;
        }

        var dialog = new TableGeneratorDialog(_main, vm.Source);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }

    private void GenerateInfobox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not ChainViewModel vm)
            return;

        if (_main.DataService == null)
        {
            _main.ShowStatus("No data loaded.", InfoBarSeverity.Error);
            return;
        }

        var dialog = new InfoboxGeneratorDialog(_main, vm.Source);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }

    private void CompareVariants_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not ChainViewModel vm)
            return;

        var basePath = _main.Settings.ChainItemOddsPath;
        if (string.IsNullOrEmpty(basePath))
        {
            _main.ShowStatus("No data file loaded.", InfoBarSeverity.Error);
            return;
        }

        var variants = VariantComparisonService.DiscoverVariants(basePath);
        if (variants.Count == 0)
        {
            _main.ShowStatus("No AB test variant directories found.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new VariantComparisonDialog(vm.Source, basePath, variants);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }
}
