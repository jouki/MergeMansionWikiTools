using System.Linq;
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
    private ParsedChain _effectiveChain;   // filtered chain for generation
    private string _tableName;
    private string? _wikiNameWarning;

    public TableGeneratorDialog(MainWindow main, ParsedChain chain)
    {
        _main = main;
        _chain = chain;
        _effectiveChain = chain;
        _tableName = chain.DisplayName;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        // Populate chain info
        txtChainInfo.Text = $"{chain.DisplayName}";
        txtChainDetail.Text = $"ConfigKey: {chain.ConfigKey} · {chain.Items.Count} levels · {chain.Summary}";

        // Auto-check hardcode name for event chains with parenthetical (e.g., "Puzzle Box (Event)")
        chkHardcodeName.IsChecked = chain.IsEventChain && chain.DisplayName.Contains('(');
        chkLowPrices.IsChecked = chain.IsEventChain ? true : main.Settings.LowPrices;
        chkIncludeHeading.IsChecked = main.Settings.TableGeneratorIncludeHeading;

        // Show "Include Tasks section" only when chain has at least one OrderFeatures item.
        bool hasOrderTasks = chain.Items.Any(i => i.OrderTasks is { Count: > 0 });
        chkIncludeTasks.Visibility = hasOrderTasks ? Visibility.Visible : Visibility.Collapsed;
        chkIncludeTasks.IsChecked = hasOrderTasks; // default ON when applicable

        // Source group selector for merged chains with level collisions
        if (chain.HasLevelCollisions && chain.MergedFromConfigKeys is { Count: > 1 })
        {
            sourceGroupPanel.Visibility = Visibility.Visible;
            cmbSourceGroup.Items.Add("All (merged)");
            foreach (var key in chain.MergedFromConfigKeys)
                cmbSourceGroup.Items.Add(key);
            cmbSourceGroup.SelectedIndex = 0;
        }

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

    private void ChkOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _main.Settings.LowPrices = chkLowPrices.IsChecked == true;
        _main.SaveSettings();

        GenerateTable();
    }

    private void ChkIncludeHeading_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _main.Settings.TableGeneratorIncludeHeading = chkIncludeHeading.IsChecked == true;
        _main.SaveSettings();

        GenerateTable();
    }

    private void CmbSourceGroup_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (cmbSourceGroup.SelectedIndex <= 0)
        {
            // "All (merged)"
            _effectiveChain = _chain;
        }
        else
        {
            var selectedKey = (string)cmbSourceGroup.SelectedItem;
            var filtered = new ParsedChain
            {
                ConfigKey = _chain.ConfigKey,
                DisplayName = _chain.DisplayName,
                OriginalName = _chain.OriginalName,
                HasNaturalName = _chain.HasNaturalName,
                CustomName = _chain.CustomName,
                IsNameFromWiki = _chain.IsNameFromWiki,
                Items = _chain.Items.Where(i => i.SourceChainKey == selectedKey).ToList()
            };
            _effectiveChain = filtered;
        }
        GenerateTable();
    }

    private void GenerateTable(bool showNotification = false)
    {
        try
        {
            var generator = new WikiTableGenerator(_main.DataService!, _main.WikiMapping);

            bool lowPrices = chkLowPrices.IsChecked == true;
            bool hardcodeName = chkHardcodeName.IsChecked == true;

            var result = generator.Generate(_effectiveChain, _tableName, lowPrices, hardcodeName);

            // Append Tasks section for items with OrderFeatures (e.g. Distillation Apparatus, Vending Machine).
            // Convention from wiki: one blank line between Merge Stages table and Tasks section.
            if (chkIncludeTasks.IsChecked == true)
            {
                var tasksBlocks = _effectiveChain.Items
                    .Where(i => i.IsOrder && i.OrderTasks is { Count: > 0 })
                    .Select(i => generator.GenerateOrderTasksTable(i))
                    .Where(b => !string.IsNullOrEmpty(b))
                    .ToList();
                if (tasksBlocks.Count > 0)
                    result += "\n" + string.Join("\n", tasksBlocks);
            }

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

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(txtOutput.Text))
        {
            App.NativeSetClipboardText(txtOutput.Text);
            Increment(s => s.TablesGenerated++);
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
