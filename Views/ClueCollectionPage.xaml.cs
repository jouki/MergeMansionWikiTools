using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
    private bool _suppressVersionChange;
    private string? _overrideExportDir;

    /// <summary>ItemsSource virtualizovaného listu — instance se nemění, rebuild = Clear + Add
    /// (zachová scroll pozici stejně jako dřívější pnlCases.Children.Clear()+Add).</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<ClueCaseCardItem> _caseItems = new();

    public ClueCollectionPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        lstCases.ItemsSource = _caseItems;
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
            // Resolve item names AFTER loading (DataService may not be ready during parse)
            if (_main.DataService != null)
            {
                _service.SetDataService(_main.DataService, _main.WikiMapping);
                _service.ResolveRewardNames();
            }
            txtStatus.Text = $"Found {_service.Cases.Count} cases. Checking wiki...";

            await _service.DetectExistingOnWikiAsync();

            int newCount = _service.Cases.Count(c => !c.ExistsOnWiki);
            txtStatus.Text = $"{_service.Cases.Count} cases loaded"
                + (newCount > 0 ? $" ({newCount} new)" : " (all on wiki)");

            pnlLoading.Visibility = Visibility.Collapsed;
            BuildCaseList();
            lstCases.Visibility = Visibility.Visible;
            _loaded = true;
            PopulateImageFolderDropdown();
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
        // Plný rebuild seznamu (stejný mechanismus jako dřív) — per-case stav (NEW badge,
        // Check Sizes, vzhled Update Wiki, fajfka) se přepočítá v konstruktoru view-modelu.
        _caseItems.Clear();
        foreach (var caseObj in _service!.Cases)
            _caseItems.Add(new ClueCaseCardItem(caseObj));
    }

    private void PopulateImageFolderDropdown()
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
        {
            pnlImageFolder.Visibility = Visibility.Collapsed;
            return;
        }

        var versions = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(basePath))
        {
            if (Directory.Exists(Path.Combine(dir, "Export - PNGs")))
                versions.Add(Path.GetFileName(dir));
        }

        if (versions.Count == 0)
        {
            pnlImageFolder.Visibility = Visibility.Collapsed;
            return;
        }

        versions.Sort((a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));

        _suppressVersionChange = true;
        cmbImageVersion.ItemsSource = versions;
        var selected = _main.Settings.SelectedApkVersion;
        int idx = !string.IsNullOrEmpty(selected) ? versions.IndexOf(selected) : -1;
        cmbImageVersion.SelectedIndex = idx >= 0 ? idx : 0;
        _suppressVersionChange = false;

        pnlImageFolder.Visibility = Visibility.Visible;
    }

    private void CmbImageVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVersionChange) return;
        if (cmbImageVersion.SelectedItem is string version)
        {
            var basePath = _main.Settings.ImageExporterBasePath;
            var dir = Path.Combine(basePath, version, "Export - PNGs");
            _overrideExportDir = Directory.Exists(dir) ? dir : null;
        }
    }

    // Vizuál case karty žije v XAML DataTemplate (lstCases.ItemTemplate) nad ClueCaseCardItem.
    // Click handlery (BtnUploadImages_Click / BtnCheckCaseSizes_Click / BtnUpdateWiki_Click)
    // dostávají ClueCollectionCase přes Tag="{Binding Case}" — signatury beze změny.

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
            // Ensure DataService is available for item name resolution
            if (_main.DataService != null && _service != null)
            {
                _service.SetDataService(_main.DataService, _main.WikiMapping);
                _service.ResolveRewardNames();
            }

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
            if (steps.Count > 3 && steps[3].IsEnabled) // Table merge
            {
                try
                {
                    results.Add("Table: " + await MergeHistoryTableAsync());
                }
                catch (Exception ex) { results.Add("Table: " + ex.Message); }
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
        var exportDir = ResolveExportDir();

        // Step 1: Module:Datatable/Various — check name exists AND rewards are present
        string moduleContent = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Various") ?? "";
        bool moduleHasName = moduleContent.Contains(caseObj.DisplayName, StringComparison.OrdinalIgnoreCase);
        bool moduleHasRewards = moduleHasName && caseObj.Sets.All(s =>
            s.Rewards.Count == 0 || moduleContent.Contains($"reward =", StringComparison.Ordinal));
        // More precise: check if the specific case section has reward fields for its sets
        bool moduleNeedsRewardUpdate = false;
        if (moduleHasName)
        {
            // Extract the case block from p.clueCollections
            string? caseBlock = ExtractCaseBlock(moduleContent, caseObj);
            if (caseBlock != null)
            {
                // Find clueSets subsection to search within (skip case header line)
                int clueSetsStart = caseBlock.IndexOf("clueSets", StringComparison.Ordinal);
                string setsBlock = clueSetsStart >= 0 ? caseBlock[clueSetsStart..] : caseBlock;

                // Only check groupImage if we can actually detect them (CategoryPhoto files exist)
                Dictionary<(int, int), int>? detectedGi = null;
                if (!string.IsNullOrEmpty(exportDir))
                    detectedGi = ClueCollectionService.DetectGroupImages(exportDir, new List<ClueCollectionCase> { caseObj });
                bool canCheckGroupImage = detectedGi != null && detectedGi.Count > 0;

                foreach (var set in caseObj.Sets)
                {
                    int setPos = setsBlock.IndexOf($"[{set.Index}]", StringComparison.Ordinal);
                    if (setPos < 0) continue;
                    int lineEnd = setsBlock.IndexOf('\n', setPos);
                    if (lineEnd < 0) lineEnd = setsBlock.Length;
                    string line = setsBlock[setPos..lineEnd];

                    // Check reward missing
                    if (set.Rewards.Count > 0 && !line.Contains("reward", StringComparison.OrdinalIgnoreCase))
                    {
                        moduleNeedsRewardUpdate = true;
                        break;
                    }
                    // Check reward VALUE is correct (not just present)
                    if (set.Rewards.Count > 0)
                    {
                        string expectedReward = string.Join("<br>", set.Rewards);
                        var rewardMatch = System.Text.RegularExpressions.Regex.Match(line, @"reward\s*=\s*""([^""]+)""");
                        if (rewardMatch.Success && rewardMatch.Groups[1].Value != expectedReward)
                        {
                            moduleNeedsRewardUpdate = true;
                            break;
                        }
                    }
                    // Check groupImage missing
                    if (canCheckGroupImage && !line.Contains("groupImage", StringComparison.OrdinalIgnoreCase))
                    {
                        moduleNeedsRewardUpdate = true;
                        break;
                    }
                }
            }
            else
            {
                // Case block not found — shouldn't happen if moduleHasName is true
                moduleNeedsRewardUpdate = true;
            }
        }

        bool moduleIsNoChange = moduleHasName && !moduleNeedsRewardUpdate;
        string moduleDetail;
        string? modulePreview;
        if (!moduleHasName)
        {
            moduleDetail = $"Add Case {caseObj.Index}: {caseObj.DisplayName} ({caseObj.Sets.Count} sets)";
            var previewGroupImages = exportDir != null && _service != null
                ? ClueCollectionService.DetectGroupImages(exportDir, new List<ClueCollectionCase> { caseObj }) : null;
            modulePreview = ClueCollectionService.GenerateModuleEntry(caseObj, previewGroupImages).Trim();
        }
        else if (moduleNeedsRewardUpdate)
        {
            // Build list of what's actually missing per set
            var missingLines = new List<string>();
            string? caseBlockForPreview = ExtractCaseBlock(moduleContent, caseObj);
            string? setsBlockForPreview = null;
            if (caseBlockForPreview != null)
            {
                int csPos = caseBlockForPreview.IndexOf("clueSets", StringComparison.Ordinal);
                setsBlockForPreview = csPos >= 0 ? caseBlockForPreview[csPos..] : caseBlockForPreview;
            }

            Dictionary<(int, int), int>? previewGi = null;
            if (!string.IsNullOrEmpty(exportDir))
                previewGi = ClueCollectionService.DetectGroupImages(exportDir, new List<ClueCollectionCase> { caseObj });
            bool canDetectGroupImage = previewGi != null && previewGi.Count > 0;

            foreach (var set in caseObj.Sets)
            {
                bool needsReward = false, needsGroupImage = false;
                string? rewardDiffInfo = null;
                if (setsBlockForPreview != null)
                {
                    int sp = setsBlockForPreview.IndexOf($"[{set.Index}]", StringComparison.Ordinal);
                    if (sp >= 0)
                    {
                        int le = setsBlockForPreview.IndexOf('\n', sp);
                        string line = le >= 0 ? setsBlockForPreview[sp..le] : setsBlockForPreview[sp..];
                        needsReward = set.Rewards.Count > 0 && !line.Contains("reward", StringComparison.OrdinalIgnoreCase);
                        // Also check if reward VALUE is wrong
                        if (!needsReward && set.Rewards.Count > 0)
                        {
                            string expected = string.Join("<br>", set.Rewards);
                            var rm = System.Text.RegularExpressions.Regex.Match(line, @"reward\s*=\s*""([^""]+)""");
                            if (rm.Success && rm.Groups[1].Value != expected)
                            {
                                needsReward = true;
                                rewardDiffInfo = $"FIX: {rm.Groups[1].Value}";
                            }
                        }
                        needsGroupImage = canDetectGroupImage && !line.Contains("groupImage", StringComparison.OrdinalIgnoreCase);
                    }
                }
                else
                {
                    needsReward = set.Rewards.Count > 0;
                    needsGroupImage = canDetectGroupImage;
                }

                var parts = new List<string>();
                if (needsReward)
                {
                    string r = $"reward = \"{string.Join("<br>", set.Rewards)}\"";
                    if (rewardDiffInfo != null) r += $"  ({rewardDiffInfo})";
                    parts.Add(r);
                }
                if (needsGroupImage) parts.Add("groupImage = ?");
                if (parts.Count > 0)
                    missingLines.Add($"[{set.Index}] {set.DisplayName}: {string.Join(", ", parts)}");
            }

            moduleDetail = $"Update {missingLines.Count} sets in Case {caseObj.Index} (missing fields)";
            modulePreview = string.Join("\n", missingLines);
        }
        else
        {
            moduleDetail = "Already listed (no change)";
            modulePreview = null;
        }

        steps.Add(new MysteryUpdateStep
        {
            Title = "Update Module:Datatable/Various",
            Detail = moduleDetail,
            IsNoChange = moduleIsNoChange,
            IsEnabled = !moduleIsNoChange,
            WikiUrl = "https://merge-mansion.fandom.com/wiki/Module:Datatable/Various",
            Icon = "\ud83d\udcda",
            ContentPreview = modulePreview
        });

        // Step 2: History table row — check if case index is already in the table
        string pageContent = await MysteryWikiService.FetchPageContentAsync("Clue Collection") ?? "";
        // Look for GetClueCollectionName|N or explicit "| N\n" row pattern
        bool pageHasCase = pageContent.Contains($"GetClueCollectionName|{caseObj.Index}}}", StringComparison.Ordinal)
            || pageContent.Contains($"GetClueCollectionName|{caseObj.Index} }}", StringComparison.Ordinal);
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

        // Step 3: Images — check local AND wiki existence
        if (!string.IsNullOrEmpty(exportDir))
        {
            var images = ClueCollectionService.FindExistingImages(exportDir, caseObj);
            var expected = ClueCollectionService.GetExpectedImageFiles(caseObj);

            // Batch wiki check to find how many are already uploaded
            int onWikiCount = 0;
            if (images.Count > 0)
            {
                var imgWikiNames = images.Select(i => "File:" + i.FileName).ToList();
                var imgExistMap = new Dictionary<string, bool>();
                for (int b = 0; b < imgWikiNames.Count; b += 50)
                {
                    var br = await MysteryWikiService.CheckPagesExistAsync(imgWikiNames.Skip(b).Take(50).ToList());
                    foreach (var kv in br) imgExistMap[kv.Key] = kv.Value;
                }
                onWikiCount = images.Count(i => imgExistMap.GetValueOrDefault("File:" + i.FileName));
            }

            bool allOnWiki = images.Count > 0 && onWikiCount == images.Count;
            int newImgCount = images.Count - onWikiCount;
            int wrongSize = images.Count(i => i.Width != 424 || i.Height != 512);

            string imgDetail;
            if (allOnWiki)
                imgDetail = $"All {images.Count} images already on wiki (no change)";
            else if (images.Count == 0)
                imgDetail = "No images found in Export - PNGs";
            else
            {
                imgDetail = $"{newImgCount} new images to upload ({onWikiCount} already on wiki)";
                if (wrongSize > 0) imgDetail += $" \u26a0 {wrongSize} with wrong resolution";
            }

            string? imgPreview = null;
            if (newImgCount > 0)
            {
                var lines = new List<string>();
                foreach (var set in caseObj.Sets)
                {
                    string prefix = $"TCE_Case{caseObj.Index:D2}_Set{set.Index:D2}_";
                    int setImgs = images.Count(i => i.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    lines.Add($"Set {set.Index}: {set.DisplayName} — {setImgs}/{set.CardCount} images");
                }
                imgPreview = string.Join("\n", lines);
            }

            steps.Add(new MysteryUpdateStep
            {
                Title = "Upload card images",
                Detail = imgDetail,
                IsNoChange = allOnWiki || images.Count == 0,
                IsEnabled = newImgCount > 0,
                Icon = "\ud83d\uddbc\ufe0f",
                ContentPreview = imgPreview
            });
        }
        else
        {
            steps.Add(new MysteryUpdateStep
            {
                Title = "Upload card images",
                Detail = "Export - PNGs folder not found",
                IsNoChange = true,
                IsEnabled = false,
                Icon = "\ud83d\uddbc\ufe0f"
            });
        }

        // Step 4: History table merge (rowspan identical rows)
        if (_service != null && _service.Cases.Count >= 5)
        {
            // Compare the freshly merged table against what's currently on the page. The old
            // heuristic only checked for the presence of "rowspan", which was wrong: a partially
            // merged table (e.g. cases 5-9 merged, but newer 10/11 each added as their own row)
            // already contains rowspan, so the step was flagged "no change" and the new rows never
            // got merged. Regenerating also refreshes stale reward cells (e.g. old "Reward Box" →
            // correct "Area Reward Box" for Tcce_final_chest_producers), so identical rows finally
            // collapse together.
            var mergedTable = ClueCollectionService.GenerateMergedHistoryTable(_service.Cases);
            string? currentTable = ExtractHistoryTable(pageContent);
            bool alreadyMerged = currentTable != null
                && NormalizeTable(currentTable) == NormalizeTable(mergedTable);
            steps.Add(new MysteryUpdateStep
            {
                Title = "Refactor History table (row merging)",
                Detail = alreadyMerged ? "Table already matches merged layout (no change)"
                    : "Merge identical Clue Sets / Total clues / Duration / Grand Reward columns with rowspan",
                IsNoChange = alreadyMerged,
                IsEnabled = false, // default off — user opts in
                Icon = "\ud83d\udd27",
                ContentPreview = alreadyMerged ? null : mergedTable.Trim()
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
            MinWidth = 630
        };

        var scrollContent = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

        var header = new TextBlock
        {
            Text = $"Case {caseObj.Index}: {caseObj.DisplayName}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        scrollContent.Children.Add(header);

        int enabledCount = steps.Count(s => s.IsEnabled);
        var summary = new TextBlock
        {
            Text = $"{steps.Count} pages checked  {(enabledCount > 0 ? $"{enabledCount} selected for update" : "no changes selected")}",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        scrollContent.Children.Add(summary);

        // Step cards (inside the same scroll)
        var stepsPanel = scrollContent;
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

            // Content preview — scrollable green code block
            if (!string.IsNullOrEmpty(step.ContentPreview))
            {
                string prefixed = string.Join("\n", step.ContentPreview
                    .Split('\n')
                    .Select(l => string.IsNullOrWhiteSpace(l) ? l : "+ " + l));

                var previewText = new TextBlock
                {
                    Text = prefixed,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0x40))
                };

                previewText.Margin = new Thickness(0, 0, 14, 0); // space for scrollbar
                var previewScroll = new ScrollViewer
                {
                    MaxHeight = 180,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = previewText
                };
                previewScroll.PreviewMouseWheel += PreviewScroll_Bubble;

                var previewBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x30, 0xC0, 0x30)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 0, 6),
                    Margin = new Thickness(0, 6, 0, 0),
                    Child = previewScroll
                };
                contentPanel.Children.Add(previewBorder);
            }

            Grid.SetColumn(contentPanel, 1);
            stepGrid.Children.Add(contentPanel);
            card.Child = stepGrid;
            stepsPanel.Children.Add(card);
        }

        var mainScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.PrimaryScreenHeight * 0.7,
            Margin = new Thickness(0, 0, -14, 0),
            Padding = new Thickness(0, 0, 14, 0),
            Content = scrollContent
        };

        box.Content = mainScroll;
        return box;
    }

    // ── Wiki update operations ──────────────────────────────────────

    private async Task<string> UpdateModuleAsync(ClueCollectionCase caseObj)
    {
        var moduleContent = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Various");
        if (string.IsNullOrEmpty(moduleContent))
            throw new Exception("Could not fetch Module:Datatable/Various.");

        // Detect group images from export folder
        var exportDir = ResolveExportDir();
        Dictionary<(int, int), int>? groupImages = null;
        if (exportDir != null && _service != null)
            groupImages = ClueCollectionService.DetectGroupImages(exportDir, new List<ClueCollectionCase> { caseObj });

        string entry = ClueCollectionService.GenerateModuleEntry(caseObj, groupImages);

        int sectionStart = moduleContent.IndexOf("p.clueCollections = {", StringComparison.Ordinal);
        if (sectionStart < 0) throw new Exception("p.clueCollections section not found.");

        // Check if case already exists — match case-level entry by looking for [N] = {name = "CaseName"
        // This is unique to case entries; set entries have [N] = {name = "...", reward = "..."
        int existingStart = -1;
        foreach (var nameVariant in new[] { caseObj.DisplayName, $"CASE {caseObj.Index}: {caseObj.DisplayName}" })
        {
            var casePattern = $"[{caseObj.Index}] = {{name = \"{nameVariant}\"";
            existingStart = moduleContent.IndexOf(casePattern, sectionStart, StringComparison.OrdinalIgnoreCase);
            if (existingStart >= 0) break;
        }

        string updated;
        string editSummary;

        if (existingStart >= 0)
        {
            // Patch existing entry — add missing reward fields without replacing names/structure
            updated = ClueCollectionService.PatchModuleEntryRewards(moduleContent, caseObj, groupImages);
            if (updated == moduleContent)
                return "All set rewards already present.";
            editSummary = $"Add set rewards to Clue Collection #{caseObj.Index}: {caseObj.DisplayName} (via MergeMansionWikiTools)";
        }
        else
        {
            // Insert new entry at the top of clueCollections
            int bracePos = moduleContent.IndexOf('{', sectionStart + 20);
            if (bracePos < 0) throw new Exception("Opening brace not found.");

            int insertPos = bracePos + 1;
            while (insertPos < moduleContent.Length && (moduleContent[insertPos] == '\n' || moduleContent[insertPos] == '\r'))
                insertPos++;

            updated = moduleContent[..insertPos] + "\n" + entry + moduleContent[insertPos..];
            editSummary = $"Add Clue Collection #{caseObj.Index}: {caseObj.DisplayName} (via MergeMansionWikiTools)";
        }

        return await MysteryWikiService.PublishPageAsync(
            _main.Settings.WikiUsername, _main.Settings.WikiPassword,
            "Module:Datatable/Various", updated, editSummary);
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

        // Add/update Set Rewards tabber — dynamic Lua invoke that renders all cases as tabber
        string rewardsTabber = "{{#Invoke:Various|GetClueCollectionRewardsTabber}}";
        if (!updated.Contains("GetClueCollectionRewardsTabber", StringComparison.OrdinalIgnoreCase))
        {
            // Replace "WIP Reward Table" line or insert before History
            int wipLine = updated.IndexOf("WIP Reward Table", StringComparison.OrdinalIgnoreCase);
            if (wipLine >= 0)
            {
                int wipLineStart = updated.LastIndexOf('\n', wipLine);
                int wipLineEnd = updated.IndexOf('\n', wipLine);
                if (wipLineStart >= 0 && wipLineEnd >= 0)
                    updated = updated[..(wipLineStart + 1)] + rewardsTabber + "\n" + updated[(wipLineEnd + 1)..];
            }
            else
            {
                int rewardsEnd = updated.IndexOf("== History ==", StringComparison.Ordinal);
                if (rewardsEnd > 0)
                    updated = updated[..rewardsEnd] + rewardsTabber + "\n\n" + updated[rewardsEnd..];
            }
        }

        return await MysteryWikiService.PublishPageAsync(
            _main.Settings.WikiUsername, _main.Settings.WikiPassword,
            "Clue Collection", updated,
            $"Add Clue Collection #{caseObj.Index}: History row + set rewards (via MergeMansionWikiTools)");
    }

    /// <summary>
    /// Extracts the History wikitable (`{| ... |}`) under the "== History ==" heading.
    /// Returns null if the heading or table delimiters are missing.
    /// </summary>
    private static string? ExtractHistoryTable(string pageContent)
    {
        int historyIdx = pageContent.IndexOf("== History ==", StringComparison.Ordinal);
        if (historyIdx < 0) return null;
        int tableStart = pageContent.IndexOf("{|", historyIdx, StringComparison.Ordinal);
        if (tableStart < 0) return null;
        int tableEnd = pageContent.IndexOf("|}", tableStart, StringComparison.Ordinal);
        if (tableEnd < 0) return null;
        return pageContent[tableStart..(tableEnd + 2)];
    }

    /// <summary>Normalizes a wikitable for equality comparison: trims each line and drops blanks,
    /// so cosmetic whitespace/line-ending differences don't read as a content change.</summary>
    private static string NormalizeTable(string table) =>
        string.Join("\n", table.Replace("\r", "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0));

    private async Task<string> MergeHistoryTableAsync()
    {
        if (_service == null) return "No data loaded.";

        var pageContent = await MysteryWikiService.FetchPageContentAsync("Clue Collection");
        if (string.IsNullOrEmpty(pageContent))
            throw new Exception("Could not fetch Clue Collection page.");

        int historyIdx = pageContent.IndexOf("== History ==", StringComparison.Ordinal);
        if (historyIdx < 0) throw new Exception("History section not found.");

        // Find the table: from {| to |}
        int tableStart = pageContent.IndexOf("{|", historyIdx);
        int tableEnd = pageContent.IndexOf("|}", tableStart);
        if (tableStart < 0 || tableEnd < 0) throw new Exception("History table not found.");
        tableEnd += 2; // include |}

        // Generate merged table
        string mergedTable = ClueCollectionService.GenerateMergedHistoryTable(_service.Cases);

        // Replace the table
        string updated = pageContent[..tableStart] + mergedTable.TrimEnd() + "\n" + pageContent[tableEnd..];

        return await MysteryWikiService.PublishPageAsync(
            _main.Settings.WikiUsername, _main.Settings.WikiPassword,
            "Clue Collection", updated,
            "Refactor History table: merge identical rows with rowspan (via MergeMansionWikiTools)");
    }

    // ── Image upload ────────────────────────────────────────────────

    private async Task<string> UploadImagesAsync(ClueCollectionCase caseObj)
    {
        var exportDir = ResolveExportDir();
        if (string.IsNullOrEmpty(exportDir)) return "No export directory.";

        var images = ClueCollectionService.FindExistingImages(exportDir, caseObj);
        if (images.Count == 0) return "No images found.";

        // Pre-check: how many already exist on wiki?
        ShowInfo("Checking wiki for existing images...", InfoBarSeverity.Informational);
        var preCheckNames = images.Select(i => "File:" + i.FileName).ToList();
        var preCheckMap = new Dictionary<string, bool>();
        for (int b = 0; b < preCheckNames.Count; b += 50)
        {
            var br = await MysteryWikiService.CheckPagesExistAsync(preCheckNames.Skip(b).Take(50).ToList());
            foreach (var kv in br) preCheckMap[kv.Key] = kv.Value;
        }
        int alreadyOnWiki = images.Count(i => preCheckMap.GetValueOrDefault("File:" + i.FileName));
        if (alreadyOnWiki == images.Count)
            return $"All {images.Count} images already on wiki.";

        int newCount = images.Count - alreadyOnWiki;
        ShowInfo($"{newCount} new images to upload ({alreadyOnWiki} already on wiki).", InfoBarSeverity.Informational);

        // Resolve Processed Images directory — never modify originals
        var basePath = _main.Settings.ImageExporterBasePath;
        string? processedDir = !string.IsNullOrEmpty(basePath)
            ? Path.Combine(basePath, "Processed Images") : null;
        if (processedDir != null) Directory.CreateDirectory(processedDir);

        string? apiKey = _main.Settings.TinifyApiKey;
        string? apiKey2 = _main.Settings.TinifyApiKey2;
        bool canOptimize = !string.IsNullOrWhiteSpace(apiKey);

        // Show TinyPNG usage bar
        if (canOptimize)
        {
            tinifyUsage.Visibility = Visibility.Visible;
            tinifyUsage.Initialize(apiKey!, apiKey2);
        }

        var ownerWindow = Window.GetWindow(this);
        int total = images.Count;
        pnlProcessing.Visibility = Visibility.Visible;

        // Build list of (wikiFilename, localPath) — copy to Processed Images first
        var fileList = new List<(string WikiName, string LocalPath, bool AlreadyOptimized)>();
        for (int idx = 0; idx < images.Count; idx++)
        {
            var (fileName, fullPath, _, _) = images[idx];
            string targetPath = processedDir != null ? Path.Combine(processedDir, fileName) : fullPath;

            // Copy to Processed Images if not there yet
            if (processedDir != null && !File.Exists(targetPath))
                File.Copy(fullPath, targetPath);

            bool isOpt = File.Exists(targetPath) && OptimizationWindow.HasOptMarker(await File.ReadAllBytesAsync(targetPath));
            fileList.Add((fileName, targetPath, isOpt));
        }

        // ── Phase 1: Optimize unoptimized files (OptimizationWindow) ──
        var unoptimized = fileList.Where(f => !f.AlreadyOptimized).Select(f => f.LocalPath).ToList();
        if (unoptimized.Count > 0 && canOptimize)
        {
            UpdateProcessingStatus("Opening optimization dialog", 0, total, $"{unoptimized.Count} files to optimize", fileList[0].LocalPath);

            var optWin = new OptimizationWindow(
                fileList.Select(f => f.LocalPath).ToList(),
                apiKey!, apiKey2 ?? "",
                new HashSet<string>(unoptimized, StringComparer.OrdinalIgnoreCase))
            {
                Owner = ownerWindow
            };
            EnsureWindowVisible(ownerWindow);
            optWin.ShowDialog();

            // Re-check optimization status after dialog
            for (int i = 0; i < fileList.Count; i++)
            {
                var f = fileList[i];
                bool isOpt = File.Exists(f.LocalPath) && OptimizationWindow.HasOptMarker(await File.ReadAllBytesAsync(f.LocalPath));
                fileList[i] = (f.WikiName, f.LocalPath, isOpt);
            }
        }

        // Check if there are still unoptimized files
        int stillUnoptimized = fileList.Count(f => !f.AlreadyOptimized);
        // Re-read after optimization window
        for (int i = 0; i < fileList.Count; i++)
        {
            var f = fileList[i];
            if (!f.AlreadyOptimized)
            {
                bool isOpt = File.Exists(f.LocalPath) && OptimizationWindow.HasOptMarker(await File.ReadAllBytesAsync(f.LocalPath));
                fileList[i] = (f.WikiName, f.LocalPath, isOpt);
            }
        }

        // Update processing status bar after optimization
        UpdateProcessingStatus("Preparing upload", 0, fileList.Count, "Checking wiki...", fileList[0].LocalPath);
        // Refresh TinyPNG bar after optimization phase
        if (canOptimize) tinifyUsage.Initialize(apiKey!, apiKey2);

        // ── Phase 2: Batch check wiki existence ──
        var existMap = new Dictionary<string, bool>();
        var wikiNames = fileList.Select(f => "File:" + f.WikiName).ToList();
        for (int batch = 0; batch < wikiNames.Count; batch += 50)
        {
            var batchResult = await MysteryWikiService.CheckPagesExistAsync(wikiNames.Skip(batch).Take(50).ToList());
            foreach (var kv in batchResult) existMap[kv.Key] = kv.Value;
        }

        // ── Phase 3: Upload with per-file UploadConflictDialog ──
        using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
            _main.Settings.WikiUsername, _main.Settings.WikiPassword);
        var csrfJson = await client.GetStringAsync(
            "https://merge-mansion.fandom.com/api.php?action=query&meta=tokens&format=json");
        var csrfToken = System.Text.Json.JsonDocument.Parse(csrfJson).RootElement
            .GetProperty("query").GetProperty("tokens")
            .GetProperty("csrftoken").GetString()!;

        int uploaded = 0, skipped = 0, errors = 0;
        bool forceAll = false;  // overwrite existing on wiki
        bool uploadAll = false; // upload all new without asking
        bool skipAll = false, cancelled = false;
        int tenPercentStep = Math.Max(1, fileList.Count / 10);

        // Dialog phase: ask user per-file until they choose a bulk action
        int dialogIdx = 0;
        for (; dialogIdx < fileList.Count && !cancelled && !uploadAll && !forceAll; dialogIdx++)
        {
            var (wikiName, localPath, _) = fileList[dialogIdx];
            int remaining = fileList.Count - dialogIdx - 1;
            bool existsOnWiki = existMap.GetValueOrDefault("File:" + wikiName);

            UpdateProcessingStatus("Uploading", dialogIdx + 1, fileList.Count, wikiName, localPath);

            if (existsOnWiki)
            {
                if (skipAll) { skipped++; continue; }
                var dlg = new UploadConflictDialog(wikiName, remaining, localPath,
                    isPartOfSplit: false, currentUser: _main.Settings.WikiUsername)
                { Owner = ownerWindow };
                EnsureWindowVisible(ownerWindow);
                dlg.ShowDialog();
                switch (dlg.Choice)
                {
                    case UploadConflictChoice.Force: break;
                    case UploadConflictChoice.ForceAll: forceAll = true; break;
                    case UploadConflictChoice.Skip: skipped++; continue;
                    case UploadConflictChoice.SkipAll: skipAll = true; skipped++; continue;
                    case UploadConflictChoice.Cancel: cancelled = true; continue;
                    default: skipped++; continue;
                }
            }
            else
            {
                var dlg = UploadConflictDialog.CreateNewFileDialog(wikiName, remaining, localPath,
                    _main.Settings.WikiUsername);
                dlg.Owner = ownerWindow;
                EnsureWindowVisible(ownerWindow);
                dlg.ShowDialog();
                switch (dlg.Choice)
                {
                    case UploadConflictChoice.Force: break;
                    case UploadConflictChoice.ForceAll: uploadAll = true; break;
                    case UploadConflictChoice.Skip: skipped++; continue;
                    case UploadConflictChoice.SkipAll: skipAll = true; skipped++; continue;
                    case UploadConflictChoice.Cancel: cancelled = true; continue;
                    default: break;
                }
            }

            // Upload this individual file (user confirmed)
            if (!forceAll && !uploadAll)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(localPath);
                    await UploadWithRetryAsync(client, csrfToken, wikiName, bytes);
                    Interlocked.Increment(ref uploaded);
                }
                catch (Exception ex) { Interlocked.Increment(ref errors); AppLogger.Warn($"[ClueUpload] {wikiName}: {ex.Message}"); }
            }
        }

        // Parallel bulk phase: upload remaining files (after Upload All or Force All)
        if ((uploadAll || forceAll) && !cancelled)
        {
            var remainingFiles = fileList.Skip(dialogIdx).ToList();
            // Include the file that triggered the bulk action (dialogIdx was incremented but file wasn't uploaded)
            if (dialogIdx > 0 && dialogIdx <= fileList.Count)
            {
                var triggerFile = fileList[dialogIdx - 1];
                // Check if it was already uploaded in the dialog phase
                bool alreadyUploaded = !uploadAll && !forceAll; // it wasn't — we broke out of loop
                if (!alreadyUploaded)
                    remainingFiles.Insert(0, triggerFile);
            }

            if (remainingFiles.Count > 0)
            {
                var semaphore = new SemaphoreSlim(2); // 2 concurrent to avoid rate limiting
                int processedInBulk = 0;

                var tasks = remainingFiles.Select(async file =>
                {
                    bool existsOnWiki = existMap.GetValueOrDefault("File:" + file.WikiName);

                    if (existsOnWiki && !forceAll)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }
                    if (existsOnWiki && skipAll)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    await semaphore.WaitAsync();
                    try
                    {
                        var bytes = await File.ReadAllBytesAsync(file.LocalPath);
                        await UploadWithRetryAsync(client, csrfToken, file.WikiName, bytes);
                        int done = Interlocked.Increment(ref uploaded);
                        int idx = Interlocked.Increment(ref processedInBulk);
                        Dispatcher.InvokeAsync(() =>
                            UpdateProcessingStatus("Uploading (parallel)", dialogIdx + idx, fileList.Count, file.WikiName, file.LocalPath));
                        if (canOptimize && done % tenPercentStep == 0)
                            Dispatcher.InvokeAsync(() => tinifyUsage.Initialize(apiKey!, apiKey2));
                    }
                    catch (Exception ex) { Interlocked.Increment(ref errors); AppLogger.Warn($"[ClueUpload] {file.WikiName}: {ex.Message}"); }
                    finally { semaphore.Release(); }
                });
                await Task.WhenAll(tasks);
            }
        }

        pnlProcessing.Visibility = Visibility.Collapsed;
        if (canOptimize) tinifyUsage.Initialize(apiKey!, apiKey2);

        string result = $"{uploaded} uploaded";
        if (skipped > 0) result += $", {skipped} skipped";
        if (errors > 0) result += $", {errors} errors (see log)";
        if (cancelled) result += " (cancelled)";
        return result;
    }

    private void UpdateProcessingStatus(string phase, int current, int total, string fileName, string filePath)
    {
        txtProcessingStatus.Text = $"{phase}... {current}/{total}";
        txtProcessingFile.Text = fileName;
        pbProcessing.Value = total > 0 ? (double)current / total * 100 : 0;

        // ThumbnailCache vrací null při chybě — odpovídá původnímu catch → Source = null
        imgProcessingThumb.Source = ThumbnailCache.Get(filePath, 48);
    }

    private string? ResolveExportDir()
    {
        if (!string.IsNullOrEmpty(_overrideExportDir)) return _overrideExportDir;
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version)) return null;
        var dir = Path.Combine(basePath, version, "Export - PNGs");
        return Directory.Exists(dir) ? dir : null;
    }

    // ── Ad-hoc resolution check ────────────────────────────────────

    private async void BtnUploadImages_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not ClueCollectionCase caseObj) return;
        if (!_main.Settings.WikiVerified)
        {
            ShowInfo("Wiki account not verified.", InfoBarSeverity.Warning);
            return;
        }
        btn.IsEnabled = false;
        try
        {
            string result = await UploadImagesAsync(caseObj);
            ShowInfo("Images: " + result, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo("Upload failed: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private async void BtnCheckCaseSizes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not ClueCollectionCase caseObj) return;
        btn.IsEnabled = false;
        try
        {
            await CheckCaseImageSizesAsync(new List<ClueCollectionCase> { caseObj }, $"Case {caseObj.Index}");
        }
        finally { btn.IsEnabled = true; }
    }

    private async Task CheckCaseImageSizesAsync(List<ClueCollectionCase> casesToCheck, string label)
    {
        if (_service == null) return;
        ShowInfo($"Checking wiki image sizes for {label}...", InfoBarSeverity.Informational);

        try
        {
            var allFiles = new List<string>();
            foreach (var c in casesToCheck)
                allFiles.AddRange(ClueCollectionService.GetExpectedImageFiles(c));

            // Batch query wiki API for image dimensions (50 per request)
            int wrongCount = 0;
            int checkedCount = 0;
            var wrongFiles = new List<(string File, int Width, int Height)>();

            for (int i = 0; i < allFiles.Count; i += 50)
            {
                var batch = allFiles.Skip(i).Take(50).Select(f => "File:" + f);
                string titles = string.Join("|", batch);
                string url = $"https://merge-mansion.fandom.com/api.php?action=query&titles={Uri.EscapeDataString(titles)}&prop=imageinfo&iiprop=size&format=json";

                // Shared client (has User-Agent — required for Fandom) instead of per-batch new HttpClient
                var json = await WikiMappingService.SharedHttp.GetStringAsync(url);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var pages = doc.RootElement.GetProperty("query").GetProperty("pages");

                foreach (var page in pages.EnumerateObject())
                {
                    if (page.Value.TryGetProperty("missing", out _)) continue;
                    if (!page.Value.TryGetProperty("imageinfo", out var iiArr)) continue;
                    var ii = iiArr[0];
                    int w = ii.GetProperty("width").GetInt32();
                    int h = ii.GetProperty("height").GetInt32();
                    string title = page.Value.GetProperty("title").GetString() ?? "";
                    checkedCount++;
                    if (w != 424 || h != 512)
                    {
                        wrongCount++;
                        wrongFiles.Add((title.Replace("File:", ""), w, h));
                    }
                }

                ShowInfo($"Checking sizes... {Math.Min(i + 50, allFiles.Count)}/{allFiles.Count}", InfoBarSeverity.Informational);
                await Task.Delay(200); // rate limit
            }

            if (wrongCount == 0)
            {
                ShowInfo($"All {checkedCount} wiki images have correct resolution (424\u00d7512).", InfoBarSeverity.Success);
            }
            else
            {
                string details = string.Join("\n", wrongFiles.Take(20).Select(f => $"  {f.File}: {f.Width}\u00d7{f.Height}"));
                if (wrongFiles.Count > 20) details += $"\n  ... and {wrongFiles.Count - 20} more";
                ShowInfo($"{wrongCount}/{checkedCount} images have wrong resolution. Use image folder to re-upload correct versions.", InfoBarSeverity.Warning);

                // Offer re-upload of wrong-sized images from local Export - PNGs
                var exportDir = ResolveExportDir();
                if (exportDir != null && _main.Settings.WikiVerified)
                {
                    var localFiles = wrongFiles
                        .Select(f => (f.File, Path.Combine(exportDir, f.File)))
                        .Where(f => File.Exists(f.Item2))
                        .ToList();

                    if (localFiles.Count > 0)
                    {
                        var result = System.Windows.MessageBox.Show(
                            $"{localFiles.Count} of {wrongCount} wrong-sized images found locally in Export - PNGs.\n\nRe-upload them to wiki with correct resolution?",
                            "Re-upload wrong-sized images",
                            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

                        if (result == System.Windows.MessageBoxResult.Yes)
                        {
                            ShowInfo($"Re-uploading {localFiles.Count} images...", InfoBarSeverity.Informational);
                            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                                _main.Settings.WikiUsername, _main.Settings.WikiPassword);
                            var csrfJson = await client.GetStringAsync(
                                "https://merge-mansion.fandom.com/api.php?action=query&meta=tokens&format=json");
                            var csrfToken = System.Text.Json.JsonDocument.Parse(csrfJson).RootElement
                                .GetProperty("query").GetProperty("tokens")
                                .GetProperty("csrftoken").GetString()!;

                            int reUploaded = 0;
                            foreach (var (fileName, localPath) in localFiles)
                            {
                                try
                                {
                                    var bytes = await File.ReadAllBytesAsync(localPath);
                                    await WikiMappingService.UploadFileAsync(client, csrfToken, fileName, bytes, "{{Permission}}");
                                    reUploaded++;
                                    ShowInfo($"Re-uploading... {reUploaded}/{localFiles.Count} ({fileName})", InfoBarSeverity.Informational);
                                    await Task.Delay(500);
                                }
                                catch { }
                            }
                            ShowInfo($"Re-uploaded {reUploaded}/{localFiles.Count} images.", InfoBarSeverity.Success);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowInfo("Check failed: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>Extracts the Lua case block for a specific case from Module:Datatable/Various content.</summary>
    private static string? ExtractCaseBlock(string moduleContent, ClueCollectionCase caseObj)
    {
        int sectionStart = moduleContent.IndexOf("p.clueCollections", StringComparison.Ordinal);
        if (sectionStart < 0) { AppLogger.Warn("[ExtractCaseBlock] p.clueCollections not found"); return null; }

        // Find [N] that is a top-level case entry (has "= {name =" on the same line)
        int searchFrom = sectionStart;
        int caseStart = -1;
        string marker = $"[{caseObj.Index}]";
        while (true)
        {
            int idx = moduleContent.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;
            // Check this isn't a commented-out line
            int lineStart = moduleContent.LastIndexOf('\n', idx);
            string linePrefix = lineStart >= 0 ? moduleContent[(lineStart + 1)..idx].TrimStart() : "";
            if (linePrefix.StartsWith("--")) { searchFrom = idx + 1; continue; }
            // Check this line has "= {name =" — it's a top-level case entry, not a nested set
            int lineEnd = moduleContent.IndexOf('\n', idx);
            if (lineEnd < 0) lineEnd = moduleContent.Length;
            string line = moduleContent[idx..lineEnd];
            if (!line.Contains("= {name =", StringComparison.Ordinal)) { searchFrom = idx + 1; continue; }
            // Verify it contains the case display name (wiki may have "CASE N: " prefix)
            if (!line.Contains(caseObj.DisplayName, StringComparison.OrdinalIgnoreCase)
                && !line.Contains($"CASE {caseObj.Index}: {caseObj.DisplayName}", StringComparison.OrdinalIgnoreCase))
            { searchFrom = idx + 1; continue; }
            caseStart = idx;
            break;
        }
        if (caseStart < 0) { AppLogger.Warn($"[ExtractCaseBlock] Case {caseObj.Index} '{caseObj.DisplayName}' not found in clueCollections"); return null; }

        // Brace-match to find end of case block
        int openBrace = moduleContent.IndexOf('{', caseStart);
        if (openBrace < 0) return null;
        int depth = 1;
        int pos = openBrace + 1;
        while (pos < moduleContent.Length && depth > 0)
        {
            if (moduleContent[pos] == '{') depth++;
            else if (moduleContent[pos] == '}') depth--;
            pos++;
        }
        return moduleContent[caseStart..pos];
    }

    /// <summary>Bubbles mouse wheel to parent ScrollViewer when inner one can't scroll further.</summary>
    private static void PreviewScroll_Bubble(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        bool atTop = sv.VerticalOffset <= 0 && e.Delta > 0;
        bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;
        if (atTop || atBottom)
        {
            e.Handled = true;
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(sv);
            while (parent != null && parent is not ScrollViewer)
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            if (parent is ScrollViewer parentSv)
                parentSv.ScrollToVerticalOffset(parentSv.VerticalOffset - e.Delta);
        }
    }

    /// <summary>Shared rate limit gate — when one upload gets rate-limited, ALL uploads pause.</summary>
    private static readonly SemaphoreSlim _rateLimitGate = new(1, 1);
    private static volatile bool _isRateLimited;

    /// <summary>Uploads a file with retry on rate limiting (HTTP 429 / "ratelimited" error). Max 10 retries, 10s delay.</summary>
    private static async Task UploadWithRetryAsync(System.Net.Http.HttpClient client, string csrfToken, string fileName, byte[] bytes)
    {
        const int maxRetries = 10;
        const int retryDelayMs = 10_000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            // Wait if another upload triggered rate limit pause
            if (_isRateLimited)
            {
                AppLogger.Info($"[ClueUpload] {fileName}: waiting for rate limit cooldown...");
                await _rateLimitGate.WaitAsync();
                _rateLimitGate.Release();
            }

            try
            {
                await WikiMappingService.UploadFileAsync(client, csrfToken, fileName, bytes, "{{Permission}}");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && IsRateLimitError(ex))
            {
                AppLogger.Warn($"[ClueUpload] {fileName}: rate limited (attempt {attempt}/{maxRetries}), pausing ALL uploads for {retryDelayMs / 1000}s...");

                // Acquire gate — blocks all other uploads from proceeding
                if (!_isRateLimited)
                {
                    _isRateLimited = true;
                    await _rateLimitGate.WaitAsync();
                    await Task.Delay(retryDelayMs);
                    _isRateLimited = false;
                    _rateLimitGate.Release();
                }
                else
                {
                    // Another thread already holds the gate — just wait for it
                    await _rateLimitGate.WaitAsync();
                    _rateLimitGate.Release();
                }
            }
        }
    }

    private static bool IsRateLimitError(Exception ex)
        => ex.Message.Contains("ratelimited", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("429", StringComparison.Ordinal)
        || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);

    // ── Helpers ──────────────────────────────────────────────────────

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }

    private static void EnsureWindowVisible(Window? window)
    {
        if (window == null) return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}

/// <summary>
/// Lehký view-model jedné case karty pro virtualizovaný lstCases (DataTemplate v XAML).
/// Vlastnosti jsou immutable — per-card update probíhá plným rebuildem seznamu
/// (BuildCaseList → Clear + nové instance), stejně jako dřívější ruční rebuild pnlCases.
/// </summary>
public sealed class ClueCaseCardItem
{
    public ClueCaseCardItem(ClueCollectionCase caseObj)
    {
        Case = caseObj;
        Title = $"Case {caseObj.Index}: {caseObj.DisplayName}";
        int totalClues = caseObj.Sets.Sum(s => s.CardCount);
        Meta = $"{caseObj.Sets.Count} sets  ·  {totalClues} clues";
        NewBadgeVisibility = caseObj.ExistsOnWiki ? Visibility.Collapsed : Visibility.Visible;
        OnWikiVisibility = caseObj.ExistsOnWiki ? Visibility.Visible : Visibility.Collapsed;
        UpdateWikiAppearance = caseObj.ExistsOnWiki ? ControlAppearance.Secondary : ControlAppearance.Primary;
        UpdateWikiFontSize = caseObj.ExistsOnWiki ? 11d : 14d;
    }

    /// <summary>Podkladová case — v template bindovaná na Tag tlačítek (handlery čtou btn.Tag).</summary>
    public ClueCollectionCase Case { get; }

    public string Title { get; }
    public string Meta { get; }
    public Visibility NewBadgeVisibility { get; }
    public Visibility OnWikiVisibility { get; }
    public ControlAppearance UpdateWikiAppearance { get; }
    public double UpdateWikiFontSize { get; }
}
