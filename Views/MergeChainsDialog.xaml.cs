using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public class MergeChainItem : INotifyPropertyChanged
{
    public ChainViewModel Source { get; }
    public string DisplayName => Source.DisplayName;
    public string ConfigKey => Source.Source.ConfigKey;
    public int ItemCount => Source.Source.Items.Count;

    /// <summary>Whether all items in this chain are aliases (not a primary/main chain).</summary>
    public bool IsAliasChain => Source.Source.Items.All(i => i.IsAlias);

    /// <summary>Whether this is a main (non-alias) chain — shown as "Primary" badge.</summary>
    public bool IsMainChain => !IsAliasChain;

    /// <summary>Whether this item represents an existing chain found on wiki (injected at top).</summary>
    public bool IsExistingChain { get; set; }

    private bool _isPrimary;
    public bool IsPrimary
    {
        get => _isPrimary;
        set { _isPrimary = value; PropertyChanged?.Invoke(this, new(nameof(IsPrimary))); }
    }

    private bool _showRadio = true;
    public bool ShowRadio
    {
        get => _showRadio;
        set { _showRadio = value; PropertyChanged?.Invoke(this, new(nameof(ShowRadio))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MergeChainItem(ChainViewModel source) => Source = source;
}

public partial class MergeChainsDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly List<MergeChainItem> _items;

    // Autocomplete
    private readonly string[] _allNames;
    private readonly string[] _allNamesLower;
    private readonly Dictionary<string, ParsedChain> _chainByName;
    private const int MaxSuggestions = 30;
    private CancellationTokenSource? _searchCts;
    private bool _suppressTextChanged;

    // Existing chain detection
    private ParsedChain? _existingPrimaryChain;
    private MergeChainItem? _injectedExistingItem;
    private readonly HashSet<string> _originalConfigKeys;

    public MergeChainsDialog(MainWindow main, IReadOnlyList<ChainViewModel> selectedChains, string? parentChainName = null)
    {
        _main = main;
        _items = selectedChains.Select(c => new MergeChainItem(c)).ToList();
        _originalConfigKeys = new HashSet<string>(_items.Select(i => i.ConfigKey));

        // Build autocomplete data
        var chains = main.DataService?.Chains ?? new List<ParsedChain>();
        var nameSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        _chainByName = new Dictionary<string, ParsedChain>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chains)
        {
            nameSet.Add(c.DisplayName);
            _chainByName.TryAdd(c.DisplayName, c);
        }
        _allNames = nameSet.ToArray();
        _allNamesLower = _allNames.Select(n => n.ToLowerInvariant()).ToArray();

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        icChains.ItemsSource = _items;

        // Measure desired height, cap at 900px, then set as fixed Height (user can resize)
        Loaded += (_, _) =>
        {
            SizeToContent = SizeToContent.Height;
            UpdateLayout();
            var desired = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            Height = Math.Min(desired, 900);

            // Shift up ~200px from default center position
            Top = Math.Max(0, Top - 130);
        };

        // Default primary: prefer first non-alias chain, fallback to first
        var defaultPrimary = _items.FirstOrDefault(i => !i.IsAliasChain) ?? _items.FirstOrDefault();
        if (defaultPrimary != null)
        {
            defaultPrimary.IsPrimary = true;
            _suppressTextChanged = true;
            // Pre-fill name: parent chain name (from wiki) > primary's display name
            txtMergedName.Text = parentChainName ?? defaultPrimary.DisplayName;
            _suppressTextChanged = false;
        }

        UpdateState();
    }

    private void PrimaryRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.DataContext is not MergeChainItem clicked) return;

        // Update primary flags
        foreach (var item in _items)
            item.IsPrimary = item == clicked;

        // Don't overwrite name if existing chain is injected (name drives the detection)
        if (!clicked.IsExistingChain)
        {
            _suppressTextChanged = true;
            txtMergedName.Text = clicked.DisplayName;
            _suppressTextChanged = false;
        }
        UpdateState();
    }

    private void TxtMergedName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        var query = txtMergedName.Text?.Trim() ?? "";

        if (query.Length < 1)
        {
            suggestPopup.IsOpen = false;
            UpdateState();
            return;
        }

        var names = _allNames;
        var namesLower = _allNamesLower;
        var queryLower = query.ToLowerInvariant();

        // Check for exact match — show popup only if not exact
        if (_chainByName.ContainsKey(query))
        {
            suggestPopup.IsOpen = false;
            UpdateState();
            return;
        }

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
                if (prefixMatches.Count + containsMatches.Count >= MaxSuggestions) break;
            }

            var results = new List<string>(prefixMatches);
            results.AddRange(containsMatches.Take(MaxSuggestions - results.Count));

            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested) return;
                suggestList.ItemsSource = results;
                suggestPopup.IsOpen = results.Count > 0;
                UpdateState();
            });
        }, token);
    }

    private void TxtMergedName_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!suggestPopup.IsOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                if (suggestList.SelectedIndex < suggestList.Items.Count - 1)
                    suggestList.SelectedIndex++;
                e.Handled = true;
                break;

            case Key.Up:
                if (suggestList.SelectedIndex > 0)
                    suggestList.SelectedIndex--;
                e.Handled = true;
                break;

            case Key.Enter:
                if (suggestList.SelectedItem is string selected)
                {
                    ApplySuggestion(selected);
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                suggestPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SuggestList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (suggestList.SelectedItem is string selected)
            ApplySuggestion(selected);
    }

    private void ApplySuggestion(string name)
    {
        _suppressTextChanged = true;
        txtMergedName.Text = name;
        txtMergedName.CaretIndex = name.Length;
        _suppressTextChanged = false;
        suggestPopup.IsOpen = false;
        UpdateState();
    }

    private void UpdateState()
    {
        var name = txtMergedName.Text?.Trim() ?? "";
        bool hasName = !string.IsNullOrWhiteSpace(name);

        // Check if entered name matches an existing chain not already in selection
        _chainByName.TryGetValue(name, out var existingChain);
        bool isExistingChain = hasName && existingChain != null
            && !_originalConfigKeys.Contains(existingChain.ConfigKey);

        if (isExistingChain)
        {
            _existingPrimaryChain = existingChain!;

            // Inject existing chain at top if not already there
            if (_injectedExistingItem == null || _injectedExistingItem.ConfigKey != existingChain!.ConfigKey)
            {
                // Remove previous injection
                RemoveInjectedItem();

                var virtualChain = new ChainViewModel(existingChain, _main);
                _injectedExistingItem = new MergeChainItem(virtualChain) { IsExistingChain = true };
                _items.Insert(0, _injectedExistingItem);
            }

            // Select existing as primary, show all radios
            foreach (var item in _items)
            {
                item.ShowRadio = true;
                item.IsPrimary = item == _injectedExistingItem;
            }

            icChains.ItemsSource = null;
            icChains.ItemsSource = _items;

            infoBar.Severity = InfoBarSeverity.Informational;
            infoBar.Title = "Existing Chain";
            infoBar.Message = $"Chain \"{name}\" already exists ({existingChain!.Items.Count} items). Select primary chain — all others will get isAlias = true.";

            var aliasCount = _items.Count - 1;
            txtStatus.Text = $"Primary: {_injectedExistingItem!.ConfigKey} — {aliasCount} alias chain{(aliasCount != 1 ? "s" : "")} will get isAlias = true";
            btnMerge.IsEnabled = true;
        }
        else
        {
            _existingPrimaryChain = null;

            // Remove injected item if switching back to normal mode
            if (_injectedExistingItem != null)
            {
                RemoveInjectedItem();
                icChains.ItemsSource = null;
                icChains.ItemsSource = _items;
            }

            // Show radio buttons
            foreach (var item in _items)
                item.ShowRadio = true;

            infoBar.Severity = InfoBarSeverity.Informational;
            infoBar.Title = "Chain Merge";
            infoBar.Message = "Select the primary chain (radio). Alias chains will be mapped to the primary chain's name on the wiki. Level collisions between merged chains won't show warnings.";

            var primary = _items.FirstOrDefault(i => i.IsPrimary);
            bool hasPrimary = primary != null;

            btnMerge.IsEnabled = hasName && hasPrimary;

            if (hasPrimary && hasName)
            {
                var aliasCount = _items.Count - 1;
                txtStatus.Text = $"Primary: {primary!.ConfigKey} — {aliasCount} alias chain{(aliasCount != 1 ? "s" : "")} will get isAlias = true";
            }
            else if (!hasPrimary)
            {
                txtStatus.Text = "Select a primary chain (radio button)";
            }
            else
            {
                txtStatus.Text = "Enter a merged chain name";
            }
        }
    }

    private void RemoveInjectedItem()
    {
        if (_injectedExistingItem != null)
        {
            _items.Remove(_injectedExistingItem);
            // If injected item was primary, reset to first non-alias
            if (_injectedExistingItem.IsPrimary)
            {
                var newPrimary = _items.FirstOrDefault(i => !i.IsAliasChain) ?? _items.FirstOrDefault();
                if (newPrimary != null) newPrimary.IsPrimary = true;
            }
            _injectedExistingItem = null;
        }
    }

    private async void BtnMerge_Click(object sender, RoutedEventArgs e)
    {
        var mergedName = txtMergedName.Text.Trim();
        if (string.IsNullOrEmpty(mergedName)) return;

        btnMerge.IsEnabled = false;
        txtStatus.Text = "Pushing merge to wiki...";

        try
        {
            var primary = _items.FirstOrDefault(i => i.IsPrimary);
            if (primary == null) return;

            // Filter out the injected existing chain item — it's already on wiki
            var aliasChains = _items
                .Where(i => !i.IsPrimary && !i.IsExistingChain)
                .Select(i => i.Source.Source).ToList();

            if (_existingPrimaryChain != null && primary.IsExistingChain)
            {
                await WikiMappingService.PushAddAliasesAsync(
                    _main.Settings.WikiUsername!,
                    _main.Settings.WikiPassword!,
                    aliasChains,
                    mergedName);
            }
            else
            {
                await WikiMappingService.PushMergeAliasAsync(
                    _main.Settings.WikiUsername!,
                    _main.Settings.WikiPassword!,
                    primary.Source.Source,
                    aliasChains,
                    mergedName);
            }

            // Rename alias items to match primary item names (by level)
            if (chkRenameAliases.IsChecked == true)
            {
                txtStatus.Text = "Renaming alias items...";
                var primaryItems = primary.Source.Source.Items;
                var nameByLevel = primaryItems
                    .Where(i => !string.IsNullOrEmpty(i.Name) && !i.Name.StartsWith("Item_"))
                    .ToDictionary(i => i.Level, i => i.Name);

                var renames = new List<(string ItemType, string NewName)>();
                foreach (var chain in aliasChains)
                    foreach (var item in chain.Items)
                        if (nameByLevel.TryGetValue(item.Level, out var name))
                            renames.Add((item.ItemType, name));

                if (renames.Count > 0)
                {
                    await WikiMappingService.PushItemNamesBatchAsync(
                        _main.Settings.WikiUsername!,
                        _main.Settings.WikiPassword!,
                        renames);
                }
            }

            _main.ShowStatus($"Chains merged as \"{mergedName}\" on wiki.", InfoBarSeverity.Success);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.Message = ex.Message;
            btnMerge.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
