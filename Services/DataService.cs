using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Loads chain_item_odds.json and parses it into usable models.
/// Uses JsonElement for flexible traversal of the complex nested structure.
/// </summary>
public class DataService
{
    private readonly ChainNameService _nameService;

    public DataService(ChainNameService nameService)
    {
        _nameService = nameService;
    }

    /// <summary>All item names by ItemType (e.g., "Lamp_09" → "Victorian Lamp")</summary>
    public Dictionary<string, string> ItemNames { get; private set; } = new();

    /// <summary>All item levels by ItemType from JSON's LevelNumber field</summary>
    public Dictionary<string, int> ItemLevels { get; private set; } = new();

    /// <summary>ConfigKey → chain name mapping</summary>
    public Dictionary<string, string> ChainNames { get; private set; } = new();

    /// <summary>ItemType → chain display name (e.g., "GardenTools_06" → "Gardening Tools")</summary>
    public Dictionary<string, string> ItemToChainName { get; private set; } = new();

    /// <summary>ItemType → chain ConfigKey. Built deterministically from JSON, so it survives
    /// cases where the heuristic <see cref="GetChainKeyFromItemType"/> would mis-strip
    /// (e.g. ItemType "Order_Cinema_Theater_01" → ConfigKey "OrderCinema_Theater" — heuristic
    /// would yield "Order_Cinema_Theater" because the chain name uses an extra underscore that
    /// the ConfigKey collapses).</summary>
    public Dictionary<string, string> ItemTypeToConfigKey { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loaded chains</summary>
    public List<ParsedChain> Chains { get; private set; } = new();

    /// <summary>Raw JSON document for advanced queries</summary>
    private JsonDocument? _rawDoc;

    /// <summary>Warnings generated during loading</summary>
    public List<string> Warnings { get; private set; } = new();

    /// <summary>CreatedAt timestamp from JSON root</summary>
    public string CreatedAt { get; private set; } = "";

    public async Task LoadAsync(string filePath)
    {
        using var _t = AppLogger.Timed("DataService.LoadAsync");
        Warnings.Clear();
        ItemNames.Clear();
        ItemLevels.Clear();
        ChainNames.Clear();
        ItemToChainName.Clear();
        ItemTypeToConfigKey.Clear();
        Chains.Clear();

        await using var stream = File.OpenRead(filePath);
        _rawDoc = await JsonDocument.ParseAsync(stream);

        var root = _rawDoc.RootElement;

        if (root.TryGetProperty("CreatedAt", out var ca))
            CreatedAt = ca.GetString() ?? "";

        if (!root.TryGetProperty("Data", out var dataArray))
            throw new InvalidDataException("JSON missing 'Data' array.");

        // First pass: build item name dictionary
        BuildItemNames(dataArray);

        // Second pass: parse chains
        ParseChains(dataArray);
    }

    private void BuildItemNames(JsonElement dataArray)
    {
        // Two-pass so PrimaryChain entries always win ItemType → ConfigKey reverse mapping.
        // (An item that is primary in chain X and an alias in chain Z must map to X, regardless
        // of JSON order.)
        foreach (var chainEl in dataArray.EnumerateArray())
        {
            var configKey = GetString(chainEl, "ConfigKey");
            var chainName = GetString(chainEl, "Name");

            if (!string.IsNullOrEmpty(configKey) && !string.IsNullOrEmpty(chainName))
                ChainNames[configKey] = chainName;

            ProcessChainList(chainEl, "PrimaryChain", chainName, configKey);
        }
        foreach (var chainEl in dataArray.EnumerateArray())
        {
            var configKey = GetString(chainEl, "ConfigKey");
            var chainName = GetString(chainEl, "Name");
            ProcessChainList(chainEl, "FallbackChain", chainName, configKey);
        }
    }

    private void ProcessChainList(JsonElement chainEl, string listName, string chainName, string configKey)
    {
        if (!chainEl.TryGetProperty(listName, out var list)) return;
        foreach (var entry in list.EnumerateArray())
        {
            // Normal: single "Item" per level
            if (entry.TryGetProperty("Item", out var item))
            {
                RegisterItemName(item, chainName, configKey);
                continue;
            }
            // Atypical: "Items" array — multiple variants per level
            if (entry.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                    RegisterItemName(it, chainName, configKey);
            }
        }
    }

    private void RegisterItemName(JsonElement item, string chainName, string configKey)
    {
        var itemType = GetString(item, "ItemType");
        var name = GetString(item, "Name");
        var levelNum = GetInt(item, "LevelNumber");

        if (!string.IsNullOrEmpty(itemType))
        {
            if (!string.IsNullOrEmpty(name))
                ItemNames.TryAdd(itemType, name);
            ItemLevels.TryAdd(itemType, levelNum);
            if (!string.IsNullOrEmpty(chainName))
                ItemToChainName.TryAdd(itemType, chainName);
            if (!string.IsNullOrEmpty(configKey))
                ItemTypeToConfigKey.TryAdd(itemType, configKey);
        }
    }

    private void ParseChains(JsonElement dataArray)
    {
        foreach (var chainEl in dataArray.EnumerateArray())
        {
            var configKey = GetString(chainEl, "ConfigKey");
            if (string.IsNullOrEmpty(configKey)) continue;

            if (!chainEl.TryGetProperty("PrimaryChain", out var primaryList)) continue;

            var parsed = new ParsedChain { ConfigKey = configKey };

            // Determine display name — trim because some chain Names in source JSON have stray
            // leading whitespace (e.g. " Seedling Kit"). Without trim, the space leaks into
            // generated wiki templates like `{{Item| Seedling Kit|1}}`.
            var originalName = GetString(chainEl, "Name").Trim();
            parsed.OriginalName = originalName;
            parsed.HasNaturalName = IsNaturalName(originalName);

            var customName = _nameService.GetCustomName(configKey);
            parsed.CustomName = customName;

            if (!string.IsNullOrEmpty(customName))
                parsed.DisplayName = customName;
            else if (parsed.HasNaturalName && !string.IsNullOrEmpty(originalName))
                parsed.DisplayName = originalName;
            else if (!string.IsNullOrEmpty(originalName))
                parsed.DisplayName = originalName;
            else
                parsed.DisplayName = configKey;

            // Parse items
            string poolTag = "";
            bool hasTestTag = false;
            bool checkedTestTag = false;
            foreach (var entry in primaryList.EnumerateArray())
            {
                // Normal: single "Item" per level
                if (entry.TryGetProperty("Item", out var itemEl))
                {
                    if (string.IsNullOrEmpty(poolTag))
                        poolTag = GetString(itemEl, "PoolTag");
                    if (!checkedTestTag)
                    {
                        checkedTestTag = true;
                        if (itemEl.TryGetProperty("Tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                        {
                            var tagList = tagsEl.EnumerateArray().Select(t => t.GetString()).ToList();
                            hasTestTag = tagList.Any(t => string.Equals(t, "Test", StringComparison.OrdinalIgnoreCase));
                            if (hasTestTag)
                                AppLogger.Info($"[TEST-TAG] Chain '{configKey}' has Test tag (tags: {string.Join(", ", tagList)})");
                        }
                    }
                    var parsedItem = ParseItem(itemEl);
                    if (parsedItem != null)
                        parsed.Items.Add(parsedItem);
                    continue;
                }
                // Atypical: "Items" array — multiple variants per level (e.g. DogAreaWood + SaunaAreaWood)
                if (entry.TryGetProperty("Items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                {
                    var variants = new List<ParsedItem>();
                    foreach (var it in itemsEl.EnumerateArray())
                    {
                        if (string.IsNullOrEmpty(poolTag))
                            poolTag = GetString(it, "PoolTag");
                        var parsedItem = ParseItem(it);
                        if (parsedItem != null)
                            variants.Add(parsedItem);
                    }

                    if (variants.Count > 1)
                    {
                        foreach (var v in variants)
                            v.VariantLabel = ExtractAreaLabel(v.ItemType, configKey);
                    }

                    parsed.Items.AddRange(variants);
                }
            }

            if (parsed.Items.Count > 0)
            {
                parsed.PoolTag = poolTag;
                parsed.HasTestTag = hasTestTag;
                if (hasTestTag)
                    AppLogger.Info($"[TEST-TAG-SET] Chain '{configKey}' HasTestTag=True (PoolTag={poolTag})");
                Chains.Add(parsed);
            }
        }
    }

    /// <summary>
    /// True when any tag marks the item as a temporary (area-bound) item. Temp-area tags encode the
    /// area two ways: prefix "Temp&lt;Area&gt;" (TempCinema, TempAttic — the common case) and suffix
    /// "&lt;Area&gt;Temp" (MaintenanceRoomTemp, ConservatoryTemp). Matching only the prefix dropped
    /// the suffix form, so those chains lost their temporary flag and the intro never resolved
    /// their area.
    /// </summary>
    public static bool HasTemporaryTag(IEnumerable<string> tags) =>
        tags.Any(t =>
            t.StartsWith("Temp", StringComparison.Ordinal) ||
            t.EndsWith("Temp", StringComparison.Ordinal));

    private ParsedItem? ParseItem(JsonElement item)
    {
        var pi = new ParsedItem
        {
            Level = GetInt(item, "LevelNumber"),
            Name = GetString(item, "Name"),
            OriginalName = GetString(item, "Name"),
            ItemType = GetString(item, "ItemType"),
            NumericConfigKey = GetStringOrNumber(item, "ConfigKey"),
            SellCoins = GetInt(item, "SellCoins"),
            Unsellable = GetBool(item, "Unsellable"),
            RequiredItemValue = item.TryGetProperty("RequiredItemValue", out var reqV)
                && reqV.ValueKind == JsonValueKind.Number ? reqV.GetInt32() : null,
            RewardItemValue = item.TryGetProperty("RewardItemValue", out var rwdV)
                && rwdV.ValueKind == JsonValueKind.Number ? rwdV.GetInt32() : null,
            PoolTag = GetString(item, "PoolTag"),
            Description = GetString(item, "Description"),
            SkinName = GetString(item, "SkinName"),
        };

        if (item.TryGetProperty("Tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            var tags = tagsEl.EnumerateArray()
                .Select(t => t.GetString())
                .Where(s => !string.IsNullOrEmpty(s))
                .Cast<string>()
                .ToList();
            pi.Tags = tags;
            pi.IsTemporary = HasTemporaryTag(tags);
            pi.IsTestTag = tags.Any(t => string.Equals(t, "Test", StringComparison.OrdinalIgnoreCase));
        }

        // ── ActivationFeatures (generator) ──
        if (item.TryGetProperty("ActivationFeatures", out var af))
        {
            pi.IsGenerator = true;

            // Storage max
            pi.StorageMax = GetInt(af, "StorageMax");
            // StartsFull determines whether StorageMax is per-cycle (true) or total (false).
            // Used by LuaGeneratorService to split charges/cycles correctly.
            pi.StartsFull = af.TryGetProperty("StartsFull", out var sfEl)
                && sfEl.ValueKind == JsonValueKind.True;

            // Activation cycle data (flattened: renamed fields, scalars instead of arrays)
            if (af.TryGetProperty("ActivationCycle", out var ac))
            {
                pi.FirstCycleStartDelayMs = GetLong(ac, "InitialCooldown");

                pi.ActivationAmountInCycle = GetInt(ac, "MiniChargesInSingleCharge");
                pi.HowManyGeneratedInCycle = GetInt(ac, "DropsInSingleMiniCharge");
                if (pi.HowManyGeneratedInCycle <= 0) pi.HowManyGeneratedInCycle = 1;
                pi.ActivationHowManyCycles = GetInt(ac, "HowManyCycles", -1);
                pi.RechargeTimeMs = GetLong(ac, "ChargeCooldown");
            }

            // DecayAfterLastCycleProducer (what generator decays into after use)
            if (af.TryGetProperty("DecayAfterLastCycleProducer", out var dap))
            {
                // Track presence of the field — absence + cycles=-1 signals "truly infinite"
                pi.HasDecayAfterLastCycleField = true;
                string? dapConst = null;
                if (dap.ValueKind == JsonValueKind.String)
                    dapConst = dap.GetString();
                else if (dap.ValueKind == JsonValueKind.Object)
                {
                    dapConst = GetConstantFirst(dap);

                    // ControlledRandom odds (multiple decay targets with probabilities)
                    if (dap.TryGetProperty("ControlledRandom", out var dapCr)
                        && dapCr.TryGetProperty("Odds", out var dapOdds)
                        && dapOdds.ValueKind == JsonValueKind.Object)
                        pi.DecayAfterLastCycleOdds = ParseOddsDictionary(dapOdds);
                }

                if (!string.IsNullOrEmpty(dapConst) && dapConst != "Empty")
                    pi.DecayAfterLastCycleItemType = dapConst;
            }

            // Drop odds — can be in multiple locations
            pi.DropOdds = ExtractOdds(af, out var dropNonConstant);
            pi.HasRandomDrop = dropNonConstant;
        }

        // ── SpawnFeatures ──
        if (item.TryGetProperty("SpawnFeatures", out var sf))
        {
            pi.IsSpawner = true;

            pi.SpawnStorageMax = GetInt(sf, "StorageMax");

            // Spawn target
            if (sf.TryGetProperty("Spawn", out var spawn) && spawn.ValueKind == JsonValueKind.Object)
            {
                pi.SpawnItemType = GetConstantFirst(spawn);

                // ControlledRandom / ControlledRandomSequence odds
                if (spawn.TryGetProperty("ControlledRandom", out var cr))
                {
                    if (cr.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
                        pi.SpawnOdds = ParseOddsDictionary(odds);
                }

                if (pi.SpawnOdds == null && spawn.TryGetProperty("ControlledRandomSequence", out var crs))
                {
                    if (crs.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
                        pi.SpawnOdds = ParseOddsDictionary(odds);
                }

                if (spawn.TryGetProperty("Random", out var rndSpawn))
                {
                    if (rndSpawn.TryGetProperty("Odds", out var odds))
                        pi.SpawnOdds = ParseOddsDictionary(odds);
                }
            }

            // Spawn cycle
            if (sf.TryGetProperty("SpawnCycle", out var sc))
            {
                pi.SpawnHowManyCycles = GetInt(sc, "HowManyCycles", -1);
                pi.SpawnAmountInCycle = GetInt(sc, "SpawnAmountInCycle");
                pi.SpawnDelayMs = GetLong(sc, "DelayBetweenCycles");
            }

            // Decay producer (what it transforms into after cycles done)
            if (sf.TryGetProperty("DecayProducer", out var dp))
            {
                string? dpConst = null;
                if (dp.ValueKind == JsonValueKind.String)
                    dpConst = dp.GetString();
                else if (dp.ValueKind == JsonValueKind.Object)
                    dpConst = GetConstantFirst(dp);

                if (!string.IsNullOrEmpty(dpConst) && dpConst != "Empty")
                    pi.SpawnDecayIntoItemType = dpConst;
            }
        }

        // ── DecayFeatures ──
        if (item.TryGetProperty("DecayFeatures", out var df))
        {
            if (GetBool(df, "DoesDecay"))
            {
                pi.HasDecay = true;
                if (df.TryGetProperty("ItemProducer", out var ip))
                {
                    if (ip.ValueKind == JsonValueKind.String)
                        pi.DecayIntoItemType = ip.GetString();
                    else if (ip.ValueKind == JsonValueKind.Object)
                    {
                        pi.DecayIntoItemType = GetConstantFirst(ip);

                        // Non-constant decay (ControlledRandom/Random) = a "roller" that resolves to
                        // one of several targets with odds (e.g. branching merge → A/B/C). Capture
                        // them for the Decay Odds section.
                        foreach (var producerKey in new[] { "ControlledRandom", "ControlledRandomSequence", "Random" })
                        {
                            if (ip.TryGetProperty(producerKey, out var prod)
                                && prod.TryGetProperty("Odds", out var oddsEl)
                                && oddsEl.ValueKind == JsonValueKind.Object)
                            {
                                pi.DecayIntoOdds = ParseOddsDictionary(oddsEl);
                                break;
                            }
                        }
                    }
                }
            }
        }

        // ── DecaysWhenCyclesAreDone (on SpawnFeatures) ──
        if (item.TryGetProperty("SpawnFeatures", out var sfDecay))
            pi.DecaysWhenCyclesAreDone = GetBool(sfDecay, "DecaysWhenCyclesAreDone");

        // ── ChestFeatures (Brown Chest, Mystery Chest, Reward Boxes, …) ──
        // LootProducer is polymorphic IItemProducer. The dumper exposes one of:
        //   - PrefixProducer  → `{Marker, BaseProducer: {...inner shape...}}`
        //   - RandomProducer  → `{Random: {Odds: {...}}}`
        //   - ControlledRandom[Sequence]Producer  → `{ControlledRandom(Sequence): {Odds: {...}}}`
        //   - ConstantProducer  → `{Constant: [{Item, Quantity}, ...]}` (post v0.20.73; legacy: `{Constant: "ItemType"}`)
        if (item.TryGetProperty("ChestFeatures", out var chest) && GetBool(chest, "IsChest"))
        {
            pi.IsChest = true;
            if (chest.TryGetProperty("LootProducer", out var lootProd)
                && lootProd.ValueKind == JsonValueKind.Object)
            {
                // PrefixProducer unwraps to BaseProducer; everything else reads from lootProd directly.
                // Guard: BaseProducer must itself be an object (PrefixProducer wraps a sub-producer).
                JsonElement src = lootProd;
                if (lootProd.TryGetProperty("BaseProducer", out var bp) && bp.ValueKind == JsonValueKind.Object)
                    src = bp;
                Dictionary<string, double>? chestOdds = null;
                List<(string Item, int Quantity)>? chestItems = null;

                if (src.TryGetProperty("Random", out var rand) &&
                    rand.TryGetProperty("Odds", out var randOdds))
                {
                    chestOdds = ParseOddsDictionary(randOdds);
                }
                else if (src.TryGetProperty("ControlledRandomSequence", out var crs) &&
                         crs.TryGetProperty("Odds", out var crsOdds))
                {
                    chestOdds = ParseOddsDictionary(crsOdds);
                }
                else if (src.TryGetProperty("ControlledRandom", out var cr) &&
                         cr.TryGetProperty("Odds", out var crOdds))
                {
                    chestOdds = ParseOddsDictionary(crOdds);
                }
                else if (src.TryGetProperty("Constant", out _))
                {
                    // ConstantProducer = guaranteed payload (= every listed item drops). Treat as
                    // a deterministic list rather than odds. Caller can convert to 100/N odds if needed.
                    chestItems = GetConstantList(src);
                    if (chestItems.Count > 0)
                    {
                        chestOdds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (it, _) in chestItems)
                        {
                            if (chestOdds.ContainsKey(it)) continue; // collapse duplicates
                            chestOdds[it] = 100.0;
                        }
                    }
                }

                if (chestOdds != null && chestOdds.Count > 0)
                    pi.ChestRewardOdds = chestOdds;
                if (chestItems != null && chestItems.Count > 0)
                    pi.ChestRewardItems = chestItems;
            }
        }

        // ── SinkFeatures (Transformative Item) ──
        if (item.TryGetProperty("SinkFeatures", out var sink) && GetBool(sink, "IsSink"))
        {
            pi.IsSink = true;
            if (sink.TryGetProperty("Factory", out var factory) &&
                factory.TryGetProperty("ScoreTargets", out var targets) &&
                targets.ValueKind == JsonValueKind.Object)
            {
                pi.SinkRequirementConfigKeys = targets.EnumerateObject()
                    .Select(p => p.Name)
                    .Where(k => !string.IsNullOrEmpty(k))
                    .ToList();
                pi.SinkRequirementAmounts = targets.EnumerateObject()
                    .Where(p => !string.IsNullOrEmpty(p.Name))
                    .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : 1);
            }
            if (factory.TryGetProperty("RewardDef", out var rewardDef) && rewardDef.ValueKind == JsonValueKind.String)
                pi.SinkRewardItemType = rewardDef.GetString();
            else if (factory.TryGetProperty("Reward", out var rewardOld) && rewardOld.ValueKind == JsonValueKind.String)
                pi.SinkRewardItemType = rewardOld.GetString(); // older dump format
        }

        // ── OrderFeatures (tasks requiring items → rewards) ──
        if (item.TryGetProperty("OrderFeatures", out var order) && GetBool(order, "IsOrder"))
        {
            pi.IsOrder = true;
            var required = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rewards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var perTaskList = new List<ParsedTask>();

            // OrderProducer can be Constant, ControlledRandom, or ControlledPredefinedSequence
            if (order.TryGetProperty("OrderProducer", out var op))
            {
                // Try each variant — all have "Tasks" arrays with "Required" and "Rewards"
                foreach (var variant in new[] { "Constant", "ControlledRandom", "ControlledPredefinedSequence" })
                {
                    if (!op.TryGetProperty(variant, out var v)) continue;
                    if (!v.TryGetProperty("Tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) continue;

                    foreach (var task in tasks.EnumerateArray())
                    {
                        var pt = new ParsedTask
                        {
                            Odds = GetDouble(task, "Odds"),
                            OddsWeight = GetDouble(task, "OddsWeight"),
                        };

                        if (task.TryGetProperty("Required", out var reqArr) && reqArr.ValueKind == JsonValueKind.Array)
                            foreach (var r in reqArr.EnumerateArray())
                            {
                                var it = GetString(r, "Item");
                                var amt = GetInt(r, "Amount", 1);
                                if (!string.IsNullOrEmpty(it))
                                {
                                    required[it] = required.GetValueOrDefault(it) + amt;
                                    pt.Required.Add((it, amt));
                                }
                            }

                        if (task.TryGetProperty("Rewards", out var rewArr) && rewArr.ValueKind == JsonValueKind.Array)
                            foreach (var r in rewArr.EnumerateArray())
                            {
                                var it = GetString(r, "Item");
                                var amt = GetInt(r, "Amount", 1);
                                if (!string.IsNullOrEmpty(it))
                                {
                                    rewards[it] = rewards.GetValueOrDefault(it) + amt;
                                    pt.Rewards.Add((it, amt));
                                }
                            }

                        perTaskList.Add(pt);
                    }
                    break;
                }
            }

            if (required.Count > 0) pi.OrderRequiredItems = required;
            if (rewards.Count > 0) pi.OrderRewardItems = rewards;
            if (perTaskList.Count > 0) pi.OrderTasks = perTaskList;
        }

        // ── MergeFeatures.Mechanic.ResultProducer ──
        // Captures the item type that 2× merging produces. Normally L+1 of same chain;
        // for chain-terminal items it can point to L1 of a DIFFERENT chain
        // (e.g. SeedBagEmpty_04 → GoldRoot_01). Used by infobox + chain table to render
        // transforms_to when target is in a different chain.
        if (item.TryGetProperty("MergeFeatures", out var mf)
            && mf.TryGetProperty("Mechanic", out var mech)
            && mech.TryGetProperty("ResultProducer", out var rp))
        {
            string? resultType = null;
            if (rp.ValueKind == JsonValueKind.Object)
                resultType = GetConstantFirst(rp);
            // rp.ValueKind == String typically holds "Empty" (no result) — skip

            if (!string.IsNullOrEmpty(resultType) && resultType != "Empty")
                pi.MergeResultItemType = resultType;
        }

        // ── BubbleFeatures ──
        if (item.TryGetProperty("BubbleFeatures", out var bf))
        {
            var spawnOdds = GetInt(bf, "SpawnOdds");
            if (spawnOdds > 0)
            {
                pi.HasBubble = true;
                pi.BubbleDurationMs = GetLong(bf, "BubbleDuration");
                pi.BubbleOpenCost = GetInt(bf, "OpenQuantity");
                pi.BubbleSpawnOdds = spawnOdds;
            }
        }

        // ── ExtraSpawn token values ──
        var extraSpawn = ParseExtraSpawnValues(item);
        if (extraSpawn.Count > 0)
            pi.ExtraSpawnValues = extraSpawn;

        // ── SpeedUpCostGems ──
        if (item.TryGetProperty("SpeedUpCostGems", out var sucEl) && sucEl.ValueKind == JsonValueKind.Number)
            pi.SpeedUpCostGems = sucEl.GetInt32();

        // ── RechargeTimeMs for secondary generators (SpawnFeatures) ──
        if (pi.RechargeTimeMs == 0
            && item.TryGetProperty("SpawnFeatures", out var spf)
            && spf.TryGetProperty("SpawnCycle", out var spCycle)
            && spCycle.TryGetProperty("DelayBetweenCycles", out var dbc)
            && dbc.ValueKind == JsonValueKind.Number)
        {
            pi.RechargeTimeMs = dbc.GetInt64();
        }

        return pi;
    }

    private static readonly string[] ExtraSpawnFieldNames =
    {
        "DigEventTaps", "QuaternaryEnergy",
        "ClassicRacesSailPoints", "RollTheDiceToken", "BuilderEventToken"
    };

    private static Dictionary<string, double> ParseExtraSpawnValues(JsonElement item)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in ExtraSpawnFieldNames)
        {
            if (item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            {
                var val = v.GetDouble();
                if (val > 0) result[name] = val;
            }
        }
        return result;
    }

    /// <summary>
    /// Extract drop odds from ActivationFeatures.
    /// Odds can be at: ActivationSpawn.BaseProducer.ControlledRandom.Odds
    ///                  ActivationSpawn.BaseProducer.Random.Odds
    ///                  ActivationSpawn.PredefinedSequence.Odds
    ///                  or directly at ActivationFeatures.Odds (rare)
    /// </summary>
    private Dictionary<string, double>? ExtractOdds(JsonElement af) => ExtractOdds(af, out _);

    /// <param name="nonConstant">
    /// Set true when the odds come from a randomized producer (ControlledRandom / Random / Sequence /
    /// direct Odds) — i.e. real variance. A ConstantProducer (single 100% entry) leaves it false.
    /// </param>
    private Dictionary<string, double>? ExtractOdds(JsonElement af, out bool nonConstant)
    {
        nonConstant = false;

        if (!af.TryGetProperty("ActivationSpawn", out var asp))
            goto fallback;

        // ActivationSpawn can be a plain string (e.g. "GarageCleanupEvent") — deterministic.
        if (asp.ValueKind == JsonValueKind.String)
        {
            var s = asp.GetString();
            return !string.IsNullOrEmpty(s) ? new Dictionary<string, double> { { s, 100.0 } } : null;
        }

        if (asp.ValueKind != JsonValueKind.Object)
            goto fallback;

        // Path 1a: ActivationSpawn → ControlledRandom → Odds (direct)
        if (asp.TryGetProperty("ControlledRandom", out var directCr))
        {
            if (directCr.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
            { nonConstant = true; return ParseOddsDictionary(odds); }
        }

        // Path 1a2: ActivationSpawn → ControlledRandomSequence → Odds (direct)
        if (asp.TryGetProperty("ControlledRandomSequence", out var directCrs))
        {
            if (directCrs.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
            { nonConstant = true; return ParseOddsDictionary(odds); }
        }

        // Path 1a3: ActivationSpawn → Random → Odds (direct, no BaseProducer wrapper).
        // Used by single-cycle Box-style items (Simple Brown Box / Suitcase / Radio etc.)
        // where the producer is a plain Random rather than a ControlledRandom.
        if (asp.TryGetProperty("Random", out var directRnd))
        {
            if (directRnd.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
            { nonConstant = true; return ParseOddsDictionary(odds); }
        }

        // Path 1b: ActivationSpawn → Constant (single item) — deterministic.
        var directConst = GetConstantFirst(asp);
        if (!string.IsNullOrEmpty(directConst))
            return new Dictionary<string, double> { { directConst, 100.0 } };

        // Path 1c: ActivationSpawn → BaseProducer → ControlledRandom/ControlledRandomSequence/Random/Constant
        if (asp.TryGetProperty("BaseProducer", out var bp))
        {
            if (bp.TryGetProperty("ControlledRandom", out var cr))
            {
                if (cr.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
                { nonConstant = true; return ParseOddsDictionary(odds); }
            }

            if (bp.TryGetProperty("ControlledRandomSequence", out var crs))
            {
                if (crs.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
                { nonConstant = true; return ParseOddsDictionary(odds); }
            }

            if (bp.TryGetProperty("Random", out var rnd))
            {
                if (rnd.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
                { nonConstant = true; return ParseOddsDictionary(odds); }
            }

            var constant = GetConstantFirst(bp);
            if (!string.IsNullOrEmpty(constant))
                return new Dictionary<string, double> { { constant, 100.0 } };
        }

        // Path 1d: ActivationSpawn → PredefinedSequence → Odds
        if (asp.TryGetProperty("PredefinedSequence", out var ps))
        {
            if (ps.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
            { nonConstant = true; return ParseOddsDictionary(odds); }
        }

        fallback:
        // Path 2: Direct Odds on ActivationFeatures (fallback) — randomized.
        if (af.TryGetProperty("Odds", out var directOdds) && directOdds.ValueKind == JsonValueKind.Object)
        { nonConstant = true; return ParseOddsDictionary(directOdds); }

        return null;
    }

    internal static Dictionary<string, double> ParseOddsDictionary(JsonElement el)
    {
        var dict = new Dictionary<string, double>();
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.TryGetDouble(out var val))
                dict[prop.Name] = val;
        }
        return dict;
    }

    /// <summary>
    /// Determines if a name looks "natural" (human-readable) vs internal ID.
    /// Internal: starts with Item_, CBE_, LDE_, or contains long underscore patterns.
    /// </summary>
    public static bool IsNaturalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.StartsWith("Item_", StringComparison.Ordinal)) return false;
        if (name.StartsWith("CBE_", StringComparison.Ordinal)) return false;
        if (name.StartsWith("LDE_", StringComparison.Ordinal)) return false;

        // If it has 2+ underscores, likely internal
        if (name.Count(c => c == '_') >= 2) return false;

        return true;
    }

    /// <summary>
    /// Extracts a human-readable area label from an ItemType prefix that differs from the chain's ConfigKey.
    /// E.g. ItemType="SaunaAreaWood_01", chainKey="DogAreaWood" → "Sauna Area".
    /// Returns null if the item belongs to the chain's own prefix.
    /// </summary>
    private static string? ExtractAreaLabel(string itemType, string chainKey)
    {
        // Get prefix before last "_XX" number suffix
        var lastUnderscore = itemType.LastIndexOf('_');
        if (lastUnderscore <= 0) return null;
        var itemPrefix = itemType[..lastUnderscore];

        // Split CamelCase into words
        string[] ToWords(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ").Split(' ');

        var itemWords = ToWords(itemPrefix);
        var chainWords = ToWords(chainKey);

        // Find how many trailing words are shared between item prefix and chain key
        // E.g. "Sauna Area Wood" vs "Dog Area Wood" → 2 common trailing words ("Area", "Wood")
        int common = 0;
        for (int i = 1; i <= Math.Min(itemWords.Length, chainWords.Length); i++)
        {
            if (itemWords[^i].Equals(chainWords[^i], StringComparison.OrdinalIgnoreCase))
                common = i;
            else
                break;
        }

        // Keep only the differing leading part: "Sauna Area Wood" → "Sauna", "Dog Area Wood" → "Dog"
        var label = common > 0 && common < itemWords.Length
            ? string.Join(' ', itemWords[..^common])
            : string.Join(' ', itemWords);

        return string.IsNullOrWhiteSpace(label) ? null : label;
    }

    /// <summary>
    /// Resolves an ItemType reference (e.g., "Lamp_09") to a human-readable name.
    /// Returns the name from ItemNames dict, or the raw type if not found.
    /// </summary>
    public string ResolveItemName(string itemType)
    {
        return ItemNames.TryGetValue(itemType, out var name) ? name : itemType;
    }

    /// <summary>
    /// Extracts the chain ConfigKey from an ItemType (e.g., "Lamp_09" → "Lamp").
    /// </summary>
    public static string GetChainKeyFromItemType(string itemType)
    {
        var idx = itemType.LastIndexOf('_');
        return idx > 0 ? itemType[..idx] : itemType;
    }

    /// <summary>
    /// Extracts the level number from an ItemType suffix (e.g., "Lamp_09" → 9).
    /// </summary>
    public static int GetLevelFromItemType(string itemType)
    {
        var idx = itemType.LastIndexOf('_');
        if (idx > 0 && int.TryParse(itemType[(idx + 1)..], out var level))
            return level;
        return 0;
    }

    /// <summary>
    /// Resolves the level for an ItemType.
    /// Priority: wiki mapping level → JSON LevelNumber field → 0.
    /// </summary>
    public int ResolveLevel(string itemType, WikiMappingCache? wikiMapping)
    {
        if (wikiMapping != null &&
            wikiMapping.Mappings.TryGetValue(itemType, out var entry) &&
            entry.Level.HasValue)
            return entry.Level.Value;

        if (ItemLevels.TryGetValue(itemType, out var jsonLevel))
            return jsonLevel;

        return 0;
    }

    /// <summary>
    /// Gets the display name of a chain given its ConfigKey.
    /// Priority: custom name → wiki mapping (ChainNames updated by ApplyWikiMapping) → original name → configKey.
    /// </summary>
    public string ResolveChainDisplayName(string configKey)
    {
        var custom = _nameService.GetCustomName(configKey);
        if (!string.IsNullOrEmpty(custom)) return custom;

        if (ChainNames.TryGetValue(configKey, out var name) && !string.IsNullOrEmpty(name))
            return name;

        return configKey;
    }

    /// <summary>
    /// Resolves a chain display name from an ItemType, checking wiki mapping first.
    /// Used by template generators for cross-chain references.
    /// </summary>
    public string ResolveChainDisplayNameFromItemType(string itemType, WikiMappingCache? wikiMapping)
    {
        var chainKey = ResolveChainKeyFromItemType(itemType);

        // Module:Datatable/Items/Mapping (multinameMappings, cached in wikiMapping) disambiguates chains whose
        // game name collides with another item — e.g. "Batteries" → "Batteries (Flashback Rewind 2025)". Its
        // keys are the chain ConfigKey (e.g. "CBE_Flashback2025_Battery_01") OR a specific item id, so check
        // the resolved chainKey FIRST (covers reward/grid items carrying a level suffix), then the raw item id.
        // The mapped chainName wins over the game's raw chain name (after a custom-name override).
        if (wikiMapping != null
            && ((wikiMapping.Mappings.TryGetValue(chainKey, out var entry)
                    || wikiMapping.Mappings.TryGetValue(itemType, out entry))
                && !string.IsNullOrEmpty(entry.ChainName)))
        {
            var custom = _nameService.GetCustomName(chainKey);
            return !string.IsNullOrEmpty(custom) ? custom : entry.ChainName;
        }

        return ResolveChainDisplayName(chainKey);
    }

    /// <summary>
    /// Resolves an ItemType to its owning chain's ConfigKey. Prefers the deterministic
    /// <see cref="ItemTypeToConfigKey"/> reverse map built from JSON; falls back to the
    /// static <see cref="GetChainKeyFromItemType"/> heuristic (last-underscore strip) for
    /// ItemTypes that weren't loaded via JSON (e.g. wiki-only references, dump-less callers).
    /// </summary>
    public string ResolveChainKeyFromItemType(string itemType)
    {
        if (ItemTypeToConfigKey.TryGetValue(itemType, out var ck))
            return ck;
        return GetChainKeyFromItemType(itemType);
    }

    // ── JSON helpers ────────────────────────────────────────────────

    internal static string GetStringOrNumber(JsonElement el, string prop, string def = "")
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString() ?? def;
            if (val.ValueKind == JsonValueKind.Number) return val.GetRawText();
        }
        return def;
    }

    internal static string GetString(JsonElement el, string prop, string def = "")
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? def;
        return def;
    }

    /// <summary>
    /// Reads the first ItemType from a ConstantProducer "Constant" field, supporting both shapes:
    /// new array `[{Item, Quantity}, ...]` (post v0.20.73 dumper) and legacy scalar string.
    /// </summary>
    internal static string GetConstantFirst(JsonElement el, string prop = "Constant", string def = "")
    {
        if (!el.TryGetProperty(prop, out var val)) return def;
        if (val.ValueKind == JsonValueKind.String) return val.GetString() ?? def;
        if (val.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in val.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty("Item", out var it)
                    && it.ValueKind == JsonValueKind.String)
                {
                    var s = it.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
        }
        return def;
    }

    /// <summary>
    /// Reads all (Item, Quantity) pairs from a ConstantProducer "Constant" field.
    /// Legacy scalar string format yields a single (item, 1) entry. Empty list if missing/malformed.
    /// </summary>
    internal static List<(string Item, int Quantity)> GetConstantList(JsonElement el, string prop = "Constant")
    {
        var result = new List<(string, int)>();
        if (!el.TryGetProperty(prop, out var val)) return result;
        if (val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString();
            if (!string.IsNullOrEmpty(s)) result.Add((s, 1));
            return result;
        }
        if (val.ValueKind != JsonValueKind.Array) return result;
        foreach (var elem in val.EnumerateArray())
        {
            if (elem.ValueKind != JsonValueKind.Object) continue;
            string? item = null;
            if (elem.TryGetProperty("Item", out var it) && it.ValueKind == JsonValueKind.String)
                item = it.GetString();
            int qty = 1;
            if (elem.TryGetProperty("Quantity", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var n))
                qty = n;
            if (!string.IsNullOrEmpty(item))
                result.Add((item, qty));
        }
        return result;
    }

    internal static int GetInt(JsonElement el, string prop, int def = 0)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var n)) return n;
            if (val.ValueKind == JsonValueKind.True) return 1;
            if (val.ValueKind == JsonValueKind.False) return 0;
        }
        return def;
    }

    internal static long GetLong(JsonElement el, string prop, long def = 0)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt64();
        return def;
    }

    internal static double GetDouble(JsonElement el, string prop, double def = 0.0)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number
            && val.TryGetDouble(out var d))
            return d;
        return def;
    }

    internal static bool GetBool(JsonElement el, string prop, bool def = false)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return def;
    }

    private static int GetFirstInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in arr.EnumerateArray())
            {
                if (v.TryGetInt32(out var n)) return n;
            }
        }
        return 0;
    }

    private static long GetFirstLong(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in arr.EnumerateArray())
            {
                return v.GetInt64();
            }
        }
        return 0;
    }
}
