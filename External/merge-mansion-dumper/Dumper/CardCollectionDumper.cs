using System;
using System.Collections.Generic;
using System.Linq;
using Code.GameLogic.GameEvents;
using GameLogic.CardCollection;
using GameLogic.Config;
using merge_mansion_dumper.Dumper.Base;
using merge_mansion_dumper.Dumper.Json;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace merge_mansion_dumper.Dumper
{
    public class CardCollectionDumper : JsonDumper<IDictionary<string, object>>
    {
        public override IDictionary<string, object> Dump(SharedGameConfig config)
        {
            return new Dictionary<string, object>
            {
                ["Cards"] = config.CardCollectionCardInfos?.EnumerateAll().Select(x =>
                {
                    var card = (CardCollectionCardInfo)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = card.ConfigKey?.Value,
                        ["Stars"] = card.Stars.ToString(),
                        ["IsSpecial"] = card.IsSpecial,
                        ["NameLocId"] = card.NameLocId,
                        ["DisplayName"] = Localize(card.NameLocId),
                        ["AssetPackId"] = card.AssetPackId?.Value,
                        ["ItemDef"] = card.ItemDef,
                    };
                }).ToArray() ?? Array.Empty<object>(),

                ["CardSets"] = config.CardCollectionCardSetInfos?.EnumerateAll().Select(x =>
                {
                    var set = (CardCollectionCardSetInfo)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = set.ConfigKey?.Value,
                        ["NameLocId"] = set.NameLocId,
                        ["DisplayName"] = Localize(set.NameLocId),
                        ["AssetPackId"] = set.AssetPackId?.Value,
                        ["CardsIds"] = set.CardsIds?.Select(c => c?.Value).ToArray(),
                        ["Rewards"] = set.Rewards,
                    };
                }).ToArray() ?? Array.Empty<object>(),

                ["Packs"] = config.CardCollectionPackInfos?.EnumerateAll().Select(x =>
                {
                    var pack = (CardCollectionPackInfo)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = pack.ConfigKey?.Value,
                        ["PackStars"] = pack.PackStars,
                        ["NameLocId"] = pack.NameLocId,
                        ["DisplayName"] = Localize(pack.NameLocId),
                        ["AssetPackId"] = pack.AssetPackId?.Value,
                        ["PocketConversionReward"] = pack.PocketConversionReward,
                        ["ItemDef"] = pack.ItemDef,
                    };
                }).ToArray() ?? Array.Empty<object>(),

                ["EvidenceBoxes"] = config.CardCollectionEvidenceBoxes?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["DuplicateRewards"] = config.CardCollectionDuplicateCardRewards?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["Balance"] = config.CardCollectionBalanceInfos?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),

                // ── Card-drop odds pipeline (per balance case, referenced from Balance.*ActivationIds) ──
                // A pack open = FixedCardsStars guaranteed cards + RandomRolls weighted star rolls
                // (PackActivations) → per star a weighted hidden-rarity roll (HiddenRarityActivations)
                // → per (star, rarity) a weighted card-set roll (SetActivations) → per set the pool of
                // active cards (CardActivations). Weights are F32 — extracted via .Double explicitly,
                // the generic F32 serializer applies Math.Ceiling and would destroy fractions.
                ["PackActivations"] = config.CardCollectionPackActivationInfos?.EnumerateAll().Select(x =>
                {
                    var act = (CardCollectionPackActivationInfo)x.Value;
                    var starTotal = act.RandomCardsStars?.Sum(r => r.Weight.Double) ?? 0;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = act.ConfigKey?.Value,
                        ["PackId"] = act.PackId?.Value,
                        ["CardsToRollFirst"] = act.CardsToRollFirst.ToString(),
                        ["RandomType"] = act.RandomType.ToString(),
                        ["RandomRolls"] = act.RandomRolls,
                        ["FixedCardsStars"] = act.FixedCardsStars?.Select(f => new Dictionary<string, object>
                        {
                            ["Stars"] = f.Stars.ToString(),
                            ["Amount"] = f.Amount,
                        }).ToArray(),
                        ["RandomCardsStars"] = act.RandomCardsStars?.Select(r => new Dictionary<string, object>
                        {
                            ["Stars"] = r.Stars.ToString(),
                            ["Weight"] = r.Weight.Double,
                            ["Percent"] = Percent(r.Weight.Double, starTotal),
                            ["MinBetweenTwoSame"] = r.MinBetweenTwoSame,
                            ["MaxBetweenTwoSame"] = r.MaxBetweenTwoSame,
                        }).ToArray(),
                        ["InitialSequence"] = act.InitialSequence?.Select(c => c?.Value).ToArray(),
                    };
                }).ToArray() ?? Array.Empty<object>(),

                ["HiddenRarityActivations"] = config.CardCollectionHiddenRarityActivationInfos?.EnumerateAll().Select(x =>
                {
                    var act = (CardCollectionHiddenRarityActivationInfo)x.Value;
                    var rarityTotal = act.RandomHiddenRarities?.Sum(r => r.Weight.Double) ?? 0;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = act.ConfigKey?.Value,
                        ["CardStars"] = act.CardStars.ToString(),
                        ["RandomType"] = act.RandomType.ToString(),
                        ["RandomHiddenRarities"] = act.RandomHiddenRarities?.Select(r => new Dictionary<string, object>
                        {
                            ["HiddenRarity"] = r.HiddenRarity.ToString(),
                            ["Weight"] = r.Weight.Double,
                            ["Percent"] = Percent(r.Weight.Double, rarityTotal),
                            ["MinBetweenTwoSame"] = r.MinBetweenTwoSame,
                            ["MaxBetweenTwoSame"] = r.MaxBetweenTwoSame,
                        }).ToArray(),
                    };
                }).ToArray() ?? Array.Empty<object>(),

                ["SetActivations"] = config.CardCollectionSetActivationInfos?.EnumerateAll().Select(x =>
                {
                    var act = (CardCollectionSetActivationInfo)x.Value;
                    var setTotal = act.RandomSetIds?.Sum(r => r.Weight.Double) ?? 0;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = act.ConfigKey?.Value,
                        ["CardStars"] = act.CardStars.ToString(),
                        ["HiddenRarity"] = act.HiddenRarity.ToString(),
                        ["RandomType"] = act.RandomType.ToString(),
                        ["RandomSetIds"] = act.RandomSetIds?.Select(r => new Dictionary<string, object>
                        {
                            ["SetId"] = r.SetId?.Value,
                            ["Weight"] = r.Weight.Double,
                            ["Percent"] = Percent(r.Weight.Double, setTotal),
                            ["MinBetweenTwoSame"] = r.MinBetweenTwoSame,
                            ["MaxBetweenTwoSame"] = r.MaxBetweenTwoSame,
                        }).ToArray(),
                    };
                }).ToArray() ?? Array.Empty<object>(),

                ["CardActivations"] = config.CardCollectionCardActivationInfos?.EnumerateAll().Select(x =>
                {
                    var act = (CardCollectionCardActivationInfo)x.Value;
                    // ActivationConfigByCardStars is a private MetaMember — Newtonsoft drops it silently
                    var byStars = typeof(CardCollectionCardActivationInfo)
                        .GetProperty("ActivationConfigByCardStars",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?.GetValue(act) as Dictionary<CardStars, ActivationConfig>;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = act.ConfigKey?.Value,
                        ["CardSetId"] = act.CardSetId?.Value,
                        ["ActivationConfigByCardStars"] = byStars?.ToDictionary(
                            kv => kv.Key.ToString(),
                            kv => (object)(kv.Value?.ConfigByHiddenRarity?.ToDictionary(
                                hv => hv.Key.ToString(),
                                hv => (object)new Dictionary<string, object>
                                {
                                    ["Cards"] = hv.Value?.Cards,
                                    ["Min"] = hv.Value?.Min,
                                    ["Max"] = hv.Value?.Max,
                                }))),
                    };
                }).ToArray() ?? Array.Empty<object>(),

                ["Events"] = config.TemporaryCardCollectionEvents?.EnumerateAll().Select(x =>
                {
                    var evt = (TemporaryCardCollectionEventInfo)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["ConfigKey"] = evt.ConfigKey?.Value,
                        ["NameLocId"] = evt.NameLocId,
                        ["DisplayName"] = Localize(evt.NameLocId),
                        ["DisplayName_Config"] = evt.DisplayName,
                        ["Description"] = evt.Description,
                        ["CardSetIds"] = evt.CardSetIds?.Select(c => c?.Value).ToArray(),
                        ["BalanceId"] = evt.BalanceId?.Value,
                        ["Rewards"] = evt.Rewards,
                        ["PrestigeRewards"] = evt.PrestigeRewards,
                        ["ActivableParams"] = evt.ActivableParams,
                    };
                }).ToArray() ?? Array.Empty<object>(),

                ["SupportingEvents"] = config.CardCollectionSupportingEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
            };
        }

        // Normalized share of one weighted roll entry in percent (rounded to 4 decimals) —
        // the in-game "Chance per Clue" panels show these values rounded to whole percent.
        private static double Percent(double weight, double total)
            => total > 0 ? Math.Round(weight / total * 100.0, 4) : 0.0;

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
