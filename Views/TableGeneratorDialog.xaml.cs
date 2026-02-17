using System.Windows;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class TableGeneratorDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly ParsedChain _chain;
    private string _tableName;

    public TableGeneratorDialog(MainWindow main, ParsedChain chain)
    {
        _main = main;
        _chain = chain;
        _tableName = chain.DisplayName;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        // Populate chain info
        txtChainInfo.Text = $"{chain.DisplayName}";
        txtChainDetail.Text = $"ConfigKey: {chain.ConfigKey} · {chain.Items.Count} levels · {chain.Summary}";

        // Restore checkbox states from settings
        chkShowNamePrompt.IsChecked = main.Settings.ShowCustomNamePrompt;
        chkForceNamePrompt.IsChecked = main.Settings.ForceCustomNamePrompt;
        chkLowPrices.IsChecked = chain.IsEventChain ? true : main.Settings.LowPrices;
    }

    private void ChkShowNamePrompt_Changed(object sender, RoutedEventArgs e)
    {
        // Save state immediately
        _main.Settings.ShowCustomNamePrompt = chkShowNamePrompt.IsChecked == true;
        _main.SaveSettings();
    }

    private void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        // Save checkbox states
        _main.Settings.ShowCustomNamePrompt = chkShowNamePrompt.IsChecked == true;
        _main.Settings.ForceCustomNamePrompt = chkForceNamePrompt.IsChecked == true;
        _main.Settings.LowPrices = chkLowPrices.IsChecked == true;
        _main.SaveSettings();

        // ── Custom name prompt logic ──
        bool showPrompt = false;

        if (chkShowNamePrompt.IsChecked == true)
        {
            if (chkForceNamePrompt.IsChecked == true)
            {
                // Force: always show
                showPrompt = true;
            }
            else
            {
                // Show only if name is non-natural or missing
                showPrompt = !_chain.HasNaturalName || string.IsNullOrEmpty(_chain.OriginalName);
            }
        }

        if (showPrompt)
        {
            var nameDialog = new ChainNameDialog(
                _main.ChainNameService,
                _chain.ConfigKey,
                _chain.DisplayName);
            nameDialog.Owner = this;

            if (nameDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(nameDialog.ChosenName))
            {
                _tableName = nameDialog.ChosenName;
                _main.ChainNameService.SetCustomName(_chain.ConfigKey, _tableName);
            }
            // If skipped, keep current display name
        }

        // ── Generate table ──
        try
        {
            var generator = new WikiTableGenerator(_main.DataService!);

            string? existingTable = string.IsNullOrWhiteSpace(txtExistingTable.Text)
                ? null
                : txtExistingTable.Text;

            bool lowPrices = chkLowPrices.IsChecked == true;

            var result = generator.Generate(_chain, _tableName, lowPrices, existingTable);

            txtOutput.Text = result;
            txtOutput.Visibility = Visibility.Visible;
            txtOutputPlaceholder.Visibility = Visibility.Collapsed;
            btnCopy.IsEnabled = true;

            // Show warnings if any
            if (generator.Warnings.Count > 0)
            {
                warningBar.Message = string.Join("\n", generator.Warnings);
                warningBar.Severity = InfoBarSeverity.Warning;
                warningBar.IsOpen = true;
            }
            else
            {
                warningBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            warningBar.Message = $"Generation error: {ex.Message}";
            warningBar.Severity = InfoBarSeverity.Error;
            warningBar.IsOpen = true;
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(txtOutput.Text))
        {
            // Retry clipboard access — sometimes locked by other apps
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    Clipboard.SetDataObject(txtOutput.Text, true);
                    break;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            btnCopy.Content = "Copied!";
            _ = ResetCopyButton();
        }
    }

    private async Task ResetCopyButton()
    {
        await Task.Delay(2000);
        btnCopy.Content = "Copy to Clipboard";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
