using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    // ── Shared services (accessible by pages) ──
    public AppSettings Settings { get; private set; }
    public ChainNameService ChainNameService { get; } = new();
    public DataService? DataService { get; private set; }

    private ChainBrowserPage? _chainPage;
    private ImageSplitterPage? _imageSplitterPage;
    private WikiDataParserPage? _wikiDataParserPage;
    private ImageExtractorPage? _imageExtractorPage;
    private SettingsPage? _settingsPage;
    private AboutPage? _aboutPage;

    public MainWindow()
    {
        InitializeComponent();

        Settings = SettingsService.Load();
        App.ApplyTheme(Settings.ThemePreference);
        ApplicationThemeManager.Apply(this);

        // Re-apply window resources whenever the global theme changes at runtime
        ApplicationThemeManager.Changed += OnAppThemeChanged;

        Loaded += (_, _) => UpdateNavIndicator(animate: false);

        // Track session
        Increment(s => { s.SessionCount++; if (s.FirstLaunch == default) s.FirstLaunch = DateTime.UtcNow; });

        // Try auto-load if path is configured
        if (!string.IsNullOrEmpty(Settings.ChainItemOddsPath) && File.Exists(Settings.ChainItemOddsPath))
        {
            _ = LoadDataAsync(Settings.ChainItemOddsPath);
        }

        // Show chains page by default (navList SelectedIndex=0 triggers this)
        ShowChainsPage();
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
        }
        return IntPtr.Zero;
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

    // ── Navigation ──

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (contentArea == null) return;

        switch (navList.SelectedIndex)
        {
            case 0: ShowChainsPage(); break;
            case 1: ShowImageSplitterPage(); break;
            case 2: ShowWikiDataParserPage(); break;
            case 3: ShowImageExtractorPage(); break;
            case 4: ShowSettingsPage(); break;
            case 5: ShowAboutPage(); break;
        }

        UpdateNavIndicator();
    }

    private void ShowChainsPage()
    {
        _chainPage ??= new ChainBrowserPage(this);
        contentArea.Content = _chainPage;
    }

    private void ShowImageSplitterPage()
    {
        _imageSplitterPage ??= new ImageSplitterPage(this);
        contentArea.Content = _imageSplitterPage;
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

    private void ShowSettingsPage()
    {
        _settingsPage ??= new SettingsPage(this);
        contentArea.Content = _settingsPage;
    }

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
        try
        {
            ShowStatus("Loading data...", InfoBarSeverity.Informational);
            txtDataStatus.Text = "Loading...";

            DataService = new DataService(ChainNameService);
            await DataService.LoadAsync(path);

            var chainCount = DataService.Chains.Count;
            var itemCount = DataService.ItemNames.Count;
            txtDataStatus.Text = $"{chainCount} chains · {itemCount} items · ? events";

            ShowStatus($"Loaded {chainCount} chains from {Path.GetFileName(path)}", InfoBarSeverity.Success);
            Increment(s => s.DataLoads++);

            // Notify chain page
            _chainPage?.OnDataLoaded();

            // Show warnings
            if (DataService.Warnings.Count > 0)
            {
                ShowStatus($"Loaded with {DataService.Warnings.Count} warning(s)", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
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

    public void SaveSettings()
    {
        SettingsService.Save(Settings);
    }

    /// <summary>
    /// Navigates to Settings page and highlights the chain_item_odds.json section.
    /// Called from WikiDataParserPage when user clicks the items file path link.
    /// </summary>
    public void NavigateToSettingsHighlightChainFile()
    {
        navList.SelectedIndex = 4;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightChainSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void NavigateToSettingsHighlightAreas()
    {
        navList.SelectedIndex = 4;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightAreasSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void NavigateToSettingsHighlightChunkSizes()
    {
        navList.SelectedIndex = 4;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightChunkSizes(),
            System.Windows.Threading.DispatcherPriority.Input);
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
