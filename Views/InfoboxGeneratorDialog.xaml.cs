using System.Windows;
using System.Windows.Controls;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public partial class InfoboxGeneratorDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly ParsedChain _chain;
    private readonly InfoboxGeneratorService _service = new();
    private bool _suppressRegenerate;

    public InfoboxGeneratorDialog(MainWindow main, ParsedChain chain)
    {
        _main = main;
        _chain = chain;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        txtChainInfo.Text = chain.DisplayName;
        txtChainDetail.Text = $"ConfigKey: {chain.ConfigKey} · {chain.Items.Count} levels · {chain.Summary}";

        // Pre-populate sources with auto-detected
        _suppressRegenerate = true;
        var ds = _main.DataService!;
        var autoSources = _service.BuildAutoSources(chain, ds.Chains, ds.ItemNames);
        txtSources.Text = string.Join("\n", autoSources);
        _suppressRegenerate = false;

        Loaded += (_, _) => RegeneratePreview();
    }

    private void ChkOption_Changed(object sender, RoutedEventArgs e)
        => RegeneratePreview();

    private void TxtSources_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRegenerate) return;
        RegeneratePreview();
    }

    private void RegeneratePreview()
    {
        if (_main.DataService == null) return;

        var ds = _main.DataService;

        var opts = new InfoboxGeneratorOptions
        {
            AddShop     = chkAddShop.IsChecked == true,
            IsStarter   = chkIsStarter.IsChecked == true,
            IsPoints    = chkIsPoints.IsChecked == true,
            IsDepleting = chkIsDepleting.IsChecked == true,
        };

        var manualSources = txtSources.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        try
        {
            var result = _service.Generate(_chain, ds.Chains, ds.ItemNames, opts, manualSources);
            txtOutput.Text = result;
            txtOutput.Visibility = Visibility.Visible;
            txtOutputPlaceholder.Visibility = Visibility.Collapsed;
            btnCopy.IsEnabled = true;
        }
        catch (Exception ex)
        {
            infoBar.Message = $"Generation error: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(txtOutput.Text)) return;

        bool success = false;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetDataObject(txtOutput.Text, false);
                success = true;
                Increment(s => s.InfoboxesGenerated++);
                break;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                await Task.Delay(100);
            }
        }

        btnCopy.Content = success ? "Copied!" : "Failed!";
        _ = ResetCopyButton();
    }

    private async Task ResetCopyButton()
    {
        await Task.Delay(2000);
        btnCopy.Content = "Copy to Clipboard";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
