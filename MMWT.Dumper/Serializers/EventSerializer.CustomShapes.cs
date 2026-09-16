using Code.GameLogic.GameEvents;
using Code.GameLogic.GameEvents.CardCollectionSupportingEvent;
using Code.GameLogic.GameEvents.SoloMilestone;
using GameLogic.Config.Shop.Items;
using GameLogic.MixABooster;
using GameLogic.ProgressivePacks;
using MergeMansionWikiTools.Dumper.Core;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// The event categories whose golden element is a hand-built object: their key order matches
/// neither TagId nor declaration order, several keys are computed rather than reflected, and — the
/// tell that they cannot come from either reflection path — they keep explicit <c>null</c>s
/// (<c>"ObjectiveParameter": null</c>, <c>"Recipes": null</c>, <c>"NameLocId": null</c>) that both
/// reflection paths drop. Each method returns an <see cref="OrderedMap"/> so the order is a
/// property of this code rather than of a dictionary implementation.
/// </summary>
public sealed partial class EventSerializer
{
    /// <summary>
    /// "Merge it Up!" pack events. Both loc ids are shared constants, not per-event config values,
    /// and <c>Description</c> is the localized start-popup text rather than the raw config stub.
    /// The referenced pack is inlined under <c>Pack</c>.
    /// </summary>
    public OrderedMap ProgressionPackEvent(ProgressionPackEventInfo evt)
    {
        var pack = evt.ProgressionPackId != null && _config.ProgressionPacks != null
            && _config.ProgressionPacks.TryGetValue(evt.ProgressionPackId, out var p) ? p : null;

        return new OrderedMap()
            .Add("ConfigKey", evt.ConfigKey)
            .Add("Name", LocOrKey(ProgressionPackNameLocId))
            .Add("NameLocId", ProgressionPackNameLocId)
            .Add("Description", LocOrKey(ProgressionPackDescriptionLocId))
            .Add("DescriptionLocId", ProgressionPackDescriptionLocId)
            .Add("ActivableParams", evt.ActivableParams)
            .Add("UnlockRequirement", evt.UnlockRequirement)
            .Add("GroupId", evt.GroupId)
            .Add("Priority", evt.Priority)
            .Add("PremiumIAP", evt.PremiumIAP)
            .Add("UseOfferId", evt.UseOfferId)
            .Add("OfferGroupId", evt.OfferGroupId)
            .Add("PlacementId", evt.PlacementId)
            .Add("CategoryInfo", evt.CategoryInfo)
            .Add("ProgressionPackId", evt.ProgressionPackId)
            .Add("Pack", pack == null ? null : ProgressionPack(pack));
    }

    /// <summary>
    /// A progression pack's three parallel arrays (<c>FreeOffers</c>, <c>PremiumOffers</c>,
    /// <c>LevelRequirements</c>) are zip-merged into one <c>Levels</c> array of
    /// <c>{Level, MergesRequired, FreeReward, PremiumReward}</c>, with <c>Level</c> 1-based. The
    /// length is the longest of the three, so a short array simply contributes nulls.
    /// </summary>
    public OrderedMap ProgressionPack(ProgressionPack pack)
    {
        var free = pack.FreeOffers;
        var premium = pack.PremiumOffers;
        var requirements = pack.LevelRequirements;
        var count = Math.Max(free?.Count ?? 0, Math.Max(premium?.Count ?? 0, requirements?.Count ?? 0));

        var levels = new List<OrderedMap>(count);
        for (var i = 0; i < count; i++)
        {
            levels.Add(new OrderedMap()
                .Add("Level", i + 1)
                .Add("MergesRequired", requirements != null && i < requirements.Count ? requirements[i] : null)
                .Add("FreeReward", free != null && i < free.Count ? free[i] : null)
                .Add("PremiumReward", premium != null && i < premium.Count ? premium[i] : null));
        }

        return new OrderedMap()
            .Add("ConfigKey", pack.ConfigKey)
            .Add("ObjectiveType", pack.ObjectiveType)
            .Add("ObjectiveParameter", pack.ObjectiveParameter)
            .Add("LevelCount", count)
            .Add("Levels", levels);
    }

    /// <summary>Clue Rush events; see <see cref="ResolveCardCollectionSupportingName"/> for the title chain.</summary>
    public OrderedMap CardCollectionSupportingEvent(CardCollectionSupportingEventInfo evt) =>
        new OrderedMap()
            .Add("ConfigKey", evt.ConfigKey)
            .Add("Name", ResolveCardCollectionSupportingName(evt))
            .Add("NameLocId", evt.NameLocId)
            .Add("Description", evt.Description)
            .Add("ActivableParams", evt.ActivableParams)
            .Add("CategoryInfo", evt.CategoryInfo)
            .Add("GroupId", evt.GroupId)
            .Add("Priority", evt.Priority);

    public OrderedMap BoultonLeagueEvent(BoultonLeagueEventInfo evt) =>
        new OrderedMap()
            .Add("EventId", evt.EventId)
            .Add("NameLocId", evt.NameLocId)
            .Add("Name", ResolveNameFromLocId(evt.NameLocId, evt.DisplayName))
            .Add("Description", evt.Description)
            .Add("ActivableParams", evt.ActivableParams)
            .Add("CategoryInfo", evt.CategoryInfo)
            .Add("GroupId", evt.GroupId)
            .Add("MatchmakingAlgorithm", evt.MatchmakingAlgorithm)
            .Add("JoinAutomatically", evt.JoinAutomatically)
            .Add("StageRefs", evt.StageRefs);

