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
            _ = RefreshUsesIndexStateAsync(); // flag a stale Uses index (built from older Items/Areas)
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

    // Result container for the off-UI-thread GC merge step in BtnGenerateEvents_Click.
    private sealed class GcMergeResult
    {
        public string Lua { get; init; } = "";
        public string? VariousGridsSpliced { get; init; }
        public int GcGroupCount { get; init; }
        public int GcWritten { get; init; }
        public List<GarageCleanupGridService.GridChange>? GcChanges { get; init; }
        public int GcRewardCount { get; init; }
        /// <summary>GC base names whose stored grid KEYS changed vs live (new/renamed/removed variant, or a
        /// level-count change) — their event pages embed those keys in invokes, so the push auto-updates
        /// their == Garage Cleanup == section (page missing → skip; spec GarageCleanupGrids.md 2b).</summary>
        public List<string> ChangedGcBases { get; init; } = new();
    }

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

        // Clear pending push state so Update Wiki cannot push a stale/partial result if we abort.
        _pendingEventsExisting = null;
        _pendingVariousContent = null;
        _pendingEventsBaseTs = null;
        _pendingVariousBaseTs = null;
        _pendingGcChangedBases = null;
        _pendingGcGroupCount = 0;
        _pendingGcWritten = 0;
        _lastGcGridsLua = null;
        _lastGcChanges = null;
        _lastEventsChanges = null;
        _lastGcRewardCount = 0;

        try
        {
            // Step 1: Fetch live modules (read-only, no auth). Revision timestamps are captured so the
            // push can pass basetimestamp — if the module changes between this fetch and Update Wiki,
            // MediaWiki rejects the push (editconflict) instead of us silently overwriting the change.
            //   - Module:Datatable/Events — to merge historical runs.
            //   - Module:Datatable/Various — for the GC airings merge/splice.
            var (existing, existingTs) = await WikiMappingService.FetchModuleWithTimestampAsync("Module:Datatable/Events");
            var (liveVarious, liveVariousTs) = _main.DataService != null
                ? await WikiMappingService.FetchModuleWithTimestampAsync("Module:Datatable/Various")
                : (null, null);

            SetRowBusy(eventsIdle, eventsBusy, txtEventsBusy, true, "Generating schedule…");

            // Step 2 (off UI thread): parse events.json + merge live history → populates
            // schedule.Groups and schedule.PendingDecisions.
            var schedule = new EventScheduleService();
            await Task.Run(async () =>
            {
                using var _t = AppLogger.Timed("GenerateEventScheduleLua.Load");
                await schedule.LoadAsync(eventsPath, existing);
            });

            // Step 3 (UI thread): drift-decision dialog for each ambiguous Seasonal Event re-air.
            // Only Seasonal Events get a NeedsDecision entry (filtered in EventScheduleService);
            // all other categories auto-separate silently (variant A, both runs kept, no dialog).
            foreach (var d in schedule.PendingDecisions.ToList())
            {
                var dlg = new EventDriftDialog(d) { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
                if (dlg.Cancelled)
                {
                    ShowInfo("Generate Events cancelled.", InfoBarSeverity.Informational);
                    // Leave Update Wiki disabled (pending state was cleared above).
                    UpdateEventsWikiButtonState();
                    return;
                }
                schedule.ApplyDriftDecision(d, dlg.IsUpdate);
            }

            // Step 4 (off UI thread): GC airings merge (mode B) + final Events/Various Lua generation.
            SetRowBusy(eventsIdle, eventsBusy, txtEventsBusy, true, "Garage Cleanup grids…");

            var gcResult = await Task.Run(() =>
            {
                using var _t = AppLogger.Timed("GenerateEventScheduleLua");

                // Pass-1 (non-GC) lua — also passed to MergeAirings for parent-run matching.
                var nonGcLua = _luaGen.GenerateEventScheduleLua(schedule.Groups, schedule.AutoMergeWindows, schedule.CreatedAt);

                if (_main.DataService == null || liveVarious == null)
                    return new GcMergeResult { Lua = nonGcLua };

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(eventsPath));
                    if (!doc.RootElement.TryGetProperty("Data", out var data))
                        return new GcMergeResult { Lua = nonGcLua };

                    var gcService = new GarageCleanupGridService(_main.DataService, _main.WikiMapping);

                    // Phase-1 grid detection (for the change-list display in the Update dialog only).
                    var grids = gcService.Build(data);
                    var rewards = gcService.BuildRewards(data);
                    var combined = GarageCleanupGridService.Combine(grids, rewards);
                    var changes = gcService.Detect(combined, liveVarious, existing);
                    var rewardCount = rewards.Count;

                    // GC airings merge: reconstruct from live → add active dump airings (ADD-ONLY, §2.9).
                    var existingAirings = GarageCleanupGridService.ReconstructAirings(liveVarious, existing ?? "");
                    var active = gcService.CollectActiveAirings(data, DateTime.UtcNow);
                    var merged = gcService.MergeAirings(existingAirings, active, nonGcLua);
                    var gcWritten = merged.Sum(kv => kv.Value.Count) - existingAirings.Sum(kv => kv.Value.Count);

                    // Append GC groups (A3c: GC run history lives in Datatable/Events).
                    var gcEventGroups = gcService.BuildGcEventGroups(merged);
                    var gcGroupCount = gcEventGroups.Count;
                    schedule.Groups.AddRange(gcEventGroups);

                    // Grids-only block → Module:Datatable/Various (A4).
                    var gridsBlock = gcService.ComposeGarageCleanupBlocks(merged);
                    var content = GarageCleanupGridService.SpliceVarious(liveVarious, gridsBlock, "garageCleanupGrids") ?? liveVarious;
                    content = GarageCleanupGridService.RemoveVar(content, "garageCleanupRuns");
                    var spliced = content != liveVarious ? content : null;

                    // Affected event pages (2b page-edit): a page embeds its grid KEYS in invokes, so any
                    // base whose key set changed (new/renamed/removed variant) or whose level count changed
                    // needs its == Garage Cleanup == section refreshed — otherwise the page calls a key
                    // that no longer exists (the Legacy Lane "(2026)" vs "(May 2026)" Lua error).
                    var changedBases = new List<string>();
                    if (spliced != null)
                    {
                        var oldGrids = GarageCleanupGridService.ParseLive(liveVarious);
                        var newGrids = GarageCleanupGridService.ParseLive(spliced);
                        string BaseOf(string k) => GarageCleanupGridService.StripVariantSuffix(k);
                        var allBases = oldGrids.Keys.Concat(newGrids.Keys).Select(BaseOf)
                            .Distinct(StringComparer.Ordinal);
                        foreach (var b in allBases)
                        {
                            var ok = oldGrids.Where(kv => BaseOf(kv.Key) == b)
                                .ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
                            var nk = newGrids.Where(kv => BaseOf(kv.Key) == b)
                                .ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
                            bool same = ok.Count == nk.Count
                                && ok.All(kv => nk.TryGetValue(kv.Key, out var c) && c == kv.Value);
                            if (!same) changedBases.Add(b);
                        }
                    }

                    // Final lua now includes GC groups.
                    var finalLua = gcGroupCount > 0
                        ? _luaGen.GenerateEventScheduleLua(schedule.Groups, schedule.AutoMergeWindows, schedule.CreatedAt)
                        : nonGcLua;

                    return new GcMergeResult
                    {
                        Lua = finalLua,
                        VariousGridsSpliced = spliced,
                        GcGroupCount = gcGroupCount,
                        GcWritten = gcWritten,
                        GcChanges = changes,
                        GcRewardCount = rewardCount,
                        ChangedGcBases = changedBases,
                    };
                }
                catch (Exception gcEx)
                {
                    AppLogger.Debug($"GC merge failed during Generate Events: {gcEx.Message}");
                    schedule.Notes.Add($"Garage Cleanup grids: merge failed ({gcEx.Message}).");
                    return new GcMergeResult { Lua = nonGcLua };
                }
            });

            var lua = gcResult.Lua;
            _lastEventsLua = lua;
            // Semantic diff old (live) vs new — powers the Events review in the Update dialog.
            _lastEventsChanges = EventScheduleDiff.Compute(existing, lua);
            _lastGcChanges = gcResult.GcChanges;
            _lastGcRewardCount = gcResult.GcRewardCount;

            // Capture everything Update Wiki needs to push (no re-fetch, no re-merge at push time).
            _pendingEventsExisting = existing;
            _pendingVariousContent = gcResult.VariousGridsSpliced;
            _pendingEventsBaseTs = existingTs;
            _pendingVariousBaseTs = liveVariousTs;
            _pendingGcChangedBases = gcResult.ChangedGcBases;
            _pendingGcGroupCount = gcResult.GcGroupCount;
            _pendingGcWritten = gcResult.GcWritten;

            if (existing == null)
                schedule.Notes.Insert(0, "⚠ Live Module:Datatable/Events not found / unreachable — output has NO merged history. Do not overwrite the live module with this if it already has runs.");

            txtEventsHeader.Text =
                $"Events schedule — {schedule.Groups.Count} events · {schedule.RunCount} runs";

            var lineCount = lua.Count(c => c == '\n') + 1;
            var bytes = Encoding.UTF8.GetByteCount(lua);
            txtEventsCardLabel.Text =
                $"Module:Datatable/Events — {lineCount} lines • {FormatSize(bytes)}";

            // Notes are surfaced in the Update dialog's "Data changes" section (like Areas/Items),
            // not in the main card — keep the card a clean summary.
            txtEventsNotes.Visibility = Visibility.Collapsed;

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

    // Renders one Lua preview card per Uses-index shard (+ /Areas presence module) into the Uses Index
    // section, identical in look/behaviour to the Areas/Items chunk cards (header with line/size, Copy,
    // Consolas read-only preview, chunked lazy-load). Standalone (own CTS, copy captures the module Lua)
    // so it never touches the Areas/Items card state — conscious, isolated parallel of BuildAreaChunkCard.
    private void BuildUsesIndexCards(UsesIndexService.GeneratedUsesIndex gen)
    {
        usesIndexChunksContainer.Children.Clear();
        _usesIndexLoadCts?.Cancel(); _usesIndexLoadCts?.Dispose();
        _usesIndexLoadCts = new System.Threading.CancellationTokenSource();

        foreach (var sh in gen.Shards)
        {
            var card = BuildUsesIndexCard(sh.Title,
                $"UsesIndex/{sh.Number} ({sh.FirstLetter}–{sh.LastLetter})", sh.Lua);
            card.Margin = new Thickness(0, 0, 0, 8);
            usesIndexChunksContainer.Children.Add(card);
        }
        var areaCard = BuildUsesIndexCard(UsesIndexService.AreaChainsTitle, "UsesIndex/Areas (presence)", gen.AreaChainsLua);
        areaCard.Margin = new Thickness(0, 0, 0, 8);
        usesIndexChunksContainer.Children.Add(areaCard);
    }

    private FrameworkElement BuildUsesIndexCard(string moduleTitle, string label, string lua)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1)
        };
        var sp = new StackPanel();

        var headerGrid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lineCount = lua.Count(c => c == '\n') + 1;
        var sizeStr = FormatSize(Encoding.UTF8.GetByteCount(lua));
        var lbl = new WpfTextBlock
        {
            Text = $"{label} ({moduleTitle}) — {lineCount} lines • {sizeStr}",
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
            Text = "Loading...",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        miniLoading.Children.Add(new Wpf.Ui.Controls.ProgressRing
        {
            Width = 12, Height = 12, IsIndeterminate = true, Margin = new Thickness(4, 0, 0, 0)
        });
        Grid.SetColumn(miniLoading, 1);

        var copyBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Copy",
            Appearance = ControlAppearance.Secondary,
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0)
        };
        string capturedLua = lua;
        copyBtn.Click += (_, _) =>
        {
            App.NativeSetClipboardText(capturedLua);
            ShowInfo($"{label} copied to clipboard.", InfoBarSeverity.Success);
        };
        Grid.SetColumn(copyBtn, 2);

        headerGrid.Children.Add(lbl);
        headerGrid.Children.Add(miniLoading);
        headerGrid.Children.Add(copyBtn);
        sp.Children.Add(headerGrid);

        var warnPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 8)
        };
        var warnText = new WpfTextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        try { warnText.Foreground = (Brush)FindResource("SystemFillColorCautionBrush"); }
        catch { warnText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25)); }
        warnPanel.Children.Add(warnText);
        sp.Children.Add(warnPanel);

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
        sp.Children.Add(separator);
        sp.Children.Add(tb);
        border.Child = sp;

        _ = LazySetChunkFullTextAsync(tb, lua, miniLoading, warnPanel, warnText, label,
            _usesIndexLoadCts?.Token ?? System.Threading.CancellationToken.None);

        return border;
    }

    // Custom styled tooltip for the Uses-index freshness warning: a rounded, shadowed card with a
    // warning header, a one-line rule, and each drift as a wrapped "problem" + accent-coloured "action".
    // Replaces the default plain-string tooltip (which justified text and mangled the arrows).
    private System.Windows.Controls.ToolTip BuildFreshnessTooltip(string headline, List<(string problem, string action)> details)
    {
        var amber = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0x63, 0x0C));
        var stack = new StackPanel { MaxWidth = 380 };

        // HEADLINE = the action in ≤5 words, big + bold + amber, with a warning icon. The user must
        // know what to do from the first words; the details below are just the why/how.
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        titleRow.Children.Add(new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24,
            FontSize = 22, Foreground = amber,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        });
        titleRow.Children.Add(new WpfTextBlock
        {
            Text = headline,
            FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = amber,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(titleRow);

        stack.Children.Add(new WpfTextBlock
        {
            Text = "Items, Areas and the Uses index must come from the same dump.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        Brush accent;
        try { accent = (Brush)FindResource("AccentTextFillColorPrimaryBrush"); }
        catch { accent = (Brush)FindResource("TextFillColorPrimaryBrush"); }

        foreach (var (problem, action) in details)
        {
            stack.Children.Add(new WpfTextBlock
            {
                Text = "•  " + problem,
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                Margin = new Thickness(0, 6, 0, 0)
            });
            stack.Children.Add(new WpfTextBlock
            {
                Text = action,
                FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = accent,
                Margin = new Thickness(16, 1, 0, 0)
            });
        }

        // SOLID (opaque) background — CardBackgroundFillColor* is acrylic/semi-transparent and lets the
        // content behind the tooltip bleed through. SolidBackgroundFillColor* has no alpha.
        Brush bg;
        try { bg = (Brush)FindResource("SolidBackgroundFillColorSecondaryBrush"); }
        catch
        {
            try { bg = (Brush)FindResource("ApplicationBackgroundBrush"); }
            catch { bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x2B)); }
        }
        var border = new Border
        {
            Background = bg,
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = stack
        };

        // Tight shadow on the panel itself for subtle elevation.
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.45,
            Color = System.Windows.Media.Colors.Black
        };

        // Strip the default ToolTip chrome — the WPF-UI ToolTip style draws its OWN background/border/
        // shadow around the content, which showed as a ~10px outline beyond the custom panel. A minimal
        // template (just a ContentPresenter) leaves only the panel above.
        var tooltip = new System.Windows.Controls.ToolTip { Content = border, HasDropShadow = false };
        tooltip.Template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.ToolTip))
        {
            VisualTree = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter))
        };
        return tooltip;
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
