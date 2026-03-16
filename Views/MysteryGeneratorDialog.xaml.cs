using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public enum MysteryGeneratorMode
{
    EventPage,
    Rewards,
    EventItemPage,
    Images
}

public partial class MysteryGeneratorDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly MysteryEvent _mystery;
    private readonly MysteryItemMapping? _mapping;
    private readonly DialogueService? _dialogueService;
    private MysteryGeneratorMode _currentMode;
    private bool _suppressScrollSync;

    /// <summary>Full generated text for the current mode.</summary>
    private string _fullOutput = "";
    private bool _isDiffMode;

    // Diff colors
    private static readonly Brush BrushAddedBg = new SolidColorBrush(Color.FromArgb(0x25, 0x30, 0xC0, 0x30));
    private static readonly Brush BrushRemovedBg = new SolidColorBrush(Color.FromArgb(0x25, 0xD0, 0x40, 0x40));
    private static readonly Brush BrushAddedFg = new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0x40));
    private static readonly Brush BrushRemovedFg = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));

    public MysteryGeneratorDialog(
        MainWindow main, MysteryEvent mystery,
        MysteryItemMapping? mapping, MysteryGeneratorMode initialMode,
        DialogueService? dialogueService = null)
    {
        _main = main;
        _mystery = mystery;
        _mapping = mapping;
        _dialogueService = dialogueService;
        _currentMode = initialMode;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        txtMysteryInfo.Text = mystery.Name;
        var dateStr = mystery.StartDate?.ToString("MMM d, yyyy") ?? "Unknown";
        var typeStr = mystery.MysteryType == MysteryType.Pet ? "Pet" : "Standard";
        txtMysteryDetail.Text = $"{typeStr} · {dateStr} · {mystery.FreeTier.Count} levels · Event Item: {mystery.EventItemName ?? "Unknown"}";

        tabMode.SelectedIndex = (int)initialMode;

        btnPublish.Visibility = _main.Settings.WikiVerified
            ? Visibility.Visible : Visibility.Collapsed;

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
        // Reset view state
        pnlOutput.Visibility = Visibility.Collapsed;
        pnlDiff.Visibility = Visibility.Collapsed;
        imagesControl.Visibility = Visibility.Collapsed;
        _isDiffMode = false;

        if (_currentMode == MysteryGeneratorMode.Images)
        {
            imagesControl.Visibility = Visibility.Visible;
            imagesControl.Initialize(_main, _mystery);
            btnCopy.IsEnabled = false;
            btnCopy.Visibility = Visibility.Collapsed;
            btnPublish.Visibility = Visibility.Collapsed;
            return;
        }
        btnCopy.Visibility = Visibility.Visible;

        try
        {
            _fullOutput = _currentMode switch
            {
                MysteryGeneratorMode.Rewards =>
                    MysteryWikiService.GenerateRewardTemplate(_mystery, _mapping),
                MysteryGeneratorMode.EventPage =>
                    MysteryWikiService.GenerateEventPageWithDialogues(
                        _mystery, _mystery.WikiStatus.MatchingVariant, _dialogueService),
                MysteryGeneratorMode.EventItemPage =>
                    MysteryWikiService.GenerateEventItemPage(_mystery, _main.DataService, _main.WikiMapping),
                _ => ""
            };

            // Show diff if wiki page/template exists
            bool showDiff = _currentMode switch
            {
                MysteryGeneratorMode.EventPage => _mystery.WikiStatus.EventPageExists == true,
                MysteryGeneratorMode.EventItemPage => _mystery.WikiStatus.EventItemPageExists == true,
                MysteryGeneratorMode.Rewards => _mystery.WikiStatus.RewardTemplateMatches == true,
                _ => false
            };

            if (showDiff)
            {
                _ = LoadDiffAsync();
                return;
            }

            // No diff — show plain generated output
            ShowPlainOutput();
        }
        catch (Exception ex)
        {
            ShowPlainOutput();
            warningBar.Message = $"Generation error: {ex.Message}";
            warningBar.Severity = InfoBarSeverity.Error;
            warningBar.IsOpen = true;
        }
    }

    private void ShowPlainOutput()
    {
        pnlOutput.Visibility = Visibility.Visible;
        txtOutput.Text = _fullOutput;
        txtOutput.Visibility = Visibility.Visible;
        txtOutputPlaceholder.Visibility = Visibility.Collapsed;
        btnCopy.IsEnabled = true;
        btnPublish.Visibility = _main.Settings.WikiVerified ? Visibility.Visible : Visibility.Collapsed;
        warningBar.IsOpen = false;
    }

    private async Task LoadDiffAsync()
    {
        pnlDiff.Visibility = Visibility.Visible;
        pnlDiffLoading.Visibility = Visibility.Visible;
        pnlLeft.Children.Clear();
        pnlRight.Children.Clear();

        try
        {
            var scope = _currentMode switch
            {
                MysteryGeneratorMode.EventPage => MysteryDiffScope.EventPage,
                MysteryGeneratorMode.Rewards => MysteryDiffScope.Rewards,
                _ => MysteryDiffScope.EventItemPage
            };

            var (wikiContent, generated, diffs) = await MysteryWikiService.ComputeDiffAsync(
                _mystery, scope, _main.DataService, _main.WikiMapping, _mapping, _dialogueService);

            pnlDiffLoading.Visibility = Visibility.Collapsed;

            if (wikiContent == null)
            {
                // Page doesn't exist — show plain output
                pnlDiff.Visibility = Visibility.Collapsed;
                ShowPlainOutput();
                return;
            }

            bool allMatch = diffs.All(d => d.Type == DiffLineType.Match);
            _isDiffMode = true;
            BuildDiffView(diffs);

            btnCopy.IsEnabled = true;
            btnPublish.Visibility = _main.Settings.WikiVerified ? Visibility.Visible : Visibility.Collapsed;

            if (allMatch)
            {
                warningBar.Message = "Content matches wiki. No changes needed.";
                warningBar.Severity = InfoBarSeverity.Success;
                warningBar.IsOpen = true;
                // Hide Publish for matching content
                btnPublish.Visibility = Visibility.Collapsed;
            }
            else
            {
                int added = diffs.Count(d => d.Type == DiffLineType.Added);
                int removed = diffs.Count(d => d.Type == DiffLineType.Removed);
                warningBar.Message = $"{added} added · {removed} removed";
                warningBar.Severity = InfoBarSeverity.Informational;
                warningBar.IsOpen = true;

                // For Rewards mismatch: show "Create New Reward Template" instead of "Publish to Wiki"
                if (_currentMode == MysteryGeneratorMode.Rewards)
                    btnPublish.Content = "Create New Reward Template";
                else
                    btnPublish.Content = "Publish to Wiki";
            }
        }
        catch (Exception ex)
        {
            pnlDiffLoading.Visibility = Visibility.Collapsed;
            pnlDiff.Visibility = Visibility.Collapsed;
            ShowPlainOutput();
            warningBar.Message = $"Diff failed: {ex.Message}";
            warningBar.Severity = InfoBarSeverity.Warning;
            warningBar.IsOpen = true;
        }
    }

    private void BuildDiffView(System.Collections.Generic.List<DiffLine> diffs)
    {
        pnlLeft.Children.Clear();
        pnlRight.Children.Clear();

        // Post-process: merge adjacent Removed+Added pairs that differ only in whitespace
        // into side-by-side "modified" lines with inline highlighting
        for (int i = 0; i < diffs.Count; i++)
        {
            var diff = diffs[i];

            if (diff.Type == DiffLineType.Match)
            {
                pnlLeft.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Match));
                pnlRight.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Match));
            }
            else if (diff.Type == DiffLineType.Removed && i + 1 < diffs.Count && diffs[i + 1].Type == DiffLineType.Added)
            {
                // Check if lines are "similar" (same content after whitespace normalization)
                var removed = diff.Text;
                var added = diffs[i + 1].Text;
                var normRemoved = Regex.Replace(removed.Trim(), @"\s+", " ");
                var normAdded = Regex.Replace(added.Trim(), @"\s+", " ");

                if (normRemoved == normAdded)
                {
                    // Whitespace-only difference → show side-by-side as modified (yellow)
                    pnlLeft.Children.Add(CreateModifiedLine(removed));
                    pnlRight.Children.Add(CreateModifiedLine(added));
                }
                else
                {
                    // Real content difference → show as removed + added (side by side)
                    pnlLeft.Children.Add(CreateDiffLine(removed, DiffLineType.Removed));
                    pnlRight.Children.Add(CreateDiffLine(added, DiffLineType.Added));
                }
                i++; // skip the Added line (already consumed)
            }
            else if (diff.Type == DiffLineType.Removed)
            {
                pnlLeft.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Removed));
                pnlRight.Children.Add(CreateDiffPlaceholder(DiffLineType.Removed));
            }
            else // Added
            {
                pnlLeft.Children.Add(CreateDiffPlaceholder(DiffLineType.Added));
                pnlRight.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Added));
            }
        }
    }

    private static Border CreateDiffLine(string text, DiffLineType type)
    {
        var border = new Border
        {
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(2)
        };
        var tb = new TextBlock
        {
            Text = string.IsNullOrEmpty(text) ? " " : text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap
        };
        switch (type)
        {
            case DiffLineType.Added:
                border.Background = BrushAddedBg;
                tb.Foreground = BrushAddedFg;
                break;
            case DiffLineType.Removed:
                border.Background = BrushRemovedBg;
                tb.Foreground = BrushRemovedFg;
                break;
            default:
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                break;
        }
        border.Child = tb;
        return border;
    }

    private static readonly Brush BrushModifiedBg = new SolidColorBrush(Color.FromArgb(0x20, 0xD0, 0xA0, 0x20));
    private static readonly Brush BrushModifiedFg = new SolidColorBrush(Color.FromRgb(0xD0, 0xB0, 0x40));

    private static Border CreateModifiedLine(string text)
    {
        var border = new Border
        {
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(2),
            Background = BrushModifiedBg
        };
        var tb = new TextBlock
        {
            Text = string.IsNullOrEmpty(text) ? " " : text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = BrushModifiedFg
        };
        border.Child = tb;
        return border;
    }

    private static Border CreateDiffPlaceholder(DiffLineType type)
    {
        var border = new Border
        {
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(2),
            Opacity = 0.3
        };
        var tb = new TextBlock { Text = " ", FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        border.Background = type == DiffLineType.Added ? BrushAddedBg : BrushRemovedBg;
        border.Child = tb;
        return border;
    }

    // ── Scroll sync ──

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

    // ── Images tab — initialized via MysteryImagesControl ──

    // ── Copy / Publish ──

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_fullOutput)) return;
        App.NativeSetClipboardText(_fullOutput);
        Increment(s => s.MysteryTemplatesGenerated++);
        btnCopy.Content = "Copied!";
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
            warningBar.Message = "Wiki account not verified.";
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
                MysteryGeneratorMode.EventPage => "Create/update mystery page (via MergeMansionWikiTools)",
                MysteryGeneratorMode.EventItemPage => "Create/update event item page (via MergeMansionWikiTools)",
                _ => "Edit via MergeMansionWikiTools"
            };

            var result = await MysteryWikiService.PublishPageAsync(
                _main.Settings.WikiUsername, _main.Settings.WikiPassword,
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
