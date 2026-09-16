using Code.GameLogic.GameEvents;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;

namespace MergeMansionWikiTools.Dumper.Dumpers;

/// <summary>
/// Produces <c>events.json</c>: 36 event-config categories in a fixed key order, each gated by an
/// <see cref="EventFilters"/> flag.
/// <para>
/// Two gating styles, both taken from the legacy <c>--filters</c> corpus and NOT interchangeable:
/// a library-gated category is emitted whenever its flag is set, even when the library is empty
/// (<c>"Shops": []</c> appears under the <c>Shops</c> flag), while the three per-event categories
/// (collectible boards, core-support events, leaderboards) are emitted only when the filtered list
/// has at least one event — which is why <c>events[Legacy].json</c> carries <c>Leaderboards</c> but
/// no <c>CollectibleBoards</c> key at all.
/// </para>
/// </summary>
public sealed class EventDumper
{
    private readonly IDumpLog _log;

    public EventDumper(IDumpLog? log = null) => _log = log ?? ConsoleDumpLog.Instance;

    public string Dump(SharedGameConfig config, EventFilters filters)
    {
        ArgumentNullException.ThrowIfNull(config);

        var serializer = new EventSerializer(config, _log);
        var settings = DumpConverters.MainFileSettings(config, _log, MetaRefSkip.Unresolved,
            serializer,
            new DailyChallengesSerializer(),
            // Event levels carry the same director-action union as hotspot completions.
            new DirectorActionSerializer(config, _log));

        var data = BuildCategories(config, filters, serializer);
        _log.Trace($"Event categories: {data.Count}");
        var json = DumpJson.Serialize(data, config.ArchiveCreatedAt, DumpFileKind.Main, settings);
        DumpConverters.LogRequirementSummary(settings, _log, "events.json");
        return json;
    }

    private OrderedMap BuildCategories(SharedGameConfig config, EventFilters filters, EventSerializer serializer)
    {
        var map = new OrderedMap();

        // 1. CollectibleBoards — per-event, four possible flags.
        AddIfAny(map, "CollectibleBoards", All(config.CollectibleBoardEvents)
            .Where(e => Has(filters, EventFilterRules.ForCollectibleBoard(e.CollectibleBoardEventId?.Value))));

        // 2-4. Progressions (Mysteries) and the always-exported progression packs.
        if (Has(filters, EventFilters.Mysteries))
            map.Add("Progressions", All(config.ProgressionEvents).ToList());
        map.Add("ProgressionPackEvents", All(config.ProgressionPackEvents).Select(serializer.ProgressionPackEvent).ToList());
        map.Add("ProgressionPacks", All(config.ProgressionPacks).Select(serializer.ProgressionPack).ToList());

        // 5. CardCollectionSupportingEvents — always exported so the event schedule can see Clue Rush.
        map.Add("CardCollectionSupportingEvents", All(config.CardCollectionSupportingEvents).Select(serializer.CardCollectionSupportingEvent).ToList());

        // 6-7. GarageCleanups + CoreSupportEvents (the latter per-event across five flags).
        if (Has(filters, EventFilters.GarageCleanup))
            map.Add("GarageCleanups", All(config.GarageCleanupEvents).ToList());
        AddIfAny(map, "CoreSupportEvents", All(config.CoreSupportEvents)
            .Where(e => Has(filters, EventFilterRules.ForCoreSupportEvent(e.ConfigKey?.Value))));

        // 8-10. Boulton League, then leaderboards (per-event: BakeOff / Bonanza / Legacy).
        if (Has(filters, EventFilters.BoultonLeague))
        {
            map.Add("BoultonLeagueEvents", All(config.BoultonLeagueEvents).Select(serializer.BoultonLeagueEvent).ToList());
            map.Add("BoultonLeagueStages", All(config.BoultonLeagueStages).Select(serializer.BoultonLeagueStage).ToList());
        }
        AddIfAny(map, "Leaderboards", All(config.LeaderboardEvents)
            .Where(e => Has(filters, EventFilterRules.ForLeaderboard(e.LeaderboardEventId?.Value, e.DisplayName))));

        // 11. Shops — library-gated, so the empty library still produces "Shops": [].
        if (Has(filters, EventFilters.Shops))
            map.Add("Shops", All(config.ShopEvents).ToList());

        // 12-16. Daily tasks (V1 + V2 + settings).
        if (Has(filters, EventFilters.DailyTrades))
        {
            map.Add("DailyTasks", All(config.DailyTasks).ToList());
            map.Add("DailyTasksV2", All(config.DailyTasksV2).ToList());
            map.Add("DailyTasksV2MergeChains", All(config.DailyTasksV2MergeChains).ToList());
            map.Add("DailyTasksV2CompletionRewards", All(config.DailyTasksV2CompletionRewards).ToList());
            map.Add("DailyTasksV2Settings", config.DailyTasksV2Settings);
        }

        // 17. EventLevels — always exported: a progression event only holds MetaRefs, so an A/B patch
        // that rewrites a level set would otherwise be invisible in the dump.
        map.Add("EventLevels", All(config.EventLevels).ToList());

        // 18-31. The Daily Scoop, V1 and V2 (DailyChallenges*), both under the DailyScoop flag.
        if (Has(filters, EventFilters.DailyScoop))
        {
            map.Add("DailyScoopMilestones", All(config.DailyScoopMilestones).ToList());
            map.Add("DailyScoopStandardObjectives", All(config.DailyScoopStandardObjectives).ToList());
            map.Add("DailyScoopSpecialObjectives", All(config.DailyScoopSpecialObjectives).ToList());
            map.Add("DailyScoopDays", All(config.DailyScoopDays).ToList());
            map.Add("DailyScoopWeeks", All(config.DailyScoopWeeks).ToList());

            map.Add("DailyChallengesMinigames", All(config.DailyChallengesMinigames).ToList());
            map.Add("DailyChallengesWeeksByMinigameId", All(config.DailyChallengesWeeksByMinigameId).ToList());
            map.Add("DailyChallengesWeeksByPreviousCompletion", All(config.DailyChallengesWeeksByPreviousCompletion).ToList());
            map.Add("DailyChallengesEventSettings", config.DailyChallengesEventSettings);
            map.Add("DailyChallengesWeeks", All(config.DailyChallengesWeeks).ToList());
            map.Add("DailyChallengesDays", All(config.DailyChallengesDays).ToList());
            map.Add("DailyChallengesStandardObjectives", All(config.DailyChallengesStandardObjectives).ToList());
            map.Add("DailyChallengesSpecialObjectives", All(config.DailyChallengesSpecialObjectives).ToList());
            map.Add("DailyChallengesMilestones", All(config.DailyChallengesMilestones).ToList());
        }

        // 32-33. Solo milestones, sorted by (base id, numeric suffix) rather than library order.
        if (Has(filters, EventFilters.SoloMilestone))
        {
            map.Add("SoloMilestoneEvents", All(config.SoloMilestoneEvents)
                .OrderBy(e => e.ConfigKey?.Value ?? "", SoloMilestoneOrder.Comparer)
                .Select(serializer.SoloMilestoneEvent).ToList());
            map.Add("SoloMilestoneMilestones", All(config.SoloMilestoneMilestones)
                .OrderBy(m => m.ConfigKey?.Value ?? "", SoloMilestoneOrder.Comparer).ToList());
        }

        // 34-36. Mix a Booster.
        if (Has(filters, EventFilters.MixABooster))
        {
            map.Add("MixABoosterEvents", All(config.MixABoosterEvents).Select(serializer.MixABoosterEvent).ToList());
            map.Add("MixABoosterRecipes", All(config.MixABoosterRecipes).Select(serializer.MixABoosterRecipe).ToList());
            map.Add("MixABoosterIngredients", All(config.MixABoosterIngredients).ToList());
        }

        return map;
    }

