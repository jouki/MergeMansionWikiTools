using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public partial class ClueCollectionPage : UserControl
{
    private readonly MainWindow _main;
    private ClueCollectionService? _service;
    private bool _loaded;

    public ClueCollectionPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        _ = TryLoadAsync();
    }

    // ── Loading ─────────────────────────────────────────────────────

    private async Task TryLoadAsync()
    {
        var path = _main.Settings.CardCollectionJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            pnlEmpty.Visibility = Visibility.Visible;
            txtEmpty.Text = "card_collection.json not configured.\nSet the path in Settings.";
            return;
        }

        try
        {
            _service = new ClueCollectionService();
            await _service.LoadAsync(path);
            txtStatus.Text = $"Found {_service.Cases.Count} cases. Checking wiki...";

            await _service.DetectExistingOnWikiAsync();

            int newCount = _service.Cases.Count(c => !c.ExistsOnWiki);
            txtStatus.Text = $"{_service.Cases.Count} cases loaded"
                + (newCount > 0 ? $" ({newCount} new)" : " (all on wiki)");

            pnlLoading.Visibility = Visibility.Collapsed;
            BuildCaseList();
            scrollCases.Visibility = Visibility.Visible;
            _loaded = true;
        }
        catch (Exception ex)
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            pnlEmpty.Visibility = Visibility.Visible;
            txtEmpty.Text = $"Failed to load: {ex.Message}";
        }
    }

    // ── Case list ───────────────────────────────────────────────────

    private void BuildCaseList()
    {
        pnlCases.Children.Clear();
        foreach (var caseObj in _service!.Cases)
            pnlCases.Children.Add(CreateCaseCard(caseObj));
    }

    private Border CreateCaseCard(ClueCollectionCase caseObj)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 4)
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: name + info
        var infoPanel = new StackPanel();
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        var nameText = new TextBlock
        {
            Text = $"Case {caseObj.Index}: {caseObj.DisplayName}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        namePanel.Children.Add(nameText);

        if (!caseObj.ExistsOnWiki)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = "NEW",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            namePanel.Children.Add(badge);
        }
        infoPanel.Children.Add(namePanel);

        int totalClues = caseObj.Sets.Sum(s => s.CardCount);
        var metaText = new TextBlock
        {
            Text = $"{caseObj.Sets.Count} sets  ·  {totalClues} clues",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        metaText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        infoPanel.Children.Add(metaText);

        Grid.SetColumn(infoPanel, 0);
        grid.Children.Add(infoPanel);

        // Right: Update Wiki button (new cases) or checkmark (existing)
        if (!caseObj.ExistsOnWiki)
        {
            var btn = new Wpf.Ui.Controls.Button
            {
                Content = "Update Wiki",
                Appearance = ControlAppearance.Primary,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = caseObj
            };
            btn.Click += BtnUpdateWiki_Click;
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);
        }
        else
        {
            var check = new TextBlock
            {
                Text = "\u2713",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }

        border.Child = grid;
        return border;
    }

    // ── Preview + Confirm dialog ────────────────────────────────────

    private async void BtnUpdateWiki_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not ClueCollectionCase caseObj) return;
        if (!_main.Settings.WikiVerified)
        {
            ShowInfo("Wiki account not verified.", InfoBarSeverity.Warning);
            return;
        }

        btn.IsEnabled = false;
        ShowInfo("Preparing preview...", InfoBarSeverity.Informational);

        try
        {
            // Gather preview data
            var steps = await BuildPreviewStepsAsync(caseObj);
            infoBar.IsOpen = false;

            // Show preview dialog
            var dialog = BuildPreviewDialog(caseObj, steps);
            if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                btn.IsEnabled = true;
                return;
            }

            // Execute selected steps
            ShowInfo("Updating wiki...", InfoBarSeverity.Informational);
            var results = new List<string>();

            if (steps[0].IsEnabled) // Module
            {
                try
                {
                    results.Add("Module: " + await UpdateModuleAsync(caseObj));
                }
                catch (Exception ex) { results.Add("Module: " + ex.Message); }
            }
            if (steps[1].IsEnabled) // History row
            {
                try
                {
                    results.Add("Page: " + await UpdateClueCollectionPageAsync(caseObj));
                }
                catch (Exception ex) { results.Add("Page: " + ex.Message); }
            }
            if (steps.Count > 2 && steps[2].IsEnabled) // Images
            {
                try
                {
                    results.Add("Images: " + await UploadImagesAsync(caseObj));
                }
                catch (Exception ex) { results.Add("Images: " + ex.Message); }
            }

            ShowInfo(string.Join(" | ", results), InfoBarSeverity.Success);
            caseObj.ExistsOnWiki = true;
            BuildCaseList();
        }
        catch (Exception ex)
        {
            ShowInfo("Failed: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private async Task<List<MysteryUpdateStep>> BuildPreviewStepsAsync(ClueCollectionCase caseObj)
    {
        var steps = new List<MysteryUpdateStep>();

        // Step 1: Module:Datatable/Various
        string moduleContent = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Various") ?? "";
        bool moduleExists = moduleContent.Contains(caseObj.DisplayName, StringComparison.OrdinalIgnoreCase);
        string moduleEntry = ClueCollectionService.GenerateModuleEntry(caseObj);
        steps.Add(new MysteryUpdateStep
        {
            Title = "Update Module:Datatable/Various",
            Detail = moduleExists ? "Already listed (no change)" : $"Add Case {caseObj.Index}: {caseObj.DisplayName} ({caseObj.Sets.Count} sets)",
            IsNoChange = moduleExists,
            IsEnabled = !moduleExists,
            WikiUrl = "https://merge-mansion.fandom.com/wiki/Module:Datatable/Various",
            Icon = "\ud83d\udcda",
            ContentPreview = moduleExists ? null : moduleEntry.Trim()
        });

        // Step 2: History table row
        string pageContent = await MysteryWikiService.FetchPageContentAsync("Clue Collection") ?? "";
        bool pageHasCase = pageContent.Contains(caseObj.DisplayName, StringComparison.OrdinalIgnoreCase);
        string historyRow = ClueCollectionService.GenerateHistoryRow(caseObj);
        steps.Add(new MysteryUpdateStep
        {
            Title = "Update Clue Collection page (History)",
            Detail = pageHasCase ? "Already listed (no change)" : $"Add History row for Case {caseObj.Index}",
            IsNoChange = pageHasCase,
            IsEnabled = !pageHasCase,
            WikiUrl = "https://merge-mansion.fandom.com/wiki/Clue_Collection",
            Icon = "\ud83d\udccb",
            ContentPreview = pageHasCase ? null : historyRow.Trim()
        });

        // Step 3: Images
        var exportDir = ResolveExportDir();
        if (!string.IsNullOrEmpty(exportDir))
        {
            var images = ClueCollectionService.FindExistingImages(exportDir, caseObj);
            var expected = ClueCollectionService.GetExpectedImageFiles(caseObj);
            int wrongSize = images.Count(i => i.Width != 424 || i.Height != 512);
            string imgDetail = $"{images.Count}/{expected.Count} images found in Export - PNGs";
            if (wrongSize > 0) imgDetail += $" ({wrongSize} with wrong resolution)";

            steps.Add(new MysteryUpdateStep
            {
                Title = "Upload card images",
                Detail = images.Count == 0 ? "No images found" : imgDetail,
                IsNoChange = images.Count == 0,
                IsEnabled = images.Count > 0,
                Icon = "\ud83d\uddbc\ufe0f"
            });
        }

        return steps;
    }

    private Wpf.Ui.Controls.MessageBox BuildPreviewDialog(ClueCollectionCase caseObj, List<MysteryUpdateStep> steps)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Update Clue Collection Wiki Pages",
            PrimaryButtonText = "Update",
            CloseButtonText = "Cancel",
            MinWidth = 550
        };

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        // Header
        var header = new TextBlock
        {
            Text = $"Case {caseObj.Index}: {caseObj.DisplayName}",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        panel.Children.Add(header);

        int enabledCount = steps.Count(s => s.IsEnabled);
        var summary = new TextBlock
        {
            Text = $"{steps.Count} pages checked  {(enabledCount > 0 ? $"{enabledCount} selected for update" : "no changes selected")}",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(summary);

        // Steps
        foreach (var step in steps)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

            var stepGrid = new Grid();
            stepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Checkbox
            if (!step.IsNoChange)
            {
                var cb = new System.Windows.Controls.CheckBox
                {
                    IsChecked = step.IsEnabled,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 8, 0)
                };
                cb.Checked += (_, _) => step.IsEnabled = true;
                cb.Unchecked += (_, _) => step.IsEnabled = false;
                Grid.SetColumn(cb, 0);
                stepGrid.Children.Add(cb);
            }

            var contentPanel = new StackPanel();
            var titleText = new TextBlock
            {
                Text = $"{step.Icon} {step.Title}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            contentPanel.Children.Add(titleText);

            var detailText = new TextBlock
            {
                Text = step.IsNoChange ? $"? {step.Detail}" : step.Detail,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            detailText.SetResourceReference(TextBlock.ForegroundProperty,
                step.IsNoChange ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush");
            contentPanel.Children.Add(detailText);

            // Content preview (green code block with + prefix, matching Mystery style)
            if (!string.IsNullOrEmpty(step.ContentPreview))
            {
                var previewBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x30, 0xC0, 0x30)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 6, 0, 0)
                };
                // Add "+ " prefix to each non-empty line
                string prefixed = string.Join("\n", step.ContentPreview
                    .Split('\n')
                    .Select(l => string.IsNullOrWhiteSpace(l) ? l : "+ " + l));
                var previewText = new TextBlock
                {
                    Text = prefixed,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0x40)),
                    MaxHeight = 200
                };
                previewBorder.Child = previewText;
                contentPanel.Children.Add(previewBorder);
            }

            Grid.SetColumn(contentPanel, 1);
            stepGrid.Children.Add(contentPanel);
            card.Child = stepGrid;
            panel.Children.Add(card);
        }

        box.Content = panel;
        return box;
    }

    // ── Wiki update operations ──────────────────────────────────────

    private async Task<string> UpdateModuleAsync(ClueCollectionCase caseObj)
    {
        var moduleContent = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Various");
        if (string.IsNullOrEmpty(moduleContent))
            throw new Exception("Could not fetch Module:Datatable/Various.");

        if (moduleContent.Contains($"[{caseObj.Index}]") &&
            moduleContent.Contains(caseObj.DisplayName, StringComparison.OrdinalIgnoreCase))
            return "Already exists.";

        string entry = ClueCollectionService.GenerateModuleEntry(caseObj);

        int sectionStart = moduleContent.IndexOf("p.clueCollections = {", StringComparison.Ordinal);
        if (sectionStart < 0) throw new Exception("p.clueCollections section not found.");

        int bracePos = moduleContent.IndexOf('{', sectionStart + 20);
        if (bracePos < 0) throw new Exception("Opening brace not found.");

        int insertPos = bracePos + 1;
        while (insertPos < moduleContent.Length && (moduleContent[insertPos] == '\n' || moduleContent[insertPos] == '\r'))
            insertPos++;

        string updated = moduleContent[..insertPos] + "\n" + entry + moduleContent[insertPos..];

        return await MysteryWikiService.PublishPageAsync(
            _main.Settings.WikiUsername, _main.Settings.WikiPassword,
            "Module:Datatable/Various", updated,
            $"Add Clue Collection #{caseObj.Index}: {caseObj.DisplayName} (via MergeMansionWikiTools)");
    }

    private async Task<string> UpdateClueCollectionPageAsync(ClueCollectionCase caseObj)
    {
        var pageContent = await MysteryWikiService.FetchPageContentAsync("Clue Collection");
        if (string.IsNullOrEmpty(pageContent))
            throw new Exception("Could not fetch Clue Collection page.");

        // Add History row
        int historyIdx = pageContent.IndexOf("== History ==", StringComparison.Ordinal);
        if (historyIdx < 0) throw new Exception("History section not found.");
        int tableEnd = pageContent.IndexOf("|}", historyIdx);
        if (tableEnd < 0) throw new Exception("History table end not found.");

        string newRow = ClueCollectionService.GenerateHistoryRow(caseObj);
        string updated = pageContent[..tableEnd] + newRow + pageContent[tableEnd..];

        // Add Set Rewards invoke to Rewards section if not present
        string invoke = $"{{{{#Invoke:Various|GetClueCollectionSetRewards|{caseObj.Index}}}}}";
        if (!updated.Contains("GetClueCollectionSetRewards", StringComparison.OrdinalIgnoreCase))
        {
            // Insert before History section
            int rewardsEnd = updated.IndexOf("== History ==", StringComparison.Ordinal);
            if (rewardsEnd > 0)
                updated = updated[..rewardsEnd] + $"=== Case {caseObj.Index}: {caseObj.DisplayName} ===\n{invoke}\n\n" + updated[rewardsEnd..];
        }

        return await MysteryWikiService.PublishPageAsync(
            _main.Settings.WikiUsername, _main.Settings.WikiPassword,
            "Clue Collection", updated,
            $"Add Clue Collection #{caseObj.Index}: History row + set rewards (via MergeMansionWikiTools)");
    }

    // ── Image upload ────────────────────────────────────────────────

    private async Task<string> UploadImagesAsync(ClueCollectionCase caseObj)
    {
        var exportDir = ResolveExportDir();
        if (string.IsNullOrEmpty(exportDir)) return "No export directory.";

        var images = ClueCollectionService.FindExistingImages(exportDir, caseObj);
        if (images.Count == 0) return "No images found.";

        // Set TinyPNG key
        string? apiKey = _main.Settings.TinifyApiKey;
        string? apiKey2 = _main.Settings.TinifyApiKey2;
        bool canOptimize = !string.IsNullOrWhiteSpace(apiKey);
        if (canOptimize) TinifyAPI.Tinify.Key = apiKey!;

        using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
            _main.Settings.WikiUsername, _main.Settings.WikiPassword);
        var csrfJson = await client.GetStringAsync(
            "https://merge-mansion.fandom.com/api.php?action=query&meta=tokens&format=json");
        var csrfToken = System.Text.Json.JsonDocument.Parse(csrfJson).RootElement
            .GetProperty("query").GetProperty("tokens")
            .GetProperty("csrftoken").GetString()!;

        int uploaded = 0;
        int optimized = 0;
        int errors = 0;
        int total = images.Count;

        foreach (var (fileName, fullPath, width, height) in images)
        {
            try
            {
                ShowInfo($"Uploading images... {uploaded + errors + 1}/{total} ({fileName})", InfoBarSeverity.Informational);
                var bytes = await File.ReadAllBytesAsync(fullPath);

                // TinyPNG optimize if not already optimized
                if (canOptimize && !OptimizationWindow.HasOptMarker(bytes))
                {
                    try
                    {
                        bytes = await (await TinifyAPI.Tinify.FromBuffer(bytes)).ToBuffer();
                        optimized++;
                    }
                    catch
                    {
                        if (!string.IsNullOrWhiteSpace(apiKey2))
                        {
                            try
                            {
                                TinifyAPI.Tinify.Key = apiKey2!;
                                bytes = await (await TinifyAPI.Tinify.FromBuffer(bytes)).ToBuffer();
                                optimized++;
                            }
                            catch { /* upload unoptimized */ }
                        }
                    }
                }

                await WikiMappingService.UploadFileAsync(client, csrfToken, fileName, bytes, "{{Permission}}");
                uploaded++;

                // Brief delay to avoid rate limiting
                await Task.Delay(500);
            }
            catch
            {
                errors++;
            }
        }

        string result = $"{uploaded} uploaded";
        if (optimized > 0) result += $", {optimized} optimized";
        if (errors > 0) result += $", {errors} errors";
        return result;
    }

    private string? ResolveExportDir()
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version)) return null;
        var dir = Path.Combine(basePath, version, "Export - PNGs");
        return Directory.Exists(dir) ? dir : null;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }
}
