using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Helpers;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class EventsPage : UserControl
{
    private readonly MainWindow _main;
    private EventService? _eventService;
    private CollectibleBoardEvent? _selectedEvent;
    private string? _lastSvg;
    private string? _lastSvgPath;
    private readonly Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(250));
    private List<object> _listItems = new();

    public EventsPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        _ = TryLoadAsync();

        _main.ChainDataLoaded += () => Dispatcher.InvokeAsync(async () =>
        {
            if (_eventService == null) return;
            // Re-resolves chains against the freshly loaded DataService — the getter
            // runs ResolveChains on the shared instance (serialized on MainWindow)
            await _main.GetEventServiceAsync();
            if (_selectedEvent != null)
                ShowEventDetail(_selectedEvent);
        });

        _main.EventsFileChanged += () => Dispatcher.InvokeAsync(async () =>
        {
            _eventService = null;
            await TryLoadAsync();
        });
    }

    private async Task TryLoadAsync()
    {
        try
        {
            // Shared per-path cache on MainWindow (single events.json parse app-wide);
            // resolves chains against the current DataService when loaded.
            // Returns null when the path is not configured or the file is missing.
            _eventService = await _main.GetEventServiceAsync();
            if (_eventService == null) return;

            BuildEventList();
        }
        catch (Exception ex)
        {
            ShowInfo($"Failed to load events: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Postaví seznam view-modelů (rok-headery + karty) a přiřadí ho jako ItemsSource
    /// virtualizovaného ItemsControl. Filtr logika 1:1 s původním ručním buildem.
    /// </summary>
    private void BuildEventList()
    {
        if (_eventService == null || _eventService.Events.Count == 0)
        {
            _listItems = new List<object>();
            eventListControl.ItemsSource = null;
            txtSummary.Text = "No events loaded.";
            return;
        }

        var searchText = txtSearch.Text?.Trim() ?? "";
        var filtered = _eventService.Events.AsEnumerable();
        if (!string.IsNullOrEmpty(searchText))
            filtered = filtered.Where(e =>
                e.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                e.EventId.Contains(searchText, StringComparison.OrdinalIgnoreCase));

        // Disabled boards (A/B variant "B", retired re-airs) are hidden unless the toggle is on.
        if (chkShowDisabled?.IsChecked != true)
            filtered = filtered.Where(e => e.IsEnabled);

        var events = filtered.ToList();
        txtSummary.Text = $"{events.Count} event{(events.Count != 1 ? "s" : "")}";

        // Group by year
        var exportDir = ResolveExportDir();
        var items = new List<object>(events.Count + 8);
        int? lastYear = null;
        foreach (var ev in events)
        {
            int year = ev.StartDate?.Year ?? 0;
            if (year != lastYear)
            {
                lastYear = year;
                items.Add(new EventYearHeaderItem { YearText = year > 0 ? year.ToString() : "Unknown" });
            }

            // IsSelected (== _selectedEvent) potlačuje hover; IsHighlighted (pozadí) se po
            // rebuildu neobnovuje — obojí 1:1 s původním chováním.
            var card = new EventCardItem(ev) { IsSelected = ev == _selectedEvent };
            if (exportDir != null) card.BadgePath = FindBadge(exportDir, ev.PrefabsOverride, ev.EventId);
            items.Add(card);
        }

        _listItems = items;
        eventListControl.ItemsSource = items;
    }

    /// <summary>Klik na kartu eventu v DataTemplate — výběr eventu + zvýraznění karty.</summary>
    private void EventCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventCardItem item) return;

        _selectedEvent = item.Event;
        ShowEventDetail(item.Event);

        // Highlight selected card
        foreach (var card in _listItems.OfType<EventCardItem>())
        {
            card.IsHighlighted = card == item;
            card.IsSelected = card == item;
        }
    }

    private void ShowEventDetail(CollectibleBoardEvent ev)
    {
        eventDetailPanel.Visibility = Visibility.Visible;
        txtEventName.Text = ev.DisplayName;

        var parts = new List<string> { ev.EventId };
        if (ev.StartDate.HasValue)
            parts.Add(ev.StartDate.Value.ToString("d MMMM yyyy"));
        if (ev.DurationDays.HasValue)
            parts.Add($"{ev.DurationDays} days");
        parts.Add($"{ev.Chains.Count} chains");
        txtEventInfo.Text = string.Join(" · ", parts);

        // Chain list
        chainExpander.Visibility = ev.Chains.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        chainListPanel.Children.Clear();
        foreach (var chain in ev.Chains.OrderBy(c => c.DisplayName))
        {
            chainListPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"{chain.DisplayName} ({chain.Items.Count} items)",
                FontSize = 12,
                Margin = new Thickness(4, 1, 0, 1),
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush")
            });
        }

        btnGenerateFlowchart.IsEnabled = ev.Chains.Count > 0;
        // Garage Cleanup section generator — available for any seasonal event; the click resolves
        // whether a "<event> Garage Cleanup" actually has grids in the live module.
        btnGenerateGc.IsEnabled = true;
        txtPlaceholder.Text = ev.Chains.Count > 0
            ? "Click 'Generate Flowchart' to build the item chain graph."
            : "No chains found for this event.";
    }

    /// <summary>
    /// Builds the full <c>== Garage Cleanup ==</c> section wikitext for the selected seasonal event:
    /// run years + level counts from the live <c>garageCleanupGrids</c>, and per-year STATIC reward
    /// tables computed from each year's dump (found by scanning the APK folders for the GC's
    /// Schedule.Start year). Copies to clipboard + saves next to the dump.
    /// </summary>
    private async void BtnGenerateGc_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEvent == null || _main.DataService == null) return;
        try
        {
            btnGenerateGc.IsEnabled = false;
            var baseName = _selectedEvent.DisplayName + " Garage Cleanup";
            ShowInfo("Building Garage Cleanup section…", InfoBarSeverity.Informational);

            var live = await WikiMappingService.FetchModuleContentAsync("Module:Datatable/Various");
            if (string.IsNullOrEmpty(live))
            {
                ShowInfo("Could not fetch live Module:Datatable/Various.", InfoBarSeverity.Error);
                return;
            }

            // Page section from the live garageCleanupGrids variants (mode B / §3.2): one GcSectionVariant
            // per stored grid key for this event; rewards + the run-list (incl. identicalTo) come from Lua
            // invokes, so no dump scan / static reward tables are needed.
            var wikitext = await Task.Run(() =>
            {
                var grids = GarageCleanupGridService.ParseLive(live);
                var variants = grids
                    .Where(kv => GarageCleanupGridService.StripVariantSuffix(kv.Key) == baseName)
                    .Select(kv => new GarageCleanupGridService.GcSectionVariant { Name = kv.Key, LevelCount = kv.Value.Count })
                    .ToList();
                return variants.Count == 0 ? "" : GarageCleanupGridService.GenerateSectionWikitext(baseName, variants);
            });

            if (string.IsNullOrEmpty(wikitext))
            {
                ShowInfo($"No Garage Cleanup grids for \"{baseName}\" in the live module. Generate/upload them first (Wiki Data Parser → Update Wiki).", InfoBarSeverity.Warning);
                return;
            }

            // WYSIWYG preview: splice the generated section into the LIVE event page so any preserved
            // hand-written prose shows in its final place — exactly what the upload will produce. Falls
            // back to the bare generated section if the page is missing or has no insertion anchor yet.
            var pageTitle = _selectedEvent.DisplayName;
            var previewText = wikitext;
            try
            {
                var evPage = await MysteryWikiService.FetchPageContentAsync(pageTitle);
                if (!string.IsNullOrEmpty(evPage))
                {
                    var (full, _) = GarageCleanupGridService.SpliceEventPageSection(evPage, wikitext);
                    var sec = full != null ? GarageCleanupGridService.ExtractGcSection(full) : null;
                    if (!string.IsNullOrEmpty(sec)) previewText = sec;
                }
            }
            catch { /* preview merge is best-effort; upload re-fetches authoritatively */ }

            try { Clipboard.SetText(previewText); } catch { /* clipboard may be locked */ }

            var subtitle = $"{baseName} · copied to clipboard";
            new WikitextPreviewDialog($"{pageTitle} — Garage Cleanup", subtitle, previewText,
                onPrimary: progress => UploadGcSectionAsync(pageTitle, wikitext, progress),
                primaryLabel: "Update Event Page")
            { Owner = Window.GetWindow(this) }.ShowDialog();

            ShowInfo("Garage Cleanup section generated.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Generate Garage Cleanup failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnGenerateGc.IsEnabled = true;
        }
    }

    /// <summary>
    /// Writes the per-year reward data to Module:Datatable/Various (so the page's reward invokes resolve),
    /// then re-fetches the event page, splices in the generated Garage Cleanup section (replace existing
    /// or insert after Rewards/Progress Bar, or a user-chosen section) and pushes it. Skips the page if it
    /// doesn't exist. Returns a status message for the dialog's info bar.
    /// </summary>
    private async Task<string> UploadGcSectionAsync(string pageTitle, string section,
        IProgress<string>? progress = null)
    {
        // Wraps a slow await so the info bar shows a live elapsed-seconds counter — server-side wiki edits
        // (large pages with Lua) can take many seconds, and a static label reads as "frozen".
        async Task<T> Beat<T>(string label, Task<T> work)
        {
            progress?.Report(label + "…");
            int secs = 0;
            while (true)
            {
                if (await Task.WhenAny(work, Task.Delay(1000)) == work) return await work;
                progress?.Report($"{label}… ({++secs}s)");
            }
        }

        var page = await Beat($"Fetching “{pageTitle}”", MysteryWikiService.FetchPageContentAsync(pageTitle));
        if (page == null) return $"Page \"{pageTitle}\" doesn't exist — skipped (nothing to update).";

        progress?.Report("Splicing Garage Cleanup section…");
        var (content, action) = GarageCleanupGridService.SpliceEventPageSection(page, section);
        if (content == null)
        {
            // No Rewards / Progress Bar anchor — let the user pick where to insert.
            var sections = GarageCleanupGridService.GetLevel2Sections(page);
            if (sections.Count == 0) return "Page has no sections to insert after — skipped.";
            var picker = new SectionPickerDialog(sections) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedSection == null)
                return "Cancelled — no section chosen.";
            (content, action) = GarageCleanupGridService.SpliceEventPageSection(page, section, picker.SelectedSection);
            if (content == null) return "Could not insert — section not found.";
        }
        bool pageChanged = content.TrimEnd() != page.TrimEnd();

        using var client = await Beat("Logging in to wiki", WikiMappingService.CreateAuthenticatedClientAsync(
            _main.Settings.WikiUsername, _main.Settings.WikiPassword));
        var csrf = await WikiMappingService.GetCsrfTokenAsync(client);

        // Grid + reward data live in Module:Datatable/Various.garageCleanupGrids (one combined table) and
        // are written by the main "Update Wiki (events)" flow (Build+BuildRewards+Combine → MergeForWrite).
        // This per-event upload only writes the event page; its reward invokes resolve against that data.

        // Event page section.
        if (pageChanged)
            await Beat($"Updating event page ({action})",
                WikiMappingService.EditModuleAsync(client, csrf, pageTitle, content,
                    "Update Garage Cleanup section (via MergeMansionWikiTools)"));

        // 3) Purge the page so it re-renders with the freshly-written module data — otherwise the page can
        //    show a stale "Lua error … index field '?'" built from a loadData snapshot before the data existed.
        progress?.Report("Purging page cache…");
        await WikiMappingService.PurgeAsync(client, pageTitle);

        return $"Updated \"{pageTitle}\" — {(pageChanged ? action : "page already up to date")}.";
    }

    /// <summary>Finds the GarageCleanupEventId for a GC by its resolved Name in the current events.json.</summary>
    private string? FindGcId(string baseName)
    {
        try
        {
            var path = _main.Settings.EventsJsonPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Data", out var data)) return null;
            if (!data.TryGetProperty("GarageCleanups", out var gcs)) return null;
            foreach (var g in gcs.EnumerateArray())
                if (g.TryGetProperty("Name", out var n) && n.GetString() == baseName)
                    return g.TryGetProperty("GarageCleanupEventId", out var id) ? id.GetString() : null;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Scans the APK folders (newest <c>Dump*</c> per version) for the GarageCleanup with the given id
    /// whose Schedule.Start year matches <paramref name="targetYear"/> (or the newest dump overall when
    /// null), returning a clone of the GC element + its schedule. Picks the newest dump (by events.json
    /// mtime) — for a re-aired config that captures the grid/rewards before devs overwrote the date.
    /// </summary>
    private (JsonElement Gc, DateTime? Start, double Dur, int Year)? FindGcInDumps(string gcId, int? targetYear)
    {
        var root = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
        // Prefer the newest dump for the year that has RESOLVED PatternSets (so static rewards work);
        // fall back to the newest dump for the year (grids still render even if rewards are missing).
        (JsonElement Gc, DateTime? Start, double Dur, int Year)? bestResolved = null, bestAny = null;
        DateTime bestResolvedMt = DateTime.MinValue, bestAnyMt = DateTime.MinValue;

        foreach (var verDir in Directory.GetDirectories(root))
        {
            // Scan ALL Dump* folders of a version (a re-dump may carry resolved patterns the newest lacks).
            foreach (var dump in Directory.GetDirectories(verDir, "Dump*"))
            {
                var evPath = Path.Combine(dump, "events.json");
                if (!File.Exists(evPath)) continue;
                var mt = File.GetLastWriteTimeUtc(evPath);
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(evPath));
                    if (!doc.RootElement.TryGetProperty("Data", out var data)) continue;
                    if (!data.TryGetProperty("GarageCleanups", out var gcs)) continue;
                    foreach (var g in gcs.EnumerateArray())
                    {
                        if (!g.TryGetProperty("GarageCleanupEventId", out var idEl) || idEl.GetString() != gcId) continue;
                        var (start, dur) = ReadSchedule(g);
                        int yr = start?.Year ?? 0;
                        if (targetYear != null && yr != targetYear.Value) continue;

                        bool resolved = g.TryGetProperty("PatternSets", out var ps)
                            && ps.ValueKind == JsonValueKind.Array && ps.GetArrayLength() > 0
                            && ps[0].ValueKind == JsonValueKind.Object;
                        if (resolved && (bestResolved == null || mt > bestResolvedMt))
                        { bestResolved = (g.Clone(), start, dur, yr); bestResolvedMt = mt; }
                        if (bestAny == null || mt > bestAnyMt)
                        { bestAny = (g.Clone(), start, dur, yr); bestAnyMt = mt; }
                    }
                }
                catch { }
            }
        }
        return bestResolved ?? bestAny;
    }

    /// <summary>Reads Schedule.Start (UTC) + duration (Lifetime ms, else Schedule.Duration) from a GC element.</summary>
    private static (DateTime? Start, double DurDays) ReadSchedule(JsonElement gc)
    {
        DateTime? start = null; double dur = 0;
        if (gc.TryGetProperty("ActivableParams", out var ap))
        {
            if (ap.TryGetProperty("Schedule", out var sched))
            {
                if (sched.TryGetProperty("Start", out var st) && st.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(st.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                    start = dt;
                // Schedule.Duration is the game's custom "3d 0h 0min 0s" format — reuse the shared parser.
                if (sched.TryGetProperty("Duration", out var d) && d.ValueKind == JsonValueKind.String
                    && EventService.ParseDuration(d.GetString()) is { } ts) dur = ts.TotalDays;
            }
            // Lifetime overrides only when it's a numeric ms value (e.g. CCSE); GC uses "ScheduleBased".
            if (ap.TryGetProperty("Lifetime", out var lt) && lt.ValueKind == JsonValueKind.Number)
                dur = lt.GetDouble() / 86400000.0;
        }
        return (start, dur);
    }

    private async void BtnGenerateFlowchart_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEvent == null || _main.DataService == null) return;

        try
        {
            btnGenerateFlowchart.IsEnabled = false;
            ShowInfo("Generating flowchart...", InfoBarSeverity.Informational);

            // Extract chain icons (highest level per chain)
            Dictionary<string, string>? chainIcons = null;
            var exportDir = ResolveExportDir();
            var processedDir = !string.IsNullOrEmpty(_main.Settings.ImageExporterBasePath)
                ? System.IO.Path.Combine(_main.Settings.ImageExporterBasePath, "Processed Images") : null;

            if (exportDir != null)
            {
                // Extract icon for each chain — try highest level first, fallback to level 1
                var itemTypes = new List<string>();
                foreach (var chain in _selectedEvent.Chains)
                {
                    if (chain.Items.Count == 0) continue;
                    // Prefer highest level for visual representation
                    var best = chain.Items.OrderByDescending(i => i.Level).First();
                    if (!string.IsNullOrEmpty(best.ItemType))
                        itemTypes.Add(best.ItemType);
                    // Also add level 1 as fallback (different atlas for some chains)
                    var first = chain.Items.OrderBy(i => i.Level).First();
                    if (!string.IsNullOrEmpty(first.ItemType) && first.ItemType != best.ItemType)
                        itemTypes.Add(first.ItemType);
                }

                chainIcons = await Task.Run(() =>
                    FlowchartImageService.ExtractItemIcons(itemTypes, _main.DataService, exportDir, processedDir));
            }

            var svg = EventChainFlowchartService.GenerateSvg(
                _selectedEvent.Chains, _main.DataService, _selectedEvent.DisplayName, chainIcons);
            _lastSvg = svg;

            // Auto-save to Events folder next to dump
            _lastSvgPath = null;
            var dumpDir = Path.GetDirectoryName(_main.Settings.EventsJsonPath);
            if (!string.IsNullOrEmpty(dumpDir))
            {
                var eventsDir = Path.Combine(dumpDir, "Events");
                Directory.CreateDirectory(eventsDir);
                _lastSvgPath = Path.Combine(eventsDir, $"{_selectedEvent.EventId}_flowchart.svg");
                File.WriteAllText(_lastSvgPath, svg);
            }

            txtPlaceholder.Text = _lastSvgPath != null
                ? $"Flowchart saved to {Path.GetFileName(_lastSvgPath)}. Click 'Open' to view."
                : "Flowchart generated. Save to view.";
            btnSaveSvg.IsEnabled = true;
            btnOpenFlowchart.IsEnabled = _lastSvgPath != null;

            ShowInfo($"Flowchart generated: {_selectedEvent.Chains.Count} chains.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Flowchart failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnGenerateFlowchart.IsEnabled = true;
        }
    }

    /// <summary>
    /// Finds the event's main-hub badge PNG in the export dir. Badge file naming is inconsistent across
    /// events (e.g. "_Main_Hub_Event_Badge", "_MainHub_Badge", "_Main_Hub_Badge", "_MainhubBadge",
    /// year-suffixed variants), so we glob "&lt;key&gt;*Badge*.png", prefer names containing "Hub", then
    /// shortest (base over decorated variants). Tries PrefabsOverride first, then the event id (some
    /// events have no PrefabsOverride). Returns null if nothing matches.
    /// </summary>
    private static string? FindBadge(string exportDir, string prefab, string eventId)
    {
        foreach (var key in new[] { prefab, eventId })
        {
            if (string.IsNullOrEmpty(key)) continue;
            try
            {
                var hits = Directory.GetFiles(exportDir, key + "*Badge*.png");
                var best = hits
                    .OrderByDescending(f => Path.GetFileName(f).Contains("Hub", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(f => Path.GetFileName(f).Length)
                    .FirstOrDefault();
                if (best != null) return best;
            }
            catch { }
        }
        return null;
    }

    private string? ResolveExportDir()
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version)) return null;
        var dir = Path.Combine(basePath, version, "Export - PNGs");
        return Directory.Exists(dir) ? dir : null;
    }

    private void BtnSaveSvg_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastSvg) || _selectedEvent == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_selectedEvent.EventId}_flowchart.svg",
            Filter = "SVG files (*.svg)|*.svg",
            InitialDirectory = _lastSvgPath != null ? Path.GetDirectoryName(_lastSvgPath) : ""
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            File.WriteAllText(dlg.FileName, _lastSvg);
            _lastSvgPath = dlg.FileName;
            btnOpenFlowchart.IsEnabled = true;
            ShowInfo($"Saved: {dlg.FileName}", InfoBarSeverity.Success);
        }
    }

    private void BtnOpenFlowchart_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastSvgPath) && File.Exists(_lastSvgPath))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastSvgPath) { UseShellExecute = true }); }
            catch (Exception ex) { ShowInfo($"Cannot open: {ex.Message}", InfoBarSeverity.Error); }
        }
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Trigger(BuildEventList);
    }

    /// <summary>"Show disabled" toggle — rebuilds the list to include/exclude disabled boards.</summary>
    private void ChkShowDisabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_eventService == null) return;   // ignore the initial XAML-load Unchecked event
        BuildEventList();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }
}

