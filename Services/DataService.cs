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

    /// <summary>Loaded chains</summary>
    public List<ParsedChain> Chains { get; private set; } = new();

    /// <summary>Raw JSON document for advanced queries</summary>
    private JsonDocument? _rawDoc;

    /// <summary>Warnings generated during loading</summary>
    public List<string> Warnings { get; private set; } = new();

    public async Task LoadAsync(string filePath)
    {
        Warnings.Clear();
        ItemNames.Clear();
        ItemLevels.Clear();
        ChainNames.Clear();
        Chains.Clear();

        await using var stream = File.OpenRead(filePath);
        _rawDoc = await JsonDocument.ParseAsync(stream);

        var root = _rawDoc.RootElement;
        if (!root.TryGetProperty("Data", out var dataArray))
            throw new InvalidDataException("JSON missing 'Data' array.");

        // First pass: build item name dictionary
        BuildItemNames(dataArray);

        // Second pass: parse chains
        ParseChains(dataArray);
    }

    private void BuildItemNames(JsonElement dataArray)
    {
        foreach (var chainEl in dataArray.EnumerateArray())
        {
            var configKey = GetString(chainEl, "ConfigKey");
            var chainName = GetString(chainEl, "Name");

            if (!string.IsNullOrEmpty(configKey) && !string.IsNullOrEmpty(chainName))
                ChainNames[configKey] = chainName;

            foreach (var listName in new[] { "PrimaryChain", "FallbackChain" })
            {
                if (!chainEl.TryGetProperty(listName, out var list)) continue;

                foreach (var entry in list.EnumerateArray())
                {
                    if (!entry.TryGetProperty("Item", out var item)) continue;

                    var itemType = GetString(item, "ItemType");
                    var name = GetString(item, "Name");
                    var levelNum = GetInt(item, "LevelNumber");

                    if (!string.IsNullOrEmpty(itemType))
                    {
                        if (!string.IsNullOrEmpty(name))
                            ItemNames.TryAdd(itemType, name);
                        ItemLevels.TryAdd(itemType, levelNum);
                    }
                }
            }
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

            // Determine display name
            var originalName = GetString(chainEl, "Name");
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
            foreach (var entry in primaryList.EnumerateArray())
            {
                if (!entry.TryGetProperty("Item", out var itemEl)) continue;

                var parsedItem = ParseItem(itemEl);
                if (parsedItem != null)
                    parsed.Items.Add(parsedItem);
            }

            if (parsed.Items.Count > 0)
                Chains.Add(parsed);
        }
    }

    private ParsedItem? ParseItem(JsonElement item)
    {
        var pi = new ParsedItem
        {
            Level = GetInt(item, "LevelNumber"),
            Name = GetString(item, "Name"),
            ItemType = GetString(item, "ItemType"),
            NumericConfigKey = GetStringOrNumber(item, "ConfigKey"),
            SellCoins = GetInt(item, "SellCoins"),
            Unsellable = GetBool(item, "Unsellable"),
            Description = GetString(item, "Description"),
        };

        if (item.TryGetProperty("Tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            pi.IsTemporary = tagsEl.EnumerateArray().Any(t => t.GetString()?.StartsWith("Temp") == true);

        // ── ActivationFeatures (generator) ──
        if (item.TryGetProperty("ActivationFeatures", out var af))
        {
            pi.IsGenerator = true;

            // Storage max
            pi.StorageMax = GetInt(af, "StorageMax");

            // Activation cycle data
            if (af.TryGetProperty("ActivationCycle", out var ac))
            {
                pi.FirstCycleStartDelayMs = GetLong(ac, "FirstCycleStartDelay");

                if (ac.TryGetProperty("DailyActivationCyclesData", out var daily))
                {
                    pi.ActivationAmountInCycle = GetFirstInt(daily, "ActivationAmountInCycle");
                    pi.RechargeTimeMs = GetFirstLong(daily, "DelaysBetweenCycles");
                }
            }

            // DecayAfterLastCycleProducer (what generator decays into after use)
            if (af.TryGetProperty("DecayAfterLastCycleProducer", out var dap))
            {
                string? dapConst = null;
                if (dap.ValueKind == JsonValueKind.String)
                    dapConst = dap.GetString();
                else if (dap.ValueKind == JsonValueKind.Object)
                    dapConst = GetString(dap, "Constant");

                if (!string.IsNullOrEmpty(dapConst) && dapConst != "Empty")
                    pi.DecayAfterLastCycleItemType = dapConst;
            }

            // Drop odds — can be in multiple locations
            pi.DropOdds = ExtractOdds(af);
        }

        // ── SpawnFeatures ──
        if (item.TryGetProperty("SpawnFeatures", out var sf))
        {
            pi.IsSpawner = true;

            pi.SpawnStorageMax = GetInt(sf, "StorageMax");

            // Spawn target
            if (sf.TryGetProperty("Spawn", out var spawn) && spawn.ValueKind == JsonValueKind.Object)
            {
                pi.SpawnItemType = GetString(spawn, "Constant");

                // ControlledRandom odds (e.g. HotelStay, Shell spawners)
                if (spawn.TryGetProperty("ControlledRandom", out var cr))
                {
                    if (cr.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
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
                    dpConst = GetString(dp, "Constant");

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
                        pi.DecayIntoItemType = GetString(ip, "Constant");
                }
            }
        }

        // ── DecaysWhenCyclesAreDone (on SpawnFeatures) ──
        if (item.TryGetProperty("SpawnFeatures", out var sfDecay))
            pi.DecaysWhenCyclesAreDone = GetBool(sfDecay, "DecaysWhenCyclesAreDone");

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
            }
        }

        return pi;
    }

    /// <summary>
    /// Extract drop odds from ActivationFeatures.
    /// Odds can be at: ActivationSpawn.BaseProducer.ControlledRandom.Odds
    ///                  ActivationSpawn.BaseProducer.Random.Odds
    ///                  or directly at ActivationFeatures.Odds (rare)
    /// </summary>
    private Dictionary<string, double>? ExtractOdds(JsonElement af)
    {
        if (!af.TryGetProperty("ActivationSpawn", out var asp))
            goto fallback;

        // ActivationSpawn can be a plain string (e.g. "GarageCleanupEvent")
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
                return ParseOddsDictionary(odds);
        }

        // Path 1b: ActivationSpawn → Constant (single item)
        var directConst = GetString(asp, "Constant");
        if (!string.IsNullOrEmpty(directConst))
            return new Dictionary<string, double> { { directConst, 100.0 } };

        // Path 1c: ActivationSpawn → BaseProducer → ControlledRandom/Random/Constant
        if (asp.TryGetProperty("BaseProducer", out var bp))
        {
            if (bp.TryGetProperty("ControlledRandom", out var cr))
            {
                if (cr.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
                    return ParseOddsDictionary(odds);
            }

            if (bp.TryGetProperty("Random", out var rnd))
            {
                if (rnd.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
                    return ParseOddsDictionary(odds);
            }

            var constant = GetString(bp, "Constant");
            if (!string.IsNullOrEmpty(constant))
                return new Dictionary<string, double> { { constant, 100.0 } };
        }

        fallback:
        // Path 2: Direct Odds on ActivationFeatures (fallback)
        if (af.TryGetProperty("Odds", out var directOdds) && directOdds.ValueKind == JsonValueKind.Object)
            return ParseOddsDictionary(directOdds);

        return null;
    }

    private Dictionary<string, double> ParseOddsDictionary(JsonElement el)
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
        // Wiki mapping has highest priority (after custom)
        if (wikiMapping != null &&
            wikiMapping.Mappings.TryGetValue(itemType, out var entry) &&
            !string.IsNullOrEmpty(entry.ChainName))
        {
            var ck = GetChainKeyFromItemType(itemType);
            var custom = _nameService.GetCustomName(ck);
            return !string.IsNullOrEmpty(custom) ? custom : entry.ChainName;
        }

        var chainKey = GetChainKeyFromItemType(itemType);
        return ResolveChainDisplayName(chainKey);
    }

    // ── JSON helpers ────────────────────────────────────────────────

    private static string GetStringOrNumber(JsonElement el, string prop, string def = "")
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString() ?? def;
            if (val.ValueKind == JsonValueKind.Number) return val.GetRawText();
        }
        return def;
    }

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
            if (val.ValueKind == JsonValueKind.True) return 1;
            if (val.ValueKind == JsonValueKind.False) return 0;
        }
        return def;
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
