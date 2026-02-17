using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    public string DisplayName => Source.DisplayName;
    public string ConfigKey => Source.ConfigKey;
    public string Summary => Source.Summary;
    public bool ShowConfigKey => Source.DisplayName != Source.ConfigKey;

    public List<ItemViewModel> Items { get; }

    public ChainViewModel(ParsedChain source)
    {
        Source = source;
        Items = source.Items.Select(i => new ItemViewModel(i)).ToList();
    }
}

public class ItemViewModel
{
    public ParsedItem Source { get; }

    public int Level => Source.Level;
    public string Name => Source.Name;

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

    public ItemViewModel(ParsedItem source) => Source = source;
}

// ── Page ─────────────────────────────────────────────────────────

public partial class ChainBrowserPage : UserControl
{
    private readonly MainWindow _main;
    private List<ChainViewModel> _allChains = new();
    private string _currentSearch = "";

    private static readonly SolidColorBrush HighlightBrush =
        new(Color.FromRgb(0xCB, 0x9C, 0xFD));

    public ChainBrowserPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        if (_main.DataService != null)
            OnDataLoaded();
    }

    public void OnDataLoaded()
    {
        if (_main.DataService == null) return;

        _allChains = _main.DataService.Chains
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ChainViewModel(c))
            .ToList();

        ApplyFilter();

        emptyState.Visibility = Visibility.Collapsed;
        lvChains.Visibility = Visibility.Visible;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (lvChains == null) return;

        var search = txtSearch?.Text?.Trim() ?? "";
        _currentSearch = search;
        var filterIndex = cmbFilter?.SelectedIndex ?? 0;

        IEnumerable<ChainViewModel> filtered = _allChains;

        // Text search
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(c =>
                c.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.ConfigKey.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Items.Any(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        // Category filter
        filtered = filterIndex switch
        {
            1 => filtered.Where(c => c.Source.HasGenerators),
            2 => filtered.Where(c => c.Source.HasSpawners && !c.Source.HasGenerators),
            3 => filtered.Where(c => !c.Source.HasGenerators && !c.Source.HasSpawners),
            4 => filtered.Where(c => c.Source.IsEventChain),
            5 => filtered.Where(c => c.Source.HasHumanReadableName),
            _ => filtered
        };

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

        // No active search — just show plain text
        if (string.IsNullOrEmpty(search))
        {
            tb.Inlines.Clear();
            tb.Text = vm.DisplayName;
            return;
        }

        var displayName = vm.DisplayName;
        tb.Text = null; // Clear Text so Inlines take over
        tb.Inlines.Clear();

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
}
