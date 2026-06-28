using System.Text;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public class InfoboxGeneratorOptions
{
    public bool AddShop { get; set; }
    public bool IsStarter { get; set; }
    public bool IsPoints { get; set; }
    public bool IsDepleting { get; set; }   // manual override
    public bool KeepFullName { get; set; }  // keep parenthetical in title
    public bool HardcodeGalleryImage { get; set; } // use item name instead of {{PAGENAME}} in gallery
    public string? EventName { get; set; }  // when non-null, prepended as {{#vardefine:EventName|...}} above the Infobox
}

public class InfoboxGeneratorService
{
    private readonly DataService? _data;
    private readonly WikiMappingCache? _wikiMapping;

    public InfoboxGeneratorService() { }

    public InfoboxGeneratorService(DataService data, WikiMappingCache? wikiMapping = null)
    {
        _data = data;
        _wikiMapping = wikiMapping;
    }

    public List<string> Warnings { get; } = new();

    /// <summary>Area display name detected from task-reward lookup (set by BuildAutoSources).</summary>
    public string? TaskRewardAreaName { get; private set; }

    /// <summary>
    /// Item-specific reward boxes that are no longer available in the game.
    /// These are excluded from auto-detected source lists.
    /// </summary>
    private static readonly HashSet<string> IgnoredSourceChains = new(StringComparer.OrdinalIgnoreCase)
    {
        "RewardBoxCleaning",
        "RewardBoxDetergent",
        "RewardBoxFlower",
        "RewardBoxGardenBench",
        "RewardBoxGardeningGloves",
        "RewardBoxLamp",
        "RewardBoxOrangeFlower",
        "RewardBoxPaintCan",
        "RewardBoxPlantedFlower",
        "RewardBoxScrews",
        "RewardBoxTools",
    };

    private bool IsSinkSuppressed(ParsedItem item) =>
        _wikiMapping?.Mappings.TryGetValue(item.ItemType, out var entry) == true && entry.IsNil("fuels");

    /// <summary>
    /// True when a chain is the game's dev/QA sandbox content, marked by the "Test" item tag
    /// (e.g. TestPlant, DustySpawner, TestSeedBag, AutoSeedToSeedling, SplitDowngrade, Backtrack).
    /// Every item in such chains carries the tag. These never ship to players, so attributing a
    /// real item's source/drop to them is misleading — same rationale as the DoesNotShowInInfoPopUp
    /// per-item filter in <see cref="BuildAutoSources"/>.
    /// </summary>
    private static bool IsTestChain(ParsedChain chain) =>
        chain.Items.Any(i => i.Tags?.Any(
            t => string.Equals(t, "Test", StringComparison.OrdinalIgnoreCase)) == true);

    /// <summary>
    /// Resolves a chain display name, preferring wiki mapping when available.
    /// Falls back to chain.DisplayName when DataService is not set.
    /// </summary>
    private string ResolveChainName(ParsedChain chain, string? itemType = null)
    {
        if (_data != null && !string.IsNullOrEmpty(itemType))
            return _data.ResolveChainDisplayNameFromItemType(itemType, _wikiMapping);
        return chain.DisplayName;
    }

    /// <summary>
    /// Resolves level for an ItemType, preferring wiki mapping when available.
    /// Falls back to the ParsedItem level when DataService is not set.
    /// </summary>
    private int ResolveLevel(string itemType, ParsedItem? matchingItem)
    {
        if (_data != null)
            return _data.ResolveLevel(itemType, _wikiMapping);
        return matchingItem?.Level ?? 1;
    }