// ── View modely položek seznamu eventů (heterogenní ItemsSource) ─────────────

/// <summary>Rok-header v seznamu eventů (oddělovací linka + nadpis roku).</summary>
public sealed class EventYearHeaderItem
{
    public string YearText { get; init; } = "";
}

/// <summary>Karta eventu v seznamu (virtualizovaný ItemsControl na EventsPage).</summary>
public sealed class EventCardItem : System.ComponentModel.INotifyPropertyChanged
{
    public CollectibleBoardEvent Event { get; }
    public string DisplayName => Event.DisplayName;
    public string InfoText { get; }

    /// <summary>Disabled board (A/B variant "B" / retired re-air) — dims the card + shows a "disabled" chip.</summary>
    public bool IsDisabled => !Event.IsEnabled;

    private bool _isSelected;
    /// <summary>True když Event == aktuálně vybraný event — potlačuje hover efekt
    /// (přežívá rebuild seznamu, 1:1 s původním Tag != _selectedEvent checkem).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
    }

    private bool _isHighlighted;
    /// <summary>True jen po kliknutí v aktuálním seznamu — řídí zvýrazněné pozadí.
    /// Rebuild (filtr) ho resetuje, stejně jako původní ruční build karet.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted != value) { _isHighlighted = value; OnPropertyChanged(nameof(IsHighlighted)); } }
    }

    /// <summary>Absolute path to the event's badge PNG (set by BuildEventList), or null if none found.</summary>
    public string? BadgePath { get; set; }
    public bool HasBadge => !string.IsNullOrEmpty(BadgePath);

    private ImageSource? _badgeImage;
    private bool _badgeLoaded;
    /// <summary>Lazily decoded badge thumbnail (frozen, file-handle-free), or null.</summary>
    public ImageSource? BadgeImage
    {
        get
        {
            if (_badgeLoaded) return _badgeImage;
            _badgeLoaded = true;
            if (!string.IsNullOrEmpty(BadgePath) && File.Exists(BadgePath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(BadgePath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;   // don't lock the file
                    bmp.DecodePixelWidth = 48;                    // small thumbnail
                    bmp.EndInit();
                    bmp.Freeze();
                    _badgeImage = bmp;
                }
                catch { }
            }
            return _badgeImage;
        }
    }

    public EventCardItem(CollectibleBoardEvent ev)
    {
        Event = ev;

        var infoText = ev.EventId;
        if (ev.StartDate.HasValue)
            infoText = ev.StartDate.Value.ToString("d MMM yyyy");
        if (ev.Chains.Count > 0)
            infoText += $" · {ev.Chains.Count} chains";
        InfoText = infoText;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
