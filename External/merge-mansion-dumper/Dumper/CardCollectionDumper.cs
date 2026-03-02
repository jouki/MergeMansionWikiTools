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
