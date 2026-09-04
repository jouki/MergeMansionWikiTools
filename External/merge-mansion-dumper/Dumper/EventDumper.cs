// Modified by Jouki (2026) — EventFilters: per-event filtering by EventId prefix
// Replaces old EventCategories (per-config-type) with game-event-based filters
using System;
using System.Collections.Generic;
using System.Linq;
using Code.GameLogic.GameEvents;
using Code.GameLogic.GameEvents.CardCollectionSupportingEvent;
using Code.GameLogic.GameEvents.SoloMilestone;
using GameLogic.Config;
using GameLogic.MixABooster;
using merge_mansion_dumper.Dumper.Base;
using merge_mansion_dumper.Dumper.Json;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace merge_mansion_dumper.Dumper
{
    [Flags]
    public enum EventFilters
    {
        None = 0,
        LuckyCatch = 1 << 0,
        LuckySnap = 1 << 1,
        Seasonal = 1 << 2,
        ReArchaeology = 1 << 3,
        HorizonsCup = 1 << 4,
        MixABooster = 1 << 5,
        RollTheDice = 1 << 6,
        GarageCleanup = 1 << 7,
        Mysteries = 1 << 8,
        BoultonLeague = 1 << 9,
        Legacy = 1 << 10,
        Uncategorised = 1 << 11,
        BakeOff = 1 << 12,
        Bonanza = 1 << 13,
        Shops = 1 << 14,
        SoloMilestone = 1 << 15,
        DailyTrades = 1 << 16,
        DailyScoop = 1 << 17,
        AutoMerge = 1 << 18,
        All = LuckyCatch | LuckySnap | Seasonal | ReArchaeology | HorizonsCup
            | MixABooster | RollTheDice | GarageCleanup | Mysteries | BoultonLeague
            | Legacy | Uncategorised | BakeOff | Bonanza | Shops
            | SoloMilestone | DailyTrades | DailyScoop | AutoMerge
    }

    public class EventDumper : JsonDumper<IDictionary<string, object>>
    {
        private readonly EventFilters _filters;

        public EventDumper(EventFilters filters = EventFilters.All)
        {
            _filters = filters;
        }

        public override IDictionary<string, object> Dump(SharedGameConfig config)
        {
            var events = new Dictionary<string, object>();

            // CollectibleBoards — sub-filtered by EventId prefix
            if (HasAnyFlag(EventFilters.LuckyCatch | EventFilters.LuckySnap | EventFilters.Seasonal | EventFilters.Legacy))
            {
                var filtered = config.CollectibleBoardEvents?.EnumerateAll()
                    .Where(x => MatchesCollectibleFilter(x.Key.ToString()))
                    .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                if (filtered.Length > 0)
                    events["CollectibleBoards"] = filtered;
            }

            // Progressions → Mysteries filter
            if (_filters.HasFlag(EventFilters.Mysteries))
                events["Progressions"] = config.ProgressionEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // ProgressionPackEvents + ProgressionPacks (= "Merge It Up" — merge-counter pass with
            // free/premium tracks). The game classifies these in their own library, NOT under
            // any of the *Event filters above, so they were silently missing from every dump.
            //
            // ProgressionPackEvents → event-level metadata (ConfigKey, Schedule with StartDate +
            //   Duration via MetaActivableParams.Schedule, PremiumIAP product, Segments, Priority,
            //   ObjectiveType, ObjectiveParameter, OfferGroupId, PlacementId).
            //
            // ProgressionPacks → pack content (FreeOffers + PremiumOffers per level —
            //   RewardCoins/RewardGems/RewardItem/etc., LevelRequirements thresholds list,
            //   ObjectiveType (Merge=1, UseProducer=3, CompleteTasks=7 — see StatsObjectiveType.cs)).
            //
            // Always exported — no filter flag, mirroring DailyScoop / EventLevels pattern.
            // Custom Dictionary shape so DisplayName/Description are resolved through the language
            // table (`PP_Shared_MainHeader` → "Merge It Up") and so each event surfaces its linked
            // pack contents inline — saves jumping between two top-level arrays just to read one event.
            events["ProgressionPackEvents"] = config.ProgressionPackEvents?.EnumerateAll()
                .OrderBy(x => GetProgressionPackEventStart(x.Value))
                .Select(x =>
                {
                    var evt = (GameLogic.ProgressivePacks.ProgressionPackEventInfo)x.Value;
                    var packId = evt.ProgressionPackId?.ToString();
                    var pack = !string.IsNullOrEmpty(packId)
                        ? config.ProgressionPacks?.EnumerateAll()
                            .Where(p => string.Equals(((GameLogic.ProgressivePacks.ProgressionPack)p.Value).ConfigKey?.ToString(), packId, StringComparison.Ordinal))
                            .Select(p => (GameLogic.ProgressivePacks.ProgressionPack)p.Value)
                            .FirstOrDefault()
                        : null;

                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = evt.ConfigKey?.ToString(),
                        ["Name"] = Localize(evt.DisplayName) ?? evt.DisplayName,
                        ["NameLocId"] = evt.DisplayName,
                        ["Description"] = Localize(evt.Description) ?? evt.Description,
                        ["DescriptionLocId"] = evt.Description,
                        ["ActivableParams"] = evt.ActivableParams,
                        ["UnlockRequirement"] = evt.UnlockRequirement,
                        ["GroupId"] = evt.GroupId?.ToString(),
                        ["Priority"] = evt.Priority,
                        ["PremiumIAP"] = evt.PremiumIAP?.KeyObject?.ToString(),
                        ["UseOfferId"] = evt.UseOfferId,
                        ["OfferGroupId"] = evt.OfferGroupId?.KeyObject?.ToString(),
                        ["PlacementId"] = evt.PlacementId,
                        ["CategoryInfo"] = evt.CategoryInfo,
                        ["ProgressionPackId"] = packId,
                        ["Pack"] = pack != null ? BuildProgressionPackPayload(pack) : null,
                    };
                }).ToArray() ?? Array.Empty<object>();

            // Standalone pack library (also keep for completeness — some packs may be referenced
            // by multiple event instances or by AB patches that override the pack content).
            events["ProgressionPacks"] = config.ProgressionPacks?.EnumerateAll()
                .Select(x => BuildProgressionPackPayload((GameLogic.ProgressivePacks.ProgressionPack)x.Value))
                .ToArray() ?? Array.Empty<object>();

            // CardCollectionSupportingEvents ("Clue Rush") — weekly CC supporting event. Not under
            // any *Event filter; always exported so the event schedule can surface it. Per-event
            // NameLocId ("CCSE_1") and DisplayName ("CCSE XL Boom") are internal stubs — the real
            // UI title is the shared loc key "CCSE_title" → "Clue Rush".
            events["CardCollectionSupportingEvents"] = config.CardCollectionSupportingEvents?.EnumerateAll()
                .Select(x =>
                {
                    var evt = (CardCollectionSupportingEventInfo)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = evt.ConfigKey?.ToString(),
                        ["Name"] = Localize(evt.NameLocId) ?? Localize("CCSE_title") ?? evt.DisplayName,
                        ["NameLocId"] = evt.NameLocId,
                        ["Description"] = evt.Description,
                        ["ActivableParams"] = evt.ActivableParams,
                        ["CategoryInfo"] = evt.CategoryInfo,
                        ["GroupId"] = evt.GroupId?.ToString(),
                        ["Priority"] = evt.Priority,
                    };
                }).ToArray() ?? Array.Empty<object>();

            // GarageCleanups → GarageCleanup filter.
            // PrefabsOverride ([MetaMember] private, ≈ the event's badge prefab) is already
            // serialized by the default resolver, so no extra projection is needed here.
            if (_filters.HasFlag(EventFilters.GarageCleanup))
                events["GarageCleanups"] = config.GarageCleanupEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // CoreSupportEvents — sub-filtered by EventId prefix.
            // AssetOverride/LocOverride ([MetaMember] private) would be surfaced by the default
            // resolver if set, but they are null for every current CSE event, so nothing to add.
            if (HasAnyFlag(EventFilters.ReArchaeology | EventFilters.HorizonsCup | EventFilters.RollTheDice | EventFilters.Uncategorised | EventFilters.AutoMerge))
            {
                // Some CoreSupportEvents carry an internal dev stub in DisplayName ("Re-Archeology",
                // "Classic Races") that is NOT a loc key. The real localized title lives under
                // `{base}_Name`, where {base} is the ConfigKey without its trailing round number
                // (CR_Sailing_06 → CR_Sailing_Name → "Hopewell Bay Horizons Cup"). DigEvents share a
                // single generic title across their per-round biome themes (DE_StoneAge_Name etc. do
                // NOT exist), so they fall back to DE_Generic_Name → "Maddie's Re-Archaeology".
                // The resolved title is emitted as "Name" (consistent with the other event types);
                // the raw config DisplayName stub is dropped from output. Field order: ConfigKey, Name, Description, ...
                var serializer = JsonSerializer.Create(CreateSettings(config));

                var coreEvents = (config.CoreSupportEvents?.EnumerateAll()
                        .Where(x => MatchesCoreFilter(x.Key.ToString()))
                        .Select(x => x.Value as CoreSupportEventInfo) ?? Enumerable.Empty<CoreSupportEventInfo>())
                    .Where(evt => evt != null)
                    .Select(evt =>
                    {
                        var raw = evt.DisplayName;
                        var resolved = raw;
                        if (evt.EventType == CoreSupportEventType.DigEvent
                            || evt.EventType == CoreSupportEventType.ClassicRaces)
                        {
                            var ck = evt.ConfigKey?.ToString() ?? "";
                            var us = ck.LastIndexOf('_');
                            var baseKey = (us > 0 && ck.Substring(us + 1).All(char.IsDigit))
                                ? ck.Substring(0, us) : ck;

                            var localized = (baseKey.Length > 0 ? Localize($"{baseKey}_Name") : null)
                                ?? (evt.EventType == CoreSupportEventType.DigEvent ? Localize("DE_Generic_Name") : null);
                            if (!string.IsNullOrEmpty(localized))
                                resolved = localized;
                        }

                        // Serialize with the dump's own settings to preserve every field + nested
                        // shapes, then re-emit with the localized title in "Name" (consistent with
                        // the other event types). The raw config DisplayName stub is dropped from
                        // output — it is still read above for the DigEvent/ClassicRaces resolution.
                        var jo = JObject.FromObject(evt, serializer);
                        var ordered = new JObject();
                        if (jo["ConfigKey"] != null) ordered["ConfigKey"] = jo["ConfigKey"];
                        ordered["Name"] = resolved;
                        if (jo["Description"] != null) ordered["Description"] = jo["Description"];
                        foreach (var prop in jo.Properties())
                            if (prop.Name != "DisplayName" && ordered[prop.Name] == null)
                                ordered[prop.Name] = prop.Value;
                        return (object)ordered;
                    })
                    .ToArray();

                if (coreEvents.Length > 0)
                    events["CoreSupportEvents"] = coreEvents;
            }

            // BoultonLeague
            if (_filters.HasFlag(EventFilters.BoultonLeague))
            {
                events["BoultonLeagueEvents"] = config.BoultonLeagueEvents?.EnumerateAll().Select(x =>
                {
                    var evt = (BoultonLeagueEventInfo)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["EventId"] = evt.EventId?.ToString(),
                        ["NameLocId"] = evt.NameLocId,
                        ["Name"] = Localize(evt.NameLocId) ?? evt.DisplayName,
                        ["Description"] = evt.Description,
                        ["ActivableParams"] = evt.ActivableParams,
                        ["CategoryInfo"] = evt.CategoryInfo,
                        ["GroupId"] = evt.GroupId?.ToString(),
                        ["MatchmakingAlgorithm"] = evt.MatchmakingAlgorithm.ToString(),
                        ["JoinAutomatically"] = evt.JoinAutomatically,
                        ["StageRefs"] = evt.StageRefs,
                    };
                }).ToArray() ?? Array.Empty<object>();

                events["BoultonLeagueStages"] = config.BoultonLeagueStages?.EnumerateAll().Select(x =>
                {
                    var stage = (BoultonLeagueStageInfo)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["StageId"] = stage.StageId?.ToString(),
                        ["NameLocId"] = stage.NameLocId,
                        ["Name"] = Localize(stage.NameLocId),
                        ["DemotionScoreThreshold"] = stage.DemotionScoreThreshold,
                        ["PromotionScoreThreshold"] = stage.PromotionScoreThreshold,
                        ["FinishReward"] = stage.FinishReward,
                        ["PromotionReward"] = stage.PromotionReward,
                        ["LeaderboardPlacementRewardLevelRefs"] = stage.LeaderboardPlacementRewardLevelRefs,
                    };
                }).ToArray() ?? Array.Empty<object>();
            }

            // Leaderboards — sub-filtered by EventId/DisplayName
            if (HasAnyFlag(EventFilters.BakeOff | EventFilters.Bonanza | EventFilters.Legacy))
            {
                var filtered = config.LeaderboardEvents?.EnumerateAll()
                    .Where(x => MatchesLeaderboardFilter(x.Key.ToString(), ((LeaderboardEventInfo)x.Value).DisplayName))
                    .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                if (filtered.Length > 0)
                    events["Leaderboards"] = filtered;
            }

            // Shops
            if (_filters.HasFlag(EventFilters.Shops))
            {
                events["Shops"] = config.ShopEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            }

            // Daily Trades (internally DailyTasks = legacy V1, DailyTasksV2 = current).
            // DailyTasksV2MergeChains carries the per-chain trade eligibility (CanBeRequirement /
            // CanBeReward / Min-MaxLevel / multipliers / hotspot+producer gates); Settings holds the
            // global tuning (streak, refresh/buyout/extension pricing, diminishing, sorting ranges).
            if (_filters.HasFlag(EventFilters.DailyTrades))
            {
                events["DailyTasks"] = config.DailyTasks?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                events["DailyTasksV2"] = config.DailyTasksV2?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                events["DailyTasksV2MergeChains"] = config.DailyTasksV2MergeChains?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                events["DailyTasksV2CompletionRewards"] = config.DailyTasksV2CompletionRewards?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                if (config.DailyTasksV2Settings != null)
                    events["DailyTasksV2Settings"] = config.DailyTasksV2Settings;
            }

            // EventLevels — resolved per-level data referenced by ProgressionEvent / Season Pass / etc.
            // ProgressionEventInfo only stores MetaRef<EventLevelInfo>, so patches that touch EventLevels
            // (e.g. WildItem_SeasonPass_01_B) don't show up in event dumps unless we serialize the
            // library itself. Always exported — covers Wild Item / Wild Card / any AB reward swap.
            events["EventLevels"] = config.EventLevels?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // The Daily Scoop (filterable since v0.23.53) — V1 DailyScoop* libraries plus the
            // V2 DailyChallenges* replacement (live since 2026-06-29). Wild Item AB-patch
            // WildItem_DailyScoop_V2_01_B touches DailyScoopStandardObjectives.
            if (_filters.HasFlag(EventFilters.DailyScoop))
            {
            events["DailyScoopMilestones"] = config.DailyScoopMilestones?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyScoopStandardObjectives"] = config.DailyScoopStandardObjectives?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyScoopSpecialObjectives"] = config.DailyScoopSpecialObjectives?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyScoopDays"] = config.DailyScoopDays?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyScoopWeeks"] = config.DailyScoopWeeks?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // DailyChallenges (The Daily Scoop V2, live since 2026-06-29) — nine interlinked
            // libraries that were previously NOT exported (only reachable via the DumpHarness
            // --probe-daily-challenges console probe). The weekly schedule itself lives in
            // CoreSupportEvents (EventType=DailyChallengesEvent) and is exported above; these
            // libraries carry the content: week selection maps, weeks with milestone ladders
            // (Rewards + RewardSegment selection), day compositions incl. the day-completion
            // box, and the standard/special objectives with reward pools and fallback chains.
            // Several types hold PRIVATE [MetaMember]s — serialized completely via
            // MetaDailyChallengesSerializer (see its remarks). Always exported.
            events["DailyChallengesMinigames"] = config.DailyChallengesMinigames?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyChallengesWeeksByMinigameId"] = config.DailyChallengesWeeksByMinigameId?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyChallengesWeeksByPreviousCompletion"] = config.DailyChallengesWeeksByPreviousCompletion?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyChallengesEventSettings"] = config.DailyChallengesEventSettings;
            events["DailyChallengesWeeks"] = config.DailyChallengesWeeks?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyChallengesDays"] = config.DailyChallengesDays?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyChallengesStandardObjectives"] = config.DailyChallengesStandardObjectives?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyChallengesSpecialObjectives"] = config.DailyChallengesSpecialObjectives?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            events["DailyChallengesMilestones"] = config.DailyChallengesMilestones?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            }

            // SoloMilestone events (Teatime Delight etc. — weekend events with milestone levels;
            // task completion in areas grants SoloMilestoneHotspotValue points → level up)
            if (_filters.HasFlag(EventFilters.SoloMilestone))
            {
                events["SoloMilestoneEvents"] = config.SoloMilestoneEvents?.EnumerateAll()
                    .OrderBy(x => GetIdBase(((SoloMilestoneEventInfo)x.Value).ConfigKey?.Value), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => GetIdNumber(((SoloMilestoneEventInfo)x.Value).ConfigKey?.Value))
                    .Select(x =>
                    {
                        var evt = (SoloMilestoneEventInfo)x.Value;
                        return new Dictionary<string, object>
                        {
                            ["ConfigKey"] = evt.ConfigKey?.ToString(),
                            ["NameLocId"] = evt.NameLocId,
                            ["Name"] = Localize(evt.NameLocId) ?? evt.DisplayName,
                            ["Description"] = evt.Description,
                            ["ActivableParams"] = evt.ActivableParams,
                            ["CategoryInfo"] = evt.CategoryInfo,
                            ["GroupId"] = evt.GroupId?.ToString(),
                            ["Theme"] = evt.Theme,
                            ["Priority"] = evt.Priority,
                            ["TokenSpawnsEnabled"] = evt.TokenSpawnsEnabled,
                            ["Milestones"] = evt.Milestones?.Select(m => m?.ToString()).ToArray(),
                            ["UnlockRequirement"] = evt.UnlockRequirement,
                        };
                    }).ToArray() ?? Array.Empty<object>();

                events["SoloMilestoneMilestones"] = config.SoloMilestoneMilestones?.EnumerateAll()
                    .OrderBy(x => GetIdBase(((SoloMilestoneMilestonesInfo)x.Value).ConfigKey?.Value), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => GetIdNumber(((SoloMilestoneMilestonesInfo)x.Value).ConfigKey?.Value))
                    .Select(x =>
                    {
                        var ms = (SoloMilestoneMilestonesInfo)x.Value;
                        return new Dictionary<string, object>
                        {
                            ["ConfigKey"] = ms.ConfigKey?.ToString(),
                            ["Requirement"] = ms.Requirement,
                            ["Rewards"] = ms.Rewards,
                            ["RewardSegment"] = ms.RewardSegment?.Select(s => s?.ToString()).ToArray(),
                        };
                    }).ToArray() ?? Array.Empty<object>();
            }

            // MixABooster (Mix and Booster) events — a CoreSupport-style event where the player
            // collects ingredients, mixes them in recipes, and earns rewards.
            // Three libraries are exported together, all gated on the same flag:
            //   MixABoosterEvents   — per-event metadata (schedule, initial ingredients, recipe refs)
            //   MixABoosterRecipes  — recipe definitions (required ingredients + reward + secret flag)
            //   MixABoosterIngredients — ingredient definitions (cost only)
            // RecipeRefs is private on MixABoosterEventInfo; the public Recipes property exposes
            // the resolved MixABoosterRecipe objects. RequiredIngredients is private on
            // MixABoosterRecipe; the public IngredientIds property (Dictionary<Id,int>) exposes it.
            if (_filters.HasFlag(EventFilters.MixABooster))
            {
                events["MixABoosterEvents"] = config.MixABoosterEvents?.EnumerateAll().Select(x =>
                {
                    var evt = (MixABoosterEventInfo)x.Value;
                    // The game localises this as "Mix A Booster event"; normalise to the canonical
                    // display name "Mix a Booster" (drop the " event" suffix + lowercase the article).
                    var mbName = Localize(evt.DisplayName) ?? evt.DisplayName;
                    if (!string.IsNullOrEmpty(mbName))
                        mbName = mbName.Replace(" event", "", StringComparison.OrdinalIgnoreCase)
                                       .Replace("Mix A Booster", "Mix a Booster");
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = evt.ConfigKey?.ToString(),
                        ["Name"] = mbName,
                        ["Description"] = Localize(evt.Description) ?? evt.Description,
                        ["ActivableParams"] = evt.ActivableParams,
                        ["CategoryInfo"] = evt.CategoryInfo,
                        ["UnlockRequirement"] = evt.UnlockRequirement,
                        ["PlacementId"] = evt.PlacementId?.ToString(),
                        // The event config carries NO icon/prefab reference (unlike seasonal CBEs). The
                        // event badge is the generic Mix a Booster asset, identical for every run, so we
                        // hardcode the addressable handle here — the schedule data then carries it
                        // (BadgeField "AssetOverride" in EventScheduleService) instead of forcing a blind
                        // asset-bundle hunt. File: MAB_MHB_Generic.png (events/mixabooster/mab_generic).
                        ["AssetOverride"] = "MAB_MHB_Generic",
                        ["InitialIngredients"] = evt.InitialIngredients?
                            .ToDictionary(kv => kv.Key?.ToString(), kv => (object)kv.Value),
                        ["Recipes"] = evt.Recipes?.Select(r => new Dictionary<string, object>
                        {
                            ["ConfigKey"] = r.ConfigKey?.ToString(),
                            ["RequiredIngredients"] = r.IngredientIds?
                                .ToDictionary(kv => kv.Key?.ToString(), kv => (object)kv.Value),
                            ["Reward"] = r.Reward,
                            ["IsSecret"] = r.IsSecret,
                        }).ToArray(),
                    };
                }).ToArray() ?? Array.Empty<object>();

                events["MixABoosterRecipes"] = config.MixABoosterRecipes?.EnumerateAll().Select(x =>
                {
                    var r = (MixABoosterRecipe)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = r.ConfigKey?.ToString(),
                        ["RequiredIngredients"] = r.IngredientIds?
                            .ToDictionary(kv => kv.Key?.ToString(), kv => (object)kv.Value),
                        ["Reward"] = r.Reward,
                        ["IsSecret"] = r.IsSecret,
                    };
                }).ToArray() ?? Array.Empty<object>();

                events["MixABoosterIngredients"] = config.MixABoosterIngredients?.EnumerateAll().Select(x =>
                {
                    var ing = (MixABoosterIngredient)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = ing.ConfigKey?.ToString(),
                        ["Cost"] = ing.Cost,
                    };
                }).ToArray() ?? Array.Empty<object>();
            }

            return events;
        }

        // ── ProgressionPack zip-merge ─────────────────────────────────
        // FreeOffers[i] + PremiumOffers[i] + LevelRequirements[i] are three parallel arrays
        // indexed by level (1..N). Wiki consumers always want them paired, so emit a
        // single "Levels" array where each entry holds the merge threshold + both rewards.

        private static Dictionary<string, object> BuildProgressionPackPayload(GameLogic.ProgressivePacks.ProgressionPack pack)
        {
            var levels = new List<Dictionary<string, object>>();
            var free = pack.FreeOffers ?? new List<GameLogic.Player.Rewards.PlayerReward>();
            var premium = pack.PremiumOffers ?? new List<GameLogic.Player.Rewards.PlayerReward>();
            var reqs = pack.LevelRequirements ?? new List<int>();
            int count = Math.Max(reqs.Count, Math.Max(free.Count, premium.Count));

            for (int i = 0; i < count; i++)
            {
                levels.Add(new Dictionary<string, object>
                {
                    ["Level"] = i + 1,
                    ["MergesRequired"] = i < reqs.Count ? (object)reqs[i] : null,
                    ["FreeReward"] = i < free.Count ? (object)free[i] : null,
                    ["PremiumReward"] = i < premium.Count ? (object)premium[i] : null,
                });
            }

            return new Dictionary<string, object>
            {
                ["ConfigKey"] = pack.ConfigKey?.ToString(),
                ["ObjectiveType"] = pack.ObjectiveType.ToString(),
                ["ObjectiveParameter"] = pack.ObjectiveParameter,
                ["LevelCount"] = levels.Count,
                ["Levels"] = levels,
            };
        }

        private static DateTime GetProgressionPackEventStart(object evt)
        {
            var typed = evt as GameLogic.ProgressivePacks.ProgressionPackEventInfo;
            try
            {
                var sched = typed?.ActivableParams?.Schedule as Metaplay.Core.Schedule.MetaRecurringCalendarSchedule;
                if (sched?.Start != null)
                    return new DateTime(sched.Start.Year, sched.Start.Month, sched.Start.Day,
                                        sched.Start.Hour, sched.Start.Minute, sched.Start.Second,
                                        DateTimeKind.Utc);
            }
            catch { }
            return DateTime.MaxValue;
        }

        // ── ConfigKey sort helpers (e.g. "MySummerTea_38" → ("MySummerTea", 38)) ──
        // Splits trailing "_NN" numeric suffix; non-numeric tails keep number=0.
        // Used to sort solo milestone events + milestones by family prefix then level.

        private static string GetIdBase(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var underscoreIdx = key.LastIndexOf('_');
            if (underscoreIdx < 0 || underscoreIdx == key.Length - 1) return key;
            var tail = key.Substring(underscoreIdx + 1);
            return int.TryParse(tail, out _) ? key.Substring(0, underscoreIdx) : key;
        }

        private static int GetIdNumber(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            var underscoreIdx = key.LastIndexOf('_');
            if (underscoreIdx < 0 || underscoreIdx == key.Length - 1) return 0;
            return int.TryParse(key.Substring(underscoreIdx + 1), out var num) ? num : 0;
        }

        // ── CollectibleBoard classification ───────────────────────────

        private bool MatchesCollectibleFilter(string id)
        {
            // Lucky Catch: LC_* or CBE_LuckyCatch
            if (id.StartsWith("LC_") || id == "CBE_LuckyCatch")
                return _filters.HasFlag(EventFilters.LuckyCatch);

            // Lucky Snap: LS_*
            if (id.StartsWith("LS_"))
                return _filters.HasFlag(EventFilters.LuckySnap);

            // Legacy: GM_* (Lost Gemstones), CBE_GemMine, *GreatEscape*, *Jailbreak*
            if (id.StartsWith("GM_") || id == "CBE_GemMine"
                || id.IndexOf("GreatEscape", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("Jailbreak", StringComparison.OrdinalIgnoreCase) >= 0)
                return _filters.HasFlag(EventFilters.Legacy);

            // Everything else → Seasonal
            return _filters.HasFlag(EventFilters.Seasonal);
        }

        // ── CoreSupport classification ────────────────────────────────

        private bool MatchesCoreFilter(string id)
        {
            // Re-Archaeology: DE_*
            if (id.StartsWith("DE_"))
                return _filters.HasFlag(EventFilters.ReArchaeology);

            // Horizons Cup (Classic Races / Sailing): CR_*
            if (id.StartsWith("CR_"))
                return _filters.HasFlag(EventFilters.HorizonsCup);

            // Auto Merge: several-times-daily auto-merge event windows (AutoMerge*)
            if (id.StartsWith("AutoMerge"))
                return _filters.HasFlag(EventFilters.AutoMerge);

            // Roll The Dice: CSE_Dinner* or *RollTheDice*
            if (id.StartsWith("CSE_Dinner") || id.IndexOf("RollTheDice", StringComparison.OrdinalIgnoreCase) >= 0)
                return _filters.HasFlag(EventFilters.RollTheDice);

            // Everything else (Builder events, etc.) → Uncategorised
            return _filters.HasFlag(EventFilters.Uncategorised);
        }

        // ── Leaderboard classification ────────────────────────────────

        private bool MatchesLeaderboardFilter(string id, string displayName)
        {
            if (id.IndexOf("Bonanza", StringComparison.OrdinalIgnoreCase) >= 0
                || displayName?.IndexOf("Bonanza", StringComparison.OrdinalIgnoreCase) >= 0)
                return _filters.HasFlag(EventFilters.Bonanza);

            if (id.IndexOf("BakeOff", StringComparison.OrdinalIgnoreCase) >= 0
                || displayName?.IndexOf("BakeOff", StringComparison.OrdinalIgnoreCase) >= 0
                || displayName?.IndexOf("Bake Off", StringComparison.OrdinalIgnoreCase) >= 0)
                return _filters.HasFlag(EventFilters.BakeOff);

            return _filters.HasFlag(EventFilters.Legacy);
        }

        private bool HasAnyFlag(EventFilters flags) => (_filters & flags) != 0;

        private static string Localize(string locId)
        {
            if (string.IsNullOrEmpty(locId))
                return null;

            var lang = MetaplaySDK.ActiveLanguage;
            if (lang?.Translations == null)
                return null;

            return lang.Translations.TryGetValue(TranslationId.FromString(locId), out var translation)
                ? translation
                : null;
        }

        protected override JsonSerializerSettings CreateSettings(SharedGameConfig config)
        {
            return new JsonSerializerSettings(base.CreateSettings(config))
            {
                Converters =
                {
                    new MergeMansionJsonConverter(config, Output, false),
                    new HotspotAwareStringEnumConverter()
                }
            };
        }
    }
}
