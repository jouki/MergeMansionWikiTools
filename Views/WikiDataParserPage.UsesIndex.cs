using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

// Uses area-requirement index orchestration. The index (Module:Datatable/Items/UsesIndex/1..N +
// /Areas) lets Module:Items.GetAllItemUses look up where a chain is consumed without scanning the
// full ~26 MB Module:Datatable/Areas (which peaked Lua memory near the 100 MB limit). The index is
// a function of BOTH Items and Areas, so it is regenerated whenever either is pushed.
public partial class WikiDataParserPage
{
    private readonly UsesIndexService _usesIndexService = new();
    private UsesIndexService.GeneratedUsesIndex? _lastUsesIndex; // result of the last Generate (stage 1)
    private bool _usesIndexCollapsed;
    private System.Threading.CancellationTokenSource? _usesIndexLoadCts; // chunked Lua-preview load

    /// <summary>
    /// Builds the index shards from the currently loaded Items + Areas + wiki mapping. Returns null
    /// (with an InfoBar reason) when either dataset is unavailable — the index needs both.
    /// </summary>
    private async Task<UsesIndexService.GeneratedUsesIndex?> BuildUsesIndexAsync()
    {
        var data = _main.DataService;
        if (data == null)
        {
            ShowInfo("Items data not loaded — generate items first.", InfoBarSeverity.Warning);
            return null;
        }
        var areas = await _main.GetAreasServiceAsync();
        if (areas == null || areas.Areas.Count == 0)
        {
            ShowInfo("Areas data not loaded — set the areas.json path first.", InfoBarSeverity.Warning);
            return null;
        }

        // Disambiguation mapping is required for bit-identical chain names (≈38 collision chains).
        // Prefer the in-memory cache; fetch on demand when it isn't loaded yet so the action works
        // standalone (it doesn't require a prior Items generation).
        var mapping = _main.WikiMapping;
        if (mapping == null || mapping.Mappings.Count == 0)
        {
            ShowInfo("Fetching items mapping (for disambiguated chain names)…", InfoBarSeverity.Informational);
            try { mapping = await WikiMappingService.FetchFromWikiAsync(); }
            catch (Exception ex) { AppLogger.Warn($"Items mapping fetch failed: {ex.Message}"); }
        }

        var itemsStamp = !string.IsNullOrEmpty(_itemsCreatedAt) ? _itemsCreatedAt : data.CreatedAt;
        var areasStamp = !string.IsNullOrEmpty(_areasCreatedAt) ? _areasCreatedAt : areas.CreatedAt;
        return _usesIndexService.Generate(
            data, areas, mapping, itemsStamp, areasStamp, Models.AppVersion.Version);
    }

    private static async Task PushUsesIndexAsync(
        HttpClient client, string csrf, UsesIndexService.GeneratedUsesIndex gen)
    {
        foreach (var shard in gen.Shards)
        {
            await WikiMappingService.EditModuleAsync(
                client, csrf, shard.Title, shard.Lua,
                $"Uses area index shard {shard.Number} ({shard.FirstLetter}-{shard.LastLetter}) via MergeMansionWikiTools");
        }
        await WikiMappingService.EditModuleAsync(
            client, csrf, UsesIndexService.AreaChainsTitle, gen.AreaChainsLua,
            "Uses area-chains presence index via MergeMansionWikiTools");

        // Keep the Modules hub's UsesIndex subpage list (parent + /1../N + /Areas, with current
        // letter ranges) in sync, placed after the numbered Items chunks.
        await WikiMappingService.UpdateModulesUsesIndexAsync(client, csrf,
            gen.Shards.Select(s => (s.Number, s.FirstLetter, s.LastLetter)).ToList());
    }

