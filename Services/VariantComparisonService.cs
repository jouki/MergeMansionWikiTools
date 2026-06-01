using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;
using static MergeMansionWikiTools.Services.DataService;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Discovers AB test variant directories and compares a single chain across them.
/// Uses targeted JSON parsing (only matching ConfigKey) for performance.
/// </summary>
internal static class VariantComparisonService
{
    /// <summary>
    /// Discovers variant subdirectories that contain chain_item_odds.json.
    /// </summary>
    public static List<VariantInfo> DiscoverVariants(string baseFilePath)
    {
        var baseDir = Path.GetDirectoryName(baseFilePath);
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
            return new List<VariantInfo>();

        var variants = new List<VariantInfo>();
        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            var candidate = Path.Combine(dir, "chain_item_odds.json");
            if (File.Exists(candidate))
            {
                variants.Add(new VariantInfo
                {
                    Name = Path.GetFileName(dir),
                    FilePath = candidate
                });
            }
        }

        variants.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return variants;
    }

    /// <summary>
    /// Checks whether any variant directories exist (lightweight, no JSON parsing).
    /// </summary>
    public static bool HasVariants(string baseFilePath)
    {
        var baseDir = Path.GetDirectoryName(baseFilePath);
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
            return false;

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            if (File.Exists(Path.Combine(dir, "chain_item_odds.json")))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Scans all chains across all variants and returns ConfigKeys that have any diffs.
    /// Parses each file once (all chains), not per-chain — much faster for bulk scan.
    /// </summary>
    public static async Task<HashSet<string>> ScanAllChainsForDiffsAsync(
        string basePath, List<VariantInfo> variants)
    {
        // Parse all files in parallel → Dictionary<ConfigKey, Dictionary<Level, ParsedItem>>
        var baseParsed = await ParseAllChainsAsync(basePath);
        var variantTasks = variants.Select(v => ParseAllChainsAsync(v.FilePath));
        var variantParsed = await Task.WhenAll(variantTasks);

        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (configKey, baseItems) in baseParsed)
        {
            foreach (var vp in variantParsed)
            {
                if (!vp.TryGetValue(configKey, out var varItems))
                    continue;

                // Quick diff: check if any level has diffs
                var allLevels = new HashSet<int>(baseItems.Keys);
                foreach (var k in varItems.Keys) allLevels.Add(k);

                foreach (var level in allLevels)
                {
                    baseItems.TryGetValue(level, out var baseItem);
                    varItems.TryGetValue(level, out var varItem);
                    var diffs = ComputeFieldDiffs(baseItem, varItem);
                    if (diffs.Count > 0)
                    {
                        result.Add(configKey);
                        goto nextChain;
                    }
                }
            }
            nextChain:;
        }

        // Also check chains that only exist in variants
        foreach (var vp in variantParsed)
        {
            foreach (var configKey in vp.Keys)
            {
                if (!baseParsed.ContainsKey(configKey))
                    result.Add(configKey);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses ALL chains from a file, returning ConfigKey → (Level → ParsedItem).
    /// </summary>
    private static async Task<Dictionary<string, Dictionary<int, ParsedItem>>> ParseAllChainsAsync(string filePath)
    {
        var allChains = new Dictionary<string, Dictionary<int, ParsedItem>>(StringComparer.Ordinal);
        try
        {
            await using var stream = File.OpenRead(filePath);
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("Data", out var dataArray))
                return allChains;

            foreach (var chainEl in dataArray.EnumerateArray())
            {
                var ck = GetString(chainEl, "ConfigKey");
                if (string.IsNullOrEmpty(ck)) continue;
                if (!chainEl.TryGetProperty("PrimaryChain", out var primaryList)) continue;

                var items = new Dictionary<int, ParsedItem>();
                foreach (var entry in primaryList.EnumerateArray())
                {
                    if (entry.TryGetProperty("Item", out var itemEl))
                    {
                        var pi = ParseItemTargeted(itemEl);
                        if (pi != null) items[pi.Level] = pi;
                        continue;
                    }
                    if (entry.TryGetProperty("Items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in itemsEl.EnumerateArray())
                        {
                            var pi = ParseItemTargeted(it);
                            if (pi != null) items.TryAdd(pi.Level, pi);
                        }
                    }
                }

                if (items.Count > 0)
                    allChains[ck] = items;
            }
        }
        catch { /* ignore unreadable files */ }
        return allChains;
    }

    /// <summary>
    /// Compares a chain across base and all variants. Returns diffs grouped by level.
    /// </summary>
    public static async Task<VariantComparisonResult> CompareChainAsync(
        string configKey, string chainName, string basePath, List<VariantInfo> variants)
    {
        // Parse base + all variants in parallel
        var tasks = new List<Task<Dictionary<int, ParsedItem>?>>();
        tasks.Add(ParseChainTargetedAsync(basePath, configKey));
        foreach (var v in variants)
            tasks.Add(ParseChainTargetedAsync(v.FilePath, configKey));

        var results = await Task.WhenAll(tasks);

        var baseItems = results[0] ?? new();
        var variantItems = new List<Dictionary<int, ParsedItem>?>();
        for (int i = 1; i < results.Length; i++)
            variantItems.Add(results[i]);

        // Build diffs
        var allLevels = new SortedSet<int>(baseItems.Keys);
        foreach (var vi in variantItems)
            if (vi != null)
                foreach (var k in vi.Keys)
                    allLevels.Add(k);

        var diffs = new List<ItemVariantDiff>();
        foreach (var level in allLevels)
        {
            baseItems.TryGetValue(level, out var baseItem);
            var itemDiff = new ItemVariantDiff
            {
                Level = level,
                ItemName = baseItem?.Name ?? "",
                ItemType = baseItem?.ItemType ?? ""
            };

            bool hasDiffs = false;
            for (int vi = 0; vi < variants.Count; vi++)
            {
                var vItems = variantItems[vi];
                ParsedItem? varItem = null;
                vItems?.TryGetValue(level, out varItem);

                var fieldDiffs = ComputeFieldDiffs(baseItem, varItem);
                if (fieldDiffs.Count > 0)
                {
                    hasDiffs = true;
                    itemDiff.DiffsByVariant[variants[vi].Name] = fieldDiffs;

                    // Fill item info from variant if base is missing
                    if (baseItem == null && varItem != null)
                    {
                        if (string.IsNullOrEmpty(itemDiff.ItemName)) itemDiff.ItemName = varItem.Name;
                        if (string.IsNullOrEmpty(itemDiff.ItemType)) itemDiff.ItemType = varItem.ItemType;
                    }
                }
            }

            if (hasDiffs)
                diffs.Add(itemDiff);
        }

        return new VariantComparisonResult
        {
            ChainName = chainName,
            ConfigKey = configKey,
            LevelCount = allLevels.Count,
            Variants = variants,
            Diffs = diffs
        };
    }

    /// <summary>
    /// Parses ONLY the matching ConfigKey chain from a JSON file (targeted, fast).
    /// Returns items keyed by Level.
    /// </summary>
    private static async Task<Dictionary<int, ParsedItem>?> ParseChainTargetedAsync(string filePath, string configKey)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            using var doc = await JsonDocument.ParseAsync(stream);

            var root = doc.RootElement;
            if (!root.TryGetProperty("Data", out var dataArray))
                return null;

            foreach (var chainEl in dataArray.EnumerateArray())
            {
                var ck = GetString(chainEl, "ConfigKey");
                if (!string.Equals(ck, configKey, StringComparison.Ordinal))
                    continue;

                if (!chainEl.TryGetProperty("PrimaryChain", out var primaryList))
                    return null;

                var items = new Dictionary<int, ParsedItem>();
                foreach (var entry in primaryList.EnumerateArray())
                {
                    if (entry.TryGetProperty("Item", out var itemEl))
                    {
                        var pi = ParseItemTargeted(itemEl);
                        if (pi != null)
                            items[pi.Level] = pi;
                        continue;
                    }
                    if (entry.TryGetProperty("Items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in itemsEl.EnumerateArray())
                        {
                            var pi = ParseItemTargeted(it);
                            if (pi != null)
                                items.TryAdd(pi.Level, pi);
                        }
                    }
                }

                return items;
            }

            return null; // Chain not found in this file
        }
        catch
        {
            return null; // File unreadable or invalid
        }
    }

    /// <summary>
    /// Parses only the fields needed for comparison from a JSON item element.
    /// </summary>
    private static ParsedItem? ParseItemTargeted(JsonElement item)
    {
        var pi = new ParsedItem
        {
            Level = GetInt(item, "LevelNumber"),
            Name = GetString(item, "Name"),
            ItemType = GetString(item, "ItemType"),
            SellCoins = GetInt(item, "SellCoins"),
        };

        // ── ActivationFeatures (generator) ──
        if (item.TryGetProperty("ActivationFeatures", out var af))
        {
            pi.IsGenerator = true;
            pi.StorageMax = GetInt(af, "StorageMax");
            pi.MaxCharges = GetInt(af, "MaxCharges");

            if (af.TryGetProperty("ActivationCycle", out var ac))
            {
                pi.FirstCycleStartDelayMs = GetLong(ac, "InitialCooldown");
                pi.MiniChargeCooldownMs = GetLong(ac, "MiniChargeCooldown");
                pi.ActivationAmountInCycle = GetInt(ac, "MiniChargesInSingleCharge");
                pi.HowManyGeneratedInCycle = GetInt(ac, "DropsInSingleMiniCharge");
                if (pi.HowManyGeneratedInCycle <= 0) pi.HowManyGeneratedInCycle = 1;
                pi.ActivationHowManyCycles = GetInt(ac, "HowManyCycles", -1);
                pi.RechargeTimeMs = GetLong(ac, "ChargeCooldown");
                if (ac.TryGetProperty("TimerSkipMultiplier", out var tsmEl) && tsmEl.ValueKind == JsonValueKind.Number)
                    pi.TimerSkipMultiplier = tsmEl.GetDouble();
            }

            // DecayAfterLastCycleProducer
            if (af.TryGetProperty("DecayAfterLastCycleProducer", out var dap))
            {
                string? dapConst = null;
                if (dap.ValueKind == JsonValueKind.String)
                    dapConst = dap.GetString();
                else if (dap.ValueKind == JsonValueKind.Object)
                {
                    dapConst = GetConstantFirst(dap);
                    if (dap.TryGetProperty("ControlledRandom", out var dapCr)
                        && dapCr.TryGetProperty("Odds", out var dapOdds)
                        && dapOdds.ValueKind == JsonValueKind.Object)
                        pi.DecayAfterLastCycleOdds = ParseOddsDictionary(dapOdds);
                }
                if (!string.IsNullOrEmpty(dapConst) && dapConst != "Empty")
                    pi.DecayAfterLastCycleItemType = dapConst;
            }

            // Drop odds
            pi.DropOdds = ExtractOddsTargeted(af);
        }

        // ── SpawnFeatures ──
        if (item.TryGetProperty("SpawnFeatures", out var sf))
        {
            pi.IsSpawner = true;
            pi.SpawnStorageMax = GetInt(sf, "StorageMax");

            if (sf.TryGetProperty("Spawn", out var spawn) && spawn.ValueKind == JsonValueKind.Object)
            {
                pi.SpawnItemType = GetConstantFirst(spawn);

                if (spawn.TryGetProperty("ControlledRandom", out var cr) &&
                    cr.TryGetProperty("Odds", out var odds) && odds.ValueKind == JsonValueKind.Object)
                    pi.SpawnOdds = ParseOddsDictionary(odds);

                if (pi.SpawnOdds == null && spawn.TryGetProperty("ControlledRandomSequence", out var crs) &&
                    crs.TryGetProperty("Odds", out var odds2) && odds2.ValueKind == JsonValueKind.Object)
                    pi.SpawnOdds = ParseOddsDictionary(odds2);

                if (pi.SpawnOdds == null && spawn.TryGetProperty("Random", out var rndSpawn) &&
                    rndSpawn.TryGetProperty("Odds", out var odds3))
                    pi.SpawnOdds = ParseOddsDictionary(odds3);
            }

            if (sf.TryGetProperty("SpawnCycle", out var sc))
            {
                pi.SpawnHowManyCycles = GetInt(sc, "HowManyCycles", -1);
                pi.SpawnAmountInCycle = GetInt(sc, "SpawnAmountInCycle");
                pi.SpawnDelayMs = GetLong(sc, "DelayBetweenCycles");
            }

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
                        pi.DecayIntoItemType = GetConstantFirst(ip);
                }
            }
        }

        // ── BubbleFeatures ──
        // Parsed unconditionally — SpawnOdds == 0 is a valid game value (means the item
        // does not naturally spawn bubbled, but BubbleDuration + OpenQuantity still apply
        // when bubbled via Black Card / Scissors). AB patches such as
        // Bubble_FS_Price_Optimization frequently mutate SpawnOdds + OpenQuantity
        // on items whose baseline SpawnOdds is 0, so the comparator must read this block.
        if (item.TryGetProperty("BubbleFeatures", out var bf) && bf.ValueKind == JsonValueKind.Object)
        {
            pi.HasBubble = true;
            pi.BubbleDurationMs = GetLong(bf, "BubbleDuration");
            pi.BubbleOpenCost = GetInt(bf, "OpenQuantity");
            pi.BubbleSpawnOdds = GetInt(bf, "SpawnOdds");
        }

        // ── SpeedUpCostGems ──
        if (item.TryGetProperty("SpeedUpCostGems", out var sucEl) && sucEl.ValueKind == JsonValueKind.Number)
            pi.SpeedUpCostGems = sucEl.GetInt32();

        // ── RechargeTimeMs fallback from SpawnFeatures ──
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

    /// <summary>
    /// Extracts drop odds from ActivationFeatures (mirrors DataService.ExtractOdds logic).
    /// </summary>
    private static Dictionary<string, double>? ExtractOddsTargeted(JsonElement af)
    {
        if (!af.TryGetProperty("ActivationSpawn", out var asp))
            goto fallback;

        if (asp.ValueKind == JsonValueKind.String)
        {
            var s = asp.GetString();
            return !string.IsNullOrEmpty(s) ? new Dictionary<string, double> { { s, 100.0 } } : null;
        }

        if (asp.ValueKind != JsonValueKind.Object)
            goto fallback;

        if (asp.TryGetProperty("ControlledRandom", out var directCr) &&
            directCr.TryGetProperty("Odds", out var odds1) && odds1.ValueKind == JsonValueKind.Object)
            return ParseOddsDictionary(odds1);

        if (asp.TryGetProperty("ControlledRandomSequence", out var directCrs) &&
            directCrs.TryGetProperty("Odds", out var odds2) && odds2.ValueKind == JsonValueKind.Object)
            return ParseOddsDictionary(odds2);

        var directConst = GetConstantFirst(asp);
        if (!string.IsNullOrEmpty(directConst))
            return new Dictionary<string, double> { { directConst, 100.0 } };

        if (asp.TryGetProperty("BaseProducer", out var bp))
        {
            if (bp.TryGetProperty("ControlledRandom", out var cr) &&
                cr.TryGetProperty("Odds", out var odds3) && odds3.ValueKind == JsonValueKind.Object)
                return ParseOddsDictionary(odds3);

            if (bp.TryGetProperty("ControlledRandomSequence", out var crs) &&
                crs.TryGetProperty("Odds", out var odds4) && odds4.ValueKind == JsonValueKind.Object)
                return ParseOddsDictionary(odds4);

            if (bp.TryGetProperty("Random", out var rnd) &&
                rnd.TryGetProperty("Odds", out var odds5) && odds5.ValueKind == JsonValueKind.Object)
                return ParseOddsDictionary(odds5);

            var constant = GetConstantFirst(bp);
            if (!string.IsNullOrEmpty(constant))
                return new Dictionary<string, double> { { constant, 100.0 } };
        }

        if (asp.TryGetProperty("PredefinedSequence", out var ps) &&
            ps.TryGetProperty("Odds", out var odds6) && odds6.ValueKind == JsonValueKind.Object)
            return ParseOddsDictionary(odds6);

        fallback:
        if (af.TryGetProperty("Odds", out var directOdds) && directOdds.ValueKind == JsonValueKind.Object)
            return ParseOddsDictionary(directOdds);

        return null;
    }

    // ── Diff computation ──

    /// <summary>
    /// Computes field-level diffs between base and variant items.
    /// </summary>
    private static List<FieldDiff> ComputeFieldDiffs(ParsedItem? baseItem, ParsedItem? varItem)
    {
        var diffs = new List<FieldDiff>();

        if (baseItem == null && varItem == null) return diffs;

        // If one side is missing entirely
        if (baseItem == null)
        {
            diffs.Add(new FieldDiff
            {
                FieldName = "(item)",
                BaseValue = "—",
                VariantValue = "present",
                Direction = DiffDirection.Changed
            });
            return diffs;
        }
        if (varItem == null)
        {
            diffs.Add(new FieldDiff
            {
                FieldName = "(item)",
                BaseValue = "present",
                VariantValue = "—",
                Direction = DiffDirection.Changed
            });
            return diffs;
        }

        // Basic
        CompareInt(diffs, "SellCoins", baseItem.SellCoins, varItem.SellCoins, higherIsBuff: true);

        // Generator fields (JSON names from ActivationFeatures / ActivationCycle)
        if (baseItem.IsGenerator || varItem.IsGenerator)
        {
            CompareInt(diffs, "StorageMax", baseItem.StorageMax, varItem.StorageMax, higherIsBuff: true);
            CompareInt(diffs, "MaxCharges", baseItem.MaxCharges, varItem.MaxCharges, higherIsBuff: true);
            CompareInt(diffs, "MiniChargesInSingleCharge", baseItem.ActivationAmountInCycle, varItem.ActivationAmountInCycle, higherIsBuff: true);
            CompareInt(diffs, "DropsInSingleMiniCharge", baseItem.HowManyGeneratedInCycle, varItem.HowManyGeneratedInCycle, higherIsBuff: true);
            CompareInt(diffs, "HowManyCycles", baseItem.ActivationHowManyCycles, varItem.ActivationHowManyCycles, higherIsBuff: true);
            CompareLong(diffs, "ChargeCooldown", baseItem.RechargeTimeMs, varItem.RechargeTimeMs, higherIsBuff: false);
            CompareLong(diffs, "MiniChargeCooldown", baseItem.MiniChargeCooldownMs, varItem.MiniChargeCooldownMs, higherIsBuff: false);
            CompareLong(diffs, "InitialCooldown", baseItem.FirstCycleStartDelayMs, varItem.FirstCycleStartDelayMs, higherIsBuff: false);
            CompareNullableInt(diffs, "SpeedUpCostGems", baseItem.SpeedUpCostGems, varItem.SpeedUpCostGems, higherIsBuff: false);

            // Drop odds
            CompareOdds(diffs, "DropOdds", baseItem.DropOdds, varItem.DropOdds);

            // DecayAfterLastCycleProducer
            CompareString(diffs, "DecayAfterLastCycleProducer", baseItem.DecayAfterLastCycleItemType, varItem.DecayAfterLastCycleItemType);
            CompareOdds(diffs, "DecayAfterLastCycleOdds", baseItem.DecayAfterLastCycleOdds, varItem.DecayAfterLastCycleOdds);
        }

        // Spawner fields (JSON names from SpawnFeatures / SpawnCycle)
        if (baseItem.IsSpawner || varItem.IsSpawner)
        {
            CompareString(diffs, "Spawn.Constant", baseItem.SpawnItemType, varItem.SpawnItemType);
            CompareInt(diffs, "SpawnCycle.HowManyCycles", baseItem.SpawnHowManyCycles, varItem.SpawnHowManyCycles, higherIsBuff: true);
            CompareInt(diffs, "SpawnCycle.SpawnAmountInCycle", baseItem.SpawnAmountInCycle, varItem.SpawnAmountInCycle, higherIsBuff: true);
            CompareLong(diffs, "SpawnCycle.DelayBetweenCycles", baseItem.SpawnDelayMs, varItem.SpawnDelayMs, higherIsBuff: false);
            CompareInt(diffs, "SpawnFeatures.StorageMax", baseItem.SpawnStorageMax, varItem.SpawnStorageMax, higherIsBuff: true);

            CompareOdds(diffs, "SpawnOdds", baseItem.SpawnOdds, varItem.SpawnOdds);
        }

        // Decay fields (JSON names from DecayFeatures)
        if (baseItem.HasDecay || varItem.HasDecay)
        {
            CompareBool(diffs, "DoesDecay", baseItem.HasDecay, varItem.HasDecay);
            CompareString(diffs, "ItemProducer", baseItem.DecayIntoItemType, varItem.DecayIntoItemType);
            CompareString(diffs, "DecayProducer", baseItem.SpawnDecayIntoItemType, varItem.SpawnDecayIntoItemType);
        }

        // Bubble fields (JSON names from BubbleFeatures)
        // Buff/nerf semantics from player perspective:
        //   SpawnOdds higher  → more bubbles encountered          → buff
        //   OpenQuantity      → cost in diamonds to pop           → lower is buff
        //   BubbleDuration    → time before bubble disappears     → higher is buff
        if (baseItem.HasBubble || varItem.HasBubble)
        {
            CompareInt(diffs, "BubbleFeatures.SpawnOdds", baseItem.BubbleSpawnOdds, varItem.BubbleSpawnOdds, higherIsBuff: true);
            CompareInt(diffs, "BubbleFeatures.OpenQuantity", baseItem.BubbleOpenCost, varItem.BubbleOpenCost, higherIsBuff: false);
            CompareLong(diffs, "BubbleFeatures.BubbleDuration", baseItem.BubbleDurationMs, varItem.BubbleDurationMs, higherIsBuff: true);
        }

        return diffs;
    }

    private static void CompareInt(List<FieldDiff> diffs, string name, int baseVal, int varVal, bool higherIsBuff)
    {
        if (baseVal == varVal) return;
        int delta = varVal - baseVal;
        diffs.Add(new FieldDiff
        {
            FieldName = name,
            BaseValue = baseVal.ToString(),
            VariantValue = varVal.ToString(),
            Direction = (varVal > baseVal) == higherIsBuff ? DiffDirection.Buff : DiffDirection.Nerf,
            DeltaText = delta > 0 ? $"+{delta}" : delta.ToString()
        });
    }

    private static void CompareLong(List<FieldDiff> diffs, string name, long baseVal, long varVal, bool higherIsBuff)
    {
        if (baseVal == varVal) return;
        long delta = varVal - baseVal;
        string sign = delta > 0 ? "+" : "-";
        diffs.Add(new FieldDiff
        {
            FieldName = name,
            BaseValue = FormatMs(baseVal),
            VariantValue = FormatMs(varVal),
            Direction = (varVal > baseVal) == higherIsBuff ? DiffDirection.Buff : DiffDirection.Nerf,
            DeltaText = sign + FormatMs(Math.Abs(delta))
        });
    }

    private static void CompareNullableInt(List<FieldDiff> diffs, string name, int? baseVal, int? varVal, bool higherIsBuff)
    {
        if (baseVal == varVal) return;
        string deltaText = "";
        if (baseVal.HasValue && varVal.HasValue)
        {
            int delta = varVal.Value - baseVal.Value;
            deltaText = delta > 0 ? $"+{delta}" : delta.ToString();
        }
        diffs.Add(new FieldDiff
        {
            FieldName = name,
            BaseValue = baseVal?.ToString() ?? "—",
            VariantValue = varVal?.ToString() ?? "—",
            Direction = baseVal == null || varVal == null
                ? DiffDirection.Changed
                : (varVal > baseVal) == higherIsBuff ? DiffDirection.Buff : DiffDirection.Nerf,
            DeltaText = deltaText
        });
    }

    private static void CompareBool(List<FieldDiff> diffs, string name, bool baseVal, bool varVal)
    {
        if (baseVal == varVal) return;
        diffs.Add(new FieldDiff
        {
            FieldName = name,
            BaseValue = baseVal.ToString(),
            VariantValue = varVal.ToString(),
            Direction = DiffDirection.Changed
        });
    }

    private static void CompareString(List<FieldDiff> diffs, string name, string? baseVal, string? varVal)
    {
        var b = baseVal ?? "";
        var v = varVal ?? "";
        if (string.Equals(b, v, StringComparison.Ordinal)) return;
        diffs.Add(new FieldDiff
        {
            FieldName = name,
            BaseValue = string.IsNullOrEmpty(b) ? "—" : b,
            VariantValue = string.IsNullOrEmpty(v) ? "—" : v,
            Direction = DiffDirection.Changed
        });
    }

    private static void CompareOdds(List<FieldDiff> diffs, string prefix,
        Dictionary<string, double>? baseOdds, Dictionary<string, double>? varOdds)
    {
        var bKeys = baseOdds?.Keys ?? Enumerable.Empty<string>();
        var vKeys = varOdds?.Keys ?? Enumerable.Empty<string>();
        var allKeys = new SortedSet<string>(bKeys.Concat(vKeys), StringComparer.Ordinal);

        foreach (var key in allKeys)
        {
            var bVal = baseOdds != null && baseOdds.TryGetValue(key, out var bv) ? bv : 0;
            var vVal = varOdds != null && varOdds.TryGetValue(key, out var vv) ? vv : 0;

            if (Math.Abs(bVal - vVal) < 0.0001) continue;

            var bPresent = baseOdds?.ContainsKey(key) == true;
            var vPresent = varOdds?.ContainsKey(key) == true;
            double delta = vVal - bVal;

            diffs.Add(new FieldDiff
            {
                FieldName = $"{prefix}: {key}",
                BaseValue = bPresent ? $"{bVal:0.##}%" : "—",
                VariantValue = vPresent ? $"{vVal:0.##}%" : "—",
                Direction = vVal > bVal ? DiffDirection.Buff : DiffDirection.Nerf,
                DeltaText = (bPresent && vPresent) ? $"{(delta > 0 ? "+" : "")}{delta:0.##}%" : ""
            });
        }
    }

    private static string FormatMs(long ms)
    {
        if (ms <= 0) return "0";
        var ts = TimeSpan.FromMilliseconds(ms);
        int h = (int)ts.TotalHours;
        int m = ts.Minutes;
        int s = ts.Seconds;

        if (h > 0 && m > 0) return $"{h}h {m}m";
        if (h > 0) return $"{h}h";
        if (m > 0 && s > 0) return $"{m}m {s}s";
        if (m > 0) return $"{m}m";
        return $"{s}s";
    }
}
