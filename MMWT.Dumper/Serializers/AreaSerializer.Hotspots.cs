using Code.GameLogic.Hotspots;
using Game.Cloud.Config;
using GameLogic;
using GameLogic.Config;
using GameLogic.Hotspots;
using GameLogic.Hotspots.CardStack;
using GameLogic.Player.Director.Config;
using GameLogic.Player.Requirements;
using MergeMansionWikiTools.Dumper.Core;
using Metaplay.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

public sealed partial class AreaSerializer
{
    // ── hotspot ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A hotspot is up to seven computed members followed by <see cref="HotspotDefinition"/>'s own
    /// <c>[MetaMember]</c>s in TagId order. The computed prefix, in golden order:
    /// <list type="number">
    /// <item><description><c>Description</c> — the localized task text, omitted entirely when the
    /// language has no entry (53 hotspots in golden: every <c>UnlockArea</c> marker plus a handful
    /// of placeholders).</description></item>
    /// <item><description>the per-task event-token values (<c>DigEventTaps</c>,
    /// <c>QuaternaryEnergy</c>, <c>ClassicRacesSailPoints</c>, <c>RollTheDiceToken</c>,
    /// <c>BuilderEventToken</c> — whatever the config defines, in its own order), summed over the
    /// task's requirement items via <see cref="ExtraSpawnHelper"/>. All five appear together on
    /// 9 617 of 9 714 hotspots; the 97 that have none are exactly the ones with no item requirement
    /// (area unlocks, card-stack and illustration minigames, the eight coin-cost tasks).</description></item>
    /// <item><description><c>Theme</c> — the minigame skin, present on the 107 minigame tasks
    /// only.</description></item>
    /// </list>
    /// </summary>
    private void WriteHotspot(JsonWriter w, HotspotDefinition def, JsonSerializer s)
    {
        w.WriteStartObject();

        var description = ResolveDescription(def);
        if (description != null)
        {
            w.WritePropertyName("Description");
            w.WriteValue(description);
        }

        foreach (var (key, amount) in ExtraSpawnHelper.SumRequirementValues(
                     _config, def.RequirementsList ?? Enumerable.Empty<PlayerRequirement>()))
        {
            w.WritePropertyName(key);
            w.WriteValue(amount);
        }

        var theme = ResolveTheme(def);
        if (theme != null)
        {
            w.WritePropertyName("Theme");
            w.WriteValue(theme);
        }

        MetaObjectWriter.WriteMembers(w, def, s, _log, (name, value) =>
        {
            if (TryWriteAreaReference(w, name, value, s)) return true;
            switch (name)
            {
                case "CompleteFocusHotspotRef":
                    w.WritePropertyName(name);
                    WriteResolvedHotspot(w, value as HotspotDef, s);
                    return true;

                case "CardStackRef":
                {
                    // Always written, like the area references above — golden carries
                    // "CardStackRef": null on the 9 700 tasks that are not card minigames. A
                    // resolvable stack is inlined as { Cards: [{ ItemDef: { ItemType }, Row }] } (the
                    // shape the app's AreasService parses); anything else falls back to the plain
                    // reference, i.e. the bare key or null.
                    w.WritePropertyName(name);
                    var stack = ResolveCardStack(value as IMetaRef);
                    if (stack == null) s.Serialize(w, value);
                    else WriteCardStack(w, stack);
                    return true;
                }

                default:
                    return false;
            }
        }, MetaRefSkip.Unresolved);

        w.WriteEndObject();
    }