    /// <summary>
    /// Generates + pushes the Uses index. When <paramref name="client"/>/<paramref name="csrf"/> are
    /// provided (from an enclosing update flow) they are reused; otherwise a fresh authenticated
    /// client is created (standalone "Update Uses Index" action). Returns true on success.
    /// </summary>
    private async Task<bool> RegenerateUsesIndexAsync(HttpClient? client = null, string? csrf = null)
    {
        var gen = await BuildUsesIndexAsync();
        if (gen == null) return false;

        ShowInfo($"Uses index: {gen.ChainCount} chains, {gen.RowCount} rows, {gen.AreaCount} areas → " +
                 $"{gen.Shards.Count} shards. Pushing…", InfoBarSeverity.Informational);

        if (client == null)
        {
            using var owned = await WikiMappingService.CreateAuthenticatedClientAsync(
                _main.Settings.WikiUsername, _main.Settings.WikiPassword);
            var token = await WikiMappingService.GetCsrfTokenAsync(owned);
            await PushUsesIndexAsync(owned, token, gen);
        }
        else
        {
            await PushUsesIndexAsync(client, csrf!, gen);
        }

        ShowInfo($"Uses index updated ({gen.Shards.Count} shards + areaChains).", InfoBarSeverity.Success);
        return true;
    }

