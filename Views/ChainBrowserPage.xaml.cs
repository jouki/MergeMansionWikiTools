using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;

using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

// ── View model wrappers for data binding ─────────────────────────

public class ChainViewModel
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
    public bool HasLevelCollisions => Source.HasLevelCollisions;

    public List<ItemViewModel> Items { get; }

    /// <summary>
    /// Returns a grouped CollectionView when the chain has level collisions from multiple
    /// source chains, otherwise returns the plain Items list (no group headers).
    /// </summary>
    public object ItemsView { get; }

    /// <summary>Whether items are grouped by source chain (for margin binding).</summary>
    public bool HasGrouping { get; }

    /// <summary>Negative top margin to compensate for first group header spacing.</summary>
    public Thickness ItemsMargin => HasGrouping ? new Thickness(0, -14, 0, 0) : new Thickness(0);

    public ChainViewModel(ParsedChain source, MainWindow main)
    {
        Source = source;
        _main = main;
        Items = source.Items.Select(i => new ItemViewModel(i)).ToList();

        // Group by SourceChainKey when collisions come from multiple source chains
        var distinctSources = Items.Select(i => i.SourceChainKey).Where(k => k.Length > 0).Distinct().Count();
        if (source.HasLevelCollisions && distinctSources > 1)
        {
            HasGrouping = true;
            var view = new ListCollectionView(Items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemViewModel.SourceChainKey)));
            ItemsView = view;
        }
        else
        {
            ItemsView = Items;
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

    private static Brush HighlightBrush =>
        Application.Current.TryFindResource("AccentTextFillColorPrimaryBrush") as Brush
        ?? new SolidColorBrush(Color.FromRgb(0xCB, 0x9C, 0xFD));

    public ChainBrowserPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

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
        bool anyFilter = fGen || fSpawn || fProd || fEvent || fNamed || fCollisions;

        if (anyFilter)
        {
            if (fGen) filtered = filtered.Where(c => c.Source.HasGenerators);
            if (fSpawn) filtered = filtered.Where(c => c.Source.HasSpawners);
            if (fProd) filtered = filtered.Where(c => !c.Source.HasGenerators && !c.Source.HasSpawners);
            if (fEvent) filtered = filtered.Where(c => c.Source.IsEventChain);
            if (fNamed) filtered = filtered.Where(c => c.Source.HasHumanReadableName);
            if (fCollisions) filtered = filtered.Where(c => c.Source.HasLevelCollisions);
        }

        var result = filtered.ToList();
        lvChains.ItemsSource = result;
        txtChainCount.Text = $"{result.Count} / {_allChains.Count} chains";
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

        _main.NavigateToImageSplitterChainMode(vm.Source);
    }

    // ── Item selection & action bar ──────────────────────────────────

    private void ClearChecks(ChainViewModel vm)
    {
        foreach (var item in vm.Items)
            item.IsChecked = false;
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

        // Set Level visible only when exactly 1 item checked
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
                    else if (content == "Move Items")
                    {
                        btn.IsEnabled = wikiVerified;
                        btn.ToolTip = wikiVerified ? null : "Wiki connection required";
                    }
                }
            }
        }
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

        if (_activeCheckChain != null && _activeCheckChain != chainVm)
            ClearChecks(_activeCheckChain);
        _activeCheckChain = chainVm;

        foreach (var item in chainVm.Items.Where(i => i.SourceChainKey == groupKey))
            item.IsChecked = isChecked;

        var actionBar = FindActionBar(expander);
        if (actionBar != null)
            UpdateActionBar(chainVm, actionBar);
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

        // Clear other chain's checks
        if (_activeCheckChain != null && _activeCheckChain != chainVm)
            ClearChecks(_activeCheckChain);
        _activeCheckChain = chainVm;

        // Toggle items in this group only
        foreach (var item in chainVm.Items.Where(i => i.SourceChainKey == groupKey))
            item.IsChecked = isChecked;

        var actionBar = FindActionBar(expander);
        if (actionBar != null)
            UpdateActionBar(chainVm, actionBar);
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

        if (_activeCheckChain != null && _activeCheckChain != chainVm)
            ClearChecks(_activeCheckChain);
        _activeCheckChain = chainVm;

        var actionBar = FindActionBar(expander);
        if (actionBar != null)
            UpdateActionBar(chainVm, actionBar);
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

        // Per-chain scope: clear checks from other chains
        if (_activeCheckChain != null && _activeCheckChain != chainVm)
        {
            ClearChecks(_activeCheckChain);
            // Hide old action bar (if still visible in the UI)
        }
        _activeCheckChain = chainVm;

        // Find action bar in this expander's content
        var actionBar = FindActionBar(expander);
        if (actionBar != null)
            UpdateActionBar(chainVm, actionBar);
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
}
