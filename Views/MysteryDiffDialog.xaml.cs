using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;

using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public partial class MysteryDiffDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly MysteryEvent _mystery;
    private readonly MysteryDiffScope _scope;
    private readonly MainWindow _main;
    private readonly MysteryItemMapping? _mapping;
    private readonly DialogueService? _dialogueService;
    private bool _suppressScrollSync;

    public MysteryDiffDialog(
        MainWindow main, MysteryEvent mystery, MysteryDiffScope scope,
        MysteryItemMapping? mapping, DialogueService? dialogueService)
    {
        _main = main;
        _mystery = mystery;
        _scope = scope;
        _mapping = mapping;
        _dialogueService = dialogueService;
        AppLogger.Info($"MysteryDiffDialog: dialogueService={(dialogueService != null ? "loaded" : "NULL")}, " +
            $"hasDialogues={dialogueService?.HasDialogues(mystery.ProgressionEventId)}");

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        var scopeName = scope switch
        {
            MysteryDiffScope.Rewards => "Rewards",
            MysteryDiffScope.EventPage => "Event Page",
            MysteryDiffScope.EventItemPage => "Event Item Page",
            _ => "Diff"
        };

        txtTitle.Text = $"Diff — {mystery.Name} — {scopeName}";
        txtMysteryName.Text = mystery.Name;
        var dateStr = mystery.StartDate?.ToString("MMM d, yyyy") ?? "Unknown";
        var typeStr = mystery.MysteryType == MysteryType.Pet ? "Pet" : "Standard";
        txtMysteryDetail.Text = $"{typeStr} · {dateStr} · {scopeName}";

        _ = LoadDiffAsync();
    }

    private async Task LoadDiffAsync()
    {
        try
        {
            var (wikiContent, generated, diffs, _) = await MysteryWikiService.ComputeDiffAsync(
                _mystery, _scope, _main.DataService, _main.WikiMapping, _mapping, _dialogueService);

            pnlLoading.Visibility = Visibility.Collapsed;

            if (wikiContent == null)
            {
                // Page doesn't exist
                pnlNotFound.Visibility = Visibility.Visible;
                txtNotFound.Text = _scope switch
                {
                    MysteryDiffScope.Rewards => "Reward template not found on wiki.",
                    MysteryDiffScope.EventPage => $"\"{_mystery.WikiStatus.SuggestedPageTitle ?? _mystery.Name}\" not found.",
                    MysteryDiffScope.EventItemPage => $"\"{_mystery.EventItemName ?? "?"}\" not found.",
                    _ => "Page not found."
                };
                return;
            }

            // Check if all lines match
            bool allMatch = diffs.All(d => d.Type == DiffLineType.Match);
            if (allMatch)
            {
                pnlNoDiffs.Visibility = Visibility.Visible;
                return;
            }

            BuildDiffView(diffs);
            pnlDiff.Visibility = Visibility.Visible;

            // Stats
            int added = diffs.Count(d => d.Type == DiffLineType.Added);
            int removed = diffs.Count(d => d.Type == DiffLineType.Removed);
            int modified = diffs.Count(d => d.Type == DiffLineType.Modified);
            int matched = diffs.Count(d => d.Type == DiffLineType.Match);
            txtStats.Text = $"{added} added · {removed} removed · {modified} modified · {matched} unchanged";
        }
        catch (Exception ex)
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            pnlError.Visibility = Visibility.Visible;
            txtError.Text = $"Error: {ex.Message}";
        }
    }

    private void BuildDiffView(List<DiffLine> diffs) =>
        DiffViewHelper.BuildDiffView(pnlLeft, pnlRight, diffs);

    // ── Synchronized scrolling ──────────────────────────────────

    private void ScrollLeft_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollSync) return;
        _suppressScrollSync = true;
        scrollRight.ScrollToVerticalOffset(scrollLeft.VerticalOffset);
        scrollRight.ScrollToHorizontalOffset(scrollLeft.HorizontalOffset);
        _suppressScrollSync = false;
    }

    private void ScrollRight_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollSync) return;
        _suppressScrollSync = true;
        scrollLeft.ScrollToVerticalOffset(scrollRight.VerticalOffset);
        scrollLeft.ScrollToHorizontalOffset(scrollRight.HorizontalOffset);
        _suppressScrollSync = false;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