    public OrderedMap BoultonLeagueStage(BoultonLeagueStageInfo stage) =>
        new OrderedMap()
            .Add("StageId", stage.StageId)
            .Add("NameLocId", stage.NameLocId)
            .Add("Name", ResolveNameFromLocId(stage.NameLocId, null))
            .Add("DemotionScoreThreshold", stage.DemotionScoreThreshold)
            .Add("PromotionScoreThreshold", stage.PromotionScoreThreshold)
            .Add("FinishReward", stage.FinishReward)
            .Add("PromotionReward", stage.PromotionReward)
            .Add("LeaderboardPlacementRewardLevelRefs", stage.LeaderboardPlacementRewardLevelRefs);

    public OrderedMap SoloMilestoneEvent(SoloMilestoneEventInfo evt) =>
        new OrderedMap()
            .Add("ConfigKey", evt.ConfigKey)
            .Add("NameLocId", evt.NameLocId)
            .Add("Name", ResolveNameFromLocId(evt.NameLocId, evt.DisplayName))
            .Add("Description", evt.Description)
            .Add("ActivableParams", evt.ActivableParams)
            .Add("CategoryInfo", evt.CategoryInfo)
            .Add("GroupId", evt.GroupId)
            .Add("Theme", evt.Theme)
            .Add("Priority", evt.Priority)
            .Add("TokenSpawnsEnabled", evt.TokenSpawnsEnabled)
            .Add("Milestones", evt.Milestones)
            .Add("UnlockRequirement", evt.UnlockRequirement);

    /// <summary>
    /// Mix a Booster events. <c>Name</c> and <c>AssetOverride</c> are constants (the config type
    /// carries neither), and <c>Recipes</c> is the public accessor rather than the private
    /// <c>RecipeRefs</c> member — it is null throughout the corpus.
    /// </summary>
    public OrderedMap MixABoosterEvent(MixABoosterEventInfo evt) =>
        new OrderedMap()
            .Add("ConfigKey", evt.ConfigKey)
            .Add("Name", LocOrKey(MixABoosterNameLocId))
            .Add("Description", evt.Description)
            .Add("ActivableParams", evt.ActivableParams)
            .Add("CategoryInfo", evt.CategoryInfo)
            .Add("UnlockRequirement", evt.UnlockRequirement)
            .Add("PlacementId", evt.PlacementId)
            .Add("AssetOverride", MixABoosterAssetOverride)
            .Add("InitialIngredients", evt.InitialIngredients)
            .Add("Recipes", evt.Recipes);

    /// <summary>
    /// Mix a Booster recipes. <c>RequiredIngredients</c> is the public <c>IngredientIds</c>
    /// accessor, not the private member of the same conceptual name — golden writes it as
    /// <c>null</c> on every recipe in the corpus, which the private member would not produce.
    /// </summary>
    public OrderedMap MixABoosterRecipe(MixABoosterRecipe recipe) =>
        new OrderedMap()
            .Add("ConfigKey", recipe.ConfigKey)
            .Add("RequiredIngredients", recipe.IngredientIds)
            .Add("Reward", recipe.Reward)
            .Add("IsSecret", recipe.IsSecret);

    /// <summary>
    /// A progression-event perk, keyed by its perk id inside <c>Track1PerkData</c>/<c>Track2PerkData</c>:
    /// the union's <c>[MetaSerializableDerived]</c> type code doubles as the
    /// <see cref="ProgressionEventPerkType"/> value golden writes under <c>Type</c>, followed by the
    /// variant's own members. Free-daily-currency perks additionally report the gem amount their
    /// shop item grants.
    /// </summary>
    private OrderedMap PerkData(ProgressionEventPerk perk)
    {
        var map = new OrderedMap().Add("Type", PerkType(perk));
        foreach (var (name, _, get) in MetaObjectWriter.Members(perk))
        {
            object? value;
            try { value = get(); }
            catch (Exception ex)
            {
                _log.Warn($"{perk.GetType().Name}.{name}: {ex.GetType().Name} — {ex.Message} — skipped");
                continue;
            }
            if (value != null) map.Add(name, value);
        }

        if (perk is ProgressionEventFreeDailyCurrencyPerk currency)
            map.Add("Gems", GemsInShopItem(currency.ShopItemId));
        return map;
    }

    private static object PerkType(ProgressionEventPerk perk)
    {
        var code = perk.GetType().GetCustomAttributes(typeof(Metaplay.Core.Model.MetaSerializableDerivedAttribute), false)
            .Cast<Metaplay.Core.Model.MetaSerializableDerivedAttribute>().FirstOrDefault()?.TypeCode ?? 0;
        return (ProgressionEventPerkType)code;
    }

    /// <summary>The gem count a diamond shop item grants, or null when the item is not a gem pack.</summary>
    private long? GemsInShopItem(GameLogic.ShopItemId? shopItemId)
    {
        if (shopItemId == null || _config.ShopItems == null) return null;
        if (!_config.ShopItems.TryGetValue(shopItemId, out var shopItem) || shopItem == null) return null;
        return shopItem.ActualItem is DiamondItem diamonds ? diamonds.Amount : null;
    }

    /// <summary>Localized text for a key, or the key itself when the language file has no entry.</summary>
    private string LocOrKey(string key) => Loc.SafeLoc(() => LocMan.Get(key), key, _log, key);
}
