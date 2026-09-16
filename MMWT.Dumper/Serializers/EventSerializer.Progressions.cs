using Code.GameLogic.GameEvents;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// Progression ("Season Pass") events. Their golden object is the TagId member dump with four
/// changes, none of which reflection can make on its own: a leading localized <c>Name</c>, the raw
/// <c>DisplayName</c> dropped, two computed members inserted (<c>EventItemName</c> right after
/// <c>EventItem</c>, and the resolved perk tables before <c>FreeEventLevelRefs</c>) and a small
/// re-ordering of the head of the object.
/// </summary>
public sealed partial class EventSerializer
{
    /// <summary>
    /// The members golden hoists to the front, in golden order. TagId order would give
    /// <c>… Description, ChancesToSpawnEventItemPerLevel, EventItem, ActivableParams,
    /// PremiumIAPOfferMinLevel, IntroDialogue …</c>; golden reads
    /// <c>… Description, ActivableParams, EventItem, EventItemName, PremiumIAPOfferMinLevel,
    /// IntroDialogue, ChancesToSpawnEventItemPerLevel, Track1PerkData, Track2PerkData,
    /// FreeEventLevelRefs …</c>, after which TagId order resumes.
    /// </summary>
    private static readonly string[] ProgressionHead =
    {
        nameof(ProgressionEventInfo.ProgressionEventId),
        nameof(ProgressionEventInfo.NameLocId),
        nameof(ProgressionEventInfo.Description),
        nameof(ProgressionEventInfo.ActivableParams),
        nameof(ProgressionEventInfo.EventItem),
        "EventItemName",
        nameof(ProgressionEventInfo.PremiumIAPOfferMinLevel),
        nameof(ProgressionEventInfo.IntroDialogue),
        nameof(ProgressionEventInfo.ChancesToSpawnEventItemPerLevel),
        "Track1PerkData",
        "Track2PerkData",
        "FreeEventLevelRefs", // private member, so no nameof
    };

    private void WriteProgression(JsonWriter w, ProgressionEventInfo evt, JsonSerializer s)
    {
        // Collect the reflected members first so the head can be emitted in golden order and the
        // tail can then continue in TagId order without re-writing anything.
        var members = new List<(string Name, object? Value)>();
        foreach (var (name, _, get) in MetaObjectWriter.Members(evt))
        {
            if (name == nameof(ProgressionEventInfo.DisplayName)) continue;
            object? value;
            try { value = get(); }
            catch (Exception ex)
            {
                _log.Warn($"ProgressionEventInfo.{name}: {ex.GetType().Name} — {ex.Message} — skipped");
                continue;
            }
            if (value is null) continue;
            members.Add((name, value));
        }

        members.Add(("EventItemName", ResolveEventItemChainName(evt.EventItem)));
        members.Add(("Track1PerkData", PerkTable(evt.Track1IAPPerkRefs)));
        members.Add(("Track2PerkData", PerkTable(evt.Track2IAPPerkRefs)));

        w.WriteStartObject();
        w.WritePropertyName("Name");
        w.WriteValue(ResolveNameFromLocId(evt.NameLocId, evt.DisplayName));

        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var head in ProgressionHead)
        {
            var index = members.FindIndex(m => m.Name == head);
            if (index < 0) continue; // member absent (null) — the rest keeps its order regardless
            WriteMember(w, s, members[index]);
            written.Add(head);
        }
        foreach (var member in members)
        {
            if (written.Contains(member.Name)) continue;
            WriteMember(w, s, member);
        }
        w.WriteEndObject();
    }

    private static void WriteMember(JsonWriter w, JsonSerializer s, (string Name, object? Value) member)
    {
        if (member.Value is null) return;
        w.WritePropertyName(member.Name);
        s.Serialize(w, member.Value);
    }

    /// <summary>
    /// Resolves a track's perk references into the golden <c>{perkId: {Type, …}}</c> table.
    /// Returns null (so the member is dropped) when the track has no perks at all.
    /// </summary>
    private OrderedMap? PerkTable(IEnumerable<Metaplay.Core.MetaRef<ProgressionEventPerkInfo>>? refs)
    {
        if (refs == null) return null;
        var table = new OrderedMap();
        foreach (var reference in refs)
        {
            if (reference is not { IsResolved: true }) continue;
            var info = reference.Ref;
            if (info?.Perk == null || info.Id?.Value == null) continue;
            table.Add(info.Id.Value, PerkData(info.Perk));
        }
        return table.Count == 0 ? null : table;
    }
}