    /// <summary>
    /// Builds the complete {{Infobox Items|...}} wikitext for a chain.
    /// sourceLines: pre-built source lines (each a wiki template string), joined with &lt;br/&gt;
    /// </summary>
    public string Generate(
        ParsedChain chain,
        IReadOnlyList<ParsedChain> allChains,
        Dictionary<string, string> itemNames,
        InfoboxGeneratorOptions opts,
        IReadOnlyList<string> manualSourceLines)
    {
        Warnings.Clear();

        // Collect all fields first, then format with aligned '='
        var fields = new List<(string Key, string Value)>();

        // ── title1 ──
        // When chain name has a parenthetical disambiguation suffix (e.g. "Tools (Beach Shack)"),
        // the wiki page name MUST keep the suffix to avoid collisions with other "Tools" pages —
        // but the visible page title should show only the short name. We use the standard
        // DisplayTitle pattern: {{#vardefine:DisplayTitle|<stripped>}} at top + DISPLAYTITLE
        // magic word + {{#var:DisplayTitle}} as title1. This mirrors MysteryWikiService's
        // handling for mystery pages. (Emitted as a prefix block below — see `sb.Append…`.)
        var baseName = StripParentheses(chain.DisplayName);
        bool hasParen = baseName != chain.DisplayName;
        if (hasParen)
            fields.Add(("title1", "{{#var:DisplayTitle}}"));

        // ── image1 (gallery) ──
        // Use the chain's ACTUAL min/max levels — a chain may have a single item at level 4 (no
        // level 1), so we must not hardcode "Level 1" nor assume more than one image exists.
        int minLvl = chain.Items.Min(i => i.Level);
        int maxLvl = chain.Items.Max(i => i.Level);
        var imgName = opts.HardcodeGalleryImage
            ? (opts.KeepFullName ? chain.DisplayName : baseName)
            : "{{PAGENAME}}";
        if (minLvl != maxLvl)
        {
            // First and last item images (Lua invoke keeps the max correct even if data shifts).
            var gallerySb = new StringBuilder();
            gallerySb.AppendLine();
            gallerySb.AppendLine("{{#tag:gallery|");
            gallerySb.AppendLine($"  {{{{ItemNameToFilename|{imgName}|{minLvl}}}}}{{{{!}}}} Level {minLvl}");
            gallerySb.AppendLine($"  {{{{ItemNameToFilename|{imgName}|{{{{#Invoke:Items|GetItemMaxLevelFromChainName}}}}}}}}{{{{!}}}} Level {{{{#Invoke:Items|GetItemMaxLevelFromChainName}}}}");
            gallerySb.Append("}}");
            fields.Add(("image1", gallerySb.ToString()));
        }
        else
        {
            // Single item (one level, possibly not level 1) → one image at that level.
            fields.Add(("image1", $"{{{{#tag:gallery|{{{{ItemNameToFilename|{imgName}|{minLvl}}}}}}}}}"));
        }

        // ── type ──
        var types = BuildTypes(chain, allChains, opts);
        if (types.Count > 0)
            fields.Add(("type", string.Join("<br>", types)));

        // ── source ──
        var sourceLines = new List<string>(manualSourceLines);
        // Repeatable-tagged items (e.g. Water Bucket) reset/respawn via a Repeatable Task —
        // emit it as the first source so readers see the gameplay loop up front.
        bool hasRepeatable = chain.Items.Any(i =>
            i.Tags != null && i.Tags.Any(t => string.Equals(t, "Repeatable", StringComparison.OrdinalIgnoreCase)));
        if (hasRepeatable)
            sourceLines.Insert(0, "Repeatable Task");
        if (opts.AddShop)
            sourceLines.Add("{{Item/Group|Shop}}");
        if (!string.IsNullOrEmpty(TaskRewardAreaName))
        {
            var taskLink = opts.KeepFullName && baseName != chain.DisplayName
                ? $"{{{{TaskLink|{{{{#var:SourceArea}}}}|{chain.DisplayName}}}}}"
                : "{{TaskLink|{{#var:SourceArea}}}}";
            sourceLines.Add(taskLink);
        }
        if (sourceLines.Count > 0)
            fields.Add(("source", string.Join("<br>", sourceLines)));

        // ── drops ──
        var drops = BuildDrops(chain, allChains, itemNames);
        if (drops.Count > 0)
            fields.Add(("drops", string.Join("<br>", drops)));

        // ── decays_into ──
        var decaysInto = BuildDecaysInto(chain, allChains, itemNames);
        if (!string.IsNullOrEmpty(decaysInto))
            fields.Add(("decays_into", decaysInto));

        // ── used_in ──
        var usedIn = BuildUsedIn(chain, allChains, itemNames);
        if (usedIn.Count > 0)
            fields.Add(("used_in", string.Join("<br>", usedIn)));

        // ── transforms_to ──
        var transforms = BuildTransformsTo(chain, allChains);
        if (transforms.Count > 0)
            fields.Add(("transforms_to", string.Join("<br>", transforms)));

        // ── needs ──
        var needs = BuildNeeds(chain, allChains, itemNames);
        if (needs.Count > 0)
            fields.Add(("needs", string.Join("<br>", needs)));

        // Format with aligned '='
        int maxKeyLen = fields.Max(f => f.Key.Length);
        var sb = new StringBuilder();

        // Prepend {{#vardefine:DisplayTitle|...}} + {{DISPLAYTITLE:...}} for parenthetical names.
        // MUST come BEFORE {{Infobox Items}} so the title swap applies to the rendered page.
        if (hasParen)
        {
            sb.AppendLine($"{{{{#vardefine:DisplayTitle|{baseName}}}}}");
            sb.AppendLine("{{DISPLAYTITLE:{{#var:DisplayTitle}}}}");
        }
        // Prepend {{#vardefine:EventName|...}} when chain belongs to a CollectibleBoard event.
        // Used by the Table Generator's Infobox Section intro sentence ({{#var:EventName}}).
        if (!string.IsNullOrEmpty(opts.EventName))
        {
            sb.AppendLine($"{{{{#vardefine:EventName|{opts.EventName}}}}}");
        }
        // Prepend {{#vardefine:SourceArea|...}} when task-reward area was detected
        if (!string.IsNullOrEmpty(TaskRewardAreaName))
        {
            sb.AppendLine($"{{{{#vardefine:SourceArea|{TaskRewardAreaName}}}}}");
        }

        sb.AppendLine("{{Infobox Items");
        foreach (var (key, value) in fields)
            sb.AppendLine($"| {key.PadRight(maxKeyLen)} = {value}");
        sb.Append("}}");
        return sb.ToString();
    }

