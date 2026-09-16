using Code.GameLogic.GameEvents;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// Garage cleanups. The event itself is the ordinary "Name first, DisplayName dropped, TagId order"
/// shape, but four of its members are stored as ids and golden expands every one of them:
/// <c>PatternSets</c> (into the full pattern set, each pattern carrying the reward it grants and its
/// row masks), <c>SlotFillRewards</c> (into reward definitions), <c>SpawnerItems</c> (item ids into
/// item type strings) and, one level deeper, each board's <c>Rows</c> (row ids into the row
/// definitions that hold the actual item grid).
/// <para>
/// Note what is NOT expanded, so the exceptions do not look like oversights: the sibling
/// <c>BoardCosts</c> stays a list of numbers, and a garage-cleanup reward's <c>VisualItem</c> is
/// always written as <c>null</c> (see <see cref="WriteReward"/>).
/// </para>
/// </summary>
public sealed partial class EventSerializer
{
    private void WriteGarageCleanup(JsonWriter w, GarageCleanupEventInfo gc, JsonSerializer s)
    {
        w.WriteStartObject();
        w.WritePropertyName("Name");
        w.WriteValue(ResolveGarageCleanupName(gc));

        MetaObjectWriter.WriteMembers(w, gc, s, _log, (member, value) =>
        {
            switch (member)
            {
                case nameof(GarageCleanupEventInfo.DisplayName):
                    return true; // raw stub (it is just the event id), never written

                case nameof(GarageCleanupEventInfo.PatternSets):
                    WriteResolvedList(w, s, "PatternSets", value, _config.GarageCleanupPatternSets);
                    return true;

                case nameof(GarageCleanupEventInfo.SpawnerItems):
                    // Raw item ids in the config, item TYPE strings in golden — unlike the sibling
                    // BoardCosts (plain numbers) and unlike a progression event's EventItem, which
                    // stays a number.
                    w.WritePropertyName(member);
                    w.WriteStartArray();
                    foreach (var id in (value as IEnumerable<int>) ?? Enumerable.Empty<int>())
                        w.WriteValue(ItemTypeOf(id));
                    w.WriteEndArray();
                    return true;

                case nameof(GarageCleanupEventInfo.SlotFillRewards):
                    WriteResolvedList(w, s, "SlotFillRewards", value, _config.GarageCleanupRewards);
                    return true;

                default:
                    return false;
            }
        });
        w.WriteEndObject();
    }

    /// <summary>
    /// One garage-cleanup pattern, with the reward it grants resolved from its <c>RewardId</c> and
    /// hoisted to the front — the rest is the plain TagId member dump.
    /// </summary>
    private void WritePattern(JsonWriter w, GarageCleanupPatternInfo pattern, JsonSerializer s)
    {
        w.WriteStartObject();
        w.WritePropertyName("Reward");
        if (pattern.RewardId != null && _config.GarageCleanupRewards != null
            && _config.GarageCleanupRewards.TryGetValue(pattern.RewardId, out var reward) && reward != null)
            s.Serialize(w, reward);
        else
            w.WriteNull();
        MetaObjectWriter.WriteMembers(w, pattern, s, _log, (member, value) =>
        {
            if (member != nameof(GarageCleanupPatternInfo.Rows)) return false;
            WriteResolvedList(w, s, "Rows", value, _config.GarageCleanupPatternRows);
            return true;
        });
        w.WriteEndObject();
    }

    /// <summary>
    /// A garage-cleanup reward. Identical to the plain member dump except that <c>VisualItem</c> is
    /// always written as <c>null</c>: golden does that on every one of the 1 491 rewards in the
    /// newest archive (and in the app's own 26.05/26.06/26.07 dumps), even though the member holds a
    /// perfectly resolvable <c>ItemDef</c> at runtime — the reading is that the member, which is
    /// marked <c>[MetaOnMemberDeserializationFailure("FixRef")]</c> because it used to be a raw item
    /// id, is reported as its pre-fix-up value. Fitted to golden, mechanism unconfirmed.
    /// </summary>
    private void WriteReward(JsonWriter w, GarageCleanupRewardInfo reward, JsonSerializer s)
    {
        w.WriteStartObject();
        MetaObjectWriter.WriteMembers(w, reward, s, _log, (member, _) =>
        {
            if (member != nameof(GarageCleanupRewardInfo.VisualItem)) return false;
            w.WritePropertyName(member);
            w.WriteNull();
            return true;
        });
        w.WriteEndObject();
    }

    /// <summary>A cleanup board, whose <c>Rows</c> member holds row IDs that golden expands in place.</summary>
    private void WriteBoard(JsonWriter w, GarageCleanupBoardInfo board, JsonSerializer s)
    {
        w.WriteStartObject();
        MetaObjectWriter.WriteMembers(w, board, s, _log, (member, value) =>
        {
            if (member != nameof(GarageCleanupBoardInfo.Rows)) return false;
            WriteResolvedList(w, s, "Rows", value, _config.GarageCleanupBoardRows);
            return true;
        });
        w.WriteEndObject();
    }

    /// <summary>The item type string for a raw item id, or the id itself when the config has no such item.</summary>
    private object ItemTypeOf(int itemId)
    {
        if (_config.Items != null && _config.Items.TryGetValue(itemId, out var item) && item?.ItemType != null)
            return item.ItemType;
        _log.Trace($"GarageCleanup SpawnerItems: unknown item id {itemId}");
        return itemId;
    }

    /// <summary>
    /// Writes a member that holds keys or references as the list of definitions they point at.
    /// A key that does not resolve is skipped, so a patched-away pattern set shortens the array
    /// rather than leaving a hole.
    /// </summary>
    private void WriteResolvedList<TKey, TValue>(JsonWriter w, JsonSerializer s, string member, object? value,
        Metaplay.Core.Config.GameConfigLibrary<TKey, TValue>? library)
        where TValue : class
    {
        w.WritePropertyName(member);
        w.WriteStartArray();
        if (value is System.Collections.IEnumerable items && library != null)
        {
            foreach (var item in items)
            {
                var key = item switch
                {
                    Metaplay.Core.IMetaRef reference => (TKey?)reference.KeyObject,
                    TKey direct => direct,
                    _ => default,
                };
                if (key == null || !library.TryGetValue(key, out var resolved) || resolved == null)
                {
                    _log.Trace($"GarageCleanup {member}: unresolved entry '{item}' skipped");
                    continue;
                }
                s.Serialize(w, resolved);
            }
        }
        w.WriteEndArray();
    }
}
