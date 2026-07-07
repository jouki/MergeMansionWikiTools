using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Parses events.json and extracts Mystery Pass (SP_) progressions.
/// Uses JsonElement traversal (not deserialization), consistent with DataService.
/// </summary>
public class MysteryService
{
    private static readonly string MappingPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "mystery_item_mapping.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<MysteryEvent> Mysteries { get; private set; } = new();

    public async Task LoadAsync(string filePath)
    {
        Mysteries.Clear();

        await using var stream = File.OpenRead(filePath);
        var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        if (!root.TryGetProperty("Data", out var data)) return;
        if (!data.TryGetProperty("Progressions", out var progressions)) return;

        // Build EventLevels lookup. Dumper v0.20.60+ emits tier refs as plain key strings
        // (MetaRef.KeyObject); the resolved level data lives in Data.EventLevels[] indexed by
        // EventLevelId. Older dumps inlined resolved objects into the tier arrays.
        var eventLevelById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (data.TryGetProperty("EventLevels", out var eventLevels) && eventLevels.ValueKind == JsonValueKind.Array)
        {
            foreach (var lvl in eventLevels.EnumerateArray())
            {
                var id = GetString(lvl, "EventLevelId");
                if (!string.IsNullOrEmpty(id)) eventLevelById[id] = lvl;
            }
        }

        foreach (var prog in progressions.EnumerateArray())
        {
            var eventId = GetString(prog, "ProgressionEventId");
            if (string.IsNullOrEmpty(eventId) || !eventId.StartsWith("SP_")) continue;

            var derivedName = DeriveName(eventId);
            var mystery = new MysteryEvent
            {
                ProgressionEventId = eventId,
                Name = derivedName,
                RawJsonName = derivedName,
            };

            // Name from JSON (may be more readable than derived)
            var jsonName = GetString(prog, "Name");
            if (!string.IsNullOrEmpty(jsonName))
            {
                mystery.RawJsonName = jsonName;
                mystery.Name = StripSeasonPassPrefix(jsonName);
            }
            else
            {
                mystery.Name = StripSeasonPassPrefix(derivedName);
            }

            // Schedule.Start + Duration — nested inside ActivableParams
            if (prog.TryGetProperty("ActivableParams", out var activable)
                && activable.TryGetProperty("Schedule", out var sched))
            {
                var startStr = GetString(sched, "Start");
                if (!string.IsNullOrEmpty(startStr) && DateTime.TryParse(startStr, out var dt))
                    mystery.StartDate = dt;

                var durStr = GetString(sched, "Duration");
                if (!string.IsNullOrEmpty(durStr))
                    mystery.Duration = ParseDuration(durStr);
            }

            // Event item — plain number field "EventItem"
            mystery.EventItemNumericId = GetLong(prog, "EventItem");

            // Parse tiers — newer format has Track1/Track2 at top level;
            // older format has them nested inside V2Info, with PremiumEventLevelRefs as single paid tier
            mystery.FreeTier = ParseTier(prog, "FreeEventLevelRefs", eventLevelById);
            mystery.SilverTier = ParseTier(prog, "Track1EventLevelRefs", eventLevelById);
            mystery.GoldTier = ParseTier(prog, "Track2EventLevelRefs", eventLevelById);
            mystery.BonusTier = ParseTier(prog, "BonusEventLevelRefs", eventLevelById);

            // Fallback: check V2Info for Track1/Track2 (older dump format)
            if (prog.TryGetProperty("V2Info", out var v2Info) && GetBool(v2Info, "IsEnabled", true))
            {
                if (mystery.SilverTier.Count == 0 && mystery.GoldTier.Count == 0)
                {
                    mystery.SilverTier = ParseTier(v2Info, "Track1EventLevelRefs", eventLevelById);
                    mystery.GoldTier = ParseTier(v2Info, "Track2EventLevelRefs", eventLevelById);
                }
                // V2Info may have more Free levels than the top-level (e.g., 51 vs 46) — use the larger set
                var v2Free = ParseTier(v2Info, "FreeEventLevelRefs", eventLevelById);
                if (v2Free.Count > mystery.FreeTier.Count)
                    mystery.FreeTier = v2Free;
                if (mystery.BonusTier.Count == 0)
                    mystery.BonusTier = ParseTier(v2Info, "BonusEventLevelRefs", eventLevelById);
            }

            // Fallback: PremiumEventLevelRefs (even older — single premium tier, use as Silver)
            if (mystery.SilverTier.Count == 0 && mystery.GoldTier.Count == 0)
            {
                mystery.SilverTier = ParseTier(prog, "PremiumEventLevelRefs", eventLevelById);
            }

            // V1 recurring tiers (not present in V2)
            mystery.RecurringFreeTier = ParseTier(prog, "RecurringFreeEventLevelRefs", eventLevelById);
            mystery.RecurringPremiumTier = ParseTier(prog, "RecurringPremiumEventLevelRefs", eventLevelById);

            // HasZeroLevel — default true (matches V2 behavior)
            mystery.HasZeroLevel = GetBool(prog, "HasZeroLevel", true);

            // V2 detection: has both Silver and Gold tiers
            mystery.IsV2 = mystery.SilverTier.Count > 0 && mystery.GoldTier.Count > 0;

            // Perk data for level 0 paid tiers (embedded by dumper)
            mystery.Track1PerkData = ParsePerkData(prog, "Track1PerkData");
            mystery.Track2PerkData = ParsePerkData(prog, "Track2PerkData");

            // Detect type: if any reward across all tiers is a Pet → Pet mystery
            mystery.MysteryType = DetectMysteryType(mystery);

            Mysteries.Add(mystery);
        }

        // Sort by start date, newest first
        Mysteries.Sort((a, b) =>
        {
            if (a.StartDate == null && b.StartDate == null) return 0;
            if (a.StartDate == null) return 1;
            if (b.StartDate == null) return -1;
            return b.StartDate.Value.CompareTo(a.StartDate.Value);
        });
    }