    // ── Type detection ────────────────────────────────────────────────

    private List<string> BuildTypes(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, InfoboxGeneratorOptions opts)
    {
        var types = new List<string>();

        // Starter Item — user-marked, must be the FIRST type in the infobox.
        if (opts.IsStarter)          types.Add("Starter Item");

        // Drop Item — any other chain drops items from THIS chain (incl. ChestFeatures
        // loot producers for chests like Brown Chest, Mystery Chest, Reward Boxes).
        // Items tagged "DoesNotShowInInfoPopUp" are skipped (game-side hidden — same
        // filter as in BuildAutoSources). Otherwise items whose only source is e.g.
        // an AdventCalendar prize chain would still be tagged "Drop Item" even though
        // the source line is empty.
        var myItemTypes = chain.Items.Select(i => i.ItemType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool isDropped = allChains
            .Where(c => c != chain)
            .SelectMany(c => c.Items)
            .Where(i => i.Tags?.Any(t => string.Equals(t, "DoesNotShowInInfoPopUp", StringComparison.OrdinalIgnoreCase)) != true)
            .Any(i => (i.DropOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                      (i.SpawnOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                      (i.SpawnItemType != null && myItemTypes.Contains(i.SpawnItemType)) ||
                      (i.ChestRewardOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true));
        if (isDropped) types.Add("Drop Item");

        // OrderFeatures items (e.g. Distillation Apparatus, Vending Machine) act as primary
        // sources of their reward chains — task rewards are equivalent in role to drops.
        bool hasOrderRewards = chain.Items.Any(i => i.OrderTasks is { Count: > 0 } && i.OrderTasks.Any(t => t.Rewards.Count > 0));
        if (chain.HasGenerators || hasOrderRewards)     types.Add("Primary Source Item");
        if (chain.HasSpawners)       types.Add("Secondary Source Item");
        if (chain.IsEventChain)      types.Add("Event Item");

        // Temporary Item — any item in chain has IsTemporary flag
        if (chain.Items.Any(i => i.IsTemporary))
            types.Add("Temporary Item");

        // Decaying Item — any item has any form of decay
        bool decaying = chain.Items.Any(i =>
            i.HasDecay ||
            !string.IsNullOrEmpty(i.DecayAfterLastCycleItemType) ||
            (i.DecayAfterLastCycleOdds != null && i.DecayAfterLastCycleOdds.Count > 0) ||
            !string.IsNullOrEmpty(i.SpawnDecayIntoItemType));
        if (decaying) types.Add("Decaying Item");

        // Depleting Item — producer with limited cycles, no decay, no transform
        bool depleting = opts.IsDepleting ||
            chain.Items.Any(i =>
                ((i.IsGenerator && i.ActivationHowManyCycles > 0) ||
                 (i.IsSpawner && i.SpawnHowManyCycles > 0))
                && !i.IsSink
                && !i.HasDecay
                && string.IsNullOrEmpty(i.DecayAfterLastCycleItemType)
                && (i.DecayAfterLastCycleOdds == null || i.DecayAfterLastCycleOdds.Count == 0)
                && string.IsNullOrEmpty(i.SpawnDecayIntoItemType));
        if (depleting) types.Add("Depleting Item");

        // Fueled Item — chain consumes other items. Two consumption mechanics: sink fuel
        // (NumericConfigKey-based, e.g. ScoreTargets) and order tasks (ItemType-based, e.g.
        // The Cinema's order requires Cinema Ticket + Cinema Snacks per movie).
        bool fueled = chain.Items.Any(i =>
            (i.IsSink && !IsSinkSuppressed(i)) ||
            (i.OrderTasks != null && i.OrderTasks.Any(t => t.Required.Count > 0)));
        if (fueled) types.Add("Fueled Item");

        // Fuel Item — this chain's items are consumed elsewhere. Mirror of Fueled detection:
        // sink consumption matches via NumericConfigKey; order consumption matches via ItemType.
        var myConfigKeys = chain.Items
            .Where(i => !string.IsNullOrEmpty(i.NumericConfigKey))
            .Select(i => i.NumericConfigKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool isFuelBySink = myConfigKeys.Count > 0 && allChains
            .Where(c => c != chain)
            .SelectMany(c => c.Items)
            .Where(i => i.IsSink && !IsSinkSuppressed(i) && i.SinkRequirementConfigKeys != null)
            .SelectMany(i => i.SinkRequirementConfigKeys!)
            .Any(req => myConfigKeys.Contains(req));
        bool isFuelByOrder = myItemTypes.Count > 0 && allChains
            .Where(c => c != chain)
            .SelectMany(c => c.Items)
            .Any(i => i.IsOrder && i.OrderRequiredItems != null
                && i.OrderRequiredItems.Keys.Any(k => myItemTypes.Contains(k)));
        if (isFuelBySink || isFuelByOrder) types.Add("Fuel Item");

        if (opts.IsPoints) types.Add("Points Item");

        return types;
    }

    // ── Source lookup ─────────────────────────────────────────────────

    /// <summary>
    /// Finds all chains that produce items in this chain.
    /// Chains in <see cref="IgnoredSourceChains"/> are excluded.
    /// Returns formatted wiki template strings.
    /// </summary>
    public List<string> BuildAutoSources(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames,
        IReadOnlyList<LuaArea>? areas = null)
    {
        var myItemTypes = chain.Items.Select(i => i.ItemType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var sourceLevels = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        var sourceChains = new Dictionary<string, ParsedChain>(StringComparer.OrdinalIgnoreCase);

        foreach (var otherChain in allChains)
        {
            if (otherChain == chain) continue;
            if (IgnoredSourceChains.Contains(otherChain.ConfigKey)) continue;
            if (IsTestChain(otherChain)) continue;

            foreach (var item in otherChain.Items)
            {
                // Game flag: items tagged "DoesNotShowInInfoPopUp" are intentionally
                // hidden from in-game source/drop popups. Wiki should respect the same
                // semantic — showing them in the wiki source field is misleading because
                // the player never sees the drop in-game.
                //
                // Affects ~67 items / 62 chains in v26.04.01 (AdventCalendar prizes,
                // Predefined chest decoys, PEChristmas2022 GoC entries, LDE Grandma's
                // Birthday2023 entrance/exit items, etc.). Example: Simple Brown Box
                // (Plain Box) was attributed to AdventCalendar2023_Chocolate7 +
                // AdventCalendar2025_Prize1 even though those drops are hidden in-game.
                if (item.Tags?.Any(t => string.Equals(t, "DoesNotShowInInfoPopUp", StringComparison.OrdinalIgnoreCase)) == true)
                    continue;

                // Drop/Spawn sources (incl. ChestFeatures.LootProducer for chests like
                // Brown Chest, Mystery Chest, Reward Boxes — items they drop on opening).
                bool drops = (item.DropOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                             (item.SpawnOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                             (item.SpawnItemType != null && myItemTypes.Contains(item.SpawnItemType)) ||
                             (item.ChestRewardOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true);

                // Sink reward source (other chain transforms INTO this chain's items)
                bool transforms = item.IsSink && !IsSinkSuppressed(item)
                    && !string.IsNullOrEmpty(item.SinkRewardItemType)
                    && myItemTypes.Contains(item.SinkRewardItemType);

                // Order reward source (other chain's order tasks reward this chain's items)
                bool orderRewards = item.IsOrder
                    && item.OrderRewardItems != null
                    && item.OrderRewardItems.Keys.Any(k => myItemTypes.Contains(k));

                // Decay source (other chain's item decays INTO this chain's items)
                bool decays =
                    (!string.IsNullOrEmpty(item.DecayIntoItemType) && myItemTypes.Contains(item.DecayIntoItemType)) ||
                    (!string.IsNullOrEmpty(item.SpawnDecayIntoItemType) && myItemTypes.Contains(item.SpawnDecayIntoItemType)) ||
                    (!string.IsNullOrEmpty(item.DecayAfterLastCycleItemType) && myItemTypes.Contains(item.DecayAfterLastCycleItemType)) ||
                    (item.DecayAfterLastCycleOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true);

                if (!drops && !transforms && !orderRewards && !decays) continue;

                // Collect ALL producing levels (aliases transparent — treated as primary items at their levels)
                var resolvedLevel = ResolveLevel(item.ItemType, item);
                sourceChains.TryAdd(otherChain.ConfigKey, otherChain);
                if (!sourceLevels.TryGetValue(otherChain.ConfigKey, out var levelSet))
                {
                    levelSet = new SortedSet<int>();
                    sourceLevels[otherChain.ConfigKey] = levelSet;
                }
                if (resolvedLevel > 0) levelSet.Add(resolvedLevel);
            }
        }

        // Emit source lines per source-level-rules (see memory/source-level-rules.md):
        // all levels produce → no level · single/range/non-consecutive → templates joined <br>
        foreach (var (configKey, producingLevels) in sourceLevels)
        {
            var otherChain = sourceChains[configKey];
            var sourceName = ResolveChainName(otherChain, otherChain.Items.FirstOrDefault()?.ItemType);
            result.Add(FormatSourceEntry(sourceName, otherChain, producingLevels));
        }

        // Task reward source — temp items that are rewards from area tasks
        TaskRewardAreaName = null;
        if (areas != null)
        {
            var tempItemTypes = chain.Items
                .Where(i => i.IsTemporary)
                .Select(i => i.ItemType)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (tempItemTypes.Count > 0)
            {
                foreach (var area in areas)
                {
                    foreach (var task in area.Tasks)
                    {
                        if (!string.IsNullOrEmpty(task.ItemReward) && tempItemTypes.Contains(task.ItemReward))
                        {
                            TaskRewardAreaName = area.DisplayName;
                            goto doneTaskSearch;
                        }
                    }
                }
                doneTaskSearch:;
            }
        }

        return result;
    }

    /// <summary>
    /// Formats one source chain entry with appropriate level(s) per source-level-rules.md.
    /// - producingLevels.Count == totalLevels OR chain is single-level → no level (generic)
    /// - Single producing level → {{Item/Group|Name|N|min=N[|fullName=true]}}
    /// - Consecutive range → {{Item/Group|Name|max|min=min|max=max[|fullName=true]}}
    /// - Non-consecutive → split into runs, each formatted as single/range, joined by &lt;br&gt;
    /// fullName=true is added only when name has a parenthetical suffix AND chain is non-event
    /// (otherwise it's redundant — wiki template defaults to name-as-is for unparenthesised chains).
    /// </summary>
    private string FormatSourceEntry(string sourceName, ParsedChain sourceChain, SortedSet<int> producingLevels)
    {
        // Resolved levels of the chain (aliases counted transparently)
        var levels = sourceChain.Items
            .Select(i => ResolveLevel(i.ItemType, i))
            .Where(lvl => lvl > 0)
            .ToList();
        var totalLevels = levels.Distinct().Count();
        // Highest producing level — required as positional arg so Item/Group picks the right icon
        // (absent positional defaults to level=1 in the Lua template)
        var maxProducingLevel = producingLevels.Count > 0
            ? producingLevels.Max()
            : (levels.Count > 0 ? levels.Max() : 1);

        // fullName=true preserves "(...)" suffix — only needed for non-event chains with disambiguation
        bool needsFullName = !sourceChain.IsEventChain && HasParentheticalSuffix(sourceName);
        string fullNameSuffix = needsFullName ? "|fullName=true" : "";

        // All levels produce OR single-level chain OR no valid producing levels
        // → no min=/max= (no level text), but emit positional level for correct icon
        if (producingLevels.Count == 0 || totalLevels <= 1 || producingLevels.Count == totalLevels)
            return $"{{{{Item/Group|{sourceName}|{maxProducingLevel}{fullNameSuffix}}}}}";

        // Detect contiguous runs and emit one template per run
        var entries = SplitIntoRuns(producingLevels).Select(r =>
            r.Min == r.Max
                ? $"{{{{Item/Group|{sourceName}|{r.Min}|min={r.Min}{fullNameSuffix}}}}}"
                : $"{{{{Item/Group|{sourceName}|{r.Max}|min={r.Min}|max={r.Max}{fullNameSuffix}}}}}");

        return string.Join("<br>", entries);
    }

    /// <summary>
    /// Detects parenthetical disambiguation suffix at end of chain name, e.g. "Hammer (Garden Rope)".
    /// Mirrors Lua Module:Utils regex `%s*%b()$` which strips such suffixes by default.
    /// </summary>
    private static bool HasParentheticalSuffix(string name)
        => name.TrimEnd().EndsWith(")") && name.Contains('(');

    /// <summary>Splits a sorted set of integers into contiguous (Min, Max) runs.</summary>
    private static List<(int Min, int Max)> SplitIntoRuns(SortedSet<int> levels)
    {
        var runs = new List<(int Min, int Max)>();
        int? start = null, end = null;
        foreach (var lvl in levels) // already sorted ascending
        {
            if (start == null) { start = end = lvl; }
            else if (lvl == end + 1) { end = lvl; }
            else
            {
                runs.Add((start.Value, end.Value));
                start = end = lvl;
            }
        }
        if (start != null) runs.Add((start.Value, end.Value));
        return runs;
    }

    // ── Drops ─────────────────────────────────────────────────────────

    private List<string> BuildDrops(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        var itemTypeToChain = BuildItemTypeToChain(allChains);

        // Collect all dropped ItemTypes across all items in the chain
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byChain = new Dictionary<string, (ParsedChain Chain, List<int> Levels)>(StringComparer.OrdinalIgnoreCase);
        var unmatchedNames = new List<string>();

        foreach (var item in chain.Items)
        {
            var allOdds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item.DropOdds != null)  foreach (var k in item.DropOdds.Keys)  allOdds.Add(k);
            if (item.SpawnOdds != null) foreach (var k in item.SpawnOdds.Keys) allOdds.Add(k);
            if (item.SpawnItemType != null) allOdds.Add(item.SpawnItemType);
            // OrderFeatures rewards (e.g. Distillation Apparatus task rewards = Perfumes / Perfume Collection)
            if (item.OrderTasks != null)
                foreach (var t in item.OrderTasks)
                    foreach (var (rewardType, _) in t.Rewards)
                        allOdds.Add(rewardType);

            foreach (var droppedType in allOdds)
            {
                if (!seen.Add(droppedType)) continue;

                if (itemTypeToChain.TryGetValue(droppedType, out var droppedChain))
                {
                    var matchingItem = droppedChain.Items
                        .FirstOrDefault(i => string.Equals(i.ItemType, droppedType, StringComparison.OrdinalIgnoreCase));
                    int lvl = ResolveLevel(droppedType, matchingItem);

                    if (!byChain.TryGetValue(droppedChain.ConfigKey, out var entry))
                    {
                        entry = (droppedChain, new List<int>());
                        byChain[droppedChain.ConfigKey] = entry;
                    }
                    if (!entry.Levels.Contains(lvl))
                        entry.Levels.Add(lvl);
                }
                else if (itemNames.TryGetValue(droppedType, out var name))
                {
                    if (!unmatchedNames.Contains(name))
                        unmatchedNames.Add(name);
                }
            }
        }

        var result = new List<string>();

        foreach (var (_, (dChain, levels)) in byChain)
        {
            var chainName = ResolveChainName(dChain, dChain.Items.FirstOrDefault()?.ItemType);
            bool needsFullName = !dChain.IsEventChain && HasParentheticalSuffix(chainName);
            string fullNameSuffix = needsFullName ? "|fullName=true" : "";
            int minLvl = levels.Min();
            int maxLvl = levels.Max();
            // Single level → {{Item}} (level-specific name).
            // Multiple levels → {{Item/Group}} with max-level icon + min/max range labels.
            result.Add(levels.Count == 1
                ? $"{{{{Item|{chainName}|{maxLvl}{fullNameSuffix}}}}}"
                : $"{{{{Item/Group|{chainName}|{maxLvl}|min={minLvl}|max={maxLvl}{fullNameSuffix}}}}}");
        }

        foreach (var name in unmatchedNames)
            result.Add($"{{{{Item/nolevel|{name}}}}}");

        return result;
    }

    // ── Decays Into ───────────────────────────────────────────────────

    private string BuildDecaysInto(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        var itemTypeToChain = BuildItemTypeToChain(allChains);

        // Group decay targets by chainName, collecting all decay levels per chain
        var byChain = new Dictionary<string, (ParsedChain Chain, SortedSet<int> Levels)>(StringComparer.OrdinalIgnoreCase);
        var unmatchedNames = new List<string>();

        foreach (var item in chain.Items)
        {
            var decayTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(item.DecayIntoItemType))
                decayTypes.Add(item.DecayIntoItemType);
            if (!string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
                decayTypes.Add(item.SpawnDecayIntoItemType);
            if (!string.IsNullOrEmpty(item.DecayAfterLastCycleItemType))
                decayTypes.Add(item.DecayAfterLastCycleItemType);
            if (item.DecayAfterLastCycleOdds != null)
                foreach (var key in item.DecayAfterLastCycleOdds.Keys)
                    decayTypes.Add(key);

            foreach (var decayType in decayTypes)
            {
                if (itemTypeToChain.TryGetValue(decayType, out var decayChain))
                {
                    var matchingItem = decayChain.Items
                        .FirstOrDefault(i => string.Equals(i.ItemType, decayType, StringComparison.OrdinalIgnoreCase));
                    int lvl = ResolveLevel(decayType, matchingItem);
                    var chainName = ResolveChainName(decayChain, decayType);

                    if (!byChain.TryGetValue(chainName, out var entry))
                    {
                        entry = (decayChain, new SortedSet<int>());
                        byChain[chainName] = entry;
                    }
                    entry.Levels.Add(lvl);
                }
                else if (itemNames.TryGetValue(decayType, out var name))
                {
                    if (!unmatchedNames.Contains(name))
                        unmatchedNames.Add(name);
                }
            }
        }

        var parts = new List<string>();
        foreach (var (chainName, (dChain, levels)) in byChain)
        {
            bool needsFullName = !dChain.IsEventChain && HasParentheticalSuffix(chainName);
            string fullNameSuffix = needsFullName ? "|fullName=true" : "";

            // Emit one template per contiguous level run (e.g. L3-L10 → one Item/Group, L3+L5+L10 → three entries)
            foreach (var (min, max) in SplitIntoRuns(levels))
            {
                if (min == max)
                    parts.Add($"{{{{Item|{chainName}|{min}{fullNameSuffix}}}}}");
                else
                    parts.Add($"{{{{Item/Group|{chainName}|{max}|min={min}|max={max}{fullNameSuffix}}}}}");
            }
        }

        foreach (var name in unmatchedNames)
            parts.Add($"{{{{Item/nolevel|{name}}}}}");

        return string.Join("<br>", parts);
    }

    // ── Transforms To (sink reward) ──────────────────────────────────

    private List<string> BuildTransformsTo(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains)
    {
        var itemTypeToChain = BuildItemTypeToChain(allChains);
        var seen = new HashSet<(string Name, int Level)>();
        var result = new List<string>();

        foreach (var item in chain.Items)
        {
            // Sink reward (Transformative Item — e.g. Distillation Apparatus output)
            if (item.IsSink && !string.IsNullOrEmpty(item.SinkRewardItemType) && !IsSinkSuppressed(item))
            {
                if (itemTypeToChain.TryGetValue(item.SinkRewardItemType, out var rewardChain))
                {
                    var rewardItem = rewardChain.Items
                        .FirstOrDefault(i => string.Equals(i.ItemType, item.SinkRewardItemType, StringComparison.OrdinalIgnoreCase));
                    int lvl = ResolveLevel(item.SinkRewardItemType, rewardItem);
                    var chainName = ResolveChainName(rewardChain, item.SinkRewardItemType);

                    if (seen.Add((chainName, lvl)))
                        result.Add($"{{{{Item|{chainName}|{lvl}}}}}");
                }
            }

            // Cross-chain merge result (e.g. Bigger Pile of Seed Bags L4 → Golden Seed L1).
            // Only emit when the merge target lives in a DIFFERENT chain — same-chain L+1
            // merges are the default progression and don't need a transforms_to row.
            if (!string.IsNullOrEmpty(item.MergeResultItemType)
                && itemTypeToChain.TryGetValue(item.MergeResultItemType, out var mergeChain)
                && mergeChain != chain)
            {
                var mergeItem = mergeChain.Items
                    .FirstOrDefault(i => string.Equals(i.ItemType, item.MergeResultItemType, StringComparison.OrdinalIgnoreCase));
                int lvl = ResolveLevel(item.MergeResultItemType, mergeItem);
                var chainName = ResolveChainName(mergeChain, item.MergeResultItemType);

                if (seen.Add((chainName, lvl)))
                    result.Add($"{{{{Item|{chainName}|{lvl}}}}}");
            }
        }

        return result;
    }

    // ── Used In (this chain's items are sink requirements elsewhere) ──

    private List<string> BuildUsedIn(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        var myConfigKeys = chain.Items
            .Where(i => !string.IsNullOrEmpty(i.NumericConfigKey))
            .Select(i => i.NumericConfigKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var myItemTypes = chain.Items
            .Where(i => !string.IsNullOrEmpty(i.ItemType))
            .Select(i => i.ItemType!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var otherChain in allChains)
        {
            if (otherChain == chain) continue;

            foreach (var item in otherChain.Items)
            {
                // Match via NumericConfigKey (ScoreTargets — sink fuel)
                bool matchesSinkFuel = item.IsSink && !IsSinkSuppressed(item)
                    && item.SinkRequirementConfigKeys != null
                    && item.SinkRequirementConfigKeys.Any(r => myConfigKeys.Contains(r));

                // Match via order required items (this chain's items are needed by order tasks)
                bool matchesOrderReq = item.IsOrder && item.OrderRequiredItems != null
                    && item.OrderRequiredItems.Keys.Any(k => myItemTypes.Contains(k));

                if (!matchesSinkFuel && !matchesOrderReq) continue;

                if (!seen.Add(otherChain.ConfigKey)) continue;

                var usedInName = ResolveChainName(otherChain, otherChain.Items.FirstOrDefault()?.ItemType);
                result.Add($"{{{{Item/Group|{usedInName}}}}}");
            }
        }

        return result;
    }

    // ── Needs (this item IS the sink) ─────────────────────────────────

    private List<string> BuildNeeds(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        var configKeyToChain = BuildConfigKeyToChain(allChains);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect per chain: actual item levels + amounts + sort key (slot index for OrderTasks
        // requirements so all slot-0 chains group before slot-1, etc. — e.g. Distillation
        // Apparatus emits Lemon/Vanilla/Rose Essence consecutively, then Watering Can/Bottle
        // consecutively, instead of interleaving. Sink-only chains use sort key 0.
        var byChain = new Dictionary<string, (ParsedChain Chain, List<(int Level, int Amount)> Items, int SortKey, int InsertOrder)>(StringComparer.OrdinalIgnoreCase);
        int insertCounter = 0;

        var itemTypeToChain = BuildItemTypeToChain(allChains);
        foreach (var item in chain.Items)
        {
            // Sink requirements (NumericConfigKey-based)
            if (item.IsSink && item.SinkRequirementConfigKeys != null && !IsSinkSuppressed(item))
            {
                foreach (var reqKey in item.SinkRequirementConfigKeys)
                {
                    if (!seen.Add(reqKey)) continue;

                    if (configKeyToChain.TryGetValue(reqKey, out var match))
                    {
                        int level = match.Item.Level;
                        int amount = item.SinkRequirementAmounts != null
                            && item.SinkRequirementAmounts.TryGetValue(reqKey, out var amt) ? amt : 1;

                        if (!byChain.TryGetValue(match.Chain.ConfigKey, out var entry))
                        {
                            entry = (match.Chain, new List<(int, int)>(), 0, insertCounter++);
                            byChain[match.Chain.ConfigKey] = entry;
                        }
                        entry.Items.Add((level, amount));
                        byChain[match.Chain.ConfigKey] = entry;
                    }
                }
            }

            // OrderFeatures requirements (per-task Required items, e.g. Distillation Apparatus essences/water)
            if (item.OrderTasks != null)
            {
                foreach (var t in item.OrderTasks)
                {
                    for (int slot = 0; slot < t.Required.Count; slot++)
                    {
                        var (reqItemType, amount) = t.Required[slot];
                        if (!seen.Add(reqItemType)) continue;
                        if (!itemTypeToChain.TryGetValue(reqItemType, out var reqChain)) continue;
                        var reqItem = reqChain.Items.FirstOrDefault(i =>
                            string.Equals(i.ItemType, reqItemType, StringComparison.OrdinalIgnoreCase));
                        if (reqItem == null) continue;
                        if (!byChain.TryGetValue(reqChain.ConfigKey, out var entry))
                        {
                            entry = (reqChain, new List<(int, int)>(), slot, insertCounter++);
                            byChain[reqChain.ConfigKey] = entry;
                        }
                        else if (slot < entry.SortKey)
                        {
                            entry = (entry.Chain, entry.Items, slot, entry.InsertOrder);
                        }
                        entry.Items.Add((reqItem.Level, amount));
                        byChain[reqChain.ConfigKey] = entry;
                    }
                }
            }
        }

        var result = new List<string>();

        foreach (var (_, (needChain, items, _, _)) in byChain.OrderBy(kv => kv.Value.SortKey).ThenBy(kv => kv.Value.InsertOrder))
        {
            var chainName = ResolveChainName(needChain, needChain.Items.FirstOrDefault()?.ItemType);
            bool needsFullName = !needChain.IsEventChain && HasParentheticalSuffix(chainName);
            string fullNameSuffix = needsFullName ? "|fullName=true" : "";

            // Aggregate consecutive levels into Item/Group ranges. Per-level amounts
            // are intentionally dropped — infobox is overview-only; per-task amounts
            // live in the Tasks table on the same wiki page.
            var levelSet = new SortedSet<int>(items.Select(x => x.Level));
            foreach (var (min, max) in SplitIntoRuns(levelSet))
            {
                result.Add(min == max
                    ? $"{{{{Item|{chainName}|{min}{fullNameSuffix}}}}}"
                    : $"{{{{Item/Group|{chainName}|{max}|min={min}|max={max}{fullNameSuffix}}}}}");
            }
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private record ConfigKeyMatch(ParsedChain Chain, ParsedItem Item);

    private static Dictionary<string, ConfigKeyMatch> BuildConfigKeyToChain(IReadOnlyList<ParsedChain> allChains)
    {
        var dict = new Dictionary<string, ConfigKeyMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in allChains)
            foreach (var item in chain.Items)
                if (!string.IsNullOrEmpty(item.NumericConfigKey))
                    dict.TryAdd(item.NumericConfigKey, new ConfigKeyMatch(chain, item));
        return dict;
    }

    private static Dictionary<string, ParsedChain> BuildItemTypeToChain(IReadOnlyList<ParsedChain> allChains)
    {
        var dict = new Dictionary<string, ParsedChain>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in allChains)
            foreach (var item in chain.Items)
                if (!string.IsNullOrEmpty(item.ItemType))
                    dict.TryAdd(item.ItemType, chain);
        return dict;
    }

    private static string StripParentheses(string name)
    {
        var idx = name.IndexOf('(');
        return idx > 0 ? name[..idx].TrimEnd() : name;
    }
}
