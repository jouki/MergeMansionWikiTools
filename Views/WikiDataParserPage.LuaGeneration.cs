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

// Lua generation: Generate Areas/Items buttons, chunk card UI, copy-to-clipboard.
public partial class WikiDataParserPage
{
    // ── Generate Areas ───────────────────────────────────────────────

    private async void BtnGenerateAreas_Click(object sender, RoutedEventArgs e)
    {
        var areasPath = _main.Settings.AreasJsonPath;
        if (string.IsNullOrEmpty(areasPath) || !File.Exists(areasPath))
        {
            ShowInfo("Areas file not configured or not found. Set it in Settings.", InfoBarSeverity.Error);
            return;
        }

        SetGenerateButtonsEnabled(false);
        SetRowBusy(areasIdle, areasBusy, txtAreasBusy, true, "Generating areas…");
        ShowInfo("Loading areas...", InfoBarSeverity.Informational);

        try
        {
            // Shared per-path cache on MainWindow (single areas.json parse app-wide).
            // Lua generation below only reads Areas — the shared instance stays untouched.
            var areasService = await _main.GetAreasServiceAsync();
            if (areasService == null)
            {
                ShowInfo("Areas file not configured or not found. Set it in Settings.", InfoBarSeverity.Error);
                return;
            }
            _areasCreatedAt = areasService.CreatedAt;

            // Generate Lua on background thread (can be CPU-intensive for large area sets)
            _lastChunks = await Task.Run(() =>
            {
                using var _t = AppLogger.Timed("GenerateAreaChunks");
                return _luaGen.GenerateAreaChunks(areasService.Areas, _areasCreatedAt);
            });

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

            _areasChangelog = null;
            _areaOrdering = null;
            UpdateWikiButtonState();
            _ = CheckAreasDateAsync();
            Increment(s => s.LuaAreaChunksGenerated += _lastChunks.Count);
            ShowInfo($"Areas generated — {_lastChunks.Count} chunk(s).", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetRowBusy(areasIdle, areasBusy, txtAreasBusy, false);
            SetGenerateButtonsEnabled(true);
        }
    }

    // ── Generate Items ────────────────────────────────────────────────

    private async void BtnGenerateItems_Click(object sender, RoutedEventArgs e)
    {
        var chainPath = _main.Settings.ChainItemOddsPath;
        if (string.IsNullOrEmpty(chainPath) || !File.Exists(chainPath))
        {
            ShowInfo("Items file not configured or not found. Set it in Settings.", InfoBarSeverity.Error);
            return;
        }

        SetGenerateButtonsEnabled(false);
        SetRowBusy(itemsIdle, itemsBusy, txtItemsBusy, true, "Generating items…");
        ShowInfo("Generating items...", InfoBarSeverity.Informational);

        try
        {
            // INTENTIONAL fresh parse — do NOT replace with _main.DataService. The shared
            // instance has wiki-mapping overrides + custom chain names applied
            // (TryApplyWikiMapping / ChainNameService), while the Lua datatable must be
            // generated from pure game data (useRawNames: true). Keeping a dedicated
            // throwaway DataService here is the only duplicate parse left by design.
            var (itemChunks, chainNamesBlock, chainCount, createdAt, firstEventIdx, flatItems) = await Task.Run(async () =>
            {
                using var _t = AppLogger.Timed("GenerateItemChunks");
                var freshDs = new DataService(new ChainNameService());
                await freshDs.LoadAsync(chainPath);
                var result = _luaGen.GenerateItemChunks(freshDs.Chains, useRawNames: true, freshDs.CreatedAt);
                var cn = _luaGen.GenerateChainNamesLua(freshDs.Chains, useRawNames: true);
                return (result.Chunks, cn, freshDs.Chains.Count, freshDs.CreatedAt, result.FirstEventChunkIndex, result.FlatItems);
            });
            _lastItemChunks = itemChunks;
            _lastChainNamesBlock = chainNamesBlock;
            _itemsCreatedAt = createdAt;
            _firstEventChunkIndex = firstEventIdx;
            _lastFlatItems = flatItems;
            _lastArchiveDiff = null;
            _lastWikiItemEntries = null;
            _lastLocalItemEntries = null;
            _lastBrokenChainIds = null;

            // For backward compat: _lastCombined is the single module content (1 chunk)
            // or the first chunk preview (multi-chunk) — used by BuildItemNameMap, changelog
            _lastCombined = itemChunks.Count == 1 ? itemChunks[0].Lua : null;

            itemsSection.Visibility = Visibility.Visible;
            if (_itemsCollapsed)
            {
                itemsContent.Visibility = Visibility.Visible;
                iconCollapseItems.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                _itemsCollapsed = false;
            }

            // Cancel any ongoing combined load
            _combinedLoadCts?.Cancel(); _combinedLoadCts?.Dispose();
            _combinedLoadCts = new CancellationTokenSource();

            if (itemChunks.Count == 1)
            {
                // Single module — same UI as before
                var lua = itemChunks[0].Lua;
                var lineCount = lua.Count(c => c == '\n') + 1;
                txtCombinedHeader.Text = $"p.items + p.chainNames — {chainCount} chains · {lineCount} lines";

                // Show items chunk cards container empty, show combined card
                itemChunksContainer.Children.Clear();
                _itemChunkCardData.Clear();
                itemChunksContainer.Visibility = Visibility.Collapsed;
                combinedCard.Visibility = Visibility.Visible;

                var (preview, remaining) = SplitForPreview(lua);
                txtCombined.Text = preview;
                txtCombinedSizeWarning.Visibility = Visibility.Collapsed;
                combinedMiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;

                _ = LazySetCombinedFullTextAsync(lua, _combinedLoadCts.Token);
            }
            else
            {
                // Multi-chunk — show chunk cards, hide single combined card
                var totalLines = itemChunks.Sum(c => c.Lua.Count(ch => ch == '\n') + 1);
                var mainCount = _firstEventChunkIndex > 0 ? _firstEventChunkIndex - 1 : itemChunks.Count;
                var eventCount = itemChunks.Count - mainCount;
                var chunkDesc = _firstEventChunkIndex > 0
                    ? $"{itemChunks.Count} chunks ({mainCount} main, {eventCount} event)"
                    : $"{itemChunks.Count} chunks";
                txtCombinedHeader.Text = $"p.items + p.chainNames — {chainCount} chains · {chunkDesc} · {totalLines} lines";

                combinedCard.Visibility = Visibility.Collapsed;
                itemChunksContainer.Visibility = Visibility.Visible;
                BuildItemChunkCards(itemChunks, chainNamesBlock);
            }

            _itemsChangelog = null;
            UpdateItemsWikiButtonState();
            _ = CheckItemsDateAsync();
            Increment(s => s.LuaItemsGenerated++);
            var chunkInfo = itemChunks.Count > 1 ? $" ({itemChunks.Count} chunks)" : "";
            ShowInfo($"Items + Chain Names generated{chunkInfo}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetRowBusy(itemsIdle, itemsBusy, txtItemsBusy, false);
            SetGenerateButtonsEnabled(true);
        }
    }

    // ── Generate Events schedule ──────────────────────────────────────

    private async void BtnGenerateEvents_Click(object sender, RoutedEventArgs e)
    {
        var eventsPath = _main.Settings.EventsJsonPath;
        if (string.IsNullOrEmpty(eventsPath) || !File.Exists(eventsPath))
        {
            ShowInfo("Events file not configured or not found. Set it in Settings.", InfoBarSeverity.Error);
            return;
        }

        SetGenerateButtonsEnabled(false);
        SetRowBusy(eventsIdle, eventsBusy, txtEventsBusy, true, "Fetching live module…");
        ShowInfo("Loading event schedules...", InfoBarSeverity.Informational);

        try
        {
            // Fetch the live Module:Datatable/Events so historical runs (which the game config
            // drops after a re-air) are merged in, not overwritten. Read-only API — no auth.
            var existing = await WikiMappingService.FetchModuleContentAsync("Module:Datatable/Events");

            SetRowBusy(eventsIdle, eventsBusy, txtEventsBusy, true, "Generating schedule…");

            var schedule = new EventScheduleService();
            var lua = await Task.Run(async () =>
            {
                using var _t = AppLogger.Timed("GenerateEventScheduleLua");
                await schedule.LoadAsync(eventsPath, existing);
                return _luaGen.GenerateEventScheduleLua(schedule.Groups, schedule.CreatedAt);
            });
            _lastEventsLua = lua;

            if (existing == null)
                schedule.Notes.Insert(0, "⚠ Live Module:Datatable/Events not found / unreachable — output has NO merged history. Do not overwrite the live module with this if it already has runs.");

            txtEventsHeader.Text =
                $"Events schedule — {schedule.Groups.Count} events · {schedule.RunCount} runs";

            var lineCount = lua.Count(c => c == '\n') + 1;
            var bytes = Encoding.UTF8.GetByteCount(lua);
            txtEventsCardLabel.Text =
                $"Module:Datatable/Events — {lineCount} lines • {FormatSize(bytes)}";

            if (schedule.Notes.Count > 0)
            {
                txtEventsNotes.Text = string.Join("\n", schedule.Notes.Select(n => "• " + n));
                txtEventsNotes.Visibility = Visibility.Visible;
            }
            else
            {
                txtEventsNotes.Visibility = Visibility.Collapsed;
            }

            eventsSection.Visibility = Visibility.Visible;
            if (_eventsCollapsed)
            {
                eventsContent.Visibility = Visibility.Visible;
                iconCollapseEvents.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                _eventsCollapsed = false;
            }

            _combinedLoadCts ??= new CancellationTokenSource();
            var (preview, remaining) = SplitForPreview(lua);
            txtEvents.Text = preview;
            eventsMiniLoading.Visibility = remaining != null ? Visibility.Visible : Visibility.Collapsed;
            _ = LazySetEventsFullTextAsync(lua, _combinedLoadCts.Token);

            UpdateEventsWikiButtonState();
            ShowInfo($"Events schedule generated — {schedule.Groups.Count} events, {schedule.RunCount} runs.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetRowBusy(eventsIdle, eventsBusy, txtEventsBusy, false);
            SetGenerateButtonsEnabled(true);
        }
    }

    private void BtnCopyEvents_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastEventsLua))
        {
            App.NativeSetClipboardText(_lastEventsLua);
            ShowInfo("Events schedule copied to clipboard.", InfoBarSeverity.Success);
        }
    }

