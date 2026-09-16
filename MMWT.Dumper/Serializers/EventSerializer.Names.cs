using System.Text.RegularExpressions;
using Code.GameLogic.GameEvents;
using Code.GameLogic.GameEvents.CardCollectionSupportingEvent;
using MergeMansionWikiTools.Dumper.Core;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// Every event family resolves its display title differently, so this partial holds the resolvers
/// rather than scattering them across the writers. The one thing they all share: the raw config
/// <c>DisplayName</c> is an internal stub and never reaches the dump under that key.
/// </summary>
public sealed partial class EventSerializer
{
    /// <summary>Loc key that titles every "Merge it Up!" progression pack event.</summary>
    private const string ProgressionPackNameLocId = "PP_Shared_MainHeader";

    /// <summary>Loc key of the shared start-popup blurb every progression pack event carries.</summary>
    private const string ProgressionPackDescriptionLocId = "PP_Generic_Start_Popup_Desc";

    /// <summary>Shared loc key behind every Clue Rush event; their own per-event NameLocId is a stub.</summary>
    private const string CardCollectionSupportingTitleLocId = "CCSE_title";

    /// <summary>Loc key titling every Mix a Booster event (they carry no NameLocId of their own).</summary>
    private const string MixABoosterNameLocId = "MaB_MainInterface_Header";

    /// <summary>
    /// Constant asset id golden reports for every Mix a Booster event. The config type has no
    /// asset field at all, and the value is the same in all six live archives.
    /// </summary>
    private const string MixABoosterAssetOverride = "MAB_MHB_Generic";

    /// <summary>Suffix appended to a garage cleanup's parent event name.</summary>
    private const string GarageCleanupNameSuffix = " Garage Cleanup";

    /// <summary>A trailing <c>_12</c>-style round number, stripped when deriving a base event id.</summary>
    private static readonly Regex TrailingRoundNumber = new(@"_\d+$", RegexOptions.Compiled);

    /// <summary>
    /// The plain rule used by collectible boards, leaderboards, progressions and Boulton League:
    /// the localized <c>NameLocId</c>, keeping the key itself when the language file has no entry
    /// (golden's <c>CBE_Easter2023</c> really is named <c>"CBE_Easter2023_Name"</c>). Falls back to
    /// the raw display name only when there is no key at all.
    /// </summary>
    private string? ResolveNameFromLocId(string? nameLocId, string? displayName)
    {
        if (string.IsNullOrEmpty(nameLocId)) return displayName;
        return Loc.SafeLoc(() => LocMan.Get(nameLocId), nameLocId, _log, nameLocId);
    }

    /// <summary>
    /// Core-support events. Dig events and classic races store an internal stub in
    /// <c>DisplayName</c> ("Re-Archeology", "Classic Races"), so their real title comes from a loc
    /// key derived from the config key with its round number cut off:
    /// <c>CR_Sailing_06</c> -&gt; <c>CR_Sailing_Name</c> -&gt; "Hopewell Bay Horizons Cup". Dig
    /// events have no per-biome key (<c>DE_StoneAge_Name</c> does not exist), so they fall back to
    /// the generic <c>DE_Generic_Name</c>. Every other type keeps its raw display name, which is a
    /// real title for them ("Auto Merge", "Roll The Dice", "Builder Event").
    /// </summary>
    private string? ResolveCoreSupportEventName(CoreSupportEventInfo evt)
    {
        var type = evt.EventType;
        if (type != CoreSupportEventType.DigEvent && type != CoreSupportEventType.ClassicRaces)
            return evt.DisplayName;

        var baseId = TrailingRoundNumber.Replace(evt.ConfigKey?.Value ?? "", "");
        if (baseId.Length > 0 && TryLoc($"{baseId}_Name", out var title)) return title;
        if (type == CoreSupportEventType.DigEvent && TryLoc("DE_Generic_Name", out var generic)) return generic;
        return evt.DisplayName;
    }

