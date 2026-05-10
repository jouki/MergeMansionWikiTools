using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WpfMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public partial class WikiDataParserPage : UserControl
{
    private readonly MainWindow _main;
    private readonly LuaGeneratorService _luaGen = new();

    private List<(string Label, string Lua)> _lastChunks = new();
    private string? _lastCombined;

    // Items chunking state
    private List<(string Label, string Lua)> _lastItemChunks = new();
    private string? _lastChainNamesBlock;
    private int _firstEventChunkIndex; // 1-based; 0 = no event separation

    // Archive (Module:Datatable/Items/Archive) state — populated during BtnUpdateItemsWiki_Click pre-flight.
    // Captures raw wiki Lua entries for items currently live on wiki (so removed items can be archived
    // with their last-known full data preserved).
    private Dictionary<string, string>? _lastWikiItemEntries;
    private Dictionary<string, string>? _lastLocalItemEntries;
    private ArchiveDiff? _lastArchiveDiff;
    private List<LuaGeneratorService.FlatItem>? _lastFlatItems; // saved at chunk-gen for chainNames regeneration with archive flags
    private HashSet<string>? _lastBrokenChainIds; // ids whose live chainName starts with "#missing#" — shadowed by archive
    private string? _lastMappingPatchedContent; // patched Module:Datatable/Items/Mapping content, ready to post
    private int _lastMappingEnrichedCount; // how many entries got enriched (for dialog display)
    private HashSet<string>? _lastMappingHandledIds; // broken-chain ids that have a non-#missing# mapping override

    private const long MaxWikiBytes = 2 * 1024 * 1024; // 2 MB

    // CreatedAt from JSON sources
    private string? _areasCreatedAt;
    private string? _itemsCreatedAt;

    // Changelog data (local vs wiki comparison)
    private sealed record ModifiedEntry(string Key, string WikiValue, string LocalValue);
    private sealed record RenamedEntry(string OldId, string NewId, string? OldChain, string? NewChain);
    /// Where: "archive" (full data preserved in Module:Datatable/Items/Archive) or
    ///        "mapping" (broken-chain handled via Module:Datatable/Items/Mapping override + enrichment).
    private sealed record ArchivedEntry(string Id, string Where, string? Chain);
    private sealed record ChangelogData(
        List<string> Added,
        List<string> Removed,
        List<ModifiedEntry> Modified,
        List<RenamedEntry>? Renamed = null)
    {
        public List<ArchivedEntry>? Archived { get; set; }
        public bool HasChanges() => Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0
                                    || (Renamed?.Count ?? 0) > 0
                                    || (Archived?.Count ?? 0) > 0;
    }
    private ChangelogData? _areasChangelog;
    private ChangelogData? _itemsChangelog;
    private Dictionary<string, double>? _areaOrdering; // from Module:Datatable/Areas/Mapping

    // Area count cache (avoids re-parsing on every navigation)
    private string? _cachedAreaPath;
    private int? _cachedAreaCount;

    // Track collapse state
    private bool _areasCollapsed;
    private bool _itemsCollapsed;

    // Cancellation for ongoing chunked text loads
    private CancellationTokenSource? _chunkLoadCts;
    private CancellationTokenSource? _combinedLoadCts;

    // Per-card references for lazy-loading and re-navigation reset
    private sealed record ChunkCardData(WpfTextBox TextBox, UIElement MiniLoading, StackPanel WarnPanel, WpfTextBlock WarnText);
    private List<ChunkCardData> _chunkCardData = new();
    private List<ChunkCardData> _itemChunkCardData = new();

    public WikiDataParserPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        RefreshStatus();
        UpdateWikiButtonState();
        UpdateItemsWikiButtonState();
        _main.WikiVerifiedChanged += OnWikiVerifiedChanged;
    }

    private void OnWikiVerifiedChanged()
    {
        UpdateWikiButtonState();
        UpdateItemsWikiButtonState();
    }

    // ── Status ──────────────────────────────────────────────────────

    public async void RefreshStatus()
    {
        var ds = _main.DataService;
        var chainPath = _main.Settings.ChainItemOddsPath;

        if (ds != null)
        {
            txtItemsStatus.Text = $"Items: {ds.Chains.Count} chains · {ds.ItemNames.Count} items";
            btnItemsPath.Content = chainPath;
            btnItemsPath.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrEmpty(chainPath))
        {
            txtItemsStatus.Text = $"Items: file not found — {Path.GetFileName(chainPath)}";
            btnItemsPath.Content = chainPath;
            btnItemsPath.Visibility = Visibility.Visible;
        }
        else
        {
            txtItemsStatus.Text = "Items: no file configured — set in Settings";
            btnItemsPath.Visibility = Visibility.Collapsed;
        }

        var areasPath = _main.Settings.AreasJsonPath;
        if (!string.IsNullOrEmpty(areasPath) && File.Exists(areasPath))
        {
            // Invalidate cache if path changed
            if (_cachedAreaPath != areasPath)
            {
                _cachedAreaPath = areasPath;
                _cachedAreaCount = null;
            }

            if (_cachedAreaCount == null)
            {
                txtAreasStatus.Text = "Areas: counting...";
                _cachedAreaCount = await Task.Run(() => TryCountAreas(areasPath));
            }

            txtAreasStatus.Text = _cachedAreaCount.HasValue
                ? $"Areas: {_cachedAreaCount.Value} areas"
                : $"Areas: {Path.GetFileName(areasPath)}";

            btnAreasPath.Content = areasPath;
            btnAreasPath.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrEmpty(areasPath))
        {
            txtAreasStatus.Text = $"Areas: file not found — {Path.GetFileName(areasPath)}";
            btnAreasPath.Content = areasPath;
            btnAreasPath.Visibility = Visibility.Visible;
        }
        else
        {
            txtAreasStatus.Text = "Areas: no file configured — set in Settings";
            btnAreasPath.Visibility = Visibility.Collapsed;
        }
    }

    private static int? TryCountAreas(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("Data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
                return data.GetArrayLength();
        }
        catch { }
        return null;
    }

    // ── Clickable paths → Settings ───────────────────────────────────

    private void BtnAreasPath_Click(object sender, RoutedEventArgs e)
    {
        _main.NavigateToSettingsHighlightAreas();
    }

    private void BtnItemsPath_Click(object sender, RoutedEventArgs e)
    {
        _main.NavigateToSettingsHighlightChainFile();
    }

    // ── Page lifecycle (called from MainWindow on navigation) ─────────

    /// <summary>
    /// Called BEFORE this page becomes visible on re-navigation.
    /// Cancels any in-flight text loads, resets TextBoxes to 150-line preview
    /// (fast layout) and shows loading spinners where needed.
    /// </summary>
    public void PrepareForShow()
    {
        // Cancel any ongoing chunked appends before resetting text
        _chunkLoadCts?.Cancel(); _chunkLoadCts?.Dispose();
        _chunkLoadCts = new CancellationTokenSource();
        _combinedLoadCts?.Cancel(); _combinedLoadCts?.Dispose();
        _combinedLoadCts = new CancellationTokenSource();

        // Area chunks
        for (int i = 0; i < _chunkCardData.Count && i < _lastChunks.Count; i++)
        {
            var (preview, remaining) = SplitForPreview(_lastChunks[i].Lua);
            _chunkCardData[i].TextBox.Text = preview;
            _chunkCardData[i].MiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;
        }

        // Items: single module
        if (!string.IsNullOrEmpty(_lastCombined))
        {
            var (preview, remaining) = SplitForPreview(_lastCombined);
            txtCombined.Text = preview;
            combinedMiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;
        }

        // Items: multi-chunk
        for (int i = 0; i < _itemChunkCardData.Count && i < _lastItemChunks.Count; i++)
        {
            var (preview, remaining) = SplitForPreview(_lastItemChunks[i].Lua);
            _itemChunkCardData[i].TextBox.Text = preview;
            _itemChunkCardData[i].MiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Called AFTER this page is set as ContentArea content.
    /// Refreshes status and fires chunked full-text loading for all cards.
    /// </summary>
    public void OnPageShown()
    {
        RefreshStatus();

        // Area chunks
        var chunkCt = _chunkLoadCts?.Token ?? CancellationToken.None;
        for (int i = 0; i < _chunkCardData.Count && i < _lastChunks.Count; i++)
        {
            var card = _chunkCardData[i];
            _ = LazySetChunkFullTextAsync(card.TextBox, _lastChunks[i].Lua,
                card.MiniLoading, card.WarnPanel, card.WarnText, _lastChunks[i].Label, chunkCt);
        }

        // Items: single module
        var combinedCt = _combinedLoadCts?.Token ?? CancellationToken.None;
        if (!string.IsNullOrEmpty(_lastCombined))
            _ = LazySetCombinedFullTextAsync(_lastCombined, combinedCt);

        // Items: multi-chunk
        for (int i = 0; i < _itemChunkCardData.Count && i < _lastItemChunks.Count; i++)
        {
            var card = _itemChunkCardData[i];
            _ = LazySetChunkFullTextAsync(card.TextBox, _lastItemChunks[i].Lua,
                card.MiniLoading, card.WarnPanel, card.WarnText, _lastItemChunks[i].Label, combinedCt);
        }
    }

    // ── Generate Areas ───────────────────────────────────────────────

    private async void BtnGenerateAreas_Click(object sender, RoutedEventArgs e)
    {
        var areasPath = _main.Settings.AreasJsonPath;
        if (string.IsNullOrEmpty(areasPath) || !File.Exists(areasPath))
        {
            ShowInfo("Areas file not configured or not found. Set it in Settings.", InfoBarSeverity.Error);
            return;
        }

        btnGenerateAreas.IsEnabled = false;
        ShowInfo("Loading areas...", InfoBarSeverity.Informational);

        try
        {
            var areasService = new AreasService();
            await areasService.LoadAsync(areasPath);
            _areasCreatedAt = areasService.CreatedAt;

            // Generate Lua on background thread (can be CPU-intensive for large area sets)
            _lastChunks = await Task.Run(() =>
            {
                using var _t = AppLogger.Timed("GenerateAreaChunks");
                return _luaGen.GenerateAreaChunks(areasService.Areas, _areasCreatedAt);
            });

            txtAreasHeader.Text = $"Areas — {areasService.Areas.Count} areas · {_lastChunks.Count} chunk(s)";

            // Cancel any ongoing chunk loads before building new cards
            _chunkLoadCts?.Cancel(); _chunkLoadCts?.Dispose();
            _chunkLoadCts = new CancellationTokenSource();

            BuildChunkCards(_lastChunks);

            areasSection.Visibility = Visibility.Visible;
            if (_areasCollapsed)
            {
                areasContent.Visibility = Visibility.Visible;
                iconCollapseAreas.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                _areasCollapsed = false;
            }

            _areasChangelog = null;
            _areaOrdering = null;
            UpdateWikiButtonState();
            _ = CheckAreasDateAsync();
            Increment(s => s.LuaAreaChunksGenerated += _lastChunks.Count);
            ShowInfo($"Areas generated — {_lastChunks.Count} chunk(s).", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnGenerateAreas.IsEnabled = true;
        }
    }

    // ── Update Wiki ─────────────────────────────────────────────────

    private void UpdateWikiButtonState()
    {
        btnUpdateWiki.IsEnabled = _main.Settings.WikiVerified && _lastChunks.Count > 0;
        iconAreasDateState.Visibility = Visibility.Collapsed;
        UpdateButtonTooltip(btnUpdateWiki, _lastChunks.Count > 0);
    }

    private async Task CheckAreasDateAsync()
    {
        if (string.IsNullOrEmpty(_areasCreatedAt) || _lastChunks.Count == 0) return;

        try
        {
            var content = await WikiMappingService.FetchModuleContentAsync("Module:Datatable/Areas/1");
            if (content == null) return;

            // Version check — block if wiki was uploaded by a newer MMWT version
            if (CheckWikiVersionNewer(content, btnUpdateWiki, iconAreasDateState))
                return;

            var wikiDate = WikiMappingService.ExtractCreatedAtFromContent(content);
            if (wikiDate != null)
            {
                var cmp = CompareDates(_areasCreatedAt, wikiDate);
                if (cmp < 0)
                    SetButtonOlderState(btnUpdateWiki, iconAreasDateState,
                        $"Local data ({_areasCreatedAt}) is older than wiki ({wikiDate})");
                else if (cmp == 0)
                    SetButtonSameDateState(btnUpdateWiki, iconAreasDateState,
                        $"Local data ({_areasCreatedAt}) has the same date as wiki");
            }
        }
        catch { }
    }

    private async Task CheckItemsDateAsync()
    {
        if (string.IsNullOrEmpty(_itemsCreatedAt)) return;

        try
        {
            var wikiContent = await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle);
            if (wikiContent == null) return;

            // Version check — block if wiki was uploaded by a newer MMWT version
            if (CheckWikiVersionNewer(wikiContent, btnUpdateItemsWiki, iconItemsDateState))
                return;

            var wikiDate = WikiMappingService.ExtractCreatedAtFromContent(wikiContent);
            if (wikiDate != null)
            {
                var cmp = CompareDates(_itemsCreatedAt, wikiDate);
                if (cmp < 0)
                    SetButtonOlderState(btnUpdateItemsWiki, iconItemsDateState,
                        $"Local data ({_itemsCreatedAt}) is older than wiki ({wikiDate})");
                else if (cmp == 0)
                    SetButtonSameDateState(btnUpdateItemsWiki, iconItemsDateState,
                        $"Local data ({_itemsCreatedAt}) has the same date as wiki");
            }
        }
        catch { }
    }

    /// <summary>
    /// Checks if the wiki module was uploaded by a newer MMWT version.
    /// Returns true (and sets button to error state) if the local version is older.
    /// If the wiki has no version tag, returns false (allows upload).
    /// </summary>
    private bool CheckWikiVersionNewer(string wikiContent,
        Wpf.Ui.Controls.Button btn, Wpf.Ui.Controls.SymbolIcon dateIcon)
    {
        var wikiVersion = WikiMappingService.ExtractMmwtVersionFromContent(wikiContent);
        if (wikiVersion == null) return false; // no version on wiki → allow

        try
        {
            var cmp = WikiMappingService.CompareVersions(Models.AppVersion.Version, wikiVersion);
            if (cmp < 0)
            {
                SetButtonOlderState(btn, dateIcon,
                    $"Wiki was updated by a newer MMWT version ({wikiVersion}), you have {Models.AppVersion.Version}");
                return true;
            }
        }
        catch { } // malformed version → allow

        return false;
    }

    /// <summary>
    /// Compares local and wiki dates (ISO 8601 lexicographic).
    /// Returns negative if local is older, 0 if equal, positive if local is newer.
    /// </summary>
    private static int CompareDates(string localDate, string wikiDate)
    {
        return string.Compare(localDate, wikiDate, StringComparison.OrdinalIgnoreCase);
    }

    private void SetButtonOlderState(Wpf.Ui.Controls.Button btn,
        Wpf.Ui.Controls.SymbolIcon dateIcon, string tooltip)
    {
        btn.IsEnabled = false;
        dateIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
        dateIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x14, 0x23));
        dateIcon.ToolTip = tooltip;
        dateIcon.Visibility = Visibility.Visible;
    }

    private void SetButtonSameDateState(Wpf.Ui.Controls.Button btn,
        Wpf.Ui.Controls.SymbolIcon dateIcon, string tooltip)
    {
        // Button stays enabled — just show warning icon
        dateIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
        dateIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25));
        dateIcon.ToolTip = tooltip;
        dateIcon.Visibility = Visibility.Visible;
    }

    private void UpdateButtonTooltip(Wpf.Ui.Controls.Button btn, bool hasData)
    {
        btn.ToolTip = hasData && !_main.Settings.WikiVerified
            ? "Wiki bot is not configured. Set up credentials in Settings."
            : null;
    }

    private async void BtnUpdateWiki_Click(object sender, RoutedEventArgs e)
    {
        using var _t = AppLogger.Timed("UpdateAreasWiki");
        if (!_main.Settings.WikiVerified)
        {
            ShowInfo("Wiki bot not verified. Configure credentials in Settings first.", InfoBarSeverity.Warning);
            return;
        }

        if (_lastChunks.Count == 0)
        {
            ShowInfo("No area chunks generated. Generate areas first.", InfoBarSeverity.Warning);
            return;
        }

        btnUpdateWiki.IsEnabled = false;

        try
        {
            // 1. Query existing modules on wiki
            ShowInfo("Querying existing area modules on wiki...", InfoBarSeverity.Informational);
            var existingIndices = await WikiMappingService.QueryExistingAreaModulesAsync();

            // 2. Compare local chunks vs wiki modules
            var localCount = _lastChunks.Count;
            var localIndices = Enumerable.Range(1, localCount).ToList();

            var toUpdate = localIndices.Where(i => existingIndices.Contains(i)).ToList();
            var toCreate = localIndices.Where(i => !existingIndices.Contains(i)).ToList();
            var toBlank = existingIndices.Where(i => i > localCount).ToList();

            // Fetch area ordering for changelog sorting
            if (_areaOrdering == null)
            {
                ShowInfo("Fetching area ordering...", InfoBarSeverity.Informational);
                _areaOrdering = await WikiMappingService.FetchAreaOrderingAsync();
            }

            // Compute changelog if not already done
            if (_areasChangelog == null && existingIndices.Count > 0)
            {
                ShowInfo("Comparing area data...", InfoBarSeverity.Informational);
                var wikiEntries = new Dictionary<string, string>();
                var fetchTasks = existingIndices.Select(async i =>
                {
                    var content = await WikiMappingService.FetchModuleContentAsync($"Module:Datatable/Areas/{i}");
                    return content != null
                        ? WikiMappingService.ExtractLuaAreaEntries(content, "areas")
                        : new Dictionary<string, string>();
                });
                foreach (var entries in await Task.WhenAll(fetchTasks))
                    foreach (var kv in entries) wikiEntries.TryAdd(kv.Key, kv.Value);

                var localEntries = new Dictionary<string, string>();
                foreach (var chunk in _lastChunks)
                    foreach (var kv in WikiMappingService.ExtractLuaAreaEntries(chunk.Lua, "areas"))
                        localEntries.TryAdd(kv.Key, kv.Value);

                _areasChangelog = ComputeAreasChangelog(wikiEntries, localEntries, _areaOrdering);
            }

            // 3. Build preview confirmation
            var totalActions = toUpdate.Count + toCreate.Count + toBlank.Count + 2; // +arbiter +modules page

            var previewBox = CreatePreviewDialog(
                "Update Areas Data on Wiki",
                BuildWikiUpdatePreview(toUpdate, toCreate, toBlank, _lastChunks.Count),
                "Update");

            if (await previewBox.ShowDialogAsync() != WpfMessageBoxResult.Primary)
            {
                btnUpdateWiki.IsEnabled = true;
                infoBar.IsOpen = false;
                return;
            }

            // 4. Authenticate
            ShowInfo("Authenticating with wiki...", InfoBarSeverity.Informational);
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                _main.Settings.WikiUsername, _main.Settings.WikiPassword);
            var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

            const string blankContent = "-- This module is no longer in use\nreturn {}";
            int done = 0;
            int created = 0, updated = 0, blanked = 0;

            // 5. Update/create data chunks
            for (int i = 0; i < localCount; i++)
            {
                var chunkIndex = i + 1;
                var title = $"Module:Datatable/Areas/{chunkIndex}";
                var isNew = toCreate.Contains(chunkIndex);
                var action = isNew ? "Create" : "Update";

                ShowInfo($"[{done + 1}/{totalActions}] {action} {title}...", InfoBarSeverity.Informational);

                var editResult = await WikiMappingService.EditModuleAsync(
                    client, csrfToken, title, _lastChunks[i].Lua,
                    $"{action} area data chunk {chunkIndex} (via MergeMansionWikiTools)");

                if (isNew) created++; else updated++;
                done++;
            }

            // 6. Blank excess modules
            foreach (var i in toBlank)
            {
                var title = $"Module:Datatable/Areas/{i}";
                ShowInfo($"[{done + 1}/{totalActions}] Blanking {title}...", InfoBarSeverity.Informational);

                await WikiMappingService.EditModuleAsync(
                    client, csrfToken, title, blankContent,
                    $"Blank unused area data chunk {i} (via MergeMansionWikiTools)");

                blanked++;
                done++;
            }

            // 7. Update arbiter module
            ShowInfo($"[{done + 1}/{totalActions}] Updating arbiter Module:Datatable/Areas...", InfoBarSeverity.Informational);
            var arbiterLua = WikiMappingService.GenerateAreasArbiterLua(localCount);
            await WikiMappingService.EditModuleAsync(
                client, csrfToken, "Module:Datatable/Areas", arbiterLua,
                $"Update area arbiter ({localCount} chunks) (via MergeMansionWikiTools)");
            done++;

            // 8. Update Modules page
            ShowInfo($"[{done + 1}/{totalActions}] Updating Modules page...", InfoBarSeverity.Informational);
            await WikiMappingService.UpdateAreasModulesPageAsync(client, csrfToken, localCount, _lastChunks);
            done++;

            // 9. Report success
            var parts = new List<string>();
            if (updated > 0) parts.Add($"{updated} updated");
            if (created > 0) parts.Add($"{created} created");
            if (blanked > 0) parts.Add($"{blanked} blanked");
            parts.Add("arbiter updated");
            parts.Add("Modules page updated");

            // 10. Check for areas missing from ordering mapping
            if (_areaOrdering != null)
            {
                var allLocalKeys = new HashSet<string>();
                foreach (var chunk in _lastChunks)
                    foreach (var kv in WikiMappingService.ExtractLuaAreaEntries(chunk.Lua, "areas"))
                        allLocalKeys.Add(kv.Key);

                var unmapped = allLocalKeys
                    .Where(k => !_areaOrdering.ContainsKey(k) && !AreaOrderingService.SkipNames.Contains(k))
                    .OrderBy(k => k)
                    .ToList();
                if (unmapped.Count > 0)
                {
                    ShowInfo($"Wiki updated — {string.Join(", ", parts)}.", InfoBarSeverity.Success);

                    // Load area unlock info and deduce ordering indices
                    var areasPath = _main.Settings.AreasJsonPath;
                    List<AreaUnlockInfo> allAreas;
                    try
                    {
                        allAreas = await AreaOrderingService.LoadFromAreasJsonAsync(areasPath);
                    }
                    catch (Exception ex)
                    {
                        ShowInfo($"Failed to load areas.json for ordering deduction: {ex.Message}", InfoBarSeverity.Error);
                        return;
                    }

                    var deduced = AreaOrderingService.Deduce(allAreas, _areaOrdering, unmapped);

                    // Fetch current module content to compute REMOVE diff (existing commented entries
                    // that will be cleared by the patch). We do this in the host page so the dialog
                    // can render the diff immediately on open.
                    var moduleContent = await WikiMappingService.FetchModuleContentAsync("Module:Datatable/Areas/Mapping");
                    var existingCommented = moduleContent != null
                        ? AreaOrderingService.ExtractCommentedEntries(moduleContent)
                        : new List<RemovedCommentedEntry>();

                    if (deduced.Count == 0 && existingCommented.Count == 0)
                    {
                        // Nothing to add and nothing to clear → silent return
                        return;
                    }

                    var dlg = new MissingOrderingDialog(
                        deduced,
                        existingCommented,
                        _main.Settings.WikiUsername,
                        _main.Settings.WikiPassword)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    dlg.ShowDialog();

                    // Re-fetch ordering after potential edit so subsequent runs see the new indices
                    _areaOrdering = await WikiMappingService.FetchAreaOrderingAsync();
                    return;
                }
            }

            ShowInfo($"Wiki updated — {string.Join(", ", parts)}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Wiki update failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnUpdateWiki.IsEnabled = _main.Settings.WikiVerified && _lastChunks.Count > 0;
            UpdateButtonTooltip(btnUpdateWiki, _lastChunks.Count > 0);
        }
    }

    private UIElement BuildWikiUpdatePreview(
        List<int> toUpdate, List<int> toCreate, List<int> toBlank, int chunkCount)
    {
        var root = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var primary = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var subtle = (Brush)FindResource("SubtleFillColorSecondaryBrush");

        // Fixed top section (pinned, won't scroll)
        var topSection = new StackPanel();
        DockPanel.SetDock(topSection, Dock.Top);

        topSection.Children.Add(new WpfTextBlock
        {
            Text = $"{toUpdate.Count + toCreate.Count + toBlank.Count + 2} module(s) will be edited",
            FontSize = 13, Foreground = secondary, Margin = new Thickness(0, 0, 0, 4)
        });

        topSection.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 4, 0, 10),
            Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
        });

        // Helper: add a step row to the top section
        void AddStep(string icon, string title, string? detail = null, string? detail2 = null, string? url = null)
        {
            var row = new Border
            {
                Background = subtle, CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconTb = new WpfTextBlock
            {
                Text = icon, FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetColumn(iconTb, 0);
            grid.Children.Add(iconTb);

            var content = new StackPanel();
            if (url != null)
            {
                var titleTb = new WpfTextBlock
                {
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = primary, TextWrapping = TextWrapping.Wrap
                };
                var linkRun = new System.Windows.Documents.Run(title)
                {
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                linkRun.MouseEnter += (s, e) => linkRun.TextDecorations = TextDecorations.Underline;
                linkRun.MouseLeave += (s, e) => linkRun.TextDecorations = null;
                var capturedUrl = url;
                linkRun.MouseLeftButtonDown += (s, e) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(capturedUrl) { UseShellExecute = true });
                };
                titleTb.Inlines.Add(linkRun);
                content.Children.Add(titleTb);
            }
            else
            {
                content.Children.Add(new WpfTextBlock
                {
                    Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = primary, TextWrapping = TextWrapping.Wrap
                });
            }
            if (detail != null)
                content.Children.Add(new WpfTextBlock
                {
                    Text = detail, FontSize = 11, Foreground = secondary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });
            if (detail2 != null)
                content.Children.Add(new WpfTextBlock
                {
                    Text = detail2, FontSize = 10, Foreground = tertiary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });

            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            row.Child = grid;
            topSection.Children.Add(row);
        }

        const string wikiBase = "https://merge-mansion.fandom.com/wiki/";

        foreach (var i in toUpdate)
        {
            var lines = _lastChunks[i - 1].Lua.Count(c => c == '\n') + 1;
            AddStep("\uD83D\uDCDD", $"Update Module:Datatable/Areas/{i}",
                $"{lines} lines", $"Overwrite existing data chunk",
                $"{wikiBase}Module:Datatable/Areas/{i}");
        }

        foreach (var i in toCreate)
        {
            var lines = _lastChunks[i - 1].Lua.Count(c => c == '\n') + 1;
            AddStep("\u2795", $"Create Module:Datatable/Areas/{i}",
                $"{lines} lines", "New data module",
                $"{wikiBase}Module:Datatable/Areas/{i}");
        }

        foreach (var i in toBlank)
        {
            AddStep("\uD83D\uDDD1", $"Blank Module:Datatable/Areas/{i}",
                "Module is no longer needed",
                "Will be replaced with empty return",
                $"{wikiBase}Module:Datatable/Areas/{i}");
        }

        AddStep("\uD83D\uDD17", "Update Module:Datatable/Areas",
            $"Arbiter \u2014 require() {chunkCount} chunk(s)",
            "Combines all data chunks into p.areas",
            $"{wikiBase}Module:Datatable/Areas");

        AddStep("\uD83D\uDCC4", "Update Modules page",
            $"Add/update {chunkCount} submodule link(s) with area ranges",
            "Keeps existing links",
            $"{wikiBase}Modules");

        root.Children.Add(topSection);

        // Changelog fills remaining space (ScrollViewer inside handles overflow)
        root.Children.Add(BuildChangelogElement(_areasChangelog, "area", primary, secondary, tertiary));

        return root;
    }

    // ── Update Items Wiki ────────────────────────────────────────────

    private const string ItemsModuleTitle = "Module:Datatable/Items";

    private void UpdateItemsWikiButtonState()
    {
        var hasData = _lastItemChunks.Count > 0;
        btnUpdateItemsWiki.IsEnabled = _main.Settings.WikiVerified && hasData;
        iconItemsDateState.Visibility = Visibility.Collapsed;
        UpdateButtonTooltip(btnUpdateItemsWiki, hasData);
    }

    private async void BtnUpdateItemsWiki_Click(object sender, RoutedEventArgs e)
    {
        using var _t = AppLogger.Timed("UpdateItemsWiki");
        AppLogger.Info($"[UpdateItems] click: chunks={_lastItemChunks.Count}, firstEventChunk={_firstEventChunkIndex}, createdAt={_itemsCreatedAt}, flatItems={_lastFlatItems?.Count ?? -1}");
        if (!_main.Settings.WikiVerified)
        {
            AppLogger.Warn("[UpdateItems] aborted: wiki bot not verified");
            ShowInfo("Wiki bot not verified. Configure credentials in Settings first.", InfoBarSeverity.Warning);
            return;
        }

        if (_lastItemChunks.Count == 0)
        {
            AppLogger.Warn("[UpdateItems] aborted: no chunks generated");
            ShowInfo("No items data generated. Generate items first.", InfoBarSeverity.Warning);
            return;
        }

        btnUpdateItemsWiki.IsEnabled = false;

        try
        {
            var isSingleModule = _lastItemChunks.Count == 1 && string.IsNullOrEmpty(_lastItemChunks[0].Label);

            if (isSingleModule)
            {
                // ── Single module path (no chunking) ──
                var singleLua = _lastItemChunks[0].Lua;

                ShowInfo("Checking wiki module...", InfoBarSeverity.Informational);
                var wikiContent = await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle);
                var exists = wikiContent != null;

                // Compute changelog
                if (_itemsChangelog == null && wikiContent != null)
                    _itemsChangelog = ComputeItemsChangelog(wikiContent, singleLua);

                var lineCount = singleLua.Count(c => c == '\n') + 1;
                var bytes = Encoding.UTF8.GetByteCount(singleLua);
                var sizeStr = FormatSize(bytes);

                var previewBox = CreatePreviewDialog(
                    "Update Items Data on Wiki",
                    BuildItemsUpdatePreviewSingle(exists, lineCount, sizeStr),
                    exists ? "Update" : "Create");

                if (await previewBox.ShowDialogAsync() != WpfMessageBoxResult.Primary)
                {
                    btnUpdateItemsWiki.IsEnabled = true;
                    infoBar.IsOpen = false;
                    return;
                }

                ShowInfo("Authenticating with wiki...", InfoBarSeverity.Informational);
                using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword);
                var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

                var action = exists ? "Update" : "Create";
                ShowInfo($"{action} {ItemsModuleTitle}...", InfoBarSeverity.Informational);

                await WikiMappingService.EditModuleAsync(
                    client, csrfToken, ItemsModuleTitle, singleLua,
                    $"{action} items + chainNames data (via MergeMansionWikiTools)");

                ShowInfo($"Wiki updated — {ItemsModuleTitle} ({lineCount} lines, {sizeStr}).", InfoBarSeverity.Success);
            }
            else
            {
                // ── Multi-chunk path ──
                ShowInfo("Querying existing item modules on wiki...", InfoBarSeverity.Informational);
                var existingIndices = await WikiMappingService.QueryExistingItemModulesAsync();

                var localCount = _lastItemChunks.Count;
                var localIndices = Enumerable.Range(1, localCount).ToList();

                var toUpdate = localIndices.Where(i => existingIndices.Contains(i)).ToList();
                var toCreate = localIndices.Where(i => !existingIndices.Contains(i)).ToList();
                var toBlank = existingIndices.Where(i => i > localCount).ToList();

                // Compute changelog (merge all wiki chunks vs all local chunks)
                if (_itemsChangelog == null && existingIndices.Count > 0)
                {
                    ShowInfo("Comparing item data...", InfoBarSeverity.Informational);
                    var wikiEntries = new Dictionary<string, string>();
                    var fetchTasks = existingIndices.Select(async i =>
                    {
                        var content = await WikiMappingService.FetchModuleContentAsync($"Module:Datatable/Items/{i}");
                        return content != null
                            ? WikiMappingService.ExtractLuaTableEntries(content, "items")
                            : new Dictionary<string, string>();
                    });
                    foreach (var entries in await Task.WhenAll(fetchTasks))
                        foreach (var kv in entries) wikiEntries.TryAdd(kv.Key, kv.Value);

                    // Also check arbiter module for items (if chunks didn't exist before)
                    if (wikiEntries.Count == 0)
                    {
                        var arbiterContent = await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle);
                        if (arbiterContent != null)
                            wikiEntries = WikiMappingService.ExtractLuaTableEntries(arbiterContent, "items");
                    }

                    var localEntries = new Dictionary<string, string>();
                    foreach (var chunk in _lastItemChunks)
                        foreach (var kv in WikiMappingService.ExtractLuaTableEntries(chunk.Lua, "items"))
                            localEntries.TryAdd(kv.Key, kv.Value);

                    _itemsChangelog = ComputeItemsChangelog(wikiEntries, localEntries);
                    _lastWikiItemEntries = wikiEntries;
                    _lastLocalItemEntries = localEntries;
                }
                else if (_itemsChangelog == null)
                {
                    // No existing chunks — check single module
                    var arbiterContent = await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle);
                    if (arbiterContent != null)
                    {
                        var localEntries = new Dictionary<string, string>();
                        foreach (var chunk in _lastItemChunks)
                            foreach (var kv in WikiMappingService.ExtractLuaTableEntries(chunk.Lua, "items"))
                                localEntries.TryAdd(kv.Key, kv.Value);

                        _itemsChangelog = ComputeItemsChangelog(arbiterContent, localEntries);
                        _lastWikiItemEntries = WikiMappingService.ExtractLuaTableEntries(arbiterContent, "items");
                        _lastLocalItemEntries = localEntries;
                    }
                }

                // Reset mapping state for this run
                _lastMappingPatchedContent = null;
                _lastMappingEnrichedCount = 0;
                _lastMappingHandledIds = new HashSet<string>(StringComparer.Ordinal);

                // Compute archive diff: existing Archive + newly-removed items + #missing#-chain shadows + restorations from live.
                // _lastWikiItemEntries[id] holds the raw Lua entry for items about to be removed/shadowed — that's
                // the canonical "last-known good" data we preserve in the archive.
                AppLogger.Debug($"[UpdateItems] _itemsChangelog: removed={_itemsChangelog?.Removed.Count}, added={_itemsChangelog?.Added.Count}, modified={_itemsChangelog?.Modified.Count}, renamed={_itemsChangelog?.Renamed?.Count ?? 0}");
                AppLogger.Debug($"[UpdateItems] _lastWikiItemEntries={_lastWikiItemEntries?.Count ?? -1}, _lastLocalItemEntries={_lastLocalItemEntries?.Count ?? -1}, _lastFlatItems={_lastFlatItems?.Count ?? -1}");
                if (_lastWikiItemEntries != null && _lastLocalItemEntries != null && _itemsChangelog != null)
                {
                    ShowInfo("Computing items archive diff...", InfoBarSeverity.Informational);
                    var existingArchiveContent = await WikiMappingService.FetchModuleContentAsync(ItemsArchiveService.ArchiveModuleTitle);
                    var existingArchive = ItemsArchiveService.ParseArchive(existingArchiveContent)
                        .ToDictionary(
                            kv => kv.Key,
                            kv => (IReadOnlyDictionary<string, string>)kv.Value,
                            StringComparer.Ordinal);

                    // Items still in local data but with broken chainName (`#missing#…` placeholder when game
                    // can't resolve a spreadsheet cell). Treat as archive shadows: they keep their old wiki entry
                    // (with proper chainName) so wiki pages keep rendering, but they're excluded from chainNames.
                    _lastBrokenChainIds = new HashSet<string>(StringComparer.Ordinal);
                    if (_lastFlatItems != null)
                    {
                        foreach (var f in _lastFlatItems)
                        {
                            if (!string.IsNullOrEmpty(f.ItemType) &&
                                !string.IsNullOrEmpty(f.ChainName) &&
                                f.ChainName.StartsWith("#missing#", StringComparison.Ordinal))
                                _lastBrokenChainIds.Add(f.ItemType);
                        }
                    }
                    AppLogger.Debug($"[UpdateItems] broken-chain ids ({_lastBrokenChainIds.Count}): {string.Join(", ", _lastBrokenChainIds.Take(10))}{(_lastBrokenChainIds.Count > 10 ? "..." : "")}");

                    // Archive source = Removed (in wiki, not in local) ∪ broken-chain (in local but unresolvable).
                    // ItemsArchiveService.Compute derives the bucket key from the item id (strip `_NN`) and
                    // overwrites the entry's chainName field — we don't need to fix anything here.
                    var archiveSourceRaw = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var id in _itemsChangelog.Removed)
                        if (_lastWikiItemEntries.TryGetValue(id, out var raw))
                            archiveSourceRaw[id] = raw;
                    foreach (var id in _lastBrokenChainIds)
                        if (_lastWikiItemEntries.TryGetValue(id, out var raw))
                            archiveSourceRaw[id] = raw;

                    // Fetch Module:Datatable/Items/Mapping. If a broken-chain item already has a non-#missing#
                    // chainName override there, the wiki page is already rendering correctly via mapping —
                    // skip from archive shadow. Plus enrich the mapping entry with any missing fields from
                    // the live items entry (without overwriting existing mapping fields).
                    ShowInfo("Fetching Module:Datatable/Items/Mapping...", InfoBarSeverity.Informational);
                    var mappingContent = await WikiMappingService.FetchModuleContentAsync(ItemsMappingService.MappingModuleTitle);
                    var mappingEntries = ItemsMappingService.ParseMappingModule(mappingContent);
                    var enrichedInners = new Dictionary<string, string>(StringComparer.Ordinal);
                    var brokenIdsHandledByMapping = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var id in _lastBrokenChainIds.ToList())
                    {
                        if (!mappingEntries.TryGetValue(id, out var mapEntry)) continue;
                        if (!mapEntry.Fields.TryGetValue("chainName", out var mapChain)) continue;
                        var chainStr = mapChain.Trim().Trim('"');
                        if (chainStr.StartsWith("#missing#", StringComparison.Ordinal)) continue;

                        // Mapping has a non-broken chainName override → wiki page works via mapping.
                        brokenIdsHandledByMapping.Add(id);

                        // Enrich mapping with missing fields from local items entry (skipping broken chainName).
                        if (!_lastLocalItemEntries.TryGetValue(id, out var localRaw)) continue;
                        // Strip outer braces from raw entry: "{key=val, ...}" → "key=val, ..."
                        var stripped = localRaw.Trim();
                        if (stripped.StartsWith("{") && stripped.EndsWith("}"))
                            stripped = stripped.Substring(1, stripped.Length - 2);
                        var (localFields, localOrder) = ItemsMappingService.ParseLuaFields(stripped);

                        bool changed = false;
                        foreach (var key in localOrder)
                        {
                            if (key == "chainName") continue; // mapping already has the corrected one
                            if (mapEntry.Fields.ContainsKey(key)) continue; // never overwrite existing
                            mapEntry.Fields[key] = localFields[key];
                            mapEntry.FieldOrder.Add(key);
                            changed = true;
                        }
                        if (changed)
                            enrichedInners[id] = mapEntry.EmitInnerLua();
                    }

                    AppLogger.Debug($"[UpdateItems] mapping: parsed {mappingEntries.Count} entries, enriched {enrichedInners.Count}, brokenIdsHandledByMapping={brokenIdsHandledByMapping.Count}");
                    if (enrichedInners.Count > 0 && mappingContent != null)
                    {
                        _lastMappingPatchedContent = ItemsMappingService.PatchMappingEntries(mappingContent, enrichedInners);
                        _lastMappingEnrichedCount = enrichedInners.Count;
                        AppLogger.Debug($"[UpdateItems] mapping patch ready: {enrichedInners.Count} entries, content size {mappingContent.Length} -> {_lastMappingPatchedContent.Length}");
                    }

                    // Drop mapping-handled ids from broken-chain set: they don't need archive shadow.
                    foreach (var id in brokenIdsHandledByMapping) _lastBrokenChainIds.Remove(id);
                    _lastMappingHandledIds = brokenIdsHandledByMapping;
                    AppLogger.Debug($"[UpdateItems] broken-chain ids after mapping handling: {_lastBrokenChainIds.Count}");
                    // Recompute archiveSourceRaw to reflect the reduced broken set.
                    archiveSourceRaw.Clear();
                    foreach (var id in _itemsChangelog.Removed)
                        if (_lastWikiItemEntries.TryGetValue(id, out var raw))
                            archiveSourceRaw[id] = raw;
                    foreach (var id in _lastBrokenChainIds)
                        if (_lastWikiItemEntries.TryGetValue(id, out var raw))
                            archiveSourceRaw[id] = raw;

                    // Live ids exclude broken-chain ones — so they stay archived (don't get treated as "Restored")
                    // even though they're still in chunks. resolveItem on wiki prefers archive when p.archived[id].
                    var liveIds = new HashSet<string>(_lastLocalItemEntries.Keys, StringComparer.Ordinal);
                    foreach (var id in _lastBrokenChainIds) liveIds.Remove(id);

                    _lastArchiveDiff = ItemsArchiveService.Compute(existingArchive, archiveSourceRaw, liveIds);
                    AppLogger.Debug($"[UpdateItems] archive diff: NewlyArchived={_lastArchiveDiff.NewlyArchived.Count}, Restored={_lastArchiveDiff.Restored.Count}, Carried={_lastArchiveDiff.Carried.Count}, FinalArchive chains={_lastArchiveDiff.FinalArchive.Count}");

                    // Build the unified Archived list for the changelog: preserved items go here regardless of
                    // mechanism (archive module or mapping override). User-facing way to confirm "nothing
                    // is silently lost" — every removed-from-live item shows up either in Removed (truly gone)
                    // or in Archived (data preserved on wiki via archive or mapping enrichment).
                    var archivedList = new List<ArchivedEntry>();
                    foreach (var entry in _lastArchiveDiff.NewlyArchived)
                        archivedList.Add(new ArchivedEntry(entry.ItemId, "archive", entry.ChainName));
                    if (_lastMappingHandledIds != null)
                    {
                        foreach (var id in _lastMappingHandledIds)
                        {
                            // Try to extract chainName from the patched mapping content
                            var chain = ExtractChainNameFromEntry(_lastWikiItemEntries.GetValueOrDefault(id, ""));
                            archivedList.Add(new ArchivedEntry(id, "mapping", chain));
                        }
                    }
                    _itemsChangelog.Archived = archivedList.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
                    AppLogger.Debug($"[UpdateItems] changelog Archived: {_itemsChangelog.Archived.Count} (archive={_lastArchiveDiff.NewlyArchived.Count}, mapping={_lastMappingHandledIds?.Count ?? 0})");
                }

                // Preview confirmation
                var previewBox = CreatePreviewDialog(
                    "Update Items Data on Wiki",
                    BuildItemsUpdatePreviewChunked(toUpdate, toCreate, toBlank, localCount),
                    "Update");

                if (await previewBox.ShowDialogAsync() != WpfMessageBoxResult.Primary)
                {
                    btnUpdateItemsWiki.IsEnabled = true;
                    infoBar.IsOpen = false;
                    return;
                }

                // Authenticate
                ShowInfo("Authenticating with wiki...", InfoBarSeverity.Informational);
                using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword);
                var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

                const string blankContent = "-- This module is no longer in use\nreturn {}";
                var willPostArchive = _lastArchiveDiff != null && _lastArchiveDiff.HasChanges;
                var willPostMapping = _lastMappingPatchedContent != null && _lastMappingEnrichedCount > 0;

                // Pre-flight: detect whether Module:Items needs the archive-loader patch.
                // We only post the patched module when (a) we actually have any archived items now
                // (so the new fallback would have something to find) AND (b) the marker isn't there.
                bool willPatchConsumer = false;
                string? patchedConsumerLua = null;
                var willHaveArchive = (_lastArchiveDiff != null && _lastArchiveDiff.FinalArchive.Count > 0);
                if (willHaveArchive)
                {
                    ShowInfo($"Checking {ItemsArchiveService.ItemsConsumerModuleTitle} for archive support...", InfoBarSeverity.Informational);
                    var consumerLua = await WikiMappingService.FetchModuleContentAsync(ItemsArchiveService.ItemsConsumerModuleTitle);
                    if (consumerLua != null)
                    {
                        try
                        {
                            var (patched, changed) = ItemsArchiveService.PatchConsumerModule(consumerLua);
                            if (changed)
                            {
                                willPatchConsumer = true;
                                patchedConsumerLua = patched;
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Anchor mismatch — abort cleanly so user investigates.
                            ShowInfo($"Cannot auto-patch {ItemsArchiveService.ItemsConsumerModuleTitle}: {ex.Message}", InfoBarSeverity.Error);
                            btnUpdateItemsWiki.IsEnabled = true;
                            return;
                        }
                    }
                }

                var totalActions = toUpdate.Count + toCreate.Count + toBlank.Count + 2 // +arbiter +modules page
                                   + (willPostArchive ? 1 : 0)
                                   + (willPostMapping ? 1 : 0)
                                   + (willPatchConsumer ? 1 : 0);
                int done = 0;
                int created = 0, updated = 0, blanked = 0;

                // Build broken-chain correction map: for items whose live chainName starts with "#missing#",
                // recover the last-known-good chain name from the archive (which already has the correct chainName
                // as its chain-bucket key). We patch chunk Lua before upload so live chunks get clean chainName values.
                var brokenChainCorrections = new Dictionary<string, string>(StringComparer.Ordinal);
                if (_lastBrokenChainIds != null && _lastArchiveDiff != null)
                {
                    foreach (var (chain, items) in _lastArchiveDiff.FinalArchive)
                    {
                        if (chain.StartsWith("#missing#", StringComparison.Ordinal)) continue; // defensive
                        foreach (var id in items.Keys)
                            if (_lastBrokenChainIds.Contains(id))
                                brokenChainCorrections[id] = chain;
                    }
                }

                // Upload data chunks
                for (int i = 0; i < localCount; i++)
                {
                    var chunkIndex = i + 1;
                    var title = $"Module:Datatable/Items/{chunkIndex}";
                    var isNew = toCreate.Contains(chunkIndex);
                    var act = isNew ? "Create" : "Update";

                    ShowInfo($"[{done + 1}/{totalActions}] {act} {title}...", InfoBarSeverity.Informational);

                    var chunkLua = _lastItemChunks[i].Lua;
                    if (brokenChainCorrections.Count > 0)
                        chunkLua = ItemsArchiveService.PatchBrokenChainNamesInChunk(chunkLua, brokenChainCorrections);

                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, title, chunkLua,
                        $"{act} item data chunk {chunkIndex} (via MergeMansionWikiTools)");

                    if (isNew) created++; else updated++;
                    done++;
                }

                // Blank excess
                foreach (var i in toBlank)
                {
                    var title = $"Module:Datatable/Items/{i}";
                    ShowInfo($"[{done + 1}/{totalActions}] Blanking {title}...", InfoBarSeverity.Informational);

                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, title, blankContent,
                        $"Blank unused item data chunk {i} (via MergeMansionWikiTools)");
                    blanked++;
                    done++;
                }

                // Upload Mapping module (enriched entries)
                if (willPostMapping && _lastMappingPatchedContent != null)
                {
                    ShowInfo($"[{done + 1}/{totalActions}] Enriching {ItemsMappingService.MappingModuleTitle}...", InfoBarSeverity.Informational);
                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, ItemsMappingService.MappingModuleTitle, _lastMappingPatchedContent,
                        $"Enrich {_lastMappingEnrichedCount} entries with missing item fields (via MergeMansionWikiTools)");
                    done++;
                }

                // Upload Archive module (when there are archive changes)
                int archivedItemCountForChainNames = 0;
                if (willPostArchive && _lastArchiveDiff != null)
                {
                    ShowInfo($"[{done + 1}/{totalActions}] Updating {ItemsArchiveService.ArchiveModuleTitle}...", InfoBarSeverity.Informational);
                    var archiveLua = LuaGeneratorService.BuildArchiveModule(_lastArchiveDiff.FinalArchive, _itemsCreatedAt);
                    var archParts = new List<string>();
                    if (_lastArchiveDiff.NewlyArchived.Count > 0) archParts.Add($"+{_lastArchiveDiff.NewlyArchived.Count} archived");
                    if (_lastArchiveDiff.Restored.Count > 0) archParts.Add($"-{_lastArchiveDiff.Restored.Count} restored");
                    var archSummary = $"Update items archive ({string.Join(", ", archParts)}) (via MergeMansionWikiTools)";
                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, ItemsArchiveService.ArchiveModuleTitle, archiveLua, archSummary);
                    done++;
                    archivedItemCountForChainNames = _lastArchiveDiff.FinalArchive.Sum(kv => kv.Value.Count);
                }

                // Upload arbiter — regenerate chainNames block to include archived ids in positional list
                // + emit p.archived flat marker map alongside. Filter out items whose live chainName starts
                // with "#missing#" — they're archived shadows so chainNames shouldn't list them under the
                // broken chain name (their old chainName from the archive is what wiki callers should use).
                ShowInfo($"[{done + 1}/{totalActions}] Updating arbiter {ItemsModuleTitle}...", InfoBarSeverity.Informational);
                var chainNamesBlockForArbiter = _lastChainNamesBlock!;
                string? archivedFlagsBlock = null;
                if (_lastArchiveDiff != null && _lastArchiveDiff.FinalArchive.Count > 0 && _lastFlatItems != null)
                {
                    var liveFlatItems = _lastFlatItems
                        .Where(f => string.IsNullOrEmpty(f.ChainName) ||
                                    !f.ChainName.StartsWith("#missing#", StringComparison.Ordinal))
                        .ToList();
                    chainNamesBlockForArbiter = LuaGeneratorService.BuildChainNamesTable(
                        liveFlatItems, _lastArchiveDiff.ArchivedIdsByChain());
                    archivedFlagsBlock = LuaGeneratorService.BuildArchivedFlagsTable(
                        _lastArchiveDiff.FinalArchive.Values.SelectMany(d => d.Keys));
                }
                var arbiterLua = WikiMappingService.GenerateItemsArbiterLua(
                    localCount, chainNamesBlockForArbiter, archivedFlagsBlock, _itemsCreatedAt);
                await WikiMappingService.EditModuleAsync(
                    client, csrfToken, ItemsModuleTitle, arbiterLua,
                    $"Update items arbiter ({localCount} chunks{(archivedItemCountForChainNames > 0 ? $", {archivedItemCountForChainNames} archived" : "")}) (via MergeMansionWikiTools)");
                done++;

                // Patch consumer Module:Items (lazy archive loader + resolveItem fallback)
                if (willPatchConsumer && patchedConsumerLua != null)
                {
                    ShowInfo($"[{done + 1}/{totalActions}] Patching {ItemsArchiveService.ItemsConsumerModuleTitle} for archive support...", InfoBarSeverity.Informational);
                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, ItemsArchiveService.ItemsConsumerModuleTitle, patchedConsumerLua,
                        $"Add archive loader + resolveItem fallback (via MergeMansionWikiTools)");
                    done++;
                }

                // Update Modules page
                ShowInfo($"[{done + 1}/{totalActions}] Updating Modules page...", InfoBarSeverity.Informational);
                await WikiMappingService.UpdateModulesPageAsync(client, csrfToken, localCount, _firstEventChunkIndex);
                done++;

                // Report success
                var parts = new List<string>();
                if (updated > 0) parts.Add($"{updated} updated");
                if (created > 0) parts.Add($"{created} created");
                if (blanked > 0) parts.Add($"{blanked} blanked");
                parts.Add("arbiter updated");
                parts.Add("Modules page updated");

                ShowInfo($"Wiki updated — {string.Join(", ", parts)}.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"Wiki update failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnUpdateItemsWiki.IsEnabled = _main.Settings.WikiVerified && _lastItemChunks.Count > 0;
            UpdateButtonTooltip(btnUpdateItemsWiki, _lastItemChunks.Count > 0);
        }
    }

    private ChangelogData ComputeItemsChangelog(string wikiContent, string localLua)
    {
        var wikiEntries = WikiMappingService.ExtractLuaTableEntries(wikiContent, "items");
        var localEntries = WikiMappingService.ExtractLuaTableEntries(localLua, "items");
        return ComputeItemsChangelog(wikiEntries, localEntries);
    }

    private ChangelogData ComputeItemsChangelog(string wikiContent, Dictionary<string, string> localEntries)
    {
        var wikiEntries = WikiMappingService.ExtractLuaTableEntries(wikiContent, "items");
        return ComputeItemsChangelog(wikiEntries, localEntries);
    }

    private static ChangelogData ComputeItemsChangelog(
        Dictionary<string, string> wikiEntries, Dictionary<string, string> localEntries)
    {
        AppLogger.Debug($"[ComputeItemsChangelog] wikiEntries={wikiEntries.Count}, localEntries={localEntries.Count}");

        var added = localEntries.Keys.Except(wikiEntries.Keys).OrderBy(k => k).ToList();
        var removed = wikiEntries.Keys.Except(localEntries.Keys).OrderBy(k => k).ToList();
        var modified = localEntries.Keys.Intersect(wikiEntries.Keys)
            .Where(k => localEntries[k] != wikiEntries[k])
            .OrderBy(k => k)
            .Select(k => new ModifiedEntry(k, wikiEntries[k], localEntries[k]))
            .ToList();

        AppLogger.Debug($"[ComputeItemsChangelog] initial: +{added.Count} added, -{removed.Count} removed, ~{modified.Count} modified");

        // Rename detection: pair Removed items with their counterparts in CURRENT LOCAL DATA.
        // Match heuristic: both ids match `^CBE_<event>_(.+)$` and the (.+) part is identical, but the
        // <event> segments differ. Example: `CBE_Easter2025_Assembly_01` (Removed — old event ended) ↔
        // `CBE_SweetMess_Assembly_01` (still in local — current event) → game devs renamed the event.
        //
        // IMPORTANT: counterparts are searched in the FULL set of localEntries (not just `added`) because
        // the new event's items typically already exist on the wiki (Modified or unchanged), they're not
        // freshly added. Looking only at `added` misses them entirely.
        var renamed = new List<RenamedEntry>();
        var rxEvent = new System.Text.RegularExpressions.Regex(
            @"^CBE_([A-Za-z0-9]+)_(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Build local index: rest → list of (id, eventPrefix)
        var localByRest = new Dictionary<string, List<(string Id, string Event)>>(StringComparer.Ordinal);
        foreach (var lid in localEntries.Keys)
        {
            var m = rxEvent.Match(lid);
            if (!m.Success) continue;
            var ev = m.Groups[1].Value;
            var rest = m.Groups[2].Value;
            if (!localByRest.TryGetValue(rest, out var list))
                localByRest[rest] = list = new List<(string, string)>();
            list.Add((lid, ev));
        }
        AppLogger.Debug($"[ComputeItemsChangelog] localByRest entries: {localByRest.Count} unique rests across local items");

        var pairedRemoved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rid in removed)
        {
            var m = rxEvent.Match(rid);
            if (!m.Success) continue;
            var oldEvent = m.Groups[1].Value;
            var rest = m.Groups[2].Value;
            if (!localByRest.TryGetValue(rest, out var candidates) || candidates.Count == 0) continue;
            // Match candidates with a DIFFERENT event prefix (otherwise it's a no-op pairing).
            (string Id, string Event)? match = null;
            foreach (var c in candidates)
            {
                if (!string.Equals(c.Event, oldEvent, StringComparison.Ordinal))
                {
                    match = c;
                    break;
                }
            }
            if (match == null) continue;

            pairedRemoved.Add(rid);
            var oldChain = ExtractChainNameFromEntry(wikiEntries.GetValueOrDefault(rid, ""));
            var newChain = ExtractChainNameFromEntry(localEntries.GetValueOrDefault(match.Value.Id, ""));
            renamed.Add(new RenamedEntry(rid, match.Value.Id, oldChain, newChain));
        }
        if (renamed.Count > 0)
        {
            removed = removed.Where(r => !pairedRemoved.Contains(r)).ToList();
            renamed = renamed.OrderBy(r => r.OldId, StringComparer.Ordinal).ToList();
        }

        AppLogger.Debug($"[ComputeItemsChangelog] final: +{added.Count} added, -{removed.Count} removed (was {removed.Count + pairedRemoved.Count}), ~{modified.Count} modified, ↻{renamed.Count} renamed");
        if (renamed.Count > 0)
        {
            // Log first 5 rename pairs for sanity check
            foreach (var r in renamed.Take(5))
                AppLogger.Debug($"[ComputeItemsChangelog] rename sample: {r.OldId} → {r.NewId}  (chain {r.OldChain} → {r.NewChain})");
        }

        return new ChangelogData(added, removed, modified, renamed.Count > 0 ? renamed : null);
    }

    private static string? ExtractChainNameFromEntry(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"chainName\s*=\s*""([^""]+)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static ChangelogData ComputeAreasChangelog(
        Dictionary<string, string> wikiEntries, Dictionary<string, string> localEntries,
        Dictionary<string, double>? ordering = null)
    {
        // Build sort key: known ordering index first, then unknown areas after max
        double SortKey(string key)
        {
            if (ordering != null && ordering.TryGetValue(key, out var idx)) return idx;
            return double.MaxValue; // unknown areas sort last
        }

        var added = localEntries.Keys.Except(wikiEntries.Keys).OrderBy(SortKey).ThenBy(k => k).ToList();
        var removed = wikiEntries.Keys.Except(localEntries.Keys).OrderBy(SortKey).ThenBy(k => k).ToList();
        var modified = localEntries.Keys.Intersect(wikiEntries.Keys)
            .Where(k => NormalizeAreaBlock(wikiEntries[k]) != NormalizeAreaBlock(localEntries[k]))
            .OrderBy(SortKey).ThenBy(k => k)
            .Select(k => new ModifiedEntry(k, wikiEntries[k], localEntries[k]))
            .ToList();
        return new ChangelogData(added, removed, modified);
    }

    private static string NormalizeAreaBlock(string block) =>
        System.Text.RegularExpressions.Regex.Replace(block.Replace("\r", "").Trim(), @"\s+", " ");

    private static string FormatSize(long bytes)
    {
        return bytes > 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):F2} MB"
            : $"{bytes / 1024.0:F0} KB";
    }

    private UIElement BuildItemsUpdatePreviewSingle(bool moduleExists, int lineCount, string sizeStr)
    {
        var root = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };

        var primary = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var subtle = (Brush)FindResource("SubtleFillColorSecondaryBrush");

        // Fixed top section
        var topSection = new StackPanel();
        DockPanel.SetDock(topSection, Dock.Top);

        var action = moduleExists ? "overwritten" : "created";
        topSection.Children.Add(new WpfTextBlock
        {
            Text = $"1 module will be {action}",
            FontSize = 13, Foreground = secondary, Margin = new Thickness(0, 0, 0, 4)
        });

        topSection.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 4, 0, 10),
            Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
        });

        var row = new Border
        {
            Background = subtle, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconTb = new WpfTextBlock
        {
            Text = moduleExists ? "\uD83D\uDCDD" : "\u2795",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
        };
        Grid.SetColumn(iconTb, 0);
        grid.Children.Add(iconTb);

        var content = new StackPanel();
        content.Children.Add(new WpfTextBlock
        {
            Text = $"{(moduleExists ? "Update" : "Create")} {ItemsModuleTitle}",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new WpfTextBlock
        {
            Text = $"p.items + p.chainNames \u2014 {lineCount} lines \u2022 {sizeStr}",
            FontSize = 11, Foreground = secondary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
        });
        content.Children.Add(new WpfTextBlock
        {
            Text = moduleExists
                ? "Existing module content will be fully replaced"
                : "New module will be created on the wiki",
            FontSize = 10, Foreground = tertiary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        row.Child = grid;
        topSection.Children.Add(row);

        root.Children.Add(topSection);

        // Changelog fills remaining space
        root.Children.Add(BuildChangelogElement(_itemsChangelog, "item", primary, secondary, tertiary));

        return root;
    }

    private UIElement BuildItemsUpdatePreviewChunked(
        List<int> toUpdate, List<int> toCreate, List<int> toBlank, int chunkCount)
    {
        var root = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };

        var primary = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var subtle = (Brush)FindResource("SubtleFillColorSecondaryBrush");

        // Fixed top section
        var topSection = new StackPanel();
        DockPanel.SetDock(topSection, Dock.Top);

        var totalActions = toUpdate.Count + toCreate.Count + toBlank.Count + 2;
        var mainChunkCount = _firstEventChunkIndex > 0 ? _firstEventChunkIndex - 1 : chunkCount;
        var eventChunkCount = chunkCount - mainChunkCount;
        var chunkSummary = _firstEventChunkIndex > 0
            ? $"{chunkCount} chunks: {mainChunkCount} main + {eventChunkCount} event"
            : $"{chunkCount} chunks";
        topSection.Children.Add(new WpfTextBlock
        {
            Text = $"{totalActions} module(s) will be edited ({chunkSummary})",
            FontSize = 13, Foreground = secondary, Margin = new Thickness(0, 0, 0, 4)
        });

        topSection.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 4, 0, 10),
            Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
        });

        const string wikiBase = "https://merge-mansion.fandom.com/wiki/";

        // Helper: add a step row to the top section
        void AddStep(string icon, string title, string? detail = null, string? detail2 = null, string? url = null)
        {
            var row = new Border
            {
                Background = subtle, CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconEl = new WpfTextBlock
            {
                Text = icon, FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetColumn(iconEl, 0);
            g.Children.Add(iconEl);

            var sp = new StackPanel();
            if (url != null)
            {
                var titleTb = new WpfTextBlock
                {
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = primary, TextWrapping = TextWrapping.Wrap
                };
                var linkRun = new System.Windows.Documents.Run(title)
                {
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                linkRun.MouseEnter += (s, e) => linkRun.TextDecorations = TextDecorations.Underline;
                linkRun.MouseLeave += (s, e) => linkRun.TextDecorations = null;
                var capturedUrl = url;
                linkRun.MouseLeftButtonDown += (s, e) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(capturedUrl) { UseShellExecute = true });
                };
                titleTb.Inlines.Add(linkRun);
                sp.Children.Add(titleTb);
            }
            else
            {
                sp.Children.Add(new WpfTextBlock
                {
                    Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = primary, TextWrapping = TextWrapping.Wrap
                });
            }
            if (detail != null)
                sp.Children.Add(new WpfTextBlock
                {
                    Text = detail, FontSize = 11, Foreground = secondary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });
            if (detail2 != null)
                sp.Children.Add(new WpfTextBlock
                {
                    Text = detail2, FontSize = 10, Foreground = tertiary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });

            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            row.Child = g;
            topSection.Children.Add(row);
        }

        foreach (var i in toUpdate)
        {
            var lines = _lastItemChunks[i - 1].Lua.Count(c => c == '\n') + 1;
            var size = FormatSize(Encoding.UTF8.GetByteCount(_lastItemChunks[i - 1].Lua));
            var typeHint = _firstEventChunkIndex > 0
                ? (i < _firstEventChunkIndex ? " (Main)" : " (Event)")
                : "";
            AddStep("\uD83D\uDCDD", $"Update Module:Datatable/Items/{i}{typeHint}",
                $"{lines} lines \u2022 {size}", "Overwrite existing data chunk",
                $"{wikiBase}Module:Datatable/Items/{i}");
        }

        foreach (var i in toCreate)
        {
            var lines = _lastItemChunks[i - 1].Lua.Count(c => c == '\n') + 1;
            var size = FormatSize(Encoding.UTF8.GetByteCount(_lastItemChunks[i - 1].Lua));
            var typeHint = _firstEventChunkIndex > 0
                ? (i < _firstEventChunkIndex ? " (Main)" : " (Event)")
                : "";
            AddStep("\u2795", $"Create Module:Datatable/Items/{i}{typeHint}",
                $"{lines} lines \u2022 {size}", "New data module",
                $"{wikiBase}Module:Datatable/Items/{i}");
        }

        foreach (var i in toBlank)
        {
            AddStep("\uD83D\uDDD1", $"Blank Module:Datatable/Items/{i}",
                "Module is no longer needed",
                "Will be replaced with empty return",
                $"{wikiBase}Module:Datatable/Items/{i}");
        }

        // Mapping enrichment step (shown only if any entries got enriched)
        if (_lastMappingPatchedContent != null && _lastMappingEnrichedCount > 0)
        {
            AddStep("🧩", $"Enrich {ItemsMappingService.MappingModuleTitle}",
                $"+{_lastMappingEnrichedCount} entries enriched with missing fields from items data",
                "Existing mapping fields are preserved; only missing fields (name, level, desc, …) are appended",
                $"{wikiBase}{ItemsMappingService.MappingModuleTitle}");
        }

        // Archive step (shown only if there are archive changes)
        if (_lastArchiveDiff != null && _lastArchiveDiff.HasChanges)
        {
            var archAdds = _lastArchiveDiff.NewlyArchived.Count;
            var archRestores = _lastArchiveDiff.Restored.Count;
            var archCarried = _lastArchiveDiff.Carried.Count;
            var brokenCount = _lastBrokenChainIds?.Count ?? 0;
            var removedRegular = archAdds - brokenCount;
            var archParts = new List<string>();
            if (archAdds > 0)
            {
                if (brokenCount > 0 && removedRegular > 0)
                    archParts.Add($"+{archAdds} new archived ({removedRegular} removed, {brokenCount} #missing# shadow)");
                else if (brokenCount > 0)
                    archParts.Add($"+{brokenCount} #missing# chain shadow archived");
                else
                    archParts.Add($"+{archAdds} new archived");
            }
            if (archRestores > 0) archParts.Add($"-{archRestores} restored to live");
            if (archCarried > 0) archParts.Add($"{archCarried} kept");
            AddStep("\uD83D\uDDC4", $"Update {ItemsArchiveService.ArchiveModuleTitle}",
                string.Join(", ", archParts),
                "Preserves last-known full data of removed items + #missing# chain shadows so wiki pages keep working",
                $"{wikiBase}{ItemsArchiveService.ArchiveModuleTitle}");
        }

        AddStep("\uD83D\uDD17", $"Update {ItemsModuleTitle}",
            $"Arbiter \u2014 require() {chunkCount} chunk(s) + p.chainNames + p.archived",
            "Flat-merges all chunks into p.items; chainNames stays positional (ipairs-friendly), p.archived flat marker map for archived ids",
            $"{wikiBase}{ItemsModuleTitle}");

        // Show consumer patch step in dialog if archive is non-empty (we'll detect at click-time)
        if (_lastArchiveDiff != null && _lastArchiveDiff.FinalArchive.Count > 0)
        {
            AddStep("\uD83E\uDE79", $"Patch {ItemsArchiveService.ItemsConsumerModuleTitle} (if needed)",
                "Lazy archive loader + resolveItem fallback to Archive module",
                "Idempotent \u2014 only posts when the loader marker isn't already present",
                $"{wikiBase}{ItemsArchiveService.ItemsConsumerModuleTitle}");
        }

        var modulesDetail = _firstEventChunkIndex > 0
            ? $"Add/update {chunkCount} submodule link(s) with main/event annotations"
            : $"Add/update {chunkCount} submodule link(s)";
        AddStep("\uD83D\uDCC4", "Update Modules page",
            modulesDetail,
            "Keeps existing links like Datatable/Items/Mapping",
            $"{wikiBase}Modules");

        root.Children.Add(topSection);

        // Changelog fills remaining space
        root.Children.Add(BuildChangelogElement(_itemsChangelog, "item", primary, secondary, tertiary));

        return root;
    }

    // ── Generate Items ────────────────────────────────────────────────

    private async void BtnGenerateItems_Click(object sender, RoutedEventArgs e)
    {
        var chainPath = _main.Settings.ChainItemOddsPath;
        if (string.IsNullOrEmpty(chainPath) || !File.Exists(chainPath))
        {
            ShowInfo("Items file not configured or not found. Set it in Settings.", InfoBarSeverity.Error);
            return;
        }

        btnGenerateItems.IsEnabled = false;
        ShowInfo("Generating items...", InfoBarSeverity.Informational);

        try
        {
            // Parse fresh from JSON — pure game data, no wiki mapping, no custom names
            var (itemChunks, chainNamesBlock, chainCount, createdAt, firstEventIdx, flatItems) = await Task.Run(() =>
            {
                using var _t = AppLogger.Timed("GenerateItemChunks");
                var freshDs = new DataService(new ChainNameService());
                freshDs.LoadAsync(chainPath).GetAwaiter().GetResult();
                var result = _luaGen.GenerateItemChunks(freshDs.Chains, useRawNames: true, freshDs.CreatedAt);
                var cn = _luaGen.GenerateChainNamesLua(freshDs.Chains, useRawNames: true);
                return (result.Chunks, cn, freshDs.Chains.Count, freshDs.CreatedAt, result.FirstEventChunkIndex, result.FlatItems);
            });
            _lastItemChunks = itemChunks;
            _lastChainNamesBlock = chainNamesBlock;
            _itemsCreatedAt = createdAt;
            _firstEventChunkIndex = firstEventIdx;
            _lastFlatItems = flatItems;
            _lastArchiveDiff = null;
            _lastWikiItemEntries = null;
            _lastLocalItemEntries = null;
            _lastBrokenChainIds = null;

            // For backward compat: _lastCombined is the single module content (1 chunk)
            // or the first chunk preview (multi-chunk) — used by BuildItemNameMap, changelog
            _lastCombined = itemChunks.Count == 1 ? itemChunks[0].Lua : null;

            itemsSection.Visibility = Visibility.Visible;
            if (_itemsCollapsed)
            {
                itemsContent.Visibility = Visibility.Visible;
                iconCollapseItems.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                _itemsCollapsed = false;
            }

            // Cancel any ongoing combined load
            _combinedLoadCts?.Cancel(); _combinedLoadCts?.Dispose();
            _combinedLoadCts = new CancellationTokenSource();

            if (itemChunks.Count == 1)
            {
                // Single module — same UI as before
                var lua = itemChunks[0].Lua;
                var lineCount = lua.Count(c => c == '\n') + 1;
                txtCombinedHeader.Text = $"p.items + p.chainNames — {chainCount} chains · {lineCount} lines";

                // Show items chunk cards container empty, show combined card
                itemChunksContainer.Children.Clear();
                _itemChunkCardData.Clear();
                itemChunksContainer.Visibility = Visibility.Collapsed;
                combinedCard.Visibility = Visibility.Visible;

                var (preview, remaining) = SplitForPreview(lua);
                txtCombined.Text = preview;
                txtCombinedSizeWarning.Visibility = Visibility.Collapsed;
                combinedMiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;

                _ = LazySetCombinedFullTextAsync(lua, _combinedLoadCts.Token);
            }
            else
            {
                // Multi-chunk — show chunk cards, hide single combined card
                var totalLines = itemChunks.Sum(c => c.Lua.Count(ch => ch == '\n') + 1);
                var mainCount = _firstEventChunkIndex > 0 ? _firstEventChunkIndex - 1 : itemChunks.Count;
                var eventCount = itemChunks.Count - mainCount;
                var chunkDesc = _firstEventChunkIndex > 0
                    ? $"{itemChunks.Count} chunks ({mainCount} main, {eventCount} event)"
                    : $"{itemChunks.Count} chunks";
                txtCombinedHeader.Text = $"p.items + p.chainNames — {chainCount} chains · {chunkDesc} · {totalLines} lines";

                combinedCard.Visibility = Visibility.Collapsed;
                itemChunksContainer.Visibility = Visibility.Visible;
                BuildItemChunkCards(itemChunks, chainNamesBlock);
            }

            _itemsChangelog = null;
            UpdateItemsWikiButtonState();
            _ = CheckItemsDateAsync();
            Increment(s => s.LuaItemsGenerated++);
            var chunkInfo = itemChunks.Count > 1 ? $" ({itemChunks.Count} chunks)" : "";
            ShowInfo($"Items + Chain Names generated{chunkInfo}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnGenerateItems.IsEnabled = true;
        }
    }

    private async Task LazySetCombinedFullTextAsync(string lua, CancellationToken ct)
    {
        var (_, remaining) = SplitForPreview(lua);
        if (remaining == null) { combinedMiniLoading.Visibility = Visibility.Collapsed; return; }

        // Let the UI render the preview + spinner before starting chunked append
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(30);
        if (ct.IsCancellationRequested) return;

        var bytes = Encoding.UTF8.GetByteCount(lua);
        if (bytes > MaxWikiBytes)
        {
            var mb = bytes / (1024.0 * 1024.0);
            txtCombinedSizeWarning.Text =
                $"⚠ This file is {mb:F2} MB and exceeds the Wiki 2 MB limit.";
            txtCombinedSizeWarning.Visibility = Visibility.Visible;
        }

        await AppendChunkedAsync(txtCombined, remaining, combinedMiniLoading, ct);
    }

    // ── Collapse / expand ────────────────────────────────────────────

    private void BtnCollapseAreas_Click(object sender, RoutedEventArgs e)
    {
        _areasCollapsed = !_areasCollapsed;
        areasContent.Visibility = _areasCollapsed ? Visibility.Collapsed : Visibility.Visible;
        iconCollapseAreas.Symbol = _areasCollapsed
            ? Wpf.Ui.Controls.SymbolRegular.ChevronDown24
            : Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
    }

    private void BtnCollapseItems_Click(object sender, RoutedEventArgs e)
    {
        _itemsCollapsed = !_itemsCollapsed;
        itemsContent.Visibility = _itemsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        iconCollapseItems.Symbol = _itemsCollapsed
            ? Wpf.Ui.Controls.SymbolRegular.ChevronDown24
            : Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
    }

    /// <summary>
    /// Builds a changelog section element for use in confirmation dialogs.
    /// Returns a DockPanel: separator + summary header pinned at top,
    /// detail content in a ScrollViewer that fills remaining dialog space.
    /// The parent Build*Preview DockPanel must place this as the fill child (last, no Dock).
    /// </summary>
    private UIElement BuildChangelogElement(ChangelogData? changelog,
        string entityName, Brush primary, Brush secondary, Brush tertiary)
    {
        var isAreaMode = entityName == "area";
        var panel = new DockPanel();

        // Separator (pinned top)
        var sep = new Border
        {
            Height = 1, Margin = new Thickness(0, 6, 0, 10),
            Background = secondary, Opacity = 0.3
        };
        DockPanel.SetDock(sep, Dock.Top);
        panel.Children.Add(sep);

        if (changelog == null)
        {
            var loading = new WpfTextBlock
            {
                Text = "Changelog: loading...",
                FontSize = 11, Foreground = tertiary
            };
            DockPanel.SetDock(loading, Dock.Top);
            panel.Children.Add(loading);
            return panel;
        }

        if (!changelog.HasChanges())
        {
            var noChanges = new WpfTextBlock
            {
                Text = "No data changes detected vs wiki",
                FontSize = 11, Foreground = tertiary
            };
            DockPanel.SetDock(noChanges, Dock.Top);
            panel.Children.Add(noChanges);
            return panel;
        }

        // Summary header (pinned top)
        var parts = new List<string>();
        if (changelog.Modified.Count > 0) parts.Add($"{changelog.Modified.Count} modified");
        if (changelog.Added.Count > 0) parts.Add($"+{changelog.Added.Count} new");
        if (changelog.Removed.Count > 0) parts.Add($"\u2212{changelog.Removed.Count} removed");
        if ((changelog.Renamed?.Count ?? 0) > 0) parts.Add($"\u21bb{changelog.Renamed!.Count} renamed");
        if ((changelog.Archived?.Count ?? 0) > 0) parts.Add($"\ud83d\udce6{changelog.Archived!.Count} archived");

        var summaryTb = new WpfTextBlock
        {
            Text = $"Data changes: {string.Join(", ", parts)}",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(summaryTb, Dock.Top);
        panel.Children.Add(summaryTb);

        // Detail in ScrollViewer
        var detailContent = new StackPanel();
        var nameMap = BuildItemNameMap();
        BuildChangelogDetail(detailContent, changelog, primary, secondary, tertiary, nameMap, isAreaMode);
        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 300,
            Content = detailContent
        });

        return panel;
    }

    // ── Chunk cards ──────────────────────────────────────────────────

    private void BuildChunkCards(List<(string Label, string Lua)> chunks)
    {
        chunksContainer.Children.Clear();
        _chunkCardData.Clear();

        // Arbiter card first (collapsed by default)
        var arbiterLua = WikiMappingService.GenerateAreasArbiterLua(chunks.Count);
        var arbiterCard = BuildAreaChunkCard("Arbiter", arbiterLua, -1, isArbiter: true);
        arbiterCard.Margin = new Thickness(0, 0, 0, 8);
        chunksContainer.Children.Add(arbiterCard);

        for (int i = 0; i < chunks.Count; i++)
        {
            var card = BuildAreaChunkCard(chunks[i].Label, chunks[i].Lua, i);
            card.Margin = new Thickness(0, 0, 0, 8);
            chunksContainer.Children.Add(card);
        }
    }

    private void BuildItemChunkCards(List<(string Label, string Lua)> chunks, string chainNamesBlock)
    {
        itemChunksContainer.Children.Clear();
        _itemChunkCardData.Clear();

        // Cancel any ongoing item chunk loads
        _combinedLoadCts?.Cancel(); _combinedLoadCts?.Dispose();
        _combinedLoadCts = new CancellationTokenSource();

        // Arbiter card first (collapsed by default)
        var arbiterPreview = WikiMappingService.GenerateItemsArbiterLua(
            chunks.Count, chainNamesBlock, _itemsCreatedAt);
        var arbiterCard = BuildItemChunkCard("Arbiter", arbiterPreview, -1, isArbiter: true);
        arbiterCard.Margin = new Thickness(0, 0, 0, 8);
        itemChunksContainer.Children.Add(arbiterCard);

        for (int i = 0; i < chunks.Count; i++)
        {
            // Determine main/event suffix for chunk label
            string? chunkSuffix = null;
            if (_firstEventChunkIndex > 0)
            {
                var chunkNumber = i + 1; // 1-based
                if (chunkNumber < _firstEventChunkIndex)
                    chunkSuffix = "Main";
                else if (chunkNumber == _firstEventChunkIndex)
                    chunkSuffix = "Event";
                else
                    chunkSuffix = "Event";
            }
            var card = BuildItemChunkCard(chunks[i].Label, chunks[i].Lua, i, chunkSuffix: chunkSuffix);
            card.Margin = new Thickness(0, 0, 0, 8);
            itemChunksContainer.Children.Add(card);
        }
    }

    private FrameworkElement BuildItemChunkCard(string label, string lua, int index, bool isArbiter = false, string? chunkSuffix = null)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1)
        };

        var sp = new StackPanel();

        // Header row
        var headerGrid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lineCount = lua.Count(c => c == '\n') + 1;
        var bytes = Encoding.UTF8.GetByteCount(lua);
        var sizeStr = FormatSize(bytes);
        var suffixStr = chunkSuffix != null ? $" ({chunkSuffix})" : "";
        var labelText = isArbiter
            ? $"Arbiter (Module:Datatable/Items) — {lineCount} lines \u2022 {sizeStr}"
            : $"Chunk {label}{suffixStr} (Module:Datatable/Items/{label}) — {lineCount} lines \u2022 {sizeStr}";

        var lbl = new WpfTextBlock
        {
            Text = labelText,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);

        var miniLoading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        miniLoading.Children.Add(new WpfTextBlock
        {
            Text = "Loading...", FontSize = 11,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        miniLoading.Children.Add(new Wpf.Ui.Controls.ProgressRing
        {
            Width = 12, Height = 12, IsIndeterminate = true,
            Margin = new Thickness(4, 0, 0, 0)
        });
        Grid.SetColumn(miniLoading, 1);

        var copyBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Copy",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0)
        };
        int capturedIndex = index;
        bool capturedIsArbiter = isArbiter;
        string capturedLua = lua;
        string capturedLabel = label;
        copyBtn.Click += (_, _) =>
        {
            App.NativeSetClipboardText(capturedLua);
            var desc = capturedIsArbiter ? "Arbiter" : $"Chunk {capturedLabel}";
            ShowInfo($"{desc} copied to clipboard.", InfoBarSeverity.Success);
        };
        Grid.SetColumn(copyBtn, 2);

        headerGrid.Children.Add(lbl);
        headerGrid.Children.Add(miniLoading);
        headerGrid.Children.Add(copyBtn);
        sp.Children.Add(headerGrid);

        // Warning panel
        var warnPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 8)
        };
        var warnText = new WpfTextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        try { warnText.Foreground = (Brush)FindResource("SystemFillColorCautionBrush"); }
        catch { warnText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25)); }
        warnPanel.Children.Add(warnText);
        sp.Children.Add(warnPanel);

        // Separator + TextBox (collapsible for arbiter)
        var separator = new Separator { Opacity = 0.15, Margin = new Thickness(0) };

        var tb = new WpfTextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 220,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = SplitForPreview(lua).Preview
        };

        if (isArbiter)
        {
            // Wrap separator + textbox in a collapsible panel, collapsed by default
            var contentPanel = new StackPanel { Visibility = Visibility.Collapsed };
            contentPanel.Children.Add(separator);
            contentPanel.Children.Add(tb);
            sp.Children.Add(contentPanel);

            // Add chevron to header (theme-aware foreground via SetResourceReference)
            var chevron = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            chevron.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "TextFillColorSecondaryBrush");
            chevron.Margin = new Thickness(8, 0, 0, 0);
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(chevron, 3);
            headerGrid.Children.Add(chevron);

            // Make entire header row clickable for expand/collapse
            headerGrid.Background = Brushes.Transparent;
            headerGrid.Cursor = System.Windows.Input.Cursors.Hand;
            headerGrid.MouseLeftButtonDown += (_, _) =>
            {
                if (contentPanel.Visibility == Visibility.Visible)
                {
                    contentPanel.Visibility = Visibility.Collapsed;
                    chevron.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
                }
                else
                {
                    contentPanel.Visibility = Visibility.Visible;
                    chevron.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                }
            };
        }
        else
        {
            sp.Children.Add(separator);
            sp.Children.Add(tb);
        }

        border.Child = sp;

        // Track for re-navigation (only data chunks, not arbiter)
        if (!isArbiter)
            _itemChunkCardData.Add(new ChunkCardData(tb, miniLoading, warnPanel, warnText));

        // Async: chunked-load full text
        _ = LazySetChunkFullTextAsync(tb, lua, miniLoading, warnPanel, warnText,
            isArbiter ? "Arbiter" : $"Chunk {label}",
            _combinedLoadCts?.Token ?? CancellationToken.None);

        return border;
    }

    private FrameworkElement BuildAreaChunkCard(string label, string lua, int index, bool isArbiter = false)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1)
        };

        var sp = new StackPanel();

        // ── Header row: label | mini-loading | Copy | (chevron for arbiter) ──
        var headerGrid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lineCount = lua.Count(c => c == '\n') + 1;
        var bytes = Encoding.UTF8.GetByteCount(lua);
        var sizeStr = FormatSize(bytes);
        var labelText = isArbiter
            ? $"Arbiter (Module:Datatable/Areas) — {lineCount} lines \u2022 {sizeStr}"
            : $"Areas {label} (Module:Datatable/Areas/{label}) — {lineCount} lines \u2022 {sizeStr}";

        var lbl = new WpfTextBlock
        {
            Text = labelText,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);

        // Mini loading (visible while full text loads)
        var miniLoading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        miniLoading.Children.Add(new WpfTextBlock
        {
            Text = "Loading...",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        miniLoading.Children.Add(new Wpf.Ui.Controls.ProgressRing
        {
            Width = 12, Height = 12, IsIndeterminate = true,
            Margin = new Thickness(4, 0, 0, 0)
        });
        Grid.SetColumn(miniLoading, 1);

        var copyBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Copy",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0)
        };
        if (isArbiter)
        {
            string capturedLua = lua;
            copyBtn.Click += (_, _) =>
            {
                App.NativeSetClipboardText(capturedLua);
                ShowInfo("Arbiter copied to clipboard.", InfoBarSeverity.Success);
            };
        }
        else
        {
            int capturedIndex = index;
            copyBtn.Click += (_, _) => CopyChunk(capturedIndex);
        }
        Grid.SetColumn(copyBtn, 2);

        headerGrid.Children.Add(lbl);
        headerGrid.Children.Add(miniLoading);
        headerGrid.Children.Add(copyBtn);
        sp.Children.Add(headerGrid);

        // ── 2MB warning panel (hidden by default) ──
        var warnPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 8)
        };
        var warnText = new WpfTextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        try { warnText.Foreground = (Brush)FindResource("SystemFillColorCautionBrush"); }
        catch { warnText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25)); }
        warnPanel.Children.Add(warnText);
        sp.Children.Add(warnPanel);

        // ── Separator + TextBox ──
        var separator = new Separator { Opacity = 0.15, Margin = new Thickness(0) };

        var tb = new WpfTextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 220,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = SplitForPreview(lua).Preview
        };

        if (isArbiter)
        {
            // Wrap separator + textbox in a collapsible panel, collapsed by default
            var contentPanel = new StackPanel { Visibility = Visibility.Collapsed };
            contentPanel.Children.Add(separator);
            contentPanel.Children.Add(tb);
            sp.Children.Add(contentPanel);

            // Add chevron to header (theme-aware foreground via SetResourceReference)
            var chevron = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            chevron.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "TextFillColorSecondaryBrush");
            chevron.Margin = new Thickness(8, 0, 0, 0);
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(chevron, 3);
            headerGrid.Children.Add(chevron);

            // Make entire header row clickable for expand/collapse
            headerGrid.Background = Brushes.Transparent;
            headerGrid.Cursor = System.Windows.Input.Cursors.Hand;
            headerGrid.MouseLeftButtonDown += (_, _) =>
            {
                if (contentPanel.Visibility == Visibility.Visible)
                {
                    contentPanel.Visibility = Visibility.Collapsed;
                    chevron.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
                }
                else
                {
                    contentPanel.Visibility = Visibility.Visible;
                    chevron.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                }
            };
        }
        else
        {
            sp.Children.Add(separator);
            sp.Children.Add(tb);
        }

        border.Child = sp;

        // Track for re-navigation reset (only data chunks, not arbiter)
        if (!isArbiter)
            _chunkCardData.Add(new ChunkCardData(tb, miniLoading, warnPanel, warnText));

        // Async: wait for UI to render, then chunked-load full text
        _ = LazySetChunkFullTextAsync(tb, lua, miniLoading, warnPanel, warnText,
            isArbiter ? "Arbiter" : label,
            _chunkLoadCts?.Token ?? CancellationToken.None);

        return border;
    }

    private async Task LazySetChunkFullTextAsync(
        WpfTextBox tb, string lua,
        UIElement miniLoading, StackPanel warnPanel, WpfTextBlock warnText, string label,
        CancellationToken ct)
    {
        var (_, remaining) = SplitForPreview(lua);
        if (remaining == null) { miniLoading.Visibility = Visibility.Collapsed; return; }

        // Let the UI render the preview + spinner before starting chunked append
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(30);
        if (ct.IsCancellationRequested) return;

        var bytes = Encoding.UTF8.GetByteCount(lua);
        if (bytes > MaxWikiBytes)
        {
            var mb = bytes / (1024.0 * 1024.0);
            warnText.Text =
                $"⚠ Chunk \"{label}\" is {mb:F2} MB and exceeds the Wiki 2 MB limit.";
            warnPanel.Visibility = Visibility.Visible;
        }

        await AppendChunkedAsync(tb, remaining, miniLoading, ct);
    }

    /// <summary>
    /// Splits text into a 150-line preview and the remaining text.
    /// If the text has ≤150 lines, Remaining is null (no chunked load needed).
    /// Preview + Remaining == original text (no data lost, no suffix added).
    /// </summary>
    private static (string Preview, string? Remaining) SplitForPreview(string lua)
    {
        const int maxLines = 150;
        int lineCount = 0;
        int pos = 0;
        while (pos < lua.Length && lineCount < maxLines)
        {
            int next = lua.IndexOf('\n', pos);
            if (next < 0) { pos = lua.Length; break; }
            pos = next + 1;
            lineCount++;
        }

        if (lineCount < maxLines || pos >= lua.Length) return (lua, null);
        return (lua[..pos], lua[pos..]);
    }

    /// <summary>
    /// Appends text to a TextBox in chunks of ~200 lines, yielding to the dispatcher
    /// between chunks so that UI stays responsive and loading animations keep spinning.
    /// </summary>
    private async Task AppendChunkedAsync(WpfTextBox tb, string remaining, UIElement loading, CancellationToken ct)
    {
        const int linesPerChunk = 200;
        int start = 0;

        while (start < remaining.Length)
        {
            if (ct.IsCancellationRequested) return;

            int lineCount = 0;
            int pos = start;
            while (pos < remaining.Length && lineCount < linesPerChunk)
            {
                int next = remaining.IndexOf('\n', pos);
                if (next < 0) { pos = remaining.Length; break; }
                pos = next + 1;
                lineCount++;
            }
            // Safety: if no progress was made, take the rest
            if (pos == start) pos = remaining.Length;

            tb.AppendText(remaining[start..pos]);
            start = pos;

            // Yield to dispatcher — lets UI process input, render spinner, etc.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        if (!ct.IsCancellationRequested)
            loading.Visibility = Visibility.Collapsed;
    }

    // ── Area diff helpers ──────────────────────────────────────────

    private sealed record AreaParsed(string? Name, string? IngameName, Dictionary<string, string> Tasks);

    /// <summary>
    /// Parses a multi-line area block into name, ingameName, and individual task blocks.
    /// </summary>
    private static AreaParsed ParseAreaBlock(string block)
    {
        string? name = null, ingameName = null;
        var tasks = new Dictionary<string, string>();

        // Extract top-level simple fields
        var nameMatch = System.Text.RegularExpressions.Regex.Match(block, @"name\s*=\s*""([^""]*)""");
        if (nameMatch.Success) name = nameMatch.Groups[1].Value;

        var ingameMatch = System.Text.RegularExpressions.Regex.Match(block, @"ingameName\s*=\s*""([^""]*)""");
        if (ingameMatch.Success) ingameName = ingameMatch.Groups[1].Value;

        // Find "tasks = {" section and extract individual task blocks
        var tasksStart = block.IndexOf("tasks = {", StringComparison.Ordinal);
        if (tasksStart < 0) return new AreaParsed(name, ingameName, tasks);

        // Find individual task entries within the tasks section
        var taskPattern = new System.Text.RegularExpressions.Regex(@"\[""([^""]+)""\]\s*=\s*\{");
        int searchFrom = tasksStart + "tasks = {".Length;

        // First, find the end of the tasks section using brace depth
        int tasksBlockStart = block.IndexOf('{', tasksStart);
        if (tasksBlockStart < 0) return new AreaParsed(name, ingameName, tasks);

        int depth = 1;
        bool inStr = false;
        int pos = tasksBlockStart + 1;
        while (pos < block.Length && depth > 0)
        {
            char c = block[pos];
            if (c == '"' && (pos == 0 || block[pos - 1] != '\\')) inStr = !inStr;
            if (!inStr)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            pos++;
        }
        var tasksSection = block[tasksBlockStart..pos];

        // Now extract individual tasks from within the tasks section
        var taskMatches = taskPattern.Matches(tasksSection);
        for (int m = 0; m < taskMatches.Count; m++)
        {
            var taskId = taskMatches[m].Groups[1].Value;
            var bracePos = taskMatches[m].Index + taskMatches[m].Length - 1;

            int d = 1;
            bool inS = false;
            int j = bracePos + 1;
            while (j < tasksSection.Length && d > 0)
            {
                char c = tasksSection[j];
                if (c == '"' && (j == 0 || tasksSection[j - 1] != '\\')) inS = !inS;
                if (!inS)
                {
                    if (c == '{') d++;
                    else if (c == '}') d--;
                }
                j++;
            }

            if (d == 0)
                tasks.TryAdd(taskId, tasksSection[bracePos..j]);
        }

        return new AreaParsed(name, ingameName, tasks);
    }

    /// <summary>
    /// Parses a task block into key-value fields.
    /// Handles nested braces and quoted strings (multi-line aware).
    /// </summary>
    private static Dictionary<string, string> ParseTaskBlock(string block)
    {
        var fields = new Dictionary<string, string>();
        if (block.Length < 2 || block[0] != '{') return fields;

        var inner = block[1..^1];

        // Split on commas at depth 0
        var parts = new List<string>();
        int depth = 0;
        bool inString = false;
        int start = 0;

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '"' && (i == 0 || inner[i - 1] != '\\')) inString = !inString;
            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(inner[start..i].Trim());
                    start = i + 1;
                }
            }
        }
        if (start < inner.Length)
            parts.Add(inner[start..].Trim());

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Match: key = value or ["key"] = value
            var eqIdx = trimmed.IndexOf(" = ", StringComparison.Ordinal);
            if (eqIdx > 0)
            {
                var key = trimmed[..eqIdx].Trim().Trim('[', ']', '"');
                var val = trimmed[(eqIdx + 3)..].Trim();
                fields[key] = val;
            }
        }

        return fields;
    }

    /// <summary>
    /// Builds task-level diff UI for a modified area entry.
    /// Shows added/removed/modified tasks with field-level details.
    /// </summary>
    private void BuildAreaModifiedDetail(StackPanel target, ModifiedEntry mod,
        Brush secondary, Brush tertiary, bool defaultExpanded,
        Dictionary<string, string>? itemNameMap = null)
    {
        var greenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0xB8, 0x4F));
        var redBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0x50, 0x60));
        var orangeBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25));

        var wikiArea = ParseAreaBlock(mod.WikiValue);
        var localArea = ParseAreaBlock(mod.LocalValue);

        var areaName = localArea.Name ?? wikiArea.Name;
        var diffPanel = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };

        // Compare top-level fields (skip whitespace-only diffs)
        if (wikiArea.Name != localArea.Name && localArea.Name != null &&
            (wikiArea.Name ?? "").Trim() != localArea.Name.Trim())
        {
            var tb = new WpfTextBlock { FontSize = 10, Foreground = secondary, Margin = new Thickness(0, 1, 0, 1) };
            tb.Inlines.Add(new System.Windows.Documents.Run("Name: ") { Foreground = tertiary });
            tb.Inlines.Add(new System.Windows.Documents.Run(wikiArea.Name ?? "(none)") { Foreground = redBrush });
            tb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = tertiary });
            tb.Inlines.Add(new System.Windows.Documents.Run(localArea.Name) { Foreground = greenBrush });
            diffPanel.Children.Add(tb);
        }

        if (wikiArea.IngameName != localArea.IngameName && localArea.IngameName != null &&
            (wikiArea.IngameName ?? "").Trim() != localArea.IngameName.Trim())
        {
            var tb = new WpfTextBlock { FontSize = 10, Foreground = secondary, Margin = new Thickness(0, 1, 0, 1) };
            tb.Inlines.Add(new System.Windows.Documents.Run("Ingame Name: ") { Foreground = tertiary });
            tb.Inlines.Add(new System.Windows.Documents.Run(wikiArea.IngameName ?? "(none)") { Foreground = redBrush });
            tb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = tertiary });
            tb.Inlines.Add(new System.Windows.Documents.Run(localArea.IngameName) { Foreground = greenBrush });
            diffPanel.Children.Add(tb);
        }

        // Build task info for sorting (index + parents from local, fallback wiki)
        var allTaskInfo = new Dictionary<string, (int Index, List<string> Parents)>(StringComparer.Ordinal);
        foreach (var kv in localArea.Tasks.Concat(wikiArea.Tasks))
        {
            if (allTaskInfo.ContainsKey(kv.Key)) continue;
            var f = ParseTaskBlock(kv.Value);
            var idx = f.TryGetValue("index", out var iv) && int.TryParse(iv, out var n) ? n : int.MaxValue;
            var parents = new List<string>();
            if (f.TryGetValue("parents", out var pv))
                parents = System.Text.RegularExpressions.Regex.Matches(pv, @"""([^""]+)""")
                    .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
            allTaskInfo[kv.Key] = (idx, parents);
        }

        // Sort by lowest parent index, then own index (Lua sortTasksByLowestParentIndex)
        int MinParentIndex(string tid)
        {
            if (!allTaskInfo.TryGetValue(tid, out var info)) return int.MaxValue;
            if (info.Parents.Count == 0) return int.MinValue;
            var min = int.MaxValue;
            foreach (var pid in info.Parents)
                if (allTaskInfo.TryGetValue(pid, out var p) && p.Index < min) min = p.Index;
            return min;
        }

        List<string> SortTaskIds(IEnumerable<string> ids) =>
            ids.OrderBy(MinParentIndex)
               .ThenBy(id => allTaskInfo.TryGetValue(id, out var info) ? info.Index : int.MaxValue)
               .ToList();

        // Clickable task header — only the task ID part is interactive (hover underline + click to copy)
        WpfTextBlock ClickableTaskHeader(string prefix, string tid, Brush brush, FontWeight? weight = null)
        {
            var indexStr = allTaskInfo.TryGetValue(tid, out var info) && info.Index < int.MaxValue
                ? $"#{info.Index} " : "";

            var tb = new WpfTextBlock
            {
                FontSize = 10, Foreground = brush,
                FontWeight = weight ?? FontWeights.Normal,
                Margin = new Thickness(0, prefix == "~" ? 2 : 1, 0, 1)
            };
            tb.Inlines.Add(new System.Windows.Documents.Run($"{prefix} Task {indexStr}"));

            var idRun = new System.Windows.Documents.Run(tid)
            {
                Cursor = System.Windows.Input.Cursors.Hand
            };
            idRun.MouseEnter += (s, e) => idRun.TextDecorations = TextDecorations.Underline;
            idRun.MouseLeave += (s, e) => idRun.TextDecorations = null;
            System.Windows.Documents.Run? copiedRun = null;
            idRun.MouseLeftButtonDown += (s, e) =>
            {
                App.NativeSetClipboardText(tid);
                if (copiedRun != null) return;
                copiedRun = new System.Windows.Documents.Run("  Copied!")
                {
                    FontWeight = FontWeights.Normal, FontSize = 9,
                    Foreground = tertiary
                };
                tb.Inlines.Add(copiedRun);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (ts, te) =>
                {
                    tb.Inlines.Remove(copiedRun);
                    copiedRun = null;
                    ((DispatcherTimer)ts!).Stop();
                };
                timer.Start();
            };
            tb.Inlines.Add(idRun);
            return tb;
        }

        // Added tasks
        var addedTasks = SortTaskIds(localArea.Tasks.Keys.Except(wikiArea.Tasks.Keys));
        foreach (var taskId in addedTasks)
            diffPanel.Children.Add(ClickableTaskHeader("+", taskId, greenBrush));

        // Removed tasks
        var removedTasks = SortTaskIds(wikiArea.Tasks.Keys.Except(localArea.Tasks.Keys));
        foreach (var taskId in removedTasks)
            diffPanel.Children.Add(ClickableTaskHeader("\u2212", taskId, redBrush));

        // Modified tasks
        int modifiedTaskCount = 0;
        var commonTasks = SortTaskIds(localArea.Tasks.Keys.Intersect(wikiArea.Tasks.Keys));
        foreach (var taskId in commonTasks)
        {
            var wikiBlock = wikiArea.Tasks[taskId];
            var localBlock = localArea.Tasks[taskId];

            if (NormalizeAreaBlock(wikiBlock) == NormalizeAreaBlock(localBlock)) continue;

            var wikiFields = ParseTaskBlock(wikiBlock);
            var localFields = ParseTaskBlock(localBlock);
            var allKeys = new SortedSet<string>(wikiFields.Keys);
            allKeys.UnionWith(localFields.Keys);

            var hasChanges = false;
            var taskPanel = new StackPanel { Margin = new Thickness(14, 0, 0, 2) };

            foreach (var field in allKeys)
            {
                var hasWiki = wikiFields.TryGetValue(field, out var wikiVal);
                var hasLocal = localFields.TryGetValue(field, out var localVal);

                if (hasWiki && hasLocal && wikiVal == localVal) continue;

                var label = FormatAreaFieldName(field);

                if (hasWiki && hasLocal)
                {
                    var fmtWiki = FormatAreaValue(wikiVal!, field, itemNameMap);
                    var fmtLocal = FormatAreaValue(localVal!, field, itemNameMap);

                    // When formatted values look identical, reveal whitespace
                    if (fmtWiki == fmtLocal)
                    {
                        fmtWiki = RevealWhitespace(wikiVal!.Trim('"'));
                        fmtLocal = RevealWhitespace(localVal!.Trim('"'));
                        if (fmtWiki == fmtLocal) continue;
                    }

                    hasChanges = true;
                    var tb = new WpfTextBlock
                    {
                        FontSize = 10, Foreground = secondary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    };
                    tb.Inlines.Add(new System.Windows.Documents.Run($"{label}: ") { Foreground = tertiary });
                    tb.Inlines.Add(new System.Windows.Documents.Run(fmtWiki) { Foreground = redBrush });
                    tb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = tertiary });
                    tb.Inlines.Add(new System.Windows.Documents.Run(fmtLocal) { Foreground = greenBrush });
                    taskPanel.Children.Add(tb);
                }
                else if (hasLocal)
                {
                    hasChanges = true;
                    taskPanel.Children.Add(new WpfTextBlock
                    {
                        Text = $"+ {label}: {FormatAreaValue(localVal!, field, itemNameMap)}",
                        FontSize = 10, Foreground = greenBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
                else
                {
                    hasChanges = true;
                    taskPanel.Children.Add(new WpfTextBlock
                    {
                        Text = $"\u2212 {label}: {FormatAreaValue(wikiVal!, field, itemNameMap)}",
                        FontSize = 10, Foreground = redBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
            }

            if (hasChanges)
            {
                modifiedTaskCount++;
                diffPanel.Children.Add(ClickableTaskHeader("~", taskId, orangeBrush, FontWeights.SemiBold));
                diffPanel.Children.Add(taskPanel);
            }
        }

        // Build area header with task change counts
        var headerParts = new List<string>();
        if (modifiedTaskCount > 0) headerParts.Add($"{modifiedTaskCount} modified");
        if (addedTasks.Count > 0) headerParts.Add($"+{addedTasks.Count} new");
        if (removedTasks.Count > 0) headerParts.Add($"\u2212{removedTasks.Count} removed");
        var countSuffix = headerParts.Count > 0 ? $" — {string.Join(", ", headerParts)}" : "";

        // Header: use mod.Key as primary; append areaName only if different
        var headerLabel = mod.Key;
        if (areaName != null && areaName != mod.Key)
            headerLabel = $"{mod.Key}  ({areaName})";

        if (diffPanel.Children.Count > 0)
            AddCollapsibleSection(target, $"{headerLabel}{countSuffix}",
                orangeBrush, secondary, diffPanel, defaultExpanded);
        else
        {
            target.Children.Add(new WpfTextBlock
            {
                Text = $"~ {headerLabel} — whitespace only",
                FontSize = 11, Foreground = tertiary, Margin = new Thickness(0, 2, 0, 2)
            });
        }
    }

    private static string FormatAreaFieldName(string field) => field switch
    {
        "index" => "Index",
        "id" => "ID",
        "desc" => "Description",
        "rewards" => "Rewards",
        "parents" => "Parents",
        "children" => "Children",
        "requirements" => "Requirements",
        "name" => "Name",
        "ingameName" => "Ingame Name",
        "release" => "Release Date",
        "unlock" => "Unlock Date",
        _ => field
    };

    private static string FormatAreaValue(string val, string? fieldName = null,
        Dictionary<string, string>? itemNameMap = null)
    {
        if (val == "nil") return "(none)";
        if (val.StartsWith('"') && val.EndsWith('"'))
        {
            var s = val[1..^1];
            if (s.Length > 100) s = s[..97] + "...";
            return s;
        }

        // Requirements: {{name = "Item_03", amount = 1}, {name = "Item_04", amount = 2}}
        if (fieldName == "requirements")
        {
            var reqMatches = System.Text.RegularExpressions.Regex.Matches(
                val, @"name\s*=\s*""([^""]+)"",\s*amount\s*=\s*(\d+)");
            if (reqMatches.Count > 0)
            {
                var items = reqMatches.Select(m =>
                {
                    var itemId = m.Groups[1].Value;
                    var amount = m.Groups[2].Value;
                    var display = ResolveItemDisplay(itemId, itemNameMap);
                    return $"{amount}x {display}";
                });
                return string.Join(", ", items);
            }
        }

        // Rewards: {xp = 50, item = "ChestItem_01"}
        if (fieldName == "rewards")
        {
            var parts = new List<string>();
            var xpMatch = System.Text.RegularExpressions.Regex.Match(val, @"xp\s*=\s*(\d+)");
            if (xpMatch.Success) parts.Add($"{xpMatch.Groups[1].Value} XP");
            var itemMatch = System.Text.RegularExpressions.Regex.Match(val, @"item\s*=\s*""([^""]+)""");
            if (itemMatch.Success)
                parts.Add(ResolveItemDisplay(itemMatch.Groups[1].Value, itemNameMap));
            if (parts.Count > 0) return string.Join(", ", parts);
        }

        // Trim nested table display
        var clean = val.TrimStart('{').TrimEnd('}').Trim();
        if (clean.Length > 100) clean = clean[..97] + "...";
        return string.IsNullOrEmpty(clean) ? "(empty)" : clean;
    }

    /// <summary>
    /// Resolves an item ID to "Display Name [L#]" using the item name map from generated Lua.
    /// Falls back to raw itemId if not found.
    /// </summary>
    private static string ResolveItemDisplay(string itemId, Dictionary<string, string>? itemNameMap)
    {
        if (itemNameMap == null) return itemId;

        // itemNameMap is itemType → name (e.g. "GardenGloves_03" → "Garden Gloves")
        if (itemNameMap.TryGetValue(itemId, out var name))
        {
            // Extract level from itemId suffix (e.g. "GardenGloves_03" → 3)
            var lastUnderscore = itemId.LastIndexOf('_');
            if (lastUnderscore >= 0 && int.TryParse(itemId[(lastUnderscore + 1)..], out var level))
                return $"{name} [L{level}]";
            return name;
        }
        return itemId;
    }

    // ── Changelog ────────────────────────────────────────────────────

    /// <summary>
    /// Populates a StackPanel with the full changelog detail (modified with field diffs, added, removed).
    /// Each category is a separate collapsible section. Shared by both inline card and confirmation dialog.
    /// Initial batch of 50 items shown, with "Show all N items..." link to reveal the rest.
    /// </summary>
    private void BuildChangelogDetail(StackPanel root, ChangelogData cl,
        Brush primary, Brush secondary, Brush tertiary,
        Dictionary<string, string>? nameMap = null, bool isAreaMode = false)
    {
        const int initialCount = 50;
        var greenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0xB8, 0x4F));
        var redBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0x50, 0x60));
        var orangeBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25));

        string DisplayName(string key) =>
            nameMap?.GetValueOrDefault(key) is string dn ? $"{dn}  ({key})" : key;

        // Helper: build a single modified entry with field-level diffs
        void AddModifiedEntry(StackPanel target, ModifiedEntry mod)
        {
            target.Children.Add(new WpfTextBlock
            {
                Text = $"~ {DisplayName(mod.Key)}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = orangeBrush, Margin = new Thickness(0, 3, 0, 2)
            });

            var wikiFields = ParseLuaEntryFields(mod.WikiValue);
            var localFields = ParseLuaEntryFields(mod.LocalValue);
            var allKeys = new SortedSet<string>(wikiFields.Keys);
            allKeys.UnionWith(localFields.Keys);

            var diffPanel = new StackPanel { Margin = new Thickness(14, 0, 0, 4) };

            foreach (var field in allKeys)
            {
                var hasWiki = wikiFields.TryGetValue(field, out var wikiVal);
                var hasLocal = localFields.TryGetValue(field, out var localVal);

                if (hasWiki && hasLocal && wikiVal == localVal) continue;

                var label = FormatFieldName(field);

                if (hasWiki && hasLocal)
                {
                    var fmtWiki = FormatLuaValue(wikiVal!, field, nameMap);
                    var fmtLocal = FormatLuaValue(localVal!, field, nameMap);

                    // When formatted values look identical, reveal whitespace from raw values
                    if (fmtWiki == fmtLocal)
                    {
                        fmtWiki = RevealWhitespace(wikiVal!.Trim('"'));
                        fmtLocal = RevealWhitespace(localVal!.Trim('"'));
                    }

                    var tb = new WpfTextBlock
                    {
                        FontSize = 10, Foreground = secondary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    };
                    tb.Inlines.Add(new System.Windows.Documents.Run($"{label}: ")
                        { Foreground = tertiary });
                    tb.Inlines.Add(new System.Windows.Documents.Run(fmtWiki)
                        { Foreground = redBrush });
                    tb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ")
                        { Foreground = tertiary });
                    tb.Inlines.Add(new System.Windows.Documents.Run(fmtLocal)
                        { Foreground = greenBrush });
                    diffPanel.Children.Add(tb);
                }
                else if (hasLocal)
                {
                    diffPanel.Children.Add(new WpfTextBlock
                    {
                        Text = $"+ {label}: {FormatLuaValue(localVal!, field, nameMap)}",
                        FontSize = 10, Foreground = greenBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
                else
                {
                    diffPanel.Children.Add(new WpfTextBlock
                    {
                        Text = $"\u2212 {label}: {FormatLuaValue(wikiVal!, field, nameMap)}",
                        FontSize = 10, Foreground = redBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
            }

            if (diffPanel.Children.Count > 0)
                target.Children.Add(diffPanel);
        }

        // Helper: add simple name entries (for Added / Removed)
        void AddSimpleEntries(StackPanel target, IEnumerable<string> items, string prefix, Brush brush)
        {
            foreach (var name in items)
                target.Children.Add(new WpfTextBlock
                {
                    Text = $"  {prefix} {DisplayName(name)}", FontSize = 11,
                    Foreground = brush, Margin = new Thickness(0, 1, 0, 1)
                });
        }

        // Helper: add a "Show all N items..." link + hidden panel with remaining items
        void AddShowAllLink(StackPanel target, int totalCount, Brush linkBrush, Action<StackPanel> buildRemaining)
        {
            var morePanel = new StackPanel { Visibility = Visibility.Collapsed };
            buildRemaining(morePanel);

            var showAll = new WpfTextBlock
            {
                Text = $"  Show all {totalCount} items...",
                FontSize = 11, Foreground = linkBrush,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 4, 0, 2)
            };
            showAll.TextDecorations = TextDecorations.Underline;
            showAll.MouseLeftButtonDown += (_, _) =>
            {
                morePanel.Visibility = Visibility.Visible;
                showAll.Visibility = Visibility.Collapsed;
            };

            target.Children.Add(showAll);
            target.Children.Add(morePanel);
        }

        if (cl.Modified.Count > 0)
        {
            var modContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            if (isAreaMode)
            {
                var expandAreas = cl.Modified.Count <= 10;
                foreach (var mod in cl.Modified)
                    BuildAreaModifiedDetail(modContent, mod, secondary, tertiary, expandAreas, nameMap);
            }
            else
            {
                foreach (var mod in cl.Modified.Take(initialCount))
                    AddModifiedEntry(modContent, mod);

                if (cl.Modified.Count > initialCount)
                    AddShowAllLink(modContent, cl.Modified.Count, secondary,
                        panel => { foreach (var mod in cl.Modified.Skip(initialCount)) AddModifiedEntry(panel, mod); });
            }

            AddCollapsibleSection(root, $"Modified ({cl.Modified.Count})", orangeBrush, secondary, modContent);
        }

        if (cl.Added.Count > 0)
        {
            var addContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            AddSimpleEntries(addContent, cl.Added.Take(initialCount), "+", greenBrush);

            if (cl.Added.Count > initialCount)
                AddShowAllLink(addContent, cl.Added.Count, secondary,
                    panel => AddSimpleEntries(panel, cl.Added.Skip(initialCount), "+", greenBrush));

            AddCollapsibleSection(root, $"Added ({cl.Added.Count})", greenBrush, secondary, addContent);
        }

        if (cl.Removed.Count > 0)
        {
            var remContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            AddSimpleEntries(remContent, cl.Removed.Take(initialCount), "\u2212", redBrush);

            if (cl.Removed.Count > initialCount)
                AddShowAllLink(remContent, cl.Removed.Count, secondary,
                    panel => AddSimpleEntries(panel, cl.Removed.Skip(initialCount), "\u2212", redBrush));

            AddCollapsibleSection(root, $"Removed ({cl.Removed.Count})", redBrush, secondary, remContent);
        }

        // Renamed (CBE event rename: CBE_Easter2025_Foo_NN \u2194 CBE_SweetMess_Foo_NN). Items are shown
        // here instead of in Removed/Added so the user sees they're not lost \u2014 just relocated.
        // Renamed items are excluded from the archive (they live in the new id).
        var renamedList = cl.Renamed;
        if (renamedList != null && renamedList.Count > 0)
        {
            var blueBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0xA0, 0xE8));
            var renContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            void AddRenamedEntries(StackPanel panel, IEnumerable<RenamedEntry> entries)
            {
                foreach (var r in entries)
                {
                    var line = new WpfTextBlock
                    {
                        FontSize = 11, Foreground = blueBrush,
                        Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap
                    };
                    line.Inlines.Add(new System.Windows.Documents.Run("\u21bb ") { Foreground = blueBrush });
                    line.Inlines.Add(new System.Windows.Documents.Run(r.OldId) { Foreground = redBrush });
                    line.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = secondary });
                    line.Inlines.Add(new System.Windows.Documents.Run(r.NewId) { Foreground = greenBrush });
                    if (!string.IsNullOrEmpty(r.OldChain) && r.OldChain != r.NewChain)
                    {
                        line.Inlines.Add(new System.Windows.Documents.Run($"  ({r.OldChain} \u2192 {r.NewChain ?? "?"})")
                        { Foreground = tertiary, FontSize = 10 });
                    }
                    panel.Children.Add(line);
                }
            }
            AddRenamedEntries(renContent, renamedList.Take(initialCount));
            if (renamedList.Count > initialCount)
                AddShowAllLink(renContent, renamedList.Count, secondary,
                    panel => AddRenamedEntries(panel, renamedList.Skip(initialCount)));

            AddCollapsibleSection(root, $"Renamed ({renamedList.Count})", blueBrush, secondary, renContent);
        }

        // Archived: items preserved either in Module:Datatable/Items/Archive (full data backed up) or
        // in Module:Datatable/Items/Mapping (override + enrichment). Reassures user nothing was silently lost.
        var archivedList = cl.Archived;
        if (archivedList != null && archivedList.Count > 0)
        {
            var goldBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xA8, 0x4A));
            var archContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            void AddArchivedEntries(StackPanel panel, IEnumerable<ArchivedEntry> entries)
            {
                foreach (var a in entries)
                {
                    var line = new WpfTextBlock
                    {
                        FontSize = 11, Foreground = primary,
                        Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap
                    };
                    line.Inlines.Add(new System.Windows.Documents.Run("📦 ") { Foreground = goldBrush });
                    line.Inlines.Add(new System.Windows.Documents.Run(a.Id) { Foreground = primary });
                    var whereLabel = a.Where == "archive" ? "→ Archive" : "→ Mapping";
                    line.Inlines.Add(new System.Windows.Documents.Run($"  {whereLabel}")
                    { Foreground = goldBrush, FontSize = 10 });
                    if (!string.IsNullOrEmpty(a.Chain))
                        line.Inlines.Add(new System.Windows.Documents.Run($"  ({a.Chain})")
                        { Foreground = tertiary, FontSize = 10 });
                    panel.Children.Add(line);
                }
            }
            AddArchivedEntries(archContent, archivedList.Take(initialCount));
            if (archivedList.Count > initialCount)
                AddShowAllLink(archContent, archivedList.Count, secondary,
                    panel => AddArchivedEntries(panel, archivedList.Skip(initialCount)));

            AddCollapsibleSection(root, $"Archived ({archivedList.Count})", goldBrush, secondary, archContent);
        }
    }

    private static void AddCollapsibleSection(StackPanel root, string headerText,
        Brush accentBrush, Brush secondaryBrush, StackPanel content, bool defaultExpanded = false)
    {
        var collapsed = !defaultExpanded;
        var arrow = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = collapsed
                ? Wpf.Ui.Controls.SymbolRegular.ChevronRight24
                : Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = secondaryBrush, Margin = new Thickness(0, 0, 6, 0)
        };
        var headerTb = new WpfTextBlock
        {
            Text = headerText, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = accentBrush, VerticalAlignment = VerticalAlignment.Center
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        header.Children.Add(arrow);
        header.Children.Add(headerTb);

        content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        header.MouseLeftButtonDown += (_, _) =>
        {
            if (content.Visibility == Visibility.Collapsed)
            {
                content.Visibility = Visibility.Visible;
                arrow.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
            }
            else
            {
                content.Visibility = Visibility.Collapsed;
                arrow.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronRight24;
            }
        };

        root.Children.Add(header);
        root.Children.Add(content);
    }

    /// <summary>
    /// Parses top-level key = value fields from a Lua table entry like {k1 = v1, k2 = v2}.
    /// Handles nested braces and quoted strings.
    /// </summary>
    private static Dictionary<string, string> ParseLuaEntryFields(string entry)
    {
        var fields = new Dictionary<string, string>();
        if (entry.Length < 2 || entry[0] != '{') return fields;

        var inner = entry[1..^1].Trim();

        var parts = new List<string>();
        int depth = 0;
        bool inString = false;
        int start = 0;

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '"' && (i == 0 || inner[i - 1] != '\\')) inString = !inString;
            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(inner[start..i].Trim());
                    start = i + 1;
                }
            }
        }
        if (start < inner.Length)
            parts.Add(inner[start..].Trim());

        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf(" = ", StringComparison.Ordinal);
            if (eqIdx > 0)
            {
                var key = part[..eqIdx].Trim().Trim('[', ']', '"');
                var val = part[(eqIdx + 3)..].Trim();
                fields[key] = val;
            }
        }

        return fields;
    }

    /// <summary>
    /// Builds itemType → displayName map. Prefers wiki-mapped names from DataService.Chains
    /// (which has wiki mapping applied). Falls back to parsing generated items Lua (raw names).
    /// </summary>
    private Dictionary<string, string> BuildItemNameMap()
    {
        var map = new Dictionary<string, string>();

        // Prefer wiki-mapped names from loaded DataService
        var ds = _main.DataService;
        if (ds != null && ds.Chains.Count > 0)
        {
            foreach (var chain in ds.Chains)
                foreach (var item in chain.Items)
                    if (!string.IsNullOrEmpty(item.ItemType))
                        map.TryAdd(item.ItemType, chain.DisplayName);
            return map;
        }

        // Fallback: parse raw names from generated items Lua
        var pattern = @"\[""([^""]+)""\] = \{name = ""([^""]+)""";

        if (!string.IsNullOrEmpty(_lastCombined))
        {
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(_lastCombined, pattern))
                map.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }
        else
        {
            foreach (var chunk in _lastItemChunks)
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(chunk.Lua, pattern))
                    map.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }

        return map;
    }

    private static string FormatFieldName(string field) => field switch
    {
        "name" => "Name",
        "level" => "Level",
        "isGen" => "Generator",
        "isTemp" => "Temporary",
        "chainName" => "Chain",
        "bubble" => "Bubble",
        "odds" => "Drop odds",
        "desc" => "Description",
        _ => field
    };

    private static string FormatLuaValue(string val, string fieldName,
        Dictionary<string, string>? nameMap = null)
    {
        if (val == "nil") return "(none)";
        if (val == "true") return "yes";
        if (val == "false") return "no";

        // Quoted string
        if (val.StartsWith('"') && val.EndsWith('"'))
        {
            var s = val[1..^1];
            if (fieldName == "desc" && s.Length > 80)
                s = s[..77] + "...";
            return s;
        }

        // Simple number
        if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return val;

        // Odds array: {{id = "X", value = 0.5}, ...}
        if (fieldName == "odds")
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                val, @"id\s*=\s*""([^""]+)"",\s*value\s*=\s*([0-9.eE+\-]+)");
            if (matches.Count > 0)
            {
                var items = matches.Select(m =>
                {
                    var id = m.Groups[1].Value;
                    var display = nameMap?.GetValueOrDefault(id) ?? id;
                    if (double.TryParse(m.Groups[2].Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        // Values ≤1 are probabilities → show as %, otherwise show raw weight
                        if (v <= 1.0)
                        {
                            var pct = v * 100;
                            return pct == Math.Floor(pct)
                                ? $"{display} {pct:F0}%"
                                : $"{display} {pct:F1}%";
                        }
                        return $"{display} (\u00D7{v:F0})";
                    }
                    return $"{display} {m.Groups[2].Value}";
                });
                return string.Join(", ", items);
            }
        }

        // Bubble: {duration = N, cost = N, spawnOdds = N}
        if (fieldName == "bubble")
        {
            var dur = System.Text.RegularExpressions.Regex.Match(val, @"duration\s*=\s*(\d+)");
            var cost = System.Text.RegularExpressions.Regex.Match(val, @"cost\s*=\s*(\d+)");
            var odds = System.Text.RegularExpressions.Regex.Match(val, @"spawnOdds\s*=\s*(\d+)");
            if (dur.Success)
            {
                var parts = new List<string>();
                var mins = int.Parse(dur.Groups[1].Value);
                parts.Add(mins >= 60
                    ? $"{mins / 60}h{(mins % 60 > 0 ? $"{mins % 60}m" : "")}"
                    : $"{mins}m");
                if (cost.Success) parts.Add($"cost {cost.Groups[1].Value}");
                if (odds.Success) parts.Add($"spawn {odds.Groups[1].Value}%");
                return string.Join(", ", parts);
            }
        }

        // Generic table — strip outer braces and ["..."] syntax
        var clean = val
            .Replace("[\"", "").Replace("\"]", "")
            .TrimStart('{').TrimEnd('}').Trim();
        if (clean.Length > 100) clean = clean[..97] + "...";
        return clean;
    }

    /// <summary>
    /// Makes leading/trailing whitespace visible using · markers.
    /// Used when formatted diff values look identical but raw values differ.
    /// </summary>
    private static string RevealWhitespace(string s)
    {
        var leading = s.Length - s.TrimStart().Length;
        var trailing = s.Length - s.TrimEnd().Length;
        if (leading == 0 && trailing == 0)
            return $"\"{s}\"";
        var trimmed = s[leading..(s.Length - trailing)];
        return $"\"{new string('\u00B7', leading)}{trimmed}{new string('\u00B7', trailing)}\"";
    }

    // ── Copy ─────────────────────────────────────────────────────────

    private void CopyChunk(int index)
    {
        if (index >= 0 && index < _lastChunks.Count)
        {
            App.NativeSetClipboardText(_lastChunks[index].Lua);
            ShowInfo($"Chunk \"{_lastChunks[index].Label}\" copied to clipboard.", InfoBarSeverity.Success);
        }
    }

    private void BtnCopyCombined_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastCombined))
        {
            App.NativeSetClipboardText(_lastCombined);
            ShowInfo("Items + Chain Names copied to clipboard.", InfoBarSeverity.Success);
        }
        else if (_lastItemChunks.Count > 0)
        {
            // Multi-chunk: copy all chunks concatenated (for manual use)
            var all = string.Join("\n\n", _lastItemChunks.Select(c => c.Lua));
            App.NativeSetClipboardText(all);
            ShowInfo($"All {_lastItemChunks.Count} item chunks copied to clipboard.", InfoBarSeverity.Success);
        }
    }

    // ── Preview dialog helper ────────────────────────────────────────

    /// <summary>
    /// Creates a preview confirmation dialog with:
    /// - Screen-based MaxHeight (prevents overflow off screen)
    /// - SizeToContent.Height (auto-grows on section expand)
    /// - Content should use DockPanel with changelog as fill child (see Build*Preview methods)
    /// </summary>
    private WpfMessageBox CreatePreviewDialog(string title, UIElement content, string primaryButton)
    {
        var owner = Window.GetWindow(this);
        var screenHeight = SystemParameters.WorkArea.Height;

        var dialog = new WpfMessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButton,
            CloseButtonText = "Cancel",
            Owner = owner,
            MinWidth = 540,
            SizeToContent = SizeToContent.Height,
            MaxHeight = screenHeight * 0.88,
        };

        dialog.Loaded += (_, _) =>
        {
            dialog.Top = Math.Max(owner.Top + 30, dialog.Top - owner.ActualHeight * 0.12);
        };

        ApplicationThemeManager.Apply(dialog);
        return dialog;
    }

    // ── InfoBar ──────────────────────────────────────────────────────

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }
}
