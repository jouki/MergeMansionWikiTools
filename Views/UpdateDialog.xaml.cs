using System.Windows;
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

        // Format changelog (markdown-lite: strip leading #, trim)
        var changelog = string.IsNullOrWhiteSpace(release.Body)
            ? "No changelog available."
            : release.Body.Trim();
        txtChangelog.Text = changelog;

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
}
