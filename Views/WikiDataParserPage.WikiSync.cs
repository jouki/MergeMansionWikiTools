using System.Globalization;
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

// Wiki sync: date/version checks, Update Wiki buttons (areas + items incl. archive/mapping post), update previews.
public partial class WikiDataParserPage
{
    // ── Update Wiki ─────────────────────────────────────────────────

    private void UpdateWikiButtonState()
    {
        btnUpdateWiki.IsEnabled = _main.Settings.WikiVerified && _lastChunks.Count > 0;
        iconAreasDateState.Visibility = Visibility.Collapsed;
        UpdateButtonTooltip(btnUpdateWiki, _lastChunks.Count > 0);
    }

    private async Task CheckAreasDateAsync()
    {
        if (string.IsNullOrEmpty(_areasCreatedAt) || _lastChunks.Count == 0) return;

        try
        {
            var content = await WikiMappingService.FetchModuleContentAsync("Module:Datatable/Areas/1");
            if (content == null) return;

            // Version check — block if wiki was uploaded by a newer MMWT version
            if (CheckWikiVersionNewer(content, btnUpdateWiki, iconAreasDateState))
                return;

            var wikiDate = WikiMappingService.ExtractCreatedAtFromContent(content);
            if (wikiDate != null)
            {
                var cmp = CompareDates(_areasCreatedAt, wikiDate);
                if (cmp < 0)
                    SetButtonOlderState(btnUpdateWiki, iconAreasDateState,
                        $"Local data ({_areasCreatedAt}) is older than wiki ({wikiDate})");
                else if (cmp == 0)
                    SetButtonSameDateState(btnUpdateWiki, iconAreasDateState,
                        $"Local data ({_areasCreatedAt}) has the same date as wiki");
            }
        }
        catch { }
    }

