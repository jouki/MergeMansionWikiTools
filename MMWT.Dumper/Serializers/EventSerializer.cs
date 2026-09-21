using Code.GameLogic.GameEvents;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// <para>
/// Owns every shape in <c>events.json</c> that plain reflection cannot produce. Three different
/// writing styles live here, and which one an event type uses is decided by golden, not by taste:
/// </para>
/// <list type="bullet">
/// <item><description><b>TagId member dump</b> (<see cref="MetaObjectWriter"/>) with a small hook —
/// collectible boards, leaderboards, progressions and garage cleanups. Their golden key order is
/// TagId order, which for three of the four differs from declaration order (a leaderboard's
/// <c>PortalItemDef</c> is tag 7 but the last line of the class), so Newtonsoft's own reflection
/// cannot reproduce it. The hook drops the raw <c>DisplayName</c> and the writer's caller prepends
/// the localized <c>Name</c>.</description></item>
/// <item><description><b>Newtonsoft's own contract, replayed</b> — core-support events only. Their
/// golden contains PUBLIC NON-MetaMember properties (<c>ActivableId</c>, <c>MinigameIdOption</c>,
/// <c>GroupIdOption</c> …) and omits the PRIVATE MetaMembers (<c>AssetOverride</c>,
/// <c>LocOverride</c>, <c>PortalItemDef</c>), i.e. exactly Newtonsoft's default property set in
/// declaration order — with <c>DisplayName</c> replaced in place by the resolved
/// <c>Name</c>.</description></item>
/// <item><description><b>Hand-built <see cref="OrderedMap"/>s</b> — the categories whose golden key
/// order matches neither (progression pack events/packs, card-collection supporting events, Boulton
/// League events and stages, solo-milestone events, all three Mix a Booster categories). These also
/// keep explicit nulls, which the two reflection paths drop.</description></item>
/// </list>
/// <para>
/// Everything else — daily tasks, event levels, the whole Daily Scoop V1 family, solo-milestone
/// milestones, activable params, schedules, category info — is deliberately NOT claimed: their
/// declaration order already equals their TagId order, so default reflection is byte-correct and
/// claiming them would only risk drift. Daily Challenges has its own converter
/// (<see cref="DailyChallengesSerializer"/>) because of its private members.
/// </para>
/// </summary>
public sealed partial class EventSerializer : JsonConverter
{
    private readonly SharedGameConfig _config;
    private readonly IDumpLog _log;

    public EventSerializer(SharedGameConfig config, IDumpLog log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? ConsoleDumpLog.Instance;
    }

