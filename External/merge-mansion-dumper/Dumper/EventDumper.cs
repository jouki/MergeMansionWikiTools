// Modified by Jouki (2026) — EventFilters: per-event filtering by EventId prefix
// Replaces old EventCategories (per-config-type) with game-event-based filters
using System;
using System.Collections.Generic;
using System.Linq;
using Code.GameLogic.GameEvents;
using Code.GameLogic.GameEvents.SoloMilestone;
using GameLogic.Config;
using merge_mansion_dumper.Dumper.Base;
using merge_mansion_dumper.Dumper.Json;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

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
        RollTheDice = 1 << 6,
        GarageCleanup = 1 << 7,
        Mysteries = 1 << 8,
        BoultonLeague = 1 << 9,
        Legacy = 1 << 10,
        Uncategorised = 1 << 11,
        BakeOff = 1 << 12,
        Bonanza = 1 << 13,
        Others = 1 << 14,
        SoloMilestone = 1 << 15,
        All = LuckyCatch | LuckySnap | Seasonal | ReArchaeology | HorizonsCup
            | RollTheDice | GarageCleanup | Mysteries | BoultonLeague
            | Legacy | Uncategorised | BakeOff | Bonanza | Others
            | SoloMilestone
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
                        ["DisplayName"] = Localize(evt.DisplayName) ?? evt.DisplayName,
                        ["DisplayNameLocId"] = evt.DisplayName,
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

            // GarageCleanups → GarageCleanup filter.
            // PrefabsOverride ([MetaMember] private, ≈ the event's badge prefab) is already
            // serialized by the default resolver, so no extra projection is needed here.
            if (_filters.HasFlag(EventFilters.GarageCleanup))
                events["GarageCleanups"] = config.GarageCleanupEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // CoreSupportEvents — sub-filtered by EventId prefix.
            // AssetOverride/LocOverride ([MetaMember] private) would be surfaced by the default
            // resolver if set, but they are null for every current CSE event, so nothing to add.
            if (HasAnyFlag(EventFilters.ReArchaeology | EventFilters.HorizonsCup | EventFilters.RollTheDice | EventFilters.Uncategorised))
            {
                var filtered = config.CoreSupportEvents?.EnumerateAll()
                    .Where(x => MatchesCoreFilter(x.Key.ToString()))
                    .Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                if (filtered.Length > 0)
                    events["CoreSupportEvents"] = filtered;
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
                        ["DisplayName"] = Localize(evt.NameLocId) ?? evt.DisplayName,
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
                        ["DisplayName"] = Localize(stage.NameLocId),
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

            // Others: Shops, DailyTasks
            if (_filters.HasFlag(EventFilters.Others))
            {
                events["Shops"] = config.ShopEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                events["DailyTasks"] = config.DailyTasks?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();
                events["DailyTasksV2"] = config.DailyTasksV2?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>();
            }

            // EventLevels — resolved per-level data referenced by ProgressionEvent / Season Pass / etc.
            // ProgressionEventInfo only stores MetaRef<EventLevelInfo>, so patches that touch EventLevels
            // (e.g. WildItem_SeasonPass_01_B) don't show up in event dumps unless we serialize the
            // library itself. Always exported — covers Wild Item / Wild Card / any AB reward swap.
            events["EventLevels"] = config.EventLevels?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // DailyScoop — five interlinked libraries (Milestones / StandardObjectives /
            // SpecialObjectives / Days / Weeks). Wild Item AB-patch WildItem_DailyScoop_V2_01_B
            // touches DailyScoopStandardObjectives. Always exported for visibility.
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
                            ["DisplayName"] = Localize(evt.NameLocId) ?? evt.DisplayName,
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

            // Auto Merge: always excluded from dump
            if (id.StartsWith("AutoMerge"))
                return false;

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
                    new StringEnumConverter()
                }
            };
        }
    }
}