    private async Task CheckItemsDateAsync()
    {
        if (string.IsNullOrEmpty(_itemsCreatedAt)) return;

        try
        {
            var wikiContent = await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle);
            if (wikiContent == null) return;

            // Version check — block if wiki was uploaded by a newer MMWT version
            if (CheckWikiVersionNewer(wikiContent, btnUpdateItemsWiki, iconItemsDateState))
                return;

            var wikiDate = WikiMappingService.ExtractCreatedAtFromContent(wikiContent);
            if (wikiDate != null)
            {
                var cmp = CompareDates(_itemsCreatedAt, wikiDate);
                if (cmp < 0)
                    SetButtonOlderState(btnUpdateItemsWiki, iconItemsDateState,
                        $"Local data ({_itemsCreatedAt}) is older than wiki ({wikiDate})");
                else if (cmp == 0)
                    SetButtonSameDateState(btnUpdateItemsWiki, iconItemsDateState,
                        $"Local data ({_itemsCreatedAt}) has the same date as wiki");
            }
        }
        catch { }
    }

    /// <summary>
    /// Checks if the wiki module was uploaded by a newer MMWT version.
    /// Returns true (and sets button to error state) if the local version is older.
    /// If the wiki has no version tag, returns false (allows upload).
    /// </summary>
    private bool CheckWikiVersionNewer(string wikiContent,
        Wpf.Ui.Controls.Button btn, Wpf.Ui.Controls.SymbolIcon dateIcon)
    {
        var wikiVersion = WikiMappingService.ExtractMmwtVersionFromContent(wikiContent);
        if (wikiVersion == null) return false; // no version on wiki → allow

        try
        {
            var cmp = WikiMappingService.CompareVersions(Models.AppVersion.Version, wikiVersion);
            if (cmp < 0)
            {
                SetButtonOlderState(btn, dateIcon,
                    $"Wiki was updated by a newer MMWT version ({wikiVersion}), you have {Models.AppVersion.Version}");
                return true;
            }
        }
        catch { } // malformed version → allow

        return false;
    }

    /// <summary>
    /// Compares local and wiki dates (ISO 8601 lexicographic).
    /// Returns negative if local is older, 0 if equal, positive if local is newer.
    /// </summary>
    private static int CompareDates(string localDate, string wikiDate)
    {
        return string.Compare(localDate, wikiDate, StringComparison.OrdinalIgnoreCase);
    }

    private void SetButtonOlderState(Wpf.Ui.Controls.Button btn,
        Wpf.Ui.Controls.SymbolIcon dateIcon, string tooltip)
    {
        btn.IsEnabled = false;
        dateIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
        dateIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x14, 0x23));
        dateIcon.ToolTip = tooltip;
        dateIcon.Visibility = Visibility.Visible;
    }

    private void SetButtonSameDateState(Wpf.Ui.Controls.Button btn,
        Wpf.Ui.Controls.SymbolIcon dateIcon, string tooltip)
    {
        // Button stays enabled — just show warning icon
        dateIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
        dateIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25));
        dateIcon.ToolTip = tooltip;
        dateIcon.Visibility = Visibility.Visible;
    }

    private void UpdateButtonTooltip(Wpf.Ui.Controls.Button btn, bool hasData)
    {
        btn.ToolTip = hasData && !_main.Settings.WikiVerified
            ? "Wiki bot is not configured. Set up credentials in Settings."
            : null;
    }

    private async void BtnUpdateWiki_Click(object sender, RoutedEventArgs e)
    {
        using var _t = AppLogger.Timed("UpdateAreasWiki");
        if (!_main.Settings.WikiVerified)
        {
            ShowInfo("Wiki bot not verified. Configure credentials in Settings first.", InfoBarSeverity.Warning);
            return;
        }

        if (_lastChunks.Count == 0)
        {
            ShowInfo("No area chunks generated. Generate areas first.", InfoBarSeverity.Warning);
            return;
        }

        btnUpdateWiki.IsEnabled = false;

        try
        {
            // 1. Query existing modules on wiki
            ShowInfo("Querying existing area modules on wiki...", InfoBarSeverity.Informational);
            var existingIndices = await WikiMappingService.QueryExistingAreaModulesAsync();

            // 2. Compare local chunks vs wiki modules
            var localCount = _lastChunks.Count;
            var localIndices = Enumerable.Range(1, localCount).ToList();

            var toUpdate = localIndices.Where(i => existingIndices.Contains(i)).ToList();
            var toCreate = localIndices.Where(i => !existingIndices.Contains(i)).ToList();
            var toBlank = existingIndices.Where(i => i > localCount).ToList();

            // Fetch area ordering for changelog sorting
            if (_areaOrdering == null)
            {
                ShowInfo("Fetching area ordering...", InfoBarSeverity.Informational);
                _areaOrdering = await WikiMappingService.FetchAreaOrderingAsync();
            }

            // Compute changelog if not already done
            if (_areasChangelog == null && existingIndices.Count > 0)
            {
                ShowInfo("Comparing area data...", InfoBarSeverity.Informational);
                var wikiEntries = new Dictionary<string, string>();
                var fetchTasks = existingIndices.Select(async i =>
                {
                    var content = await WikiMappingService.FetchModuleContentAsync($"Module:Datatable/Areas/{i}");
                    return content != null
                        ? WikiMappingService.ExtractLuaAreaEntries(content, "areas")
                        : new Dictionary<string, string>();
                });
                foreach (var entries in await Task.WhenAll(fetchTasks))
                    foreach (var kv in entries) wikiEntries.TryAdd(kv.Key, kv.Value);

                var localEntries = new Dictionary<string, string>();
                foreach (var chunk in _lastChunks)
                    foreach (var kv in WikiMappingService.ExtractLuaAreaEntries(chunk.Lua, "areas"))
                        localEntries.TryAdd(kv.Key, kv.Value);

                _areasChangelog = ComputeAreasChangelog(wikiEntries, localEntries, _areaOrdering);
            }

            // 3. Build preview confirmation
            var totalActions = toUpdate.Count + toCreate.Count + toBlank.Count + 2; // +arbiter +modules page

            var previewBox = CreatePreviewDialog(
                "Update Areas Data on Wiki",
                BuildWikiUpdatePreview(toUpdate, toCreate, toBlank, _lastChunks.Count),
                "Update");

            if (await previewBox.ShowDialogAsync() != WpfMessageBoxResult.Primary)
            {
                btnUpdateWiki.IsEnabled = true;
                infoBar.IsOpen = false;
                return;
            }

            // 4. Authenticate
            ShowInfo("Authenticating with wiki...", InfoBarSeverity.Informational);
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                _main.Settings.WikiUsername, _main.Settings.WikiPassword);
            var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

            const string blankContent = "-- This module is no longer in use\nreturn {}";
            int done = 0;
            int created = 0, updated = 0, blanked = 0;

            // 5. Update/create data chunks
            for (int i = 0; i < localCount; i++)
            {
                var chunkIndex = i + 1;
                var title = $"Module:Datatable/Areas/{chunkIndex}";
                var isNew = toCreate.Contains(chunkIndex);
                var action = isNew ? "Create" : "Update";

                ShowInfo($"[{done + 1}/{totalActions}] {action} {title}...", InfoBarSeverity.Informational);

                var editResult = await WikiMappingService.EditModuleAsync(
                    client, csrfToken, title, _lastChunks[i].Lua,
                    $"{action} area data chunk {chunkIndex} (via MergeMansionWikiTools)");

                if (isNew) created++; else updated++;
                done++;
            }

            // 6. Blank excess modules
            foreach (var i in toBlank)
            {
                var title = $"Module:Datatable/Areas/{i}";
                ShowInfo($"[{done + 1}/{totalActions}] Blanking {title}...", InfoBarSeverity.Informational);

                await WikiMappingService.EditModuleAsync(
                    client, csrfToken, title, blankContent,
                    $"Blank unused area data chunk {i} (via MergeMansionWikiTools)");

                blanked++;
                done++;
            }

            // 7. Update arbiter module
            ShowInfo($"[{done + 1}/{totalActions}] Updating arbiter Module:Datatable/Areas...", InfoBarSeverity.Informational);
            var arbiterLua = WikiMappingService.GenerateAreasArbiterLua(localCount);
            await WikiMappingService.EditModuleAsync(
                client, csrfToken, "Module:Datatable/Areas", arbiterLua,
                $"Update area arbiter ({localCount} chunks) (via MergeMansionWikiTools)");
            done++;

            // 8. Update Modules page
            ShowInfo($"[{done + 1}/{totalActions}] Updating Modules page...", InfoBarSeverity.Informational);
            await WikiMappingService.UpdateAreasModulesPageAsync(client, csrfToken, localCount, _lastChunks);
            done++;

            // 9. Report success
            var parts = new List<string>();
            if (updated > 0) parts.Add($"{updated} updated");
            if (created > 0) parts.Add($"{created} created");
            if (blanked > 0) parts.Add($"{blanked} blanked");
            parts.Add("arbiter updated");
            parts.Add("Modules page updated");

            // 10. Check for areas missing from ordering mapping + stale/renamed mapping rows
            if (_areaOrdering != null)
            {
                var allLocalKeys = new HashSet<string>();
                foreach (var chunk in _lastChunks)
                    foreach (var kv in WikiMappingService.ExtractLuaAreaEntries(chunk.Lua, "areas"))
                        allLocalKeys.Add(kv.Key);

                var unmapped = allLocalKeys
                    .Where(k => !_areaOrdering.ContainsKey(k) && !AreaOrderingService.SkipNames.Contains(k))
                    .OrderBy(k => k)
                    .ToList();

                // Stale/rename detection needs areas.json + the current module content — probe them
                // up front (cheap) so the dialog also opens when there is nothing to ADD but a stale
                // row to rename/delete (e.g. localization arrived: "First Floor Pantry" → "Pantry").
                List<AreaUnlockInfo>? allAreasProbe = null;
                string? moduleContent = null;
                var existingCommented = new List<RemovedCommentedEntry>();
                // POZOR: plná kvalifikace — WikiDataParserPage má vlastní (nesouvisející) nested typ RenamedEntry
                var renames = new List<MergeMansionWikiTools.Services.RenamedEntry>();
                var staleDeletes = new List<MergeMansionWikiTools.Services.StaleEntry>();
                try
                {
                    allAreasProbe = await AreaOrderingService.LoadFromAreasJsonAsync(_main.Settings.AreasJsonPath);
                    moduleContent = await WikiMappingService.FetchModuleContentAsync("Module:Datatable/Areas/Mapping");
                    existingCommented = moduleContent != null
                        ? AreaOrderingService.ExtractCommentedEntries(moduleContent)
                        : new List<RemovedCommentedEntry>();
                    (renames, staleDeletes) = AreaOrderingService.DetectStaleEntries(
                        allAreasProbe, _areaOrdering, existingCommented);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Area stale/rename detection failed: {ex.Message}");
                }

                if (unmapped.Count > 0 || renames.Count > 0 || staleDeletes.Count > 0)
                {
                    ShowInfo($"Wiki updated — {string.Join(", ", parts)}.", InfoBarSeverity.Success);

                    // Load area unlock info and deduce ordering indices
                    List<AreaUnlockInfo> allAreas;
                    if (allAreasProbe != null)
                    {
                        allAreas = allAreasProbe;
                    }
                    else
                    {
                        try
                        {
                            allAreas = await AreaOrderingService.LoadFromAreasJsonAsync(_main.Settings.AreasJsonPath);
                        }
                        catch (Exception ex)
                        {
                            ShowInfo($"Failed to load areas.json for ordering deduction: {ex.Message}", InfoBarSeverity.Error);
                            return;
                        }
                    }

                    // Renamed areas keep their orderingIndex under the NEW name: exclude them from
                    // additions and seed the ordering with the new name so children (whose parent
                    // AreaId resolves to the new display name) and nextIdx compute correctly.
                    var effectiveOrdering = new Dictionary<string, double>(_areaOrdering, StringComparer.Ordinal);
                    foreach (var ren in renames.Where(r => !r.IsCommented))
                    {
                        effectiveOrdering.Remove(ren.OldName);
                        effectiveOrdering[ren.NewName] = ren.OrderingIndex;
                    }
                    var renameTargets = new HashSet<string>(renames.Select(r => r.NewName), StringComparer.Ordinal);
                    var unmappedEffective = unmapped.Where(n => !renameTargets.Contains(n)).ToList();

                    var deduced = AreaOrderingService.Deduce(allAreas, effectiveOrdering, unmappedEffective);

                    // KONVENCE (2026-06-11): patch maže jen komentované řádky oblastí, které znovu vkládá —
                    // diff proto ukazuje removals jen pro ně. Navíc no-op filtr: deduced COMMENTED entry,
                    // která už v modulu existuje jako identický komentovaný řádek (jméno + index), se
                    // nepostuje ani neukazuje (typicky in-prep oblast s Unlock = Impossible, jež už je
                    // správně zakomentovaná — např. Factory Office).
                    var deducedByName = deduced.ToDictionary(d => d.Name, StringComparer.Ordinal);
                    var noOpNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var d in deduced)
                    {
                        if (!d.IsCommented) continue;
                        if (existingCommented.Any(r => r.Name == d.Name
                                && Math.Abs(r.OrderingIndex - d.OrderingIndex) < 0.0001))
                            noOpNames.Add(d.Name);
                    }
                    deduced = deduced.Where(d => !noOpNames.Contains(d.Name)).ToList();
                    existingCommented = existingCommented
                        .Where(r => deducedByName.ContainsKey(r.Name) && !noOpNames.Contains(r.Name))
                        .ToList();

                    if (deduced.Count == 0 && existingCommented.Count == 0 &&
                        renames.Count == 0 && staleDeletes.Count == 0)
                    {
                        // Nothing to add, clear, rename or delete → silent return (vše už na wiki sedí)
                        return;
                    }

                    var dlg = new MissingOrderingDialog(
                        deduced,
                        existingCommented,
                        renames,
                        staleDeletes,
                        moduleContent,
                        _main.Settings.WikiUsername,
                        _main.Settings.WikiPassword)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    dlg.ShowDialog();

                    // Re-fetch ordering after potential edit so subsequent runs see the new indices
                    _areaOrdering = await WikiMappingService.FetchAreaOrderingAsync();
                    return;
                }
            }

            ShowInfo($"Wiki updated — {string.Join(", ", parts)}.", InfoBarSeverity.Success);

            // Index = f(Items, Areas) — regenerate after an Areas push, then re-check freshness so the
            // warning icon updates (or hides once everything is back in sync).
            try
            {
                await RegenerateUsesIndexAsync();
                await RefreshUsesIndexStateAsync();
            }
            catch (Exception ix) { AppLogger.Warn($"Uses index regen after areas push failed: {ix.Message}"); }
        }
        catch (Exception ex)
        {
            ShowInfo($"Wiki update failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnUpdateWiki.IsEnabled = _main.Settings.WikiVerified && _lastChunks.Count > 0;
            UpdateButtonTooltip(btnUpdateWiki, _lastChunks.Count > 0);
        }
    }

    private UIElement BuildWikiUpdatePreview(
        List<int> toUpdate, List<int> toCreate, List<int> toBlank, int chunkCount)
    {
        var root = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var primary = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var subtle = (Brush)FindResource("SubtleFillColorSecondaryBrush");

        // Fixed top section (pinned, won't scroll)
        var topSection = new StackPanel();
        DockPanel.SetDock(topSection, Dock.Top);

        topSection.Children.Add(new WpfTextBlock
        {
            Text = $"{toUpdate.Count + toCreate.Count + toBlank.Count + 2} module(s) will be edited",
            FontSize = 13, Foreground = secondary, Margin = new Thickness(0, 0, 0, 4)
        });

        topSection.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 4, 0, 10),
            Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
        });

        // Helper: add a step row to the top section (delegates to the shared card builder so the
        // Areas/Items and Events dialogs render identical cards).
        void AddStep(string icon, string title, string? detail = null, string? detail2 = null, string? url = null)
            => AddDialogStepCard(topSection, icon, title, detail, detail2, url);

        const string wikiBase = "https://merge-mansion.fandom.com/wiki/";

        foreach (var i in toUpdate)
        {
            var lines = _lastChunks[i - 1].Lua.Count(c => c == '\n') + 1;
            AddStep("\uD83D\uDCDD", $"Update Module:Datatable/Areas/{i}",
                $"{lines} lines", $"Overwrite existing data chunk",
                $"{wikiBase}Module:Datatable/Areas/{i}");
        }

        foreach (var i in toCreate)
        {
            var lines = _lastChunks[i - 1].Lua.Count(c => c == '\n') + 1;
            AddStep("\u2795", $"Create Module:Datatable/Areas/{i}",
                $"{lines} lines", "New data module",
                $"{wikiBase}Module:Datatable/Areas/{i}");
        }

        foreach (var i in toBlank)
        {
            AddStep("\uD83D\uDDD1", $"Blank Module:Datatable/Areas/{i}",
                "Module is no longer needed",
                "Will be replaced with empty return",
                $"{wikiBase}Module:Datatable/Areas/{i}");
        }

        AddStep("\uD83D\uDD17", "Update Module:Datatable/Areas",
            $"Arbiter \u2014 require() {chunkCount} chunk(s)",
            "Combines all data chunks into p.areas",
            $"{wikiBase}Module:Datatable/Areas");

        AddStep("\uD83D\uDCC4", "Update Modules page",
            $"Add/update {chunkCount} submodule link(s) with area ranges",
            "Keeps existing links",
            $"{wikiBase}Modules");

        root.Children.Add(topSection);

        // Changelog fills remaining space (ScrollViewer inside handles overflow)
        root.Children.Add(BuildChangelogElement(_areasChangelog, "area", primary, secondary, tertiary));

        return root;
    }

    // ── Update Items Wiki ────────────────────────────────────────────

    private const string ItemsModuleTitle = "Module:Datatable/Items";
    private const string EventsModuleTitle = "Module:Datatable/Events";

    private void UpdateItemsWikiButtonState()
    {
        var hasData = _lastItemChunks.Count > 0;
        btnUpdateItemsWiki.IsEnabled = _main.Settings.WikiVerified && hasData;
        iconItemsDateState.Visibility = Visibility.Collapsed;
        UpdateButtonTooltip(btnUpdateItemsWiki, hasData);
    }

    private void UpdateEventsWikiButtonState()
    {
        var hasData = !string.IsNullOrEmpty(_lastEventsLua);
        btnUpdateEventsWiki.IsEnabled = _main.Settings.WikiVerified && hasData;
        UpdateButtonTooltip(btnUpdateEventsWiki, hasData);
    }

    /// <summary>
    /// Pushes the pre-generated Events + Various content to the wiki.
    /// The merge pipeline (drift decisions, GC airings merge, Lua generation) runs in
    /// "Generate Events" — this method is a pure push of what was already computed.
    /// Conflict safety: the revision timestamps captured at generate time are passed as
    /// <c>basetimestamp</c>, so a module edited between Generate and this push is rejected by
    /// MediaWiki (editconflict) instead of silently overwritten (the 2026-07-01 Frosty incident).
    /// </summary>
    private async void BtnUpdateEventsWiki_Click(object sender, RoutedEventArgs e)
    {
        using var _t = AppLogger.Timed("UpdateEventsWiki");
        if (!_main.Settings.WikiVerified)
        {
            ShowInfo("Wiki bot not verified. Configure credentials in Settings first.", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrEmpty(_lastEventsLua))
        {
            ShowInfo("No events data generated. Generate events first.", InfoBarSeverity.Warning);
            return;
        }

        var lua = _lastEventsLua;
        var existing = _pendingEventsExisting;
        var variousGridsSpliced = _pendingVariousContent;
        var eventsBaseTs = _pendingEventsBaseTs;
        var variousBaseTs = _pendingVariousBaseTs;
        var gcChangedBases = _pendingGcChangedBases;
        var gcGroupCount = _pendingGcGroupCount;
        var gcWritten = _pendingGcWritten;

        btnUpdateEventsWiki.IsEnabled = false;
        SetGenerateButtonsEnabled(false);
        SetRowBusy(eventsIdle, eventsBusy, txtEventsBusy, true, "Preparing push…");

        try
        {
            var exists = existing != null;
            var lineCount = lua.Count(c => c == '\n') + 1;
            var sizeStr = FormatSize(Encoding.UTF8.GetByteCount(lua));

            // The on-screen preview already shows the final lua (set during Generate Events).
            // Show the confirmation dialog so the user can review before committing.
            SetRowBusy(eventsIdle, eventsBusy, txtEventsBusy, false);

            // Reconstruct a light schedule just for the run/event counts in the dialog header.
            // We don't re-run LoadAsync — the counts are already visible in the card header text,
            // so we parse them from the Lua directly (or use a placeholder).
            int runCount = lua.Count(c => c == '\n') > 0 ? -1 : 0; // placeholder; dialog uses lineCount instead

            var previewBox = CreatePreviewDialog(
                "Update Events Data on Wiki",
                BuildEventsUpdatePreviewPushOnly(exists, lineCount, sizeStr),
                exists ? "Update" : "Create");

            if (await previewBox.ShowDialogAsync() != WpfMessageBoxResult.Primary)
            {
                infoBar.IsOpen = false;
                return;
            }

            ShowInfo("Authenticating with wiki...", InfoBarSeverity.Informational);
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                _main.Settings.WikiUsername, _main.Settings.WikiPassword);
            var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

            var action = exists ? "Update" : "Create";
            ShowInfo($"{action} {EventsModuleTitle}...", InfoBarSeverity.Informational);

            // Push Events module. The Lua was generated from the live module state captured at
            // Generate Events time — basetimestamp makes MediaWiki reject the push if the module
            // changed since that capture (editconflict), so a stale snapshot can never clobber
            // a concurrent edit.
            await WikiMappingService.EditModuleAsync(
                client, csrfToken, EventsModuleTitle, lua,
                $"{action} event schedule data (via MergeMansionWikiTools)",
                baseTimestamp: eventsBaseTs);

            // (A4) Garage Cleanup GRIDS → Module:Datatable/Various.
            // The spliced content was computed during Generate Events (base = liveVarious at generate time).
            bool gcVariousWritten = false;
            if (variousGridsSpliced != null)
            {
                ShowInfo($"Updating Module:Datatable/Various (Garage Cleanup grids, +{gcWritten} new airing(s))…", InfoBarSeverity.Informational);
                await WikiMappingService.EditModuleAsync(client, csrfToken, "Module:Datatable/Various", variousGridsSpliced,
                    $"Update Garage Cleanup grids (+{gcWritten} airing(s), via MergeMansionWikiTools)",
                    baseTimestamp: variousBaseTs);
                gcVariousWritten = true;
            }

            // (2b page-edit) Auto-refresh the == Garage Cleanup == section of event pages whose grid
            // KEYS changed — those pages embed the keys in invokes, so a renamed/added variant would
            // otherwise leave them calling a key that no longer exists (Lua error). Page missing →
            // skipped (spec GarageCleanupGrids.md); section handling mirrors the manual per-event flow.
            string gcPagesSummary = "";
            if (gcVariousWritten && gcChangedBases is { Count: > 0 })
            {
                var (updated, skipped, failed) = await AutoUpdateGcPagesAsync(
                    client, csrfToken, variousGridsSpliced!, gcChangedBases);
                if (updated + skipped + failed > 0)
                    gcPagesSummary = $" GC sections: {updated} page(s) updated"
                        + (skipped > 0 ? $", {skipped} skipped" : "")
                        + (failed > 0 ? $", {failed} FAILED (see log)" : "") + ".";
            }

            ShowInfo($"Wiki updated — {EventsModuleTitle} ({lineCount} lines"
                + (gcGroupCount > 0 ? $", {gcGroupCount} GC group(s)" : "") + ")"
                + (gcVariousWritten ? $" + Datatable/Various (grids, +{gcWritten} GC airing(s))." : ".")
                + gcPagesSummary,
                InfoBarSeverity.Success);
            _autoRefreshAfterPush = true;   // re-generate below so the changelog reflects the pushed state
        }
        catch (WikiEditConflictException ex)
        {
            // The live module changed between Generate Events and this push — nothing was written.
            AppLogger.Error("UpdateEventsWiki edit conflict", ex);
            ShowInfo($"⚠ {ex.Message}", InfoBarSeverity.Warning);
            // Invalidate the pending state so the stale content can't be pushed again by accident.
            _lastEventsLua = null;
            _lastEventsChanges = null;
            _pendingEventsExisting = null;
            _pendingVariousContent = null;
            _pendingEventsBaseTs = null;
            _pendingVariousBaseTs = null;
            _pendingGcChangedBases = null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateEventsWiki failed", ex);
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetRowBusy(eventsIdle, eventsBusy, txtEventsBusy, false);
            SetGenerateButtonsEnabled(true);
            UpdateEventsWikiButtonState();
        }

        // Auto-refresh: after a successful push the live modules changed, so re-generate to refresh the
        // changelog to the post-push state (parents now live, resolved GC versions now present). Without
        // this the preview keeps showing the stale pre-push diff until the user regenerates by hand.
        if (_autoRefreshAfterPush)
        {
            _autoRefreshAfterPush = false;
            ShowInfo("Refreshing changelog from the updated wiki…", InfoBarSeverity.Informational);
            BtnGenerateEvents_Click(this, new RoutedEventArgs());
        }
    }

    // Set true by a successful Events push so the finally-block can trigger a regenerate (auto-refresh).
    private bool _autoRefreshAfterPush;

    /// <summary>
    /// (2b page-edit) Refreshes the <c>== Garage Cleanup ==</c> section on the event pages of the given
    /// GC base names, using the freshly-pushed grid keys from <paramref name="variousSpliced"/>. Each
    /// affected page shows a <see cref="GcPageUpdateDialog"/> (drift-dialog style) with a red/green diff
    /// of the current vs. new section — the user confirms or skips per page. Page missing → skip; no known
    /// insert anchor → skip (the manual flow's section picker covers that case); confirmed pages get the
    /// section surgically replaced/inserted + purged. Per-page failures are logged and don't stop the rest.
    /// </summary>
    private async Task<(int Updated, int Skipped, int Failed)> AutoUpdateGcPagesAsync(
        HttpClient client, string csrfToken, string variousSpliced, List<string> changedBases)
    {
        int updated = 0, skipped = 0, failed = 0;
        var grids = GarageCleanupGridService.ParseLive(variousSpliced);
        int index = 0;

        foreach (var baseName in changedBases)
        {
            index++;
            var pageTitle = GarageCleanupNameService.DeriveParent(baseName);
            try
            {
                ShowInfo($"Checking Garage Cleanup section on “{pageTitle}”…", InfoBarSeverity.Informational);

                var variants = grids
                    .Where(kv => GarageCleanupGridService.StripVariantSuffix(kv.Key) == baseName)
                    .Select(kv => new GarageCleanupGridService.GcSectionVariant { Name = kv.Key, LevelCount = kv.Value.Count })
                    .ToList();
                if (variants.Count == 0) { skipped++; continue; }   // base vanished from grids — nothing to render

                var section = GarageCleanupGridService.GenerateSectionWikitext(baseName, variants);
                var page = await MysteryWikiService.FetchPageContentAsync(pageTitle);
                if (page == null)
                {
                    AppLogger.Info($"GC auto page-update: \"{pageTitle}\" doesn't exist — skipped.");
                    skipped++;
                    continue;
                }

                var (content, action) = GarageCleanupGridService.SpliceEventPageSection(page, section);
                if (content == null)
                {
                    // No == Garage Cleanup == section and no Rewards/Progress Bar anchor — needs the manual
                    // flow's section picker (Events tab → Generate Garage Cleanup), don't guess here.
                    AppLogger.Info($"GC auto page-update: \"{pageTitle}\" has no section anchor — skipped (use the Events tab).");
                    skipped++;
                    continue;
                }
                if (content.TrimEnd() == page.TrimEnd()) { skipped++; continue; }   // already up to date

                // Diff just the GC section (old vs. what the splice will produce, incl. preserved prose)
                // and let the user confirm/skip per page — drift-dialog pattern, Mysteries diff look.
                var oldSection = GarageCleanupGridService.ExtractGcSection(page) ?? "";
                var newSection = GarageCleanupGridService.ExtractGcSection(content) ?? section;
                var diffs = MysteryWikiService.ComputeLineDiffs(oldSection, newSection);
                var dlg = new GcPageUpdateDialog(pageTitle, action, diffs, index, changedBases.Count)
                    { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
                if (!dlg.Confirmed)
                {
                    AppLogger.Info($"GC auto page-update: \"{pageTitle}\" skipped by user.");
                    skipped++;
                    continue;
                }

                ShowInfo($"Updating Garage Cleanup section on “{pageTitle}”…", InfoBarSeverity.Informational);
                await WikiMappingService.EditModuleAsync(client, csrfToken, pageTitle, content,
                    "Update Garage Cleanup section — grid keys changed (via MergeMansionWikiTools)");
                await WikiMappingService.PurgeAsync(client, pageTitle);
                AppLogger.Info($"GC auto page-update: \"{pageTitle}\" — {action}.");
                updated++;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"GC auto page-update failed for \"{pageTitle}\"", ex);
                failed++;
            }
        }
        return (updated, skipped, failed);
    }

    private UIElement BuildEventsUpdatePreviewPushOnly(bool moduleExists, int lineCount, string sizeStr)
    {
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var gold = new SolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x41));

        var root = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

        // Year-unresolved GC changes are SKIPPED (nothing written) — they're not changes, they're a
        // "set the year manually" warning that reappears every push. Exclude them from the change counts
        // and list them separately below so the changelog only counts what actually gets written.
        int gcNew = _lastGcChanges?.Count(c => c.Action == GarageCleanupGridService.GridAction.New) ?? 0;
        int gcSplit = _lastGcChanges?.Count(c => !c.YearUnresolved && c.Action is GarageCleanupGridService.GridAction.Split
            or GarageCleanupGridService.GridAction.AddVersion) ?? 0;
        int gcNote = _lastGcChanges?.Count(c => !c.YearUnresolved && c.Action == GarageCleanupGridService.GridAction.NoteIdentical) ?? 0;
        int gcSkipped = _lastGcChanges?.Count(c => c.YearUnresolved) ?? 0;
        int gcData = gcNew + gcSplit;
        bool gcRewards = _lastGcRewardCount > 0;
        bool hasVarious = _pendingVariousContent != null;
        int moduleCount = 1 + (hasVarious ? 1 : 0);

        root.Children.Add(new WpfTextBlock
        {
            Text = $"{moduleCount} module{(moduleCount == 1 ? "" : "s")} will be edited",
            FontSize = 13, Foreground = secondary, Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 4, 0, 10),
            Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
        });

        // Events schedule change summary (new events / new runs / field changes such as the GC parent).
        var evc = _lastEventsChanges;
        int evNewEvents = evc?.NewEvents.Count ?? 0;
        int evNewRuns = evc?.NewRuns.Count ?? 0;
        int evFields = evc?.FieldChanges.Count ?? 0;
        int evRemoved = (evc?.RemovedEvents.Count ?? 0) + (evc?.RemovedRuns.Count ?? 0);
        bool evHasChanges = evc?.HasChanges == true;

        AddDialogStepCard(root,
            moduleExists ? "📝" : "➕",
            $"{(moduleExists ? "Update" : "Create")} {EventsModuleTitle}",
            $"{lineCount} lines · {sizeStr}",
            !moduleExists ? "New data module"
                : evHasChanges
                    ? $"{evNewEvents} new event(s) · {evNewRuns} new run(s) · {evFields} field change(s)"
                        + (evRemoved > 0 ? $" · {evRemoved} removed" : "")
                    : "No schedule changes — historical runs preserved, nothing dropped",
            "https://merge-mansion.fandom.com/wiki/" + EventsModuleTitle);

        if (hasVarious)
        {
            var d1Parts = new List<string>();
            if (gcNew > 0) d1Parts.Add($"{gcNew} new grid(s)");
            if (gcSplit > 0) d1Parts.Add($"{gcSplit} replay version split(s)");
            if (gcRewards) d1Parts.Add($"{_lastGcRewardCount} reward table(s)");
            AddDialogStepCard(root, "📝", "Update Module:Datatable/Various",
                (d1Parts.Count > 0 ? "Update " + string.Join(" · ", d1Parts) : "Update Garage Cleanup data"),
                gcNote > 0 ? $"{gcNote} re-air identical to an older year (page note only)" : null,
                "https://merge-mansion.fandom.com/wiki/Module:Datatable/Various");
        }

        var changeParts = new List<string>();
        if (evHasChanges)
            changeParts.Add($"{evNewEvents} new · {evNewRuns} run · {evFields} field event schedule change(s)");
        if (gcData + gcNote > 0) changeParts.Add($"{gcNew} new · {gcSplit} split · {gcNote} note GC grid(s)");

        if (changeParts.Count > 0)
            root.Children.Add(new WpfTextBlock
            {
                Text = "Data changes: " + string.Join(", ", changeParts),
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = gold,
                Margin = new Thickness(0, 10, 0, 4)
            });

        // ── Events schedule: semantic change list + collapsible raw diff ──
        if (evHasChanges && evc != null)
        {
            static string Ymd(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            static string Dur(TimeSpan t) => $"+{(int)Math.Round(t.TotalDays)}d";
            static string Range(DateTime? a, DateTime? b) =>
                a == null ? "no runs"
                : b == null || a == b ? Ymd(a.Value)
                : $"{Ymd(a.Value)} → {Ymd(b.Value)}";

            var evContent = new StackPanel { Margin = new Thickness(18, 2, 0, 4) };

            // Green (added) / red (removed) rows with a faint tinted background — same colours as the
            // side-by-side diff views (DiffViewHelper), so the change list reads like a diff.
            Border DiffRow(Brush? bg)
            {
                var b = new Border
                {
                    Background = bg, CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 1, 0, 1)
                };
                evContent.Children.Add(b);
                return b;
            }
            void Added(string text) => DiffRow(DiffViewHelper.AddedBackground).Child = new WpfTextBlock
                { Text = text, FontSize = 11, Foreground = DiffViewHelper.AddedForeground, TextWrapping = TextWrapping.Wrap };
            void Removed(string text) => DiffRow(DiffViewHelper.RemovedBackground).Child = new WpfTextBlock
                { Text = text, FontSize = 11, Foreground = DiffViewHelper.RemovedForeground, TextWrapping = TextWrapping.Wrap };
            // A value change: label neutral, old value red, new value green — inline, one row.
            void Changed(string label, string oldVal, string newVal)
            {
                var tb = new WpfTextBlock { FontSize = 11, Foreground = secondary, TextWrapping = TextWrapping.Wrap };
                tb.Inlines.Add(new System.Windows.Documents.Run(label));
                tb.Inlines.Add(new System.Windows.Documents.Run(oldVal) { Foreground = DiffViewHelper.RemovedForeground });
                tb.Inlines.Add(new System.Windows.Documents.Run(" → ") { Foreground = secondary });
                tb.Inlines.Add(new System.Windows.Documents.Run(newVal) { Foreground = DiffViewHelper.AddedForeground });
                DiffRow(null).Child = tb;
            }

            foreach (var e in evc.NewEvents)
                Added($"+ new event: {e.Name} ({e.RunCount} run(s), {Range(e.FirstStart, e.LastStart)})");
            foreach (var r in evc.NewRuns)
                Added($"+ new run: {r.EventName} — {Ymd(r.Start)} ({Dur(r.Duration)})");
            foreach (var f in evc.FieldChanges)
            {
                var at = f.RunStart is { } rs ? $" @ {Ymd(rs)}" : "";
                if (string.IsNullOrEmpty(f.OldValue))          // field first written (e.g. GC parent) = an ADD
                    Added($"+ {f.Field} set: {f.EventName}{at} → {f.NewValue}");
                else if (string.IsNullOrEmpty(f.NewValue))     // field removed
                    Removed($"− {f.Field} cleared: {f.EventName}{at} (was {f.OldValue})");
                else                                           // value changed → old red, new green inline
                    Changed($"~ {f.Field}: {f.EventName}{at} — ", f.OldValue, f.NewValue);
            }
            foreach (var e in evc.RemovedEvents)
                Removed($"− removed event: {e.Name} ({e.RunCount} run(s))");
            foreach (var r in evc.RemovedRuns)
                Removed($"− removed run: {r.EventName} — {Ymd(r.Start)}");

            AddCollapsibleSection(root,
                $"Events schedule ({evNewEvents} new · {evNewRuns} run · {evFields} field"
                    + (evRemoved > 0 ? $" · {evRemoved} removed)" : ")"),
                gold, secondary, evContent, defaultExpanded: true);
        }

        if (gcData + gcNote > 0 && _lastGcChanges != null)
        {
            var gcContent = new StackPanel { Margin = new Thickness(18, 2, 0, 4) };
            void Added(string text) => gcContent.Children.Add(new Border
            {
                Background = DiffViewHelper.AddedBackground, CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 1, 0, 1),
                Child = new WpfTextBlock { Text = text, FontSize = 11, Foreground = DiffViewHelper.AddedForeground, TextWrapping = TextWrapping.Wrap }
            });
            void Note(string text) => gcContent.Children.Add(new WpfTextBlock
                { Text = text, FontSize = 11, Foreground = tertiary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 1, 0, 1) });

            // Only WRITTEN changes here (year-resolved). Split migrates plain → (oldYear) + adds (newYear).
            foreach (var c in _lastGcChanges.Where(c => !c.YearUnresolved && c.Action == GarageCleanupGridService.GridAction.Split))
                Added($"+ version: {c.EventName} — split into ({c.OldYear}) + ({c.NewYear})  [{c.Detail}]");
            foreach (var c in _lastGcChanges.Where(c => !c.YearUnresolved && c.Action == GarageCleanupGridService.GridAction.AddVersion))
                Added($"+ version: {c.EventName} ({c.NewYear})  [{c.Detail}]");
            foreach (var c in _lastGcChanges.Where(c => !c.YearUnresolved && c.Action == GarageCleanupGridService.GridAction.NoteIdentical))
                Note($"≡ note: {c.EventName} — {c.NewYear} identical to {c.IdenticalToYear} (page note only)");
            foreach (var c in _lastGcChanges.Where(c => c.Action == GarageCleanupGridService.GridAction.New))
                Added($"+ new: {c.EventName}");

            AddCollapsibleSection(root, $"Garage Cleanup grids ({gcNew} new · {gcSplit} split · {gcNote} note)", gold, secondary, gcContent);
        }

        // Year-unresolved GC grids: NOT written (skipped) — surfaced separately as a persistent
        // "needs manual attention" note so they don't masquerade as per-push changes.
        if (gcSkipped > 0 && _lastGcChanges != null)
        {
            var skipContent = new StackPanel { Margin = new Thickness(18, 2, 0, 4) };
            foreach (var c in _lastGcChanges.Where(c => c.YearUnresolved))
                skipContent.Children.Add(new WpfTextBlock
                {
                    // Detail = FirstDifference(dump, live wiki grid) — "wiki → dump" for the first diverging cell.
                    // Surfaced so the actual mismatch (often just a chain-name resolution difference, wiki → dump)
                    // is visible instead of the vague "year unresolved".
                    Text = $"⚠ {c.EventName} — grid differs from wiki"
                        + (string.IsNullOrEmpty(c.Detail) ? "" : $": {c.Detail}")
                        + "; not written (year unresolved) — resolve on the wiki manually.",
                    FontSize = 11, Foreground = tertiary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1)
                });
            AddCollapsibleSection(root,
                $"⚠ Needs manual attention — {gcSkipped} GC grid(s) not written (year unresolved)",
                new SolidColorBrush(Color.FromRgb(0xC9, 0x8A, 0x2B)), secondary, skipContent);
        }

        // Scroll the whole preview so a long change list (e.g. many GC parents set) can't hide behind
        // the dialog's action buttons. Capped below the dialog's own 88% so title + buttons stay visible.
        return new ScrollViewer
        {
            Content = root,
            MaxHeight = SystemParameters.WorkArea.Height * 0.72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    /// <summary>
    /// Reusable "step card" row for the wiki-update dialogs (icon + title + 2 detail lines, optional
    /// clickable wiki link). Shared by the Areas/Items dialog and the Events dialog so they look the same.
    /// </summary>
    private void AddDialogStepCard(StackPanel parent, string icon, string title,
        string? detail = null, string? detail2 = null, string? url = null)
    {
        var primary = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var subtle = (Brush)FindResource("SubtleFillColorSecondaryBrush");

        var row = new Border
        {
            Background = subtle, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconTb = new WpfTextBlock
        {
            Text = icon, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
        };
        Grid.SetColumn(iconTb, 0);
        grid.Children.Add(iconTb);

        var content = new StackPanel();
        if (url != null)
        {
            var titleTb = new WpfTextBlock
            {
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = primary, TextWrapping = TextWrapping.Wrap
            };
            var linkRun = new System.Windows.Documents.Run(title) { Cursor = System.Windows.Input.Cursors.Hand };
            linkRun.MouseEnter += (s, e) => linkRun.TextDecorations = TextDecorations.Underline;
            linkRun.MouseLeave += (s, e) => linkRun.TextDecorations = null;
            var capturedUrl = url;
            linkRun.MouseLeftButtonDown += (s, e) =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(capturedUrl) { UseShellExecute = true });
            titleTb.Inlines.Add(linkRun);
            content.Children.Add(titleTb);
        }
        else
        {
            content.Children.Add(new WpfTextBlock
            {
                Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = primary, TextWrapping = TextWrapping.Wrap
            });
        }
        if (detail != null)
            content.Children.Add(new WpfTextBlock { Text = detail, FontSize = 11, Foreground = secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (detail2 != null)
            content.Children.Add(new WpfTextBlock { Text = detail2, FontSize = 10, Foreground = tertiary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        row.Child = grid;
        parent.Children.Add(row);
    }

    private async void BtnUpdateItemsWiki_Click(object sender, RoutedEventArgs e)
    {
        using var _t = AppLogger.Timed("UpdateItemsWiki");
        AppLogger.Info($"[UpdateItems] click: chunks={_lastItemChunks.Count}, firstEventChunk={_firstEventChunkIndex}, createdAt={_itemsCreatedAt}, flatItems={_lastFlatItems?.Count ?? -1}");
        if (!_main.Settings.WikiVerified)
        {
            AppLogger.Warn("[UpdateItems] aborted: wiki bot not verified");
            ShowInfo("Wiki bot not verified. Configure credentials in Settings first.", InfoBarSeverity.Warning);
            return;
        }

        if (_lastItemChunks.Count == 0)
        {
            AppLogger.Warn("[UpdateItems] aborted: no chunks generated");
            ShowInfo("No items data generated. Generate items first.", InfoBarSeverity.Warning);
            return;
        }

        btnUpdateItemsWiki.IsEnabled = false;

        try
        {
            var isSingleModule = _lastItemChunks.Count == 1 && string.IsNullOrEmpty(_lastItemChunks[0].Label);

            if (isSingleModule)
            {
                // ── Single module path (no chunking) ──
                var singleLua = _lastItemChunks[0].Lua;

                ShowInfo("Checking wiki module...", InfoBarSeverity.Informational);
                var wikiContent = await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle);
                var exists = wikiContent != null;

                // Compute changelog
                if (_itemsChangelog == null && wikiContent != null)
                    _itemsChangelog = ComputeItemsChangelog(wikiContent, singleLua);

                var lineCount = singleLua.Count(c => c == '\n') + 1;
                var bytes = Encoding.UTF8.GetByteCount(singleLua);
                var sizeStr = FormatSize(bytes);

                var previewBox = CreatePreviewDialog(
                    "Update Items Data on Wiki",
                    BuildItemsUpdatePreviewSingle(exists, lineCount, sizeStr),
                    exists ? "Update" : "Create");

                if (await previewBox.ShowDialogAsync() != WpfMessageBoxResult.Primary)
                {
                    btnUpdateItemsWiki.IsEnabled = true;
                    infoBar.IsOpen = false;
                    return;
                }

                ShowInfo("Authenticating with wiki...", InfoBarSeverity.Informational);
                using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword);
                var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

                var action = exists ? "Update" : "Create";
                ShowInfo($"{action} {ItemsModuleTitle}...", InfoBarSeverity.Informational);

                await WikiMappingService.EditModuleAsync(
                    client, csrfToken, ItemsModuleTitle, singleLua,
                    $"{action} items + chainNames data (via MergeMansionWikiTools)");

                ShowInfo($"Wiki updated — {ItemsModuleTitle} ({lineCount} lines, {sizeStr}).", InfoBarSeverity.Success);
            }
            else
            {
                // ── Multi-chunk path ──
                ShowInfo("Querying existing item modules on wiki...", InfoBarSeverity.Informational);
                var existingIndices = await WikiMappingService.QueryExistingItemModulesAsync();

                var localCount = _lastItemChunks.Count;
                var localIndices = Enumerable.Range(1, localCount).ToList();

                var toUpdate = localIndices.Where(i => existingIndices.Contains(i)).ToList();
                var toCreate = localIndices.Where(i => !existingIndices.Contains(i)).ToList();
                var toBlank = existingIndices.Where(i => i > localCount).ToList();

                // Compute changelog (merge all wiki chunks vs all local chunks)
                if (_itemsChangelog == null && existingIndices.Count > 0)
                {
                    ShowInfo("Comparing item data...", InfoBarSeverity.Informational);
                    var wikiEntries = new Dictionary<string, string>();
                    var fetchTasks = existingIndices.Select(async i =>
                    {
                        var content = await WikiMappingService.FetchModuleContentAsync($"Module:Datatable/Items/{i}");
                        return content != null
                            ? WikiMappingService.ExtractLuaTableEntries(content, "items")
                            : new Dictionary<string, string>();
                    });
                    foreach (var entries in await Task.WhenAll(fetchTasks))
                        foreach (var kv in entries) wikiEntries.TryAdd(kv.Key, kv.Value);

                    // Also check arbiter module for items (if chunks didn't exist before)
                    if (wikiEntries.Count == 0)
                    {
                        var arbiterContent = await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle);
                        if (arbiterContent != null)
                            wikiEntries = WikiMappingService.ExtractLuaTableEntries(arbiterContent, "items");
                    }

                    var localEntries = new Dictionary<string, string>();
                    foreach (var chunk in _lastItemChunks)
                        foreach (var kv in WikiMappingService.ExtractLuaTableEntries(chunk.Lua, "items"))
                            localEntries.TryAdd(kv.Key, kv.Value);

                    _itemsChangelog = ComputeItemsChangelog(wikiEntries, localEntries);
                    _lastWikiItemEntries = wikiEntries;
                    _lastLocalItemEntries = localEntries;
                }
                else if (_itemsChangelog == null)
                {
                    // No existing chunks — check single module
                    var arbiterContent = await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle);
                    if (arbiterContent != null)
                    {
                        var localEntries = new Dictionary<string, string>();
                        foreach (var chunk in _lastItemChunks)
                            foreach (var kv in WikiMappingService.ExtractLuaTableEntries(chunk.Lua, "items"))
                                localEntries.TryAdd(kv.Key, kv.Value);

                        _itemsChangelog = ComputeItemsChangelog(arbiterContent, localEntries);
                        _lastWikiItemEntries = WikiMappingService.ExtractLuaTableEntries(arbiterContent, "items");
                        _lastLocalItemEntries = localEntries;
                    }
                }

                // Reset mapping state for this run
                _lastMappingPatchedContent = null;
                _lastMappingEnrichedCount = 0;
                _lastMappingHandledIds = new HashSet<string>(StringComparer.Ordinal);

                // Compute archive diff: existing Archive + newly-removed items + #missing#-chain shadows + restorations from live.
                // _lastWikiItemEntries[id] holds the raw Lua entry for items about to be removed/shadowed — that's
                // the canonical "last-known good" data we preserve in the archive.
                AppLogger.Debug($"[UpdateItems] _itemsChangelog: removed={_itemsChangelog?.Removed.Count}, added={_itemsChangelog?.Added.Count}, modified={_itemsChangelog?.Modified.Count}, renamed={_itemsChangelog?.Renamed?.Count ?? 0}");
                AppLogger.Debug($"[UpdateItems] _lastWikiItemEntries={_lastWikiItemEntries?.Count ?? -1}, _lastLocalItemEntries={_lastLocalItemEntries?.Count ?? -1}, _lastFlatItems={_lastFlatItems?.Count ?? -1}");
                if (_lastWikiItemEntries != null && _lastLocalItemEntries != null && _itemsChangelog != null)
                {
                    ShowInfo("Computing items archive diff...", InfoBarSeverity.Informational);
                    var existingArchiveContent = await WikiMappingService.FetchModuleContentAsync(ItemsArchiveService.ArchiveModuleTitle);
                    var existingArchive = ItemsArchiveService.ParseArchive(existingArchiveContent)
                        .ToDictionary(
                            kv => kv.Key,
                            kv => (IReadOnlyDictionary<string, string>)kv.Value,
                            StringComparer.Ordinal);

                    // Items still in local data but with broken chainName (`#missing#…` placeholder when game
                    // can't resolve a spreadsheet cell). Treat as archive shadows: they keep their old wiki entry
                    // (with proper chainName) so wiki pages keep rendering, but they're excluded from chainNames.
                    _lastBrokenChainIds = new HashSet<string>(StringComparer.Ordinal);
                    if (_lastFlatItems != null)
                    {
                        foreach (var f in _lastFlatItems)
                        {
                            if (!string.IsNullOrEmpty(f.ItemType) &&
                                !string.IsNullOrEmpty(f.ChainName) &&
                                f.ChainName.StartsWith("#missing#", StringComparison.Ordinal))
                                _lastBrokenChainIds.Add(f.ItemType);
                        }
                    }
                    AppLogger.Debug($"[UpdateItems] broken-chain ids ({_lastBrokenChainIds.Count}): {string.Join(", ", _lastBrokenChainIds.Take(10))}{(_lastBrokenChainIds.Count > 10 ? "..." : "")}");

                    // Archive source = Removed (in wiki, not in local) ∪ broken-chain (in local but unresolvable).
                    // ItemsArchiveService.Compute derives the bucket key from the item id (strip `_NN`) and
                    // overwrites the entry's chainName field — we don't need to fix anything here.
                    var archiveSourceRaw = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var id in _itemsChangelog.Removed)
                        if (_lastWikiItemEntries.TryGetValue(id, out var raw))
                            archiveSourceRaw[id] = raw;
                    foreach (var id in _lastBrokenChainIds)
                        if (_lastWikiItemEntries.TryGetValue(id, out var raw))
                            archiveSourceRaw[id] = raw;

                    // Fetch Module:Datatable/Items/Mapping. If a broken-chain item already has a non-#missing#
                    // chainName override there, the wiki page is already rendering correctly via mapping —
                    // skip from archive shadow. Plus enrich the mapping entry with any missing fields from
                    // the live items entry (without overwriting existing mapping fields).
                    ShowInfo("Fetching Module:Datatable/Items/Mapping...", InfoBarSeverity.Informational);
                    var mappingContent = await WikiMappingService.FetchModuleContentAsync(ItemsMappingService.MappingModuleTitle);
                    var mappingEntries = ItemsMappingService.ParseMappingModule(mappingContent);
                    var enrichedInners = new Dictionary<string, string>(StringComparer.Ordinal);
                    var brokenIdsHandledByMapping = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var id in _lastBrokenChainIds.ToList())
                    {
                        if (!mappingEntries.TryGetValue(id, out var mapEntry)) continue;
                        if (!mapEntry.Fields.TryGetValue("chainName", out var mapChain)) continue;
                        var chainStr = mapChain.Trim().Trim('"');
                        if (chainStr.StartsWith("#missing#", StringComparison.Ordinal)) continue;

                        // Mapping has a non-broken chainName override → wiki page works via mapping.
                        brokenIdsHandledByMapping.Add(id);

                        // Enrich mapping with missing fields from local items entry (skipping broken chainName).
                        if (!_lastLocalItemEntries.TryGetValue(id, out var localRaw)) continue;
                        // Strip outer braces from raw entry: "{key=val, ...}" → "key=val, ..."
                        var stripped = localRaw.Trim();
                        if (stripped.StartsWith("{") && stripped.EndsWith("}"))
                            stripped = stripped.Substring(1, stripped.Length - 2);
                        var (localFields, localOrder) = ItemsMappingService.ParseLuaFields(stripped);

                        bool changed = false;
                        foreach (var key in localOrder)
                        {
                            if (key == "chainName") continue; // mapping already has the corrected one
                            if (mapEntry.Fields.ContainsKey(key)) continue; // never overwrite existing
                            mapEntry.Fields[key] = localFields[key];
                            mapEntry.FieldOrder.Add(key);
                            changed = true;
                        }
                        if (changed)
                            enrichedInners[id] = mapEntry.EmitInnerLua();
                    }

                    AppLogger.Debug($"[UpdateItems] mapping: parsed {mappingEntries.Count} entries, enriched {enrichedInners.Count}, brokenIdsHandledByMapping={brokenIdsHandledByMapping.Count}");
                    if (enrichedInners.Count > 0 && mappingContent != null)
                    {
                        _lastMappingPatchedContent = ItemsMappingService.PatchMappingEntries(mappingContent, enrichedInners);
                        _lastMappingEnrichedCount = enrichedInners.Count;
                        AppLogger.Debug($"[UpdateItems] mapping patch ready: {enrichedInners.Count} entries, content size {mappingContent.Length} -> {_lastMappingPatchedContent.Length}");
                    }

                    // Drop mapping-handled ids from broken-chain set: they don't need archive shadow.
                    foreach (var id in brokenIdsHandledByMapping) _lastBrokenChainIds.Remove(id);
                    _lastMappingHandledIds = brokenIdsHandledByMapping;
                    AppLogger.Debug($"[UpdateItems] broken-chain ids after mapping handling: {_lastBrokenChainIds.Count}");
                    // Recompute archiveSourceRaw to reflect the reduced broken set.
                    archiveSourceRaw.Clear();
                    foreach (var id in _itemsChangelog.Removed)
                        if (_lastWikiItemEntries.TryGetValue(id, out var raw))
                            archiveSourceRaw[id] = raw;
                    foreach (var id in _lastBrokenChainIds)
                        if (_lastWikiItemEntries.TryGetValue(id, out var raw))
                            archiveSourceRaw[id] = raw;

                    // Live ids exclude broken-chain ones — so they stay archived (don't get treated as "Restored")
                    // even though they're still in chunks. resolveItem on wiki prefers archive when p.archived[id].
                    var liveIds = new HashSet<string>(_lastLocalItemEntries.Keys, StringComparer.Ordinal);
                    foreach (var id in _lastBrokenChainIds) liveIds.Remove(id);

                    _lastArchiveDiff = ItemsArchiveService.Compute(existingArchive, archiveSourceRaw, liveIds);
                    AppLogger.Debug($"[UpdateItems] archive diff: NewlyArchived={_lastArchiveDiff.NewlyArchived.Count}, Restored={_lastArchiveDiff.Restored.Count}, Carried={_lastArchiveDiff.Carried.Count}, FinalArchive chains={_lastArchiveDiff.FinalArchive.Count}");

                    // Build the unified Archived list for the changelog: preserved items go here regardless of
                    // mechanism (archive module or mapping override). User-facing way to confirm "nothing
                    // is silently lost" — every removed-from-live item shows up either in Removed (truly gone)
                    // or in Archived (data preserved on wiki via archive or mapping enrichment).
                    var archivedList = new List<ArchivedEntry>();
                    foreach (var entry in _lastArchiveDiff.NewlyArchived)
                        archivedList.Add(new ArchivedEntry(entry.ItemId, "archive", entry.ChainName));
                    if (_lastMappingHandledIds != null)
                    {
                        foreach (var id in _lastMappingHandledIds)
                        {
                            // Only report mapping-handled ids whose mapping entry ACTUALLY changes
                            // this run (got enriched with new fields). Items already fully covered by
                            // an existing mapping override (e.g. the CBE_SweetMess_Points_* point
                            // items, whose category localization is permanently #missing#) are pure
                            // no-ops — mapping module isn't re-posted for them, nothing changes on the
                            // wiki. Listing them every run as "archived" was misleading noise.
                            if (!enrichedInners.ContainsKey(id)) continue;
                            var chain = ExtractChainNameFromEntry(_lastWikiItemEntries.GetValueOrDefault(id, ""));
                            archivedList.Add(new ArchivedEntry(id, "mapping", chain));
                        }
                    }
                    _itemsChangelog.Archived = archivedList.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
                    AppLogger.Debug($"[UpdateItems] changelog Archived: {_itemsChangelog.Archived.Count} (archive={_lastArchiveDiff.NewlyArchived.Count}, mapping={_lastMappingHandledIds?.Count ?? 0})");
                }

                // Preview confirmation
                var previewBox = CreatePreviewDialog(
                    "Update Items Data on Wiki",
                    BuildItemsUpdatePreviewChunked(toUpdate, toCreate, toBlank, localCount),
                    "Update");

                if (await previewBox.ShowDialogAsync() != WpfMessageBoxResult.Primary)
                {
                    btnUpdateItemsWiki.IsEnabled = true;
                    infoBar.IsOpen = false;
                    return;
                }

                // Authenticate
                ShowInfo("Authenticating with wiki...", InfoBarSeverity.Informational);
                using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                    _main.Settings.WikiUsername, _main.Settings.WikiPassword);
                var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

                const string blankContent = "-- This module is no longer in use\nreturn {}";
                var willPostArchive = _lastArchiveDiff != null && _lastArchiveDiff.HasChanges;
                var willPostMapping = _lastMappingPatchedContent != null && _lastMappingEnrichedCount > 0;

                // Pre-flight: detect whether Module:Items needs the archive-loader patch.
                // We only post the patched module when (a) we actually have any archived items now
                // (so the new fallback would have something to find) AND (b) the marker isn't there.
                bool willPatchConsumer = false;
                string? patchedConsumerLua = null;
                var willHaveArchive = (_lastArchiveDiff != null && _lastArchiveDiff.FinalArchive.Count > 0);
                if (willHaveArchive)
                {
                    ShowInfo($"Checking {ItemsArchiveService.ItemsConsumerModuleTitle} for archive support...", InfoBarSeverity.Informational);
                    var consumerLua = await WikiMappingService.FetchModuleContentAsync(ItemsArchiveService.ItemsConsumerModuleTitle);
                    if (consumerLua != null)
                    {
                        try
                        {
                            var (patched, changed) = ItemsArchiveService.PatchConsumerModule(consumerLua);
                            if (changed)
                            {
                                willPatchConsumer = true;
                                patchedConsumerLua = patched;
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Anchor mismatch — abort cleanly so user investigates.
                            ShowInfo($"Cannot auto-patch {ItemsArchiveService.ItemsConsumerModuleTitle}: {ex.Message}", InfoBarSeverity.Error);
                            btnUpdateItemsWiki.IsEnabled = true;
                            return;
                        }
                    }
                }

                var totalActions = toUpdate.Count + toCreate.Count + toBlank.Count + 2 // +arbiter +modules page
                                   + (willPostArchive ? 1 : 0)
                                   + (willPostMapping ? 1 : 0)
                                   + (willPatchConsumer ? 1 : 0);
                int done = 0;
                int created = 0, updated = 0, blanked = 0;

                // Build broken-chain correction map: for items whose live chainName starts with "#missing#",
                // recover the last-known-good chain name from the archive (which already has the correct chainName
                // as its chain-bucket key). We patch chunk Lua before upload so live chunks get clean chainName values.
                var brokenChainCorrections = new Dictionary<string, string>(StringComparer.Ordinal);
                if (_lastBrokenChainIds != null && _lastArchiveDiff != null)
                {
                    foreach (var (chain, items) in _lastArchiveDiff.FinalArchive)
                    {
                        if (chain.StartsWith("#missing#", StringComparison.Ordinal)) continue; // defensive
                        foreach (var id in items.Keys)
                            if (_lastBrokenChainIds.Contains(id))
                                brokenChainCorrections[id] = chain;
                    }
                }

                // Upload data chunks
                for (int i = 0; i < localCount; i++)
                {
                    var chunkIndex = i + 1;
                    var title = $"Module:Datatable/Items/{chunkIndex}";
                    var isNew = toCreate.Contains(chunkIndex);
                    var act = isNew ? "Create" : "Update";

                    ShowInfo($"[{done + 1}/{totalActions}] {act} {title}...", InfoBarSeverity.Informational);

                    var chunkLua = _lastItemChunks[i].Lua;
                    if (brokenChainCorrections.Count > 0)
                        chunkLua = ItemsArchiveService.PatchBrokenChainNamesInChunk(chunkLua, brokenChainCorrections);

                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, title, chunkLua,
                        $"{act} item data chunk {chunkIndex} (via MergeMansionWikiTools)");

                    if (isNew) created++; else updated++;
                    done++;
                }

                // Blank excess
                foreach (var i in toBlank)
                {
                    var title = $"Module:Datatable/Items/{i}";
                    ShowInfo($"[{done + 1}/{totalActions}] Blanking {title}...", InfoBarSeverity.Informational);

                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, title, blankContent,
                        $"Blank unused item data chunk {i} (via MergeMansionWikiTools)");
                    blanked++;
                    done++;
                }

                // Upload Mapping module (enriched entries)
                if (willPostMapping && _lastMappingPatchedContent != null)
                {
                    ShowInfo($"[{done + 1}/{totalActions}] Enriching {ItemsMappingService.MappingModuleTitle}...", InfoBarSeverity.Informational);
                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, ItemsMappingService.MappingModuleTitle, _lastMappingPatchedContent,
                        $"Enrich {_lastMappingEnrichedCount} entries with missing item fields (via MergeMansionWikiTools)");
                    done++;
                }

                // Upload Archive module (when there are archive changes)
                int archivedItemCountForChainNames = 0;
                if (willPostArchive && _lastArchiveDiff != null)
                {
                    ShowInfo($"[{done + 1}/{totalActions}] Updating {ItemsArchiveService.ArchiveModuleTitle}...", InfoBarSeverity.Informational);
                    var archiveLua = LuaGeneratorService.BuildArchiveModule(_lastArchiveDiff.FinalArchive, _itemsCreatedAt);
                    var archParts = new List<string>();
                    if (_lastArchiveDiff.NewlyArchived.Count > 0) archParts.Add($"+{_lastArchiveDiff.NewlyArchived.Count} archived");
                    if (_lastArchiveDiff.Restored.Count > 0) archParts.Add($"-{_lastArchiveDiff.Restored.Count} restored");
                    var archSummary = $"Update items archive ({string.Join(", ", archParts)}) (via MergeMansionWikiTools)";
                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, ItemsArchiveService.ArchiveModuleTitle, archiveLua, archSummary);
                    done++;
                    archivedItemCountForChainNames = _lastArchiveDiff.FinalArchive.Sum(kv => kv.Value.Count);
                }

                // Upload arbiter — regenerate chainNames block to include archived ids in positional list
                // + emit p.archived flat marker map alongside. Filter out items whose live chainName starts
                // with "#missing#" — they're archived shadows so chainNames shouldn't list them under the
                // broken chain name (their old chainName from the archive is what wiki callers should use).
                ShowInfo($"[{done + 1}/{totalActions}] Updating arbiter {ItemsModuleTitle}...", InfoBarSeverity.Informational);
                var chainNamesBlockForArbiter = _lastChainNamesBlock!;
                string? archivedFlagsBlock = null;
                if (_lastArchiveDiff != null && _lastArchiveDiff.FinalArchive.Count > 0 && _lastFlatItems != null)
                {
                    var liveFlatItems = _lastFlatItems
                        .Where(f => string.IsNullOrEmpty(f.ChainName) ||
                                    !f.ChainName.StartsWith("#missing#", StringComparison.Ordinal))
                        .ToList();
                    chainNamesBlockForArbiter = LuaGeneratorService.BuildChainNamesTable(
                        liveFlatItems, _lastArchiveDiff.ArchivedIdsByChain());
                    archivedFlagsBlock = LuaGeneratorService.BuildArchivedFlagsTable(
                        _lastArchiveDiff.FinalArchive.Values.SelectMany(d => d.Keys));
                }
                var arbiterLua = WikiMappingService.GenerateItemsArbiterLua(
                    localCount, chainNamesBlockForArbiter, archivedFlagsBlock, _itemsCreatedAt);
                await WikiMappingService.EditModuleAsync(
                    client, csrfToken, ItemsModuleTitle, arbiterLua,
                    $"Update items arbiter ({localCount} chunks{(archivedItemCountForChainNames > 0 ? $", {archivedItemCountForChainNames} archived" : "")}) (via MergeMansionWikiTools)");
                done++;

                // Patch consumer Module:Items (lazy archive loader + resolveItem fallback)
                if (willPatchConsumer && patchedConsumerLua != null)
                {
                    ShowInfo($"[{done + 1}/{totalActions}] Patching {ItemsArchiveService.ItemsConsumerModuleTitle} for archive support...", InfoBarSeverity.Informational);
                    await WikiMappingService.EditModuleAsync(
                        client, csrfToken, ItemsArchiveService.ItemsConsumerModuleTitle, patchedConsumerLua,
                        $"Add archive loader + resolveItem fallback (via MergeMansionWikiTools)");
                    done++;
                }

                // Update Modules page
                ShowInfo($"[{done + 1}/{totalActions}] Updating Modules page...", InfoBarSeverity.Informational);
                await WikiMappingService.UpdateModulesPageAsync(client, csrfToken, localCount, _firstEventChunkIndex);
                done++;

                // Report success
                var parts = new List<string>();
                if (updated > 0) parts.Add($"{updated} updated");
                if (created > 0) parts.Add($"{created} created");
                if (blanked > 0) parts.Add($"{blanked} blanked");
                parts.Add("arbiter updated");
                parts.Add("Modules page updated");

                ShowInfo($"Wiki updated — {string.Join(", ", parts)}.", InfoBarSeverity.Success);
            }

            // Index = f(Items, Areas) — regenerate after an Items push, then re-check freshness so the
            // warning icon updates (or hides once everything is back in sync).
            try
            {
                await RegenerateUsesIndexAsync();
                await RefreshUsesIndexStateAsync();
            }
            catch (Exception ix) { AppLogger.Warn($"Uses index regen after items push failed: {ix.Message}"); }
        }
        catch (Exception ex)
        {
            ShowInfo($"Wiki update failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnUpdateItemsWiki.IsEnabled = _main.Settings.WikiVerified && _lastItemChunks.Count > 0;
            UpdateButtonTooltip(btnUpdateItemsWiki, _lastItemChunks.Count > 0);
        }
    }

    private UIElement BuildItemsUpdatePreviewSingle(bool moduleExists, int lineCount, string sizeStr)
    {
        var root = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };

        var primary = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var subtle = (Brush)FindResource("SubtleFillColorSecondaryBrush");

        // Fixed top section
        var topSection = new StackPanel();
        DockPanel.SetDock(topSection, Dock.Top);

        var action = moduleExists ? "overwritten" : "created";
        topSection.Children.Add(new WpfTextBlock
        {
            Text = $"1 module will be {action}",
            FontSize = 13, Foreground = secondary, Margin = new Thickness(0, 0, 0, 4)
        });

        topSection.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 4, 0, 10),
            Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
        });

        var row = new Border
        {
            Background = subtle, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconTb = new WpfTextBlock
        {
            Text = moduleExists ? "\uD83D\uDCDD" : "\u2795",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
        };
        Grid.SetColumn(iconTb, 0);
        grid.Children.Add(iconTb);

        var content = new StackPanel();
        content.Children.Add(new WpfTextBlock
        {
            Text = $"{(moduleExists ? "Update" : "Create")} {ItemsModuleTitle}",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new WpfTextBlock
        {
            Text = $"p.items + p.chainNames \u2014 {lineCount} lines \u2022 {sizeStr}",
            FontSize = 11, Foreground = secondary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
        });
        content.Children.Add(new WpfTextBlock
        {
            Text = moduleExists
                ? "Existing module content will be fully replaced"
                : "New module will be created on the wiki",
            FontSize = 10, Foreground = tertiary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        row.Child = grid;
        topSection.Children.Add(row);

        root.Children.Add(topSection);

        // Changelog fills remaining space
        root.Children.Add(BuildChangelogElement(_itemsChangelog, "item", primary, secondary, tertiary));

        return root;
    }

    private UIElement BuildItemsUpdatePreviewChunked(
        List<int> toUpdate, List<int> toCreate, List<int> toBlank, int chunkCount)
    {
        var root = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };

        var primary = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var subtle = (Brush)FindResource("SubtleFillColorSecondaryBrush");

        // Fixed top section
        var topSection = new StackPanel();
        DockPanel.SetDock(topSection, Dock.Top);

        var totalActions = toUpdate.Count + toCreate.Count + toBlank.Count + 2;
        var mainChunkCount = _firstEventChunkIndex > 0 ? _firstEventChunkIndex - 1 : chunkCount;
        var eventChunkCount = chunkCount - mainChunkCount;
        var chunkSummary = _firstEventChunkIndex > 0
            ? $"{chunkCount} chunks: {mainChunkCount} main + {eventChunkCount} event"
            : $"{chunkCount} chunks";
        topSection.Children.Add(new WpfTextBlock
        {
            Text = $"{totalActions} module(s) will be edited ({chunkSummary})",
            FontSize = 13, Foreground = secondary, Margin = new Thickness(0, 0, 0, 4)
        });

        topSection.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 4, 0, 10),
            Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
        });

        const string wikiBase = "https://merge-mansion.fandom.com/wiki/";

        // Helper: add a step row to the top section
        void AddStep(string icon, string title, string? detail = null, string? detail2 = null, string? url = null)
        {
            var row = new Border
            {
                Background = subtle, CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconEl = new WpfTextBlock
            {
                Text = icon, FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetColumn(iconEl, 0);
            g.Children.Add(iconEl);

            var sp = new StackPanel();
            if (url != null)
            {
                var titleTb = new WpfTextBlock
                {
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = primary, TextWrapping = TextWrapping.Wrap
                };
                var linkRun = new System.Windows.Documents.Run(title)
                {
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                linkRun.MouseEnter += (s, e) => linkRun.TextDecorations = TextDecorations.Underline;
                linkRun.MouseLeave += (s, e) => linkRun.TextDecorations = null;
                var capturedUrl = url;
                linkRun.MouseLeftButtonDown += (s, e) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(capturedUrl) { UseShellExecute = true });
                };
                titleTb.Inlines.Add(linkRun);
                sp.Children.Add(titleTb);
            }
            else
            {
                sp.Children.Add(new WpfTextBlock
                {
                    Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = primary, TextWrapping = TextWrapping.Wrap
                });
            }
            if (detail != null)
                sp.Children.Add(new WpfTextBlock
                {
                    Text = detail, FontSize = 11, Foreground = secondary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });
            if (detail2 != null)
                sp.Children.Add(new WpfTextBlock
                {
                    Text = detail2, FontSize = 10, Foreground = tertiary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });

            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            row.Child = g;
            topSection.Children.Add(row);
        }

        foreach (var i in toUpdate)
        {
            var lines = _lastItemChunks[i - 1].Lua.Count(c => c == '\n') + 1;
            var size = FormatSize(Encoding.UTF8.GetByteCount(_lastItemChunks[i - 1].Lua));
            var typeHint = _firstEventChunkIndex > 0
                ? (i < _firstEventChunkIndex ? " (Main)" : " (Event)")
                : "";
            AddStep("\uD83D\uDCDD", $"Update Module:Datatable/Items/{i}{typeHint}",
                $"{lines} lines \u2022 {size}", "Overwrite existing data chunk",
                $"{wikiBase}Module:Datatable/Items/{i}");
        }

        foreach (var i in toCreate)
        {
            var lines = _lastItemChunks[i - 1].Lua.Count(c => c == '\n') + 1;
            var size = FormatSize(Encoding.UTF8.GetByteCount(_lastItemChunks[i - 1].Lua));
            var typeHint = _firstEventChunkIndex > 0
                ? (i < _firstEventChunkIndex ? " (Main)" : " (Event)")
                : "";
            AddStep("\u2795", $"Create Module:Datatable/Items/{i}{typeHint}",
                $"{lines} lines \u2022 {size}", "New data module",
                $"{wikiBase}Module:Datatable/Items/{i}");
        }

        foreach (var i in toBlank)
        {
            AddStep("\uD83D\uDDD1", $"Blank Module:Datatable/Items/{i}",
                "Module is no longer needed",
                "Will be replaced with empty return",
                $"{wikiBase}Module:Datatable/Items/{i}");
        }

        // Mapping enrichment step (shown only if any entries got enriched)
        if (_lastMappingPatchedContent != null && _lastMappingEnrichedCount > 0)
        {
            AddStep("🧩", $"Enrich {ItemsMappingService.MappingModuleTitle}",
                $"+{_lastMappingEnrichedCount} entries enriched with missing fields from items data",
                "Existing mapping fields are preserved; only missing fields (name, level, desc, …) are appended",
                $"{wikiBase}{ItemsMappingService.MappingModuleTitle}");
        }

        // Archive step (shown only if there are archive changes)
        if (_lastArchiveDiff != null && _lastArchiveDiff.HasChanges)
        {
            var archAdds = _lastArchiveDiff.NewlyArchived.Count;
            var archRestores = _lastArchiveDiff.Restored.Count;
            var archCarried = _lastArchiveDiff.Carried.Count;
            var brokenCount = _lastBrokenChainIds?.Count ?? 0;
            var removedRegular = archAdds - brokenCount;
            var archParts = new List<string>();
            if (archAdds > 0)
            {
                if (brokenCount > 0 && removedRegular > 0)
                    archParts.Add($"+{archAdds} new archived ({removedRegular} removed, {brokenCount} #missing# shadow)");
                else if (brokenCount > 0)
                    archParts.Add($"+{brokenCount} #missing# chain shadow archived");
                else
                    archParts.Add($"+{archAdds} new archived");
            }
            if (archRestores > 0) archParts.Add($"-{archRestores} restored to live");
            if (archCarried > 0) archParts.Add($"{archCarried} kept");
            AddStep("\uD83D\uDDC4", $"Update {ItemsArchiveService.ArchiveModuleTitle}",
                string.Join(", ", archParts),
                "Preserves last-known full data of removed items + #missing# chain shadows so wiki pages keep working",
                $"{wikiBase}{ItemsArchiveService.ArchiveModuleTitle}");
        }

        AddStep("\uD83D\uDD17", $"Update {ItemsModuleTitle}",
            $"Arbiter \u2014 require() {chunkCount} chunk(s) + p.chainNames + p.archived",
            "Flat-merges all chunks into p.items; chainNames stays positional (ipairs-friendly), p.archived flat marker map for archived ids",
            $"{wikiBase}{ItemsModuleTitle}");

        // Show consumer patch step in dialog if archive is non-empty (we'll detect at click-time)
        if (_lastArchiveDiff != null && _lastArchiveDiff.FinalArchive.Count > 0)
        {
            AddStep("\uD83E\uDE79", $"Patch {ItemsArchiveService.ItemsConsumerModuleTitle} (if needed)",
                "Lazy archive loader + resolveItem fallback to Archive module",
                "Idempotent \u2014 only posts when the loader marker isn't already present",
                $"{wikiBase}{ItemsArchiveService.ItemsConsumerModuleTitle}");
        }

        var modulesDetail = _firstEventChunkIndex > 0
            ? $"Add/update {chunkCount} submodule link(s) with main/event annotations"
            : $"Add/update {chunkCount} submodule link(s)";
        AddStep("\uD83D\uDCC4", "Update Modules page",
            modulesDetail,
            "Keeps existing links like Datatable/Items/Mapping",
            $"{wikiBase}Modules");

        root.Children.Add(topSection);

        // Changelog fills remaining space
        root.Children.Add(BuildChangelogElement(_itemsChangelog, "item", primary, secondary, tertiary));

        return root;
    }

    // ── Preview dialog helper ────────────────────────────────────────

    /// <summary>
    /// Creates a preview confirmation dialog with:
    /// - Screen-based MaxHeight (prevents overflow off screen)
    /// - SizeToContent.Height (auto-grows on section expand)
    /// - Content should use DockPanel with changelog as fill child (see Build*Preview methods)
    /// </summary>
    private WpfMessageBox CreatePreviewDialog(string title, UIElement content, string primaryButton)
    {
        var owner = Window.GetWindow(this);
        var screenHeight = SystemParameters.WorkArea.Height;

        var dialog = new WpfMessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButton,
            CloseButtonText = "Cancel",
            Owner = owner,
            MinWidth = 540,
            SizeToContent = SizeToContent.Height,
            MaxHeight = screenHeight * 0.88,
        };

        dialog.Loaded += (_, _) =>
        {
            dialog.Top = Math.Max(owner.Top + 30, dialog.Top - owner.ActualHeight * 0.12);
        };

        ApplicationThemeManager.Apply(dialog);
        return dialog;
    }
}
