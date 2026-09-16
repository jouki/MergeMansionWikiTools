using GameLogic.Player.Rewards;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Support;

/// <summary>
/// Golden dump shape for one <see cref="PlayerReward"/> entry (hotspot <c>Rewards</c> /
/// <c>BonusRewards</c> / <c>DifficultyRewards</c>, event <c>EventLevels[].Rewards</c>): a
/// single-key JSON object whose key is the reward's exact runtime type name and whose value is
/// that object's own <c>[MetaMember]</c>s (own + inherited, in TagId order) — e.g.
/// <c>{"RewardExperience": {"Amount": 3, "Source": "HotspotCompleted"}}</c> or
/// <c>{"RewardItem": {"ItemDef": "SimpleBrownBox_01", "Amount": 1, "FromSupport": false,
/// "MergeBoardId": "Garage", "ForceOnTopOfPocket": false, "Source": "HotspotCompleted"}}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="RequirementModel"/>, no naming exception was found: every
/// <see cref="PlayerReward"/> subtype's discriminator in the golden dump is simply
/// <c>GetType().Name</c> verbatim (RewardItem, RewardCoins, RewardExperience,
/// RewardCooldownRemover, RewardDiamonds, RewardDecoration, RewardLayeredDecoration,
/// RewardCardCollectionPack, RewardCardCollectionInformantTip, RewardEnergy, RewardPet,
/// RewardActivateInfiniteEnergy, RewardSkipTime, ProgressionEventPerkReward — all confirmed
/// against <c>Dump 2/areas.json</c> hotspot <c>Rewards</c>/<c>BonusRewards</c> and
/// <c>events.json</c> <c>EventLevels[].Rewards</c>, 26.07.01). Members always include the base
/// <see cref="PlayerReward.Source"/> field (TagId 100) last. True <c>null</c> members (e.g.
/// <c>RewardItem.OverrideItemFeatures</c>/<c>OverridePoolTag</c>) are omitted entirely — that is
/// <see cref="MetaObjectWriter"/>'s own explicit null-check (fix round 1; the shared
/// <c>NullValueHandling.Ignore</c> serializer setting in <c>Core/DumpJson.cs</c> does NOT reach
/// this path, since <c>MetaObjectWriter</c> writes each property name manually rather than through
/// Newtonsoft's own reflection). A member that is a non-null wrapper whose own conversion happens
/// to render as JSON <c>null</c> — e.g. <c>RewardItem.MergeBoardId</c> being a non-null
/// <c>MergeBoardId</c> (StringId) with a null inner <c>Value</c>, 466/1782 golden entries — is NOT
/// treated the same way: the key is still written, with a <c>null</c> value.
/// </para>
/// <para>
/// <b>VERIFIED BY EXECUTION</b> (fix round 1's throwaway harness ran <see cref="From"/> against a
/// constructed <c>RewardItem</c> and diffed the output against the literal golden string):
/// <c>RewardItem</c> with a resolved <c>MergeBoardId</c> (<c>{"ItemDef": "SimpleBrownBox_01", ...,
/// "MergeBoardId": "Garage", ...}</c>) — this required <see cref="ConfigDefinitionConverter"/> to
/// be registered on the caller's serializer settings to resolve <c>ItemDef</c> to its item type
/// string (without it, <c>ItemDef</c> wrongly renders as <c>{"ConfigKey": ...}</c>, which is what
/// fix round 1 was about) — and <c>RewardItem</c> with a null-valued <c>MergeBoardId</c> (confirms
/// the null-vs-null-rendering-wrapper distinction above).
/// </para>
/// <para>
/// <b>Shape matched by source inspection only, not execution-verified:</b> every other confirmed
/// discriminator above (RewardCoins, RewardExperience, RewardCooldownRemover, RewardDiamonds,
/// RewardDecoration, RewardLayeredDecoration, RewardCardCollectionPack,
/// RewardCardCollectionInformantTip, RewardEnergy, RewardPet, RewardActivateInfiniteEnergy,
/// RewardSkipTime, ProgressionEventPerkReward) — each follows the exact same
/// <c>GetType().Name</c> + <see cref="MetaObjectPayload"/> mechanism already execution-verified via
/// <c>RewardItem</c>, but wasn't individually run against its own golden example.
/// </para>
/// <para>
/// Every other <c>PlayerReward</c> subtype under <c>GameLogic/Player/Rewards/*.cs</c> (e.g.
/// RewardBoultonLeaguePoints, RewardCurrencyBank, RewardDailyScoopPoints,
/// RewardCollectibleBoardEventProgress, RewardEventCharacter, RewardEventCurrency,
/// RewardEventPoints, RewardExtendGameEvents, RewardIAP, RewardItemForCollectibleBoardEvent,
/// RewardLevelUpMergeChain, RewardMysteryMachine*, RewardOfferContents, RewardOnFire,
/// RewardProgressionEventPoints, RewardShortLeaderboardEventStars, RewardSideBoardEventProgress,
/// RewardStartingValues, RewardUpgradableId/RewardUpgradableInfo/RewardUpgradableRewardData,
/// BaseProgressionEventPremiumIAPReward + its ProgressionEventPremiumIAPReward /
/// ProgressionPackEventPremiumIAPReward derivatives, EmptyReward, LinkWithNoReward,
/// LinkWithTimed12TraitVerificationReward, NegativeReward) has no example anywhere in the 26.07.01
/// golden dump — <see cref="From"/> maps them the same way (type name + full member set), which is
/// UNVERIFIED but consistent with every confirmed case. See task-4-report.md for the full list.
/// </para>
/// </remarks>
[JsonConverter(typeof(SingleKeyJsonConverter))]
public sealed class RewardModel : ISingleKeyJson
{
    public string Kind { get; }
    public object? Payload { get; }

    private RewardModel(string kind, object? payload)
    {
        Kind = kind;
        Payload = payload;
    }

    /// <summary>Builds a wrapper with an explicit kind/payload pair (bypassing subtype inference).</summary>
    public static RewardModel Of(string kind, object? payload) => new(kind, payload);

    /// <summary>Maps a <see cref="PlayerReward"/> instance to its golden JSON shape (see remarks above).</summary>
    public static RewardModel From(PlayerReward reward, IDumpLog? log = null, MetaRefSkip refSkip = MetaRefSkip.EmptyKey)
    {
        ArgumentNullException.ThrowIfNull(reward);
        var effectiveLog = log ?? ConsoleDumpLog.Instance;
        return new RewardModel(reward.GetType().Name, new MetaObjectPayload(reward, effectiveLog, refSkip));
    }
}
