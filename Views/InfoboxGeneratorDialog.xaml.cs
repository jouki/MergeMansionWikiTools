using System.IO;
using System.Windows;
using System.Windows.Controls;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public partial class InfoboxGeneratorDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly ParsedChain _chain;
    private readonly InfoboxGeneratorService _service;
    private List<LuaArea>? _areas;
    private EventService? _eventService;
    private List<string> _autoSources = new();
    private bool _suppressRegenerate;
    private string? _wikiNameWarning;

    public InfoboxGeneratorDialog(MainWindow main, ParsedChain chain)
    {
        _main = main;
        _chain = chain;
        _service = new InfoboxGeneratorService(main.DataService!, main.WikiMapping);

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        txtChainInfo.Text = chain.DisplayName;
        txtChainDetail.Text = $"ConfigKey: {chain.ConfigKey} · {chain.Items.Count} levels · {chain.Summary}";

        // Load areas (task-reward source detection) + events (EventName vardefine)
        // asynchronously — the dialog shows immediately and the preview updates
        // once the data arrives (previously these loads blocked the UI thread).
        _ = InitializeDataAsync();

        // Hide "keep full name" if there's no parenthetical; otherwise pre-check it — the parenthetical
        // is disambiguation (incl. event suffix) the gallery image filename needs to hit the right File pages.
        if (!chain.DisplayName.Contains('('))
            chkKeepFullName.Visibility = Visibility.Collapsed;
        else
            chkKeepFullName.IsChecked = true;

        // Check for missing wiki name
        _wikiNameWarning = CheckWikiNameMissing(chain, main);

        Loaded += (_, _) => RegeneratePreview();
    }

    private async Task InitializeDataAsync()
    {
        try
        {
            await Task.WhenAll(LoadAreasAsync(), LoadEventsAsync());

            // Pre-populate sources with auto-detected (depends on _areas being loaded)
            var ds = _main.DataService;
            if (ds != null)
                _autoSources = _service.BuildAutoSources(_chain, ds.Chains, ds.ItemNames, _areas);

            // Refresh preview now that areas/events/auto-sources are available
            RegeneratePreview();
        }
        catch (Exception ex)
        {
            infoBar.Message = $"Data load error: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
    }

    private async Task LoadEventsAsync()
    {
        try
        {
            // Shared per-path cache on MainWindow — events.json is parsed once app-wide;
            // the getter also resolves chains against the current DataService when loaded
            _eventService = await _main.GetEventServiceAsync();
        }
        catch { /* event loading failure is non-critical; EventName vardefine just won't be emitted */ }
    }

    private async Task LoadAreasAsync()
    {
        try
        {
            // Shared per-path cache on MainWindow — areas.json is parsed once app-wide
            // instead of on every dialog open (path/existence checks live in the getter)
            _areas = (await _main.GetAreasServiceAsync())?.Areas;
        }
        catch { /* areas loading failure is non-critical */ }
    }

    private static string? CheckWikiNameMissing(ParsedChain chain, MainWindow main)
    {
        if (chain.HasHumanReadableName) return null;

        var mapping = main.WikiMapping;
        if (mapping == null || mapping.Mappings.Count == 0) return null;

        bool hasWikiEntry = chain.Items.Any(i =>
            !string.IsNullOrEmpty(i.ItemType) && mapping.Mappings.ContainsKey(i.ItemType));

        if (hasWikiEntry) return null;

        return $"Chain \"{chain.DisplayName}\" has no human-readable name and is not mapped on the wiki. " +
               "The generated {{Item}} templates may reference a non-existent page. " +
               "Consider adding a name to Module:Datatable/Items/Mapping first.";
    }

    private void ChkOption_Changed(object sender, RoutedEventArgs e)
        => RegeneratePreview();

    private void RegeneratePreview()
    {
        if (_main.DataService == null) return;

        var ds = _main.DataService;

        var opts = new InfoboxGeneratorOptions
        {
            AddShop     = chkAddShop.IsChecked == true,
            IsStarter   = chkIsStarter.IsChecked == true,
            IsPoints    = chkIsPoints.IsChecked == true,
            IsDepleting = chkIsDepleting.IsChecked == true,
            KeepFullName = chkKeepFullName.IsChecked == true,
            HardcodeGalleryImage = chkHardcodeGallery.IsChecked == true,
            EventName   = _chain.IsEventChain ? _eventService?.FindEventForChain(_chain)?.DisplayName : null,
        };

        try
        {
            var result = _service.Generate(_chain, ds.Chains, ds.ItemNames, opts, _autoSources);
            txtOutput.Text = result;
            txtOutput.Visibility = Visibility.Visible;
            txtOutputPlaceholder.Visibility = Visibility.Collapsed;
            btnCopy.IsEnabled = true;

            var allWarnings = new List<string>();
            if (_wikiNameWarning != null) allWarnings.Add(_wikiNameWarning);
            allWarnings.AddRange(_service.Warnings);

            if (allWarnings.Count > 0)
            {
                infoBar.Message = string.Join("\n", allWarnings);
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.IsOpen = true;
            }
            else
            {
                infoBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            infoBar.Message = $"Generation error: {ex.Message}";
            infoBar.Severity = InfoBarSeverity.Error;
            infoBar.IsOpen = true;
        }
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(txtOutput.Text)) return;

        App.NativeSetClipboardText(txtOutput.Text);
        Increment(s => s.InfoboxesGenerated++);
        bool success = true;

        btnCopy.Content = success ? "Copied!" : "Failed!";
        _ = ResetCopyButton();
    }

    private async Task ResetCopyButton()
    {
        await Task.Delay(2000);
        btnCopy.Content = "Copy to Clipboard";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
