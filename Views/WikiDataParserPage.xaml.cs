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

    // Events schedule state
    private string? _lastEventsLua;
    // Garage Cleanup grids (detected during Generate Events; surfaced in the Update dialog)
    private string? _lastGcGridsLua;
    private List<GarageCleanupGridService.GridChange>? _lastGcChanges;
    private int _lastGcRewardCount;   // # of GC events with resolved reward tables (0 until events.json re-dumped)

    // Pending push state — captured during Generate Events, consumed by Update Wiki (push-only).
    // Null means Generate Events has not been run (or was aborted), which blocks Update Wiki.
    private string? _pendingEventsExisting;       // live Module:Datatable/Events content at generate time (null = not found)
    private string? _pendingVariousContent;       // spliced Module:Datatable/Various to push (null = no GC changes)
    private string? _pendingEventsBaseTs;         // revision timestamp of fetched Events (basetimestamp conflict guard)
    private string? _pendingVariousBaseTs;        // revision timestamp of fetched Various (basetimestamp conflict guard)
    private List<string>? _pendingGcChangedBases; // GC bases whose grid keys changed → auto-update their event pages
    private int _pendingGcGroupCount;             // # GC EventScheduleGroups appended (for success message)
    private int _pendingGcWritten;                // # net-new GC airings added (for success message)

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
    private bool _eventsCollapsed;

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
        UpdateEventsWikiButtonState();
        _main.WikiVerifiedChanged += OnWikiVerifiedChanged;
    }

    private void OnWikiVerifiedChanged()
    {
        UpdateWikiButtonState();
        UpdateItemsWikiButtonState();
        UpdateEventsWikiButtonState();
    }

    // ── Status ──────────────────────────────────────────────────────

    public async void RefreshStatus()
    {
        // Set every row's status synchronously FIRST, so no row is left blank while the
        // async area count runs (the await below used to leave Events empty mid-refresh).
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

        var eventsPath = _main.Settings.EventsJsonPath;
        if (!string.IsNullOrEmpty(eventsPath) && File.Exists(eventsPath))
        {
            txtEventsStatus.Text = "Events: schedule source ready";
            btnEventsPath.Content = eventsPath;
            btnEventsPath.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrEmpty(eventsPath))
        {
            txtEventsStatus.Text = $"Events: file not found — {Path.GetFileName(eventsPath)}";
            btnEventsPath.Content = eventsPath;
            btnEventsPath.Visibility = Visibility.Visible;
        }
        else
        {
            txtEventsStatus.Text = "Events: no file configured — set in Settings";
            btnEventsPath.Visibility = Visibility.Collapsed;
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

            btnAreasPath.Content = areasPath;
            btnAreasPath.Visibility = Visibility.Visible;

            if (_cachedAreaCount == null)
            {
                // Unified busy indicator (spinner + verb) instead of bare "counting..." text.
                SetRowBusy(areasIdle, areasBusy, txtAreasBusy, true, "Counting areas…");
                _cachedAreaCount = await Task.Run(() => TryCountAreas(areasPath));
                SetRowBusy(areasIdle, areasBusy, txtAreasBusy, false);
            }

            txtAreasStatus.Text = _cachedAreaCount.HasValue
                ? $"Areas: {_cachedAreaCount.Value} areas"
                : $"Areas: {Path.GetFileName(areasPath)}";
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

    // ── Busy-state helpers (unified loading communication across all three rows) ──

    /// <summary>
    /// Toggles a toolbar row between its idle status panel and a busy panel
    /// (spinner + verb like "Generating…"). Keeps the three rows consistent.
    /// </summary>
    private static void SetRowBusy(UIElement idle, UIElement busy, WpfTextBlock verbText, bool isBusy, string verb = "")
    {
        busy.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        idle.Visibility = isBusy ? Visibility.Collapsed : Visibility.Visible;
        if (isBusy) verbText.Text = verb;
    }

    /// <summary>
    /// Enables/disables ALL three generate buttons. Called around any generation so the
    /// user gets a clear "wait" signal (greyed buttons) instead of being able to launch
    /// overlapping work while one job is already running.
    /// </summary>
    private void SetGenerateButtonsEnabled(bool enabled)
    {
        btnGenerateAreas.IsEnabled = enabled;
        btnGenerateItems.IsEnabled = enabled;
        btnGenerateEvents.IsEnabled = enabled;
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

    private void BtnEventsPath_Click(object sender, RoutedEventArgs e)
    {
        _main.NavigateToSettingsHighlightEvents();
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

        // Events schedule
        if (!string.IsNullOrEmpty(_lastEventsLua))
        {
            var (preview, remaining) = SplitForPreview(_lastEventsLua);
            txtEvents.Text = preview;
            eventsMiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;
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

        // Events schedule
        if (!string.IsNullOrEmpty(_lastEventsLua))
            _ = LazySetEventsFullTextAsync(_lastEventsLua, combinedCt);
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

    private void BtnCollapseEvents_Click(object sender, RoutedEventArgs e)
    {
        _eventsCollapsed = !_eventsCollapsed;
        eventsContent.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        iconCollapseEvents.Symbol = _eventsCollapsed
            ? Wpf.Ui.Controls.SymbolRegular.ChevronDown24
            : Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
    }

    private async Task LazySetEventsFullTextAsync(string lua, CancellationToken ct)
    {
        var (_, remaining) = SplitForPreview(lua);
        if (remaining == null) { eventsMiniLoading.Visibility = Visibility.Collapsed; return; }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(30);
        if (ct.IsCancellationRequested) return;

        await AppendChunkedAsync(txtEvents, remaining, eventsMiniLoading, ct);
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

    // ── Shared formatting helpers ────────────────────────────────────

    private static string FormatSize(long bytes)
    {
        return bytes > 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):F2} MB"
            : $"{bytes / 1024.0:F0} KB";
    }

    private static string? ExtractChainNameFromEntry(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"chainName\s*=\s*""([^""]+)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ── InfoBar ──────────────────────────────────────────────────────

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }
}
