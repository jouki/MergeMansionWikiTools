using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    public EventsPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        _ = TryLoadAsync();

        _main.ChainDataLoaded += () => Dispatcher.InvokeAsync(() =>
        {
            _eventService?.ResolveChains(_main.DataService!);
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
        var path = _main.Settings.EventsJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            _eventService = new EventService();
            await _eventService.LoadAsync(path);

            if (_main.DataService != null)
                _eventService.ResolveChains(_main.DataService);

            BuildEventList();
        }
        catch (Exception ex)
        {
            ShowInfo($"Failed to load events: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void BuildEventList()
    {
        eventListPanel.Children.Clear();
        if (_eventService == null || _eventService.Events.Count == 0)
        {
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
        int? lastYear = null;
        foreach (var ev in events)
        {
            int year = ev.StartDate?.Year ?? 0;
            if (year != lastYear)
            {
                lastYear = year;
                eventListPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = year > 0 ? year.ToString() : "Unknown",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(4, 12, 0, 4),
                    Foreground = (Brush)FindResource("TextFillColorSecondaryBrush")
                });
            }

            var card = CreateEventCard(ev);
            eventListPanel.Children.Add(card);
        }
    }

    private Border CreateEventCard(CollectibleBoardEvent ev)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = ev.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush")
        });

        var infoText = ev.EventId;
        if (ev.StartDate.HasValue)
            infoText = ev.StartDate.Value.ToString("d MMM yyyy");
        if (ev.Chains.Count > 0)
            infoText += $" · {ev.Chains.Count} chains";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = infoText,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush")
        });

        var border = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
            Tag = ev
        };

        border.MouseLeftButtonUp += (_, _) =>
        {
            _selectedEvent = ev;
            ShowEventDetail(ev);

            // Highlight selected card
            foreach (var child in eventListPanel.Children)
            {
                if (child is Border b)
                    b.Background = b == border
                        ? (Brush)FindResource("ControlFillColorDefaultBrush")
                        : Brushes.Transparent;
            }
        };

        return border;
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

    private void BtnGenerateFlowchart_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEvent == null || _main.DataService == null) return;

        try
        {
            var svg = EventChainFlowchartService.GenerateSvg(
                _selectedEvent.Chains, _main.DataService, _selectedEvent.DisplayName);
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
        BuildEventList();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }
}
