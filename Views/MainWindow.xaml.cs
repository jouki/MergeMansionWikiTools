using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private AssetRipperPage? _assetRipperPage;
    private AboutPage? _aboutPage;

    public MainWindow()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        Settings = SettingsService.Load();

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
            case 4: ShowAssetRipperPage(); break;
            case 5: ShowSettingsPage(); break;
            case 6: ShowAboutPage(); break;
        }
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

    private void ShowAssetRipperPage()
    {
        _assetRipperPage ??= new AssetRipperPage(this);
        contentArea.Content = _assetRipperPage;
    }

    private void ShowAboutPage()
    {
        _aboutPage ??= new AboutPage(this);
        _aboutPage.RefreshStats();
        contentArea.Content = _aboutPage;
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
        navList.SelectedIndex = 5;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightChainSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void NavigateToSettingsHighlightAreas()
    {
        navList.SelectedIndex = 5;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightAreasSection(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void NavigateToSettingsHighlightChunkSizes()
    {
        navList.SelectedIndex = 5;
        Dispatcher.InvokeAsync(
            () => _settingsPage?.HighlightChunkSizes(),
            System.Windows.Threading.DispatcherPriority.Input);
    }
}