    /// <summary>
    /// Adds a per-event category only when the filtered list is non-empty — see the class remarks
    /// for why this differs from the library-gated categories.
    /// </summary>
    private static void AddIfAny<T>(OrderedMap map, string key, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (list.Count > 0) map.Add(key, list);
    }

    private static bool Has(EventFilters filters, EventFilters flag) => (filters & flag) != 0;

    /// <summary>
    /// Library values in config order; a library missing from the archive yields nothing. The game's
    /// <c>EnumerateAll</c> is untyped (<c>KeyValuePair&lt;object, object&gt;</c>), so the cast lives
    /// here rather than at each of the 30-odd call sites.
    /// </summary>
    private static IEnumerable<TValue> All<TKey, TValue>(Metaplay.Core.Config.GameConfigLibrary<TKey, TValue>? library)
        => library == null ? Enumerable.Empty<TValue>() : library.EnumerateAll().Select(kv => (TValue)kv.Value);
}

/// <summary>
/// Solo-milestone ordering: ids sort by their base name and then by the numeric suffix, so
/// <c>MySummerTea_2</c> precedes <c>MySummerTea_10</c> and the <c>_Onfire</c> / <c>_C_</c> variants
/// group after the plain run. Base comparison is ORDINAL — that is what puts <c>MySummerTeaCards</c>
/// before <c>MySummerTea_19_Onfire</c> ('C' &lt; '_'), which a culture-aware compare would not.
/// </summary>
public static class SoloMilestoneOrder
{
    public static readonly IComparer<string> Comparer = new KeyComparer();

    /// <summary>Splits <c>"MySummerTea_38"</c> into <c>("MySummerTea", 38)</c>; no suffix gives 0.</summary>
    public static (string Base, int Index) Split(string id)
    {
        var cut = id.LastIndexOf('_');
        if (cut > 0 && cut < id.Length - 1 && int.TryParse(id.AsSpan(cut + 1), out var n))
            return (id[..cut], n);
        return (id, 0);
    }

    private sealed class KeyComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var (bx, ix) = Split(x ?? "");
            var (by, iy) = Split(y ?? "");
            var c = string.CompareOrdinal(bx, by);
            return c != 0 ? c : ix.CompareTo(iy);
        }
    }
}
