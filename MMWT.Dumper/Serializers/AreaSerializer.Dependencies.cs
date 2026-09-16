using System.Globalization;
using System.Text;
using Game.Cloud.Config;
using GameLogic.Config;
using GameLogic;
using GameLogic.Config.Costs;
using GameLogic.Hotspots;
using GameLogic.Player.Requirements;
using MergeMansionWikiTools.Dumper.Core;

namespace MergeMansionWikiTools.Dumper.Serializers;

public sealed partial class AreaSerializer
{
    private const string GraphHeader = "digraph{rankdir=\"TB\";node[shape=box];";

    /// <summary>
    /// Renders the area's task unlock graph as a one-line Graphviz document, stored as a plain
    /// string in the dump:
    /// <code>
    /// digraph{rankdir="TB";node[shape=box];0[label="&lt;Id&gt;\r\n&lt;Description&gt;&lt;requirements&gt;"];…;0-&gt;1;1-&gt;2;…;}
    /// </code>
    /// <para>
    /// <b>Node numbering</b> (verified against all 75 areas of 26.07.01, nodes and edges both):
    /// walk the area's tasks in ascending <see cref="HotspotId"/> ENUM VALUE — not the order they
    /// are listed in the area, which is effectively shuffled — and for each task first register its
    /// unlock parents (in their own order), then the task itself. A node's number is the order it
    /// was first registered in. That is why the numbering is neither the area's task order nor a
    /// topological one: node 0 of The Grand Drive is <c>MansionStairsRemoveTrash</c>, the parent of
    /// the lowest-numbered task, and it is a CHILD of node 6.
    /// </para>
    /// <para>
    /// Parents that live in another area (or that resolve to nothing) become nodes too — that is
    /// how Mansion Right Filler ends up with 50 nodes for 49 tasks.
    /// </para>
    /// <para>
    /// <b>Edges</b> are parent-&gt;child, emitted grouped by source node in node order, each
    /// source's targets in the order the edges were created.
    /// </para>
    /// <para>
    /// <b>Labels</b> are <c>&lt;hotspot name&gt;</c>, CRLF, the task description, then one
    /// unbroken run of requirement texts: <c>&lt;item name&gt; x&lt;count&gt;</c> per required item
    /// and <c>CRLF&lt;currency&gt; x&lt;amount&gt;</c> per coin cost (the leading CRLF is the
    /// currency line's own, so a cost task's label has three lines). SEPARATE requirements run
    /// together with no separator; alternatives inside ONE requirement are the exception and get a
    /// CRLF between them (see <see cref="AppendRequirementText"/>). Requirement kinds with nothing
    /// to spend — card stacks, illustrations — contribute nothing. Double quotes in a description
    /// are rewritten as apostrophes so the label needs no escaping (7 hotspots in golden).
    /// </para>
    /// </summary>
    private string BuildTaskDependencies(GameLogic.Area.AreaInfo area)
    {
        var index = new Dictionary<HotspotId, int>();
        var nodes = new List<HotspotId>();
        var adjacency = new List<List<int>>();

        int Register(HotspotId id)
        {
            if (index.TryGetValue(id, out var existing)) return existing;
            var assigned = nodes.Count;
            index[id] = assigned;
            nodes.Add(id);
            adjacency.Add(new List<int>());
            return assigned;
        }

        foreach (var hotspotRef in (area.HotspotsRefs ?? new List<HotspotDef>())
                     .Where(h => h != null)
                     .OrderBy(h => (int)h.ConfigKey))
        {
            var parents = ResolveHotspot(hotspotRef)?.UnlockingParentRefs ?? new List<HotspotDef>();
            foreach (var parent in parents)
                if (parent != null)
                    Register(parent.ConfigKey);

            var self = Register(hotspotRef.ConfigKey);

            foreach (var parent in parents)
            {
                if (parent == null) continue;
                var from = index[parent.ConfigKey];
                if (!adjacency[from].Contains(self)) adjacency[from].Add(self);
            }
        }

        var sb = new StringBuilder(GraphHeader);
        for (var i = 0; i < nodes.Count; i++)
        {
            sb.Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append("[label=\"");
            sb.Append(NodeLabel(nodes[i]));
            sb.Append("\"];");
        }
        for (var from = 0; from < adjacency.Count; from++)
            foreach (var to in adjacency[from])
                sb.Append(from.ToString(CultureInfo.InvariantCulture)).Append("->")
                  .Append(to.ToString(CultureInfo.InvariantCulture)).Append(';');
        sb.Append('}');
        return sb.ToString();
    }

    private string NodeLabel(HotspotId id)
    {
        var def = _config.HotspotDefinitions != null && id != HotspotId.None
                  && _config.HotspotDefinitions.TryGetValue(id, out var found)
            ? found
            : null;

        var sb = new StringBuilder(HotspotIdNames.Resolve(id));
        sb.Append("\r\n");
        if (def != null)
        {
            sb.Append(ResolveDescription(def) ?? string.Empty);
            foreach (var requirement in def.RequirementsList ?? Enumerable.Empty<PlayerRequirement>())
                AppendRequirementText(sb, requirement);
        }
        return sb.Replace('"', '\'').ToString();
    }

    private void AppendRequirementText(StringBuilder sb, PlayerRequirement requirement)
    {
        switch (requirement)
        {
            case PlayerItemRequirement itemRequirement:
                var first = true;
                foreach (var itemDef in ItemRefsOf(itemRequirement))
                {
                    var itemType = ResolveItemType(itemDef);
                    if (itemType == null) continue;
                    // Alternatives WITHIN one requirement go on their own lines, while separate
                    // requirements just run together: the Rufus sign task reads
                    // "Edge strip x1Table Saw x1\r\nActive Table Saw x1" — two requirements, the
                    // second of which accepts either state of the circular saw station.
                    if (!first) sb.Append("\r\n");
                    first = false;
                    sb.Append(Loc.SafeLoc(() => LocMan.GetItemName(itemType), itemType, _log, itemType));
                    sb.Append(" x").Append(itemRequirement.Requirement.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case CostRequirement { RequiredCost: CurrencyCost cost }:
                sb.Append("\r\n").Append(cost.Currency)
                  .Append(" x").Append(cost.CurrencyAmount.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>The requirement's private <c>ItemRefs</c> list (empty when it has none).</summary>
    private IEnumerable<ItemDef> ItemRefsOf(PlayerItemRequirement requirement) =>
        MetaObjectWriter.GetMember(requirement, "ItemRefs", _log) as IEnumerable<ItemDef> ?? Enumerable.Empty<ItemDef>();
}
