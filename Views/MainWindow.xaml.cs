using System.IO;
using System.Windows;
using System.Windows.Controls;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class MainWindow : FluentWindow
{
    // ── Shared services (accessible by pages) ──
    public AppSettings Settings { get; private set; }
    public ChainNameService ChainNameService { get; } = new();
    public DataService? DataService { get; private set; }

    private ChainBrowserPage? _chainPage;
    private SettingsPage? _settingsPage;

    public MainWindow()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        Settings = SettingsService.Load();

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
            case 1: ShowSettingsPage(); break;
        }
    }

    private void ShowChainsPage()
    {
        _chainPage ??= new ChainBrowserPage(this);
        contentArea.Content = _chainPage;
    }

    private void ShowSettingsPage()
    {
        _settingsPage ??= new SettingsPage(this);
        contentArea.Content = _settingsPage;
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
            txtDataStatus.Text = $"{chainCount} chains · {itemCount} items";

            ShowStatus($"Loaded {chainCount} chains from {Path.GetFileName(path)}", InfoBarSeverity.Success);

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
}
