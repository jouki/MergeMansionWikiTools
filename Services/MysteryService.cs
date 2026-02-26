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

        foreach (var prog in progressions.EnumerateArray())
        {
            var eventId = GetString(prog, "ProgressionEventId");
            if (string.IsNullOrEmpty(eventId) || !eventId.StartsWith("SP_")) continue;

            var mystery = new MysteryEvent
            {
                ProgressionEventId = eventId,
                Name = DeriveName(eventId),
            };

            // Name from JSON (may be more readable than derived)
            var jsonName = GetString(prog, "Name");
            if (!string.IsNullOrEmpty(jsonName))
                mystery.Name = jsonName;

            // Schedule.Start — nested inside ActivableParams
            if (prog.TryGetProperty("ActivableParams", out var activable)
                && activable.TryGetProperty("Schedule", out var sched))
            {
                var startStr = GetString(sched, "Start");
                if (!string.IsNullOrEmpty(startStr) && DateTime.TryParse(startStr, out var dt))
                    mystery.StartDate = dt;
            }

            // Event item — plain number field "EventItem"
            mystery.EventItemNumericId = GetLong(prog, "EventItem");

            // Parse tiers
            mystery.FreeTier = ParseTier(prog, "FreeEventLevelRefs");
            mystery.SilverTier = ParseTier(prog, "Track1EventLevelRefs");
            mystery.GoldTier = ParseTier(prog, "Track2EventLevelRefs");

            // Count premium levels
            mystery.PremiumLevels = Math.Max(mystery.SilverTier.Count, mystery.GoldTier.Count);

            // Detect type: if any reward across all tiers is a Decoration → Pet
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

    private static List<MysteryRewardLevel> ParseTier(JsonElement prog, string tierProperty)
    {
        var levels = new List<MysteryRewardLevel>();

        if (!prog.TryGetProperty(tierProperty, out var tierArray)) return levels;

        int levelIndex = 0;
        foreach (var levelEl in tierArray.EnumerateArray())
        {
            var level = new MysteryRewardLevel
            {
                Level = levelIndex,
                XpRequired = GetInt(levelEl, "RequiredPoints"),
            };
            levelIndex++;

            if (levelEl.TryGetProperty("Rewards", out var rewardsArray))
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
            var reward = new MysteryReward
            {
                Type = MysteryRewardType.Item,
                Amount = GetInt(item, "Amount", 1),
                ItemKey = GetString(item, "ItemDef"),
            };

            // Parse hourglass duration override
            if (item.TryGetProperty("OverrideItemFeatures", out var overrides)
                && overrides.TryGetProperty("TimeContainerInitialTime", out var timeContainer))
            {
                reward.DurationMs = GetLong(timeContainer, "Milliseconds");
            }

            rewards.Add(reward);
        }

        // RewardDecoration — actual data is nested inside DecorationRef
        if (rewardEl.TryGetProperty("RewardDecoration", out var deco))
        {
            var reward = new MysteryReward
            {
                Type = MysteryRewardType.Decoration,
                Amount = 1,
            };

            if (deco.TryGetProperty("DecorationRef", out var decoRef))
            {
                reward.DecorationId = GetString(decoRef, "DecorationId");
                reward.DecorationName = GetString(decoRef, "DisplayName");
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
                reward.DecorationId = GetString(layeredRef, "DecorationId");
                reward.DecorationName = GetString(layeredRef, "DisplayName");
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
                reward.PetName = GetString(petRef, "ConfigKey");

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

        return rewards;
    }

    private static MysteryType DetectMysteryType(MysteryEvent mystery)
    {
        // Only RewardPet determines Pet type — both Standard and Pet have decorations
        var allTiers = mystery.FreeTier.Concat(mystery.SilverTier).Concat(mystery.GoldTier);
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
        // Build NumericConfigKey → (ItemType, ChainDisplayName) lookup
        var lookup = new Dictionary<long, (string ItemType, string ChainName)>();
        foreach (var chain in ds.Chains)
        {
            foreach (var item in chain.Items)
            {
                if (!string.IsNullOrEmpty(item.NumericConfigKey)
                    && long.TryParse(item.NumericConfigKey, out var numId))
                {
                    lookup.TryAdd(numId, (item.ItemType, chain.DisplayName));
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
            var allTiers = mystery.FreeTier.Concat(mystery.SilverTier).Concat(mystery.GoldTier);
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

                    // Priority 3: DataService item names
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
            var allTiers = mystery.FreeTier.Concat(mystery.SilverTier).Concat(mystery.GoldTier);
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
}