    /// <summary>
    /// Serializes the resolved <see cref="CardStackInfo"/> down to what a minigame requirement
    /// actually needs: the card grid rows and each card's item type. The stack's own
    /// <c>Width</c>/<c>Height</c>/<c>Style</c>/<c>Theme</c> members are not part of this shape
    /// (<c>Theme</c> is hoisted to the hotspot prefix instead), and neither is a card's
    /// <c>Column</c>.
    /// </summary>
    private void WriteCardStack(JsonWriter w, CardStackInfo stack)
    {
        w.WriteStartObject();
        w.WritePropertyName("Cards");
        w.WriteStartArray();
        foreach (var card in stack.Cards ?? new List<PlayCard>())
        {
            w.WriteStartObject();
            w.WritePropertyName("ItemDef");
            w.WriteStartObject();
            w.WritePropertyName("ItemType");
            w.WriteValue(ResolveItemType(card.ItemDef));
            w.WriteEndObject();
            w.WritePropertyName("Row");
            w.WriteValue(card.Row);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    // ── description ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The task's localized description: the per-Id key first (<c>LocMan</c> tries both
    /// <c>HotspotDescription_&lt;Id&gt;</c> and the bare <c>&lt;Id&gt;</c>), then the multistep
    /// group fallback. Returns null when nothing resolves, which drops the member.
    /// <para>
    /// The hotspot's own <c>DescriptionLocalizationId</c> member is deliberately NOT consulted:
    /// 108 hotspots carry one (<c>HotspotDescriptionOverride_*</c> keys with different wording) and
    /// golden shows the per-Id text for every single one of them.
    /// </para>
    /// </summary>
    private string? ResolveDescription(HotspotDefinition def)
    {
        if (LocMan.HasHotspotDescription(def.Id))
            return LocMan.GetHotspotDescription(def.Id);
        return ResolveMultistepDescription(def);
    }

    /// <summary>
    /// Fallback for RENAMED steps of a multistep task group. The game stores step texts under
    /// <c>HotspotDescription_&lt;MultistepGroupId&gt;_&lt;N&gt;</c> (final step: the bare
    /// <c>HotspotDescription_&lt;MultistepGroupId&gt;</c>). Usually a step's own Id already equals
    /// <c>&lt;GroupId&gt;_&lt;N&gt;</c> so the per-Id lookup finds it, but a step renamed after the
    /// group was authored has no key of its own — the documented case is
    /// <c>FirstFloorHallwayReadingNookFixRightChair</c> (26.05.01), step 1 of the
    /// <c>...PlaceMirror</c> group, whose "Place hook" text lives under
    /// <c>HotspotDescription_FirstFloorHallwayReadingNookPlaceMirror_1</c>. The step number is the
    /// hotspot's position in the group's own unlock chain.
    /// <para>
    /// Not exercised anywhere in the current 108-file corpus — the 26.06/26.07 language file has a
    /// bare-Id key for every hotspot that has a description at all, and all 53 description-less
    /// hotspots have no <c>MultistepGroupId</c> — so this path is implemented to spec
    /// (<c>_CONTEXT/Dumper/GameDataDumper.md</c>, 2026-06-11) rather than fitted to golden bytes.
    /// </para>
    /// </summary>
    private string? ResolveMultistepDescription(HotspotDefinition def)
    {
        var group = def.MultistepGroupId?.Value;
        if (string.IsNullOrEmpty(group)) return null;

        var chain = GetMultistepChain(group);
        var position = chain.IndexOf(def.Id);
        if (position < 0) return null;

        var key = position == chain.Count - 1
            ? $"HotspotDescription_{group}"
            : $"HotspotDescription_{group}_{position + 1}";
        return LocMan.TryGet(key, out var translation) ? translation : null;
    }

    private readonly Dictionary<string, List<HotspotId>> _multistepChains = new(StringComparer.Ordinal);

    /// <summary>
    /// The hotspots of one multistep group, ordered along their intra-group unlock chain: the
    /// member nobody else in the group unlocks comes first, then whoever names it as a parent, and
    /// so on. Members the chain cannot reach (a broken group) are appended in library order so the
    /// result still contains every member exactly once.
    /// </summary>
    private List<HotspotId> GetMultistepChain(string group)
    {
        if (_multistepChains.TryGetValue(group, out var cached)) return cached;

        var members = new List<HotspotDefinition>();
        if (_config.HotspotDefinitions != null)
            foreach (var kv in _config.HotspotDefinitions.EnumerateAll())
                if (kv.Value is HotspotDefinition hotspot && string.Equals(hotspot.MultistepGroupId?.Value, group, StringComparison.Ordinal))
                    members.Add(hotspot);

        var ids = members.Select(m => m.Id).ToHashSet();
        var childOf = new Dictionary<HotspotId, HotspotId>();
        foreach (var member in members)
            foreach (var parent in member.UnlockingParentRefs ?? new List<HotspotDef>())
                if (parent != null && ids.Contains(parent.ConfigKey) && !childOf.ContainsKey(parent.ConfigKey))
                    childOf[parent.ConfigKey] = member.Id;

        var hasInGroupParent = new HashSet<HotspotId>(childOf.Values);
        var ordered = new List<HotspotId>();
        var seen = new HashSet<HotspotId>();
        foreach (var head in members.Select(m => m.Id).Where(id => !hasInGroupParent.Contains(id)))
        {
            var current = head;
            while (seen.Add(current))
            {
                ordered.Add(current);
                if (!childOf.TryGetValue(current, out var next)) break;
                current = next;
            }
        }
        foreach (var member in members)
            if (seen.Add(member.Id))
                ordered.Add(member.Id);

        _multistepChains[group] = ordered;
        return ordered;
    }

    // ── theme / card stack / items ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The minigame skin shown on the task. Two sources, both present in golden: a card-stack task
    /// takes it from its <see cref="CardStackInfo"/> (14 tasks: Card / Book / SpyNotes), an
    /// illustration task from its custom hotspot table (93 tasks: Painting / Dollhouse /
    /// Perfumery). The 90 illustration CHILDREN reach the table through their
    /// <c>CustomHotspotTableId</c> member, the 3 illustration PARENTS through
    /// <c>CustomHotspotTableInfoRef</c> instead — both have to be tried.
    /// </summary>
    private string? ResolveTheme(HotspotDefinition def)
    {
        var stack = ResolveCardStack(MetaObjectWriter.GetMember(def, "CardStackRef", _log) as IMetaRef);
        if (stack?.Theme != null) return stack.Theme;

        if (_config.CustomTables == null) return null;

        if (MetaObjectWriter.GetMember(def, "CustomHotspotTableId", _log) is CustomHotspotTableId tableId && tableId.Value != null
            && _config.CustomTables.TryGetValue(tableId, out var byId))
            return byId?.Theme;

        if (MetaObjectWriter.GetMember(def, "CustomHotspotTableInfoRef", _log) is IMetaRef tableRef
            && tableRef.KeyObject is CustomHotspotTableId refKey && refKey.Value != null
            && _config.CustomTables.TryGetValue(refKey, out var byRef))
            return byRef?.Theme;

        return null;
    }

    /// <summary>
    /// Resolves a <c>MetaRef&lt;CardStackInfo&gt;</c> through <c>SharedGameConfig.CardStacks</c>
    /// via its key rather than <c>MetaRef.Ref</c>, which throws on an unresolved reference.
    /// </summary>
    private CardStackInfo? ResolveCardStack(IMetaRef? reference)
    {
        if (reference?.KeyObject is not CardStackId key || key.Value == null) return null;
        if (_config.CardStacks == null) return null;
        return _config.CardStacks.TryGetValue(key, out var info) ? info : null;
    }

    /// <summary>Item type behind an <see cref="ItemDef"/> key, or null when the config has no such item.</summary>
    private string? ResolveItemType(ItemDef? def)
    {
        if (def is null || _config.Items == null) return null;
        return _config.Items.TryGetValue(def.ConfigKey, out var item) ? item?.ItemType : null;
    }
}
