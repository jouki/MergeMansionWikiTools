using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;
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

    private const long MaxWikiBytes = 2 * 1024 * 1024; // 2 MB

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

    public WikiDataParserPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        RefreshStatus();
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
        else
        {
            txtItemsStatus.Text = "Items: no data loaded — load chain_item_odds.json in Settings";
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

        for (int i = 0; i < _chunkCardData.Count && i < _lastChunks.Count; i++)
        {
            var (preview, remaining) = SplitForPreview(_lastChunks[i].Lua);
            _chunkCardData[i].TextBox.Text = preview;
            _chunkCardData[i].MiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(_lastCombined))
        {
            var (preview, remaining) = SplitForPreview(_lastCombined);
            txtCombined.Text = preview;
            combinedMiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Called AFTER this page is set as ContentArea content.
    /// Refreshes status and fires chunked full-text loading for all cards.
    /// </summary>
    public void OnPageShown()
    {
        RefreshStatus();

        var chunkCt = _chunkLoadCts?.Token ?? CancellationToken.None;
        for (int i = 0; i < _chunkCardData.Count && i < _lastChunks.Count; i++)
        {
            var card = _chunkCardData[i];
            _ = LazySetChunkFullTextAsync(card.TextBox, _lastChunks[i].Lua,
                card.MiniLoading, card.WarnPanel, card.WarnText, _lastChunks[i].Label, chunkCt);
        }

        var combinedCt = _combinedLoadCts?.Token ?? CancellationToken.None;
        if (!string.IsNullOrEmpty(_lastCombined))
            _ = LazySetCombinedFullTextAsync(_lastCombined, combinedCt);
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

            var chunkSizes = _main.Settings.AreaChunkSizes;
            if (chunkSizes == null || chunkSizes.Count == 0) chunkSizes = new List<int> { 40 };

            // Generate Lua on background thread (can be CPU-intensive for large area sets)
            _lastChunks = await Task.Run(() =>
                _luaGen.GenerateAreaChunks(areasService.Areas, chunkSizes));

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

    // ── Generate Items ────────────────────────────────────────────────

    private async void BtnGenerateItems_Click(object sender, RoutedEventArgs e)
    {
        var chains = _main.DataService?.Chains;
        if (chains == null || chains.Count == 0)
        {
            ShowInfo("No chain data. Load chain_item_odds.json in Settings first.", InfoBarSeverity.Error);
            return;
        }

        btnGenerateItems.IsEnabled = false;
        ShowInfo("Generating items...", InfoBarSeverity.Informational);

        try
        {
            // Generate Lua + count lines on background thread
            var (lua, lineCount) = await Task.Run(() =>
            {
                var l = _luaGen.GenerateCombinedItemsAndChainNamesLua(chains);
                return (l, l.Count(c => c == '\n') + 1);
            });
            _lastCombined = lua;

            itemsSection.Visibility = Visibility.Visible;
            if (_itemsCollapsed)
            {
                itemsContent.Visibility = Visibility.Visible;
                iconCollapseItems.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                _itemsCollapsed = false;
            }

            txtCombinedHeader.Text = $"p.items + p.chainNames — {chains.Count} chains · {lineCount} lines";

            // Cancel any ongoing combined load
            _combinedLoadCts?.Cancel(); _combinedLoadCts?.Dispose();
            _combinedLoadCts = new CancellationTokenSource();

            // Show 150-line preview immediately (fast), then chunked-load full text
            var (preview, remaining) = SplitForPreview(lua);
            txtCombined.Text = preview;
            txtCombinedSizeWarning.Visibility = Visibility.Collapsed;
            combinedMiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;

            _ = LazySetCombinedFullTextAsync(lua, _combinedLoadCts.Token);

            Increment(s => s.LuaItemsGenerated++);
            ShowInfo("Items + Chain Names generated.", InfoBarSeverity.Success);
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

    // ── Chunk cards ──────────────────────────────────────────────────

    private void BuildChunkCards(List<(string Label, string Lua)> chunks)
    {
        chunksContainer.Children.Clear();
        _chunkCardData.Clear();

        for (int i = 0; i < chunks.Count; i++)
        {
            var card = BuildChunkCard(chunks[i].Label, chunks[i].Lua, i);
            card.Margin = new Thickness(0, 0, 0, 8);
            chunksContainer.Children.Add(card);
        }
    }

    private FrameworkElement BuildChunkCard(string label, string lua, int index)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1)
        };

        var sp = new StackPanel();

        // ── Header row: label | mini-loading | Copy ──
        var headerGrid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lineCount = lua.Count(c => c == '\n') + 1;
        var lbl = new WpfTextBlock
        {
            Text = $"Areas {label} — {lineCount} lines",
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
        int capturedIndex = index;
        copyBtn.Click += (_, _) => CopyChunk(capturedIndex);
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

        var settingsBtn = new Wpf.Ui.Controls.Button
        {
            Content = "→ Settings",
            Appearance = ControlAppearance.Secondary,
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        settingsBtn.Click += (_, _) => _main.NavigateToSettingsHighlightChunkSizes();

        warnPanel.Children.Add(warnText);
        warnPanel.Children.Add(settingsBtn);
        sp.Children.Add(warnPanel);

        // ── Separator + TextBox ──
        sp.Children.Add(new Separator { Opacity = 0.15, Margin = new Thickness(0) });

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
            // Show 150-line preview immediately — fast, no UI freeze
            Text = SplitForPreview(lua).Preview
        };
        sp.Children.Add(tb);
        border.Child = sp;

        // Track for re-navigation reset
        _chunkCardData.Add(new ChunkCardData(tb, miniLoading, warnPanel, warnText));

        // Async: wait for UI to render, then chunked-load full text
        _ = LazySetChunkFullTextAsync(tb, lua, miniLoading, warnPanel, warnText, label,
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
                $"⚠ Chunk \"{label}\" is {mb:F2} MB and exceeds the Wiki 2 MB limit. " +
                $"Reduce the number of areas per chunk in Settings.";
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

    // ── Copy ─────────────────────────────────────────────────────────

    private void CopyChunk(int index)
    {
        if (index >= 0 && index < _lastChunks.Count)
        {
            Clipboard.SetDataObject(_lastChunks[index].Lua, false);
            ShowInfo($"Chunk \"{_lastChunks[index].Label}\" copied to clipboard.", InfoBarSeverity.Success);
        }
    }

    private void BtnCopyCombined_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastCombined))
        {
            Clipboard.SetDataObject(_lastCombined, false);
            ShowInfo("Items + Chain Names copied to clipboard.", InfoBarSeverity.Success);
        }
    }

    // ── InfoBar ──────────────────────────────────────────────────────

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }
}
