using System.Windows;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public partial class TableGeneratorDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly ParsedChain _chain;
    private string _tableName;
    private string? _wikiNameWarning;

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

        // Check for missing wiki name
        _wikiNameWarning = CheckWikiNameMissing(chain, main);

        Loaded += (_, _) => GenerateTable();
    }

    /// <summary>
    /// Returns a warning string if chain has no human-readable name and isn't mapped on the wiki.
    /// </summary>
    private static string? CheckWikiNameMissing(ParsedChain chain, MainWindow main)
    {
        if (chain.HasHumanReadableName) return null;

        var mapping = main.WikiMapping;
        if (mapping == null || mapping.Mappings.Count == 0) return null;

        bool hasWikiEntry = chain.Items.Any(i =>
            !string.IsNullOrEmpty(i.ItemType) && mapping.Mappings.ContainsKey(i.ItemType));

        if (hasWikiEntry) return null;

        return $"Chain \"{chain.DisplayName}\" has no human-readable name and is not mapped on the wiki. " +
               "The generated {{Item}} templates may reference a non-existent page. " +
               "Consider adding a name to Module:Datatable/Items/Mapping first.";
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
                showPrompt = true;
            }
            else
            {
                showPrompt = !_chain.HasNaturalName || string.IsNullOrEmpty(_chain.OriginalName);
            }
        }

        if (showPrompt)
        {
            bool canWiki = _main.Settings.WikiVerified;

            var nameDialog = new ChainNameDialog(
                _main.ChainNameService,
                _chain.ConfigKey,
                _chain.DisplayName,
                canWiki);
            nameDialog.Owner = this;

            if (nameDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(nameDialog.ChosenName))
            {
                _tableName = nameDialog.ChosenName;
                _main.ChainNameService.SetCustomName(_chain.ConfigKey, _tableName);

                if (nameDialog.SaveToWiki)
                    _ = PushNameToWikiAsync(_tableName);
            }
        }

        GenerateTable(showNotification: true);
    }

    private async Task PushNameToWikiAsync(string chainName)
    {
        try
        {
            warningBar.Message = "Saving to wiki...";
            warningBar.Severity = InfoBarSeverity.Informational;
            warningBar.IsOpen = true;

            await WikiMappingService.PushChainNameToWikiAsync(
                _main.Settings.WikiUsername,
                _main.Settings.WikiPassword,
                _chain.Items,
                chainName);

            warningBar.Message = "Chain name saved to wiki.";
            warningBar.Severity = InfoBarSeverity.Success;
            warningBar.IsOpen = true;

            // Refresh local cache
            _ = _main.RefreshWikiMappingAsync();
        }
        catch (Exception ex)
        {
            warningBar.Message = $"Wiki save failed: {ex.Message}";
            warningBar.Severity = InfoBarSeverity.Error;
            warningBar.IsOpen = true;
        }
    }

    private void GenerateTable(bool showNotification = false)
    {
        try
        {
            var generator = new WikiTableGenerator(_main.DataService!, _main.WikiMapping);

            string? existingTable = string.IsNullOrWhiteSpace(txtExistingTable.Text)
                ? null
                : txtExistingTable.Text;

            bool lowPrices = chkLowPrices.IsChecked == true;

            var result = generator.Generate(_chain, _tableName, lowPrices, existingTable);

            // Prepend wiki heading if checked
            if (chkIncludeHeading.IsChecked == true)
                result = "== Statistics ==\n=== Merge Stages ===\n" + result;

            txtOutput.Text = result;
            txtOutput.Visibility = Visibility.Visible;
            txtOutputPlaceholder.Visibility = Visibility.Collapsed;
            btnCopy.IsEnabled = true;

            // Collect all warnings (generator + wiki name)
            var allWarnings = new List<string>(generator.Warnings);
            if (_wikiNameWarning != null)
                allWarnings.Add(_wikiNameWarning);

            if (allWarnings.Count > 0)
            {
                warningBar.Message = string.Join("\n", allWarnings);
                warningBar.Severity = InfoBarSeverity.Warning;
                warningBar.IsOpen = true;
            }
            else if (showNotification)
            {
                _ = ShowRefreshNotification();
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

    private async Task ShowRefreshNotification()
    {
        warningBar.Message = "Table refreshed.";
        warningBar.Severity = InfoBarSeverity.Informational;
        warningBar.IsOpen = true;

        await Task.Delay(2000);
        if (warningBar.Message == "Table refreshed.")
            warningBar.IsOpen = false;
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(txtOutput.Text))
        {
            // SetDataObject with copy=false is fast (no OLE flush).
            // Data stays on clipboard while the app is running.
            bool success = false;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetDataObject(txtOutput.Text, false);
                    success = true;
                    Increment(s => s.TablesGenerated++);
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
    }

    private async Task ResetCopyButton()
    {
        await Task.Delay(2000);
        btnCopy.Content = "Copy to Clipboard";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
