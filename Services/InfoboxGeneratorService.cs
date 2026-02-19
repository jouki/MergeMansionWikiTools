using System.Text;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public class InfoboxGeneratorOptions
{
    public bool AddShop { get; set; }
    public bool IsStarter { get; set; }
    public bool IsPoints { get; set; }
    public bool IsDepleting { get; set; }   // manual override
}

public class InfoboxGeneratorService
{
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
        var sb = new StringBuilder();
        sb.AppendLine("{{Infobox Items");

        // ── title1 ──
        var baseName = StripParentheses(chain.DisplayName);
        if (baseName != chain.DisplayName)
            sb.AppendLine($"| title1 = {baseName}");

        // ── image1 (gallery) ──
        int maxLevel = chain.Items.Count > 0 ? chain.Items[^1].Level : 1;
        sb.AppendLine("| image1 =");
        sb.AppendLine("{{#tag:gallery|");
        sb.AppendLine($"  {{{{ItemNameToFilename|{{{{PAGENAME}}}}|1}}}}{{{{!}}}} Level 1");
        if (chain.Items.Count > 1)
            sb.AppendLine($"  {{{{ItemNameToFilename|{{{{PAGENAME}}}}|{{{{#Invoke:Items|GetItemMaxLevelFromChainName}}}}}}}}{{{{!}}}} Level {{{{#Invoke:Items|GetItemMaxLevelFromChainName}}}}");
        sb.AppendLine("}}");

        // ── type ──
        var types = BuildTypes(chain, allChains, opts);
        if (types.Count > 0)
            sb.AppendLine($"|type    = {string.Join("<br/>", types)}");

        // ── source ──
        var sourceLines = new List<string>(manualSourceLines);
        if (opts.AddShop)
            sourceLines.Add("{{Item/Group|Shop}}");
        if (sourceLines.Count > 0)
            sb.AppendLine($"|source  = {string.Join("<br/>", sourceLines)}");

        // ── drops ──
        var drops = BuildDrops(chain, allChains, itemNames);
        if (drops.Count > 0)
            sb.AppendLine($"|drops   = {string.Join("<br/>", drops)}");

        // ── decays_into ──
        var decaysInto = BuildDecaysInto(chain, allChains, itemNames);
        if (!string.IsNullOrEmpty(decaysInto))
            sb.AppendLine($"|decays_into = {decaysInto}");

        // ── used_in (fuel for sinks that accept this chain's items) ──
        var usedIn = BuildUsedIn(chain, allChains, itemNames);
        if (usedIn.Count > 0)
            sb.AppendLine($"|used_in = {string.Join("<br/>", usedIn)}");

        // ── needs (this item is a sink — what it requires) ──
        var needs = BuildNeeds(chain, allChains, itemNames);
        if (needs.Count > 0)
            sb.AppendLine($"|needs   = {string.Join("<br/>", needs)}");