    /// <summary>See the hook in <see cref="WriteNamedEvent"/>: the single reference member golden
    /// keeps even with an empty key.</summary>
    private const string AlwaysWrittenReference = nameof(CollectibleBoardEventInfo.EventInitTask);

    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) =>
        typeof(CollectibleBoardEventInfo).IsAssignableFrom(objectType)
        || typeof(LeaderboardEventInfo).IsAssignableFrom(objectType)
        || typeof(ProgressionEventInfo).IsAssignableFrom(objectType)
        || typeof(GarageCleanupEventInfo).IsAssignableFrom(objectType)
        || typeof(CoreSupportEventInfo).IsAssignableFrom(objectType)
        || typeof(GarageCleanupPatternInfo).IsAssignableFrom(objectType)
        || typeof(GarageCleanupBoardInfo).IsAssignableFrom(objectType)
        || typeof(GarageCleanupRewardInfo).IsAssignableFrom(objectType)
        || typeof(GameLogic.DailyTaskDefinition).IsAssignableFrom(objectType)
        // Plain TagId member dumps: these carry public non-[MetaMember] properties that golden does
        // NOT have (EventLevelInfo's ConfigKey shorthand), so default reflection would over-write
        // them. Their sibling DailyTaskV2Info, by contrast, DOES show its ConfigKey in golden and is
        // therefore deliberately left to default reflection.
        || typeof(EventLevelInfo).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        switch (value)
        {
            case null: writer.WriteNull(); return;
            case CollectibleBoardEventInfo board: WriteNamedEvent(writer, board, serializer, ResolveNameFromLocId(board.NameLocId, board.DisplayName)); return;
            case LeaderboardEventInfo lb: WriteNamedEvent(writer, lb, serializer, ResolveNameFromLocId(lb.NameLocId, lb.DisplayName)); return;
            case ProgressionEventInfo progression: WriteProgression(writer, progression, serializer); return;
            case GarageCleanupEventInfo gc: WriteGarageCleanup(writer, gc, serializer); return;
            case CoreSupportEventInfo cse: WriteCoreSupportEvent(writer, cse, serializer); return;
            case GarageCleanupPatternInfo pattern: WritePattern(writer, pattern, serializer); return;
            case GarageCleanupBoardInfo board: WriteBoard(writer, board, serializer); return;
            case GarageCleanupRewardInfo reward: WriteReward(writer, reward, serializer); return;
            case GameLogic.DailyTaskDefinition task: WriteDailyTask(writer, task, serializer); return;
            default: MetaObjectWriter.WriteObject(writer, value, serializer, _log); return;
        }
    }

    /// <summary>
    /// The shared "MetaEventSerializer" shape: a computed <c>Name</c> first, then the type's
    /// <c>[MetaMember]</c>s in TagId order with the raw <c>DisplayName</c> stub suppressed.
    /// </summary>
    private void WriteNamedEvent(JsonWriter w, object evt, JsonSerializer s, string? name)
    {
        w.WriteStartObject();
        if (name != null)
        {
            w.WritePropertyName("Name");
            w.WriteValue(name);
        }
        MetaObjectWriter.WriteMembers(w, evt, s, _log, (member, value) =>
        {
            // The raw stub title never reaches the dump; the localized Name above replaces it.
            if (member == "DisplayName") return true;
            // A collectible board's init task is the one reference golden keeps even when its key is
            // empty — all 46 boards carry the member, 40 of them as null — while every OTHER
            // empty-key reference on the same object disappears (ActiveDecorationRef on 34 boards,
            // StartCutsceneRef on 45, StartDialogueRef on 29, ExtensionInAppProduct on 16). A probe
            // of the runtime state (--probe-meta-members) shows all of them in the same state, so
            // this is a per-member rule, not a value rule. Writing it here, ahead of the reference
            // rule below, is what keeps the member.
            // The decoration a board upgrades: written as usual, then followed by the
            // resolved detail the raw key cannot carry (see EventSerializer.Decorations.cs).
            // Only when the key is non-empty, so the MetaRefSkip.EmptyKey rule below keeps
            // deciding which boards show the member at all.
            if (member == nameof(CollectibleBoardEventInfo.ActiveDecoration) + "Ref")
            {
                var detail = ResolveActiveDecoration(evt, value!);
                if (detail == null) return false;
                w.WritePropertyName(member);
                s.Serialize(w, value);
                WriteActiveDecoration(w, detail);
                return true;
            }
            if (member != AlwaysWrittenReference) return false;
            w.WritePropertyName(member);
            s.Serialize(w, value);
            return true;
        }, MetaRefSkip.EmptyKey);
        w.WriteEndObject();
    }

    /// <summary>
    /// Core-support events, written through Newtonsoft's own contract for the type so the property
    /// set and order are exactly what default reflection would produce — with <c>DisplayName</c>
    /// swapped for the resolved title under the name <c>Name</c>, in place.
    /// </summary>
    private void WriteCoreSupportEvent(JsonWriter w, CoreSupportEventInfo evt, JsonSerializer s)
    {
        var name = ResolveCoreSupportEventName(evt);

        ContractObjectWriter.WriteObject(w, evt, s, _log, (member, _) =>
        {
            if (member != nameof(CoreSupportEventInfo.DisplayName)) return false;
            w.WritePropertyName("Name");
            if (name is null) w.WriteNull(); else w.WriteValue(name);
            return true;
        });
    }

    /// <summary>
    /// Daily tasks. The plain member dump, except that the two <c>ItemDef</c> members are written as
    /// their raw item id: golden has <c>"RequiredItemDef": 1712</c> where every other
    /// <c>ItemDef</c> in the file (and every one in <c>areas.json</c>) is the resolved item type
    /// string. The shared <see cref="ConfigDefinitionConverter"/> has to stay registered — the
    /// reward payloads around these tasks depend on it — so the exception is made here.
    /// </summary>
    private void WriteDailyTask(JsonWriter w, GameLogic.DailyTaskDefinition task, JsonSerializer s)
    {
        w.WriteStartObject();
        MetaObjectWriter.WriteMembers(w, task, s, _log, (member, value) =>
        {
            if (value is not GameLogic.Config.ItemDef item) return false;
            w.WritePropertyName(member);
            w.WriteValue(item.ConfigKey);
            return true;
        });
        w.WriteEndObject();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(EventSerializer)} is write-only (dumper never deserializes).");
}