    // ── Chunk cards ──────────────────────────────────────────────────

    private void BuildChunkCards(List<(string Label, string Lua)> chunks)
    {
        chunksContainer.Children.Clear();
        _chunkCardData.Clear();

        // Arbiter card first (collapsed by default)
        var arbiterLua = WikiMappingService.GenerateAreasArbiterLua(chunks.Count);
        var arbiterCard = BuildAreaChunkCard("Arbiter", arbiterLua, -1, isArbiter: true);
        arbiterCard.Margin = new Thickness(0, 0, 0, 8);
        chunksContainer.Children.Add(arbiterCard);

        for (int i = 0; i < chunks.Count; i++)
        {
            var card = BuildAreaChunkCard(chunks[i].Label, chunks[i].Lua, i);
            card.Margin = new Thickness(0, 0, 0, 8);
            chunksContainer.Children.Add(card);
        }
    }

    private void BuildItemChunkCards(List<(string Label, string Lua)> chunks, string chainNamesBlock)
    {
        itemChunksContainer.Children.Clear();
        _itemChunkCardData.Clear();

        // Cancel any ongoing item chunk loads
        _combinedLoadCts?.Cancel(); _combinedLoadCts?.Dispose();
        _combinedLoadCts = new CancellationTokenSource();

        // Arbiter card first (collapsed by default)
        var arbiterPreview = WikiMappingService.GenerateItemsArbiterLua(
            chunks.Count, chainNamesBlock, _itemsCreatedAt);
        var arbiterCard = BuildItemChunkCard("Arbiter", arbiterPreview, -1, isArbiter: true);
        arbiterCard.Margin = new Thickness(0, 0, 0, 8);
        itemChunksContainer.Children.Add(arbiterCard);

        for (int i = 0; i < chunks.Count; i++)
        {
            // Determine main/event suffix for chunk label
            string? chunkSuffix = null;
            if (_firstEventChunkIndex > 0)
            {
                var chunkNumber = i + 1; // 1-based
                if (chunkNumber < _firstEventChunkIndex)
                    chunkSuffix = "Main";
                else if (chunkNumber == _firstEventChunkIndex)
                    chunkSuffix = "Event";
                else
                    chunkSuffix = "Event";
            }
            var card = BuildItemChunkCard(chunks[i].Label, chunks[i].Lua, i, chunkSuffix: chunkSuffix);
            card.Margin = new Thickness(0, 0, 0, 8);
            itemChunksContainer.Children.Add(card);
        }
    }

