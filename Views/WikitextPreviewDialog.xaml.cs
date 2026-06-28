using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Reusable read-only wikitext preview dialog (monospace + Copy to Clipboard + Close), styled like the
/// "Generate Page" dialog. Used by the Events tab's Generate Garage Cleanup action.
/// </summary>
public partial class WikitextPreviewDialog : FluentWindow
{
    private readonly Func<IProgress<string>, Task<string>>? _onPrimary;

    /// <param name="onPrimary">Optional async action (e.g. push to wiki). Receives an
    /// <see cref="IProgress{String}"/> it can call to surface step-by-step progress in the info bar
    /// (so the user sees the work isn't stuck), and returns a final status message. When set, a primary
    /// button labelled <paramref name="primaryLabel"/> appears.</param>
    public WikitextPreviewDialog(string title, string subtitle, string wikitext,
        Func<IProgress<string>, Task<string>>? onPrimary = null, string primaryLabel = "")
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        Title = title;
        titleBar.Title = title;
        txtTitle.Text = title;
        txtSubtitle.Text = subtitle;
        txtOutput.Text = wikitext;

        _onPrimary = onPrimary;
        if (onPrimary != null)
        {
            btnPrimary.Content = primaryLabel;
            btnPrimary.Visibility = Visibility.Visible;
        }
    }

    private async void BtnPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_onPrimary == null) return;
        try
        {
            btnPrimary.IsEnabled = false;
            infoBar.Message = "Working…";
            infoBar.Severity = InfoBarSeverity.Informational;
            infoBar.IsOpen = true;

            // Progress<T> captures this (UI) thread's SynchronizationContext, so step reports from the
            // background upload marshal back here and keep the info bar live (visible "not stuck" feedback).
            var progress = new Progress<string>(m =>
            {
                infoBar.Message = m;
                infoBar.Severity = InfoBarSeverity.Informational;
                infoBar.IsOpen = true;
            });

            var msg = await _onPrimary(progress);

            infoBar.Message = msg;
            infoBar.Severity = msg.Contains("skipped", StringComparison.OrdinalIgnoreCase)
                ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            infoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            infoBar.Message = $"Failed: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
        finally
        {
            btnPrimary.IsEnabled = true;
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(txtOutput.Text);
            infoBar.Message = "Copied to clipboard.";
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.IsOpen = true;
        }
        catch
        {
            infoBar.Message = "Clipboard is locked — try again.";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
