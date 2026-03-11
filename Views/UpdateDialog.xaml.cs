using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class UpdateDialog : FluentWindow
{
    private readonly ReleaseInfo _release;
    private bool _updating;

    public UpdateDialog(ReleaseInfo release)
    {
        _release = release;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        txtCurrentVersion.Text = AppVersion.Version;
        txtNewVersion.Text = release.TagName;

        // Parse markdown changelog into FlowDocument
        var changelog = string.IsNullOrWhiteSpace(release.Body)
            ? "No changelog available."
            : release.Body.Trim();
        rtbChangelog.Document = ParseMarkdown(changelog);

        if (release.AssetSize > 0)
            txtAssetSize.Text = $"Download size: {UpdateService.FormatSize(release.AssetSize)}";

        if (string.IsNullOrEmpty(release.AssetUrl))
        {
            btnUpdate.Content = "Open in Browser";
            txtAssetSize.Text = "No downloadable asset found — manual update required.";
        }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_release.AssetUrl))
        {
            // No asset — open GitHub releases page
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_release.HtmlUrl) { UseShellExecute = true }); } catch { }
            Close();
            return;
        }

        if (_updating) return;
        _updating = true;

        btnUpdate.IsEnabled = false;
        btnLater.IsEnabled = false;
        progressPanel.Visibility = Visibility.Visible;

        var progress = new Progress<(double percent, string status)>(p =>
        {
            progressBar.Value = p.percent;
            txtProgress.Text = p.status;
        });

        try
        {
            var success = await UpdateService.DownloadAndApplyAsync(_release, progress);
            if (success)
            {
                txtProgress.Text = "Update applied. Restarting...";
                // The updater script will restart the app — we need to exit
                await System.Threading.Tasks.Task.Delay(500);
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            progressPanel.Visibility = Visibility.Collapsed;
            btnUpdate.IsEnabled = true;
            btnLater.IsEnabled = true;
            _updating = false;

            var mb = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Update Failed",
                Content = $"The update could not be applied:\n\n{ex.Message}\n\nYou can try again or download manually from GitHub.",
                PrimaryButtonText = "OK"
            };
            await mb.ShowDialogAsync();
        }
    }

    private void BtnLater_Click(object sender, RoutedEventArgs e)
    {
        if (!_updating)
            Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_updating)
            e.Cancel = true; // Prevent closing during update
        base.OnClosing(e);
    }

    /// <summary>
    /// Simple markdown → FlowDocument: ## headings, **bold**, - bullet lists.
    /// </summary>
    private FlowDocument ParseMarkdown(string md)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI") };
        var primaryBrush = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondaryBrush = (Brush)FindResource("TextFillColorSecondaryBrush");
        var accentBrush = (Brush)FindResource("AccentFillColorDefaultBrush");

        foreach (var rawLine in md.Split('\n'))
        {
            var line = rawLine.TrimEnd();

            // ## Heading 2
            if (line.StartsWith("## "))
            {
                var p = new Paragraph { Margin = new Thickness(0, 6, 0, 4), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = accentBrush };
                p.Inlines.Add(new Run(line[3..].Trim()));
                doc.Blocks.Add(p);
                continue;
            }

            // ### Heading 3
            if (line.StartsWith("### "))
            {
                var p = new Paragraph { Margin = new Thickness(0, 8, 0, 2), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = primaryBrush };
                p.Inlines.Add(new Run(line[4..].Trim()));
                doc.Blocks.Add(p);
                continue;
            }

            // - Bullet item
            if (line.StartsWith("- "))
            {
                var p = new Paragraph { Margin = new Thickness(8, 1, 0, 1), TextIndent = -10, FontSize = 12, Foreground = secondaryBrush };
                p.Inlines.Add(new Run("•  "));
                AddInlinesWithBold(p, line[2..].Trim(), primaryBrush);
                doc.Blocks.Add(p);
                continue;
            }

            // Empty line
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Regular text
            {
                var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1), FontSize = 12, Foreground = secondaryBrush };
                AddInlinesWithBold(p, line, primaryBrush);
                doc.Blocks.Add(p);
            }
        }

        return doc;
    }

    /// <summary>
    /// Parses **bold** segments within text and adds them as Runs.
    /// </summary>
    private static void AddInlinesWithBold(Paragraph p, string text, Brush boldBrush)
    {
        var parts = Regex.Split(text, @"(\*\*[^*]+?\*\*)");
        foreach (var part in parts)
        {
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
            {
                var boldText = part[2..^2];
                p.Inlines.Add(new Run(boldText) { FontWeight = FontWeights.SemiBold, Foreground = boldBrush });
            }
            else if (!string.IsNullOrEmpty(part))
            {
                p.Inlines.Add(new Run(part));
            }
        }
    }
}
