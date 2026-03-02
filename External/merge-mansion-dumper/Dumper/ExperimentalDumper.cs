using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Code.GameLogic.GameEvents;
using Code.GameLogic.GameEvents.DailyChallenges.Data;
using GameLogic.Config;
using GameLogic.Player.Rewards;
using GameLogic.Story;
using merge_mansion_dumper.Dumper.Base;
using merge_mansion_dumper.Dumper.Json;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace merge_mansion_dumper.Dumper
{
    public class ExperimentalDumper : JsonDumper<IDictionary<string, object>>
    {
        /// <summary>
        /// Writes each section as a separate JSON file into the given directory.
        /// Returns list of (sectionName, filePath) for successfully written files.
        /// </summary>
        public List<(string Section, string Path)> WriteIndividualFiles(string outputDir, SharedGameConfig config)
        {
            Directory.CreateDirectory(outputDir);

            var sections = Dump(config);
            var settings = CreateSettings(config);
            var written = new List<(string, string)>();

            foreach (var (key, value) in sections)
            {
                var data = new { CreatedAt = config.ArchiveCreatedAt, Data = value };
                var filePath = System.IO.Path.Combine(outputDir, $"{key}.json");
                File.WriteAllText(filePath, JsonConvert.SerializeObject(data, Formatting.Indented, settings));
                written.Add((key, filePath));
            }

            return written;
        }

        public override IDictionary<string, object> Dump(SharedGameConfig config)
        {
            var result = new Dictionary<string, object>();

            // ── 1. DigEvent (Archaeology) ──────────────────────────────

            result["DigEvents"] = config.DigEvents?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["DigEventBoards"] = config.DigEventBoards?.EnumerateAll().Select(x =>
            {
                var b = (DigEventBoards)x.Value;
                return new Dictionary<string, object>
                {
                    ["BoardId"] = b.BoardId?.ToString(),
                    ["BoardWidth"] = b.BoardWidth,
                    ["BoardHeight"] = b.BoardHeight,
                    ["CellSize"] = b.CellSize,
                    ["Treasures"] = b.Treasures?.Select(t => t?.ToString()).ToArray(),
                    ["BoardReward"] = b.BoardReward,
                    ["CompensationChance"] = b.CompensationChance,
                };
            }).ToArray() ?? Array.Empty<object>();

            result["DigEventItems"] = config.DigEventItemInfos?.EnumerateAll().Select(x =>
            {
                var item = (DigEventItemInfo)x.Value;
                return new Dictionary<string, object>
                {
                    ["ItemId"] = item.ItemId?.ToString(),
                    ["AssetId"] = item.AssetId,
                    ["GoesMuseum"] = item.GoesMuseum,
                    ["CanBeShiny"] = item.CanBeShiny,
                    ["Shape"] = item.Coordinates?.Select(c => new[] { c.Item1, c.Item2 }).ToArray(),
                    ["Weight"] = item.Weight,
                    ["MuseumSize"] = $"{item.MuseumItemWidth}x{item.MuseumItemHeight}",
                    ["MuseumItemRotation"] = item.MuseumItemRotation.ToString(),
                };
            }).ToArray() ?? Array.Empty<object>();

            result["DigEventMuseumShelves"] = config.DigEventShelves?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["DigEventMuseumCollections"] = config.DigEventMuseumCollections?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["DigEventShinyProgression"] = config.DigEventShinyProgression?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // ── 2. BoultonLeague ────────────────────────────────────────

            result["BoultonLeagueEvents"] = config.BoultonLeagueEvents?.EnumerateAll().Select(x =>
            {
                var evt = (BoultonLeagueEventInfo)x.Value;
                return new Dictionary<string, object>
                {
                    ["EventId"] = evt.EventId?.ToString(),
                    ["NameLocId"] = evt.NameLocId,
                    ["DisplayName"] = Localize(evt.NameLocId) ?? evt.DisplayName,
                    ["Description"] = evt.Description,
                    ["GroupId"] = evt.GroupId?.ToString(),
                    ["MatchmakingAlgorithm"] = evt.MatchmakingAlgorithm.ToString(),
                    ["JoinAutomatically"] = evt.JoinAutomatically,
                    ["StageRefs"] = evt.StageRefs,
                    ["ActivableParams"] = evt.ActivableParams,
                    ["CategoryInfo"] = evt.CategoryInfo,
                };
            }).ToArray() ?? Array.Empty<object>();

            result["BoultonLeagueStages"] = config.BoultonLeagueStages?.EnumerateAll().Select(x =>
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

            // ── 3. Dialogues ────────────────────────────────────────────

            result["Dialogues"] = config.DialogItems?.EnumerateAll().Select(x =>
            {
                var d = (DialogItemInfo)x.Value;
                return new Dictionary<string, object>
                {
                    ["DialogItemId"] = d.DialogItemId?.ToString(),
                    ["LocalizationId"] = d.LocalizationId,
                    ["Text"] = Localize(d.LocalizationId),
                    ["DialogMode"] = d.DialogMode.ToString(),
                    ["LeftCharacter"] = d.LeftCharacter.ToString(),
                    ["LeftCharacterState"] = d.LeftCharacterState.ToString(),
                    ["LeftSpeaks"] = d.LeftSpeaks,
                    ["RightCharacter"] = d.RightCharacter.ToString(),
                    ["RightCharacterState"] = d.RightCharacterState.ToString(),
                    ["RightSpeaks"] = d.RightSpeaks,
                    ["WaitConfirmation"] = d.WaitConfirmation,
                };
            }).ToArray() ?? Array.Empty<object>();

            result["DialogueCharacters"] = config.DialogueCharacters?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["CollectibleDialogues"] = config.CollectibleDialoguesInfo?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // ── 4. Pets ─────────────────────────────────────────────────

            result["Pets"] = config.PetInfos?.EnumerateAll().Select(x =>
            {
                var pet = (PetInfo)x.Value;
                return new Dictionary<string, object>
                {
                    ["PetId"] = pet.ConfigKey?.ToString(),
                    ["UnlockHeaderLocId"] = pet.UnlockHeaderLocId,
                    ["UnlockHeader"] = Localize(pet.UnlockHeaderLocId),
                    ["UnlockDescLocId"] = pet.UnlockDescLocId,
                    ["UnlockDesc"] = Localize(pet.UnlockDescLocId),
                    ["InfoHeaderLocId"] = pet.InfoHeaderLocId,
                    ["InfoHeader"] = Localize(pet.InfoHeaderLocId),
                    ["InfoDescLocId"] = pet.InfoDescLocId,
                    ["InfoDesc"] = Localize(pet.InfoDescLocId),
                    ["SelectionHeaderLocId"] = pet.SelectionHeaderLocId,
                    ["SelectionHeader"] = Localize(pet.SelectionHeaderLocId),
                    ["SelectionDescriptionLocId"] = pet.SelectionDescriptionLocId,
                    ["SelectionDescription"] = Localize(pet.SelectionDescriptionLocId),
                    ["Decoration"] = pet.Decoration?.ToString(),
                    ["AssetPackId"] = pet.AssetPackId?.ToString(),
                };
            }).ToArray() ?? Array.Empty<object>();

            // ── 5. Offers + InAppProducts ───────────────────────────────

            result["Offers"] = config.Offers?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["InAppProducts"] = config.InAppProducts?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["OfferGroups"] = config.OfferGroups?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            // ── 6. Energy Settings ──────────────────────────────────────

            result["EnergySettings"] = config.EnergySettings?.EnumerateAll().Select(x =>
            {
                var e = (EnergySettingsConfig)x.Value;
                return new Dictionary<string, object>
                {
                    ["EnergyType"] = e.ConfigKey.ToString(),
                    ["MaxRechargeAmount"] = e.MaxRechargeAmount,
                    ["DefaultUnitRestoreDuration"] = e.DefaultUnitRestoreDuration,
                };
            }).ToArray() ?? Array.Empty<object>();

            // ── 7. DailyChallenges ──────────────────────────────────────

            result["DailyChallengesWeeks"] = config.DailyChallengesWeeks?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["DailyChallengesDays"] = config.DailyChallengesDays?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["DailyChallengesMinigames"] = config.DailyChallengesMinigames?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["DailyChallengesStandardObjectives"] = config.DailyChallengesStandardObjectives?.EnumerateAll().Select(x =>
            {
                var obj = (DailyChallengesStandardObjectiveData)x.Value;
                return new Dictionary<string, object>
                {
                    ["ConfigKey"] = obj.ConfigKey?.ToString(),
                    ["LocId"] = obj.LocId,
                    ["DisplayName"] = Localize(obj.LocId),
                    ["ObjectiveType"] = obj.ObjectiveType.ToString(),
                    ["ObjectiveRequirement"] = obj.ObjectiveRequirement,
                    ["ObjectiveParameter"] = obj.ObjectiveParameter,
                    ["OrderPriority"] = obj.OrderPriority,
                    ["RewardsPoolData"] = obj.RewardsPoolData,
                };
            }).ToArray() ?? Array.Empty<object>();

            result["DailyChallengesSpecialObjectives"] = config.DailyChallengesSpecialObjectives?.EnumerateAll().Select(x =>
            {
                var obj = (DailyChallengesSpecialObjectiveData)x.Value;
                return new Dictionary<string, object>
                {
                    ["ConfigKey"] = obj.ConfigKey?.ToString(),
                    ["LocId"] = obj.LocId,
                    ["DisplayName"] = Localize(obj.LocId),
                    ["ObjectiveType"] = obj.ObjectiveType.ToString(),
                    ["ObjectiveRequirement"] = obj.ObjectiveRequirement,
                    ["ObjectiveGroup"] = obj.ObjectiveGroup,
                    ["ObjectiveParameter"] = obj.ObjectiveParameter,
                    ["RewardsPoolData"] = obj.RewardsPoolData,
                };
            }).ToArray() ?? Array.Empty<object>();

            result["DailyChallengesMilestones"] = config.DailyChallengesMilestones?.EnumerateAll()
                .Select(x => x.Value).ToArray() ?? Array.Empty<object>();

            result["DailyChallengesEventSettings"] = config.DailyChallengesEventSettings;

            return result;
        }

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
