using GameLogic.CardCollection;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// The one shape in <c>card_collection.json</c> that neither reflection path produces on its own:
/// <see cref="CardCollectionEvidenceBoxInfo"/>.
/// <para>
/// Its golden object is plain Newtonsoft reflection — <c>ConfigKey, SlotIndex, Quality, RefreshTime,
/// SegmentPriority, Segment, Rewards, AssetPackId, ItemDef</c>, i.e. the public properties in
/// declaration order with the three PRIVATE <c>[MetaMember]</c>s (<c>CurrencyPricePairs</c>,
/// <c>SkipCurrencyPricePairs</c>, <c>IsSkipPriceDynamic</c>) absent — except for the last member:
/// <c>ItemDef</c> expands to the FULL item definition instead of the item type string the shared
/// <see cref="ConfigDefinitionConverter"/> writes everywhere else.
/// </para>
/// <para>
/// That exception cannot be a converter on <c>ItemDef</c> itself, because the same file's reward
/// payloads need the string form (<c>PocketConversionReward.RewardItem.ItemDef</c> is
/// <c>"BronzeCoin_01"</c>, 17 of them, plus 91 in <c>CardSets[].Rewards</c> and 66 across
/// <c>Events[].Rewards</c>/<c>PrestigeRewards</c>) — the same per-member, not per-file, split
/// <c>events.json</c> hit with its daily tasks. The other two
/// expanded <c>ItemDef</c>s in this file (<c>Cards[]</c>, <c>Packs[]</c>) are built by
/// <c>CardCollectionDumper</c> itself and call <see cref="Expand"/> directly.
/// </para>
/// </summary>
public sealed class CardCollectionSerializer : JsonConverter
{
    private readonly SharedGameConfig _config;
    private readonly IDumpLog _log;

    public CardCollectionSerializer(SharedGameConfig config, IDumpLog log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? ConsoleDumpLog.Instance;
    }

    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) =>
        typeof(CardCollectionEvidenceBoxInfo).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter w, object? value, JsonSerializer s)
    {
        if (value is not CardCollectionEvidenceBoxInfo box) { w.WriteNull(); return; }

        ContractObjectWriter.WriteObject(w, box, s, _log, (member, memberValue) =>
        {
            if (member != nameof(CardCollectionEvidenceBoxInfo.ItemDef) || memberValue is not ItemDef itemDef) return false;
            w.WritePropertyName(member);
            s.Serialize(w, Expand(_config, itemDef));
            return true;
        });
    }

    /// <summary>
    /// Resolves an <see cref="ItemDef"/> to the <c>ItemDefinition</c> behind it, so that serializing
    /// the result goes through <see cref="ChainSerializer"/> and produces the same full item object
    /// <c>chain_item_odds.json</c> writes.
    /// <para>
    /// "No item" is <b>an empty object</b>, not <c>null</c> and not a placeholder item — the same
    /// rule <c>areas.json</c> needs for <c>HotspotId.None</c>, and for the same reason: the
    /// <c>Items</c> library has a real row at key 0 whose <c>ItemType</c> is literally <c>"None"</c>,
    /// so a plain <c>TryGetValue</c> succeeds and would write a full (meaningless) item. Golden is
    /// emphatic: 1 395 of the 1 397 cards carry <c>"ItemDef": {}</c>, because a collectible card is
    /// not a board item — only the two wild cards, the 17 envelopes and the 6 evidence boxes are.
    /// </para>
    /// <para>
    /// A <c>null</c> <paramref name="itemDef"/> gives <c>null</c>, which the two callers then treat
    /// the way their surrounding shape does: the dumper's plain dictionary entry writes
    /// <c>"ItemDef": null</c>, while the evidence-box hook declines the member and lets the normal
    /// null-skip drop it. No card, pack or box in the corpus has a null <c>ItemDef</c>, so neither
    /// branch is golden-verified — they just each match what that path does for every other member.
    /// </para>
    /// </summary>
    public static object? Expand(SharedGameConfig? config, ItemDef? itemDef)
    {
        if (itemDef == null) return null;
        if (itemDef.ConfigKey != NoItemKey && config?.Items != null
            && config.Items.TryGetValue(itemDef.ConfigKey, out var item) && item != null)
            return item;
        return NoItem();
    }

    /// <summary>
    /// The key of the <c>Items</c> library's blank placeholder row, i.e. "this reference points
    /// nowhere". The row exists and resolves, so the key has to be tested before the lookup.
    /// </summary>
    private const int NoItemKey = 0;

    /// <summary>
    /// A fresh empty object, which serializes as <c>{}</c> — see <see cref="Expand"/>. Deliberately a
    /// new instance per call rather than a shared static: the value is handed to a serializer and
    /// ends up in caller-owned structures, and a shared mutable dictionary escaping into 1 395 card
    /// entries is an aliasing bug waiting to happen.
    /// </summary>
    private static Dictionary<string, object?> NoItem() => new();

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(CardCollectionSerializer)} is write-only (dumper never deserializes).");
}
