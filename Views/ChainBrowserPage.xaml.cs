using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MergeMansionWikiTools.Helpers;
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
    public bool IsUnmappedStraggler => Source.IsUnmappedStraggler;
    public string StragglerTooltip => Source.StragglerParentWikiName is { Length: > 0 } parent
        ? $"Not in wiki mapping — split from \"{parent}\". Move these items to add their mapping."
        : "Not in wiki mapping — sibling items in this game chain are already on the wiki.";

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

    /// <summary>Whether the "Save Variant Labels" button shows — chain has ≥1 variant item.</summary>
    public bool ShowVariantLabelButton => Items.Any(i => i.IsVariant);

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
        // variants group the same way as aliases (own "Variants" bucket in alias mode)
        HasAliasItems = Items.Any(i => i.IsAlias || i.IsVariant);

        ComputeVariantLetterHints();
        RebuildItemsView();
    }

    /// <summary>
    /// Watermarks each variant's empty label box with the positional letter the wiki will assign.
    /// Mirrors the Module:Items ordering (deterministic since rev 49880):
    /// • chest/rod chains — ALL boxes (incl. non-variant plain chests, excl. aliases) sorted by
    ///   variantOrder-first (asc) → level → name → itemType; letter = position (A + index).
    /// • other chains — variants only, shared WikiTableGenerator.SortVariants order.
    /// Letters shift when variantOrder edits are SAVED (VMs rebuild from the refreshed mapping).
    /// </summary>
    private void ComputeVariantLetterHints()
    {
        static string Letter(int i) => i < 26 ? ((char)('A' + i)).ToString() : "A" + (char)('A' + i - 26);

        // Chest-loot table columns: every chest/fishing-rod item of the chain is a box.
        var boxes = Items.Where(i => !i.IsAlias && (i.Source.IsChest || i.Source.IsFishingRod))
            .OrderBy(i => i.Source.MappingVariantOrder.HasValue ? 0 : 1)
            .ThenBy(i => i.Source.MappingVariantOrder ?? int.MaxValue)
            .ThenBy(i => i.Source.Level)
            .ThenBy(i => i.Source.Name, StringComparer.Ordinal)
            .ThenBy(i => i.ItemType, StringComparer.Ordinal)
            .ToList();
        if (boxes.Count >= 2)
        {
            // Letters only where disambiguation is needed (mirrors Module:Items rev 49888):
            // a positional letter shows ONLY when another box shares the same level — a single
            // box per tier is already identified by its icon/level and gets no letter.
            var perLevel = boxes.GroupBy(b => b.Source.Level).ToDictionary(g => g.Key, g => g.Count());
            for (int i = 0; i < boxes.Count; i++)
                if (perLevel[boxes[i].Source.Level] > 1)
                    boxes[i].VariantLetterHint = Letter(i);
            // Non-variant boxes (plain chests) have no label field, but they still occupy a wiki
            // column — show their letter as a static badge when a variant exists at their level,
            // so the app row ↔ wiki column mapping is complete (e.g. plain Red chest = G).
            foreach (var b in boxes.Where(b => !b.IsVariant && b.VariantLetterHint.Length > 0))
                b.ShowStaticLetter = boxes.Any(v => v.IsVariant && v.Source.Level == b.Source.Level);
            return;
        }

        // No chest table — positional letters among the variant items (Decay Odds / Merge Stages
        // variant columns use the shared variantSort = SortVariants order). Same per-level rule:
        // a lone variant on its level needs no letter.
        var variants = Items.Where(i => i.IsVariant && !i.IsAlias).ToList();
        if (variants.Count < 2) return;
        var perLevelV = variants.GroupBy(v => v.Source.Level).ToDictionary(g => g.Key, g => g.Count());
        var sorted = WikiTableGenerator.SortVariants(variants.Select(v => v.Source));
        for (int i = 0; i < sorted.Count; i++)
        {
            if (perLevelV.GetValueOrDefault(sorted[i].Level) <= 1) continue;
            var vm = variants.FirstOrDefault(v => ReferenceEquals(v.Source, sorted[i]));
            if (vm != null) vm.VariantLetterHint = Letter(i);
        }
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
            // Remap CheckedGroups per flag bucket: individual keys → bucket if all were checked
            RemapFlagBucket(i => i.IsAlias, "Aliases");
            RemapFlagBucket(i => i.IsVariant && !i.IsAlias, "Variants");

            var view = new ListCollectionView(Items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemViewModel.AliasGroupKey)));
            ItemsView = view;
        }
        else
        {
            // Remap CheckedGroups: buckets → expand back to individual SourceChainKeys
            if (CheckedGroups.Remove("Aliases"))
            {
                foreach (var k in Items.Where(i => i.IsAlias).Select(i => i.SourceChainKey).Distinct())
                    CheckedGroups.Add(k);
            }
            if (CheckedGroups.Remove("Variants"))
            {
                foreach (var k in Items.Where(i => i.IsVariant && !i.IsAlias).Select(i => i.SourceChainKey).Distinct())
                    CheckedGroups.Add(k);
            }

            var view = new ListCollectionView(Items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemViewModel.SourceChainKey)));
            ItemsView = view;
        }
    }

    /// <summary>Collapses individual per-source group keys of flagged items into one named
    /// bucket ("Aliases"/"Variants") when all of them were checked, else clears both forms.</summary>
    private void RemapFlagBucket(Func<ItemViewModel, bool> flag, string bucket)
    {
        var keys = Items.Where(flag).Select(i => i.SourceChainKey).Distinct().ToList();
        if (keys.Count > 0 && keys.All(k => CheckedGroups.Contains(k)))
        {
            foreach (var k in keys) CheckedGroups.Remove(k);
            CheckedGroups.Add(bucket);
        }
        else
        {
            foreach (var k in keys) CheckedGroups.Remove(k);
            CheckedGroups.Remove(bucket);
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
    public bool IsVariant => Source.IsVariant;
    public string? VariantLabel => Source.VariantLabel;

    /// <summary>Inline-editable variant label (isVariant = "Spring"). Defaults to the live mapping
    /// value; edits are committed in a batch by the "Save Labels" button. Empty = plain isVariant = true.</summary>
    private string? _variantLabelEdit;
    private bool _variantLabelEditSet;
    public string VariantLabelEdit
    {
        get => _variantLabelEditSet ? (_variantLabelEdit ?? "") : (Source.MappingVariantLabel ?? "");
        set { _variantLabelEdit = value; _variantLabelEditSet = true; }
    }

    private string? _variantOrderEdit;
    private bool _variantOrderEditSet;
    /// <summary>Inline-editable variant order (variantOrder = N). Empty = unset. Committed by Save Mapping.</summary>
    public string VariantOrderEdit
    {
        get => _variantOrderEditSet ? (_variantOrderEdit ?? "")
             : (Source.MappingVariantOrder?.ToString() ?? "");
        set { _variantOrderEdit = value; _variantOrderEditSet = true; }
    }

    /// <summary>Watermark for the empty label box: the positional letter the wiki assigns when no
    /// label is set (mirrors Module:Items chest-column sort — deterministic since rev 49880).
    /// Computed by <see cref="ChainViewModel"/> after items are built; empty when not applicable.</summary>
    public string VariantLetterHint { get; set; } = "";

    /// <summary>Show <see cref="VariantLetterHint"/> as a STATIC badge (no edit field) — set on
    /// non-variant chest boxes that still occupy a wiki column, when a variant exists at their level.</summary>
    public bool ShowStaticLetter { get; set; }

    /// <summary>True when the label OR order edit differs from the published mapping value
    /// — keeps the action bar (with Save Mapping) visible even with nothing checked.</summary>
    public bool VariantMappingDirty =>
        Source.IsVariant && (
            (VariantLabelEdit ?? "").Trim() != (Source.MappingVariantLabel ?? "")
            || (VariantOrderEdit ?? "").Trim() != (Source.MappingVariantOrder?.ToString() ?? ""));

    /// <summary>Grouping key for alias mode: aliases → "Aliases", variants → "Variants", others → SourceChainKey.</summary>
    public string AliasGroupKey => Source.IsAlias ? "Aliases" : Source.IsVariant ? "Variants" : SourceChainKey;

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
            // Classification flags first — they decide whether the item gets its own wiki row,
            // so they matter more at a glance than what the item does. ALIAS had no badge at all
            // before: its only cue was "always show the ItemType", which the global Show IDs
            // toggle makes invisible by showing IDs on every item.
            if (Source.IsAlias) parts.Add("ALIAS");
            if (Source.IsVariant) parts.Add("VAR");
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
    private readonly Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(250));

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
        chkStragglers.IsChecked = s.FilterStragglers;
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

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        _searchDebounce.Trigger(ApplyFilter);
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
        s.FilterStragglers = chkStragglers.IsChecked == true;
        _main.SaveSettings();
    }

    /// <summary>Splits search text into non-empty whitespace-separated tokens.</summary>
    private static string[] SplitSearchTokens(string search) =>
        string.IsNullOrWhiteSpace(search)
            ? Array.Empty<string>()
            : search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when EVERY token is found in at least one searchable field
    /// (DisplayName / ConfigKey / any item name). Case-insensitive substring match.</summary>
    private static bool MatchesAllTokens(ChainViewModel c, string[] tokens)
    {
        foreach (var tok in tokens)
        {
            bool matched = c.DisplayName.Contains(tok, StringComparison.OrdinalIgnoreCase)
                        || c.ConfigKey.Contains(tok, StringComparison.OrdinalIgnoreCase)
                        || c.Items.Any(i => i.Name.Contains(tok, StringComparison.OrdinalIgnoreCase));
            if (!matched) return false;
        }
        return true;
    }

    private void ApplyFilter()
    {
        using var _t = AppLogger.Timed("FilterChains");
        if (lvChains == null) return;

        var search = txtSearch?.Text?.Trim() ?? "";
        _currentSearch = search;

        IEnumerable<ChainViewModel> filtered = _allChains;

        // Text search — tokenize by whitespace, each token must match ANY searchable field
        // (DisplayName/ConfigKey/item names). Token AND, field OR. So "tools beach" matches
        // "Tools (Beach Shack)" because BOTH "tools" and "beach" appear in DisplayName.
        var tokens = SplitSearchTokens(search);
        if (tokens.Length > 0)
        {
            filtered = filtered.Where(c => MatchesAllTokens(c, tokens));
        }

        // Category filters (combinable — if none checked, show all)
        bool fGen = chkGenerators?.IsChecked == true;
        bool fSpawn = chkSpawners?.IsChecked == true;
        bool fProd = chkProducts?.IsChecked == true;
        bool fEvent = chkEvent?.IsChecked == true;
        bool fNamed = chkNamed?.IsChecked == true;
        bool fCollisions = chkCollisions?.IsChecked == true;
        bool fABDiffs = chkABDiffs?.IsChecked == true;
        bool fStragglers = chkStragglers?.IsChecked == true;
        bool anyFilter = fGen || fSpawn || fProd || fEvent || fNamed || fCollisions || fABDiffs || fStragglers;

        if (anyFilter)
        {
            if (fGen) filtered = filtered.Where(c => c.Source.HasGenerators);
            if (fSpawn) filtered = filtered.Where(c => c.Source.HasSpawners);
            if (fProd) filtered = filtered.Where(c => !c.Source.HasGenerators && !c.Source.HasSpawners);
            if (fEvent) filtered = filtered.Where(c => c.Source.IsEventChain);
            if (fNamed) filtered = filtered.Where(c => c.Source.HasHumanReadableName);
            if (fCollisions) filtered = filtered.Where(c => c.Source.HasLevelCollisions);
            if (fABDiffs) filtered = filtered.Where(c => c.HasVariantDiffs == true);
            if (fStragglers) filtered = filtered.Where(c => c.Source.IsUnmappedStraggler);
        }

        var result = filtered.ToList();
        AppLogger.Info($"FilterChains: {result.Count}/{_allChains.Count} matches, setting ItemsSource...");
        lvChains.ItemsSource = result;
        txtChainCount.Text = $"{result.Count} / {_allChains.Count} chains";

        // Unmapped-straggler banner — surfaced so partially-adopted chains with items missing from the
        // wiki mapping aren't overlooked. Hidden while the Unmapped filter is on (list already shows them).
        var stragglerCount = _allChains.Count(c => c.Source.IsUnmappedStraggler);
        if (stragglerBanner != null)
        {
            if (stragglerCount > 0 && !fStragglers)
            {
                txtStragglerBanner.Text = $"{stragglerCount} chain{(stragglerCount != 1 ? "s have" : " has")} " +
                    "items missing from the wiki mapping";
                stragglerBanner.Visibility = Visibility.Visible;
            }
            else
            {
                stragglerBanner.Visibility = Visibility.Collapsed;
            }
        }

        // Show hint when search matches exist but are hidden by filters
        if (anyFilter && tokens.Length > 0)
        {
            var searchOnly = _allChains.Where(c => MatchesAllTokens(c, tokens));
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
        if (sender is TextBlock tb) RenderItemName(tb);
    }

    /// <summary>Renders an item-name TextBlock: name (with search highlight) + optional variant label +
    /// distinguishing identifier. The raw ItemType (ID) is shown for aliases/variants always, and for
    /// every item when the global "Show IDs" toggle is on.</summary>
    private void RenderItemName(TextBlock tb)
    {
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

        // Show distinguishing identifier. Aliases/variants ALWAYS show the ItemType so same-named boxes
        // are distinguishable; every item shows it when the global "Show IDs" toggle is on.
        if (vm.IsAlias || vm.IsVariant || _main.Settings.ShowItemIds)
        {
            var idRun = new Run($"  ({vm.Source.ItemType})") { FontSize = 10 };
            idRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
            tb.Inlines.Add(idRun);
        }
        else if (!string.IsNullOrEmpty(vm.Source.OriginalName)
            && vm.Source.OriginalName != vm.Name)
        {
            // Non-alias: show original JSON name when item was renamed via wiki
            var rawRun = new Run($"  ({vm.Source.OriginalName})") { FontSize = 10 };
            rawRun.SetResourceReference(Run.ForegroundProperty, "TextFillColorTertiaryBrush");
            tb.Inlines.Add(rawRun);
        }
    }

    private void ShowIds_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb) cb.IsChecked = _main.Settings.ShowItemIds;
    }

    private void ShowIds_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        _main.Settings.ShowItemIds = cb.IsChecked == true;
        _main.SaveSettings();
        // Re-render every visible item-name TextBlock so the change applies without reopening chains.
        foreach (var t in FindVisualChildren<TextBlock>(this).Where(t => t.Tag is ItemViewModel))
            RenderItemName(t);
    }

    private void StragglerBanner_Click(object sender, MouseButtonEventArgs e)
    {
        // Turn on the Unmapped filter so the user sees exactly the flagged chains to review.
        if (chkStragglers != null) chkStragglers.IsChecked = true;
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
        if (chkStragglers?.IsChecked == true) count++;

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
        // Unsaved variant-label edits keep the bar (and its Save Labels button) up even
        // with nothing checked — the labels are edited inline, without checking boxes.
        bool labelsDirty = chainVm.Items.Any(i => i.VariantMappingDirty);
        bool hasVariants = chainVm.Items.Any(i => i.IsVariant);

        if (count == 0 && !labelsDirty)
        {
            actionBar.Visibility = Visibility.Collapsed;
            return;
        }

        actionBar.Visibility = Visibility.Visible;

        // Save Mapping: shown whenever the chain has variants; enabled only when something changed
        foreach (var child in actionBar.Children)
        {
            if (child is Wpf.Ui.Controls.Button sb && sb.Name == "btnSaveLabels")
            {
                sb.Visibility = hasVariants ? Visibility.Visible : Visibility.Collapsed;
                sb.IsEnabled = labelsDirty && _main.Settings.WikiVerified;
                sb.ToolTip = !_main.Settings.WikiVerified
                    ? "Wiki connection required"
                    : labelsDirty ? "Publish variant labels and order to the wiki mapping"
                    : "No unsaved mapping changes";
            }
            // Group Drop Odds by variant: shown for variant chains; reflects current mapping state
            if (child is System.Windows.Controls.CheckBox gc && gc.Name == "chkGroupOdds")
            {
                gc.Visibility = hasVariants ? Visibility.Visible : Visibility.Collapsed;
                gc.IsChecked = chainVm.Items.Any(i => i.IsVariant && i.Source.MappingGroupOdds);
                gc.IsEnabled = _main.Settings.WikiVerified;
            }
        }

        // Count text: hide the selection-only buttons when nothing is checked (label-only mode)
        foreach (var child in actionBar.Children)
        {
            if (child is TextBlock tb)
            {
                tb.Text = count > 0 ? $"{count} selected" : "Unsaved labels";
                break;
            }
        }

        if (count == 0)
        {
            // Label-only mode: only Save Labels is relevant
            foreach (var child in actionBar.Children)
                if (child is Wpf.Ui.Controls.Button b && b.Name != "btnSaveLabels")
                    b.Visibility = Visibility.Collapsed;
            return;
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
                    else if (content is "Set as Alias" or "Remove Alias")
                    {
                        btn.Visibility = count >= 1 ? Visibility.Visible : Visibility.Collapsed;
                        btn.IsEnabled = wikiVerified;
                        btn.ToolTip = wikiVerified ? null : "Wiki connection required";
                        // Label reflects the toggle direction: only when EVERY checked item is
                        // already an alias does the action remove the flag; otherwise it sets all
                        // selected items as aliases (normalising a mixed selection to alias=true).
                        var checkedItems = chainVm.Items.Where(i => i.IsChecked).ToList();
                        bool allAlias = checkedItems.Count > 0 && checkedItems.All(i => i.IsAlias);
                        btn.Content = allAlias ? "Remove Alias" : "Set as Alias";
                    }
                    else if (content is "Set as Variant" or "Remove Variant")
                    {
                        btn.Visibility = count >= 1 ? Visibility.Visible : Visibility.Collapsed;
                        btn.IsEnabled = wikiVerified;
                        btn.ToolTip = wikiVerified ? null : "Wiki connection required";
                        var checkedItems = chainVm.Items.Where(i => i.IsChecked).ToList();
                        bool allVariant = checkedItems.Count > 0 && checkedItems.All(i => i.IsVariant);
                        btn.Content = allVariant ? "Remove Variant" : "Set as Variant";
                    }
                }
            }
        }
    }

    // ── Right-click "Copy ID" context menu (chain header / group / item rows) ──

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { /* clipboard can be transiently locked by another app */ }
    }

    /// <summary>Chain header → copies the chain ConfigKey.</summary>
    private void CopyChainId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChainViewModel vm)
            CopyToClipboard(vm.Source.ConfigKey);
    }

    /// <summary>Group header → copies the group key (source-chain ID).</summary>
    private void CopyGroupId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is System.Windows.Data.CollectionViewGroup g)
            CopyToClipboard(g.Name as string);
    }

    /// <summary>Item row → copies the item ItemType.</summary>
    private void CopyItemId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ItemViewModel vm)
            CopyToClipboard(vm.Source.ItemType);
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
    /// Gets items DISPLAYED under a group header. Handles the "Aliases"/"Variants" buckets, and for a
    /// plain SourceChainKey group matches by the same key the view groups on (<see cref="ItemViewModel.AliasGroupKey"/>
    /// in alias mode) — otherwise a variant/alias sharing this SourceChainKey (but shown in the "Variants"/
    /// "Aliases" bucket) would be ticked too when the header checkbox is clicked.
    /// </summary>
    private static IEnumerable<ItemViewModel> GetGroupItems(ChainViewModel chainVm, string groupKey)
    {
        if (groupKey == "Aliases") return chainVm.Items.Where(i => i.IsAlias);
        if (groupKey == "Variants") return chainVm.Items.Where(i => i.IsVariant && !i.IsAlias);
        bool aliasMode = chainVm.GroupAliases && chainVm.HasAliasItems;
        return aliasMode
            ? chainVm.Items.Where(i => i.AliasGroupKey == groupKey)
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

    private async void BtnToggleAlias_Click(object sender, RoutedEventArgs e)
        => await ToggleMappingFlagAsync(GetChainVmFromButton(sender), "isAlias", "alias", i => i.IsAlias);

    private async void BtnToggleVariant_Click(object sender, RoutedEventArgs e)
        => await ToggleMappingFlagAsync(GetChainVmFromButton(sender), "isVariant", "variant", i => i.IsVariant);

    /// <summary>
    /// Shared toggle for a boolean mapping flag (isAlias / isVariant) over the checked items: fetch
    /// live mapping → compute exact before/after per item → preview diff → publish. Direction is
    /// "remove" only when EVERY selected item already has the flag, otherwise "set" (normalises a
    /// mixed selection). Reuses <see cref="ApplyFlagToggle"/> so alias and variant share one code path.
    /// </summary>
    private async Task ToggleMappingFlagAsync(ChainViewModel? chainVm, string flagName, string flagLabel, Func<ItemViewModel, bool> hasFlag)
    {
        if (chainVm == null) return;

        var checkedItems = chainVm.Items.Where(i => i.IsChecked).ToList();
        if (checkedItems.Count == 0) return;

        string chainName = chainVm.Source.DisplayName;
        bool remove = checkedItems.All(hasFlag);
        string action = remove ? $"Remove {flagLabel} flag" : $"Set as {flagLabel}";

        // Fetch the live module first so the preview shows the EXACT before/after per item.
        string lua;
        try
        {
            lua = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Items/Mapping");
            if (string.IsNullOrEmpty(lua)) throw new Exception("Could not fetch Items/Mapping module.");
        }
        catch (Exception ex)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Error",
                Content = $"Failed to fetch mapping: {ex.Message}",
                CloseButtonText = "OK"
            }.ShowDialogAsync();
            return;
        }

        // Compute the resulting Lua up-front (also reused on publish). Each pass reuses the updated
        // text so a freshly-inserted entry is matched (not duplicated) by a later iteration.
        var newLua = lua;
        foreach (var vm in checkedItems)
            newLua = ApplyFlagToggle(newLua, vm.Source.ItemType, flagName, remove);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = checkedItems.Count == 1
                ? $"{action} for \"{checkedItems[0].Source.Name}\" ({checkedItems[0].Source.ItemType})"
                : $"{action} for {checkedItems.Count} items in \"{chainName}\"",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Module:Datatable/Items/Mapping",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        });

        // One real before→after diff pair per item, read straight from the fetched/computed Lua.
        var diffPanel = new StackPanel();
        foreach (var vm in checkedItems)
        {
            string before = ExtractEntryLine(lua, vm.Source.ItemType) ?? "(not in mapping table)";
            string after = ExtractEntryLine(newLua, vm.Source.ItemType) ?? "(no entry — nothing written)";

            diffPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x25, 0xD0, 0x40, 0x40)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                Child = new TextBlock
                {
                    Text = "- " + before,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
                    TextWrapping = TextWrapping.Wrap
                }
            });
            diffPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x25, 0x30, 0xC0, 0x30)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, checkedItems.Count > 1 ? 8 : 0),
                Child = new TextBlock
                {
                    Text = "+ " + after,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0x40)),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        if (checkedItems.Count > 4)
            panel.Children.Add(new ScrollViewer
            {
                Content = diffPanel,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
        else
            panel.Children.Add(diffPanel);

        var confirmBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = action,
            Content = panel,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel"
        };
        if (await confirmBox.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        try
        {
            string summary = checkedItems.Count == 1
                ? $"{action} for {checkedItems[0].Source.ItemType} (via MergeMansionWikiTools)"
                : $"{action} for {checkedItems.Count} items in {chainName} (via MergeMansionWikiTools)";

            await MysteryWikiService.PublishPageAsync(
                _main.Settings.WikiUsername, _main.Settings.WikiPassword,
                "Module:Datatable/Items/Mapping", newLua, summary);

            _ = RefreshAfterWikiChange();
        }
        catch (Exception ex)
        {
            var errBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Error",
                Content = $"Failed: {ex.Message}",
                CloseButtonText = "OK"
            };
            await errBox.ShowDialogAsync();
        }
    }

    /// <summary>Returns the raw <c>["itemType"] = {...}</c> entry from the mapping Lua, or null if absent.</summary>
    private static string? ExtractEntryLine(string lua, string itemType)
    {
        var escapedType = System.Text.RegularExpressions.Regex.Escape(itemType);
        var m = System.Text.RegularExpressions.Regex.Match(
            lua, @"\[""" + escapedType + @"""\]\s*=\s*\{[^}]*\}");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Adds or removes a boolean flag (<c>isAlias</c> / <c>isVariant</c>) for one item in the
    /// Items/Mapping Lua. Deliberately NEVER writes <c>chainName</c>: these flags just classify the
    /// item, its chain comes from the game data (mapping chainName is only an override, set via Move/Rename).
    /// • set + entry missing  → create <c>{flag = true}</c> (no chainName)
    /// • set + entry exists    → add the flag, leave other fields untouched
    /// • remove + entry exists → strip the flag; if that empties the body, drop the whole line
    /// • remove + entry missing→ no-op
    /// Shared by the alias and variant toggles.
    /// </summary>
    internal static string ApplyFlagToggle(string lua, string itemType, string flagName, bool remove)
    {
        var escapedType = System.Text.RegularExpressions.Regex.Escape(itemType);
        var entryRegex = new System.Text.RegularExpressions.Regex(
            @"(\[""" + escapedType + @"""\]\s*=\s*\{)([^}]*)(})");

        if (!entryRegex.IsMatch(lua))
        {
            if (remove) return lua; // nothing to remove from a non-existent entry
            int insertPos = lua.LastIndexOf("\n}", StringComparison.Ordinal);
            if (insertPos < 0) throw new Exception("Could not find insertion point.");
            string luaEntry = $"\t[\"{itemType}\"] = {{{flagName} = true}},\n";
            return lua[..(insertPos + 1)] + luaEntry + lua[(insertPos + 1)..];
        }

        // Match the flag with a bool OR string value — isVariant can be a named label (isVariant = "Autumn").
        // Without the string alternative a labelled variant looked "unset", so a re-toggle fell through and
        // APPENDED a second `isVariant = true` → duplicate key → Lua keeps the last one → the label was
        // silently wiped. Matching the string also lets `remove` strip a labelled variant.
        var flagRegex = new System.Text.RegularExpressions.Regex(
            @",?\s*" + System.Text.RegularExpressions.Regex.Escape(flagName) + @"\s*=\s*(""(?:[^""\\]|\\.)*""|true|false)");
        lua = entryRegex.Replace(lua, m =>
        {
            var prefix = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            var suffix = m.Groups[3].Value;

            if (remove)
            {
                body = flagRegex.Replace(body, "");
            }
            else
            {
                var existing = flagRegex.Match(body);
                if (existing.Success)
                {
                    // Already set — preserve a string label ("Autumn") and an idempotent true; only a
                    // literal false is flipped to true. NEVER append a duplicate flag.
                    if (existing.Groups[1].Value == "false")
                        body = flagRegex.Replace(body, $", {flagName} = true");
                }
                else if (body.Trim().Length == 0)
                    body = $"{flagName} = true";       // empty entry → bare flag, no leading comma
                else
                    body += $", {flagName} = true";
            }
            // The flag regex only consumes a PRECEDING comma. When the flag was the entry's
            // FIRST field ({isAlias = true, chainName = ...}), removal/replacement leaves the
            // body starting with the next field's comma → invalid Lua ("{, chainName = ...").
            body = System.Text.RegularExpressions.Regex.Replace(body, @"^\s*,\s*", "");
            return prefix + body + suffix;
        });

        // Removing the flag may leave an empty {} — drop the whole line so the table stays clean.
        if (remove)
        {
            var emptyEntryRegex = new System.Text.RegularExpressions.Regex(
                @"[ \t]*\[""" + escapedType + @"""\]\s*=\s*\{\s*\},?\r?\n");
            lua = emptyEntryRegex.Replace(lua, "");
        }
        return lua;
    }

    /// <summary>
    /// Sets the <c>isVariant</c> value for one item to a NAMED label (<c>isVariant = "Spring"</c>) or,
    /// when <paramref name="label"/> is empty, back to the plain <c>isVariant = true</c>. Matches an
    /// existing bool OR string value; creates the entry if missing. Shares the leading-comma fix and
    /// entry-insert logic with <see cref="ApplyFlagToggle"/>. Never touches other fields.
    /// </summary>
    internal static string SetVariantLabel(string lua, string itemType, string label)
    {
        // Lua string: escape backslash and double-quote so labels stay valid.
        string value = string.IsNullOrEmpty(label)
            ? "true"
            : "\"" + label.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        var escapedType = System.Text.RegularExpressions.Regex.Escape(itemType);
        var entryRegex = new System.Text.RegularExpressions.Regex(
            @"(\[""" + escapedType + @"""\]\s*=\s*\{)([^}]*)(})");

        if (!entryRegex.IsMatch(lua))
        {
            int insertPos = lua.LastIndexOf("\n}", StringComparison.Ordinal);
            if (insertPos < 0) throw new Exception("Could not find insertion point.");
            string luaEntry = $"\t[\"{itemType}\"] = {{isVariant = {value}}},\n";
            return lua[..(insertPos + 1)] + luaEntry + lua[(insertPos + 1)..];
        }

        // Existing isVariant value is bool OR quoted string.
        var flagRegex = new System.Text.RegularExpressions.Regex(
            @",?\s*isVariant\s*=\s*(?:true|false|""(?:[^""\\]|\\.)*"")");
        return entryRegex.Replace(lua, m =>
        {
            var prefix = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            var suffix = m.Groups[3].Value;

            if (flagRegex.IsMatch(body))
                body = flagRegex.Replace(body, $", isVariant = {value}");
            else if (body.Trim().Length == 0)
                body = $"isVariant = {value}";
            else
                body += $", isVariant = {value}";
            // Same first-field guard as ApplyFlagToggle: the flag regex consumes a PRECEDING comma,
            // so replacing a leading isVariant leaves the body starting with the next field's comma.
            body = System.Text.RegularExpressions.Regex.Replace(body, @"^\s*,\s*", "");
            return prefix + body + suffix;
        });
    }

    /// <summary>
    /// Sets an arbitrary variant mapping field (variantOrder, groupOdds) on one item to a RAW Lua
    /// value ("3", "true", "\"X\""). Empty <paramref name="luaValue"/> removes the field. Creates the
    /// entry if missing. Shares the leading-comma fix and entry-insert logic with <see cref="SetVariantLabel"/>.
    /// </summary>
    internal static string SetVariantField(string lua, string itemType, string field, string luaValue)
    {
        var escapedType = System.Text.RegularExpressions.Regex.Escape(itemType);
        var escapedField = System.Text.RegularExpressions.Regex.Escape(field);
        var entryRegex = new System.Text.RegularExpressions.Regex(
            @"(\[""" + escapedType + @"""\]\s*=\s*\{)([^}]*)(})");

        bool remove = string.IsNullOrEmpty(luaValue);

        if (!entryRegex.IsMatch(lua))
        {
            if (remove) return lua; // nothing to remove, no entry
            int insertPos = lua.LastIndexOf("\n}", StringComparison.Ordinal);
            if (insertPos < 0) throw new Exception("Could not find insertion point.");
            string luaEntry = $"\t[\"{itemType}\"] = {{{field} = {luaValue}}},\n";
            return lua[..(insertPos + 1)] + luaEntry + lua[(insertPos + 1)..];
        }

        // Match an existing `field = <value>` (number, bool, or quoted string), incl. preceding comma.
        var fieldRegex = new System.Text.RegularExpressions.Regex(
            @",?\s*" + escapedField + @"\s*=\s*(?:true|false|-?\d+(?:\.\d+)?|""(?:[^""\\]|\\.)*"")");

        return entryRegex.Replace(lua, m =>
        {
            var prefix = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            var suffix = m.Groups[3].Value;

            if (remove)
                body = fieldRegex.Replace(body, "");
            else if (fieldRegex.IsMatch(body))
                body = fieldRegex.Replace(body, $", {field} = {luaValue}");
            else if (body.Trim().Length == 0)
                body = $"{field} = {luaValue}";
            else
                body += $", {field} = {luaValue}";

            // First-field guard: fieldRegex consumes a PRECEDING comma, so removing/replacing a
            // leading field leaves the body starting with the next field's comma.
            body = System.Text.RegularExpressions.Regex.Replace(body, @"^\s*,\s*", "");
            return prefix + body + suffix;
        });
    }

    /// <summary>
    /// Commits pending variant-label edits for the chain: gathers each variant whose inline label
    /// differs from the live mapping, fetches the module once, applies all in one pass, and publishes
    /// a single edit. No-op when nothing changed.
    /// </summary>
    /// <summary>Enter in a variant-label box commits the binding (LostFocus trigger) without needing
    /// the user to click away first — so "Save Labels" sees the typed value.</summary>
    private void VariantLabel_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;

        if (e.Key == Key.Enter)
        {
            tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Tab) return;

        // Column-wise Tab flow so a whole column fills top→bottom before moving on:
        //   order → next order  ·  LAST order → FIRST label  ·  label → next label.
        // Shift+Tab reverses (first label → last order; first order → default focus out).
        bool isOrder = IsVariantBox(tb, "VariantOrderEdit");
        bool isLabel = IsVariantBox(tb, "VariantLabelEdit");
        if (!isOrder && !isLabel) return;

        tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        var list = FindVisualParent<System.Windows.Controls.ItemsControl>(tb);
        if (list == null) return;
        var boxes = FindVisualChildren<System.Windows.Controls.TextBox>(list).ToList();
        var orders = boxes.Where(b => IsVariantBox(b, "VariantOrderEdit")).ToList();
        var labels = boxes.Where(b => IsVariantBox(b, "VariantLabelEdit")).ToList();
        bool back = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        System.Windows.Controls.TextBox? target;
        int idx;
        if (isOrder)
        {
            idx = orders.IndexOf(tb);
            target = back ? (idx > 0 ? orders[idx - 1] : null)
                          : (idx < orders.Count - 1 ? orders[idx + 1] : labels.FirstOrDefault());
        }
        else
        {
            idx = labels.IndexOf(tb);
            target = back ? (idx > 0 ? labels[idx - 1] : orders.LastOrDefault())
                          : (idx < labels.Count - 1 ? labels[idx + 1] : null);
        }

        AppLogger.Debug($"[VariantTab] {(isOrder ? "order" : "label")} idx={idx} orders={orders.Count} labels={labels.Count} back={back} → target={(target == null ? "null" : IsVariantBox(target, "VariantOrderEdit") ? "order" : "label")}");

        if (target != null)
        {
            bool ok = target.Focus();
            target.SelectAll();
            e.Handled = true;
            AppLogger.Debug($"[VariantTab] Focus() returned {ok}");
        }
    }

    private static bool IsVariantBox(System.Windows.Controls.TextBox tb, string bindingPath)
        => tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.ParentBinding?.Path?.Path == bindingPath;

    /// <summary>A committed label edit may have made the chain dirty/clean — refresh the
    /// action bar so Save Labels appears (or its enabled state updates) without a checkbox.</summary>
    private void VariantLabel_LostFocus(object sender, RoutedEventArgs e)
    {
        var chainVm = GetChainVmFromButton(sender);
        if (chainVm == null || sender is not FrameworkElement fe) return;
        var expander = FindVisualParent<Expander>(fe);
        var actionBar = expander != null ? FindActionBar(expander) : null;
        if (actionBar != null) UpdateActionBar(chainVm, actionBar);
    }

    private async void BtnSaveVariantLabels_Click(object sender, RoutedEventArgs e)
    {
        var chainVm = GetChainVmFromButton(sender);
        if (chainVm == null) return;

        int pendingCount = chainVm.Items.Count(i => i.VariantMappingDirty);
        if (pendingCount == 0)
        {
            _main.ShowStatus("No mapping changes to save.", InfoBarSeverity.Informational);
            return;
        }

        // Working state: fetch + publish are network round-trips (~1-2 s) — disable the
        // button and show a persistent "Saving…" status so the click has visible feedback.
        var btn = sender as Wpf.Ui.Controls.Button;
        var origContent = btn?.Content;
        if (btn != null) { btn.IsEnabled = false; btn.Content = "Saving…"; }
        _main.ShowStatus($"Saving {pendingCount} mapping change(s)…", InfoBarSeverity.Informational);

        try
        {
            string lua;
            try
            {
                lua = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Items/Mapping");
                if (string.IsNullOrEmpty(lua)) throw new Exception("Could not fetch Items/Mapping module.");
            }
            catch (Exception ex)
            {
                _main.ShowStatus($"Failed to fetch mapping: {ex.Message}", InfoBarSeverity.Error);
                return;
            }

            var newLua = lua;
            int changeCount = ApplyPendingVariantLabelOrder(chainVm, ref newLua);

            var summary = $"Update variant mapping ({changeCount} change(s)) in {chainVm.Source.DisplayName} (via MergeMansionWikiTools)";

            try
            {
                await MysteryWikiService.PublishPageAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword,
                    "Module:Datatable/Items/Mapping", newLua, summary);
                _main.ShowStatus($"Saved {changeCount} mapping change(s).", InfoBarSeverity.Success);
                _ = RefreshAfterWikiChange();
            }
            catch (Exception ex)
            {
                _main.ShowStatus($"Save failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (btn != null) { btn.Content = origContent; btn.IsEnabled = true; }
        }
    }

    /// <summary>Applies each variant's pending inline label + order edits onto the mapping Lua and syncs
    /// the Source model. Shared by Save Mapping and the Group-Drop-Odds toggle so clicking the checkbox
    /// never discards labels/orders the user typed but hadn't saved (the toggle publishes + refreshes,
    /// which would otherwise rebuild the VMs from the mapping that lacks those edits). Returns # fields changed.</summary>
    private static int ApplyPendingVariantLabelOrder(ChainViewModel chainVm, ref string lua)
    {
        int changeCount = 0;
        foreach (var i in chainVm.Items.Where(x => x.IsVariant))
        {
            var label = (i.VariantLabelEdit ?? "").Trim();
            if (label != (i.Source.MappingVariantLabel ?? ""))
            { lua = SetVariantLabel(lua, i.Source.ItemType, label); i.Source.MappingVariantLabel = string.IsNullOrEmpty(label) ? null : label; changeCount++; }

            var order = (i.VariantOrderEdit ?? "").Trim();
            if (order != (i.Source.MappingVariantOrder?.ToString() ?? ""))
            {
                // numeric-only, positive; empty (or invalid) removes the field
                var n = (int.TryParse(order, out var parsed) && parsed > 0) ? (int?)parsed : null;
                lua = SetVariantField(lua, i.Source.ItemType, "variantOrder", n?.ToString() ?? "");
                i.Source.MappingVariantOrder = n;
                changeCount++;
            }
        }
        return changeCount;
    }

    /// <summary>Toggles groupOdds on all variant items of the chain (one wiki edit).</summary>
    private async void ChkGroupOdds_Click(object sender, RoutedEventArgs e)
    {
        var chainVm = GetChainVmFromButton(sender);
        if (chainVm == null) return;
        bool on = (sender as System.Windows.Controls.CheckBox)?.IsChecked == true;

        var variants = chainVm.Items.Where(i => i.IsVariant).ToList();
        if (variants.Count == 0) return;

        var cb = sender as System.Windows.Controls.CheckBox;
        if (cb != null) cb.IsEnabled = false;
        _main.ShowStatus(on ? "Enabling grouped Drop Odds…" : "Disabling grouped Drop Odds…", InfoBarSeverity.Informational);
        try
        {
            var lua = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Items/Mapping");
            if (string.IsNullOrEmpty(lua)) throw new Exception("Could not fetch Items/Mapping module.");
            var newLua = lua;
            foreach (var i in variants)
                newLua = SetVariantField(newLua, i.Source.ItemType, "groupOdds", on ? "true" : "");
            // Flush pending inline label/order edits in the SAME publish — otherwise the refresh below
            // rebuilds the VMs from a mapping that lacks them and the user's typed labels/orders are lost.
            int flushed = ApplyPendingVariantLabelOrder(chainVm, ref newLua);
            var extra = flushed > 0 ? $" + {flushed} label/order change(s)" : "";
            await MysteryWikiService.PublishPageAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword,
                "Module:Datatable/Items/Mapping", newLua,
                $"{(on ? "Enable" : "Disable")} grouped Drop Odds{extra} for {chainVm.Source.DisplayName} (via MergeMansionWikiTools)");
            foreach (var i in variants) i.Source.MappingGroupOdds = on;
            _main.ShowStatus(on ? "Grouped Drop Odds enabled." : "Grouped Drop Odds disabled.", InfoBarSeverity.Success);
            _ = RefreshAfterWikiChange();
        }
        catch (Exception ex) { _main.ShowStatus($"Save failed: {ex.Message}", InfoBarSeverity.Error); }
        finally { if (cb != null) cb.IsEnabled = true; }
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

        // Find primary item name per level (first non-alias name at that level). Group-by, not
        // ToDictionary(level): a level can hold several non-alias items (variants → all non-alias),
        // which would throw "same key" (crash report #8: duplicate level key in batch rename).
        var primaryNameByLevel = chain.Items
            .Where(i => !i.IsAlias && !string.IsNullOrEmpty(i.Name) && !i.Name.StartsWith("Item_"))
            .GroupBy(i => i.Level)
            .ToDictionary(g => g.Key, g => g.First().Name);

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