    /// <summary>
    /// Garage cleanups have no title of their own: golden names them
    /// "&lt;parent seasonal event&gt; Garage Cleanup". The parent is found by cutting the
    /// <c>GC_</c> prefix and any trailing round number off the cleanup id and taking the first
    /// collectible board event whose id ENDS with what remains — the prefix varies
    /// (<c>GC_GrandmasBirthday2023</c> belongs to <c>LDE_GrandmasBirthday2023</c>,
    /// <c>GC_DoubleDateDisaster2024</c> to <c>SE_DoubleDateDisaster2024</c>). A cleanup with no
    /// matching parent (<c>Bingo_01</c>, <c>GC_SpringSeason2023</c>) is simply "Garage Cleanup".
    /// </summary>
    private string ResolveGarageCleanupName(GarageCleanupEventInfo gc)
    {
        var parent = ResolveGarageCleanupParentName(gc.GarageCleanupEventId?.Value ?? "");
        return string.IsNullOrEmpty(parent) ? "Garage Cleanup" : parent + GarageCleanupNameSuffix;
    }

    private string? ResolveGarageCleanupParentName(string cleanupId)
    {
        var suffix = GarageCleanupParentSuffix(cleanupId);
        if (suffix.Length == 0 || _config.CollectibleBoardEvents == null) return null;

        foreach (var (_, value) in _config.CollectibleBoardEvents.EnumerateAll())
        {
            if (value is not CollectibleBoardEventInfo board) continue;
            var boardId = board.CollectibleBoardEventId?.Value;
            if (boardId == null || !boardId.EndsWith(suffix, StringComparison.Ordinal)) continue;
            return ResolveNameFromLocId(board.NameLocId, board.DisplayName);
        }
        return null;
    }

    /// <summary>
    /// <c>GC_AmeliaBoulton2024_01</c> -&gt; <c>AmeliaBoulton2024</c>. Public for the unit test that
    /// pins the derivation.
    /// </summary>
    public static string GarageCleanupParentSuffix(string cleanupId)
    {
        var id = cleanupId ?? "";
        if (id.StartsWith("GC_", StringComparison.Ordinal)) id = id[3..];
        return TrailingRoundNumber.Replace(id, "");
    }

    /// <summary>
    /// Clue Rush: the per-event <c>NameLocId</c> ("CCSE_1") and <c>DisplayName</c> ("CCSE XL Boom")
    /// are both internal stubs, and the real title lives under the shared <c>CCSE_title</c> key.
    /// Unlike <see cref="ResolveNameFromLocId"/> this chain uses a real "has the key?" test, so an
    /// unresolvable per-event key falls through instead of being written verbatim.
    /// </summary>
    private string? ResolveCardCollectionSupportingName(CardCollectionSupportingEventInfo evt)
    {
        if (!string.IsNullOrEmpty(evt.NameLocId) && TryLoc(evt.NameLocId, out var own)) return own;
        if (TryLoc(CardCollectionSupportingTitleLocId, out var shared)) return shared;
        return evt.DisplayName;
    }

    /// <summary>
    /// The event item's merge-CHAIN name (its item category), not the level-1 item name: the
    /// chain's own <c>ItemCategory_&lt;key&gt;</c>, then the item's pool tag, then its
    /// <c>OverrideLocalizationItemCategory</c> used as a whole key.
    /// </summary>
    private string? ResolveEventItemChainName(int eventItem)
    {
        if (eventItem == 0 || _config.Items == null) return null;
        if (!_config.Items.TryGetValue(eventItem, out var item) || item == null) return null;

        var chainKey = item.MergeChainDef?.ConfigKey?.Value;
        if (!string.IsNullOrEmpty(chainKey) && TryLoc($"ItemCategory_{chainKey}", out var byChain)) return byChain;
        if (!string.IsNullOrEmpty(item.PoolTag) && TryLoc($"ItemCategory_{item.PoolTag}", out var byPool)) return byPool;
        if (!string.IsNullOrEmpty(item.OverrideLocalizationItemCategory)
            && TryLoc(item.OverrideLocalizationItemCategory, out var byOverride)) return byOverride;
        return null;
    }

    /// <summary>
    /// Localization lookup that reports "no such key" instead of echoing the key back — the shared
    /// <see cref="Loc.SafeLoc"/> cannot express that, since <c>LocMan.Get</c> returns the key for a
    /// miss. Same exception policy: a throwing lookup is logged and treated as a miss.
    /// </summary>
    private bool TryLoc(string key, out string value)
    {
        try
        {
            return LocMan.TryGet(key, out value);
        }
        catch (Exception ex)
        {
            _log.Warn($"Localization failed for {key} ({ex.GetType().Name}: {ex.Message}) — treated as missing");
            value = "";
            return false;
        }
    }
}