    /// <summary>
    /// Strips the "Season Pass - " prefix from a mystery display name (case-insensitive).
    /// "Season Pass - Buzzing with Purpose" → "Buzzing with Purpose".
    /// Names without the prefix are returned unchanged.
    /// </summary>
    public static string StripSeasonPassPrefix(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        const string prefix = "Season Pass - ";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return name[prefix.Length..].TrimStart();
        return name;
    }

    /// <summary>
    /// Derives a human-readable name from the progression event ID.
    /// E.g., "SP_UdderlyAdorable" → "Udderly Adorable"
    /// </summary>
    private static string DeriveName(string eventId)
    {
        // Strip SP_ prefix
        var name = eventId.StartsWith("SP_") ? eventId[3..] : eventId;

        // Insert spaces before uppercase letters (PascalCase → "Pascal Case")
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                result.Append(' ');
            else if (i > 0 && char.IsUpper(name[i]) && i + 1 < name.Length && char.IsLower(name[i + 1])
                     && char.IsUpper(name[i - 1]))
                result.Append(' ');
            result.Append(name[i]);
        }

        // Replace underscores with spaces
        return result.ToString().Replace('_', ' ').Trim();
    }

    private static List<MysteryRewardLevel> ParseTier(JsonElement prog, string tierProperty,
        IReadOnlyDictionary<string, JsonElement> eventLevelById)
    {
        var levels = new List<MysteryRewardLevel>();

        if (!prog.TryGetProperty(tierProperty, out var tierArray)
            || tierArray.ValueKind != JsonValueKind.Array) return levels;

        int levelIndex = 0;
        foreach (var rawEl in tierArray.EnumerateArray())
        {
            // Tier ref may be: (a) a plain key string (dumper v0.20.60+ — resolve via
            // eventLevelById lookup), or (b) an inlined resolved object (older dumps).
            JsonElement levelEl;
            if (rawEl.ValueKind == JsonValueKind.String)
            {
                var key = rawEl.GetString() ?? "";
                if (!eventLevelById.TryGetValue(key, out levelEl))
                {
                    // Reference points to a missing event level — still emit an empty level
                    // so per-tier level counts stay consistent with the source.
                    levels.Add(new MysteryRewardLevel { Level = levelIndex, XpRequired = 0 });
                    levelIndex++;
                    continue;
                }
            }
            else if (rawEl.ValueKind == JsonValueKind.Object)
            {
                levelEl = rawEl;
            }
            else
            {
                levelIndex++;
                continue;
            }

            var level = new MysteryRewardLevel
            {
                Level = levelIndex,
                XpRequired = GetInt(levelEl, "RequiredPoints"),
            };
            levelIndex++;

            if (levelEl.TryGetProperty("Rewards", out var rewardsArray)
                && rewardsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var rewardEl in rewardsArray.EnumerateArray())
                {
                    var rewards = ParseRewards(rewardEl);
                    level.Rewards.AddRange(rewards);
                }
            }

            levels.Add(level);
        }

        return levels;
    }

    private static List<MysteryReward> ParseRewards(JsonElement rewardEl)
    {
        var rewards = new List<MysteryReward>();

        // RewardEnergy
        if (rewardEl.TryGetProperty("RewardEnergy", out var energy))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.Energy,
                Amount = GetInt(energy, "Amount", 1),
                EnergyType = GetString(energy, "EnergyType"),
            });
        }

        // RewardDiamonds
        if (rewardEl.TryGetProperty("RewardDiamonds", out var diamonds))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.Diamonds,
                Amount = GetInt(diamonds, "Amount", 1),
            });
        }

        // RewardCoins
        if (rewardEl.TryGetProperty("RewardCoins", out var coins))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.Coins,
                Amount = GetInt(coins, "Amount", 1),
            });
        }

        // RewardItem — ItemDef is a plain string (item type key), not an object
        if (rewardEl.TryGetProperty("RewardItem", out var item))
        {
            var itemKey = GetString(item, "ItemDef");
            if (string.IsNullOrEmpty(itemKey))
                itemKey = GetString(item, "ItemRef"); // older dump format
            var reward = new MysteryReward
            {
                Type = MysteryRewardType.Item,
                Amount = GetInt(item, "Amount", 1),
                ItemKey = itemKey,
            };

            // Parse hourglass duration override
            if (item.TryGetProperty("OverrideItemFeatures", out var overrides)
                && overrides.ValueKind == JsonValueKind.Object
                && overrides.TryGetProperty("TimeContainerInitialTime", out var timeContainer))
            {
                // MetaDuration serializes as a plain millisecond NUMBER in current dumps;
                // legacy dumps inlined an object with a Milliseconds member. TryGetProperty
                // on a number element throws, so branch on the value kind first.
                reward.DurationMs = timeContainer.ValueKind == JsonValueKind.Number
                    ? timeContainer.GetInt64()
                    : GetLong(timeContainer, "Milliseconds");
            }

            rewards.Add(reward);
        }

        // RewardDecoration — actual data is in DecorationRef.
        // Dumper v0.20.60+ emits MetaRef as raw key string (= KeyObject), older dumps had
        // nested object {DecorationId, DisplayName}. Handle both shapes.
        if (rewardEl.TryGetProperty("RewardDecoration", out var deco))
        {
            var reward = new MysteryReward
            {
                Type = MysteryRewardType.Decoration,
                Amount = 1,
            };

            if (deco.TryGetProperty("DecorationRef", out var decoRef))
            {
                if (decoRef.ValueKind == JsonValueKind.String)
                {
                    reward.DecorationId = decoRef.GetString();
                }
                else if (decoRef.ValueKind == JsonValueKind.Object)
                {
                    reward.DecorationId = GetString(decoRef, "DecorationId");
                    reward.DecorationName = GetString(decoRef, "DisplayName");
                }
            }

            rewards.Add(reward);
        }

        // RewardLayeredDecoration — Pet mysteries use this for gold decorations
        if (rewardEl.TryGetProperty("RewardLayeredDecoration", out var layered))
        {
            var reward = new MysteryReward
            {
                Type = MysteryRewardType.Decoration,
                Amount = 1,
            };

            if (layered.TryGetProperty("DecorationRef", out var layeredRef))
            {
                if (layeredRef.ValueKind == JsonValueKind.String)
                {
                    reward.DecorationId = layeredRef.GetString();
                }
                else if (layeredRef.ValueKind == JsonValueKind.Object)
                {
                    reward.DecorationId = GetString(layeredRef, "DecorationId");
                    reward.DecorationName = GetString(layeredRef, "DisplayName");
                }
            }

            rewards.Add(reward);
        }

        // RewardExperience
        if (rewardEl.TryGetProperty("RewardExperience", out var xp))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.Experience,
                Amount = GetInt(xp, "Amount", 1),
            });
        }

        // RewardCardCollectionPack
        if (rewardEl.TryGetProperty("RewardCardCollectionPack", out var card))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.CardPack,
                Amount = GetInt(card, "Amount", 1),
                CardPackId = GetString(card, "CardCollectionPackId"),
            });
        }

        // ProgressionEventPerkReward
        if (rewardEl.TryGetProperty("ProgressionEventPerkReward", out var perk))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.Perk,
                Amount = 1,
                PerkId = GetString(perk, "PerkId"),
            });
        }

        // RewardPet — PetRef.ConfigKey is the pet name
        if (rewardEl.TryGetProperty("RewardPet", out var pet))
        {
            var reward = new MysteryReward
            {
                Type = MysteryRewardType.Pet,
                Amount = 1,
            };

            if (pet.TryGetProperty("PetRef", out var petRef))
            {
                if (petRef.ValueKind == JsonValueKind.String)
                    reward.PetName = petRef.GetString();
                else if (petRef.ValueKind == JsonValueKind.Object)
                    reward.PetName = GetString(petRef, "ConfigKey");
            }

            rewards.Add(reward);
        }

        // RewardCardCollectionInformantTip
        if (rewardEl.TryGetProperty("RewardCardCollectionInformantTip", out var tip))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.InformantTip,
                Amount = 1,
                InformantTipCardId = GetString(tip, "CardId"),
            });
        }

        // RewardCooldownRemover — "Unlimited Production" booster.
        // Shape: {"Duration": 180000, "Source": "..."} — Duration in ms, no Amount field (always 1).
        // Removes producer cooldowns for the specified duration (in game UI shows as
        // "Unlimited Production" timer, despite the dialog FTUE wording).
        if (rewardEl.TryGetProperty("RewardCooldownRemover", out var cdr))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.CooldownRemover,
                Amount = 1,
                DurationMs = GetLong(cdr, "Duration"),
            });
        }

        // RewardActivateInfiniteEnergy — Unlimited Energy booster, auto-activates on claim
        // (distinct from the inventory item InfiniteEnergySmall_01 etc.).
        // Shape: {"Duration": 180000, "Source": "..."}.
        if (rewardEl.TryGetProperty("RewardActivateInfiniteEnergy", out var aie))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.ActivateInfiniteEnergy,
                Amount = 1,
                DurationMs = GetLong(aie, "Duration"),
            });
        }

        // RewardSkipTime — Time Skip booster (auto-applied on claim).
        // Shape: {"MergeBoardIds": [...], "DurationToSkip": 1800000, "Source": "..."}.
        if (rewardEl.TryGetProperty("RewardSkipTime", out var skip))
        {
            rewards.Add(new MysteryReward
            {
                Type = MysteryRewardType.SkipTime,
                Amount = 1,
                DurationMs = GetLong(skip, "DurationToSkip"),
            });
        }

        return rewards;
    }

    private static MysteryType DetectMysteryType(MysteryEvent mystery)
    {
        // Only RewardPet determines Pet type — both Standard and Pet have decorations
        var allTiers = mystery.FreeTier.Concat(mystery.SilverTier).Concat(mystery.GoldTier).Concat(mystery.BonusTier);
        foreach (var level in allTiers)
        {
            foreach (var r in level.Rewards)
            {
                if (r.Type == MysteryRewardType.Pet)
                {
                    mystery.PetName = r.PetName;
                    return MysteryType.Pet;
                }
            }
        }
        return MysteryType.Standard;
    }

    // ── Event item resolution ─────────────────────────────────────

    /// <summary>
    /// Resolves event item numeric IDs to item names using DataService.
    /// Maps to the chain display name (e.g., "Moo-La-La Accessories"), not the individual item name
    /// (e.g., "Bubblegum Bow"), because event items on the wiki are named after the chain.
    /// </summary>
    public void ResolveEventItems(DataService ds)
    {
        // Build NumericConfigKey → (ItemType, ChainDisplayName, PoolTag) lookup
        var lookup = new Dictionary<long, (string ItemType, string ChainName, string PoolTag)>();
        foreach (var chain in ds.Chains)
        {
            foreach (var item in chain.Items)
            {
                if (!string.IsNullOrEmpty(item.NumericConfigKey)
                    && long.TryParse(item.NumericConfigKey, out var numId))
                {
                    lookup.TryAdd(numId, (item.ItemType, chain.DisplayName, chain.PoolTag));
                }
            }
        }

        foreach (var mystery in Mysteries)
        {
            if (mystery.EventItemNumericId != 0
                && lookup.TryGetValue(mystery.EventItemNumericId, out var match))
            {
                mystery.EventItemType = match.ItemType;
                mystery.EventItemName = match.ChainName;
                mystery.EventItemPoolTag = match.PoolTag;
            }
        }
    }

    /// <summary>
    /// Resolves reward item names. Priority: manual override → wiki mapping → DataService → raw key.
    /// </summary>
    public void ResolveRewardItems(DataService ds, WikiMappingCache? wikiMapping, MysteryItemMapping? overrides)
    {
        foreach (var mystery in Mysteries)
        {
            var allTiers = mystery.FreeTier.Concat(mystery.SilverTier).Concat(mystery.GoldTier).Concat(mystery.BonusTier);
            foreach (var level in allTiers)
            {
                foreach (var reward in level.Rewards)
                {
                    if (reward.Type != MysteryRewardType.Item || string.IsNullOrEmpty(reward.ItemKey))
                        continue;

                    // Priority 1: manual override
                    if (overrides?.Overrides.TryGetValue(reward.ItemKey, out var overrideName) == true)
                    {
                        reward.ItemDisplayName = overrideName;
                        continue;
                    }

                    // Priority 2: wiki mapping
                    if (wikiMapping?.Mappings.TryGetValue(reward.ItemKey, out var wikiEntry) == true
                        && !string.IsNullOrEmpty(wikiEntry.Name))
                    {
                        reward.ItemDisplayName = wikiEntry.Name;
                        if (wikiEntry.Level.HasValue)
                            reward.ItemLevel = wikiEntry.Level.Value;
                        continue;
                    }

                    // Priority 3: DataService — prefer CHAIN name (e.g. "Gardening Tools" for
                    // GardenTools_06) over individual item name ("Knife"). Wiki templates link
                    // to the chain page, not to per-level item pages. Level preserved from
                    // ItemLevels so {{Item/Group|Gardening Tools|6}} renders the correct level.
                    if (ds.ItemToChainName.TryGetValue(reward.ItemKey, out var dsChain)
                        && !string.IsNullOrEmpty(dsChain))
                    {
                        reward.ItemDisplayName = dsChain;
                        if (ds.ItemLevels.TryGetValue(reward.ItemKey, out var dsLevel))
                            reward.ItemLevel = dsLevel;
                        continue;
                    }
                    if (ds.ItemNames.TryGetValue(reward.ItemKey, out var dsName))
                    {
                        reward.ItemDisplayName = dsName;
                        if (ds.ItemLevels.TryGetValue(reward.ItemKey, out var dsLevel))
                            reward.ItemLevel = dsLevel;
                        continue;
                    }

                    // Fallback: raw key
                    reward.ItemDisplayName = reward.ItemKey;
                }
            }
        }
    }

    // ── Item mapping persistence ──────────────────────────────────

    public static MysteryItemMapping LoadMapping()
    {
        try
        {
            if (File.Exists(MappingPath))
            {
                var json = File.ReadAllText(MappingPath);
                return JsonSerializer.Deserialize<MysteryItemMapping>(json, JsonOpts)
                       ?? new MysteryItemMapping();
            }
        }
        catch { /* Return empty */ }
        return new MysteryItemMapping();
    }

    public static void SaveMapping(MysteryItemMapping mapping)
    {
        try
        {
            var json = JsonSerializer.Serialize(mapping, JsonOpts);
            File.WriteAllText(MappingPath, json);
        }
        catch { /* Silently fail */ }
    }

    /// <summary>
    /// Collects all unique item keys used across all mystery rewards.
    /// </summary>
    public Dictionary<string, string?> GetAllRewardItemKeys()
    {
        var items = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var mystery in Mysteries)
        {
            var allTiers = mystery.FreeTier.Concat(mystery.SilverTier).Concat(mystery.GoldTier).Concat(mystery.BonusTier);
            foreach (var level in allTiers)
            {
                foreach (var reward in level.Rewards)
                {
                    if (reward.Type == MysteryRewardType.Item && !string.IsNullOrEmpty(reward.ItemKey))
                        items.TryAdd(reward.ItemKey, reward.ItemDisplayName);
                }
            }
        }

        return items;
    }

    // ── JSON helpers (same pattern as DataService) ────────────────

    private static string GetString(JsonElement el, string prop, string def = "")
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? def;
        return def;
    }

    private static int GetInt(JsonElement el, string prop, int def = 0)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var n)) return n;
        }
        return def;
    }

    private static int? GetIntNullable(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var n))
            return n;
        return null;
    }

    private static long GetLong(JsonElement el, string prop, long def = 0)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt64();
        return def;
    }

    private static bool GetBool(JsonElement el, string prop, bool def = false)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return def;
    }

    private static MysteryPerkData? ParsePerkData(JsonElement prog, string propertyName)
    {
        if (!prog.TryGetProperty(propertyName, out var perkObj) ||
            perkObj.ValueKind != JsonValueKind.Object)
            return null;

        var result = new MysteryPerkData();
        foreach (var perk in perkObj.EnumerateObject())
        {
            var type = GetString(perk.Value, "Type");
            switch (type)
            {
                case "ExtraInventorySlots":
                    result.ExtraInventorySlots += GetInt(perk.Value, "SlotCount");
                    break;
                case "FreeDailyShopItem":
                case "FreeDailyCurrency":
                    result.FreeDailyGems += GetInt(perk.Value, "Gems");
                    break;
                case "EventXp":
                    result.EventXpBonus += GetInt(perk.Value, "Amount");
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Parses a duration period string (e.g. "21d 0h 0min 0s" or "32d 1h 0min 0s")
    /// and returns a TimeSpan preserving full precision for calendar-day computation.
    /// </summary>
    private static TimeSpan? ParseDuration(string durStr)
    {
        int days = 0, hours = 0, minutes = 0;

        var dayMatch = System.Text.RegularExpressions.Regex.Match(durStr, @"(\d+)d\b");
        if (dayMatch.Success) days = int.Parse(dayMatch.Groups[1].Value);

        var hourMatch = System.Text.RegularExpressions.Regex.Match(durStr, @"(\d+)h\b");
        if (hourMatch.Success) hours = int.Parse(hourMatch.Groups[1].Value);

        var minMatch = System.Text.RegularExpressions.Regex.Match(durStr, @"(\d+)min\b");
        if (minMatch.Success) minutes = int.Parse(minMatch.Groups[1].Value);

        var ts = new TimeSpan(days, hours, minutes, 0);
        return ts > TimeSpan.Zero ? ts : null;
    }
}