    /// <summary>
    /// Freshness check: the live index carries <c>-- sourceItems</c>/<c>-- sourceAreas</c> header
    /// stamps. Returns a stale flag + message when they don't match the live Items/Areas CreatedAt
    /// (the index = f(Items, Areas); a drift means it must be regenerated).
    /// </summary>
    private async Task<(bool stale, string headline, List<(string problem, string action)> details)> CheckUsesIndexFreshnessAsync()
    {
        var details = new List<(string problem, string action)>();
        var pushLabels = new List<string>();
        bool needRegen = false;
        try
        {
            var idx = await WikiMappingService.FetchModuleContentAsync($"{UsesIndexService.BaseTitle}/1");
            if (idx == null) return (false, "", details);
            var srcItems = ExtractHeaderStamp(idx, "sourceItems");
            var srcAreas = ExtractHeaderStamp(idx, "sourceAreas");

            var liveItems = WikiMappingService.ExtractCreatedAtFromContent(
                await WikiMappingService.FetchModuleContentAsync(ItemsModuleTitle) ?? "");
            var liveAreas = WikiMappingService.ExtractCreatedAtFromContent(
                await WikiMappingService.FetchModuleContentAsync("Module:Datatable/Areas/1") ?? "");

            // All three modules (Items, Areas, UsesIndex) must come from the SAME dump. Classify each
            // drift DIRECTIONALLY into the concrete action so we can lead with a ≤5-word command:
            //  - live module OLDER than the index → that module lags → push it.
            //  - index OLDER than a live module   → the index lags  → regenerate it.
            void Check(string label, string? indexStamp, string? liveStamp)
            {
                if (string.IsNullOrEmpty(indexStamp) || string.IsNullOrEmpty(liveStamp)) return;
                int cmp = CompareDates(indexStamp!, liveStamp!);
                if (cmp == 0) return;
                if (cmp < 0)
                {
                    details.Add(($"The Uses index is older than live {label}.", "Regenerate the Uses index."));
                    needRegen = true;
                }
                else
                {
                    details.Add(($"Live {label} is older than the Uses index.", $"{label}:  Generate {label} → Update Wiki."));
                    pushLabels.Add(label);
                }
            }
            Check("Items", srcItems, liveItems);
            Check("Areas", srcAreas, liveAreas);

            if (details.Count == 0) return (false, "", details);

            // Lead with the action (≤5 words): "Update Items + Areas" / "Regenerate Uses index" / mix.
            string headline =
                pushLabels.Count > 0 && !needRegen ? "Update " + string.Join(" + ", pushLabels) :
                needRegen && pushLabels.Count == 0 ? "Regenerate Uses index" :
                "Update " + string.Join(" + ", pushLabels) + " + regen index";

            return (true, headline, details);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"CheckUsesIndexFreshness failed: {ex.Message}");
            return (false, "", details);
        }
    }

    private static string? ExtractHeaderStamp(string lua, string key)
    {
        var m = Regex.Match(lua, @"--\s*" + Regex.Escape(key) + @":\s*(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Stage 1: "Generate Uses Index" — builds the index locally (no push) and reveals the
    /// section with the "Update Wiki" button, mirroring Generate Areas/Items/Events.</summary>
    private async void BtnGenerateUsesIndex_Click(object sender, RoutedEventArgs e)
    {
        btnGenerateUsesIndex.IsEnabled = false;
        try
        {
            var gen = await BuildUsesIndexAsync();
            if (gen == null) return;
            _lastUsesIndex = gen;

            txtUsesIndexStatus.Text = $"Uses Index: {gen.ChainCount} chains · {gen.RowCount} rows · {gen.AreaCount} areas";
            BuildUsesIndexCards(gen); // one Lua preview card per shard + /Areas (identical to Areas/Items)

            usesIndexSection.Visibility = Visibility.Visible;
            if (_usesIndexCollapsed)
            {
                usesIndexContent.Visibility = Visibility.Visible;
                iconCollapseUsesIndex.Symbol = SymbolRegular.ChevronUp24;
                _usesIndexCollapsed = false;
            }
            _ = RefreshUsesIndexStateAsync();
            ShowInfo($"Uses index generated — {gen.Shards.Count} shards. Review, then Update Wiki.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLogger.Error("GenerateUsesIndex failed", ex);
            ShowInfo($"Uses index generation failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnGenerateUsesIndex.IsEnabled = true;
        }
    }

    /// <summary>Stage 2: "Update Wiki" — pushes the generated index (mirrors the per-section push).</summary>
    private async void BtnUpdateUsesIndex_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.Settings.WikiVerified)
        {
            ShowInfo("Wiki bot not verified. Configure credentials in Settings first.", InfoBarSeverity.Warning);
            return;
        }
        if (_lastUsesIndex == null)
        {
            ShowInfo("Generate the Uses index first.", InfoBarSeverity.Warning);
            return;
        }
        btnUpdateUsesIndex.IsEnabled = false;
        try
        {
            var gen = _lastUsesIndex;
            ShowInfo($"Pushing Uses index ({gen.Shards.Count} shards + areaChains)…", InfoBarSeverity.Informational);
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                _main.Settings.WikiUsername, _main.Settings.WikiPassword);
            var csrf = await WikiMappingService.GetCsrfTokenAsync(client);
            await PushUsesIndexAsync(client, csrf, gen);
            ShowInfo($"Uses index updated ({gen.Shards.Count} shards + areaChains).", InfoBarSeverity.Success);
            await RefreshUsesIndexStateAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateUsesIndex failed", ex);
            ShowInfo($"Uses index update failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnUpdateUsesIndex.IsEnabled = true;
        }
    }

    private void BtnCollapseUsesIndex_Click(object sender, RoutedEventArgs e)
    {
        _usesIndexCollapsed = !_usesIndexCollapsed;
        usesIndexContent.Visibility = _usesIndexCollapsed ? Visibility.Collapsed : Visibility.Visible;
        iconCollapseUsesIndex.Symbol = _usesIndexCollapsed ? SymbolRegular.ChevronDown24 : SymbolRegular.ChevronUp24;
    }

    /// <summary>Sets the Uses-index date-state icon from the freshness check (stale → amber warning,
    /// button stays enabled because a stale index NEEDS regenerating).</summary>
    private async Task RefreshUsesIndexStateAsync()
    {
        var (stale, headline, details) = await CheckUsesIndexFreshnessAsync();
        if (stale)
        {
            iconUsesIndexState.Symbol = SymbolRegular.Warning24;
            iconUsesIndexState.Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C));
            iconUsesIndexState.ToolTip = BuildFreshnessTooltip(headline, details);
            iconUsesIndexState.Visibility = Visibility.Visible;
        }
        else
        {
            iconUsesIndexState.Visibility = Visibility.Collapsed;
        }
    }
}
