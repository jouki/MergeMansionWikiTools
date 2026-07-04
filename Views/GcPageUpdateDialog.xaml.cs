using System.Windows;
using System.Windows.Controls;
using MergeMansionWikiTools.Models;
using Wpf.Ui.Appearance;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Confirmation dialog for the automatic Garage Cleanup section refresh on an event page
/// (shown per affected page after the Events/Various push, drift-dialog style): side-by-side
/// red/green diff of the current wiki section vs the newly generated one, with
/// "Update page" / "Skip this page" as the decision. Closing the window = skip.
/// </summary>
public partial class GcPageUpdateDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>True when the user confirmed the page update ("Update page").</summary>
    public bool Confirmed { get; private set; }

    private bool _suppressScrollSync;

    public GcPageUpdateDialog(string pageTitle, string action, List<DiffLine> diffs, int index, int total)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        txtPage.Text = pageTitle;
        int added = diffs.Count(d => d.Type == DiffLineType.Added);
        int removed = diffs.Count(d => d.Type == DiffLineType.Removed);
        int modified = diffs.Count(d => d.Type == DiffLineType.Modified);
        txtSubtitle.Text = $"Garage Cleanup grid keys changed — {action} · "
            + $"{added} added · {removed} removed · {modified} modified"
            + (total > 1 ? $" · page {index}/{total}" : "");

        DiffViewHelper.BuildDiffView(pnlLeft, pnlRight, diffs);
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e) => Close();

    // ── Synchronized scrolling (mirror of MysteryDiffDialog) ──────────────

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
}
