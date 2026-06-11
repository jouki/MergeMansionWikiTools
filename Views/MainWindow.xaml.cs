using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public partial class MainWindow : FluentWindow
{
    // ── Events ──
    public event Action? ApkVersionChanged;
    public void RaiseApkVersionChanged() => ApkVersionChanged?.Invoke();

    public event Action? WikiVerifiedChanged;
    public void RaiseWikiVerifiedChanged() => WikiVerifiedChanged?.Invoke();

    public event Action? TinifyApiKeyChanged;
    public void RaiseTinifyApiKeyChanged() => TinifyApiKeyChanged?.Invoke();

    // ── Shared services (accessible by pages) ──
    public AppSettings Settings { get; private set; }
    public ChainNameService ChainNameService { get; } = new();
    public DataService? DataService { get; private set; }
    public WikiMappingCache? WikiMapping { get; set; }
    public MysteryService? MysteryService { get; set; }

    /// <summary>Path the current <see cref="MysteryService"/> was loaded from (base or
    /// AB-bucket events.json). Lets MysteriesPage decide whether the preloaded instance
    /// matches its bucket-resolved path or a fresh load is needed.</summary>
    public string? MysteryServiceLoadedPath { get; set; }

    internal List<ApkDownloadService.ApkVersionInfo>? ApkVersions { get; set; }

    // ── Shared data caches ──────────────────────────────────────────────
    // One JSON parse per file, shared across pages/dialogs (AreasService used to be
    // parsed 5×, EventService 3×, etc.). Invalidated in the Set*Path methods — the
    // Game Data Dumper calls those after every dump, even when the path itself is
    // unchanged (file rewritten in place) — and in RefreshAutocompleteData as a
    // post-APK-extraction safety net. See AsyncDataCache<T>.
    private readonly AsyncDataCache<AreasService> _areasServiceCache = new(async path =>
    {
        var svc = new AreasService();
        await svc.LoadAsync(path);
        return svc;
    });
    private readonly AsyncDataCache<EventService> _eventServiceCache = new(async path =>
    {
        var svc = new EventService();
        await svc.LoadAsync(path);
        return svc;
    });
    private readonly AsyncDataCache<DialogueService> _dialogueServiceCache = new(async path =>
    {
        var svc = new DialogueService();
        await svc.LoadAsync(path);
        return svc;
    });
    private readonly AsyncDataCache<MysteryService> _mysteryServiceCache = new(async path =>
    {
        var svc = new MysteryService();
        await svc.LoadAsync(path);
        return svc;
    });

    // EventService.ResolveChains mutates the shared instance, so resolves are chained
    // (serialized) to avoid two callers mutating ev.Chains concurrently. Resolution is
    // re-run on every GetEventServiceAsync call — matching the old per-consumer behavior —
    // because TryApplyWikiMapping replaces ParsedChain objects inside the SAME DataService
    // instance (Chains.Clear + AddRange), so a one-shot "already resolved" flag would keep
    // ev.Chains pointing at dead chain objects.
    private readonly object _eventResolveLock = new();
    private Task _eventResolveChain = Task.CompletedTask;

    /// <summary>Shared, cached AreasService loaded from Settings.AreasJsonPath.
    /// Returns null when the path is not configured or the file is missing.</summary>
    public async Task<AreasService?> GetAreasServiceAsync()
    {
        var path = Settings.AreasJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return await _areasServiceCache.GetOrLoadAsync(path);
    }

    /// <summary>Shared, cached EventService loaded from Settings.EventsJsonPath, with
    /// chains resolved against the current DataService (when loaded). Returns null when
    /// the path is not configured or the file is missing.</summary>
    public async Task<EventService?> GetEventServiceAsync()
    {
        var path = Settings.EventsJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var svc = await _eventServiceCache.GetOrLoadAsync(path);

        var ds = DataService;
        if (ds != null)
        {
            Task resolveTask;
            lock (_eventResolveLock)
            {
                var prev = _eventResolveChain;
                _eventResolveChain = resolveTask = Task.Run(async () =>
                {
                    try { await prev; } catch { /* previous resolve failure doesn't block this one */ }
                    svc.ResolveChains(ds);
                });
            }
            await resolveTask;
        }
        return svc;
    }

    /// <summary>Shared, cached DialogueService. Resolves dialogues.json via the same
    /// candidate chain MysteriesPage historically used (explicit setting → next to
    /// events.json → dumper output → APK dump folder). Returns null when no candidate
    /// exists or the load fails (logged, matching the previous per-page behavior).</summary>
    public async Task<DialogueService?> GetDialogueServiceAsync()
    {
        var path = ResolveDialoguesJsonPath();
        if (path == null) return null;
        try
        {
            var svc = await _dialogueServiceCache.GetOrLoadAsync(path);
            AppLogger.Info("DialogueService loaded from: " + path);
            return svc;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Failed to load dialogues.json: " + ex.Message);
            return null;
        }
    }

    /// <summary>Finds dialogues.json — explicit setting first, then conventional
    /// locations next to other dump files. Moved from MysteriesPage so all consumers
    /// share one resolution + one parsed instance.</summary>
    private string? ResolveDialoguesJsonPath()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(Settings.DialoguesJsonPath))
            candidates.Add(Settings.DialoguesJsonPath);
        var eventsPath = Settings.EventsJsonPath;
        if (!string.IsNullOrEmpty(eventsPath))
        {
            var dir = Path.GetDirectoryName(eventsPath);
            if (!string.IsNullOrEmpty(dir))
                candidates.Add(Path.Combine(dir, "dialogues.json"));
        }
        if (!string.IsNullOrEmpty(Settings.DumperOutputPath))
            candidates.Add(Path.Combine(Settings.DumperOutputPath, "dialogues.json"));
        if (!string.IsNullOrEmpty(Settings.ImageExporterBasePath) && !string.IsNullOrEmpty(Settings.SelectedApkVersion))
        {
            var dumpFolder = !string.IsNullOrEmpty(Settings.ActiveDumpFolder) ? Settings.ActiveDumpFolder : "Dump";
            var dumpDir = Path.Combine(Settings.ImageExporterBasePath, Settings.SelectedApkVersion, dumpFolder);
            candidates.Add(Path.Combine(dumpDir, "dialogues.json"));
        }
        AppLogger.Info($"DialogueService: searching {candidates.Count} candidates: {string.Join(", ", candidates)}");
        var path = candidates.FirstOrDefault(File.Exists);
        if (path == null)
            AppLogger.Warn("DialogueService: dialogues.json not found in any candidate path");
        return path;
    }

    /// <summary>Shared, cached MysteryService for the given events.json path (callers pass
    /// the base path or an AB-bucket patched copy — see MysteriesPage.ResolveEventsJsonPath).</summary>
    public Task<MysteryService> GetMysteryServiceAsync(string path)
        => _mysteryServiceCache.GetOrLoadAsync(path);

    /// <summary>Forces the next GetMysteryServiceAsync to re-parse from disk. Used by the
    /// MysteriesPage Refresh button, which intentionally rebuilds mysteries from the file
    /// (fresh WikiStatus objects) before re-applying the persisted status cache.</summary>
    public void InvalidateMysteryServiceCache() => _mysteryServiceCache.Invalidate();

    /// <summary>Pre-built autocomplete dataset for the Generate Page dialog. Eagerly populated
    /// off-thread after chain data + areas load — see <see cref="StartAutocompletePrewarm"/>.
    /// Null until the prewarm completes; consumers fall back to inline build.</summary>
    public AutocompleteData? CachedAutocomplete { get; private set; }

    /// <summary>Background task that builds CachedAutocomplete. Public so TableGeneratorDialog
    /// can await it (instead of starting its own duplicate build) when it opens before prewarm finishes.</summary>
    public Task<AutocompleteData>? AutocompletePrewarmTask { get; private set; }

    /// <summary>Fired after the autocomplete dataset has been (re)built. Open Generate Page
    /// dialogs subscribe to re-pull thumbnails — useful when image extraction finishes AFTER
    /// the dialog opened (new version → no images → user extracts → thumbnails appear live).</summary>
    public event Action? AutocompleteDataRefreshed;

    /// <summary>Re-runs the autocomplete prewarm (e.g. after APK image extraction completes so
    /// freshly-exported PNGs get picked up). Raises <see cref="AutocompleteDataRefreshed"/> when done.</summary>
    public void RefreshAutocompleteData()
    {
        SpriteMetadataService.InvalidateCache();
        // Safety net: drop shared JSON data caches too (the dumper's Set*Path calls are
        // the primary invalidation hooks; this covers any extraction flow that bypasses
        // them). Cheap — caches re-parse lazily on next access.
        _areasServiceCache.Invalidate();
        _eventServiceCache.Invalidate();
        _dialogueServiceCache.Invalidate();
        _mysteryServiceCache.Invalidate();
        StartAutocompletePrewarm();
        // Notify subscribers once the rebuild finishes (task may already be running)
        AutocompletePrewarmTask?.ContinueWith(_ =>
            Dispatcher.Invoke(() => AutocompleteDataRefreshed?.Invoke()),
            TaskScheduler.Default);
    }

    /// <summary>Whether variant subdirectories exist alongside chain_item_odds.json.</summary>
    public bool HasVariantDirectories { get; private set; }

    // ── Data file change events ──
    /// <summary>Fired after chain_item_odds.json is successfully loaded.</summary>
    public event Action? ChainDataLoaded;
    /// <summary>Fired when areas.json path changes (set or cleared).</summary>
    public event Action? AreasFileChanged;
    /// <summary>Fired when events.json path changes (set or cleared).</summary>
    public event Action? EventsFileChanged;

    private bool _wikiMappingApplied;
    private DispatcherTimer? _updateTimer;

    private ChainBrowserPage? _chainPage;
    private ImageOptimiserPage? _imageOptimiserPage;
    private WikiDataParserPage? _wikiDataParserPage;
    private ImageExtractorPage? _imageExtractorPage;
    private DialogueMakerPage? _dialogueMakerPage;
    private EventsPage? _eventsPage;
    private MysteriesPage? _mysteriesPage;
    private AreaFlowchartsPage? _areaFlowchartsPage;
    private ClueCollectionPage? _clueCollectionPage;
    private GameDataDumperPage? _gameDataDumperPage;
    private SettingsPage? _settingsPage;
    private AboutPage? _aboutPage;

    public MainWindow()
    {
        using var _t = AppLogger.Timed("MainWindow.ctor");
        InitializeComponent();
        txtSidebarVersion.Text = Models.AppVersion.Build;

        Settings = SettingsService.Load();
        App.ApplyTheme(Settings.ThemePreference);
        ApplicationThemeManager.Apply(this);

        // Re-apply window resources whenever the global theme changes at runtime
        ApplicationThemeManager.Changed += OnAppThemeChanged;

        Loaded += (_, _) => UpdateNavIndicator(animate: false);
        RestoreWindowPosition();
        Closing += (_, _) => SaveWindowPosition();

        // Track session
        Increment(s => { s.SessionCount++; if (s.FirstLaunch == default) s.FirstLaunch = DateTime.UtcNow; });

        // Validate dump file paths — clear stale paths that no longer exist
        ValidateDumpFilePaths();

        // Consume pre-loaded data from splash screen, or load fresh
        if (App.PreloadedChainDataTask != null)
            _ = ConsumePreloadedChainDataAsync();
        else if (!string.IsNullOrEmpty(Settings.ChainItemOddsPath) && File.Exists(Settings.ChainItemOddsPath))
            _ = LoadDataAsync(Settings.ChainItemOddsPath);

        if (App.PreloadedWikiMappingTask != null)
            _ = ConsumePreloadedWikiMappingAsync();
        else
            _ = LoadWikiMappingAsync();

        // Pre-load mysteries + wiki status check
        _ = LoadMysteriesAsync();

        // Show chains page by default (navList SelectedIndex=0 triggers this)
        ShowChainsPage();


        // Auto-update check: at startup + every 15 minutes
        _ = CheckForUpdateAsync();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
    }

    // ── Windows appearance change detection ──

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_SYSCOLORCHANGE = 0x0015;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_MOUSEHWHEEL = 0x020E;

    private DispatcherTimer? _appearanceTimer;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_DWMCOLORIZATIONCOLORCHANGED:
            case WM_THEMECHANGED:
            case WM_SYSCOLORCHANGE:
                // Accent/color changes — debounce (multiple messages fire in quick succession)
                ScheduleAppearanceUpdate();
                break;
            case WM_SETTINGCHANGE:
                // Dark mode toggle — fires AFTER registry is updated, so GetSystemTheme()
                // returns the correct value. Cancel any pending debounce and process now.
                if (lParam != IntPtr.Zero && Marshal.PtrToStringAuto(lParam) == "ImmersiveColorSet")
                {
                    _appearanceTimer?.Stop();
                    Dispatcher.InvokeAsync(ApplySystemAppearance);
                }
                break;
            case WM_MOUSEHWHEEL:
                HandleMouseHWheel(wParam, ref handled);
                break;
        }
        return IntPtr.Zero;
    }

    private void RestoreWindowPosition()
    {
        var s = Settings;
        if (s.WindowLeft.HasValue && s.WindowTop.HasValue)
        {
            // Basic sanity: ensure at least part of the window is visible on the virtual screen
            double left = s.WindowLeft.Value;
            double top = s.WindowTop.Value;
            double w = s.WindowWidth ?? 1380;
            double h = s.WindowHeight ?? 963;
            bool onScreen = left + w > SystemParameters.VirtualScreenLeft
                && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                && top + h > SystemParameters.VirtualScreenTop
                && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        if (s.WindowWidth.HasValue && s.WindowWidth.Value >= 400)
            Width = s.WindowWidth.Value;
        if (s.WindowHeight.HasValue && s.WindowHeight.Value >= 300)
            Height = s.WindowHeight.Value;
        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowPosition()
    {
        var s = Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        // Save Normal state bounds (not Maximized — so restoring from maximized goes back to last normal size)
        if (WindowState == WindowState.Normal)
        {
            s.WindowLeft = Left;
            s.WindowTop = Top;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        SaveSettings();
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ScrollViewer sv)
                return sv;
            // VisualTreeHelper throws on non-Visual elements (e.g. Run, Inline) — fall back to logical tree
            DependencyObject? parent;
            try { parent = VisualTreeHelper.GetParent(element); }
            catch { parent = LogicalTreeHelper.GetParent(element); }
            element = parent;
        }
        return null;
    }

    /// <summary>
    /// Handles WM_MOUSEHWHEEL (tilt wheel) for any window. Call from WndProc hooks.
    /// </summary>
    public static bool HandleMouseHWheel(IntPtr wParam, ref bool handled)
    {
        int tiltDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        var target = Mouse.DirectlyOver as DependencyObject;
        if (target != null)
        {
            var sv = FindParentScrollViewer(target);
            if (sv != null)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset + tiltDelta * 0.4);
                handled = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Debounces WM_DWMCOLORIZATIONCOLORCHANGED / WM_SYSCOLORCHANGE into a single update.
    /// WM_SETTINGCHANGE bypasses this (processed immediately via Dispatcher).
    /// </summary>
    private void ScheduleAppearanceUpdate()
    {
        if (_appearanceTimer == null)
        {
            _appearanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _appearanceTimer.Tick += (_, _) =>
            {
                _appearanceTimer.Stop();
                ApplySystemAppearance();
            };
        }
        _appearanceTimer.Stop();
        _appearanceTimer.Start();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplySystemAppearance()
    {
        // Re-apply theme with current preference (handles accent + light/dark changes)
        App.ApplyTheme(Settings.ThemePreference);

        // Restore Mica backdrop — ApplicationThemeManager.Apply(theme,...) can't find
        // our MainWindow because App inherits from Application, not UiApplication.
        // Use direct DWM call instead of WindowBackdrop API (which modifies window styles
        // and can interfere with subsequent WM_ message reception).
        var hwnd = new WindowInteropHelper(this).Handle;
        int micaBackdrop = 2; // DWMSBT_MAINWINDOW = Mica
        DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref micaBackdrop, sizeof(int));

        // Ensure window background is transparent so Mica shows through
        Background = Brushes.Transparent;
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
    }

    // ── Global keyboard shortcuts ──

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.V &&
            System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            if (contentArea.Content is ImageOptimiserPage ioPage)
            {
                if (ioPage.HandleCtrlV())
                    e.Handled = true;
            }
        }
    }

    // ── Navigation ──

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (contentArea == null) return;
        AppLogger.Info($"Navigate → index {navList.SelectedIndex}");

        // Stop clipboard monitor when leaving IO page (unless global)
        if (navList.SelectedIndex != 1 && _imageOptimiserPage != null && !Settings.ClipboardMonitorGlobal)
            _imageOptimiserPage.StopClipboardMonitor();

        switch (navList.SelectedIndex)
        {
            case 0: ShowChainsPage(); break;
            case 1: ShowImageOptimiserPage(); break;
            case 2: ShowDialogueMakerPage(); break;
            case 3: ShowWikiDataParserPage(); break;
            case 4: ShowImageExtractorPage(); break;
            case 5: ShowEventsPage(); break;
            case 6: ShowMysteriesPage(); break;
            case 7: ShowGameDataDumperPage(); break;
            case 8: ShowAreaFlowchartsPage(); break;
            case 9: ShowClueCollectionPage(); break;
            case 10: ShowSettingsPage(); break;
            case 11: ShowAboutPage(); break;
        }

        UpdateNavIndicator();
    }

    private void ShowChainsPage()
    {
        _chainPage ??= new ChainBrowserPage(this);
        contentArea.Content = _chainPage;
    }

    private void ShowImageOptimiserPage()
    {
        _imageOptimiserPage ??= new ImageOptimiserPage(this);
        contentArea.Content = _imageOptimiserPage;
        _imageOptimiserPage.StartClipboardMonitor();
    }

    private void ShowWikiDataParserPage()
    {
        if (_wikiDataParserPage == null)
        {
            _wikiDataParserPage = new WikiDataParserPage(this);
            contentArea.Content = _wikiDataParserPage;
            _wikiDataParserPage.OnPageShown();
        }
        else
        {
            _wikiDataParserPage.PrepareForShow();
            contentArea.Content = _wikiDataParserPage;
            _wikiDataParserPage.OnPageShown();
        }
    }

    private void ShowImageExtractorPage()
    {
        _imageExtractorPage ??= new ImageExtractorPage(this);
        contentArea.Content = _imageExtractorPage;
    }

    private void ShowDialogueMakerPage()
    {
        _dialogueMakerPage ??= new DialogueMakerPage(this);
        contentArea.Content = _dialogueMakerPage;
    }

    private void ShowEventsPage()
    {
        _eventsPage ??= new EventsPage(this);
        contentArea.Content = _eventsPage;
    }

    private void ShowMysteriesPage()
    {
        _mysteriesPage ??= new MysteriesPage(this);
        contentArea.Content = _mysteriesPage;
    }

    private void ShowGameDataDumperPage()
    {
        _gameDataDumperPage ??= new GameDataDumperPage(this);
        contentArea.Content = _gameDataDumperPage;
    }

    private void ShowAreaFlowchartsPage()
    {
        _areaFlowchartsPage ??= new AreaFlowchartsPage(this);
        contentArea.Content = _areaFlowchartsPage;
        _areaFlowchartsPage.OnPageShown();
    }

    private void ShowClueCollectionPage()
    {
        _clueCollectionPage ??= new ClueCollectionPage(this);
        contentArea.Content = _clueCollectionPage;
    }

    private void ShowSettingsPage()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _settingsPage ??= new SettingsPage(this);
        AppLogger.Info($"[ShowSettings] ctor: {sw.ElapsedMilliseconds}ms");
        _settingsPage.RefreshPaths();
        AppLogger.Info($"[ShowSettings] RefreshPaths: {sw.ElapsedMilliseconds}ms");
        contentArea.Content = _settingsPage;
        AppLogger.Info($"[ShowSettings] Content assigned: {sw.ElapsedMilliseconds}ms");
        Dispatcher.InvokeAsync(() => AppLogger.Info($"[ShowSettings] UI idle after: {sw.ElapsedMilliseconds}ms"),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    internal void SetPreCreatedSettingsPage(SettingsPage page) => _settingsPage = page;

    /// <summary>
    /// Returns game version string for a given date, or null if versions not loaded yet.
    /// </summary>
    public string? GetVersionForDate(DateTimeOffset date)
    {
        if (ApkVersions == null || ApkVersions.Count == 0) return null;
        return ApkDownloadService.MatchVersionByDate(ApkVersions, date)?.Version;
    }

    public string? GetVersionForDate(DateTime date) => GetVersionForDate(new DateTimeOffset(date));

    public void RefreshSettingsPaths() => _settingsPage?.RefreshPaths();

    private void ShowAboutPage()
    {
        _aboutPage ??= new AboutPage(this);
        _aboutPage.RefreshStats();
        contentArea.Content = _aboutPage;
    }

    // ── Nav indicator animation ──

    private void UpdateNavIndicator(bool animate = true)
    {
        if (navList.SelectedIndex < 0 || navIndicator == null) return;

        if (navList.ItemContainerGenerator.ContainerFromIndex(navList.SelectedIndex)
            is not ListBoxItem container)
            return;

        var transform = container.TransformToVisual(navList);
        var point = transform.Transform(new Point(0, 0));
        double targetY = point.Y + (container.ActualHeight - navIndicator.ActualHeight) / 2;

        if (animate)
        {
            var anim = new DoubleAnimation
            {
                To = targetY,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            navIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        else
        {
            navIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
            navIndicatorTransform.Y = targetY;
        }
    }

    // ── Data loading ──

    public async Task LoadDataAsync(string path)
    {
        using var _t = AppLogger.Timed("LoadDataAsync");
        try
        {
            ShowStatus("Loading data...", InfoBarSeverity.Informational);
            txtDataStatus.Text = "Loading...";

            DataService = new DataService(ChainNameService);
            await DataService.LoadAsync(path);

            ShowStatus($"Loaded {DataService.Chains.Count} chains from {Path.GetFileName(path)}", InfoBarSeverity.Success);
            Increment(s => s.DataLoads++);

            // Check for AB test variant directories
            HasVariantDirectories = VariantComparisonService.HasVariants(path);

            // Reset flag — fresh data from JSON, mapping can be re-applied
            _wikiMappingApplied = false;
            TryApplyWikiMapping();

            // Resolve mystery items (if mysteries already loaded)
            ResolveMysteryItems();

            // Update sidebar status
            UpdateSidebarStatus();

            // Notify subscribers
            _chainPage?.OnDataLoaded();
            ChainDataLoaded?.Invoke();
            StartAutocompletePrewarm();

            // Show warnings
            if (DataService.Warnings.Count > 0)
            {
                ShowStatus($"Loaded with {DataService.Warnings.Count} warning(s)", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoadDataAsync failed", ex);
            txtDataStatus.Text = "Load failed";
            ShowStatus($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ── Status bar ──

    public void ShowStatus(string message, InfoBarSeverity severity, bool autoClose = true)
    {
        statusBar.Message = message;
        statusBar.Severity = severity;
        statusBar.IsOpen = true;

        if (autoClose && severity != InfoBarSeverity.Error)
        {
            _ = AutoCloseStatus();
        }
    }

    public void ShowWarning(string message)
    {
        statusBar.Message = message;
        statusBar.Severity = InfoBarSeverity.Warning;
        statusBar.IsOpen = true;
    }

    private async Task AutoCloseStatus()
    {
        await Task.Delay(4000);
        statusBar.IsOpen = false;
    }

    private async Task ConsumePreloadedChainDataAsync()
    {
        try
        {
            ShowStatus("Loading data...", Wpf.Ui.Controls.InfoBarSeverity.Informational);
            txtDataStatus.Text = "Loading...";

            DataService = await App.PreloadedChainDataTask!;
            App.PreloadedChainDataTask = null;

            ShowStatus($"Loaded {DataService.Chains.Count} chains...", Wpf.Ui.Controls.InfoBarSeverity.Success);
            Increment(s => s.DataLoads++);

            HasVariantDirectories = VariantComparisonService.HasVariants(Settings.ChainItemOddsPath);
            _wikiMappingApplied = false;
            TryApplyWikiMapping();
            ResolveMysteryItems();
            UpdateSidebarStatus();
            _chainPage?.OnDataLoaded();
            ChainDataLoaded?.Invoke();
            StartAutocompletePrewarm();
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConsumePreloadedChainData failed, falling back to fresh load", ex);
            App.PreloadedChainDataTask = null;
            if (!string.IsNullOrEmpty(Settings.ChainItemOddsPath))
                _ = LoadDataAsync(Settings.ChainItemOddsPath);
        }
    }

    private async Task ConsumePreloadedWikiMappingAsync()
    {
        try
        {
            var cache = WikiMappingService.Load(); // local cache first (instant)
            WikiMapping = cache;

            var fresh = await App.PreloadedWikiMappingTask!;
            App.PreloadedWikiMappingTask = null;
            WikiMapping = fresh;
            TryApplyWikiMapping();
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConsumePreloadedWikiMapping failed", ex);
            App.PreloadedWikiMappingTask = null;
            _ = LoadWikiMappingAsync();
        }
    }

    private async Task LoadWikiMappingAsync()
    {
        using var _t = AppLogger.Timed("LoadWikiMappingAsync");
        // Load cache immediately for fast startup
        var cache = WikiMappingService.Load();
        WikiMapping = cache;

        // Always fetch fresh data from wiki (cache is fallback only)
        try
        {
            cache = await WikiMappingService.FetchFromWikiAsync();
            WikiMapping = cache;
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoadWikiMappingAsync: fetch failed, using cache", ex);
        }

        TryApplyWikiMapping();
    }

    /// <summary>
    /// Guards against double-application within the same data load cycle.
    /// Called from both LoadDataAsync and LoadWikiMappingAsync (whichever completes second triggers the work).
    /// </summary>
    private void TryApplyWikiMapping()
    {
        if (_wikiMappingApplied || DataService == null || WikiMapping == null
            || WikiMapping.Mappings.Count == 0) return;
        _wikiMappingApplied = true;
        ApplyWikiMappingToChains();
    }

    /// <summary>
    /// Applies wiki mapping as primary data override: renames items, overrides levels,
    /// regroups items into chains based on wiki chainName, and detects level collisions.
    /// Priority: customName > wikiName > originalName > configKey.
    /// </summary>
    private void ApplyWikiMappingToChains()
    {
        if (DataService == null || WikiMapping == null || WikiMapping.Mappings.Count == 0) return;
        using var _t = AppLogger.Timed("ApplyWikiMappingToChains");

        var mappings = WikiMapping.Mappings;

        // ── Phase 1: Apply field overrides per item ──
        // Record each item's wiki-specified chainName (if any)
        var itemWikiChainName = new Dictionary<ParsedItem, string>(); // item → wiki chainName
        var itemIsAlias = new HashSet<ParsedItem>(); // items marked as alias in wiki mapping

        foreach (var chain in DataService.Chains)
        {
            foreach (var item in chain.Items)
            {
                if (string.IsNullOrEmpty(item.ItemType)) continue;
                if (!mappings.TryGetValue(item.ItemType, out var entry)) continue;

                // Override item name
                if (!string.IsNullOrEmpty(entry.Name))
                {
                    item.Name = entry.Name;
                    DataService.ItemNames[item.ItemType] = entry.Name;
                }

                // Override level
                if (entry.Level.HasValue)
                    item.Level = entry.Level.Value;

                // Track alias flag
                if (entry.IsAlias)
                {
                    item.IsAlias = true;
                    itemIsAlias.Add(item);
                }

                // Record wiki chainName for Phase 2
                if (!string.IsNullOrEmpty(entry.ChainName))
                    itemWikiChainName[item] = entry.ChainName;
            }
        }

        // ── Phase 2: Build flat item list with effective chainName ──
        // Each entry: (item, effectiveChainName, sourceChain)
        var flatItems = new List<(ParsedItem Item, string EffectiveChainName, ParsedChain SourceChain)>();

        foreach (var chain in DataService.Chains)
        {
            // Determine chain-level wiki default name:
            // if ALL wiki-mapped items in this chain agree on one chainName → that name
            var wikiNames = chain.Items
                .Where(i => itemWikiChainName.ContainsKey(i))
                .Select(i => itemWikiChainName[i])
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string? chainWikiDefault = wikiNames.Count == 1 ? wikiNames[0] : null;

            foreach (var item in chain.Items)
            {
                // Item's own wiki chainName takes priority, then chain-level wiki default, then original DisplayName
                string effectiveName;
                if (itemWikiChainName.TryGetValue(item, out var itemSpecific))
                    effectiveName = itemSpecific;
                else if (chainWikiDefault != null)
                    effectiveName = chainWikiDefault;
                else
                    effectiveName = chain.DisplayName;

                item.SourceChainKey = chain.ConfigKey;
                flatItems.Add((item, effectiveName, chain));
            }
        }

        // ── Phase 3: Group items by effectiveChainName → build new chains ──
        var newChains = new List<ParsedChain>();

        foreach (var group in flatItems.GroupBy(x => x.EffectiveChainName, StringComparer.Ordinal))
        {
            var items = group.OrderBy(x => x.Item.Level).Select(x => x.Item).ToList();
            var sourceChains = group.Select(x => x.SourceChain).Distinct().ToList();
            var primarySource = sourceChains[0];

            // Collect all source ConfigKeys
            var allConfigKeys = sourceChains.Select(c => c.ConfigKey).Distinct().ToList();

            // Check if the effective name came from wiki mapping
            bool nameFromWiki = group.Any(x => itemWikiChainName.ContainsKey(x.Item));

            // Determine display name: customName (from any source) > effectiveChainName
            string? customName = null;
            foreach (var sc in sourceChains)
            {
                if (!string.IsNullOrEmpty(sc.CustomName))
                {
                    customName = sc.CustomName;
                    break;
                }
            }

            var newChain = new ParsedChain
            {
                ConfigKey = primarySource.ConfigKey,
                PoolTag = primarySource.PoolTag,
                HasTestTag = primarySource.HasTestTag,
                DisplayName = customName ?? group.Key,
                OriginalName = primarySource.OriginalName,
                HasNaturalName = primarySource.HasNaturalName,
                CustomName = customName,
                IsNameFromWiki = nameFromWiki && customName == null,
                MergedFromConfigKeys = allConfigKeys.Count > 1 ? allConfigKeys : null,
                Items = items,
            };

            newChains.Add(newChain);
        }

        // ── Phase 4: Replace DataService.Chains + update dictionaries ──
        DataService.Chains.Clear();
        DataService.Chains.AddRange(newChains);

        foreach (var chain in newChains)
        {
            DataService.ChainNames[chain.ConfigKey] = chain.DisplayName;
            if (chain.MergedFromConfigKeys != null)
            {
                foreach (var key in chain.MergedFromConfigKeys)
                    DataService.ChainNames[key] = chain.DisplayName;
            }
        }

        // ── Phase 5: Detect level collisions ──
        // Skip collision warnings when all items in a level group are aliases,
        // or when it's a mix of alias + non-alias items (alias defers to primary).
        foreach (var chain in newChains)
        {
            var levelGroups = chain.Items.GroupBy(i => i.Level).Where(g => g.Count() > 1).ToList();
            if (levelGroups.Count > 0)
            {
                bool anyRealCollision = false;
                foreach (var lg in levelGroups)
                {
                    // Only non-alias items can collide — aliases defer to the primary
                    var nonAliasItems = lg.Where(item => !itemIsAlias.Contains(item)).ToList();
                    if (nonAliasItems.Count <= 1) continue;

                    anyRealCollision = true;
                    foreach (var item in nonAliasItems)
                        item.IsColliding = true;
                }
                chain.HasLevelCollisions = anyRealCollision;
            }
        }

        // Refresh chain browser if it was already showing data
        _chainPage?.OnDataLoaded();
    }

    /// <summary>Kicks off off-thread autocomplete dataset build. Re-invoked on every
    /// data-load — discards previous cache. The Generate Page dialog reads the cached
    /// dataset (or awaits the running task) instead of building inline, saving ~1-2s
    /// per dialog open. Idempotent: safe to call before/after dialog opens.</summary>
    private void StartAutocompletePrewarm()
    {
        if (DataService == null) return;
        CachedAutocomplete = null;
        var ds = DataService;
        var wm = WikiMapping;
        var imgBase = Settings.ImageExporterBasePath;
        var apkVer = Settings.SelectedApkVersion;
        AutocompletePrewarmTask = Task.Run(async () =>
        {
            using var _t = AppLogger.Timed("AutocompletePrewarm");
            List<LuaArea>? areas = null;
            try
            {
                // Shared per-path cache — repeated prewarms no longer re-parse areas.json
                areas = (await GetAreasServiceAsync())?.Areas;
            }
            catch { /* areas load failure non-critical for autocomplete */ }
            // Pre-fetch wiki area ordering so autocomplete sorts areas by wiki orderingIndex
            // (same source as Area Flowcharts page). 5-second timeout inside the call; on
            // failure GetOrderingIndex returns double.MaxValue and we fall back to alpha sort.
            try { await DiscordFlowchartService.EnsureMappingLoadedAsync(); }
            catch (Exception ex) { AppLogger.Info($"[AUTOCOMPLETE-PREWARM] area ordering fetch failed: {ex.Message}"); }

            // STAGE 1 (fast, <1 s): text-only dataset — names/levels/areas. The prewarm task
            // completes here, so a dialog opened right after app start gets usable text
            // suggestions immediately instead of waiting for thumbnail resolution
            // (previously the whole build blocked the dialog for ~30 s on large datasets).
            var core = AutocompleteDataService.BuildCore(ds, wm, areas);
            CachedAutocomplete = core;

            // STAGE 2 (heavy, parallel): thumbnail resolution continues in the background.
            // Publishes a new dataset + fires AutocompleteDataRefreshed so open dialogs
            // re-pull and thumbnails appear. Staleness guard: if a newer prewarm replaced
            // CachedAutocomplete meanwhile, this result is discarded.
            _ = Task.Run(() =>
            {
                try
                {
                    using var _t2 = AppLogger.Timed("AutocompletePrewarm/Images");
                    var imageDir = AutocompleteDataService.ResolveImageDir(imgBase, apkVer);
                    var full = AutocompleteDataService.BuildImages(core, ds, wm, areas, imageDir, imgBase, apkVer);
                    if (ReferenceEquals(CachedAutocomplete, core))
                    {
                        CachedAutocomplete = full;
                        Dispatcher.InvokeAsync(() => AutocompleteDataRefreshed?.Invoke());
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[AUTOCOMPLETE-PREWARM] image stage failed: {ex.Message}");
                }
            });
            return core;
        });
    }

    /// <summary>
    /// Pre-loads Mystery events from events.json and checks wiki page existence.
    /// Runs during splash screen. Item resolution happens later when DataService loads.
    /// </summary>
    private async Task LoadMysteriesAsync()
    {
        var path = Settings.EventsJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        using var _t = AppLogger.Timed("LoadMysteriesAsync");
        try
        {
            // Shared per-path cache — MysteriesPage requesting the same path while this
            // preload is still running awaits the same parse instead of starting its own
            var svc = await _mysteryServiceCache.GetOrLoadAsync(path);
            MysteryService = svc;
            MysteryServiceLoadedPath = path;

            // Resolve items if DataService is already available
            if (DataService != null)
            {
                svc.ResolveEventItems(DataService);
                svc.ResolveRewardItems(DataService, WikiMapping, null);
            }

            // Update sidebar event count
            UpdateSidebarStatus();
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoadMysteriesAsync failed", ex);
            // Silently fail — user can reload from Mysteries page
        }
    }

    /// <summary>
    /// Called when chain data finishes loading — resolves mystery event items
    /// and checks any missing wiki statuses (item pages need resolved names).
    /// </summary>
    private void ResolveMysteryItems()
    {
        if (MysteryService == null || DataService == null) return;

        MysteryService.ResolveEventItems(DataService);
        MysteryService.ResolveRewardItems(DataService, WikiMapping, null);
        UpdateSidebarStatus();

        // Check item pages that couldn't be checked earlier (EventItemName was null)
        _ = CheckMissingItemPagesAsync();
    }

    private async Task CheckMissingItemPagesAsync()
    {
        if (MysteryService == null) return;

        // Collect mysteries that have a resolved item name but no item page check yet
        var needsCheck = MysteryService.Mysteries
            .Where(m => !string.IsNullOrEmpty(m.EventItemName)
                        && !m.WikiStatus.EventItemPageExists.HasValue)
            .ToList();

        if (needsCheck.Count == 0) return;

        try
        {
            var titles = needsCheck
                .Select(m => m.EventItemName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existMap = await MysteryWikiService.CheckPagesExistAsync(titles);

            foreach (var m in needsCheck)
                m.WikiStatus.EventItemPageExists = existMap.GetValueOrDefault(m.EventItemName!, false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("CheckMissingItemPagesAsync failed", ex);
            // Non-critical — user can re-check manually
        }
    }

    private void UpdateSidebarStatus()
    {
        var chainCount = DataService?.Chains.Count ?? 0;
        var itemCount = DataService?.ItemNames.Count ?? 0;
        var eventCount = MysteryService?.Mysteries.Count ?? 0;

        var eventText = eventCount > 0 ? $"{eventCount}" : "?";
        txtDataStatus.Text = $"{chainCount} chains · {itemCount} items · {eventText} events";

        // Show the game version of the actually-loaded data on its own line (derived from the
        // workspace folder, e.g. ...\APKs\26.05.01\Dump\chain_item_odds.json). This reflects
        // what's loaded, not the APK dropdown selection — the two can diverge.
        var version = ExtractVersionFromDataPath(Settings.ChainItemOddsPath);
        if (version != null)
        {
            txtVersionStatus.Text = $"v{version}";
            txtVersionStatus.Visibility = Visibility.Visible;
        }
        else
        {
            txtVersionStatus.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Pulls the game version out of a loaded data file path by finding the path segment
    /// shaped like a version number (e.g. "26.05.01" in ...\APKs\26.05.01\Dump\...).
    /// Returns null when no such segment exists.
    /// </summary>
    private static string? ExtractVersionFromDataPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var seg in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (Regex.IsMatch(seg, @"^\d+(\.\d+)+$"))
                return seg;
        return null;
    }

    /// <summary>
    /// Forces a fresh fetch of wiki mapping data. Called from SettingsPage refresh button.
    /// Reloads chain data from JSON to get fresh chains, then re-applies mapping.
    /// </summary>
    public async Task RefreshWikiMappingAsync()
    {
        WikiMapping = await WikiMappingService.FetchFromWikiAsync();

        // Reload data from JSON so mapping applies to fresh (unmodified) chains
        if (!string.IsNullOrEmpty(Settings.ChainItemOddsPath) && File.Exists(Settings.ChainItemOddsPath))
        {
            _wikiMappingApplied = false;
            await LoadDataAsync(Settings.ChainItemOddsPath);
        }
    }

    public void SaveSettings()
    {
        SettingsService.Save(Settings);
    }

    // ── Auto-update ──

    private ReleaseInfo? _pendingUpdate;

    public async Task CheckForUpdateAsync()
    {
        var release = await UpdateService.CheckForUpdateAsync();
        if (release == null) return;

        // Don't show the same version again if user already dismissed it
        if (_pendingUpdate?.TagName == release.TagName) return;
        _pendingUpdate = release;

        ShowUpdateDialog(release);
    }

    public void ShowUpdateDialog(ReleaseInfo release)
    {
        var dialog = new UpdateDialog(release) { Owner = this };
        dialog.Show();
    }

    /// <summary>
    /// Clears persisted dump file paths that no longer exist on disk.
    /// Runs once at startup so stale paths don't confuse users.
    /// </summary>
    private void ValidateDumpFilePaths()
    {
        bool changed = false;

        if (!string.IsNullOrEmpty(Settings.ChainItemOddsPath) && !File.Exists(Settings.ChainItemOddsPath))
        {
            Settings.ChainItemOddsPath = "";
            changed = true;
        }
        if (!string.IsNullOrEmpty(Settings.AreasJsonPath) && !File.Exists(Settings.AreasJsonPath))
        {
            Settings.AreasJsonPath = "";
            changed = true;
        }
        if (!string.IsNullOrEmpty(Settings.EventsJsonPath) && !File.Exists(Settings.EventsJsonPath))
        {
            Settings.EventsJsonPath = "";
            changed = true;
        }
        if (!string.IsNullOrEmpty(Settings.DialoguesJsonPath) && !File.Exists(Settings.DialoguesJsonPath))
        {
            Settings.DialoguesJsonPath = "";
            changed = true;
        }
        if (!string.IsNullOrEmpty(Settings.PetsJsonPath) && !File.Exists(Settings.PetsJsonPath))
        {
            Settings.PetsJsonPath = "";
            changed = true;
        }
        if (!string.IsNullOrEmpty(Settings.CardCollectionJsonPath) && !File.Exists(Settings.CardCollectionJsonPath))
        {
            Settings.CardCollectionJsonPath = "";
            changed = true;
        }

        if (changed) SaveSettings();
    }

    /// <summary>
    /// Updates areas.json path, saves settings, and notifies subscribers.
    /// </summary>
    public void SetAreasPath(string path)
    {
        Settings.AreasJsonPath = path;
        SaveSettings();
        // File may have been rewritten in place (dumper re-run) — force re-parse
        _areasServiceCache.Invalidate();
        AreasFileChanged?.Invoke();
    }

    /// <summary>
    /// Updates events.json path, saves settings, and notifies subscribers.
    /// </summary>
    public void SetEventsPath(string path)
    {
        Settings.EventsJsonPath = path;
        SaveSettings();
        // File may have been rewritten in place (dumper re-run) — force re-parse of
        // both events.json consumers (EventService + MysteryService)
        _eventServiceCache.Invalidate();
        _mysteryServiceCache.Invalidate();
        MysteryService = null; // Force reload
        MysteryServiceLoadedPath = null;
        _ = LoadMysteriesAsync();
        EventsFileChanged?.Invoke();
    }

    public void SetDialoguesPath(string path)
    {
        Settings.DialoguesJsonPath = path;
        SaveSettings();
        // File may have been rewritten in place (dumper re-run) — force re-parse
        _dialogueServiceCache.Invalidate();
    }

    public void SetPetsPath(string path)
    {
        Settings.PetsJsonPath = path;
        SaveSettings();
        if (File.Exists(path))
            MysteryWikiService.LoadPetDisplayNamesFromPath(path);
    }

    public void SetCardCollectionPath(string path)
    {
        Settings.CardCollectionJsonPath = path;
        SaveSettings();
    }

    /// <summary>
    /// Navigates to Settings page and highlights the chain_item_odds.json section.
    /// Called from WikiDataParserPage when user clicks the items file path link.
    /// </summary>
    /// <summary>
    /// Navigates to Image Optimiser in chain mode — pre-fills chain data for wiki upload workflow.
    /// </summary>
    public void NavigateToImageOptimiserChainMode(ParsedChain chain)
    {
        _imageOptimiserPage ??= new ImageOptimiserPage(this);
        if (Settings.ClearOptimiserOnChainEntry)
            _imageOptimiserPage.ClearAll();
        _imageOptimiserPage.EnterChainMode(chain);
        contentArea.Content = _imageOptimiserPage;
        navList.SelectedIndex = 1;
    }

    public void NavigateToImageOptimiserWithFile(string filePath)
    {
        _imageOptimiserPage ??= new ImageOptimiserPage(this);
        _imageOptimiserPage.AddFileFromPath(filePath);
        contentArea.Content = _imageOptimiserPage;
        navList.SelectedIndex = 1;
    }

    /// <summary>
    /// Opens Image Optimiser from Mysteries context — adds file, sets algorithm detection,
    /// and configures "Back to Mysteries" mode with callback for returning split results.
    /// </summary>
    public void NavigateToImageOptimiserForMystery(string filePath, Action<List<string>> onComplete)
    {
        _imageOptimiserPage ??= new ImageOptimiserPage(this);
        _imageOptimiserPage.ClearAll();
        _imageOptimiserPage.ForceAlgorithmDetection = true; // prevent atlas override
        _imageOptimiserPage.AddFileFromPath(filePath);
        _imageOptimiserPage.SetMysteryReturnMode(onComplete);
        contentArea.Content = _imageOptimiserPage;
        navList.SelectedIndex = 1;
    }

    /// <summary>
    /// Opens Image Optimiser from Prepare dialog context — adds file, maps chain, sets FloodFill,
    /// and configures "Back to Prepare" mode with callback for returning split results.
    /// </summary>
    public void NavigateToImageOptimiserForPrepare(string filePath, ParsedChain? chain, Action<List<string>> onComplete)
    {
        _imageOptimiserPage ??= new ImageOptimiserPage(this);
        _imageOptimiserPage.ClearAll();
        _imageOptimiserPage.ForceAlgorithmDetection = true;
        _imageOptimiserPage.AddFileFromPath(filePath);
        _imageOptimiserPage.SetMysteryReturnMode(onComplete, "Back to Mysteries");
        if (chain != null)
            _imageOptimiserPage.EnterChainMode(chain);
        contentArea.Content = _imageOptimiserPage;
        navList.SelectedIndex = 1;
    }

    public void NavigateToPage(int index)
    {
        navList.SelectedIndex = index;
    }

    public void NavigateToSettingsHighlightApk()
    {
        navList.SelectedIndex = 10;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightApkSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void NavigateToSettingsHighlightChainFile()
    {
        navList.SelectedIndex = 10;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightChainSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void NavigateToSettingsHighlightAreas()
    {
        navList.SelectedIndex = 10;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightAreasSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void NavigateToSettingsHighlightWikiMapping()
    {
        navList.SelectedIndex = 10;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightWikiMappingSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void NavigateToSettingsHighlightEvents()
    {
        navList.SelectedIndex = 10;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightEventsSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── Image overlay ──

    public void ShowImageOverlay(byte[] imageData)
    {
        var bi = new System.Windows.Media.Imaging.BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(imageData);
        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();

        overlayImage.Source = bi;
        imageOverlay.Visibility = Visibility.Visible;
        imageOverlay.Focus();
    }

    public void ShowImageOverlay(string filePath)
    {
        if (!File.Exists(filePath)) return;
        ShowImageOverlay(File.ReadAllBytes(filePath));
    }

    private void ImageOverlay_Close(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        imageOverlay.Visibility = Visibility.Collapsed;
        overlayImage.Source = null;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && imageOverlay.Visibility == Visibility.Visible)
        {
            imageOverlay.Visibility = Visibility.Collapsed;
            overlayImage.Source = null;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ── Theme change handler ──

    private void OnAppThemeChanged(ApplicationTheme currentTheme, Color systemAccent)
    {
        // Fix dark theme accent BEFORE Apply(this), because Apply(FrameworkElement)
        // copies Application.Current.Resources → Window.Resources.
        // If we fix after, Window.Resources retains the washed-out copy.
        if (currentTheme == ApplicationTheme.Dark)
        {
            App.ApplyDarkAccentFix(systemAccent);
        }

        // Update custom theme-aware brushes BEFORE Apply(this) copies them to Window.Resources
        var isLight = currentTheme == ApplicationTheme.Light;
        Application.Current.Resources["ChainExpandedContentBackground"] = new SolidColorBrush(
            isLight ? Color.FromArgb(0x20, 0, 0, 0) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Application.Current.Resources["SidebarItemSelectedBackground"] = new SolidColorBrush(
            isLight ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));

        // Now copy corrected resources to this window
        ApplicationThemeManager.Apply(this);
    }
}
