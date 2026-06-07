using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MergeMansionWikiTools.Helpers;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public partial class TableGeneratorDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly ParsedChain _chain;
    private ParsedChain _effectiveChain;   // filtered chain for generation
    private string _tableName;
    private string? _wikiNameWarning;
    private List<LuaArea>? _areas;
    private EventService? _eventService;
    private WikiMappingService.WikiSectionFetchResult? _gameTipsResult;
    private WikitextAutocomplete? _autocomplete;

    // Section-aware editing model. Each section is built independently and composed via Render().
    // User edits are detected by parsing the textbox back into sections (heading-based) and stored
    // as overrides — so toggling an unrelated checkbox preserves edits in other sections.
    private enum SectionKey
    {
        InfoboxSection,    // vardefine + {{Infobox Items}} + intro sentence (no heading on intro)
        GameplayTips,      // == Gameplay Tips ==
        ItemDescriptions,  // == Item Descriptions ==
        MergeStages,       // == Statistics == + === Merge Stages === + main table
        Tasks,             // === Tasks === (subsection of Statistics)
        DropOdds,          // === Drop Odds === (subsection of Statistics)
        DoubleBubbles,     // === [[Double Bubble]]s === (subsection of Statistics)
        Uses,              // == Uses ==
    }
    private static readonly SectionKey[] SectionOrder = {
        SectionKey.InfoboxSection, SectionKey.GameplayTips, SectionKey.ItemDescriptions,
        SectionKey.MergeStages, SectionKey.Tasks, SectionKey.DropOdds, SectionKey.DoubleBubbles,
        SectionKey.Uses,
    };
    private readonly Dictionary<SectionKey, string?> _generated = new();
    private readonly Dictionary<SectionKey, string?> _userOverride = new();
    private string _lastRendered = "";
    private bool _suppressTextChanged;
    private bool _suppressCheckedHandler;

    public TableGeneratorDialog(MainWindow main, ParsedChain chain)
    {
        _main = main;
        _chain = chain;
        _effectiveChain = chain;
        _tableName = chain.DisplayName;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        // Populate chain info
        txtChainInfo.Text = $"{chain.DisplayName}";
        txtChainDetail.Text = $"ConfigKey: {chain.ConfigKey} · {chain.Items.Count} levels · {chain.Summary}";

        // Auto-check hardcode name for event chains with parenthetical (e.g., "Puzzle Box (Event)")
        chkHardcodeName.IsChecked = chain.IsEventChain && chain.DisplayName.Contains('(');
        chkLowPrices.IsChecked = chain.IsEventChain ? true : main.Settings.LowPrices;

        // Show "Include Tasks section" only when chain has at least one OrderFeatures item.
        bool hasOrderTasks = chain.Items.Any(i => i.OrderTasks is { Count: > 0 });
        gridTasks.Visibility = hasOrderTasks ? Visibility.Visible : Visibility.Collapsed;
        chkIncludeTasks.IsChecked = hasOrderTasks; // default ON when applicable

        // Load areas (for Uses section gating + Infobox intro temporary-area detection)
        LoadAreas();

        // Load events (for Infobox intro Event variant)
        LoadEvents();

        // Always-visible: Infobox Section, Gameplay Tips, Statistics
        chkIncludeInfoboxSection.IsChecked = main.Settings.TableGeneratorIncludeInfoboxSection;
        chkIncludeGameTips.IsChecked = main.Settings.TableGeneratorIncludeGameTips;
        chkIncludeStatistics.IsChecked = main.Settings.TableGeneratorIncludeStatistics;

        // Infobox Options defaults (mirror InfoboxGeneratorDialog ctor behavior):
        // KeepFullName auto-on for parenthetical non-event chains; others off by default.
        chkInfoboxKeepFullName.IsChecked = chain.DisplayName.Contains('(') && !chain.IsEventChain;
        // Visibility tied to Infobox Section checkbox
        UpdateInfoboxOptionsVisibility();

        // Per-section gating — collapse the WHOLE outer Grid (not just inner checkbox) so the
        // 4px top margin doesn't accumulate empty rows in the layout.
        var probeGenerator = new WikiTableGenerator(main.DataService!, main.WikiMapping);
        bool hasItemDescs = probeGenerator.GenerateItemDescriptionsSection(chain) != null;
        bool hasDropOdds = probeGenerator.GenerateDropOddsSection(chain) != null;
        bool hasDoubleBubbles = probeGenerator.GenerateDoubleBubblesSection(chain) != null;
        bool hasUses = probeGenerator.GenerateUsesSection(chain, main.DataService!.Chains, _areas) != null;
        gridItemDescriptions.Visibility = hasItemDescs ? Visibility.Visible : Visibility.Collapsed;
        chkIncludeItemDescriptions.IsChecked = hasItemDescs && main.Settings.TableGeneratorIncludeItemDescriptions;
        gridDropOdds.Visibility = hasDropOdds ? Visibility.Visible : Visibility.Collapsed;
        chkIncludeDropOdds.IsChecked = hasDropOdds && main.Settings.TableGeneratorIncludeDropOdds;
        gridDoubleBubbles.Visibility = hasDoubleBubbles ? Visibility.Visible : Visibility.Collapsed;
        chkIncludeDoubleBubbles.IsChecked = hasDoubleBubbles && main.Settings.TableGeneratorIncludeDoubleBubbles;
        gridUses.Visibility = hasUses ? Visibility.Visible : Visibility.Collapsed;
        chkIncludeUses.IsChecked = hasUses && main.Settings.TableGeneratorIncludeUses;

        // Kick off async Gameplay Tips fetch (page name = resolved wiki chain name)
        var wikiName = probeGenerator.GetWikiChainName(chain);
        _ = FetchGameTipsAsync(wikiName);

        // Autocomplete for the editable output box. Current chain name set immediately so the
        // {{Item/Group|… hardcoded shortcut works from the first keystroke. The larger chain-name
        // list is sorted off-thread (a few thousand entries) — autocomplete is functional for
        // templates before it finishes, then transparently gains item suggestions.
        _autocomplete = new WikitextAutocomplete(txtOutput);
        _autocomplete.SetCurrentChainName(wikiName);
        // Parenthetical chain (e.g. "Tools (Beach Shack)") → DisplayTitle vardefine is emitted
        // into the infobox section; autocomplete uses this flag to auto-inject
        // |displayName={{#var:DisplayTitle}} when closing Item-shape templates with }}.
        bool hasParen = chain.DisplayName.TrimEnd().EndsWith(")") && chain.DisplayName.Contains('(');
        _autocomplete.SetCurrentChainHasDisplayTitle(hasParen);
        // Autocomplete data: use MainWindow's pre-built cache (built off-thread after data load).
        // Saves ~1-2s per dialog open. Falls back to building inline if cache not ready yet
        // (e.g. user opens dialog right after data load, before prewarm completes).
        _ = LoadAutocompleteDataAsync(chain);

        // Live-refresh thumbnails when image extraction finishes while this dialog is open.
        // (New game version → no images yet → user extracts → autocomplete updates without reopen.)
        _main.AutocompleteDataRefreshed += OnAutocompleteDataRefreshed;
        Closed += (_, _) => _main.AutocompleteDataRefreshed -= OnAutocompleteDataRefreshed;

        FinalizeConstruction(chain, main);
    }

    private void OnAutocompleteDataRefreshed()
    {
        // Re-pull the (now rebuilt) dataset into this dialog's autocomplete.
        _ = LoadAutocompleteDataAsync(_chain);
    }

    private async Task LoadAutocompleteDataAsync(ParsedChain chain)
    {
        AutocompleteData? data = _main.CachedAutocomplete;
        if (data == null && _main.AutocompletePrewarmTask != null)
        {
            // Prewarm running — await its result instead of starting a duplicate build
            try { data = await _main.AutocompletePrewarmTask; }
            catch (Exception ex) { AppLogger.Info($"[AUTOCOMPLETE] await prewarm failed: {ex.Message}"); }
        }
        if (data == null)
        {
            // No prewarm — build inline (rare edge case: dialog opened before any data load
            // finished its post-load hook, e.g. external code path)
            var ds = _main.DataService;
            if (ds == null) return;
            var imageDir = AutocompleteDataService.ResolveImageDir(
                _main.Settings.ImageExporterBasePath, _main.Settings.SelectedApkVersion);
            data = await Task.Run(() => AutocompleteDataService.Build(
                ds, _main.WikiMapping, _areas, imageDir,
                _main.Settings.ImageExporterBasePath, _main.Settings.SelectedApkVersion));
        }
        // Resolve dialog-specific temp area (per-chain, can't be precomputed)
        string? tempArea = null;
        if (_areas != null && chain.Items.Any(i => i.IsTemporary))
        {
            var chainItemTypes = chain.Items.Select(i => i.ItemType).Where(t => !string.IsNullOrEmpty(t)).ToList();
            var matching = AreasService.FindAreasRequiringChain(chainItemTypes!, _areas);
            if (matching.Count > 0) tempArea = matching[0].DisplayName;
        }
        Dispatcher.Invoke(() =>
        {
            if (_autocomplete == null) return;
            _autocomplete.SetChainNames(data.ChainNames);
            _autocomplete.SetChainMaxLevels(data.ChainMaxLevels);
            _autocomplete.SetChainImagePaths(data.ChainImagePaths);
            _autocomplete.SetChainSpriteRects(data.ChainSpriteRects);
            _autocomplete.SetChainSpriteRotated(data.ChainSpriteRotated);
            _autocomplete.SetAreaNames(data.AreaNames);
            _autocomplete.SetCurrentTemporaryAreaName(tempArea);
            _autocomplete.SetAreaImagePaths(data.AreaImagePaths);
            _autocomplete.SetAreaFractionalRects(data.AreaFractionalRects);
            _autocomplete.RefreshIfOpen();
        });
    }

    /// <summary>Continues ctor work AFTER LoadAutocompleteDataAsync kicks off — hooks editor
    /// events, source-group selector, warnings, and initial generate. Split out only for
    /// readability; called inline at end of ctor.</summary>
    private void FinalizeConstruction(ParsedChain chain, MainWindow main)
    {
        // Edit detection
        txtOutput.TextChanged += OnOutputTextChanged;
        // Editor hotkeys
        WikitextEditorHotkeys.Attach(txtOutput);
        // Source group selector for merged chains with level collisions
        if (chain.HasLevelCollisions && chain.MergedFromConfigKeys is { Count: > 1 })
        {
            sourceGroupPanel.Visibility = Visibility.Visible;
            cmbSourceGroup.Items.Add("All (merged)");
            foreach (var key in chain.MergedFromConfigKeys)
                cmbSourceGroup.Items.Add(key);
            cmbSourceGroup.SelectedIndex = 0;
        }
        _wikiNameWarning = CheckWikiNameMissing(chain, main);
        Loaded += (_, _) => GenerateTable();
    }

    private void LoadAreas()
    {
        var path = _main.Settings.AreasJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var svc = new AreasService();
            Task.Run(() => svc.LoadAsync(path)).GetAwaiter().GetResult();
            _areas = svc.Areas;
        }
        catch { /* areas loading failure is non-critical; Uses gating just won't see task references */ }
    }

    /// <summary>Multi-strategy AreaId → Area_InfoPopupBg_{key}.png resolver. Mirrors
    /// AreaFlowchartsPage.ResolveImageKey (same priority chain) — see that file for rationale
    /// per strategy. Centralizing into AreasService is a follow-up.</summary>
    private static string? ResolveAreaImageKey(string areaId,
        IReadOnlyDictionary<string, string> bgFiles,
        IReadOnlyDictionary<string, string>? bundleMapping)
    {
        if (string.IsNullOrEmpty(areaId)) return null;
        // 0. Bundle mapping (authoritative)
        if (bundleMapping != null && bundleMapping.TryGetValue(areaId, out var bundleKey)
            && bgFiles.ContainsKey(bundleKey)) return bundleKey;
        // 1. Direct
        if (bgFiles.ContainsKey(areaId)) return areaId;
        // 2. Strip trailing digits ("GardenRight20" → "GardenRight")
        var stripped = areaId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (stripped.Length > 0 && stripped != areaId && bgFiles.ContainsKey(stripped)) return stripped;
        // 3. Image is longest suffix of AreaId ("MansionPlaza" → "Plaza")
        string? best = null;
        foreach (var img in bgFiles.Keys)
            if (img.Length >= 3 && areaId.EndsWith(img, StringComparison.OrdinalIgnoreCase)
                && (best == null || img.Length > best.Length)) best = img;
        if (best != null) return best;
        // 4. AreaId is suffix of image ("Hideout" → "SecretHideout")
        best = null;
        foreach (var img in bgFiles.Keys)
            if (areaId.Length >= 3 && img.EndsWith(areaId, StringComparison.OrdinalIgnoreCase)
                && (best == null || img.Length < best.Length)) best = img;
        if (best != null) return best;
        // 5. Image is longest prefix of AreaId ("SaunaBurn" → "Sauna")
        best = null;
        foreach (var img in bgFiles.Keys)
            if (img.Length >= 3 && areaId.StartsWith(img, StringComparison.OrdinalIgnoreCase)
                && (best == null || img.Length > best.Length)) best = img;
        if (best != null) return best;
        // 6. Possessive 's' ("DebRoom" ↔ "DebsRoom")
        foreach (var img in bgFiles.Keys)
        {
            if (img.Length != areaId.Length + 1) continue;
            for (int i = 1; i < img.Length; i++)
            {
                if (char.ToLowerInvariant(img[i]) != 's') continue;
                if (string.Concat(img.AsSpan(0, i), img.AsSpan(i + 1))
                        .Equals(areaId, StringComparison.OrdinalIgnoreCase)) return img;
            }
        }
        return null;
    }

    /// <summary>Extracts the numeric level suffix from a sprite name (e.g. "Cinema_Concession_12" → 12).
    /// Returns 0 when not parseable. Used to pick the max-level sprite from an atlas group.</summary>
    private static int ExtractSpriteLevel(string spriteName)
    {
        int idx = spriteName.LastIndexOf('_');
        if (idx < 0 || idx + 1 >= spriteName.Length) return 0;
        return int.TryParse(spriteName.AsSpan(idx + 1), out var n) ? n : 0;
    }

    private void LoadEvents()
    {
        var path = _main.Settings.EventsJsonPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var svc = new EventService();
            Task.Run(() => svc.LoadAsync(path)).GetAwaiter().GetResult();
            if (_main.DataService != null)
                svc.ResolveChains(_main.DataService);
            _eventService = svc;
        }
        catch { /* event loading failure is non-critical; Infobox intro Event variant just won't fire */ }
    }

    private async Task FetchGameTipsAsync(string wikiPageTitle)
    {
        var result = await WikiMappingService.FetchPageSectionAsync(wikiPageTitle, "Gameplay Tips");
        _gameTipsResult = result;
        UpdateGameTipsStatusIcon();
        if (IsLoaded) GenerateTable();
    }

    private void UpdateGameTipsStatusIcon()
    {
        if (_gameTipsResult == null || _gameTipsResult.Status == WikiMappingService.WikiSectionFetchStatus.Ok)
        {
            iconGameTipsStatus.Visibility = Visibility.Collapsed;
            return;
        }
        iconGameTipsStatus.Visibility = Visibility.Visible;
        switch (_gameTipsResult.Status)
        {
            case WikiMappingService.WikiSectionFetchStatus.PageMissing:
                iconGameTipsStatus.Symbol = Wpf.Ui.Controls.SymbolRegular.Info24;
                iconGameTipsStatus.Foreground = System.Windows.Media.Brushes.LightBlue;
                ToolTipService.SetToolTip(iconGameTipsStatus, "Wiki page does not exist yet — Gameplay Tips section will be empty in the output.");
                break;
            case WikiMappingService.WikiSectionFetchStatus.SectionMissing:
                iconGameTipsStatus.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                iconGameTipsStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
                ToolTipService.SetToolTip(iconGameTipsStatus, "Wiki page exists but has no \"Gameplay Tips\" section — output section will be empty.");
                break;
            case WikiMappingService.WikiSectionFetchStatus.NetworkError:
                iconGameTipsStatus.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                iconGameTipsStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                ToolTipService.SetToolTip(iconGameTipsStatus, "Failed to fetch Gameplay Tips from the wiki — check network connection. Output section will be empty.");
                break;
        }
    }

    /// <summary>
    /// Returns a warning string if chain has no human-readable name and isn't mapped on the wiki.
    /// </summary>
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
    {
        if (!IsLoaded) return;

        _main.Settings.LowPrices = chkLowPrices.IsChecked == true;
        _main.SaveSettings();

        GenerateTable();
    }

    /// <summary>
    /// Hardcode-name toggle: regenerate as usual, AND swap PAGENAME ↔ chain name in user-edited
    /// sections so the user's overrides stay consistent with the new mode. Only literal
    /// occurrences are swapped (no regex magic) — narrative text containing the chain name will
    /// also be swapped, which is the documented behavior per user spec.
    /// </summary>
    private void ChkHardcodeName_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        bool nowHardcode = chkHardcodeName.IsChecked == true;
        var generator = new WikiTableGenerator(_main.DataService!, _main.WikiMapping);
        var chainName = generator.GetWikiChainName(_effectiveChain);

        if (!string.IsNullOrEmpty(chainName))
        {
            foreach (var key in _userOverride.Keys.ToList())
            {
                var val = _userOverride[key];
                if (string.IsNullOrEmpty(val)) continue;
                _userOverride[key] = nowHardcode
                    ? val.Replace("{{PAGENAME}}", chainName)
                    : val.Replace(chainName, "{{PAGENAME}}");
            }
        }

        GenerateTable();
    }

    private void ChkIncludeInfoboxSection_Changed(object sender, RoutedEventArgs e)
    {
        UpdateInfoboxOptionsVisibility();
        HandleSectionCheckboxToggle(chkIncludeInfoboxSection, SectionKey.InfoboxSection, "Infobox Section",
            v => _main.Settings.TableGeneratorIncludeInfoboxSection = v);
    }

    private void ChkIncludeGameTips_Changed(object sender, RoutedEventArgs e) =>
        HandleSectionCheckboxToggle(chkIncludeGameTips, SectionKey.GameplayTips, "Gameplay Tips",
            v => _main.Settings.TableGeneratorIncludeGameTips = v);

    private void ChkIncludeItemDescriptions_Changed(object sender, RoutedEventArgs e) =>
        HandleSectionCheckboxToggle(chkIncludeItemDescriptions, SectionKey.ItemDescriptions, "Item Descriptions",
            v => _main.Settings.TableGeneratorIncludeItemDescriptions = v);

    private void ChkIncludeStatistics_Changed(object sender, RoutedEventArgs e) =>
        HandleSectionCheckboxToggle(chkIncludeStatistics, SectionKey.MergeStages, "Statistics",
            v => _main.Settings.TableGeneratorIncludeStatistics = v);

    /// <summary>
    /// Shared handler for section checkbox toggles. Only prompts when the user is DISABLING
    /// (unchecking) a section that has unsaved edits — re-enabling is silent (the saved override
    /// just gets re-displayed). Per user spec: Keep on uncheck preserves edits; subsequent
    /// uncheck prompts again (edits might have changed in the meantime).
    /// </summary>
    private async void HandleSectionCheckboxToggle(CheckBox checkbox, SectionKey key, string sectionName, Action<bool> persistSetting)
    {
        if (!IsLoaded) return;
        if (_suppressCheckedHandler) return;

        bool nowChecked = checkbox.IsChecked == true;

        // Only prompt when DISABLING a section with edits. Enabling is always silent — saved edits
        // just re-appear, no decision needed.
        if (!nowChecked && _userOverride.ContainsKey(key))
        {
            if (!await PromptKeepOrResetAsync(key, sectionName))
            {
                // User cancelled — revert checkbox without re-firing the handler
                _suppressCheckedHandler = true;
                try { checkbox.IsChecked = true; }
                finally { _suppressCheckedHandler = false; }
                return;
            }
        }

        persistSetting(nowChecked);
        _main.SaveSettings();
        GenerateTable();
    }

    private void ChkIncludeDropOdds_Changed(object sender, RoutedEventArgs e) =>
        HandleSectionCheckboxToggle(chkIncludeDropOdds, SectionKey.DropOdds, "Drop Odds",
            v => _main.Settings.TableGeneratorIncludeDropOdds = v);

    private void ChkIncludeDoubleBubbles_Changed(object sender, RoutedEventArgs e) =>
        HandleSectionCheckboxToggle(chkIncludeDoubleBubbles, SectionKey.DoubleBubbles, "Double Bubbles",
            v => _main.Settings.TableGeneratorIncludeDoubleBubbles = v);

    private void ChkIncludeUses_Changed(object sender, RoutedEventArgs e) =>
        HandleSectionCheckboxToggle(chkIncludeUses, SectionKey.Uses, "Uses",
            v => _main.Settings.TableGeneratorIncludeUses = v);

    private void CmbSourceGroup_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (cmbSourceGroup.SelectedIndex <= 0)
        {
            // "All (merged)"
            _effectiveChain = _chain;
        }
        else
        {
            var selectedKey = (string)cmbSourceGroup.SelectedItem;
            var filtered = new ParsedChain
            {
                ConfigKey = _chain.ConfigKey,
                DisplayName = _chain.DisplayName,
                OriginalName = _chain.OriginalName,
                HasNaturalName = _chain.HasNaturalName,
                CustomName = _chain.CustomName,
                IsNameFromWiki = _chain.IsNameFromWiki,
                Items = _chain.Items.Where(i => i.SourceChainKey == selectedKey).ToList()
            };
            _effectiveChain = filtered;
        }
        GenerateTable();
    }

    private void GenerateTable(bool showNotification = false)
    {
        try
        {
            var generator = new WikiTableGenerator(_main.DataService!, _main.WikiMapping);

            bool lowPrices = chkLowPrices.IsChecked == true;
            bool hardcodeName = chkHardcodeName.IsChecked == true;
            string? hardcodedName = hardcodeName ? generator.GetWikiChainName(_effectiveChain) : null;

            // Build per-section generated content (regardless of checkbox state — checkbox only gates
            // inclusion in render). Sections that don't apply to this chain return null.
            _generated[SectionKey.InfoboxSection] = BuildInfoboxSection(generator, hardcodeName, hardcodedName);
            _generated[SectionKey.GameplayTips] = BuildGameplayTipsSection(generator, hardcodedName);
            _generated[SectionKey.ItemDescriptions] = generator.GenerateItemDescriptionsSection(_effectiveChain, hardcodedName);
            _generated[SectionKey.MergeStages] = BuildMergeStagesSection(generator, lowPrices, hardcodeName);
            _generated[SectionKey.Tasks] = BuildTasksSection(generator);
            _generated[SectionKey.DropOdds] = generator.GenerateDropOddsSection(_effectiveChain, hardcodedName);
            _generated[SectionKey.DoubleBubbles] = generator.GenerateDoubleBubblesSection(_effectiveChain, hardcodedName);
            _generated[SectionKey.Uses] = generator.GenerateUsesSection(_effectiveChain, _main.DataService!.Chains, _areas, hardcodedName);

            RenderOutput();
            UpdateRefreshIcons();

            // Collect all warnings (generator + wiki name)
            var allWarnings = new List<string>(generator.Warnings);
            if (_wikiNameWarning != null)
                allWarnings.Add(_wikiNameWarning);

            if (allWarnings.Count > 0)
            {
                warningBar.Message = string.Join("\n", allWarnings);
                warningBar.Severity = InfoBarSeverity.Warning;
                warningBar.IsOpen = true;
            }
            else if (showNotification)
            {
                _ = ShowRefreshNotification();
            }
            else
            {
                warningBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            warningBar.Message = $"Generation error: {ex.Message}";
            warningBar.Severity = InfoBarSeverity.Error;
            warningBar.IsOpen = true;
        }
    }

    // ── Per-section builders ───────────────────────────────────────────────────────

    /// <summary>Infobox Section = {{Infobox Items}} (via reused InfoboxGeneratorService) + intro sentence.</summary>
    private string? BuildInfoboxSection(WikiTableGenerator generator, bool hardcodeName, string? hardcodedName)
    {
        var ds = _main.DataService!;
        var infoboxService = new InfoboxGeneratorService(ds, _main.WikiMapping);
        var autoSources = infoboxService.BuildAutoSources(_effectiveChain, ds.Chains, ds.ItemNames, _areas);
        var infoboxOpts = new InfoboxGeneratorOptions
        {
            // Hardcode Name (single checkbox) controls BOTH the table caption and the gallery
            // image — they should always match (`{{ItemNameToFilename|<name>|...}}` filename
            // matches the chain's wiki page name).
            HardcodeGalleryImage = hardcodeName,
            // KeepFullName: explicit checkbox in Infobox Options. Default = true for non-event
            // parenthetical chains (set in dialog ctor), user can toggle.
            KeepFullName = chkInfoboxKeepFullName.IsChecked == true,
            IsStarter = chkInfoboxStarter.IsChecked == true,
            IsPoints = chkInfoboxPoints.IsChecked == true,
            IsDepleting = chkInfoboxDepleting.IsChecked == true,
            EventName = _effectiveChain.IsEventChain
                ? _eventService?.FindEventForChain(_effectiveChain)?.DisplayName
                : null,
        };
        var infobox = infoboxService.Generate(_effectiveChain, ds.Chains, ds.ItemNames, infoboxOpts, autoSources);
        var intro = generator.GenerateInfoboxSectionIntro(_effectiveChain, hardcodedName, _areas, _eventService);
        return string.IsNullOrEmpty(intro) ? infobox : $"{infobox}\n{intro}";
    }

    /// <summary>Handler for any Infobox Options checkbox change. Invalidates any user override
    /// on the Infobox section before regenerating — otherwise the override wins over the freshly
    /// generated content and the new Starter/Points/Depleting type wouldn't appear. User's manual
    /// edits to the infobox section are discarded; this matches user expectation that toggling
    /// these checkboxes IS the way to manage type fields.</summary>
    private void ChkInfoboxOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _userOverride.Remove(SectionKey.InfoboxSection);
        GenerateTable();
    }

    /// <summary>Sync the Infobox Options panel + title visibility with the Infobox Section checkbox.</summary>
    private void UpdateInfoboxOptionsVisibility()
    {
        var vis = chkIncludeInfoboxSection.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        pnlInfoboxOptions.Visibility = vis;
        txtInfoboxOptionsTitle.Visibility = vis;
    }

    /// <summary>Gameplay Tips = fetched wiki content (post-processed) under `== Gameplay Tips ==` heading.
    /// Returns null until fetch resolves; on fetch failure (page/section missing or network error)
    /// the section body is `TBA` so the editor leaves an explicit placeholder rather than an empty
    /// section. Per-spec: chain-name-in-templates is swapped to {{PAGENAME}}, then if Hardcode is on
    /// the caller-supplied hardcoded name swaps {{PAGENAME}} back to the literal — making the toggle
    /// transparent.</summary>
    private string? BuildGameplayTipsSection(WikiTableGenerator generator, string? hardcodedName)
    {
        if (_gameTipsResult == null) return null;
        if (_gameTipsResult.Status == WikiMappingService.WikiSectionFetchStatus.Ok)
        {
            var currentChain = generator.GetWikiChainName(_effectiveChain);
            var processed = generator.PostProcessGameplayTips(_gameTipsResult.Content ?? "", currentChain);
            if (hardcodedName != null) processed = processed.Replace("{{PAGENAME}}", hardcodedName);
            return $"== Gameplay Tips ==\n{processed}";
        }
        // Fetch failed (page missing / section missing / network) → explicit TBA placeholder.
        return "== Gameplay Tips ==\nTBA";
    }

    /// <summary>Merge Stages = `== Statistics ==` heading + `=== Merge Stages ===` + main table.</summary>
    private string BuildMergeStagesSection(WikiTableGenerator generator, bool lowPrices, bool hardcodeName)
    {
        var table = generator.Generate(_effectiveChain, _tableName, lowPrices, hardcodeName);
        return $"== Statistics ==\n=== Merge Stages ===\n{table}";
    }

    /// <summary>Tasks subsection = concatenated tasks tables across all OrderFeatures items in chain.</summary>
    private string? BuildTasksSection(WikiTableGenerator generator)
    {
        var tasksBlocks = _effectiveChain.Items
            .Where(i => i.IsOrder && i.OrderTasks is { Count: > 0 })
            .Select(i => generator.GenerateOrderTasksTable(i))
            .Where(b => !string.IsNullOrEmpty(b))
            .ToList();
        return tasksBlocks.Count > 0 ? string.Join("\n", tasksBlocks!) : null;
    }

    // ── Render orchestrator ────────────────────────────────────────────────────────

    /// <summary>
    /// Composes the output by iterating sections in order. For each section: picks user override
    /// when present, else generated. Skips disabled checkboxes and sections without content.
    /// Suppresses TextChanged so edit detection doesn't fire on our own write.
    /// </summary>
    private void RenderOutput()
    {
        var sb = new StringBuilder();
        SectionKey? prev = null;
        foreach (var key in SectionOrder)
        {
            if (!IsSectionEnabled(key)) continue;
            string? text = _userOverride.GetValueOrDefault(key) ?? _generated.GetValueOrDefault(key);
            if (string.IsNullOrEmpty(text)) continue;

            text = text.TrimEnd('\r', '\n');
            if (prev.HasValue) sb.Append(GetSeparator(prev.Value, key));
            sb.Append(text);
            prev = key;
        }

        _suppressTextChanged = true;
        try
        {
            txtOutput.Text = sb.ToString();
            _lastRendered = txtOutput.Text;
        }
        finally { _suppressTextChanged = false; }

        txtOutput.Visibility = Visibility.Visible;
        txtOutputPlaceholder.Visibility = Visibility.Collapsed;
        btnCopy.IsEnabled = true;
    }

    private bool IsSectionEnabled(SectionKey key) => key switch
    {
        SectionKey.InfoboxSection => chkIncludeInfoboxSection.IsChecked == true,
        SectionKey.GameplayTips => chkIncludeGameTips.IsChecked == true,
        SectionKey.ItemDescriptions => chkIncludeItemDescriptions.IsChecked == true,
        SectionKey.MergeStages => chkIncludeStatistics.IsChecked == true,
        SectionKey.Tasks => chkIncludeTasks.IsChecked == true,
        SectionKey.DropOdds => chkIncludeDropOdds.IsChecked == true,
        SectionKey.DoubleBubbles => chkIncludeDoubleBubbles.IsChecked == true,
        SectionKey.Uses => chkIncludeUses.IsChecked == true,
        _ => false,
    };

    /// <summary>Separator between sections — always a blank line (= \n\n) for consistency.
    /// MediaWiki needs double newlines to actually render a paragraph break, so any single-\n
    /// separator collapses on the wiki to no gap, which the user reported as missing.</summary>
    private static string GetSeparator(SectionKey prev, SectionKey current) => "\n\n";

    // ── User edit detection ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses the user-edited text into sections by scanning for known headings. Each section's
    /// content is compared to <see cref="_generated"/>. Differences are stored as user overrides.
    /// Matches clear the override (= user reverted manually).
    /// </summary>
    private void OnOutputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        if (txtOutput.Text == _lastRendered) return;

        var parsed = ParseSections(txtOutput.Text);
        foreach (var key in SectionOrder)
        {
            // Only track sections that are currently visible — disabled sections shouldn't acquire
            // overrides just because the user typed something that looks like their heading.
            if (!IsSectionEnabled(key)) continue;
            if (!parsed.TryGetValue(key, out var userContent)) continue;

            string? gen = _generated.GetValueOrDefault(key);
            if (NormalizeForCompare(userContent) == NormalizeForCompare(gen))
            {
                // Reverted to generated — clear override
                _userOverride.Remove(key);
            }
            else
            {
                _userOverride[key] = userContent;
            }
        }
        UpdateRefreshIcons();
    }

    /// <summary>Normalizes line endings to \n and trims leading/trailing whitespace+newlines.
    /// Critical: generator uses sb.AppendLine which emits Environment.NewLine (= \r\n on Windows),
    /// but parser pre-normalizes to \n. Without this, INTERIOR line ending mismatches falsely mark
    /// every enabled section as edited even when the user only touched one.</summary>
    private static string NormalizeForCompare(string? s) =>
        (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim('\n', ' ', '\t');

    /// <summary>
    /// Scans text line-by-line, splitting into sections at known headings.
    /// First chunk (before any heading) is treated as InfoboxSection content.
    /// Unknown headings are folded into the current section (= user-added content stays put).
    /// </summary>
    private static Dictionary<SectionKey, string> ParseSections(string text)
    {
        var result = new Dictionary<SectionKey, string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        SectionKey current = SectionKey.InfoboxSection;
        var buffer = new List<string>();

        void Flush()
        {
            // Strip leading/trailing blank lines from each section's stored content so re-render
            // separators don't accumulate.
            while (buffer.Count > 0 && string.IsNullOrWhiteSpace(buffer[0])) buffer.RemoveAt(0);
            while (buffer.Count > 0 && string.IsNullOrWhiteSpace(buffer[^1])) buffer.RemoveAt(buffer.Count - 1);
            if (buffer.Count > 0) result[current] = string.Join("\n", buffer);
            buffer.Clear();
        }

        foreach (var line in lines)
        {
            var detected = DetectSectionHeading(line);
            if (detected.HasValue && detected.Value != current)
            {
                Flush();
                current = detected.Value;
            }
            buffer.Add(line);
        }
        Flush();
        return result;
    }

    private static SectionKey? DetectSectionHeading(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return null;
        if (t == "== Gameplay Tips ==") return SectionKey.GameplayTips;
        if (t == "== Item Descriptions ==") return SectionKey.ItemDescriptions;
        if (t == "== Statistics ==") return SectionKey.MergeStages;
        if (t == "=== Tasks ===") return SectionKey.Tasks;
        if (t == "=== Drop Odds ===") return SectionKey.DropOdds;
        if (t == "=== [[Double Bubble]]s ===") return SectionKey.DoubleBubbles;
        if (t == "== Uses ==") return SectionKey.Uses;
        return null;
    }

    // ── Refresh icons (visible when section has user override) ─────────────────────

    /// <summary>
    /// Updates per-section refresh button visibility based on edit state.
    /// Gameplay Tips is special: when the wiki page exists, the refresh icon is always visible
    /// (re-fetch from wiki); otherwise it follows the standard edit-state rule.
    /// </summary>
    private void UpdateRefreshIcons()
    {
        SetRefreshVisible(btnRefreshInfoboxSection, _userOverride.ContainsKey(SectionKey.InfoboxSection));
        bool gameTipsPageExists = _gameTipsResult != null
            && _gameTipsResult.Status != WikiMappingService.WikiSectionFetchStatus.PageMissing;
        SetRefreshVisible(btnRefreshGameTips, gameTipsPageExists || _userOverride.ContainsKey(SectionKey.GameplayTips));
        SetRefreshVisible(btnRefreshItemDescriptions, _userOverride.ContainsKey(SectionKey.ItemDescriptions));
        SetRefreshVisible(btnRefreshStatistics, _userOverride.ContainsKey(SectionKey.MergeStages));
        SetRefreshVisible(btnRefreshTasks, _userOverride.ContainsKey(SectionKey.Tasks));
        SetRefreshVisible(btnRefreshDropOdds, _userOverride.ContainsKey(SectionKey.DropOdds));
        SetRefreshVisible(btnRefreshDoubleBubbles, _userOverride.ContainsKey(SectionKey.DoubleBubbles));
        SetRefreshVisible(btnRefreshUses, _userOverride.ContainsKey(SectionKey.Uses));
    }

    private static void SetRefreshVisible(Wpf.Ui.Controls.Button? btn, bool visible)
    {
        if (btn != null) btn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Section checkbox prompt + refresh button click handlers ────────────────────

    /// <summary>
    /// If the section has user edits, prompts "Keep edits / Reset / Cancel". Returns false when
    /// the user cancels (caller should revert the checkbox state). Reset clears the override.
    /// </summary>
    /// <summary>
    /// Shows the Keep/Reset/Cancel dialog when DISABLING a section with edits. Keep preserves
    /// the override for next re-enable; Reset discards it. Returns false ONLY on Cancel.
    /// </summary>
    private async Task<bool> PromptKeepOrResetAsync(SectionKey key, string sectionName)
    {
        var msgBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Section has unsaved edits",
            Content = $"You've edited the \"{sectionName}\" section.\n\nWhat would you like to do?",
            PrimaryButtonText = "Keep edits",
            SecondaryButtonText = "Reset to default",
            CloseButtonText = "Cancel",
            MinWidth = 480,
        };
        var result = await msgBox.ShowDialogAsync();
        switch (result)
        {
            case Wpf.Ui.Controls.MessageBoxResult.Primary:    // Keep edits — override preserved
                return true;
            case Wpf.Ui.Controls.MessageBoxResult.Secondary:  // Reset to default — discard override
                _userOverride.Remove(key);
                return true;
            default:                                           // Cancel
                return false;
        }
    }

    private void ResetSection(SectionKey key)
    {
        _userOverride.Remove(key);
        RenderOutput();
        UpdateRefreshIcons();
    }

    private void BtnRefreshInfoboxSection_Click(object sender, RoutedEventArgs e) => ResetSection(SectionKey.InfoboxSection);
    private void BtnRefreshItemDescriptions_Click(object sender, RoutedEventArgs e) => ResetSection(SectionKey.ItemDescriptions);
    private void BtnRefreshStatistics_Click(object sender, RoutedEventArgs e) => ResetSection(SectionKey.MergeStages);
    private void BtnRefreshTasks_Click(object sender, RoutedEventArgs e) => ResetSection(SectionKey.Tasks);
    private void BtnRefreshDropOdds_Click(object sender, RoutedEventArgs e) => ResetSection(SectionKey.DropOdds);
    private void BtnRefreshDoubleBubbles_Click(object sender, RoutedEventArgs e) => ResetSection(SectionKey.DoubleBubbles);
    private void BtnRefreshUses_Click(object sender, RoutedEventArgs e) => ResetSection(SectionKey.Uses);

    private async void BtnRefreshGameTips_Click(object sender, RoutedEventArgs e)
    {
        // Re-fetch from wiki + clear user override + rerender. For Gameplay Tips the refresh
        // semantically means "pull latest from the wiki" regardless of edit state.
        _userOverride.Remove(SectionKey.GameplayTips);
        var probe = new WikiTableGenerator(_main.DataService!, _main.WikiMapping);
        await FetchGameTipsAsync(probe.GetWikiChainName(_effectiveChain));
    }

    private async Task ShowRefreshNotification()
    {
        warningBar.Message = "Table refreshed.";
        warningBar.Severity = InfoBarSeverity.Informational;
        warningBar.IsOpen = true;

        await Task.Delay(2000);
        if (warningBar.Message == "Table refreshed.")
            warningBar.IsOpen = false;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(txtOutput.Text))
        {
            App.NativeSetClipboardText(txtOutput.Text);
            Increment(s => s.TablesGenerated++);
            btnCopy.Content = "Copied!";
            _ = ResetCopyButton();
        }
    }

    private async Task ResetCopyButton()
    {
        await Task.Delay(2000);
        btnCopy.Content = "Copy to Clipboard";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
