using System.Collections;
using Code.GameLogic.GameEvents;
using GameLogic.Decorations;
using GameLogic.Player.Rewards;
using MergeMansionWikiTools.Dumper.Core;
using Metaplay.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

public sealed partial class EventSerializer
{
    /// <summary>
    /// The <c>ActiveDecoration</c> block written next to a board's <c>ActiveDecorationRef</c>.
    /// <para>
    /// A seasonal event's decoration is handed out one layer at a time, and from some layer onwards
    /// the player KEEPS it even if the event ends unfinished. That threshold is
    /// <see cref="LayeredDecorationSetInfo.RequiredProgressToOwn"/> — a config field two references
    /// away from the board (board → <see cref="DecorationInfo"/> → <see cref="LayeredDecorationSetInfo"/>),
    /// which is why it never reached the dump: no dumped object was within reach of it.
    /// </para>
    /// <para>
    /// Without it the threshold could only be read out of the localized popup
    /// (<c>&lt;EventId&gt;_LayeredDecoInfoPopup_Description</c>), which spells the level out in prose
    /// and does not exist for every event — a source that breaks the moment MetaCore rewords it.
    /// </para>
    /// </summary>
    /// <param name="DecorationId">The decoration the board upgrades, i.e. the ref's own key.</param>
    /// <param name="LayeredDecorationSetId">Its layer set, the object the threshold lives on.</param>
    /// <param name="RequiredProgressToOwn">
    /// Decoration progress (= layer) from which the decoration is permanently owned. Every pet home
    /// in the config uses 1, i.e. owned on the first layer.
    /// </param>
    /// <param name="EventLevelToOwn">
    /// The board's own level that first awards that progress, resolved through <c>LevelRefs</c> —
    /// 1-based, i.e. the number the game and the wiki call "Event Level". Null when no level of this
    /// board awards the progress (a decoration shared with another event, or a level list the patch
    /// removed), never guessed.
    /// </param>
    private sealed record ActiveDecorationDetail(
        string DecorationId,
        string? LayeredDecorationSetId,
        int RequiredProgressToOwn,
        int? EventLevelToOwn);

    /// <summary>
    /// Resolves the detail block for a board's decoration reference, or null when there is nothing
    /// to say (unresolved reference, no layer set behind it). Never throws: a dump must not die over
    /// a decoration, so every step that can fail is logged and degrades to null.
    /// </summary>
    private ActiveDecorationDetail? ResolveActiveDecoration(object board, object decorationRef)
    {
        var decoration = ResolveRef(decorationRef) as DecorationInfo;
        if (decoration == null) return null;

        var decorationId = decoration.DecorationId.ToString();
        if (string.IsNullOrEmpty(decorationId)) return null;

        // DecorationInfo exposes the set only through a PRIVATE MetaMember; its public
        // LayeredDecorationSetInfo shorthand is not populated on the config objects we import.
        var setRef = MetaObjectWriter.GetMember(decoration, "LayeredDecorationSetInfoRef", _log);
        if (ResolveRef(setRef) is not LayeredDecorationSetInfo set) return null;

        var progress = set.RequiredProgressToOwn;
        return new ActiveDecorationDetail(
            decorationId,
            set.SetId.ToString(),
            progress,
            FindLevelAwardingProgress(board, decorationId, progress));
    }

    /// <summary>
    /// Walks the board's <c>LevelRefs</c> in order and returns the 1-based position of the first
    /// level whose rewards push this decoration to <paramref name="progress"/> or beyond.
    /// <para>
    /// The position is used, not the level id: the id is a string the content team chose
    /// (<c>LDE_MurderAtTheMansion_RewardLevel35</c>) and its number has no guarantee of matching the
    /// level's place in the list, whereas the place IS what the game shows as the Event Level.
    /// </para>
    /// <para><c>&gt;=</c> rather than <c>==</c> so a set whose owning threshold sits between two
    /// awarded layers still resolves to the level that first passes it.</para>
    /// </summary>
    private int? FindLevelAwardingProgress(object board, string decorationId, int progress)
    {
        if (MetaObjectWriter.GetMember(board, nameof(CollectibleBoardEventInfo.LevelRefs), _log)
            is not IEnumerable levelRefs) return null;

        var position = 0;
        foreach (var levelRef in levelRefs)
        {
            position++;
            if (ResolveRef(levelRef) is not EventLevelInfo level || level.Rewards == null) continue;
            foreach (var reward in level.Rewards)
            {
                if (reward is not RewardLayeredDecoration layered) continue;
                if (layered.Progress < progress) continue;
                var rewardDecoration = ResolveRef(
                    MetaObjectWriter.GetMember(layered, "DecorationRef", _log)) as DecorationInfo;
                if (rewardDecoration?.DecorationId.ToString() != decorationId) continue;
                return position;
            }
        }

        return null;
    }

    /// <summary>
    /// The resolved target of a <see cref="IMetaRef"/>, or null for anything unresolved. A reference
    /// whose key is present but whose target is gone (the <c>SPNoDecorations_01_B</c> patch removes
    /// decorations wholesale) throws on <c>Ref</c>, so the access is guarded.
    /// </summary>
    private object? ResolveRef(object? maybeRef)
    {
        if (maybeRef is not IMetaRef metaRef) return null;
        // Same emptiness test the member writer uses for MetaRefSkip.EmptyKey, so a reference this
        // helper accepts is exactly one the dump would have printed.
        if (metaRef.KeyObject is null || metaRef.KeyObject.ToString() is null) return null;
        try
        {
            return metaRef.IsResolved ? metaRef.MaybeRefObject : null;
        }
        catch (Exception ex)
        {
            _log.Warn($"decoration reference {metaRef.KeyObject}: {ex.GetType().Name} — {ex.Message} — treated as unresolved");
            return null;
        }
    }

    /// <summary>Writes the block produced by <see cref="ResolveActiveDecoration"/>.</summary>
    private static void WriteActiveDecoration(JsonWriter w, ActiveDecorationDetail detail)
    {
        w.WritePropertyName("ActiveDecoration");
        w.WriteStartObject();
        w.WritePropertyName("LayeredDecorationSetId");
        w.WriteValue(detail.LayeredDecorationSetId);
        w.WritePropertyName("RequiredProgressToOwn");
        w.WriteValue(detail.RequiredProgressToOwn);
        if (detail.EventLevelToOwn.HasValue)
        {
            w.WritePropertyName("EventLevelToOwn");
            w.WriteValue(detail.EventLevelToOwn.Value);
        }
        w.WriteEndObject();
    }
}
