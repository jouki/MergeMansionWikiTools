using Code.GameLogic.GameEvents;
using GameLogic.CardCollection;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;

namespace MergeMansionWikiTools.Dumper.Dumpers;

/// <summary>
/// Produces <c>card_collection.json</c>: the Clue Rush card set (cards, sets, envelopes, evidence
/// boxes) plus the complete card-drop pipeline behind an envelope open.
/// <para>
/// The file is 12 hand-built sections rather than a reflection dump, because most of it is a
/// projection: <c>DisplayName</c> is the localized <c>NameLocId</c>, the three weighted-roll
/// sections carry a computed <c>Percent</c> next to each weight, and
/// <c>CardActivations.ActivationConfigByCardStars</c> is a PRIVATE member no default serializer
/// would emit at all. Sections that are not projected (<c>EvidenceBoxes</c>,
/// <c>DuplicateRewards</c>, <c>Balance</c>, <c>SupportingEvents</c>) are handed to the shared
/// converter stack as-is.
/// </para>
/// <para>
/// Drop pipeline, for orientation: <c>Events[].BalanceId → Balance[].*ActivationIds</c>; one pack
/// open = the <c>FixedCardsStars</c> guaranteed cards plus <c>RandomRolls</c> weighted star rolls
/// (<c>PackActivations</c>) → per star a weighted hidden-rarity roll
/// (<c>HiddenRarityActivations</c>) → per (star, rarity) a weighted set roll
/// (<c>SetActivations</c>) → per set the active card pool (<c>CardActivations</c>).
/// </para>
/// </summary>
public sealed class CardCollectionDumper
{
    private readonly IDumpLog _log;

    public CardCollectionDumper(IDumpLog? log = null) => _log = log ?? ConsoleDumpLog.Instance;

    public string Dump(SharedGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var data = BuildData(config);
        var settings = DumpConverters.MainFileSettings(config, _log, MetaRefSkip.EmptyKey,
            new CardCollectionSerializer(config, _log),
            // Cards / Packs / EvidenceBoxes expand their ItemDef into the full item object, which is
            // byte-identical to the same item in chain_item_odds.json — so the chain serializer owns
            // that shape here too. dropsAsPercent matches what the legacy dumper asked for; no odds
            // table appears anywhere in this file, so the flag is unobservable.
            new ChainSerializer(config, dropsAsPercent: false, _log));

        var json = DumpJson.Serialize(data, config.ArchiveCreatedAt, DumpFileKind.Main, settings);
        DumpConverters.LogRequirementSummary(settings, _log, "card_collection.json");
        return json;
    }

    private Dictionary<string, object?> BuildData(SharedGameConfig config) => new()
    {
        ["Cards"] = config.CardCollectionCardInfos?.EnumerateAll().Select(x =>
        {
            var card = (CardCollectionCardInfo)x.Value;
            return new Dictionary<string, object?>
            {
                ["ConfigKey"] = card.ConfigKey?.Value,
                ["Stars"] = card.Stars.ToString(),
                ["IsSpecial"] = card.IsSpecial,
                ["NameLocId"] = card.NameLocId,
                ["DisplayName"] = Loc.Translate(card.NameLocId),
                ["AssetPackId"] = card.AssetPackId?.Value,
                ["ItemDef"] = CardCollectionSerializer.Expand(config, card.ItemDef),
            };
        }).ToArray() ?? Array.Empty<object>(),

        ["CardSets"] = config.CardCollectionCardSetInfos?.EnumerateAll().Select(x =>
        {
            var set = (CardCollectionCardSetInfo)x.Value;
            return new Dictionary<string, object?>
            {
                ["ConfigKey"] = set.ConfigKey?.Value,
                ["NameLocId"] = set.NameLocId,
                ["DisplayName"] = Loc.Translate(set.NameLocId),
                ["AssetPackId"] = set.AssetPackId?.Value,
                ["CardsIds"] = set.CardsIds?.Select(c => c?.Value).ToArray(),
                ["Rewards"] = set.Rewards,
            };
        }).ToArray() ?? Array.Empty<object>(),

        ["Packs"] = config.CardCollectionPackInfos?.EnumerateAll().Select(x =>
        {
            var pack = (CardCollectionPackInfo)x.Value;
            return new Dictionary<string, object?>
            {
                ["ConfigKey"] = pack.ConfigKey?.Value,
                ["PackStars"] = pack.PackStars,
                ["NameLocId"] = pack.NameLocId,
                ["DisplayName"] = Loc.Translate(pack.NameLocId),
                ["AssetPackId"] = pack.AssetPackId?.Value,
                ["PocketConversionReward"] = pack.PocketConversionReward,
                ["ItemDef"] = CardCollectionSerializer.Expand(config, pack.ItemDef),
            };
        }).ToArray() ?? Array.Empty<object>(),

        ["EvidenceBoxes"] = config.CardCollectionEvidenceBoxes?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
        ["DuplicateRewards"] = config.CardCollectionDuplicateCardRewards?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
        ["Balance"] = config.CardCollectionBalanceInfos?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),

