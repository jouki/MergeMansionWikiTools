using System.IO;
using System.Windows;
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

        var events = filtered.ToList();
        txtSummary.Text = $"{events.Count} event{(events.Count != 1 ? "s" : "")}";

        // Group by year
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
            items.Add(new EventCardItem(ev) { IsSelected = ev == _selectedEvent });
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
        txtPlaceholder.Text = ev.Chains.Count > 0
            ? "Click 'Generate Flowchart' to build the item chain graph."
            : "No chains found for this event.";
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
