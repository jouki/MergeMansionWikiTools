using System.Windows;
using System.Windows.Controls;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public enum MysteryGeneratorMode
{
    Rewards,
    EventPage,
    EventItemPage
}

public partial class MysteryGeneratorDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly MysteryEvent _mystery;
    private readonly MysteryItemMapping? _mapping;
    private MysteryGeneratorMode _currentMode;

    /// <summary>Full generated text for the current mode (not truncated).</summary>
    private string _fullOutput = "";

    public MysteryGeneratorDialog(
        MainWindow main, MysteryEvent mystery,
        MysteryItemMapping? mapping, MysteryGeneratorMode initialMode)
    {
        _main = main;
        _mystery = mystery;
        _mapping = mapping;
        _currentMode = initialMode;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        // Info header
        txtMysteryInfo.Text = mystery.Name;
        var dateStr = mystery.StartDate?.ToString("MMM d, yyyy") ?? "Unknown";
        var typeStr = mystery.MysteryType == MysteryType.Pet ? "Pet" : "Standard";
        txtMysteryDetail.Text = $"{typeStr} · {dateStr} · {mystery.FreeTier.Count} levels · Event Item: {mystery.EventItemName ?? "Unknown"}";

        // Set initial tab
        tabMode.SelectedIndex = (int)initialMode;

        // Show publish button if wiki verified
        btnPublish.Visibility = _main.Settings.WikiVerified
            ? Visibility.Visible
            : Visibility.Collapsed;

        Loaded += (_, _) => GenerateOutput();
    }

    private void TabMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _currentMode = (MysteryGeneratorMode)tabMode.SelectedIndex;
        GenerateOutput();
    }

    private void GenerateOutput()
    {
        try
        {
            _fullOutput = _currentMode switch
            {
                MysteryGeneratorMode.Rewards =>
                    MysteryWikiService.GenerateRewardTemplate(_mystery, _mapping),
                MysteryGeneratorMode.EventPage =>
                    MysteryWikiService.GenerateEventPage(_mystery, _mystery.WikiStatus.MatchingVariant),
                MysteryGeneratorMode.EventItemPage =>
                    MysteryWikiService.GenerateEventItemPage(_mystery, _main.DataService, _main.WikiMapping),
                _ => ""
            };

            txtOutput.Text = _fullOutput;
            txtOutput.Visibility = Visibility.Visible;
            txtOutputPlaceholder.Visibility = Visibility.Collapsed;
            btnCopy.IsEnabled = true;
            warningBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            warningBar.Message = $"Generation error: {ex.Message}";
            warningBar.Severity = InfoBarSeverity.Error;
            warningBar.IsOpen = true;
        }
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_fullOutput)) return;

        bool success = false;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetDataObject(_fullOutput, false);
                success = true;
                Increment(s => s.MysteryTemplatesGenerated++);
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

    private async void BtnPublish_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_fullOutput)) return;

        if (!_main.Settings.WikiVerified)
        {
            warningBar.Message = "Wiki account not verified. Go to Settings → Wiki to log in.";
            warningBar.Severity = InfoBarSeverity.Warning;
            warningBar.IsOpen = true;
            return;
        }

        var pageTitle = await GetPageTitleForModeAsync();
        if (string.IsNullOrEmpty(pageTitle))
        {
            warningBar.Message = "Cannot determine page title.";
            warningBar.Severity = InfoBarSeverity.Warning;
            warningBar.IsOpen = true;
            return;
        }

        btnPublish.IsEnabled = false;
        warningBar.Message = $"Publishing to {pageTitle}...";
        warningBar.Severity = InfoBarSeverity.Informational;
        warningBar.IsOpen = true;

        try
        {
            var summary = _currentMode switch
            {
                MysteryGeneratorMode.Rewards => "Create reward template (via MergeMansionWikiTools)",
                MysteryGeneratorMode.EventPage => "Create mystery page (via MergeMansionWikiTools)",
                MysteryGeneratorMode.EventItemPage => "Create event item page (via MergeMansionWikiTools)",
                _ => "Edit via MergeMansionWikiTools"
            };

            var result = await MysteryWikiService.PublishPageAsync(
                _main.Settings.WikiUsername,
                _main.Settings.WikiPassword,
                pageTitle, _fullOutput, summary);

            warningBar.Message = $"Published: {result}";
            warningBar.Severity = InfoBarSeverity.Success;
            warningBar.IsOpen = true;

            Increment(s => s.MysteryPagesPublished++);
        }
        catch (Exception ex)
        {
            warningBar.Message = $"Publish failed: {ex.Message}";
            warningBar.Severity = InfoBarSeverity.Error;
            warningBar.IsOpen = true;
        }
        finally
        {
            btnPublish.IsEnabled = true;
        }
    }

    private async Task<string?> GetPageTitleForModeAsync()
    {
        switch (_currentMode)
        {
            case MysteryGeneratorMode.Rewards:
                // Determine next variant number
                var isPet = _mystery.MysteryType == MysteryType.Pet;
                var variant = await MysteryWikiService.GetNextVariantNameAsync(isPet);
                var suffix = string.IsNullOrEmpty(variant) ? "" : $"/{variant}";
                return $"Template:Mystery Pass/Rewards{suffix}";

            case MysteryGeneratorMode.EventPage:
                return _mystery.WikiStatus.SuggestedPageTitle ?? _mystery.Name;

            case MysteryGeneratorMode.EventItemPage:
                return _mystery.EventItemName;

            default:
                return null;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