        ["PackActivations"] = config.CardCollectionPackActivationInfos?.EnumerateAll().Select(x =>
        {
            var act = (CardCollectionPackActivationInfo)x.Value;
            var starTotal = act.RandomCardsStars?.Sum(r => r.Weight.Double) ?? 0;
            return new Dictionary<string, object?>
            {
                ["ConfigKey"] = act.ConfigKey?.Value,
                ["PackId"] = act.PackId?.Value,
                ["CardsToRollFirst"] = act.CardsToRollFirst.ToString(),
                ["RandomType"] = act.RandomType.ToString(),
                ["RandomRolls"] = act.RandomRolls,
                ["FixedCardsStars"] = act.FixedCardsStars?.Select(f => new Dictionary<string, object?>
                {
                    ["Stars"] = f.Stars.ToString(),
                    ["Amount"] = f.Amount,
                }).ToArray(),
                ["RandomCardsStars"] = act.RandomCardsStars?.Select(r => new Dictionary<string, object?>
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
            return new Dictionary<string, object?>
            {
                ["ConfigKey"] = act.ConfigKey?.Value,
                ["CardStars"] = act.CardStars.ToString(),
                ["RandomType"] = act.RandomType.ToString(),
                ["RandomHiddenRarities"] = act.RandomHiddenRarities?.Select(r => new Dictionary<string, object?>
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
            return new Dictionary<string, object?>
            {
                ["ConfigKey"] = act.ConfigKey?.Value,
                ["CardStars"] = act.CardStars.ToString(),
                ["HiddenRarity"] = act.HiddenRarity.ToString(),
                ["RandomType"] = act.RandomType.ToString(),
                ["RandomSetIds"] = act.RandomSetIds?.Select(r => new Dictionary<string, object?>
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
            // ActivationConfigByCardStars is a private [MetaMember]: Newtonsoft's public-only
            // reflection drops it silently, so it is read by name instead.
            var byStars = MetaObjectWriter.GetMember(act, "ActivationConfigByCardStars", _log)
                as Dictionary<CardStars, ActivationConfig>;
            return new Dictionary<string, object?>
            {
                ["ConfigKey"] = act.ConfigKey?.Value,
                ["CardSetId"] = act.CardSetId?.Value,
                ["ActivationConfigByCardStars"] = byStars?.ToDictionary(
                    kv => kv.Key.ToString(),
                    kv => (object?)kv.Value?.ConfigByHiddenRarity?.ToDictionary(
                        hv => hv.Key.ToString(),
                        hv => (object?)new Dictionary<string, object?>
                        {
                            ["Cards"] = hv.Value?.Cards,
                            ["Min"] = hv.Value?.Min,
                            ["Max"] = hv.Value?.Max,
                        })),
            };
        }).ToArray() ?? Array.Empty<object>(),

        ["Events"] = config.TemporaryCardCollectionEvents?.EnumerateAll().Select(x =>
        {
            var evt = (TemporaryCardCollectionEventInfo)x.Value;
            return new Dictionary<string, object?>
            {
                ["ConfigKey"] = evt.ConfigKey?.Value,
                ["NameLocId"] = evt.NameLocId,
                ["DisplayName"] = Loc.Translate(evt.NameLocId),
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

    /// <summary>
    /// One weighted-roll entry's normalized share, in percent, rounded to 4 decimals — the shape the
    /// wiki's "chance per clue" tables consume. An all-zero weight list yields 0. Public so the
    /// rounding rule can be unit-tested without a loaded config.
    /// </summary>
    public static double Percent(double weight, double total)
        => total > 0 ? Math.Round(weight / total * 100.0, 4) : 0.0;
}
