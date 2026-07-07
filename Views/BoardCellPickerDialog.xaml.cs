using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Two-step board-tile picker: choose a chain (Name + icon, non-event only), then a level (icon per
/// row). Returns the chosen (ChainKey, Level), a request to clear the tile, or null on cancel.
/// </summary>
public partial class BoardCellPickerDialog : FluentWindow
{
    private readonly ChainDisplayResolver _resolver;
    private string? _pickedChainKey;

    /// <summary>Result: (ChainKey, Level) when an item was picked.</summary>
    public (string ChainKey, int Level)? PickedItem { get; private set; }
    /// <summary>True when the user asked to clear the tile.</summary>
    public bool ClearRequested { get; private set; }

    public BoardCellPickerDialog(ChainDisplayResolver resolver, bool allowClear,
        IReadOnlyList<string>? recentChainKeys = null, string? currentChainKey = null)
    {
        _resolver = resolver;
        InitializeComponent();
        lstChains.ItemsSource = resolver.ChainRows;
        btnClear.Visibility = allowClear ? Visibility.Visible : Visibility.Collapsed;

        // Recent-chains column: one PickRow per recent key (icon + name), newest first.
        if (recentChainKeys is { Count: > 0 })
        {
            var rows = recentChainKeys
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => new PickRow(resolver, k, null, resolver.GetName(k), ""))
                .ToList();
            if (rows.Count > 0)
            {
                lstRecent.ItemsSource = rows;
                recentPanel.Visibility = Visibility.Visible;
                EnsureRowIcons(rows);
            }
        }

        // Clicking an occupied tile jumps straight to that chain's levels (just change the level).
        if (!string.IsNullOrEmpty(currentChainKey))
            Loaded += (_, _) => ShowLevels(currentChainKey!);
        else
            Loaded += (_, _) => txtSearch.Focus();
    }

    private void Recent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not PickRow row) return;
        lstRecent.SelectedIndex = -1;
        ShowLevels(row.ChainKey);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var rows = _resolver.Search(txtSearch.Text ?? "");
        lstChains.ItemsSource = rows;
        EnsureRowIcons(rows);
    }

    /// <summary>Flood-fill-crops the per-level icons for the chains in the current search results
    /// (item rows show a specific level), then refreshes the list once ready.</summary>
    private async void EnsureRowIcons(System.Collections.Generic.List<PickRow> rows)
    {
        var chains = rows.Select(r => r.ChainKey).Distinct(System.StringComparer.OrdinalIgnoreCase)
            .Take(40).ToList();
        if (chains.Count == 0) return;
        await System.Threading.Tasks.Task.Run(() =>
        {
            if (!_resolver.EnsureLevelImages(chains)) return;
            Dispatcher.Invoke(() =>
            {
                if (chainStep.Visibility == Visibility.Visible)
                    lstChains.ItemsSource = _resolver.Search(txtSearch.Text ?? "");
            });
        });
    }

    // Single click selects. A chain row → level step; an item row (specific level) adds directly.
    // Clear the selection after handling so the SAME row can be re-picked after Back.
    private void Chains_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not PickRow row) return;
        lstChains.SelectedIndex = -1;
        if (row.IsItem)
        {
            PickedItem = (row.ChainKey, row.Level!.Value);
            DialogResult = true;
        }
        else
        {
            ShowLevels(row.ChainKey);
        }
    }

    private void ShowLevels(string chainKey)
    {
        _pickedChainKey = chainKey;
        txtLevelHeader.Text = _resolver.GetName(chainKey);
        lstLevels.ItemsSource = _resolver.LevelOptions(chainKey);
        chainStep.Visibility = Visibility.Collapsed;
        levelStep.Visibility = Visibility.Visible;
        EnsureChainIcons(chainKey);
    }

    /// <summary>Flood-fill-crops this chain's per-level icons in the background, then refreshes the
    /// level list once they're ready (no-op if already extracted).</summary>
    private async void EnsureChainIcons(string chainKey)
    {
        await System.Threading.Tasks.Task.Run(() =>
        {
            if (!_resolver.EnsureLevelImages(new[] { chainKey })) return;
            Dispatcher.Invoke(() =>
            {
                if (_pickedChainKey == chainKey && levelStep.Visibility == Visibility.Visible)
                    lstLevels.ItemsSource = _resolver.LevelOptions(chainKey);
            });
        });
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        levelStep.Visibility = Visibility.Collapsed;
        chainStep.Visibility = Visibility.Visible;
        txtSearch.Focus();
    }

    private void Levels_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not LevelOption lvl) return;
        if (_pickedChainKey != null)
        {
            PickedItem = (_pickedChainKey, lvl.Level);
            DialogResult = true;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearRequested = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Escape: on the level step → back to chain search; on the chain step → first clear a
    /// non-empty search, otherwise close the dialog.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (levelStep.Visibility == Visibility.Visible)
                Back_Click(this, new RoutedEventArgs());
            else if (!string.IsNullOrEmpty(txtSearch.Text))
                txtSearch.Text = "";
            else
                DialogResult = false;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