        sb.Append("}}");
        return sb.ToString();
    }

    // ── Type detection ────────────────────────────────────────────────

    private static List<string> BuildTypes(
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

        if (chain.HasGenerators)     types.Add("Primary Source Item");
        if (chain.HasSpawners)       types.Add("Secondary Source Item");
        if (chain.IsEventChain)      types.Add("Event Item");
        if (opts.IsPoints)           types.Add("Points Item");
        if (opts.IsStarter)          types.Add("Starter Item");

        bool decaying = chain.Items.Any(i => i.HasDecay);
        if (decaying) types.Add("Decaying Item");

        bool depleting = opts.IsDepleting ||
            chain.Items.Any(i => i.DecaysWhenCyclesAreDone && i.SpawnHowManyCycles > 0);
        if (depleting) types.Add("Depleting Item");

        bool transformative = chain.Items.Any(i => i.IsSink);
        if (transformative) types.Add("Transformative Item");

        return types;
    }

    // ── Source lookup ─────────────────────────────────────────────────

    /// <summary>
    /// Finds all chains that produce items in this chain.
    /// Returns formatted wiki template strings.
    /// </summary>
    public List<string> BuildAutoSources(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        var myItemTypes = chain.Items.Select(i => i.ItemType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var otherChain in allChains)
        {
            if (otherChain == chain) continue;

            foreach (var item in otherChain.Items)
            {
                bool drops = (item.DropOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                             (item.SpawnOdds?.Keys.Any(k => myItemTypes.Contains(k)) == true) ||
                             (item.SpawnItemType != null && myItemTypes.Contains(item.SpawnItemType));

                if (!drops) continue;
                var key = $"{otherChain.ConfigKey}:{item.Level}";
                if (!seen.Add(key)) continue;

                // Use chain display name + level
                result.Add($"{{{{Item/Group|{otherChain.DisplayName}|{item.Level}}}}}");
                break; // one entry per source chain
            }
        }

        return result;
    }

    // ── Drops ─────────────────────────────────────────────────────────

    private static List<string> BuildDrops(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        // Build ItemType → chain lookup
        var itemTypeToChain = BuildItemTypeToChain(allChains);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

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
                    // Find min/max level of this item type within that chain
                    var matchingItems = droppedChain.Items
                        .Where(i => string.Equals(i.ItemType, droppedType, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    int minLvl = matchingItems.Count > 0 ? matchingItems.Min(i => i.Level) : 1;
                    int maxLvl = matchingItems.Count > 0 ? matchingItems.Max(i => i.Level) : 1;

                    result.Add(minLvl == maxLvl
                        ? $"{{{{Item/Group|{droppedChain.DisplayName}|{minLvl}}}}}"
                        : $"{{{{Item/Group|{droppedChain.DisplayName}|{minLvl}|{maxLvl}}}}}");
                }
                else if (itemNames.TryGetValue(droppedType, out var name))
                {
                    result.Add($"{{{{Item/nolevel|{name}}}}}");
                }
            }
        }

        return result;
    }

    // ── Decays Into ───────────────────────────────────────────────────

    private static string BuildDecaysInto(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        var itemTypeToChain = BuildItemTypeToChain(allChains);

        foreach (var item in chain.Items)
        {
            var decayType = item.DecayIntoItemType ?? item.SpawnDecayIntoItemType;
            if (string.IsNullOrEmpty(decayType)) continue;

            if (itemTypeToChain.TryGetValue(decayType, out var decayChain))
            {
                var matchingItem = decayChain.Items
                    .FirstOrDefault(i => string.Equals(i.ItemType, decayType, StringComparison.OrdinalIgnoreCase));
                int lvl = matchingItem?.Level ?? 1;
                return $"{{{{Item|{decayChain.DisplayName}|{lvl}}}}}";
            }
            if (itemNames.TryGetValue(decayType, out var name))
                return $"{{{{Item/nolevel|{name}}}}}";
        }
        return "";
    }

    // ── Used In (this chain's items are sink requirements elsewhere) ──

    private static List<string> BuildUsedIn(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        var myItemTypes = chain.Items.Select(i => i.ItemType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var itemTypeToChain = BuildItemTypeToChain(allChains);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var otherChain in allChains)
        {
            foreach (var item in otherChain.Items)
            {
                if (!item.IsSink || item.SinkRequirementItemTypes == null) continue;
                if (!item.SinkRequirementItemTypes.Any(r => myItemTypes.Contains(r))) continue;

                var key = otherChain.ConfigKey;
                if (!seen.Add(key)) continue;

                result.Add($"{{{{Item|{otherChain.DisplayName}|{item.Level}}}}}");
            }
        }

        return result;
    }

    // ── Needs (this item IS the sink) ─────────────────────────────────

    private static List<string> BuildNeeds(
        ParsedChain chain, IReadOnlyList<ParsedChain> allChains, Dictionary<string, string> itemNames)
    {
        var itemTypeToChain = BuildItemTypeToChain(allChains);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var item in chain.Items)
        {
            if (!item.IsSink || item.SinkRequirementItemTypes == null) continue;

            foreach (var reqType in item.SinkRequirementItemTypes)
            {
                if (!seen.Add(reqType)) continue;

                if (itemTypeToChain.TryGetValue(reqType, out var reqChain))
                {
                    var matchingItems = reqChain.Items
                        .Where(i => string.Equals(i.ItemType, reqType, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (chain.Items.Count > 1)
                    {
                        // Multi-level chain: use displayName= to show only level
                        int lvl = matchingItems.Count > 0 ? matchingItems[0].Level : 1;
                        result.Add($"{{{{Item|{reqChain.DisplayName}|{lvl}|displayName=}}}}");
                    }
                    else
                    {
                        int lvl = matchingItems.Count > 0 ? matchingItems[0].Level : 1;
                        result.Add($"{{{{Item|{reqChain.DisplayName}|{lvl}}}}}");
                    }
                }
                else if (itemNames.TryGetValue(reqType, out var name))
                {
                    result.Add($"{{{{Item/nolevel|{name}}}}}");
                }
            }
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────

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