    private FrameworkElement BuildItemChunkCard(string label, string lua, int index, bool isArbiter = false, string? chunkSuffix = null)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1)
        };

        var sp = new StackPanel();

        // Header row
        var headerGrid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lineCount = lua.Count(c => c == '\n') + 1;
        var bytes = Encoding.UTF8.GetByteCount(lua);
        var sizeStr = FormatSize(bytes);
        var suffixStr = chunkSuffix != null ? $" ({chunkSuffix})" : "";
        var labelText = isArbiter
            ? $"Arbiter (Module:Datatable/Items) — {lineCount} lines \u2022 {sizeStr}"
            : $"Chunk {label}{suffixStr} (Module:Datatable/Items/{label}) — {lineCount} lines \u2022 {sizeStr}";

        var lbl = new WpfTextBlock
        {
            Text = labelText,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);

        var miniLoading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        miniLoading.Children.Add(new WpfTextBlock
        {
            Text = "Loading...", FontSize = 11,
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
        bool capturedIsArbiter = isArbiter;
        string capturedLua = lua;
        string capturedLabel = label;
        copyBtn.Click += (_, _) =>
        {
            App.NativeSetClipboardText(capturedLua);
            var desc = capturedIsArbiter ? "Arbiter" : $"Chunk {capturedLabel}";
            ShowInfo($"{desc} copied to clipboard.", InfoBarSeverity.Success);
        };
        Grid.SetColumn(copyBtn, 2);

        headerGrid.Children.Add(lbl);
        headerGrid.Children.Add(miniLoading);
        headerGrid.Children.Add(copyBtn);
        sp.Children.Add(headerGrid);

        // Warning panel
        var warnPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 8)
        };
        var warnText = new WpfTextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        try { warnText.Foreground = (Brush)FindResource("SystemFillColorCautionBrush"); }
        catch { warnText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25)); }
        warnPanel.Children.Add(warnText);
        sp.Children.Add(warnPanel);

        // Separator + TextBox (collapsible for arbiter)
        var separator = new Separator { Opacity = 0.15, Margin = new Thickness(0) };

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
            Text = SplitForPreview(lua).Preview
        };

        if (isArbiter)
        {
            // Wrap separator + textbox in a collapsible panel, collapsed by default
            var contentPanel = new StackPanel { Visibility = Visibility.Collapsed };
            contentPanel.Children.Add(separator);
            contentPanel.Children.Add(tb);
            sp.Children.Add(contentPanel);

            // Add chevron to header (theme-aware foreground via SetResourceReference)
            var chevron = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            chevron.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "TextFillColorSecondaryBrush");
            chevron.Margin = new Thickness(8, 0, 0, 0);
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(chevron, 3);
            headerGrid.Children.Add(chevron);

            // Make entire header row clickable for expand/collapse
            headerGrid.Background = Brushes.Transparent;
            headerGrid.Cursor = System.Windows.Input.Cursors.Hand;
            headerGrid.MouseLeftButtonDown += (_, _) =>
            {
                if (contentPanel.Visibility == Visibility.Visible)
                {
                    contentPanel.Visibility = Visibility.Collapsed;
                    chevron.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
                }
                else
                {
                    contentPanel.Visibility = Visibility.Visible;
                    chevron.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                }
            };
        }
        else
        {
            sp.Children.Add(separator);
            sp.Children.Add(tb);
        }

        border.Child = sp;

        // Track for re-navigation (only data chunks, not arbiter)
        if (!isArbiter)
            _itemChunkCardData.Add(new ChunkCardData(tb, miniLoading, warnPanel, warnText));

        // Async: chunked-load full text
        _ = LazySetChunkFullTextAsync(tb, lua, miniLoading, warnPanel, warnText,
            isArbiter ? "Arbiter" : $"Chunk {label}",
            _combinedLoadCts?.Token ?? CancellationToken.None);

        return border;
    }

    private FrameworkElement BuildAreaChunkCard(string label, string lua, int index, bool isArbiter = false)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1)
        };

        var sp = new StackPanel();

        // ── Header row: label | mini-loading | Copy | (chevron for arbiter) ──
        var headerGrid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lineCount = lua.Count(c => c == '\n') + 1;
        var bytes = Encoding.UTF8.GetByteCount(lua);
        var sizeStr = FormatSize(bytes);
        var labelText = isArbiter
            ? $"Arbiter (Module:Datatable/Areas) — {lineCount} lines \u2022 {sizeStr}"
            : $"Areas {label} (Module:Datatable/Areas/{label}) — {lineCount} lines \u2022 {sizeStr}";

        var lbl = new WpfTextBlock
        {
            Text = labelText,
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
        if (isArbiter)
        {
            string capturedLua = lua;
            copyBtn.Click += (_, _) =>
            {
                App.NativeSetClipboardText(capturedLua);
                ShowInfo("Arbiter copied to clipboard.", InfoBarSeverity.Success);
            };
        }
        else
        {
            int capturedIndex = index;
            copyBtn.Click += (_, _) => CopyChunk(capturedIndex);
        }
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
        warnPanel.Children.Add(warnText);
        sp.Children.Add(warnPanel);

        // ── Separator + TextBox ──
        var separator = new Separator { Opacity = 0.15, Margin = new Thickness(0) };

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
            Text = SplitForPreview(lua).Preview
        };

        if (isArbiter)
        {
            // Wrap separator + textbox in a collapsible panel, collapsed by default
            var contentPanel = new StackPanel { Visibility = Visibility.Collapsed };
            contentPanel.Children.Add(separator);
            contentPanel.Children.Add(tb);
            sp.Children.Add(contentPanel);

            // Add chevron to header (theme-aware foreground via SetResourceReference)
            var chevron = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            chevron.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "TextFillColorSecondaryBrush");
            chevron.Margin = new Thickness(8, 0, 0, 0);
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(chevron, 3);
            headerGrid.Children.Add(chevron);

            // Make entire header row clickable for expand/collapse
            headerGrid.Background = Brushes.Transparent;
            headerGrid.Cursor = System.Windows.Input.Cursors.Hand;
            headerGrid.MouseLeftButtonDown += (_, _) =>
            {
                if (contentPanel.Visibility == Visibility.Visible)
                {
                    contentPanel.Visibility = Visibility.Collapsed;
                    chevron.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
                }
                else
                {
                    contentPanel.Visibility = Visibility.Visible;
                    chevron.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
                }
            };
        }
        else
        {
            sp.Children.Add(separator);
            sp.Children.Add(tb);
        }

        border.Child = sp;

        // Track for re-navigation reset (only data chunks, not arbiter)
        if (!isArbiter)
            _chunkCardData.Add(new ChunkCardData(tb, miniLoading, warnPanel, warnText));

        // Async: wait for UI to render, then chunked-load full text
        _ = LazySetChunkFullTextAsync(tb, lua, miniLoading, warnPanel, warnText,
            isArbiter ? "Arbiter" : label,
            _chunkLoadCts?.Token ?? CancellationToken.None);

        return border;
    }

    // ── Copy ─────────────────────────────────────────────────────────

    private void CopyChunk(int index)
    {
        if (index >= 0 && index < _lastChunks.Count)
        {
            App.NativeSetClipboardText(_lastChunks[index].Lua);
            ShowInfo($"Chunk \"{_lastChunks[index].Label}\" copied to clipboard.", InfoBarSeverity.Success);
        }
    }

    private void BtnCopyCombined_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastCombined))
        {
            App.NativeSetClipboardText(_lastCombined);
            ShowInfo("Items + Chain Names copied to clipboard.", InfoBarSeverity.Success);
        }
        else if (_lastItemChunks.Count > 0)
        {
            // Multi-chunk: copy all chunks concatenated (for manual use)
            var all = string.Join("\n\n", _lastItemChunks.Select(c => c.Lua));
            App.NativeSetClipboardText(all);
            ShowInfo($"All {_lastItemChunks.Count} item chunks copied to clipboard.", InfoBarSeverity.Success);
        }
    }
}
