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
        var baseName = StripParentheses(chain.DisplayName);
        if (baseName != chain.DisplayName)
            fields.Add(("title1", opts.KeepFullName ? chain.DisplayName : baseName));

        // ── image1 (gallery) ──
        int maxLvl = chain.Items.Max(i => i.Level);
        var imgName = opts.HardcodeGalleryImage
            ? (opts.KeepFullName ? chain.DisplayName : baseName)
            : "{{PAGENAME}}";
        if (maxLvl > 1)
        {
            var gallerySb = new StringBuilder();
            gallerySb.AppendLine();
            gallerySb.AppendLine("{{#tag:gallery|");
            gallerySb.AppendLine($"  {{{{ItemNameToFilename|{imgName}|1}}}}{{{{!}}}} Level 1");
            gallerySb.AppendLine($"  {{{{ItemNameToFilename|{imgName}|{{{{#Invoke:Items|GetItemMaxLevelFromChainName}}}}}}}}{{{{!}}}} Level {{{{#Invoke:Items|GetItemMaxLevelFromChainName}}}}");
            gallerySb.Append("}}");
            fields.Add(("image1", gallerySb.ToString()));
        }
        else
        {
            fields.Add(("image1", $"{{{{#tag:gallery|{{{{ItemNameToFilename|{imgName}|1}}}}}}}}"));
        }

        // ── type ──
        var types = BuildTypes(chain, allChains, opts);
        if (types.Count > 0)
            fields.Add(("type", string.Join("<br>", types)));

        // ── source ──
        var sourceLines = new List<string>(manualSourceLines);
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

        // Drop Item — any other chain drops items from THIS chain
        var myItemTypes = chain.Items.Select(i => i.ItemType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool isDropped = allChains
            .Where(c => c != chain)
            .SelectMany(c => c.Items)
            .Any(i => (i.DropOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                      (i.SpawnOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                      (i.SpawnItemType != null && myItemTypes.Contains(i.SpawnItemType)));
        if (isDropped) types.Add("Drop Item");

        if (opts.IsStarter)          types.Add("Starter Item");
        if (chain.HasGenerators)     types.Add("Primary Source Item");
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

        bool fueled = chain.Items.Any(i => i.IsSink && !IsSinkSuppressed(i));
        if (fueled) types.Add("Fueled Item");

        // Fuel Item — any item's NumericConfigKey is referenced by a sink in another chain
        var myConfigKeys = chain.Items
            .Where(i => !string.IsNullOrEmpty(i.NumericConfigKey))
            .Select(i => i.NumericConfigKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (myConfigKeys.Count > 0)
        {
            bool isFuel = allChains
                .Where(c => c != chain)
                .SelectMany(c => c.Items)
                .Where(i => i.IsSink && !IsSinkSuppressed(i) && i.SinkRequirementConfigKeys != null)
                .SelectMany(i => i.SinkRequirementConfigKeys!)
                .Any(req => myConfigKeys.Contains(req));
            if (isFuel) types.Add("Fuel Item");
        }

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

            foreach (var item in otherChain.Items)
            {
                // Drop/Spawn sources
                bool drops = (item.DropOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                             (item.SpawnOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                             (item.SpawnItemType != null && myItemTypes.Contains(item.SpawnItemType));

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
        // Total distinct levels in source chain (aliases counted transparently)
        var totalLevels = sourceChain.Items
            .Select(i => ResolveLevel(i.ItemType, i))
            .Where(lvl => lvl > 0)
            .Distinct()
            .Count();

        // fullName=true preserves "(...)" suffix — only needed for non-event chains with disambiguation
        bool needsFullName = !sourceChain.IsEventChain && HasParentheticalSuffix(sourceName);
        string fullNameSuffix = needsFullName ? "|fullName=true" : "";

        // All levels produce OR single-level chain OR no valid producing levels → no level
        if (producingLevels.Count == 0 || totalLevels <= 1 || producingLevels.Count == totalLevels)
            return $"{{{{Item/Group|{sourceName}{fullNameSuffix}}}}}";

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
            int minLvl = levels.Min();
            int maxLvl = levels.Max();
            result.Add(minLvl == maxLvl
                ? $"{{{{Item/Group|{chainName}|{minLvl}}}}}"
                : $"{{{{Item/Group|{chainName}|{minLvl}|min={minLvl}|max={maxLvl}}}}}");
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

        // Deduplicate by (DisplayName, level) to avoid alias duplicates
        var seen = new HashSet<(string Name, int Level)>();
        var parts = new List<string>();
        var unmatchedNames = new List<string>();

        foreach (var item in chain.Items)
        {
            // Collect all decay target item types from this item
            var decayTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(item.DecayIntoItemType))
                decayTypes.Add(item.DecayIntoItemType);
            if (!string.IsNullOrEmpty(item.SpawnDecayIntoItemType))
                decayTypes.Add(item.SpawnDecayIntoItemType);
            if (!string.IsNullOrEmpty(item.DecayAfterLastCycleItemType))
                decayTypes.Add(item.DecayAfterLastCycleItemType);

            // DecayAfterLastCycleOdds — multiple decay targets with probabilities
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

                    if (seen.Add((chainName, lvl)))
                        parts.Add($"{{{{Item|{chainName}|{lvl}}}}}");
                }
                else if (itemNames.TryGetValue(decayType, out var name))
                {
                    if (!unmatchedNames.Contains(name))
                        unmatchedNames.Add(name);
                }
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
            if (!item.IsSink || string.IsNullOrEmpty(item.SinkRewardItemType) || IsSinkSuppressed(item)) continue;

            if (itemTypeToChain.TryGetValue(item.SinkRewardItemType, out var rewardChain))
            {
                var rewardItem = rewardChain.Items
                    .FirstOrDefault(i => string.Equals(i.ItemType, item.SinkRewardItemType, StringComparison.OrdinalIgnoreCase));
                int lvl = ResolveLevel(item.SinkRewardItemType, rewardItem);
                var chainName = ResolveChainName(rewardChain, item.SinkRewardItemType);

                if (!seen.Add((chainName, lvl))) continue;
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

        // Collect per chain: actual item levels + amounts
        var byChain = new Dictionary<string, (ParsedChain Chain, List<(int Level, int Amount)> Items)>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in chain.Items)
        {
            if (!item.IsSink || item.SinkRequirementConfigKeys == null || IsSinkSuppressed(item)) continue;

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
                        entry = (match.Chain, new List<(int, int)>());
                        byChain[match.Chain.ConfigKey] = entry;
                    }
                    entry.Items.Add((level, amount));
                }
            }
        }

        var result = new List<string>();

        foreach (var (_, (needChain, items)) in byChain)
        {
            var chainName = ResolveChainName(needChain, needChain.Items.FirstOrDefault()?.ItemType);
            foreach (var (lvl, amt) in items.OrderBy(x => x.Level))
            {
                var template = $"{{{{Item|{chainName}|{lvl}}}}}";
                result.Add(amt > 1 ? $"{amt}x {template}" : template);
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
