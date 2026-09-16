using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using GameLogic.Config;
using GameLogic.Hotspots.CardStack;
using Code.GameLogic.Hotspots;
using MergeMansionWikiTools.Dumper;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Localization;
using Metaplay.Unity;

namespace DumpHarness;

// Minimal console harness that runs only ExperimentalDumper and prints SpeedUpCostBehavior.
// Used to validate SharedGlobals serialization diagnostics without booting the WPF app.
internal static class Program
{
    /// <summary>Dump engine used by every dump sub-command; selected with the global --engine flag.</summary>
    private static IDumpEngine Engine = new LegacyDumpEngineHarness();

    public static int Main(string[] args)
    {
        // Global option: --engine legacy|native (default legacy). Parsed and removed from args
        // before any sub-command dispatch so positional arguments keep their existing indices.
        int engIdx = Array.IndexOf(args, "--engine");
        if (engIdx >= 0)
        {
            var engValue = engIdx + 1 < args.Length ? args[engIdx + 1] : null;
            if (engValue != null && engValue.Equals("native", StringComparison.OrdinalIgnoreCase))
                Engine = new NativeDumpEngine();
            else if (engValue == null || !engValue.Equals("legacy", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("[WARN] --engine expects legacy|native, using Legacy");
            // Drop the flag (and its value, when one was given) so positional indices stay intact.
            var engLast = engValue != null ? engIdx + 1 : engIdx;
            args = args.Where((_, i) => i < engIdx || i > engLast).ToArray();
            Console.WriteLine($"[engine] {Engine.Kind}");
        }

        // Sub-command: probe Unity bundles for minigame icon assets
        if (args.Length >= 2 && args[0] == "--probe-minigame-bundles")
        {
            return ProbeBundles(args[1]);
        }
        if (args.Length >= 2 && args[0] == "--probe-fishing")
        {
            // --probe-fishing <configPath> [<languagePath>]
            // Prints FishingSettings (GameConfigKeyValue): SmallFish/NonFish WaterDropletCounts
            // (splash type → droplet count) + weight-category odds/size arrays.
            return ProbeFishing(args[1], args.Length >= 3 ? args[2] : null);
        }
        if (args.Length >= 3 && args[0] == "--probe-chest")
        {
            // --probe-chest <configPath> <itemTypeSubstring>
            // Prints ChestFeatures loot structure for matching items: HowManyToRoll, producer type,
            // and for PrefixProducer the guaranteed prefix Items sequence (resolved ItemTypes).
            return ProbeChest(args[1], args[2]);
        }
        if (args.Length >= 3 && args[0] == "--probe-pool")
        {
            return ProbePool(args[1], args[2], args.Length >= 4 ? int.Parse(args[3]) : 2);
        }
        if (args.Length >= 2 && args[0] == "--probe-one")
        {
            return ProbeOneBundle(args[1]);
        }
        if (args.Length >= 4 && args[0] == "--extract-minigame-icons")
        {
            // --extract-minigame-icons <gameFilesRoot> <outputDir> <tpkPath>
            return ExtractIcons(args[1], args[2], args[3]);
        }
        if (args.Length >= 2 && args[0] == "--probe-hotspot-map")
        {
            // --probe-hotspot-map <xapk|apk|global-metadata.dat> [<HotspotId.cs to diff against>]
            return ProbeHotspotMap(args[1], args.Length >= 3 ? args[2] : null);
        }
        if (args.Length >= 3 && args[0] == "--dump-stories")
        {
            // --dump-stories <configPath> <out.json>  — StoryElements: story id -> ordered DialogItem ids
            // (+ music, follow-up stories triggered on completion). The app's DialogueDumper flattens this away.
            return DumpStories(args[1], args[2]);
        }
        if (args.Length >= 3 && args[0] == "--dump-loc")
        {
            // --dump-loc <mpcOrLFile> <out.json>  — every translation of one language file as JSON.
            // Accepts a Metaplay L-file (name = ContentHash) or an APK assets/Localizations/en.mpc.
            return DumpLoc(args[1], args[2]);
        }
        if (args.Length >= 4 && args[0] == "--probe-loc")
        {
            // --probe-loc <configPath> <languagePath> <regexPattern>
            return ProbeLoc(args[1], args[2], args[3]);
        }
        if (args.Length >= 3 && args[0] == "--probe-sprite-fields")
        {
            // --probe-sprite-fields <bundlePath> <tpkPath> [<spriteNameFilter>]
            string spriteFilter = args.Length >= 4 ? args[3] : "";
            return ProbeSpriteFields(args[1], args[2], spriteFilter);
        }
        if (args.Length >= 3 && args[0] == "--probe-atlas-fields")
        {
            // --probe-atlas-fields <bundlePath> <tpkPath> [<spriteNameFilter>]
            string atlasFilter = args.Length >= 4 ? args[3] : "";
            return ProbeAtlasFields(args[1], args[2], atlasFilter);
        }
        if (args.Length >= 5 && args[0] == "--dump-events-patched")
        {
            // --dump-events-patched <configPath> <patchPath> <languagePath> <outputDir>
            return DumpEventsPatched(args[1], args[2], args[3], args[4]);
        }
        if (args.Length >= 4 && args[0] == "--probe-bubble")
        {
            // --probe-bubble <_DATA_dir> <itemPrefix> <languagePath>
            // Scans every (configArchive × patchFile × (expId,varId)) tuple individually
            // (no dedup) and probes BubbleFeatures.SpawnOdds for matching items.
            return ProbeBubble(args[1], args[2], args[3]);
        }
        if (args.Length >= 4 && args[0] == "--dump-chain")
        {
            // --dump-chain <configPath> <languagePath> <outputDir>
            // Runs only MergeChainDumper → chain_item_odds.json. Used to validate
            // ConstantProducer multi-product serialization (v0.20.73+).
            return DumpChain(args[1], args[2], args[3]);
        }
        if (args.Length >= 2 && args[0] == "--decode-lastsession")
        {
            // --decode-lastsession <datFile>  — deserialize Metaplay_LastSessionGameConfig.dat and
            // print BaselineVersion + PatchesVersion ContentHashes (= which config/patch blob the game
            // used LAST session — the authoritative "current" set, vs stale cached blobs).
            try
            {
                MetaplayCore.Initialize();
                var bytes = File.ReadAllBytes(args[1]);
                var info = Metaplay.Core.Serialization.MetaSerialization.DeserializeTagged<Metaplay.Unity.ConnectionGameConfigInfo>(
                    bytes, Metaplay.Core.Serialization.MetaSerializationFlags.IncludeAll, null, null, null);
                Console.WriteLine($"BaselineVersion (config) = {info.BaselineVersion}");
                Console.WriteLine($"PatchesVersion (patch set) = {info.PatchesVersion}");
                Console.WriteLine($"ExperimentMemberships = {info.ExperimentMemberships?.Count ?? 0}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }
        if (args.Length >= 2 && args[0] == "--list-patches")
        {
            // --list-patches <patchPath (dir or file)>
            // For each patch file: print its Version (ContentHash of the baseline config it targets)
            // + the (expId_varId) labels it contains. No chain dump — cheap. Used to see the
            // multi-snapshot picture (which file has which branches) and pick the newest.
            try
            {
                MetaplayCore.Initialize();
                var files = new List<string>();
                if (Directory.Exists(args[1])) files.AddRange(Directory.GetFiles(args[1]).OrderBy(x => x));
                else if (File.Exists(args[1])) files.Add(args[1]);
                foreach (var pf in files)
                {
                    try
                    {
                        var sp = Metaplay.Core.Config.GameConfigSpecializationPatches.FromBytes(File.ReadAllBytes(pf));
                        var labels = new List<string>();
                        foreach (var (expId, vs) in sp.Patches)
                            foreach (var (varId, _) in vs)
                                labels.Add($"{expId}_{varId}");
                        labels.Sort(StringComparer.Ordinal);
                        Console.WriteLine($"{Path.GetFileName(pf)}  Version={sp.Version}  patches={labels.Count}");
                        Console.WriteLine($"    {string.Join(", ", labels)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{Path.GetFileName(pf)}  FAILED: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"FATAL: {ex.Message}"); return 1; }
        }
        if (args.Length >= 3 && args[0] == "--compare-engines")
        {
            // --compare-engines <outDir> live|newest|all|<ver> [--filters] [--only <filePrefix>]
            // Runs the Legacy and the Native dump engine over the same config(s) and byte-compares
            // every produced file. _DATA is taken from MMWT_DATA when set. Exit code 0 only when
            // every row is IDENTICAL.
            // "newest" = only the newest-CreatedAt archive in _DATA/C (with all its patches) —
            // a fast single-archive loop for iterating on one dumper.
            // --only <filePrefix> restricts the compared files to those whose name starts with the
            // prefix (patch rows included), e.g. "--only chain".
            var onlyIdx = Array.IndexOf(args, "--only");
            if (onlyIdx >= 0 && onlyIdx + 1 >= args.Length)
            {
                Console.Error.WriteLine("ERROR: --only matched no files (no prefix given)");
                return 2;
            }
            var only = onlyIdx >= 0 ? args[onlyIdx + 1] : null;
            return CompareEngines(args[1], args[2], args.Contains("--filters"), only);
        }
        if (args.Length >= 5 && args[0] == "--dump-full-patched")
        {
            // --dump-full-patched <configPath> <patchPath> <languagePath> <outputDir>
            // FULL per-patch dump (chain_item_odds/areas/events/card_collection/dialogues/Pets),
            // base + per-patch subfolders — mirrors the real app dump for a single patch file.
            return DumpFullPatched(args[1], args[2], args[3], args[4]);
        }
        if (args.Length >= 5 && args[0] == "--dump-areas-patched")
        {
            // --dump-areas-patched <configPath> <patchPath> <languagePath> <outputDir>
            // Mirror of --dump-chain-patched but runs AreaDumper → per-patch areas.json.
            return DumpAreasPatched(args[1], args[2], args[3], args[4]);
        }
        if (args.Length >= 5 && args[0] == "--dump-chain-patched")
        {
            // --dump-chain-patched <configPath> <patchPath> <languagePath> <outputDir>
            // Mirror of --dump-events-patched but runs MergeChainDumper → per-patch
            // chain_item_odds.json. Diagnoses producer-balance AB branches (Items/MergeChains)
            // that the events-only probe cannot see. Reproduces the production chain patch
            // pipeline 1:1 (distinct-content dedup, all versions kept).
            return DumpChainPatched(args[1], args[2], args[3], args[4]);
        }
        if (args.Length >= 4 && args[0] == "--probe-hotspot")
        {
            // --probe-hotspot <configPath> <languagePath> <idSubstring>
            // Prints HotspotDefinition fields relevant for description resolution
            // (DescriptionLocalizationId, MultistepGroupId) + all loc key attempts.
            // Used to diagnose tasks with missing Description in areas.json.
            return ProbeHotspot(args[1], args[2], args[3]);
        }
        if (args.Length >= 4 && args[0] == "--probe-prefab-skeleton")
        {
            // --probe-prefab-skeleton <bundlePath> <tpkPath> <prefabNameFilter>
            return ProbePrefabSkeleton(args[1], args[2], args[3]);
        }
        if (args.Length >= 3 && args[0] == "--probe-bundle-skeletons")
        {
            // --probe-bundle-skeletons <bundleDir> <tpkPath> [nameFilter]
            return ProbeBundleSkeletons(args[1], args[2], args.Length >= 4 ? args[3] : "",
                args.Length >= 5 ? args[4] : "");
        }
        if (args.Length >= 4 && args[0] == "--dump-config-library")
        {
            // --dump-config-library <configPath> <languagePath> <libraryName> [keyFilter]
            return DumpConfigLibrary(args[1], args[2], args[3], args.Length >= 5 ? args[4] : "",
                args.Length >= 6 ? args[5] : "", args.Length >= 7 ? args[6] : "");
        }
        if (args.Length >= 4 && args[0] == "--find-config-refs")
        {
            // --find-config-refs <configPath> <languagePath> <needle>
            // Reports every place in EVERY config library that references the needle
            // (ItemType, ConfigKey, MetaRef key or plain string).
            return FindConfigRefs(args[1], args[2], args[3],
                args.Length >= 5 ? args[4] : "", args.Length >= 6 ? args[5] : "");
        }
        if (args.Length >= 3 && args[0] == "--probe-player-levels")
        {
            // --probe-player-levels <configPath> <languagePath>
            // Prints MaxPlayerLevel + per-level NextLevelExperience (with cumulative)
            // and reward summary from the PlayerLevels config library.
            return ProbePlayerLevels(args[1], args[2]);
        }
        if (args.Length >= 3 && args[0] == "--probe-energy-modes")
        {
            // --probe-energy-modes <configPath> <languagePath>
            // Prints EnergyModes library (Supercharge/Hypercharge): multipliers + LevelUpChance.
            return ProbeEnergyModes(args[1], args[2]);
        }
        if (args.Length >= 3 && args[0] == "--probe-board")
        {
            // --probe-board <configPath> <languagePath> [<boardIdSubstring>]
            // Prints Boards library entries: BoardId, size, EnergyType, ItemSellCost
            // (per-board sell price override — source of "1 coin on event boards").
            return ProbeBoard(args[1], args[2], args.Length >= 4 ? args[3] : "");
        }

        if (args.Length >= 3 && args[0] == "--pull-phone-file")
        {
            // --pull-phone-file <relPathUnderPackage> <localOut>
            // Downloads a single file from the game package over MTP, path relative to the
            // package root (e.g. "cache/Metaplay_LastSessionGameConfig.dat" or "files/il2cpp/unity.ver").
            return PullPhoneFile(args[1], args[2]);
        }
        if (args.Length >= 2 && args[0] == "--list-phone-dir")
        {
            // --list-phone-dir <relPath under storage root, "" = list roots> [nameFilter] [limit]
            return ListPhoneDir(args[1], args.Length >= 3 ? args[2] : "",
                args.Length >= 4 && int.TryParse(args[3], out var lim) ? lim : 30);
        }
        if (args.Length >= 3 && args[0] == "--pull-phone-dir")
        {
            // --pull-phone-dir <relPath> <localOut> [nameFilter] [sinceDate yyyy-MM-dd]
            return PullPhoneDir(args[1], args[2], args.Length >= 4 ? args[3] : "",
                args.Length >= 5 ? args[4] : "");
        }
        if (args[0] == "--probe-phone-tree")
        {
            // --probe-phone-tree [maxDepth]
            // Recursively lists the game's cache/ directory tree over MTP to find where the
            // ACTIVE experiment patch lives (e.g. a Testing-phase tester patch NOT in the
            // public SharedGameConfigPatches blob cache). Default depth 4.
            int depth = args.Length >= 2 && int.TryParse(args[1], out var d) ? d : 4;
            return ProbePhoneTree(depth);
        }
        if (args.Length >= 2 && args[0] == "--probe-createdat")
        {
            // --probe-createdat <C dir>  — read each archive's header CreatedAt WITHOUT
            // MetaplayCore.Initialize, to prove the selection can run during Pull from Phone
            // (which may happen before any dump has initialized Metaplay).
            foreach (var f in Directory.GetFiles(args[1]).OrderBy(x => x))
            {
                try
                {
                    var a = Metaplay.Core.Config.ConfigArchive.FromBytes(File.ReadAllBytes(f));
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(a.CreatedAt.MillisecondsSinceEpoch);
                    Console.WriteLine($"{Path.GetFileName(f)}  CreatedAt={dt:yyyy-MM-dd HH:mm:ss}Z  (NO init)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{Path.GetFileName(f)}  FAILED (no init): {ex.GetType().Name}: {ex.Message}");
                }
            }
            return 0;
        }

        if (args.Length >= 3 && args[0] == "--probe-schedule")
        {
            // --probe-schedule <C dir (or single config file)> <eventIdSubstring>
            // For every config archive: print archive.CreatedAt (header) + for each matching
            // CollectibleBoardEvent the full schedule incl. Recurrence/NumRepeats (the two
            // MetaMembers the JSON dump drops). Proves data integrity (newest archive) and
            // whether a config-level recurrence drives re-airings.
            return ProbeSchedule(args[1], args[2]);
        }

        if (args.Length >= 5 && args[0] == "--probe-meta-members")
        {
            // --probe-meta-members <configPath> <languagePath> <libraryName> <keyFilter>
            // Prints every [MetaMember] of the matching library entries the way the native dumpers
            // see them: TagId, declaring visibility, runtime type, and — for a MetaRef — whether it
            // is resolved and what its key renders as. Written for the events dump, where golden
            // keeps some empty-key references as null and drops others, and the only way to tell
            // which rule separates them is to look at the actual member state.
            return ProbeMetaMembers(args[1], args[2], args[3], args[4]);
        }
        if (args.Length >= 3 && args[0] == "--probe-daily-challenges")
        {
            // --probe-daily-challenges <configPath> <patchPath (dir/file) or "-"> [<patchLabelFilter>]
            // Prints the DailyChallenges V2 system (Daily Scoop revamp, 2026-06): the 8
            // DailyChallenges* config libraries the JSON dump does not export — weekly event
            // timeline (CoreSupportEvents EventType=DailyChallengesEvent), week selection maps
            // (ByMinigameId segments + ByPreviousCompletion adaptive difficulty), weeks with
            // milestone ladders, day compositions and standard/special objectives with reward
            // pools. Optionally re-runs with each patch matching <patchLabelFilter> applied
            // (e.g. "DailyChallenges" → DailyChallenges_03_B), since the account may see the
            // system only through an AB branch.
            return ProbeDailyChallenges(args[1], args[2], args.Length >= 4 ? args[3] : null);
        }
        if (args.Length >= 2 && args[0] == "--probe-segments")
        {
            // --probe-segments <configPath> [<idFilter>]
            // Prints PlayerSegments definitions: id, display name and the basic condition
            // (property requirements with min/max + segment references). Use to answer
            // "what are the exact bounds of segment X" (e.g. Spenders_LT_500).
            return ProbeSegments(args[1], args.Length >= 3 ? args[2] : null);
        }
        if (args.Length >= 2 && args[0] == "--probe-inventory")
        {
            // --probe-inventory <configPath>
            // Prints the InventorySlots library (garage/pocket inventory): slot id, currency
            // and cost per slot. Cost=0 rows = slots the player starts with for free.
            // Used to answer "how many base inventory slots are there" for the Predictor.
            return ProbeInventory(args[1]);
        }
        if (args.Length >= 2 && args[0] == "--probe-daily-scoop")
        {
            // --probe-daily-scoop <C dir (or single config file)> [<languagePath>]
            // Per config archive: print CreatedAt + the DailyScoopEvents library, which the
            // JSON dump does NOT export — it carries the calendar Schedule, the ordered
            // WeekIds rotation and the WeekSegments gating (which week variant a segment
            // gets). Plus a per-week summary (milestone point ladder + final reward) so a
            // retuned week revision is visible per archive without a full dump.
            return ProbeDailyScoop(args[1], args.Length >= 3 ? args[2] : null);
        }

        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: DumpHarness <configPath> <languagePath> <outputDir>");
            Console.Error.WriteLine("   or: DumpHarness --probe-minigame-bundles <APK/Game Files/APK dir>");
            Console.Error.WriteLine("   or: DumpHarness --probe-loc <configPath> <languagePath> <regexPattern>");
            return 2;
        }

        string configPath = args[0];
        string languagePath = args[1];
        string outputDir = args[2];

        // Optional: --hotspot-map <xapk|apk|global-metadata.dat> — load HotspotId names from the
        // game binary's metadata before dumping (mirrors HotspotIdMapService in the app). Lets a
        // side dump prove the runtime map path even when the compiled enum is stale.
        int mapArg = Array.IndexOf(args, "--hotspot-map");
        if (mapArg >= 0 && mapArg + 1 < args.Length)
        {
            var src = args[mapArg + 1];
            var bytes = src.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                ? File.ReadAllBytes(src)
                : GameLogic.Il2Cpp.Il2CppMetadataEnumReader.ExtractGlobalMetadata(src);
            var members = GameLogic.Il2Cpp.Il2CppMetadataEnumReader.ReadEnum(bytes, "HotspotId");
            GameLogic.HotspotIdNames.Load(
                members.Select(m => new KeyValuePair<int, string>(m.Value, m.Name)), Path.GetFileName(src));
            Console.WriteLine($"HotspotId map loaded: {GameLogic.HotspotIdNames.LoadedCount} members from {Path.GetFileName(src)}");
        }

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Config file not found: {configPath}");
            return 2;
        }

        Directory.CreateDirectory(outputDir);

        Console.WriteLine("=== DumpHarness (SpeedUpCostBehavior diagnostic) ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Language: {languagePath}");
        Console.WriteLine($"Output:   {outputDir}");

        try
        {
            Console.WriteLine("[1/4] Initializing MetaplayCore...");
            MetaplayCore.Initialize();

            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                Console.WriteLine("[2/4] Loading language file...");
                var langFileName = Path.GetFileName(languagePath);
                var langHash = ContentHash.ParseString(langFileName);
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }

            Console.WriteLine("[3/4] Importing SharedGameConfig...");
            var archiveBytes = File.ReadAllBytes(configPath);
            var archive = ConfigArchive.FromBytes(archiveBytes);
            Console.WriteLine($"        Archive: {archive.Entries.Count} entries");
            var patchedArchive = PatchedConfigArchive.WithNoPatches(archive);
            var gameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
            ClientGlobal.SharedGameConfig = gameConfig;

            Console.WriteLine("[4/4] Running ExperimentalDumper...");
            var written = Engine.DumpExperimental(gameConfig, outputDir);
            foreach (var (section, path) in written)
                Console.WriteLine($"        -> {section}.json ({new FileInfo(path).Length / 1024} KB)");

            // Also dump areas.json so we can verify Theme field for IllustrationTask/CardStack hotspots.
            Console.WriteLine("[4b/4] Running AreaDumper...");
            var areasPath = Path.Combine(outputDir, "areas.json");
            Engine.DumpAreas(gameConfig, areasPath);
            Console.WriteLine($"        -> areas.json ({new FileInfo(areasPath).Length / 1024} KB)");

            Console.WriteLine();
            Console.WriteLine("=== SharedGlobals.SpeedUpCostBehavior probe ===");
            var speedUp = gameConfig.SharedGlobals?.SpeedUpCostBehavior;
            if (speedUp == null)
            {
                Console.WriteLine("Runtime object: SpeedUpCostBehavior = null (property not populated by deserializer)");
            }
            else
            {
                Console.WriteLine($"Runtime object: SpeedUpCostBehavior populated:");
                Console.WriteLine($"  SecondsPerGem      = {speedUp.SecondsPerGem.Double}");
                Console.WriteLine($"  FirstDiscount      = {speedUp.FirstDiscount.Double}");
                Console.WriteLine($"  FirstDiscountTime  = {speedUp.FirstDiscountTime.Double}");
                Console.WriteLine($"  SecondDiscount     = {speedUp.SecondDiscount.Double}");
                Console.WriteLine($"  SecondDiscountTime = {speedUp.SecondDiscountTime.Double}");
            }

            // ── Byte-level probe: inspect raw SharedGlobals.mpc entry bytes ──
            Console.WriteLine();
            Console.WriteLine("=== Raw SharedGlobals.mpc byte scan ===");
            try
            {
                var entryByName = typeof(ConfigArchive).GetMethod("GetEntryByName")
                    ?? typeof(ConfigArchive).GetMethod("GetEntry");
                var entries = archive.Entries;
                var sharedGlobalsEntry = entries.FirstOrDefault(e => e.Name == "SharedGlobals.mpc" || e.Name == "SharedGlobals");
                if (sharedGlobalsEntry == null)
                {
                    Console.WriteLine("SharedGlobals entry NOT FOUND in archive entries!");
                    Console.WriteLine("First 5 entry names: " + string.Join(", ", entries.Take(5).Select(e => e.Name)));
                }
                else
                {
                    var bytes = sharedGlobalsEntry.Uncompress();
                    Console.WriteLine($"Entry: {sharedGlobalsEntry.Name}, raw={sharedGlobalsEntry.RawBytes.Length} B, uncompressed={bytes.Length} B, compression={sharedGlobalsEntry.Compression}");
                    int maxDump = Math.Min(bytes.Length, 2048);
                    Console.WriteLine($"First {maxDump} bytes (hex + ASCII):");
                    for (int off = 0; off < maxDump; off += 32)
                    {
                        int count = Math.Min(32, maxDump - off);
                        var hexParts = new string[count];
                        var asciiChars = new char[count];
                        for (int i = 0; i < count; i++)
                        {
                            byte b = bytes[off + i];
                            hexParts[i] = b.ToString("x2");
                            asciiChars[i] = (b >= 32 && b < 127) ? (char)b : '.';
                        }
                        Console.WriteLine($"  {off:x4}: {string.Join(" ", hexParts).PadRight(95)}  {new string(asciiChars)}");
                    }
                    // Write full bytes for later analysis
                    var rawPath = Path.Combine(outputDir, "SharedGlobals.raw");
                    File.WriteAllBytes(rawPath, bytes);
                    Console.WriteLine($"Full raw bytes written to: {rawPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Raw scan error: {ex.Message}");
            }

            // ── Special task libraries: CardStacks + CustomTables (for Illustrations) ──
            Console.WriteLine();
            Console.WriteLine("=== Special task libraries ===");
            try
            {
                if (gameConfig.CardStacks != null)
                {
                    Console.WriteLine($"CardStacks ({gameConfig.CardStacks.Count} entries):");
                    Console.WriteLine($"  {"ConfigKey",-30} {"Style",-10} {"Theme",-30} {"Size"}");
                    foreach (var kv in gameConfig.CardStacks.EnumerateAll())
                    {
                        var info = (CardStackInfo)kv.Value;
                        Console.WriteLine($"  {kv.Key,-30} {info.Style,-10} {(info.Theme ?? "<null>"),-30} {info.Width}x{info.Height}");
                    }
                }
                else
                {
                    Console.WriteLine("CardStacks = null");
                }

                Console.WriteLine();
                if (gameConfig.CustomTables != null)
                {
                    Console.WriteLine($"CustomTables (Illustrations) ({gameConfig.CustomTables.Count} entries):");
                    Console.WriteLine($"  {"ConfigKey",-40} {"Theme"}");
                    foreach (var kv in gameConfig.CustomTables.EnumerateAll())
                    {
                        var info = (CustomHotspotTablesInfo)kv.Value;
                        Console.WriteLine($"  {kv.Key,-40} {(info.Theme ?? "<null>")}");
                    }
                }
                else
                {
                    Console.WriteLine("CustomTables = null");
                }

                // ── HotspotDefinitions grouped by CustomHotspotTableId + CardStackRef (sub-tasks) ──
                // Public property 'HotspotTableId' aliases private 'CustomHotspotTableId'.
                // CardStackRef is private — read via reflection.
                Console.WriteLine();
                Console.WriteLine("=== HotspotDefinitions grouped by CustomHotspotTableId ===");
                if (gameConfig.HotspotDefinitions != null)
                {
                    var tableFld = typeof(GameLogic.Hotspots.HotspotDefinition).GetField(
                        "<CustomHotspotTableId>k__BackingField",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var cardStackFld = typeof(GameLogic.Hotspots.HotspotDefinition).GetField(
                        "<CardStackRef>k__BackingField",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    var groupedT = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<GameLogic.Hotspots.HotspotDefinition>>();
                    var groupedC = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<GameLogic.Hotspots.HotspotDefinition>>();

                    foreach (var kv in gameConfig.HotspotDefinitions.EnumerateAll())
                    {
                        var def = (GameLogic.Hotspots.HotspotDefinition)kv.Value;
                        var tableId = tableFld?.GetValue(def);
                        if (tableId != null)
                        {
                            var key = tableId.ToString();
                            if (!string.IsNullOrEmpty(key))
                            {
                                if (!groupedT.TryGetValue(key, out var list))
                                    groupedT[key] = list = new System.Collections.Generic.List<GameLogic.Hotspots.HotspotDefinition>();
                                list.Add(def);
                            }
                        }
                        var csRef = cardStackFld?.GetValue(def);
                        if (csRef != null)
                        {
                            string csKey = null;
                            try
                            {
                                var keyObjProp = csRef.GetType().GetProperty("KeyObject") ?? csRef.GetType().GetProperty("Key");
                                csKey = keyObjProp?.GetValue(csRef)?.ToString() ?? csRef.ToString();
                            }
                            catch { csKey = null; }
                            if (string.IsNullOrEmpty(csKey)) csKey = "<unresolved-ref>";
                            if (!groupedC.TryGetValue(csKey, out var list2))
                                groupedC[csKey] = list2 = new System.Collections.Generic.List<GameLogic.Hotspots.HotspotDefinition>();
                            list2.Add(def);
                        }
                    }

                    Console.WriteLine($"Total HotspotDefinitions with CustomHotspotTableId: {groupedT.Values.Sum(l => l.Count)} across {groupedT.Count} tables");
                    foreach (var g in groupedT.OrderBy(x => x.Key))
                    {
                        Console.WriteLine($"\n--- Table: {g.Key} ({g.Value.Count} hotspots) ---");
                        foreach (var def in g.Value.OrderBy(d => d.Id.ToString()).Take(30))
                        {
                            string desc = "";
                            try { desc = LocMan.GetHotspotDescription(def.Id) ?? ""; } catch { }
                            if (string.IsNullOrEmpty(desc)) desc = "(no loc)";
                            string reqs = "";
                            if (def.RequirementsList != null)
                                reqs = string.Join(", ", def.RequirementsList.Select(r => r?.GetType().Name ?? "null"));
                            Console.WriteLine($"   {def.Id,-55} Type={def.Type,-18} desc='{desc}' reqs=[{reqs}]");
                        }
                    }

                    Console.WriteLine();
                    Console.WriteLine("=== HotspotDefinitions grouped by CardStackRef ===");
                    Console.WriteLine($"Total HotspotDefinitions with CardStackRef: {groupedC.Values.Sum(l => l.Count)} across {groupedC.Count} stacks");
                    foreach (var g in groupedC.OrderBy(x => x.Key).Take(5))
                    {
                        Console.WriteLine($"\n--- CardStack: {g.Key} ({g.Value.Count} hotspots) — sample of up to 5 ---");
                        foreach (var def in g.Value.Take(5))
                        {
                            string desc = "";
                            try { desc = LocMan.GetHotspotDescription(def.Id) ?? ""; } catch { }
                            if (string.IsNullOrEmpty(desc)) desc = "(no loc)";
                            Console.WriteLine($"   {def.Id,-55} Type={def.Type,-15} desc='{desc}'");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Library dump error: {ex.GetType().Name}: {ex.Message}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.Error.WriteLine($"Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                Console.Error.WriteLine(ex.InnerException.StackTrace);
            }
            return 1;
        }
    }

    // Bundle probe — list asset names in minigame-related Unity bundles to find icons.
    // ── --dump-events-patched: import config + apply each specialization patch + dump events.json per patch ──
    private static int DumpEventsPatched(string configPath, string patchPath, string languagePath, string outputDir)
    {
        Console.WriteLine("=== DumpHarness --dump-events-patched ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Patches:  {patchPath}");
        Console.WriteLine($"Language: {languagePath}");
        Console.WriteLine($"Output:   {outputDir}");
        Directory.CreateDirectory(outputDir);

        try
        {
            Console.WriteLine("[1] Initializing MetaplayCore...");
            MetaplayCore.Initialize();

            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                Console.WriteLine("[2] Loading language...");
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }

            Console.WriteLine("[3] Importing SharedGameConfig...");
            var archiveBytes = File.ReadAllBytes(configPath);
            var archive = ConfigArchive.FromBytes(archiveBytes);
            var patchedArchive = PatchedConfigArchive.WithNoPatches(archive);
            var masterConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
            ClientGlobal.SharedGameConfig = masterConfig;

            Console.WriteLine("[4] Dumping base events.json...");
            var basePath = Path.Combine(outputDir, "events.json");
            Engine.DumpEvents(masterConfig, basePath, EventFilters.All);
            Console.WriteLine($"        -> events.json ({new FileInfo(basePath).Length / 1024} KB)");

            if (!string.IsNullOrEmpty(patchPath))
            {
                Console.WriteLine("[5] Loading patches...");
                var configPatches = LoadDistinctPatches(patchPath);

                foreach (var (expId, varId, rawBytes, env, folderLabel) in configPatches)
                {
                    var entries = env.EntryNames.ToArray();
                    Console.WriteLine($"  Patch {folderLabel}: entries=[{string.Join(",", entries)}]");

                    var pa = new PatchedConfigArchive(archive, new[] { env });
                    try
                    {
                        var patchedConfig = SharedGameConfig.ImportPatchedFrom(masterConfig, pa, entries);
                        var subDir = Path.Combine(outputDir, folderLabel);
                        Directory.CreateDirectory(subDir);
                        var subPath = Path.Combine(subDir, "events.json");
                        Engine.DumpEvents(patchedConfig, subPath, EventFilters.All);
                        var size = new FileInfo(subPath).Length / 1024;
                        Console.WriteLine($"     -> {folderLabel}/events.json OK ({size} KB)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"     !! {folderLabel} FAILED: {ex.GetType().Name}: {ex.Message}");
                        // Walk full exception chain + print full stack traces
                        var e = ex;
                        int depth = 0;
                        while (e != null)
                        {
                            Console.WriteLine($"        [depth={depth}] {e.GetType().Name}: {e.Message}");
                            if (!string.IsNullOrEmpty(e.StackTrace))
                            {
                                foreach (var line in e.StackTrace.Split('\n'))
                                    Console.WriteLine($"           {line.TrimEnd()}");
                            }
                            e = e.InnerException;
                            depth++;
                        }

                        // Also probe whether ContractResolver actually marks Decoration as Ignored
                        try
                        {
                            var resolver = new MergeMansionWikiTools.Dumper.Core.IgnoreDataMemberContractResolver();
                            var contract = resolver.ResolveContract(typeof(GameLogic.Player.Rewards.RewardDecoration))
                                as Newtonsoft.Json.Serialization.JsonObjectContract;
                            if (contract != null)
                            {
                                Console.WriteLine($"        [probe] RewardDecoration property check via ContractResolver:");
                                foreach (var p in contract.Properties)
                                    Console.WriteLine($"           - {p.PropertyName}: Ignored={p.Ignored}, Readable={p.Readable}");
                            }
                            else
                            {
                                Console.WriteLine("        [probe] No contract returned for RewardDecoration");
                            }
                        }
                        catch (Exception probeEx)
                        {
                            Console.WriteLine($"        [probe] failed: {probeEx.Message}");
                        }
                    }
                }
            }

            Console.WriteLine("=== Done ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // ── --probe-phone-tree: recursively list the game's cache/ tree over MTP ──
    // Finds where the active experiment patch is stored on the device. The standard pull only
    // reads cache\SharedGameConfig, \SharedGameConfigPatches, \Localizations (non-recursive).
    // If a Testing-phase tester patch lives elsewhere (subfolder, session/player blob), it shows here.
    /// <summary>
    /// Lists a directory anywhere on the phone (not just the game package) over MTP, newest first.
    /// Used to find screenshot folders — the existing --probe-phone-tree is hard-wired to
    /// Android/data/&lt;package&gt;.
    /// </summary>
    private static int ListPhoneDir(string relPath, string nameFilter, int limit)
    {
        var devices = MediaDevices.MediaDevice.GetDevices().ToList();
        if (devices.Count == 0) { Console.WriteLine("No MTP devices."); return 2; }
        foreach (var device in devices)
        {
            Console.WriteLine($"=== {device.FriendlyName} ===");
            try { device.Connect(); } catch (Exception ex) { Console.WriteLine($"  connect failed: {ex.Message}"); continue; }
            try
            {
                foreach (var root in device.GetDirectories("\\"))
                {
                    var path = string.IsNullOrEmpty(relPath) ? root : root + "\\" + relPath.Trim('\\');
                    if (!SafeDirExists(device, path))
                    {
                        if (string.IsNullOrEmpty(relPath))
                            Console.WriteLine($"  [root] {root}");
                        continue;
                    }
                    Console.WriteLine($"  {path}");
                    try
                    {
                        foreach (var sub in device.GetDirectories(path))
                            Console.WriteLine($"    [dir] {Path.GetFileName(sub)}");
                    }
                    catch { }

                    List<string> files;
                    try { files = device.GetFiles(path).ToList(); }
                    catch (Exception ex) { Console.WriteLine($"    list files failed: {ex.Message}"); continue; }

                    // Names only: GetFileInfo is a per-file MTP round trip and takes minutes over
                    // a few thousand files. Screenshot names already carry the timestamp.
                    var names = files.Select(Path.GetFileName)
                        .Where(n => MatchesAny(n, nameFilter))
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
                    Console.WriteLine($"    files: {names.Count}"
                        + (string.IsNullOrEmpty(nameFilter) ? "" : $" matching '{nameFilter}'"));
                    foreach (var n in names.Take(limit)) Console.WriteLine($"      {n}");
                }
            }
            finally { try { device.Disconnect(); } catch { } }
        }
        return 0;
    }

    /// <summary>Copies files from a phone directory whose name matches a filter, optionally only those newer than a date.</summary>
    private static int PullPhoneDir(string relPath, string localOut, string nameFilter, string sinceDate)
    {
        DateTime? since = DateTime.TryParse(sinceDate, out var sd) ? sd : null;
        Directory.CreateDirectory(localOut);
        var devices = MediaDevices.MediaDevice.GetDevices().ToList();
        if (devices.Count == 0) { Console.WriteLine("No MTP devices."); return 2; }
        int copied = 0, skipped = 0;
        foreach (var device in devices)
        {
            try { device.Connect(); } catch { continue; }
            try
            {
                foreach (var root in device.GetDirectories("\\"))
                {
                    var path = root + "\\" + relPath.Trim('\\');
                    if (!SafeDirExists(device, path)) continue;
                    Console.WriteLine($"pulling from {path}");
                    foreach (var f in device.GetFiles(path))
                    {
                        var name = Path.GetFileName(f);
                        if (!MatchesAny(name, nameFilter)) { skipped++; continue; }
                        var dest = Path.Combine(localOut, name);
                        if (File.Exists(dest)) { skipped++; continue; }
                        try
                        {
                            using var fs = File.Create(dest);
                            device.DownloadFile(f, fs);
                            copied++;
                            if (copied % 25 == 0) Console.WriteLine($"  … {copied} copied");
                        }
                        catch (Exception ex) { Console.WriteLine($"  {name}: {ex.Message}"); }
                    }
                }
            }
            finally { try { device.Disconnect(); } catch { } }
        }
        Console.WriteLine($"copied {copied}, skipped {skipped} → {localOut}");
        return 0;
    }

    /// <summary>Comma-separated substring filter; empty matches everything.</summary>
    private static bool MatchesAny(string name, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        foreach (var part in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int ProbePhoneTree(int maxDepth)
    {
        string[] knownPackages = { "com.everywear.game5" };
        string[] fallbackPrefixes = { "com.metacore." };
        try
        {
            var devices = MediaDevices.MediaDevice.GetDevices().ToList();
            if (devices.Count == 0)
            {
                Console.WriteLine("No MTP devices. Connect phone in File Transfer (MTP) mode and unlock it.");
                return 2;
            }
            foreach (var device in devices)
            {
                Console.WriteLine($"=== Device: {device.FriendlyName} ===");
                try { device.Connect(); }
                catch (Exception ex) { Console.WriteLine($"  connect failed: {ex.Message}"); continue; }

                try
                {
                    string[] roots;
                    try { roots = device.GetDirectories("\\"); }
                    catch (Exception ex) { Console.WriteLine($"  list storage failed: {ex.Message}"); continue; }

                    string packagePath = null;
                    foreach (var root in roots)
                    {
                        var androidData = root + "\\Android\\data";
                        if (!SafeDirExists(device, androidData)) continue;
                        foreach (var pkg in knownPackages)
                        {
                            var cand = androidData + "\\" + pkg;
                            if (SafeDirExists(device, cand)) { packagePath = cand; break; }
                        }
                        if (packagePath == null)
                        {
                            try
                            {
                                foreach (var dir in device.GetDirectories(androidData))
                                {
                                    var name = dir.Split('\\').Last();
                                    if (fallbackPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                                    { packagePath = dir; break; }
                                }
                            }
                            catch { }
                        }
                        if (packagePath != null) break;
                    }

                    if (packagePath == null) { Console.WriteLine("  game package not found"); continue; }
                    Console.WriteLine($"  package: {packagePath}");

                    // Walk the WHOLE package (files\, cache\, etc.) so any patch store outside
                    // cache\SharedGameConfigPatches surfaces. Skip the noisy UnityShaderCache.
                    Console.WriteLine($"  walking {packagePath} (maxDepth={maxDepth}, skipping UnityShaderCache):");
                    WalkTree(device, packagePath, 1, maxDepth);
                }
                finally { try { device.Disconnect(); } catch { } }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int PullPhoneFile(string relUnderCache, string localOut)
    {
        string[] knownPackages = { "com.everywear.game5" };
        string[] fallbackPrefixes = { "com.metacore." };
        try
        {
            var devices = MediaDevices.MediaDevice.GetDevices().ToList();
            if (devices.Count == 0) { Console.WriteLine("No MTP devices."); return 2; }
            foreach (var device in devices)
            {
                try { device.Connect(); } catch { continue; }
                try
                {
                    string[] roots; try { roots = device.GetDirectories("\\"); } catch { continue; }
                    string packagePath = null;
                    foreach (var root in roots)
                    {
                        var androidData = root + "\\Android\\data";
                        if (!SafeDirExists(device, androidData)) continue;
                        foreach (var pkg in knownPackages)
                        {
                            var cand = androidData + "\\" + pkg;
                            if (SafeDirExists(device, cand)) { packagePath = cand; break; }
                        }
                        if (packagePath == null)
                        {
                            try
                            {
                                foreach (var dir in device.GetDirectories(androidData))
                                {
                                    var name = dir.Split('\\').Last();
                                    if (fallbackPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                                    { packagePath = dir; break; }
                                }
                            }
                            catch { }
                        }
                        if (packagePath != null) break;
                    }
                    if (packagePath == null) continue;
                    var remote = packagePath + "\\" + relUnderCache.Replace('/', '\\');
                    if (!SafeFileExists(device, remote)) { Console.WriteLine($"  not found: {remote}"); continue; }
                    using (var fs = File.Create(localOut))
                        device.DownloadFile(remote, fs);
                    Console.WriteLine($"OK -> {localOut} ({new FileInfo(localOut).Length} B) from {remote}");
                    return 0;
                }
                finally { try { device.Disconnect(); } catch { } }
            }
            Console.WriteLine("game package / file not found on any device");
            return 1;
        }
        catch (Exception ex) { Console.Error.WriteLine($"FATAL: {ex.Message}"); return 1; }
    }

    private static bool SafeFileExists(MediaDevices.MediaDevice device, string path)
    {
        try { return device.FileExists(path); } catch { return false; }
    }

    private static bool SafeDirExists(MediaDevices.MediaDevice device, string path)
    {
        try { return device.DirectoryExists(path); } catch { return false; }
    }

    private static void WalkTree(MediaDevices.MediaDevice device, string dir, int depth, int maxDepth)
    {
        string indent = new string(' ', depth * 2 + 2);
        string[] files = Array.Empty<string>(), subs = Array.Empty<string>();
        try { files = device.GetFiles(dir); } catch { }
        try { subs = device.GetDirectories(dir); } catch { }

        foreach (var f in files.OrderBy(x => x))
        {
            long size = -1;
            try { var fi = device.GetFileInfo(f); size = (long)fi.Length; } catch { }
            Console.WriteLine($"{indent}{f.Split('\\').Last()}   {(size >= 0 ? size + " B" : "?")}");
        }
        foreach (var s in subs.OrderBy(x => x))
        {
            var name = s.Split('\\').Last();
            if (name.Equals("UnityShaderCache", StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine($"{indent}[{name}]/  (skipped)"); continue; }
            Console.WriteLine($"{indent}[{name}]/");
            if (depth < maxDepth) WalkTree(device, s, depth + 1, maxDepth);
            else Console.WriteLine($"{indent}  … (depth limit)");
        }
    }

    // ── Shared A/B patch loading (single source of truth for every *-patched sub-command) ──
    /// <summary>
    /// Load every distinct-content specialization patch from a patch snapshot file or a directory of
    /// snapshots, and assign each one the output folder label the dump sub-commands use.
    ///
    /// Dedup key = (label, SHA-256 of the raw envelope bytes): byte-identical copies of the same label
    /// across snapshots collapse into one, but the SAME label carrying DIFFERENT content survives as a
    /// separate version. (The old "richer payload wins" heuristic silently dropped the smaller version
    /// even when it was the only one changing a specific event — 2026-06-26 fix, mirrors DumperService.)
    ///
    /// Folder naming per label: primary (largest raw size) keeps the bare label, extras become
    /// label__v2, label__v3, … Ordering inside a label is descending RawSize; LINQ's OrderByDescending
    /// is stable, so equal-size versions keep their discovery order, i.e. the patch files' order
    /// (ascending last-write time, and ascending file name for equal timestamps).
    /// </summary>
    private static List<(Metaplay.Core.Player.PlayerExperimentId ExpId, Metaplay.Core.Player.ExperimentVariantId VarId, byte[] RawBytes, Metaplay.Core.Config.GameConfigPatchEnvelope Envelope, string Label)> LoadDistinctPatches(string patchDirOrFile)
    {
        var result = new List<(Metaplay.Core.Player.PlayerExperimentId ExpId, Metaplay.Core.Player.ExperimentVariantId VarId, byte[] RawBytes, Metaplay.Core.Config.GameConfigPatchEnvelope Envelope, string Label)>();
        if (string.IsNullOrEmpty(patchDirOrFile)) return result;

        // Support both file mode (single snapshot) and directory mode (union across all snapshots).
        var patchFiles = new List<string>();
        if (Directory.Exists(patchDirOrFile))
            patchFiles.AddRange(Directory.GetFiles(patchDirOrFile).OrderBy(File.GetLastWriteTimeUtc));
        else if (File.Exists(patchDirOrFile))
            patchFiles.Add(patchDirOrFile);

        Console.WriteLine($"        Loading {patchFiles.Count} patch file(s)...");
        var distinctPatches = new Dictionary<string, (Metaplay.Core.Player.PlayerExperimentId ExpId, Metaplay.Core.Player.ExperimentVariantId VarId, byte[] RawBytes, Metaplay.Core.Config.GameConfigPatchEnvelope Envelope, int RawSize)>(StringComparer.Ordinal);
        foreach (var pf in patchFiles)
        {
            var specPatches = Metaplay.Core.Config.GameConfigSpecializationPatches.FromBytes(File.ReadAllBytes(pf));
            foreach (var (expId, vs) in specPatches.Patches)
                foreach (var (varId, bytes) in vs)
                {
                    var label = $"{expId}_{varId}";
                    var actualBytes = bytes ?? Array.Empty<byte>();
                    var hashHex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actualBytes));
                    var dedupKey = $"{label}\0{hashHex}";
                    if (distinctPatches.ContainsKey(dedupKey)) continue;
                    distinctPatches[dedupKey] = (expId, varId, actualBytes, Metaplay.Core.Config.GameConfigPatchEnvelope.Deserialize(actualBytes), actualBytes.Length);
                    Console.WriteLine($"        [distinct] {label}: {actualBytes.Length} B [{hashHex[..8]}] from {Path.GetFileName(pf)}");
                }
        }

        // Group per label, then assign folder names.
        var byLabel = distinctPatches.Values
            .GroupBy(v => $"{v.ExpId}_{v.VarId}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.RawSize).ToList(), StringComparer.Ordinal);

        Console.WriteLine($"        Found {byLabel.Values.Sum(v => v.Count)} distinct patch version(s), {byLabel.Count} unique label(s) across {patchFiles.Count} file(s)");
        foreach (var (lbl, vs) in byLabel.Where(kv => kv.Value.Count > 1))
            Console.WriteLine($"        [multi-version] {lbl}: {vs.Count} versions — folders: {lbl}, {string.Join(", ", Enumerable.Range(2, vs.Count - 1).Select(i => $"{lbl}__v{i}"))}");

        foreach (var kv in byLabel)
            for (int i = 0; i < kv.Value.Count; i++)
            {
                var v = kv.Value[i];
                result.Add((v.ExpId, v.VarId, v.RawBytes, v.Envelope, i == 0 ? kv.Key : $"{kv.Key}__v{i + 1}"));
            }
        return result;
    }

    // ── --compare-engines: run Legacy and Native over identical inputs and byte-compare the output ──
    // Two locations live outside the repo on this machine. _DATA is normally found relative to the
    // harness binary, but the harness is built into %TEMP% during development, so the relative guess
    // is unreliable: MMWT_DATA overrides it and this constant is the last-resort fallback.
    private const string FallbackDataDir = @"D:\_BACKUP_2.0\Code Projects\MergeMansionWikiTools\bin\Debug\net9.0-windows10.0.19041.0\win-x64\_DATA";
    private const string ApkRoot = @"D:\_BACKUP_2.0\Adobe Photoshop - Savy\Merge Mansion\APKs";

    /// <summary>
    /// Dump the same config with both engines and compare the produced files byte for byte.
    /// Exit code is 0 only when every row is IDENTICAL.
    /// </summary>
    private static int CompareEngines(string outDir, string selector, bool filterMatrix, string onlyPrefix = null)
    {
        Console.WriteLine("=== DumpHarness --compare-engines ===");
        var dataDir = ResolveDataDir();
        Console.WriteLine($"Data:     {dataDir}");
        Console.WriteLine($"Selector: {selector}{(filterMatrix ? "  (+ EventFilters matrix)" : "")}");
        if (onlyPrefix != null) Console.WriteLine($"Only:     files starting with '{onlyPrefix}'");
        Console.WriteLine($"Output:   {outDir}\n");

        bool Wanted(string file) => onlyPrefix == null || file.StartsWith(onlyPrefix, StringComparison.OrdinalIgnoreCase);

        // A prefix that matches nothing would otherwise "succeed" with an empty table and exit 0,
        // which reads exactly like "everything is identical". Fail loudly instead.
        if (onlyPrefix != null && !Runs(filterMatrix).Select(r => r.File).Append("Experimental/").Any(Wanted))
        {
            Console.Error.WriteLine($"ERROR: --only matched no files (prefix '{onlyPrefix}')");
            return 2;
        }

        // 1. Resolve inputs. "live" = every archive in _DATA/C (with _DATA/P patches and _DATA/L
        //    language), "newest" = only the newest-CreatedAt one of those; anything else comes from
        //    Codex/sources.json and has no patches/language.
        var inputs = new List<(string Name, string ConfigPath, string PatchDir, string LangPath)>();
        bool live = string.Equals(selector, "live", StringComparison.OrdinalIgnoreCase);
        bool newest = string.Equals(selector, "newest", StringComparison.OrdinalIgnoreCase);
        if (live || newest)
        {
            var cDir = Path.Combine(dataDir, "C");
            if (!Directory.Exists(cDir)) { Console.Error.WriteLine($"No config directory at {cDir}"); return 2; }
            var pDir = Path.Combine(dataDir, "P");
            var lang = FirstFileOrNull(Path.Combine(dataDir, "L"));
            var archives = Directory.GetFiles(cDir).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (newest)
            {
                // Same CreatedAt source as --probe-createdat: the archive header, readable without
                // MetaplayCore.Initialize. Unreadable archives sort last (MinValue) and are dropped.
                var pick = archives
                    .Select(f => (File: f, CreatedAt: TryReadArchiveCreatedAt(f)))
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefault();
                if (pick.File == null || pick.CreatedAt == DateTimeOffset.MinValue)
                { Console.Error.WriteLine($"No readable config archive in {cDir}"); return 2; }
                Console.WriteLine($"Newest archive: {Path.GetFileName(pick.File)}  CreatedAt={pick.CreatedAt:yyyy-MM-dd HH:mm:ss}Z\n");
                archives = new List<string> { pick.File };
            }
            foreach (var c in archives)
                inputs.Add((Path.GetFileName(c), c, Directory.Exists(pDir) ? pDir : null, lang));
        }
        else
        {
            foreach (var (ver, cfg) in ReadSourcesJson(dataDir, selector))
                inputs.Add((ver, cfg, null, null));
        }
        if (inputs.Count == 0) { Console.Error.WriteLine($"Selector '{selector}' matched no input"); return 2; }

        try { MetaplayCore.Initialize(); }
        catch (Exception ex) { Console.Error.WriteLine($"FATAL: {DescribeError(ex)}"); return 1; }

        // 2. Run both engines over every input. The engines are created here on purpose: --compare-engines
        //    always runs BOTH regardless of the global --engine flag.
        IDumpEngine legacy = new LegacyDumpEngineHarness(), native = new NativeDumpEngine();
        var rows = new List<(string Input, string File, string Result)>();
        foreach (var inp in inputs)
        {
            Console.WriteLine($"--- {inp.Name} ---");
            if (inp.ConfigPath == null) { rows.Add((inp.Name, "-", "SKIP (no config)")); Console.WriteLine("    SKIP (no config)"); continue; }

            var lDir = Path.Combine(outDir, inp.Name, "legacy");
            var nDir = Path.Combine(outDir, inp.Name, "native");
            ConfigArchive archive;
            SharedGameConfig master;
            try
            {
                if (inp.LangPath != null)
                    MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(ContentHash.ParseString(Path.GetFileName(inp.LangPath)), File.ReadAllBytes(inp.LangPath));
                archive = ConfigArchive.FromBytes(File.ReadAllBytes(inp.ConfigPath));
                master = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
                ClientGlobal.SharedGameConfig = master;
            }
            catch (Exception ex)
            {
                rows.Add((inp.Name, "(import)", $"ERROR (input: {DescribeError(ex)})"));
                Console.WriteLine($"    (import) ERROR (input: {DescribeError(ex)})");
                continue;
            }

            // 2a. The plain one-file-per-call dumps (+ the optional EventFilters matrix).
            foreach (var (file, run) in Runs(filterMatrix))
            {
                if (!Wanted(file)) continue;
                var res = CompareOne(() => run(legacy, master, lDir), () => run(native, master, nDir),
                                     Path.Combine(lDir, file), Path.Combine(nDir, file));
                rows.Add((inp.Name, file, res));
                Console.WriteLine($"    {file,-28} {res}");
            }

            // 2b. Experimental: one call per engine writes N section files, so it needs its own loop.
            if (Wanted("Experimental/"))
            {
                var expRows = CompareExperimental(legacy, native, master, Path.Combine(lDir, "Experimental"), Path.Combine(nDir, "Experimental"));
                foreach (var (file, res) in expRows)
                {
                    rows.Add((inp.Name, file, res));
                    Console.WriteLine($"    {file,-28} {res}");
                }
            }

            // 2c. A/B patches: the three sections a patch can realistically change.
            var patchFiles = new[] { "chain_item_odds.json", "areas.json", "events.json" }.Where(Wanted).ToArray();
            if (patchFiles.Length > 0 && inp.PatchDir != null && Directory.Exists(inp.PatchDir))
            {
                foreach (var p in LoadDistinctPatches(inp.PatchDir))
                {
                    SharedGameConfig patched;
                    try
                    {
                        var pa = new PatchedConfigArchive(archive, new[] { p.Envelope });
                        patched = SharedGameConfig.ImportPatchedFrom(master, pa, p.Envelope.EntryNames.ToArray());
                    }
                    catch (Exception ex)
                    {
                        rows.Add(($"{inp.Name}/{p.Label}", "(patch import)", $"ERROR (input: {DescribeError(ex)})"));
                        Console.WriteLine($"    {p.Label} (patch import) ERROR (input: {DescribeError(ex)})");
                        continue;
                    }

                    // ClientGlobal deliberately stays on MASTER here. DumperService's phase B dumps
                    // every patch from a Parallel.Invoke while the global keeps pointing at the
                    // master config (one shared static cannot be per-patch anyway), passing the
                    // patched config explicitly instead. Pointing it at `patched` would verify a
                    // state the app never reaches — and it is exactly the state that hides whether
                    // an engine secretly reads the global (the API's PredefinedSequenceProducer /
                    // ControlledRandomProducer TimeSkipPriceGems do).
                    foreach (var file in patchFiles)
                    {
                        var lp = Path.Combine(lDir, p.Label, file);
                        var np = Path.Combine(nDir, p.Label, file);
                        // baseline stays null so the file is ALWAYS written and can be byte-compared.
                        var res = CompareOne(() => RunPatch(legacy, patched, lp, file), () => RunPatch(native, patched, np, file), lp, np);
                        rows.Add(($"{inp.Name}/{p.Label}", file, res));
                        Console.WriteLine($"    {p.Label}/{file,-28} {res}");
                    }
                }
            }
        }

        // 3. Report.
        Console.WriteLine("\n=== compare-engines report ===");
        foreach (var r in rows) Console.WriteLine($"{r.Input,-40} {r.File,-28} {r.Result}");
        int bad = rows.Count(r => r.Result != "IDENTICAL");
        Console.WriteLine($"\n{rows.Count - bad}/{rows.Count} IDENTICAL");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>_DATA location: MMWT_DATA wins, then the path relative to the binary, then the fallback.</summary>
    private static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("MMWT_DATA");
        if (!string.IsNullOrEmpty(env)) return env;
        var guess = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Debug", "net9.0-windows10.0.19041.0", "win-x64", "_DATA"));
        return Directory.Exists(Path.Combine(guess, "C")) ? guess : FallbackDataDir;
    }

    /// <summary>
    /// Archive header CreatedAt, read the same way --probe-createdat does (no MetaplayCore.Initialize
    /// needed). Returns <see cref="DateTimeOffset.MinValue"/> for anything that fails to parse, so an
    /// unreadable archive can never win a "newest" selection.
    /// </summary>
    private static DateTimeOffset TryReadArchiveCreatedAt(string path)
    {
        try
        {
            var a = ConfigArchive.FromBytes(File.ReadAllBytes(path));
            return DateTimeOffset.FromUnixTimeMilliseconds(a.CreatedAt.MillisecondsSinceEpoch);
        }
        catch { return DateTimeOffset.MinValue; }
    }

    private static string FirstFileOrNull(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null;

    /// <summary>
    /// Historical inputs from Codex/sources.json: versions[ver].configArchives holds absolute archive
    /// paths, and embeddedConfig versions keep their archive as a *.mpa inside the APK folder.
    /// ConfigPath is null when neither exists — the caller turns that into a SKIP row.
    /// </summary>
    private static List<(string Ver, string ConfigPath)> ReadSourcesJson(string dataDir, string selector)
    {
        var result = new List<(string, string)>();
        // dataDir is <repo>\bin\Debug\<tfm>\win-x64\_DATA, so the repo root is five levels up; the
        // current directory is tried first because it is the reliable one when running from the repo.
        string repoRoot = null;
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d != null && repoRoot == null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Codex", "sources.json"))) repoRoot = d.FullName;
        repoRoot ??= Path.GetFullPath(Path.Combine(dataDir, "..", "..", "..", "..", ".."));

        var sourcesPath = Path.Combine(repoRoot, "Codex", "sources.json");
        if (!File.Exists(sourcesPath)) { Console.Error.WriteLine($"sources.json not found at {sourcesPath}"); return result; }

        var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(sourcesPath));
        var versions = root["versions"] as Newtonsoft.Json.Linq.JObject;
        if (versions == null) { Console.Error.WriteLine("sources.json has no 'versions' object"); return result; }

        bool all = string.Equals(selector, "all", StringComparison.OrdinalIgnoreCase);
        foreach (var prop in versions.Properties())
        {
            if (!all && !string.Equals(prop.Name, selector, StringComparison.OrdinalIgnoreCase)) continue;
            var v = (Newtonsoft.Json.Linq.JObject)prop.Value;

            var archives = v["configArchives"] as Newtonsoft.Json.Linq.JArray;
            var cfg = archives?.Select(t => (string)t).FirstOrDefault(File.Exists);
            if (cfg == null && (bool?)v["embeddedConfig"] == true)
            {
                // The embedded archive lives next to the APK; prefer the folder the APK itself is in.
                var apk = (string)v["apk"];
                var dir = !string.IsNullOrEmpty(apk) ? Path.GetDirectoryName(apk) : Path.Combine(ApkRoot, prop.Name);
                if (Directory.Exists(dir))
                    cfg = Directory.GetFiles(dir, "*.mpa", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
            }
            result.Add((prop.Name, cfg));
        }
        if (result.Count == 0) Console.Error.WriteLine($"sources.json has no version '{selector}'");
        return result;
    }

    /// <summary>The dump calls compared per input: the six main files, plus one per EventFilters flag.</summary>
    private static List<(string File, Action<IDumpEngine, SharedGameConfig, string> Run)> Runs(bool filterMatrix)
    {
        var runs = new List<(string, Action<IDumpEngine, SharedGameConfig, string>)>
        {
            ("chain_item_odds.json", (e, c, d) => e.DumpChains(c, Path.Combine(d, "chain_item_odds.json"))),
            ("areas.json",           (e, c, d) => e.DumpAreas(c, Path.Combine(d, "areas.json"))),
            ("events.json",          (e, c, d) => e.DumpEvents(c, Path.Combine(d, "events.json"), EventFilters.All)),
            ("card_collection.json", (e, c, d) => e.DumpCardCollection(c, Path.Combine(d, "card_collection.json"))),
            ("dialogues.json",       (e, c, d) => e.DumpDialogues(c, Path.Combine(d, "dialogues.json"))),
            ("Pets.json",            (e, c, d) => e.DumpPets(c, Path.Combine(d, "Pets.json"))),
        };
        if (filterMatrix)
            foreach (EventFilters f in Enum.GetValues(typeof(EventFilters)))
            {
                // All is already covered by the plain events.json row above. None is NOT skipped:
                // it is the "every checkbox unchecked" state the dump page can actually produce,
                // and it is the one case where the two engines could disagree about what an empty
                // filter set means (everything vs nothing).
                if (f == EventFilters.All) continue;
                var flag = f;
                var name = $"events[{flag}].json";
                runs.Add((name, (e, c, d) => e.DumpEvents(c, Path.Combine(d, name), flag)));
            }
        return runs;
    }

    /// <summary>The three sections a specialization patch can change, dumped with no baseline.</summary>
    private static void RunPatch(IDumpEngine engine, SharedGameConfig cfg, string path, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        switch (file)
        {
            case "chain_item_odds.json": engine.DumpChains(cfg, path); break;
            case "areas.json":           engine.DumpAreas(cfg, path); break;
            case "events.json":          engine.DumpEvents(cfg, path, EventFilters.All); break;
            default: throw new ArgumentOutOfRangeException(nameof(file), file, "unsupported patch dump file");
        }
    }

    /// <summary>
    /// DumpExperimental writes a whole set of section files per call, so both engines run once and
    /// every section file returned by either of them becomes its own row. When one engine throws, the
    /// rows still come from the other engine's section list so the table keeps its per-section shape;
    /// only a double failure collapses into a single Experimental/* row.
    /// </summary>
    private static List<(string File, string Result)> CompareExperimental(IDumpEngine legacy, IDumpEngine native, SharedGameConfig cfg, string lDir, string nDir)
    {
        var rows = new List<(string, string)>();
        IReadOnlyList<(string Section, string Path)> lExp = null, nExp = null;
        string lErr = null, nErr = null;
        try { Directory.CreateDirectory(lDir); lExp = legacy.DumpExperimental(cfg, lDir); }
        catch (Exception ex) { lErr = $"ERROR (legacy: {DescribeError(ex)})"; }
        try { Directory.CreateDirectory(nDir); nExp = native.DumpExperimental(cfg, nDir); }
        catch (Exception ex) { nErr = $"ERROR (native: {DescribeError(ex)})"; }

        var names = (lExp ?? Array.Empty<(string, string)>()).Select(x => Path.GetFileName(x.Path))
            .Union((nExp ?? Array.Empty<(string, string)>()).Select(x => Path.GetFileName(x.Path)), StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0) { rows.Add(("Experimental/*", lErr ?? nErr ?? "ERROR (no sections written)")); return rows; }

        foreach (var name in names)
            rows.Add(($"Experimental/{name}", lErr ?? nErr ?? CompareFiles(Path.Combine(lDir, name), Path.Combine(nDir, name))));
        return rows;
    }

    /// <summary>Run both engines, then byte-compare the two files they were supposed to produce.</summary>
    private static string CompareOne(Action runLegacy, Action runNative, string lPath, string nPath)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(lPath)); runLegacy(); }
        catch (Exception ex) { return $"ERROR (legacy: {DescribeError(ex)})"; }
        try { Directory.CreateDirectory(Path.GetDirectoryName(nPath)); runNative(); }
        catch (Exception ex) { return $"ERROR (native: {DescribeError(ex)})"; }
        return CompareFiles(lPath, nPath);
    }

    /// <summary>Raw byte comparison (a BOM difference counts); on mismatch, locate the first differing line.</summary>
    private static string CompareFiles(string lPath, string nPath)
    {
        if (!File.Exists(lPath)) return "ERROR (legacy: file not written)";
        if (!File.Exists(nPath)) return "ERROR (native: file not written)";
        var lb = File.ReadAllBytes(lPath);
        var nb = File.ReadAllBytes(nPath);
        if (lb.AsSpan().SequenceEqual(nb)) return "IDENTICAL";

        // Decode without stripping the BOM so a BOM-only difference still shows up on line 1.
        var ll = System.Text.Encoding.UTF8.GetString(lb).Split('\n');
        var nl = System.Text.Encoding.UTF8.GetString(nb).Split('\n');
        for (int i = 0; i < Math.Min(ll.Length, nl.Length); i++)
            if (!string.Equals(ll[i], nl[i], StringComparison.Ordinal))
                return $"DIFF line {i + 1} | legacy: {Clip(ll[i])} | native: {Clip(nl[i])}";
        return $"DIFF length (legacy {ll.Length} lines, native {nl.Length} lines)";
    }

    private static string Clip(string s)
    {
        s = (s ?? "").TrimEnd('\r');
        return s.Length <= 120 ? s : s.Substring(0, 120);
    }

    /// <summary>Exception type + first line of the message — enough to identify the failure in a table row.</summary>
    private static string DescribeError(Exception ex)
        => $"{ex.GetType().Name}: {(ex.Message ?? "").Split('\n')[0].TrimEnd('\r').Trim()}";

    // ── --dump-full-patched: FULL per-patch dump (all sections), base + per-patch subfolders ──
    private static int DumpFullPatched(string configPath, string patchPath, string languagePath, string outputDir)
    {
        Console.WriteLine("=== DumpHarness --dump-full-patched ===");
        Directory.CreateDirectory(outputDir);

        // Dump every section a normal dump produces (chain/areas/events/cards/dialogues/pets).
        void DumpAll(string dir, GameLogic.Config.SharedGameConfig cfg)
        {
            Directory.CreateDirectory(dir);
            ClientGlobal.SharedGameConfig = cfg;
            void Try(string name, Action a) { try { a(); } catch (Exception ex) { Console.WriteLine($"       [{Path.GetFileName(dir)}] {name} FAILED: {ex.GetType().Name}: {ex.Message}"); } }
            Try("chain_item_odds.json", () => Engine.DumpChains(cfg, Path.Combine(dir, "chain_item_odds.json")));
            Try("areas.json",           () => Engine.DumpAreas(cfg, Path.Combine(dir, "areas.json")));
            Try("events.json",          () => Engine.DumpEvents(cfg, Path.Combine(dir, "events.json"), EventFilters.All));
            Try("card_collection.json", () => Engine.DumpCardCollection(cfg, Path.Combine(dir, "card_collection.json")));
            Try("dialogues.json",       () => Engine.DumpDialogues(cfg, Path.Combine(dir, "dialogues.json")));
            Try("Pets.json",            () => Engine.DumpPets(cfg, Path.Combine(dir, "Pets.json")));
        }

        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var masterConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));

            Console.WriteLine("[base] dumping all sections...");
            DumpAll(outputDir, masterConfig);

            if (!string.IsNullOrEmpty(patchPath))
            {
                var configPatches = LoadDistinctPatches(patchPath);
                Console.WriteLine($"[patches] {configPatches.Count} patch version(s)");

                foreach (var (expId, varId, rawBytes, env, folderLabel) in configPatches)
                {
                    var entries = env.EntryNames.ToArray();
                    Console.WriteLine($"  Patch {folderLabel}: entries=[{string.Join(",", entries)}]");
                    var pa = new PatchedConfigArchive(archive, new[] { env });
                    try
                    {
                        var patchedConfig = SharedGameConfig.ImportPatchedFrom(masterConfig, pa, entries);
                        DumpAll(Path.Combine(outputDir, folderLabel), patchedConfig);
                    }
                    catch (Exception ex) { Console.WriteLine($"     !! {folderLabel} IMPORT FAILED: {ex.GetType().Name}: {ex.Message}"); }
                    finally { ClientGlobal.SharedGameConfig = masterConfig; }
                }
            }
            Console.WriteLine("=== Done ===");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 1; }
    }

    // ── --dump-areas-patched: import config + apply each specialization patch + dump areas.json per patch ──
    private static int DumpAreasPatched(string configPath, string patchPath, string languagePath, string outputDir)
    {
        Console.WriteLine("=== DumpHarness --dump-areas-patched ===");
        Directory.CreateDirectory(outputDir);
        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var masterConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
            ClientGlobal.SharedGameConfig = masterConfig;
            Engine.DumpAreas(masterConfig, Path.Combine(outputDir, "areas.json"));
            Console.WriteLine("        -> areas.json (base)");

            if (!string.IsNullOrEmpty(patchPath))
            {
                var configPatches = LoadDistinctPatches(patchPath);

                foreach (var (expId, varId, rawBytes, env, folderLabel) in configPatches)
                {
                    var entries = env.EntryNames.ToArray();
                    bool touchesAreas = entries.Any(e => e == "Areas" || e == "HotspotDefinitions" || e == "Items" || e == "MergeChains");
                    Console.WriteLine($"  Patch {folderLabel}: entries=[{string.Join(",", entries)}]{(touchesAreas ? " *AREA?*" : "")}");
                    var pa = new PatchedConfigArchive(archive, new[] { env });
                    try
                    {
                        var patchedConfig = SharedGameConfig.ImportPatchedFrom(masterConfig, pa, entries);
                        ClientGlobal.SharedGameConfig = patchedConfig;
                        var subDir = Path.Combine(outputDir, folderLabel);
                        Directory.CreateDirectory(subDir);
                        Engine.DumpAreas(patchedConfig, Path.Combine(subDir, "areas.json"));
                        Console.WriteLine($"     -> {folderLabel}/areas.json OK");
                    }
                    catch (Exception ex) { Console.WriteLine($"     !! {folderLabel} FAILED: {ex.GetType().Name}: {ex.Message}"); }
                    finally { ClientGlobal.SharedGameConfig = masterConfig; }
                }
            }
            Console.WriteLine("=== Done ===");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 1; }
    }

    // ── --dump-chain-patched: import config + apply each specialization patch + dump chain_item_odds.json per patch ──
    // Mirror of DumpEventsPatched (same distinct-content dedup, same folder naming) but emits
    // chain_item_odds.json via MergeChainDumper. Used to diagnose producer-balance AB branches
    // (Items/MergeChains) that --dump-events-patched cannot surface.
    private static int DumpChainPatched(string configPath, string patchPath, string languagePath, string outputDir)
    {
        Console.WriteLine("=== DumpHarness --dump-chain-patched ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Patches:  {patchPath}");
        Console.WriteLine($"Language: {languagePath}");
        Console.WriteLine($"Output:   {outputDir}");
        Directory.CreateDirectory(outputDir);

        try
        {
            Console.WriteLine("[1] Initializing MetaplayCore...");
            MetaplayCore.Initialize();

            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                Console.WriteLine("[2] Loading language...");
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }

            Console.WriteLine("[3] Importing SharedGameConfig...");
            var archiveBytes = File.ReadAllBytes(configPath);
            var archive = ConfigArchive.FromBytes(archiveBytes);
            var patchedArchive = PatchedConfigArchive.WithNoPatches(archive);
            var masterConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
            ClientGlobal.SharedGameConfig = masterConfig;

            Console.WriteLine("[4] Dumping base chain_item_odds.json...");
            var basePath = Path.Combine(outputDir, "chain_item_odds.json");
            Engine.DumpChains(masterConfig, basePath);
            Console.WriteLine($"        -> chain_item_odds.json ({new FileInfo(basePath).Length / 1024} KB)");

            if (!string.IsNullOrEmpty(patchPath))
            {
                Console.WriteLine("[5] Loading patches...");
                var configPatches = LoadDistinctPatches(patchPath);

                foreach (var (expId, varId, rawBytes, env, folderLabel) in configPatches)
                {
                    var entries = env.EntryNames.ToArray();
                    // Only patches touching Items/MergeChains can change chain_item_odds.json.
                    bool touchesChains = entries.Any(e => e == "Items" || e == "MergeChains");
                    Console.WriteLine($"  Patch {folderLabel}: entries=[{string.Join(",", entries)}]{(touchesChains ? " *CHAIN*" : "")}");

                    var pa = new PatchedConfigArchive(archive, new[] { env });
                    try
                    {
                        var patchedConfig = SharedGameConfig.ImportPatchedFrom(masterConfig, pa, entries);
                        ClientGlobal.SharedGameConfig = patchedConfig;
                        var subDir = Path.Combine(outputDir, folderLabel);
                        Directory.CreateDirectory(subDir);
                        var subPath = Path.Combine(subDir, "chain_item_odds.json");
                        Engine.DumpChains(patchedConfig, subPath);
                        var size = new FileInfo(subPath).Length / 1024;
                        Console.WriteLine($"     -> {folderLabel}/chain_item_odds.json OK ({size} KB)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"     !! {folderLabel} FAILED: {ex.GetType().Name}: {ex.Message}");
                        var e = ex; int depth = 0;
                        while (e != null)
                        {
                            Console.WriteLine($"        [depth={depth}] {e.GetType().Name}: {e.Message}");
                            if (!string.IsNullOrEmpty(e.StackTrace))
                                foreach (var line in e.StackTrace.Split('\n'))
                                    Console.WriteLine($"           {line.TrimEnd()}");
                            e = e.InnerException; depth++;
                        }
                    }
                    finally
                    {
                        ClientGlobal.SharedGameConfig = masterConfig;
                    }
                }
            }

            Console.WriteLine("=== Done ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // ── --probe-schedule: per config archive in C/, print CreatedAt + matching CBE schedules ──
    private static int ProbeSchedule(string cDirOrFile, string substr)
    {
        var files = new List<string>();
        if (Directory.Exists(cDirOrFile)) files.AddRange(Directory.GetFiles(cDirOrFile).OrderBy(f => f));
        else if (File.Exists(cDirOrFile)) files.Add(cDirOrFile);
        if (files.Count == 0) { Console.Error.WriteLine($"No config files at {cDirOrFile}"); return 2; }

        Console.WriteLine($"=== --probe-schedule (substr='{substr}') over {files.Count} archive(s) ===\n");
        try
        {
            MetaplayCore.Initialize();

            (string File, DateTimeOffset? CreatedAt) newest = (null, null);
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                DateTimeOffset? createdAt = null;
                Metaplay.Core.Config.ConfigArchive archive = null;
                try
                {
                    var bytes = File.ReadAllBytes(f);
                    archive = Metaplay.Core.Config.ConfigArchive.FromBytes(bytes);
                    createdAt = DateTimeOffset.FromUnixTimeMilliseconds(archive.CreatedAt.MillisecondsSinceEpoch);
                }
                catch (Exception ex) { Console.WriteLine($"[{name}] header read FAILED: {ex.Message}"); continue; }

                if (createdAt.HasValue && (newest.CreatedAt == null || createdAt > newest.CreatedAt))
                    newest = (name, createdAt);

                Console.WriteLine($"### Archive {name}");
                Console.WriteLine($"    CreatedAt (header) = {createdAt:yyyy-MM-dd HH:mm:ss} UTC   entries={archive.Entries.Count}");

                GameLogic.Config.SharedGameConfig cfg;
                try
                {
                    var pa = Metaplay.Core.Config.PatchedConfigArchive.WithNoPatches(archive);
                    cfg = (GameLogic.Config.SharedGameConfig)Metaplay.Core.Config.GameConfigFactory.Instance.ImportSharedGameConfig(pa);
                    ClientGlobal.SharedGameConfig = cfg;
                }
                catch (Exception ex) { Console.WriteLine($"    import FAILED: {ex.GetType().Name}: {ex.Message}\n"); continue; }

                int hits = 0;
                foreach (var kv in cfg.CollectibleBoardEvents.EnumerateAll())
                {
                    var key = kv.Key.ToString();
                    if (!string.IsNullOrEmpty(substr) && key.IndexOf(substr, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hits++;
                    var info = kv.Value;
                    var ap = GetMember(info, "ActivableParams");
                    var isEnabled = GetMember(ap, "IsEnabled");
                    var allowAdj = GetMember(ap, "AllowActivationAdjustment");
                    var sched = GetMember(ap, "Schedule");
                    Console.WriteLine($"    • {key}");
                    Console.WriteLine($"        IsEnabled={isEnabled}  AllowActivationAdjustment={allowAdj}  Schedule={sched?.GetType().Name ?? "null"}");
                    if (sched != null)
                    {
                        foreach (var prop in new[] { "Start", "Duration", "EndingSoon", "Preview", "Review", "Recurrence", "NumRepeats", "TimeMode" })
                        {
                            var v = GetMember(sched, prop);
                            Console.WriteLine($"        {prop,-12} = {Describe(v)}");
                        }
                    }
                }
                if (hits == 0) Console.WriteLine($"    (no CollectibleBoardEvent matching '{substr}')");
                Console.WriteLine();
            }

            Console.WriteLine($"=== NEWEST archive by header CreatedAt: {newest.File}  @ {newest.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
    }

    // ── --probe-daily-challenges: DailyChallenges V2 libraries (not in JSON dump), base + patched ──
    private static int ProbeDailyChallenges(string configPath, string patchPath, string labelFilter)
    {
        Console.WriteLine("=== --probe-daily-challenges ===");
        Console.WriteLine($"Config:  {configPath}");
        Console.WriteLine($"Patches: {patchPath}");
        Console.WriteLine($"Filter:  {labelFilter ?? "(none)"}\n");
        try
        {
            MetaplayCore.Initialize();
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var masterConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
            ClientGlobal.SharedGameConfig = masterConfig;
            PrintDailyChallenges(masterConfig, "BASE (no patches)");

            if (!string.IsNullOrEmpty(patchPath) && patchPath != "-" && !string.IsNullOrEmpty(labelFilter))
            {
                var patchFiles = new List<string>();
                if (Directory.Exists(patchPath)) patchFiles.AddRange(Directory.GetFiles(patchPath).OrderBy(File.GetLastWriteTimeUtc));
                else if (File.Exists(patchPath)) patchFiles.Add(patchPath);

                // Distinct-content dedup per label (same rule as --dump-events-patched).
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pf in patchFiles)
                {
                    var specPatches = Metaplay.Core.Config.GameConfigSpecializationPatches.FromBytes(File.ReadAllBytes(pf));
                    foreach (var (expId, vs) in specPatches.Patches)
                        foreach (var (varId, bytes) in vs)
                        {
                            var label = $"{expId}_{varId}";
                            if (label.IndexOf(labelFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var raw = bytes ?? Array.Empty<byte>();
                            var dedupKey = label + "\0" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw));
                            if (!seen.Add(dedupKey)) continue;
                            var env = Metaplay.Core.Config.GameConfigPatchEnvelope.Deserialize(raw);
                            Console.WriteLine($"\n########## PATCH {label} ({raw.Length} B, entries=[{string.Join(",", env.EntryNames)}]) ##########\n");
                            try
                            {
                                var pa = new PatchedConfigArchive(archive, new[] { env });
                                var patched = SharedGameConfig.ImportPatchedFrom(masterConfig, pa, env.EntryNames.ToArray());
                                PrintDailyChallenges(patched, $"PATCHED: {label}");
                            }
                            catch (Exception ex) { Console.WriteLine($"  patch apply FAILED: {ex.GetType().Name}: {ex.Message}"); }
                        }
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
    }

    private static void PrintDailyChallenges(SharedGameConfig cfg, string header)
    {
        Console.WriteLine($"===== {header} =====");

        // 1) Weekly event timeline (CoreSupportEvents, EventType=DailyChallengesEvent)
        Console.WriteLine("--- Weekly events (CoreSupportEvents / DailyChallengesEvent) ---");
        if (cfg.CoreSupportEvents != null)
        {
            foreach (var kv in cfg.CoreSupportEvents.EnumerateAll())
            {
                var info = kv.Value;
                if (!string.Equals(GetMember(info, "EventType")?.ToString(), "DailyChallengesEvent", StringComparison.Ordinal)) continue;
                var ap = GetMember(info, "ActivableParams");
                var sched = GetMember(ap, "Schedule");
                Console.WriteLine($"  {kv.Key,-22} start={Describe(GetMember(sched, "Start"))}  dur={Describe(GetMember(sched, "Duration"))}  minigame={GetMember(info, "MinigameId")}  enabled={GetMember(ap, "IsEnabled")}");
            }
        }

        // 2) Minigames (week selection type) + selection maps
        Console.WriteLine("--- DailyChallengesMinigames (WeekSelectionType) ---");
        if (cfg.DailyChallengesMinigames != null)
            foreach (var kv in cfg.DailyChallengesMinigames.EnumerateAll())
                Console.WriteLine($"  {kv.Key,-20} selection={GetMember(kv.Value, "WeekSelectionType")}");

        Console.WriteLine("--- DailyChallengesWeeksByMinigameId (segment -> week variant) ---");
        if (cfg.DailyChallengesWeeksByMinigameId != null)
            foreach (var kv in cfg.DailyChallengesWeeksByMinigameId.EnumerateAll())
            {
                var segs = GetMember(kv.Value, "WeekSegments") as System.Collections.IEnumerable;
                var wks = GetMember(kv.Value, "WeeksIds") as System.Collections.IEnumerable;
                Console.WriteLine($"  {kv.Key,-20} segments=[{string.Join(", ", Enumerate(segs))}]  weeks=[{string.Join(", ", Enumerate(wks))}]");
            }

        Console.WriteLine("--- DailyChallengesWeeksByPreviousCompletion (adaptive difficulty) ---");
        if (cfg.DailyChallengesWeeksByPreviousCompletion != null)
            foreach (var kv in cfg.DailyChallengesWeeksByPreviousCompletion.EnumerateAll())
            {
                var ratios = GetMember(kv.Value, "CompletionRatioToNewWeekData") as System.Collections.IEnumerable;
                Console.WriteLine($"  prev={kv.Key,-30} -> [{string.Join(", ", Enumerate(ratios))}]");
            }

        Console.WriteLine($"--- DailyChallengesEventSettings: FallbackPreviousWeekId={GetMember(cfg.DailyChallengesEventSettings, "FallbackPreviousWeekId")} ---");

        // 3) Weeks: difficulty + milestone ladder + final reward
        Console.WriteLine("--- DailyChallengesWeeks ---");
        var milestones = new Dictionary<string, object>(StringComparer.Ordinal);
        if (cfg.DailyChallengesMilestones != null)
            foreach (var kv in cfg.DailyChallengesMilestones.EnumerateAll())
                milestones[kv.Key.ToString()] = kv.Value;
        if (cfg.DailyChallengesWeeks != null)
        foreach (var kv in cfg.DailyChallengesWeeks.EnumerateAll())
        {
            var w = kv.Value;
            var days = GetMember(w, "Days") as System.Collections.IEnumerable;
            var mids = (GetMember(w, "Milestones") as System.Collections.IEnumerable)?.Cast<object>().Select(Pretty).ToList() ?? new List<string>();
            Console.WriteLine($"  {kv.Key,-34} difficulty={GetMember(w, "WeekDifficulty")}  days=[{string.Join(", ", Enumerate(days))}]");
            foreach (var mid in mids)
            {
                if (!milestones.TryGetValue(mid, out var m)) { Console.WriteLine($"      milestone {mid}: <missing>"); continue; }
                var rewards = GetMember(m, "Rewards") as System.Collections.IEnumerable;
                var segFilter = GetMember(m, "RewardSegment") as System.Collections.IEnumerable;
                var rewardsStr = rewards == null ? "-" : string.Join(" + ", rewards.Cast<object>().Select(DescribeReward));
                var segStr = string.Join(",", Enumerate(segFilter));
                Console.WriteLine($"      {GetMember(m, "RequiredPoints"),5}b  {rewardsStr}{(segStr.Length > 0 ? $"   segFilter=[{segStr}]" : "")}");
            }
        }

        // 4) Days: composition
        Console.WriteLine("--- DailyChallengesDays ---");
        if (cfg.DailyChallengesDays != null)
        foreach (var kv in cfg.DailyChallengesDays.EnumerateAll())
        {
            var day = kv.Value;
            // Day completion reward lives in PRIVATE MetaMembers (Rewards + RewardSegment) —
            // this is where the daily box type (DailyChest1 vs DailyChest2) is defined.
            var dayRewards = GetAnyMember(day, "Rewards") as System.Collections.IEnumerable;
            var dayRewardSeg = GetAnyMember(day, "RewardSegment") as System.Collections.IEnumerable;
            var drStr = dayRewards == null ? "-" : string.Join(" + ", dayRewards.Cast<object>().Select(DescribeReward));
            var drSegStr = string.Join(",", Enumerate(dayRewardSeg));
            Console.WriteLine($"  {kv.Key,-40} minPerGroup=[{string.Join(",", Enumerate(GetMember(day, "MinObjectivesPerGroup") as System.Collections.IEnumerable))}]  specialGroups=[{string.Join(",", Enumerate(GetMember(day, "SpecialObjectiveGroups") as System.Collections.IEnumerable))}]  targetMilestoneIdx={GetMember(day, "TargetMilestoneIndex")}  reqCompletedForDayReward={GetMember(day, "RequiredCompletedObjectivesForDayReward")}");
            Console.WriteLine($"      dayReward: {drStr}{(drSegStr.Length > 0 ? $"   segFilter=[{drSegStr}]" : "")}");
            var stdRefs = GetMember(day, "StandardObjectives") as System.Collections.IEnumerable;
            Console.WriteLine($"      std=[{string.Join(", ", Enumerate(stdRefs))}]");
        }

        // 5) Standard objectives
        Console.WriteLine("--- DailyChallengesStandardObjectives ---");
        if (cfg.DailyChallengesStandardObjectives != null)
        foreach (var kv in cfg.DailyChallengesStandardObjectives.EnumerateAll())
        {
            var o = kv.Value;
            var pars = GetMember(o, "ObjectiveParameter") as System.Collections.IEnumerable;
            Console.WriteLine($"  {kv.Key,-46} type={GetMember(o, "ObjectiveType"),-18} req={GetMember(o, "ObjectiveRequirement"),-7} prio={GetMember(o, "OrderPriority"),-3} params=[{string.Join(",", Enumerate(pars))}] loc={GetMember(o, "LocId")}");
            DescribeRewardPool(GetMember(o, "RewardsPoolData"), "      ");
            var fallbacks = GetMember(o, "FallbackObjectiveIdReferencesList") as System.Collections.IEnumerable;
            var fbStr = string.Join(", ", Enumerate(fallbacks));
            if (fbStr.Length > 0) Console.WriteLine($"      fallbacks=[{fbStr}]");
            DescribeRequirements(GetAnyMember(o, "_requirements"), "      ");
        }

        // 6) Special objectives
        Console.WriteLine("--- DailyChallengesSpecialObjectives ---");
        if (cfg.DailyChallengesSpecialObjectives != null)
        foreach (var kv in cfg.DailyChallengesSpecialObjectives.EnumerateAll())
        {
            var o = kv.Value;
            var pars = GetMember(o, "ObjectiveParameter") as System.Collections.IEnumerable;
            var reqs = GetMember(o, "ObjectiveRequirement") as System.Collections.IEnumerable;
            var weights = GetMember(o, "GroupWeightBySegment") as System.Collections.IEnumerable;
            Console.WriteLine($"  {kv.Key,-46} group={GetMember(o, "ObjectiveGroup"),-5} type={GetMember(o, "ObjectiveType"),-18} params=[{string.Join(",", Enumerate(pars))}] reqs=[{string.Join(",", Enumerate(reqs))}] loc={GetMember(o, "LocId")}");
            Console.WriteLine($"      groupWeightBySegment={{{string.Join(", ", Enumerate(weights))}}}");
            DescribeRewardPool(GetMember(o, "RewardsPoolData"), "      ");
            DescribeRequirements(GetAnyMember(o, "_requirements"), "      ");
        }
        Console.WriteLine();
    }

    /// <summary>Print BaseObjectiveRewardPoolData (private members: RewardDefinitionsBySlotId, RewardSlotsAmount).</summary>
    private static void DescribeRewardPool(object pool, string indent)
    {
        if (pool == null) { Console.WriteLine($"{indent}rewardPool=null"); return; }
        var slotsAmount = GetAnyMember(pool, "RewardSlotsAmount");
        var bySlot = GetAnyMember(pool, "RewardDefinitionsBySlotId") as System.Collections.IEnumerable;
        var catchUp = GetAnyMember(pool, "ForcedCatchUpPointsAmounts") as System.Collections.IEnumerable;
        var catchUpStr = catchUp == null ? "" : $"  forcedCatchUpPoints=[{string.Join(",", Enumerate(catchUp))}]";
        Console.WriteLine($"{indent}rewardPool: slots={slotsAmount}{catchUpStr}");
        int slotIdx = 0;
        if (bySlot != null)
        {
            foreach (var slot in bySlot)
            {
                var defs = slot as System.Collections.IEnumerable;
                var parts = new List<string>();
                if (defs != null)
                {
                    foreach (var def in defs)
                    {
                        var weights = GetMember(def, "WeightPerSegment") as System.Collections.IEnumerable;
                        var amounts = GetAnyMember(def, "RewardAmounts") as System.Collections.IEnumerable;
                        var rewardType = GetAnyMember(def, "RewardType");
                        var aux0 = GetAnyMember(def, "RewardAux0");
                        var auxStr = string.IsNullOrEmpty(aux0?.ToString()) ? "" : $" aux={aux0}";
                        parts.Add($"{GetMember(def, "RewardId")}(type={rewardType}{auxStr} amounts=[{string.Join(",", Enumerate(amounts))}] w={{{string.Join(",", Enumerate(weights))}}})");
                    }
                }
                Console.WriteLine($"{indent}  slot{slotIdx}: {string.Join(" | ", parts)}");
                slotIdx++;
            }
        }
    }

    /// <summary>Print an objective's PlayerRequirement list (private _requirements MetaMember) —
    /// this is where per-player availability gating (e.g. PlayerItemRequirement) would live.</summary>
    private static void DescribeRequirements(object reqs, string indent)
    {
        if (reqs is not System.Collections.IEnumerable list) return;
        var parts = new List<string>();
        foreach (var r in list)
        {
            if (r == null) { parts.Add("null"); continue; }
            var sb = new System.Text.StringBuilder(r.GetType().Name);
            var fields = new List<string>();
            foreach (var fname in new[] { "ItemRefs", "ItemTypes", "Requirement", "Amount", "Level", "ChainId", "ItemDef", "_requirementAmount", "ActivableKind", "RegexPattern", "DurationInMilliseconds" })
            {
                var v = GetAnyMember(r, fname);
                if (v == null) continue;
                if (v is System.Collections.IEnumerable en && v is not string)
                {
                    var joined = string.Join(",", Enumerate(en));
                    if (joined.Length > 0) fields.Add($"{fname}=[{joined}]");
                }
                else fields.Add($"{fname}={v}");
            }
            if (fields.Count > 0) sb.Append('(').Append(string.Join(" ", fields)).Append(')');
            parts.Add(sb.ToString());
        }
        if (parts.Count > 0) Console.WriteLine($"{indent}requirements=[{string.Join("; ", parts)}]");
    }

    /// <summary>Like GetMember but also reads non-public properties/fields (MetaMember privates).</summary>
    private static object GetAnyMember(object obj, string name)
    {
        if (obj == null) return null;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, flags);
            if (p != null) { try { return p.GetValue(obj); } catch { return "<err>"; } }
            var fld = t.GetField(name, flags);
            if (fld != null) { try { return fld.GetValue(obj); } catch { return "<err>"; } }
        }
        return null;
    }

    // ── --probe-daily-scoop: per config archive, DailyScoopEvents (not in JSON dump) + week summary ──
    // ── --probe-chest: ChestFeatures loot structure incl. PrefixProducer prefix items ──
    private static int ProbeChest(string configPath, string itemTypeSubstring)
    {
        MetaplayCore.Initialize();
        var archive = Metaplay.Core.Config.ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
        var patched = PatchedConfigArchive.WithNoPatches(archive);
        var cfg = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patched);
        ClientGlobal.SharedGameConfig = cfg;

        int hits = 0;
        foreach (var item in cfg.Items.Values)
        {
            if (item?.ItemType == null || !item.ItemType.Contains(itemTypeSubstring, StringComparison.OrdinalIgnoreCase))
                continue;
            var chest = item.ChestFeatures;
            if (chest == null || !chest.IsChest) continue;
            hits++;
            Console.WriteLine($"=== {item.ItemType} ===");
            Console.WriteLine($"  HowManyToRoll = {chest.HowManyToRoll}");
            var lp = chest.LootProducer;
            Console.WriteLine($"  LootProducer  = {lp?.GetType().Name ?? "null"}");
            if (lp is GameLogic.Player.Items.Production.PrefixProducer pp)
            {
                Console.WriteLine($"  Marker        = {pp.Marker}");
                var items = pp.Items;
                if (items == null) Console.WriteLine("  Prefix Items  = null");
                else
                {
                    Console.WriteLine($"  Prefix Items  ({items.Count}):");
                    foreach (var it in items)
                        Console.WriteLine($"    {it?.GetDef(cfg)?.ItemType ?? "(unresolved)"}");
                }
                Console.WriteLine($"  BaseProducer  = {pp.BaseProducer?.GetType().Name ?? "null"}");
            }
        }
        Console.WriteLine($"\n{hits} chest item(s) matched \"{itemTypeSubstring}\".");
        return hits > 0 ? 0 : 2;
    }

    // ── --probe-fishing: FishingSettings splash-type → droplet count + weight arrays ──
    private static int ProbeFishing(string configPath, string? languagePath)
    {
        MetaplayCore.Initialize();
        var archive = Metaplay.Core.Config.ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
        var patched = PatchedConfigArchive.WithNoPatches(archive);
        var cfg = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patched);
        ClientGlobal.SharedGameConfig = cfg;

        var fs = cfg.FishingSettings;
        Console.WriteLine("=== FishingSettings ===");
        if (fs == null) { Console.WriteLine("FishingSettings = null (not populated)"); return 2; }

        // The two droplet-count dicts + weight arrays. Read via reflection so IgnoreDataMember
        // private/derived backing still surfaces whatever the deserializer set.
        void DumpMember(string name)
        {
            var val = GetAnyMember(fs, name);
            if (val is System.Collections.IDictionary dict)
            {
                var parts = new List<string>();
                foreach (System.Collections.DictionaryEntry e in dict) parts.Add($"{e.Key}={e.Value}");
                parts.Sort();
                Console.WriteLine($"  {name} = {{{string.Join(", ", parts)}}}");
            }
            else if (val is System.Collections.IEnumerable en && val is not string)
            {
                Console.WriteLine($"  {name} = [{string.Join(", ", Enumerate(en))}]");
            }
            else Console.WriteLine($"  {name} = {val}");
        }
        // SplashType enum: None=0, VeryTiny=1, Tiny=2, Medium=3, Large=4, VeryLarge=5
        Console.WriteLine("  (SplashType: 1=VeryTiny 2=Tiny 3=Medium 4=Large 5=VeryLarge)");
        DumpMember("SmallFishWaterDropletCounts");
        DumpMember("NonFishWaterDropletCounts");
        DumpMember("FishWeightCategoryOdds");
        DumpMember("FishWeightCategorySizePercentages");

        // Dicts are [IgnoreDataMember] in the decompiled DLL → deserializer skips them.
        // Hexdump the raw FishingSettings.mpc so members 1/2 (the count maps) can be decoded.
        Console.WriteLine();
        Console.WriteLine("=== Raw FishingSettings.mpc byte scan ===");
        try
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name == "FishingSettings.mpc" || e.Name == "FishingSettings");
            if (entry == null)
            {
                Console.WriteLine("FishingSettings entry NOT FOUND. Entries containing 'Fish': "
                    + string.Join(", ", archive.Entries.Where(e => e.Name.Contains("Fish", StringComparison.OrdinalIgnoreCase)).Select(e => e.Name)));
            }
            else
            {
                var bytes = entry.Uncompress();
                Console.WriteLine($"Entry: {entry.Name}, uncompressed={bytes.Length} B");
                for (int off = 0; off < bytes.Length; off += 32)
                {
                    int count = Math.Min(32, bytes.Length - off);
                    var hex = new string[count]; var asc = new char[count];
                    for (int i = 0; i < count; i++)
                    { byte b = bytes[off + i]; hex[i] = b.ToString("x2"); asc[i] = (b >= 32 && b < 127) ? (char)b : '.'; }
                    Console.WriteLine($"  {off:x4}: {string.Join(" ", hex).PadRight(95)}  {new string(asc)}");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"Raw scan error: {ex.Message}"); }
        return 0;
    }

    private static int ProbeSegments(string configPath, string idFilter)
    {
        MetaplayCore.Initialize();
        var archive = Metaplay.Core.Config.ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
        var patched = PatchedConfigArchive.WithNoPatches(archive);
        var cfg = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patched);
        if (cfg.PlayerSegments == null) { Console.Error.WriteLine("PlayerSegments library missing"); return 2; }

        foreach (var kv in cfg.PlayerSegments.EnumerateAll())
        {
            var seg = (Metaplay.Core.Player.PlayerSegmentInfoBase)kv.Value;
            var id = seg.ConfigKey?.ToString() ?? "?";
            if (!string.IsNullOrEmpty(idFilter) && !id.Contains(idFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            Console.WriteLine($"  {id,-40} display='{seg.DisplayName}'");
            if (seg.PlayerCondition is Metaplay.Core.Player.PlayerSegmentBasicCondition bc)
            {
                foreach (var r in bc.PropertyRequirements ?? new List<Metaplay.Core.Player.PlayerPropertyRequirement>())
                    Console.WriteLine($"      prop {r.Id,-40} min={ConstVal(r.Min)}  max={ConstVal(r.Max)}");
                if (bc.RequireAnySegment is { Count: > 0 })
                    Console.WriteLine($"      requireAny=[{string.Join(", ", bc.RequireAnySegment)}]");
                if (bc.RequireAllSegments is { Count: > 0 })
                    Console.WriteLine($"      requireAll=[{string.Join(", ", bc.RequireAllSegments)}]");
            }
            else
                Console.WriteLine($"      condition={seg.PlayerCondition?.GetType().Name ?? "null"}");
        }
        return 0;
    }

    private static int ProbeInventory(string configPath)
    {
        MetaplayCore.Initialize();
        var archive = Metaplay.Core.Config.ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
        var patched = PatchedConfigArchive.WithNoPatches(archive);
        var cfg = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patched);
        if (cfg.InventorySlots == null) { Console.Error.WriteLine("InventorySlots library missing"); return 2; }

        int free = 0, paid = 0;
        Console.WriteLine("=== InventorySlots (garage/pocket inventory) ===");
        foreach (var kv in cfg.InventorySlots.EnumerateAll())
        {
            var slot = (GameLogic.Config.InventorySlotsConfig)kv.Value;
            Console.WriteLine($"  {slot.ConfigKey,-24} currency={slot.Currency,-10} cost={slot.Cost}");
            if (slot.Cost == 0) free++; else paid++;
        }
        Console.WriteLine($"\nTotal slots: {free + paid}  (free/base: {free}, purchasable: {paid})");
        return 0;
    }

    // PlayerPropertyConstant subtypes (F64Constant/LongConstant/BoolConstant/...) hide the
    // value in a private field — unwrap via reflection for readable probe output.
    private static string ConstVal(object c)
    {
        if (c == null) return "null";
        var t = c.GetType();
        foreach (var name in new[] { "ConstantValue", "Value", "_value", "value" })
        {
            var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (f != null) return f.GetValue(c)?.ToString() ?? "null";
            var pr = t.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (pr != null) return pr.GetValue(c)?.ToString() ?? "null";
        }
        return c.ToString();
    }

    private static int ProbeDailyScoop(string cDirOrFile, string languagePath)
    {
        var files = new List<string>();
        if (Directory.Exists(cDirOrFile)) files.AddRange(Directory.GetFiles(cDirOrFile).OrderBy(f => f));
        else if (File.Exists(cDirOrFile)) files.Add(cDirOrFile);
        if (files.Count == 0) { Console.Error.WriteLine($"No config files at {cDirOrFile}"); return 2; }

        Console.WriteLine($"=== --probe-daily-scoop over {files.Count} archive(s) ===\n");
        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }

            (string File, DateTimeOffset? CreatedAt) newest = (null, null);
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                Metaplay.Core.Config.ConfigArchive archive;
                DateTimeOffset? createdAt = null;
                try
                {
                    archive = Metaplay.Core.Config.ConfigArchive.FromBytes(File.ReadAllBytes(f));
                    createdAt = DateTimeOffset.FromUnixTimeMilliseconds(archive.CreatedAt.MillisecondsSinceEpoch);
                }
                catch (Exception ex) { Console.WriteLine($"[{name}] header read FAILED: {ex.Message}"); continue; }

                if (createdAt.HasValue && (newest.CreatedAt == null || createdAt > newest.CreatedAt))
                    newest = (name, createdAt);

                Console.WriteLine($"### Archive {name}");
                Console.WriteLine($"    CreatedAt (header) = {createdAt:yyyy-MM-dd HH:mm:ss} UTC   entries={archive.Entries.Count}");

                GameLogic.Config.SharedGameConfig cfg;
                try
                {
                    var pa = Metaplay.Core.Config.PatchedConfigArchive.WithNoPatches(archive);
                    cfg = (GameLogic.Config.SharedGameConfig)Metaplay.Core.Config.GameConfigFactory.Instance.ImportSharedGameConfig(pa);
                    ClientGlobal.SharedGameConfig = cfg;
                }
                catch (Exception ex) { Console.WriteLine($"    import FAILED: {ex.GetType().Name}: {ex.Message}\n"); continue; }

                // 1) DailyScoopEvents — schedule + WeekIds rotation + WeekSegments (dump-invisible)
                if (cfg.DailyScoopEvents == null)
                {
                    Console.WriteLine("    DailyScoopEvents: <library null>");
                }
                else
                {
                    foreach (var kv in cfg.DailyScoopEvents.EnumerateAll())
                    {
                        var info = kv.Value;
                        var ap = GetMember(info, "ActivableParams");
                        var sched = GetMember(ap, "Schedule");
                        Console.WriteLine($"    • Event {kv.Key}  DisplayName='{GetMember(info, "DisplayName")}'");
                        Console.WriteLine($"        IsEnabled={GetMember(ap, "IsEnabled")}  Lifetime={Describe(GetMember(ap, "Lifetime"))}  Schedule={sched?.GetType().Name ?? "null"}");
                        if (sched != null)
                        {
                            foreach (var prop in new[] { "Start", "Duration", "EndingSoon", "Preview", "Review", "Recurrence", "NumRepeats", "TimeMode" })
                                Console.WriteLine($"        {prop,-12} = {Describe(GetMember(sched, prop))}");
                        }
                        Console.WriteLine($"        UnlockRequirement = {Describe(GetMember(info, "UnlockRequirement"))}");
                        var weekIds = GetMember(info, "WeekIds") as System.Collections.IEnumerable;
                        var weekSegs = GetMember(info, "WeekSegments") as System.Collections.IEnumerable;
                        Console.WriteLine($"        WeekIds      = [{string.Join(", ", Enumerate(weekIds))}]");
                        Console.WriteLine($"        WeekSegments = [{string.Join(", ", Enumerate(weekSegs))}]");
                        var segs = GetMember(info, "Segments") as System.Collections.IEnumerable;
                        Console.WriteLine($"        Segments     = [{string.Join(", ", Enumerate(segs))}]");
                    }
                }

                // 2) Week summary — point ladder + final reward (spots retuned week revisions per archive)
                if (cfg.DailyScoopWeeks != null && cfg.DailyScoopMilestones != null)
                {
                    var milestones = cfg.DailyScoopMilestones.EnumerateAll()
                        .ToDictionary(kv => kv.Key.ToString(), kv => (Code.GameLogic.GameEvents.DailyScoop.DailyScoopMilestoneData)kv.Value);
                    Console.WriteLine($"    Weeks ({cfg.DailyScoopWeeks.EnumerateAll().Count()}):");
                    foreach (var kv in cfg.DailyScoopWeeks.EnumerateAll())
                    {
                        var week = (Code.GameLogic.GameEvents.DailyScoop.DailyScoopWeekData)kv.Value;
                        var ladder = new List<string>();
                        string finalReward = "-";
                        foreach (var mid in week.Milestones ?? new List<Code.GameLogic.GameEvents.DailyScoop.DailyScoopMilestoneId>())
                        {
                            if (!milestones.TryGetValue(mid.ToString(), out var m)) { ladder.Add("?"); continue; }
                            ladder.Add(m.RequiredPoints.ToString());
                            finalReward = m.Rewards == null ? "-" : string.Join(" + ", m.Rewards.Select(DescribeReward));
                        }
                        Console.WriteLine($"      {kv.Key,-40} [{string.Join(",", ladder)}]  FINAL: {finalReward}");
                    }
                }
                Console.WriteLine();
            }

            Console.WriteLine($"=== NEWEST archive by header CreatedAt: {newest.File}  @ {newest.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
    }

    private static IEnumerable<string> Enumerate(System.Collections.IEnumerable src)
    {
        if (src == null) yield break;
        foreach (var item in src) yield return Pretty(item);
    }

    /// <summary>Human-readable rendering: unwraps ConfigId (→ ConfigKey), ValueTuple and KeyValuePair.</summary>
    private static string Pretty(object item)
    {
        if (item == null) return "null";
        var t = item.GetType();
        var configKey = t.GetProperty("ConfigKey");
        if (configKey != null && t.Name.StartsWith("ConfigId"))
        {
            try { return Pretty(configKey.GetValue(item)); } catch { }
        }
        if (t.IsGenericType && t.Name.StartsWith("ValueTuple"))
        {
            var i1 = t.GetField("Item1")?.GetValue(item);
            var i2 = t.GetField("Item2")?.GetValue(item);
            return $"({Pretty(i1)} -> {Pretty(i2)})";
        }
        if (t.IsGenericType && t.Name.StartsWith("KeyValuePair"))
        {
            var k = t.GetProperty("Key")?.GetValue(item);
            var v = t.GetProperty("Value")?.GetValue(item);
            return $"{Pretty(k)}={Pretty(v)}";
        }
        return item.ToString();
    }

    /// <summary>Read a public property or field by name via reflection (handles both kinds).</summary>
    private static object GetMember(object obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (p != null) { try { return p.GetValue(obj); } catch { return "<err>"; } }
        var fld = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (fld != null) { try { return fld.GetValue(obj); } catch { return "<err>"; } }
        return null;
    }

    /// <summary>Stringify a calendar value (MetaCalendarDateTime/Period) by reflecting its non-zero parts.</summary>
    private static string Describe(object v)
    {
        if (v == null) return "null";
        var t = v.GetType();
        if (t.IsPrimitive || v is string || v is Enum) return v.ToString();
        var parts = new List<string>();
        foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object val; try { val = p.GetValue(v); } catch { continue; }
            if (val == null) continue;
            if (val is int iv && iv == 0) continue;
            if (val.GetType().IsPrimitive || val is string || val is Enum)
                parts.Add($"{p.Name}={val}");
        }
        var s = string.Join(" ", parts);
        return string.IsNullOrEmpty(s) ? $"<{t.Name}:{v}>" : $"{t.Name}({s})";
    }

    private static int ProbeBubble(string dataDir, string itemPrefix, string languagePath)
    {
        Console.WriteLine($"=== DumpHarness --probe-bubble (itemPrefix={itemPrefix}) ===");
        Console.WriteLine($"Data dir: {dataDir}");

        var cDir = Path.Combine(dataDir, "C");
        var pDir = Path.Combine(dataDir, "P");
        if (!Directory.Exists(cDir) || !Directory.Exists(pDir))
        {
            Console.Error.WriteLine("Expected <dataDir>/C/ and <dataDir>/P/");
            return 2;
        }

        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }

            var configFiles = Directory.GetFiles(cDir);
            var patchFiles = Directory.GetFiles(pDir);
            Console.WriteLine($"Configs: {configFiles.Length}, patch files: {patchFiles.Length}");

            // Track ALL patch envelopes per file (no merge dedup)
            var allPatches = new List<(string FileName, Metaplay.Core.Player.PlayerExperimentId ExpId, Metaplay.Core.Player.ExperimentVariantId VarId, Metaplay.Core.Config.GameConfigPatchEnvelope Env)>();
            foreach (var pf in patchFiles)
            {
                var fileName = Path.GetFileName(pf);
                var spec = Metaplay.Core.Config.GameConfigSpecializationPatches.FromBytes(File.ReadAllBytes(pf));
                foreach (var (expId, vs) in spec.Patches)
                    foreach (var (varId, bytes) in vs)
                        allPatches.Add((fileName, expId, varId, Metaplay.Core.Config.GameConfigPatchEnvelope.Deserialize(bytes)));
            }
            Console.WriteLine($"Total raw (expId, varId) tuples across files: {allPatches.Count}");
            Console.WriteLine();

            foreach (var cf in configFiles)
            {
                var cName = Path.GetFileName(cf);
                Console.WriteLine($"=== CONFIG ARCHIVE: {cName} ===");
                var archive = Metaplay.Core.Config.ConfigArchive.FromBytes(File.ReadAllBytes(cf));
                var baseConfig = (GameLogic.Config.SharedGameConfig)Metaplay.Core.Config.GameConfigFactory.Instance.ImportSharedGameConfig(
                    Metaplay.Core.Config.PatchedConfigArchive.WithNoPatches(archive));

                PrintBubbleSnapshot("BASELINE", baseConfig, itemPrefix);

                foreach (var (fileName, expId, varId, env) in allPatches)
                {
                    var label = $"{fileName.Substring(0, 8)}.. / {expId}_{varId}";
                    try
                    {
                        var pa = new Metaplay.Core.Config.PatchedConfigArchive(archive, new[] { env });
                        var pcfg = GameLogic.Config.SharedGameConfig.ImportPatchedFrom(baseConfig, pa, env.EntryNames.ToArray());
                        PrintBubbleSnapshot(label, pcfg, itemPrefix);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [{label}] FAILED: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                Console.WriteLine();
            }

            Console.WriteLine("=== Done ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static void PrintBubbleSnapshot(string label, GameLogic.Config.SharedGameConfig cfg, string itemPrefix)
    {
        try
        {
            // SingleMergeChainElement.First() resolves via ClientGlobal.SharedGameConfig.Items.
            // Swap the global so the iteration reads the patched config's items, not the
            // last assigned baseline.
            ClientGlobal.SharedGameConfig = cfg;

            var items = new List<GameLogic.Player.Items.ItemDefinition>();
            foreach (var (key, chainObj) in cfg.MergeChains.EnumerateAll())
            {
                var chain = (GameLogic.MergeChains.MergeChainDefinition)chainObj;
                if (chain?.PrimaryChain == null) continue;
                foreach (var element in chain.PrimaryChain)
                {
                    GameLogic.Player.Items.ItemDefinition it = null;
                    try { it = element?.First(); } catch { continue; }
                    if (it?.ItemType?.ToString().StartsWith(itemPrefix, StringComparison.Ordinal) == true)
                        items.Add(it);
                }
            }
            items = items.OrderBy(it => it.ItemType?.ToString()).ToList();
            if (items.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.Append($"  [{label}] ");
            foreach (var it in items)
            {
                var bf = it.BubbleFeatures;
                if (bf == null) { sb.Append($"{it.ItemType}=NULL "); }
                else { sb.Append($"{it.ItemType}=odds:{bf.SpawnOdds} "); }
            }
            Console.WriteLine(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{label}] SNAPSHOT FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.StackTrace != null)
            {
                var firstFrame = ex.StackTrace.Split('\n').FirstOrDefault();
                if (firstFrame != null) Console.WriteLine($"    at {firstFrame.Trim()}");
            }
        }
    }

    private static int ProbeBundles(string apkDir)
    {
        if (!Directory.Exists(apkDir))
        {
            Console.Error.WriteLine($"Not a directory: {apkDir}");
            return 2;
        }
        var targetPatterns = new[] {
            "uiicons", "scriptableobjectsareaiconslibrary",
            "uihotspot", "uiunifiedicons"
        };
        var bundles = Directory.EnumerateFiles(apkDir, "*.bundle", SearchOption.AllDirectories)
            .Where(p => targetPatterns.Any(pat => Path.GetFileName(p).ToLowerInvariant().Contains(pat)))
            .ToList();
        Console.WriteLine($"Found {bundles.Count} candidate bundles in {apkDir}:");
        foreach (var b in bundles) Console.WriteLine($"  - {Path.GetFileName(b)}");
        Console.WriteLine();

        var am = new AssetsTools.NET.Extra.AssetsManager();
        // Try to auto-detect classdata.tpk for type info (optional — without it we can still enumerate by class id)
        try
        {
            var tpkCandidates = new[] {
                Path.Combine(Path.GetDirectoryName(apkDir) ?? apkDir, "..", "classdata.tpk"),
                Path.Combine(Path.GetDirectoryName(apkDir) ?? apkDir, "classdata.tpk"),
                Path.Combine(apkDir, "classdata.tpk"),
            };
            foreach (var tpk in tpkCandidates)
                if (File.Exists(tpk)) { am.LoadClassPackage(tpk); Console.WriteLine($"Loaded classdata.tpk from {tpk}"); break; }
        }
        catch { }

        foreach (var bpath in bundles)
        {
            Console.WriteLine($"\n=== {Path.GetFileName(bpath)} ===");
            try
            {
                var bunInst = am.LoadBundleFile(bpath, unpackIfPacked: true);
                if (bunInst == null) { Console.WriteLine("  (failed to load)"); continue; }

                foreach (var afInst in am.LoadAllAssetsFromBundle(bunInst))
                {
                    try { am.LoadClassDatabaseFromPackage(afInst.file.Metadata.UnityVersion); } catch { }
                    var afile = afInst.file;
                    // Texture2D + Sprite + MonoBehaviour names
                    foreach (AssetClassID classId in new[] {
                        AssetClassID.Texture2D,
                        AssetClassID.Sprite,
                        AssetClassID.MonoBehaviour })
                    {
                        var assets = afile.GetAssetsOfType(classId);
                        if (assets.Count == 0) continue;
                        foreach (var ai in assets)
                        {
                            string nm = "";
                            try
                            {
                                var baseField = am.GetBaseField(afInst, ai);
                                nm = baseField?["m_Name"]?.AsString ?? "";
                            }
                            catch { }
                            // Filter: only names containing theme keywords or minigame/icon keywords
                            if (string.IsNullOrEmpty(nm)) continue;
                            var lo = nm.ToLowerInvariant();
                            // Show all names (for uiicons / areaicons exploration)
                            Console.WriteLine($"  [{classId}] {nm}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }

    // Harness-side dynamic icon extraction — mirrors Services/MinigameIconExtractor.cs.
    // Themes are discovered from SharedGameConfig (CardStacks + CustomTables);
    // sprite names are guessed via candidate patterns and matched in UI-ish bundles.
    private static int ExtractIcons(string gameFilesRoot, string outputDir, string tpkPath)
    {
        // Boot MetaplayCore + import SharedGameConfig to discover themes dynamically.
        // Config archive is expected under <gameFilesRoot>/../Dump/ or passed via --config.
        // For the harness we just re-use the _DATA paths from the earlier workflow.
        string[] guessedConfigPaths = {
            Path.Combine(gameFilesRoot, "..", "_DATA", "C"),
            @"D:\_BACKUP_2.0\Code Projects\MergeMansionWikiTools\bin\Debug\net9.0-windows10.0.19041.0\win-x64\_DATA\C",
        };
        string? configDir = guessedConfigPaths.FirstOrDefault(Directory.Exists);
        List<string> themes = new();
        if (configDir != null)
        {
            try
            {
                var cFile = Directory.EnumerateFiles(configDir).FirstOrDefault();
                if (cFile != null)
                {
                    Metaplay.Core.MetaplayCore.Initialize();
                    var archive = Metaplay.Core.Config.ConfigArchive.FromBytes(File.ReadAllBytes(cFile));
                    var patched = Metaplay.Core.Config.PatchedConfigArchive.WithNoPatches(archive);
                    var cfg = (GameLogic.Config.SharedGameConfig)
                        Metaplay.Core.Config.GameConfigFactory.Instance.ImportSharedGameConfig(patched);
                    // Enumerate themes
                    if (cfg.CardStacks != null)
                        foreach (var kv in cfg.CardStacks.EnumerateAll())
                        {
                            var info = (GameLogic.Hotspots.CardStack.CardStackInfo)kv.Value;
                            if (!string.IsNullOrEmpty(info.Theme)) themes.Add(info.Theme);
                        }
                    if (cfg.CustomTables != null)
                        foreach (var kv in cfg.CustomTables.EnumerateAll())
                        {
                            var info = (Code.GameLogic.Hotspots.CustomHotspotTablesInfo)kv.Value;
                            if (!string.IsNullOrEmpty(info.Theme)) themes.Add(info.Theme);
                        }
                    themes = themes.Distinct(StringComparer.Ordinal).ToList();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Could not auto-discover themes: {ex.Message}");
            }
        }
        if (themes.Count == 0)
        {
            Console.WriteLine("[INFO] No themes discovered from config — falling back to known 26.03.01 set.");
            themes = new List<string> { "Dollhouse", "Painting", "Perfumery", "Card", "Book", "SpyNotes" };
        }
        Console.WriteLine($"Themes to extract: {string.Join(", ", themes)}");

        // Candidate sprite names per theme (matches Services/MinigameIconExtractor.cs logic).
        IEnumerable<string> CandsFor(string theme)
        {
            var t = theme; var tl = theme.ToLowerInvariant();
            yield return $"MapSpot_Icon_{t}Task";
            yield return $"MapSpot_Icon_{t}";
            yield return $"ui_icon_{tl}";
            yield return $"ui_icon_{tl}_white";
            yield return $"ui_icon_area_hotspot_{tl}";
            yield return $"Icon_{t}";
            yield return $"{t}_Icon";
        }
        var spriteToTheme = new Dictionary<string, (string Theme, int Rank)>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in themes)
        {
            int rank = 0;
            foreach (var c in CandsFor(theme))
            {
                if (!spriteToTheme.ContainsKey(c)) spriteToTheme[c] = (theme, rank);
                rank++;
            }
        }
        var unresolved = new HashSet<string>(themes);

        // Scan bundles in pattern priority order.
        var patterns = new[] {
            "uigeneric_assets_all",
            "featuresstackminigamesprites_assets_all",
            "featuresillustrationtask",
            "uiicons_assets_all",
            "uisharedalleventsui_assets_all",
            "scriptableobjectsillustration",
            "scriptableobjectsareaiconslibrary",
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundles = new List<string>();
        foreach (var pattern in patterns)
            foreach (var sub in new[] { "APK", "Server" })
            {
                var dir = Path.Combine(gameFilesRoot, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (var b in Directory.EnumerateFiles(dir, "*.bundle", SearchOption.TopDirectoryOnly))
                    if (Path.GetFileName(b).Contains(pattern, StringComparison.OrdinalIgnoreCase) && seen.Add(b))
                        bundles.Add(b);
            }

        Directory.CreateDirectory(outputDir);
        int extracted = 0, missing = 0;

        // PASS 1 — scan every bundle, collect the best-ranked candidate per theme.
        // "Best" = lowest rank (MapSpot_Icon_*Task has highest priority, ui_icon_*_white lowest).
        // This prevents the first-found white fallback from blocking the colored MapSpot icon
        // that lives in a later-scanned bundle.
        var bestPerTheme = new Dictionary<string, (int Rank, string Bundle, string SpriteName)>(StringComparer.Ordinal);
        Console.WriteLine($"Scanning {bundles.Count} bundles (PASS 1 — pick best candidate per theme)...");
        foreach (var bundlePath in bundles)
        {
            if (themes.All(t => bestPerTheme.TryGetValue(t, out var b) && b.Rank == 0)) break; // all got rank-0
            try
            {
                var am = new AssetsManager();
                am.LoadClassPackage(tpkPath);
                var bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
                for (int i = 0; i < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    AssetsTools.NET.Extra.AssetsFileInstance? afInst;
                    try { afInst = am.LoadAssetsFileFromBundle(bunInst, i); } catch { continue; }
                    if (afInst?.file == null) continue;
                    try { am.LoadClassDatabaseFromPackage(afInst.file.Metadata.UnityVersion); } catch { }
                    foreach (var si in afInst.file.GetAssetsOfType(AssetClassID.Sprite))
                    {
                        string nm;
                        try { nm = am.GetBaseField(afInst, si)["m_Name"].AsString ?? ""; } catch { continue; }
                        if (!spriteToTheme.TryGetValue(nm, out var tr)) continue;
                        if (bestPerTheme.TryGetValue(tr.Theme, out var existing) && existing.Rank <= tr.Rank) continue;
                        bestPerTheme[tr.Theme] = (tr.Rank, bundlePath, nm);
                    }
                }
            }
            catch { }
        }
        foreach (var t in themes)
        {
            if (bestPerTheme.TryGetValue(t, out var b))
                Console.WriteLine($"  {t}: rank {b.Rank} → {b.SpriteName} in {Path.GetFileName(b.Bundle)}");
            else
                Console.WriteLine($"  {t}: no candidate found");
        }

        // PASS 2 — for each theme, extract just that one sprite from the chosen bundle.
        Console.WriteLine("\nPASS 2 — extract chosen sprites:");
        foreach (var group in bestPerTheme.Values.GroupBy(b => b.Bundle))
        {
            var wantedSprites = group.ToDictionary(g => g.SpriteName, g => themes.First(t => bestPerTheme[t].SpriteName == g.SpriteName), StringComparer.OrdinalIgnoreCase);
            var resolved = new HashSet<string>(wantedSprites.Values);
            ExtractFromBundleInline(group.Key, tpkPath, outputDir, wantedSprites.ToDictionary(kv => kv.Key, kv => (kv.Value, 0)), resolved, ref extracted, ref missing);
        }

        // Check unresolved (themes with no candidate in any bundle)
        foreach (var t in themes)
        {
            if (!bestPerTheme.ContainsKey(t))
            {
                Console.WriteLine($"[NOT-FOUND] Theme '{t}' — no sprite matches in any bundle");
                missing++;
            }
        }

        Console.WriteLine($"\nExtracted {extracted}. Missing {missing}.");
        return missing == 0 ? 0 : 1;
    }

    private static void ExtractFromBundleInline(string bundlePath, string tpkPath, string outputDir,
        Dictionary<string, (string Theme, int Rank)> spriteToTheme, HashSet<string> unresolved,
        ref int extracted, ref int missing)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpkPath);
        BundleFileInstance bunInst;
        try { bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true); }
        catch (Exception ex) { Console.WriteLine($"  [LOAD] {ex.Message}"); return; }

        // Collect all asset files in bundle + build cross-file texture lookup.
        var allAssetFiles = new List<AssetsTools.NET.Extra.AssetsFileInstance>();
        for (int i = 0; i < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            AssetsTools.NET.Extra.AssetsFileInstance? fi;
            try { fi = am.LoadAssetsFileFromBundle(bunInst, i); }
            catch { continue; }
            if (fi?.file != null) allAssetFiles.Add(fi);
        }
        foreach (var afInst in allAssetFiles)
        {
            try { am.LoadClassDatabaseFromPackage(afInst.file.Metadata.UnityVersion); } catch { }
        }

        // Build texture lookup: (fileInstance, pathId) — try matching by pathId across all files
        // (sprite→texture references in Unity bundles can span asset files via m_FileID).
        var texturesByPathId = new Dictionary<long, (AssetsTools.NET.Extra.AssetsFileInstance AfInst, AssetFileInfo Info)>();
        foreach (var afInst in allAssetFiles)
            foreach (var ti in afInst.file.GetAssetsOfType(AssetClassID.Texture2D))
                texturesByPathId[ti.PathId] = (afInst, ti);

        var decoded = new Dictionary<long, Image<Bgra32>?>();
        Image<Bgra32>? Decode(long pathId)
        {
            if (decoded.TryGetValue(pathId, out var c)) return c;
            if (!texturesByPathId.TryGetValue(pathId, out var entry)) { decoded[pathId] = null; return null; }
            try
            {
                var bf = am.GetBaseField(entry.AfInst, entry.Info);
                var tf = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(bf);
                byte[]? data = null;
                try { data = tf.FillPictureData(entry.AfInst); } catch { }
                if (data == null || data.Length == 0) { tf.SetPictureDataFromBundle(bunInst); data = tf.pictureData; }
                if (data == null || data.Length == 0) { decoded[pathId] = null; return null; }
                var raw = tf.DecodeTextureRaw(data, useBgra: true);
                if (raw == null || raw.Length == 0) { decoded[pathId] = null; return null; }
                var img = Image.LoadPixelData<Bgra32>(raw, tf.m_Width, tf.m_Height);
                img.Mutate(x => x.Flip(FlipMode.Vertical));
                decoded[pathId] = img;
                return img;
            }
            catch (Exception ex) { Console.WriteLine($"  DECODE err: {ex.Message}"); decoded[pathId] = null; return null; }
        }

        // Build SpriteAtlas fallback: spriteName → (textureName, rect)
        // Used when Sprite.m_RD.texture.m_PathID == 0 (packed sprite).
        var atlasFallback = new Dictionary<string, (long TexPathId, float Rx, float Ry, float Rw, float Rh)>(StringComparer.Ordinal);
        foreach (var afInst in allAssetFiles)
        {
            foreach (var atlas in afInst.file.GetAssetsOfType(AssetClassID.SpriteAtlas))
            {
                try
                {
                    var bf = am.GetBaseField(afInst, atlas);
                    var names = new List<string>();
                    var namesField = bf["m_PackedSpriteNamesToIndex.Array"];
                    if (!namesField.IsDummy)
                        foreach (var c in namesField.Children) names.Add(c.AsString);
                    var mapField = bf["m_RenderDataMap.Array"];
                    if (mapField.IsDummy) continue;
                    int idx = 0;
                    foreach (var entry in mapField.Children)
                    {
                        if (idx >= names.Count) break;
                        var value = entry[1];
                        var rect = value["textureRect"];
                        long texPathId = value["texture"]["m_PathID"].AsLong;
                        var name = names[idx];
                        if (!string.IsNullOrEmpty(name) && !atlasFallback.ContainsKey(name))
                            atlasFallback[name] = (texPathId,
                                rect["x"].AsFloat, rect["y"].AsFloat,
                                rect["width"].AsFloat, rect["height"].AsFloat);
                        idx++;
                    }
                }
                catch { }
            }
        }

        foreach (var afInst in allAssetFiles)
        {
            // Per-theme best match (lowest rank wins).
            var bestByTheme = new Dictionary<string, (int Rank, AssetFileInfo Sprite, long TexPathId, float Rx, float Ry, float Rw, float Rh)>();
            foreach (var si in afInst.file.GetAssetsOfType(AssetClassID.Sprite))
            {
                string nm;
                AssetTypeValueField sf;
                try { sf = am.GetBaseField(afInst, si); nm = sf["m_Name"].AsString ?? ""; } catch { continue; }
                if (string.IsNullOrEmpty(nm)) continue;
                if (!spriteToTheme.TryGetValue(nm, out var tr)) continue;
                if (!unresolved.Contains(tr.Theme)) continue;
                if (bestByTheme.TryGetValue(tr.Theme, out var existing) && existing.Rank <= tr.Rank) continue;
                try
                {
                    var rd = sf["m_RD"];
                    long texPathId = rd["texture"]["m_PathID"].AsLong;
                    float rx = rd["textureRect"]["x"].AsFloat;
                    float ry = rd["textureRect"]["y"].AsFloat;
                    float rw = rd["textureRect"]["width"].AsFloat;
                    float rh = rd["textureRect"]["height"].AsFloat;
                    // Fallback to SpriteAtlas metadata if direct reference is null.
                    if (texPathId == 0 && atlasFallback.TryGetValue(nm, out var atlasEntry))
                    {
                        texPathId = atlasEntry.TexPathId;
                        rx = atlasEntry.Rx; ry = atlasEntry.Ry; rw = atlasEntry.Rw; rh = atlasEntry.Rh;
                    }
                    bestByTheme[tr.Theme] = (tr.Rank, si, texPathId, rx, ry, rw, rh);
                }
                catch (Exception ex) { Console.WriteLine($"  [META] {nm}: {ex.Message}"); }
            }
            if (bestByTheme.Count == 0) continue;

            foreach (var (theme, info) in bestByTheme)
            {
                var parent = Decode(info.TexPathId);
                if (parent == null) { Console.WriteLine($"  [NOTEX] {theme} (pathId={info.TexPathId})"); missing++; continue; }
                int cx = (int)Math.Round(info.Rx);
                int cy = parent.Height - (int)Math.Round(info.Ry) - (int)Math.Round(info.Rh);
                int cw = (int)Math.Round(info.Rw);
                int ch = (int)Math.Round(info.Rh);
                cx = Math.Clamp(cx, 0, parent.Width - 1);
                cy = Math.Clamp(cy, 0, parent.Height - 1);
                cw = Math.Clamp(cw, 1, parent.Width - cx);
                ch = Math.Clamp(ch, 1, parent.Height - cy);
                try
                {
                    using var crop = parent.Clone(x => x.Crop(new Rectangle(cx, cy, cw, ch)));
                    var outPath = Path.Combine(outputDir, $"MinigameIcon_{theme}.png");
                    crop.SaveAsPng(outPath);
                    Console.WriteLine($"  -> {theme} ({cw}x{ch}) {Path.GetFileName(outPath)}");
                    extracted++;
                    unresolved.Remove(theme);
                }
                catch (Exception ex) { Console.WriteLine($"  [CROP] {theme}: {ex.Message}"); missing++; }
            }
        }
        foreach (var img in decoded.Values) img?.Dispose();
    }

    /// <summary>
    /// Dumps every field of every Sprite asset in a bundle. Used to discover what
    /// metadata Unity exposes (m_Pivot, m_Border, m_RD.textureRect, m_PixelsToUnits, etc.)
    /// beyond just m_Rect that AssetExtractionService currently captures.
    /// </summary>
    private static int ProbeSpriteFields(string bundlePath, string tpkPath, string filter)
    {
        if (!File.Exists(bundlePath)) { Console.Error.WriteLine($"Not found: {bundlePath}"); return 2; }
        if (!File.Exists(tpkPath)) { Console.Error.WriteLine($"TPK not found: {tpkPath}"); return 2; }

        var am = new AssetsManager();
        am.LoadClassPackage(tpkPath);

        var bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
        Console.WriteLine($"Bundle: {Path.GetFileName(bundlePath)}");
        if (!string.IsNullOrEmpty(filter))
            Console.WriteLine($"Filter: substring match on '{filter}'");

        int spritesFound = 0;
        int spritesShown = 0;
        foreach (var afInst in am.LoadAllAssetsFromBundle(bunInst))
        {
            try { am.LoadClassDatabaseFromPackage(afInst.file.Metadata.UnityVersion); } catch { }

            var spriteInfos = afInst.file.GetAssetsOfType(AssetClassID.Sprite);
            if (spriteInfos.Count == 0) continue;

            foreach (var si in spriteInfos)
            {
                AssetTypeValueField? bf = null;
                try { bf = am.GetBaseField(afInst, si); } catch { continue; }
                if (bf == null) continue;

                string name = bf["m_Name"].AsString ?? "";
                spritesFound++;
                if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                spritesShown++;
                Console.WriteLine($"\n========== Sprite: {name} ==========");
                DumpField(bf, indent: 0, maxDepth: 6);
            }
        }
        Console.WriteLine($"\nTotal sprites in bundle: {spritesFound}, shown: {spritesShown}");
        return 0;
    }

    /// <summary>Recursively prints a Unity asset field tree with values. Used by --probe-sprite-fields.</summary>
    private static void DumpField(AssetTypeValueField field, int indent, int maxDepth)
    {
        if (indent > maxDepth) return;
        string pad = new string(' ', indent * 2);
        string typeName = field.TypeName ?? "";
        string fieldName = field.FieldName ?? "(root)";

        // Leaf — print value if it's a primitive
        var children = field.Children;
        if (children.Count == 0)
        {
            string val;
            try
            {
                val = typeName switch
                {
                    "int"     => field.AsInt.ToString(),
                    "UInt32"  or "unsigned int" => field.AsUInt.ToString(),
                    "SInt64"  or "long" => field.AsLong.ToString(),
                    "UInt64"  or "unsigned long" => field.AsULong.ToString(),
                    "float"   => field.AsFloat.ToString("0.######"),
                    "bool"    => field.AsBool.ToString(),
                    "string"  => "\"" + (field.AsString ?? "") + "\"",
                    _ => $"<{typeName}>"
                };
            }
            catch { val = $"<{typeName} unreadable>"; }
            Console.WriteLine($"{pad}{fieldName}: {typeName} = {val}");
            return;
        }

        // Container — print header and recurse. Skip large arrays (vertex data, index buffer).
        if (fieldName.Contains("VertexData", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("IndexBuffer", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("PhysicsShape", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("Bones", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("SubMeshes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{pad}{fieldName}: {typeName} <skipped — large/binary>");
            return;
        }

        Console.WriteLine($"{pad}{fieldName}: {typeName}");
        foreach (var ch in children)
            DumpField(ch, indent + 1, maxDepth);
    }

    /// <summary>
    /// Dumps SpriteAtlas asset render data map entries — these are the post-packing
    /// position rect + per-sprite textureRect inside the atlas page. This is the
    /// authoritative source for atlas position; image_atlas_data.json currently
    /// only stores atlas position but loses textureRectOffset and uvTransform.
    /// </summary>
    private static int ProbeAtlasFields(string bundlePath, string tpkPath, string filter)
    {
        if (!File.Exists(bundlePath)) { Console.Error.WriteLine($"Not found: {bundlePath}"); return 2; }
        if (!File.Exists(tpkPath)) { Console.Error.WriteLine($"TPK not found: {tpkPath}"); return 2; }

        var am = new AssetsManager();
        am.LoadClassPackage(tpkPath);

        var bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
        Console.WriteLine($"Bundle: {Path.GetFileName(bundlePath)}");

        foreach (var afInst in am.LoadAllAssetsFromBundle(bunInst))
        {
            try { am.LoadClassDatabaseFromPackage(afInst.file.Metadata.UnityVersion); } catch { }

            var atlasInfos = afInst.file.GetAssetsOfType(AssetClassID.SpriteAtlas);
            if (atlasInfos.Count == 0) continue;

            foreach (var ai in atlasInfos)
            {
                AssetTypeValueField? bf = null;
                try { bf = am.GetBaseField(afInst, ai); } catch { continue; }
                if (bf == null) continue;

                string atlasName = bf["m_Name"].AsString ?? "";
                Console.WriteLine($"\n========== SpriteAtlas: {atlasName} ==========");

                // Top-level fields — show non-array ones
                foreach (var ch in bf.Children)
                {
                    string fname = ch.FieldName ?? "";
                    if (fname == "m_PackedSprites" || fname == "m_PackedSpriteNamesToIndex"
                        || fname == "m_RenderDataMap" || fname == "m_MasterAtlas") continue;
                    DumpField(ch, indent: 1, maxDepth: 3);
                }

                // Packed sprite names list
                var namesField = bf["m_PackedSpriteNamesToIndex.Array"];
                var spriteNames = new List<string>();
                if (!namesField.IsDummy)
                    foreach (var n in namesField.Children)
                        spriteNames.Add(n.AsString);
                Console.WriteLine($"  Packed sprite names ({spriteNames.Count}): {string.Join(", ", spriteNames)}");

                // RenderDataMap entries — the meat
                var mapField = bf["m_RenderDataMap.Array"];
                if (mapField.IsDummy)
                {
                    Console.WriteLine("  m_RenderDataMap is empty");
                    continue;
                }
                Console.WriteLine($"  m_RenderDataMap entries: {mapField.Children.Count}");

                int idx = 0;
                foreach (var entry in mapField.Children)
                {
                    string spriteName = idx < spriteNames.Count ? spriteNames[idx] : $"<idx{idx}>";
                    if (!string.IsNullOrEmpty(filter) && !spriteName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        idx++;
                        continue;
                    }
                    Console.WriteLine($"\n  ── Entry [{idx}] sprite: {spriteName} ──");
                    // Entry is a pair: first = key (GUID+pathId), second = SpriteAtlasData
                    DumpField(entry, indent: 2, maxDepth: 6);
                    idx++;
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// --probe-pool &lt;startup_scenes_all.bundle&gt; &lt;classdata.tpk&gt; [maxEntries]
    /// Dumps the field tree of the PoolConfig MonoBehaviour's first "pools" entries (names, type
    /// names, string values) — use when the app logs "Extracted 0 pool entries" after a game
    /// update to see which field got renamed (itemTag / prefabRef).
    /// </summary>
    private static int ProbePool(string bundlePath, string tpkPath, int maxEntries)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpkPath);
        var bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
        var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
        for (int fi = 0; fi < dirInfos.Count; fi++)
        {
            AssetsFileInstance? afileInst;
            try { afileInst = am.LoadAssetsFileFromBundle(bunInst, fi); } catch { continue; }
            if (afileInst?.file == null) continue;
            am.LoadClassDatabaseFromPackage(afileInst.file.Metadata.UnityVersion);
            foreach (var monoInfo in afileInst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                AssetTypeValueField? bf;
                try { bf = am.GetBaseField(afileInst, monoInfo); } catch { continue; }
                if (bf == null || bf.IsDummy) continue;
                var pools = bf["pools"];
                if (pools.IsDummy) continue;
                var arr = pools["Array"];
                if (arr.IsDummy || arr.Children.Count == 0) continue;
                Console.WriteLine($"MonoBehaviour '{bf["m_Name"]?.AsString}' pools={arr.Children.Count} (Unity {afileInst.file.Metadata.UnityVersion})");
                Console.WriteLine("Top-level fields: " + string.Join(", ", bf.Children.Select(c => $"{c.FieldName}:{c.TypeName}")));
                for (int i = 0; i < Math.Min(maxEntries, arr.Children.Count); i++)
                {
                    Console.WriteLine($"--- pools[{i}]");
                    DumpField(arr.Children[i], 1, 4);
                }
                return 0;
            }
        }
        Console.WriteLine("No MonoBehaviour with a 'pools' array found.");
        return 1;

        static void DumpField(AssetTypeValueField f, int depth, int maxDepth)
        {
            var indent = new string(' ', depth * 2);
            string val = "";
            try
            {
                if (f.TypeName == "string") val = $" = \"{f.AsString}\"";
                else if (f.Value != null && f.Children.Count == 0) val = $" = {f.AsString}";
            }
            catch { }
            Console.WriteLine($"{indent}{f.FieldName}:{f.TypeName}{val}");
            if (depth >= maxDepth) return;
            foreach (var c in f.Children.Take(12)) DumpField(c, depth + 1, maxDepth);
        }
    }

    private static int ProbeOneBundle(string bundlePath)
    {
        if (!File.Exists(bundlePath)) { Console.Error.WriteLine($"Not found: {bundlePath}"); return 2; }
        var am = new AssetsManager();
        var bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
        Console.WriteLine($"Bundle: {Path.GetFileName(bundlePath)}");
        foreach (var afInst in am.LoadAllAssetsFromBundle(bunInst))
        {
            foreach (AssetClassID cid in new[] {
                AssetClassID.Texture2D, AssetClassID.Sprite, AssetClassID.SpriteAtlas,
                AssetClassID.MonoBehaviour, AssetClassID.GameObject })
            {
                var assets = afInst.file.GetAssetsOfType(cid);
                if (assets.Count == 0) continue;
                Console.WriteLine($"-- {cid} ({assets.Count}) --");
                foreach (var ai in assets)
                {
                    string nm = "";
                    try { nm = am.GetBaseField(afInst, ai)?["m_Name"]?.AsString ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(nm)) Console.WriteLine($"  {nm}");
                }
            }
        }
        return 0;
    }

    private static System.Collections.Generic.IEnumerable<AssetsTools.NET.Extra.AssetsFileInstance> LoadAllAssetsFromBundle_Compat(
        AssetsTools.NET.Extra.AssetsManager am, AssetsTools.NET.Extra.BundleFileInstance bunInst)
    {
        for (int i = 0; i < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            AssetsTools.NET.Extra.AssetsFileInstance fi = null;
            try { fi = am.LoadAssetsFileFromBundle(bunInst, i, loadDeps: false); } catch { }
            if (fi != null) yield return fi;
        }
    }

    // Probe HotspotDefinition — print description-resolution fields for hotspots whose Id
    // contains the substring. Diagnoses tasks with missing Description in areas.json
    // (e.g. renamed multistep members like FirstFloorHallwayReadingNookFixRightChair).
    private static int ProbeHotspot(string configPath, string languagePath, string idSubstring)
    {
        Console.WriteLine($"=== ProbeHotspot ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Language: {languagePath}");
        Console.WriteLine($"Filter:   {idSubstring}");

        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var patchedArchive = PatchedConfigArchive.WithNoPatches(archive);
            var gameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
            ClientGlobal.SharedGameConfig = gameConfig;

            int count = 0;
            foreach (var kv in gameConfig.HotspotDefinitions.EnumerateAll())
            {
                var def = (GameLogic.Hotspots.HotspotDefinition)kv.Value;
                var id = def.Id.ToString();
                if (!id.Contains(idSubstring, StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                Console.WriteLine($"--- {id}");
                Console.WriteLine($"    DescriptionLocalizationId: '{def.DescriptionLocalizationId}'");
                Console.WriteLine($"    MultistepGroupId:          '{def.MultistepGroupId}'");
                // MetaRef state per member: whether the wrapper itself is null, whether its key is
                // null / renders empty, and whether it resolves. areas.json writes some of these as
                // JSON null and omits others, and only these three flags tell them apart.
                foreach (var (memberName, _, get) in MergeMansionWikiTools.Dumper.Core.MetaObjectWriter.Members(def))
                {
                    object value;
                    try { value = get(); } catch (Exception ex) { Console.WriteLine($"    ref {memberName}: THREW {ex.GetType().Name}"); continue; }
                    if (value is not Metaplay.Core.IMetaRef metaRef) continue;
                    var key = metaRef.KeyObject;
                    Console.WriteLine($"    ref {memberName}: keyNull={key is null} keyText='{key?.ToString() ?? "<null>"}' resolved={metaRef.IsResolved}");
                }
                string prefixed = $"HotspotDescription_{id}";
                Console.WriteLine($"    key '{prefixed}': {(LocMan.HasString(prefixed) ? "'" + LocMan.Get(prefixed) + "'" : "MISSING")}");
                Console.WriteLine($"    key '{id}' (bare): {(LocMan.HasString(id) ? "'" + LocMan.Get(id) + "'" : "MISSING")}");
                var dli = def.DescriptionLocalizationId;
                if (!string.IsNullOrEmpty(dli))
                {
                    Console.WriteLine($"    key '{dli}' (DLI bare): {(LocMan.HasString(dli) ? "'" + LocMan.Get(dli) + "'" : "MISSING")}");
                    var dliPrefixed = $"HotspotDescription_{dli}";
                    Console.WriteLine($"    key '{dliPrefixed}' (DLI prefixed): {(LocMan.HasString(dliPrefixed) ? "'" + LocMan.Get(dliPrefixed) + "'" : "MISSING")}");
                }
            }
            Console.WriteLine($"Matched hotspots: {count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // Probe PlayerLevels config library — prints MaxPlayerLevel and per-level
    // NextLevelExperience (+ cumulative) with a reward summary. Source of truth
    // for player level cap / XP curve (wiki only documents up to L50).
    private static int ProbePlayerLevels(string configPath, string languagePath)
    {
        Console.WriteLine("=== ProbePlayerLevels ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Language: {languagePath}");

        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var patchedArchive = PatchedConfigArchive.WithNoPatches(archive);
            var gameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
            ClientGlobal.SharedGameConfig = gameConfig;

            Console.WriteLine($"MaxPlayerLevel: {gameConfig.MaxPlayerLevel}");

            long cumulative = 0;
            var levels = gameConfig.PlayerLevels.EnumerateAll()
                .Select(kv => (global::Player.PlayerLevelData)kv.Value)
                .OrderBy(l => l.Level)
                .ToList();
            Console.WriteLine($"PlayerLevels entries: {levels.Count}");
            foreach (var lv in levels)
            {
                cumulative += lv.NextLevelExperience;
                string rewards = lv.Rewards == null || lv.Rewards.Count == 0
                    ? "-"
                    : string.Join(", ", lv.Rewards.Select(DescribeReward));
                Console.WriteLine($"L{lv.Level,3}: nextXP={lv.NextLevelExperience,8} cum={cumulative,10} rewards: {rewards}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // Probe EnergyModes config library — Supercharge/Hypercharge definitions
    // (EnergyConsumptionMultiplier drives aux-attachment chance scaling).
    private static int ProbeEnergyModes(string configPath, string languagePath)
    {
        Console.WriteLine("=== ProbeEnergyModes ===");
        Console.WriteLine($"Config:   {configPath}");

        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var patchedArchive = PatchedConfigArchive.WithNoPatches(archive);
            var gameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
            ClientGlobal.SharedGameConfig = gameConfig;

            foreach (var kv in gameConfig.EnergyModes.EnumerateAll())
            {
                var m = (GameLogic.Player.Modes.EnergyModeInfo)kv.Value;
                Console.WriteLine($"{m.ConfigKey,-20} EnergyMult={m.EnergyConsumptionMultiplier} CapacityMult={m.CapacityConsumptionMultiplier} LevelUpChance={m.LevelUpChance} LevelUpCount={m.LevelUpCount} NameLocId={m.NameLocId}");
            }
            Console.WriteLine("--- EnergySettings (per EnergyType) ---");
            foreach (var kv in gameConfig.EnergySettings.EnumerateAll())
            {
                var t = kv.Value.GetType();
                var parts = new List<string>();
                foreach (var prop in t.GetProperties())
                {
                    object val = null;
                    try { val = prop.GetValue(kv.Value); } catch { }
                    if (val != null) parts.Add($"{prop.Name}={val}");
                }
                Console.WriteLine($"{kv.Key}: {string.Join(", ", parts)}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // Probe Boards config library — per-board sell price override (BoardInfo.ItemSellCost).
    // Verifies the "everything sells for 1 coin on event boards" rule from game config.
    private static int ProbeBoard(string configPath, string languagePath, string idSubstring)
    {
        Console.WriteLine("=== ProbeBoard ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Filter:   '{idSubstring}'");

        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var patchedArchive = PatchedConfigArchive.WithNoPatches(archive);
            var gameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
            ClientGlobal.SharedGameConfig = gameConfig;

            int count = 0;
            foreach (var kv in gameConfig.Boards.EnumerateAll())
            {
                var board = (Code.GameLogic.GameEvents.BoardInfo)kv.Value;
                var id = board.BoardId.ToString();
                if (!string.IsNullOrEmpty(idSubstring) && !id.Contains(idSubstring, StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
                string sellCost = "null (fallback ItemSellPrices per level)";
                if (board.ItemSellCost != null)
                {
                    var t = board.ItemSellCost.GetType();
                    var parts = new List<string>();
                    foreach (var prop in t.GetProperties())
                    {
                        object val = null;
                        try { val = prop.GetValue(board.ItemSellCost); } catch { }
                        if (val != null) parts.Add($"{prop.Name}={val}");
                    }
                    sellCost = $"{t.Name}({string.Join(",", parts)})";
                }
                Console.WriteLine($"{id,-50} {board.Width}x{board.Height,-3} Energy={board.EnergyType,-12} ItemSellCost={sellCost}");
            }
            Console.WriteLine($"Matched boards: {count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Resolves prefab → Spine skeleton the way the game itself does: the prefab's SkeletonGraphic
    /// (or SkeletonAnimation) component points at a SkeletonDataAsset, which points at the skeleton
    /// TextAsset whose NAME is what skinMappings are keyed by.
    ///
    /// This is the link that name-matching cannot recover: ItemPlayerLevelChest's skeleton is
    /// called RewardChests.
    /// </summary>
    private static int ProbePrefabSkeleton(string bundlePath, string tpkPath, string prefabFilter)
    {
        Console.WriteLine("=== ProbePrefabSkeleton ===");
        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(tpkPath);
            var bun = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            var dirInfos = bun.file.BlockAndDirInfo.DirectoryInfos;

            for (int fi = 0; fi < dirInfos.Count; fi++)
            {
                AssetsFileInstance? inst;
                try { inst = am.LoadAssetsFileFromBundle(bun, fi); } catch { continue; }
                if (inst?.file == null) continue;
                am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);

                foreach (var goInfo in inst.file.GetAssetsOfType(AssetClassID.GameObject))
                {
                    AssetTypeValueField go;
                    try { go = am.GetBaseField(inst, goInfo); } catch { continue; }
                    var goName = go["m_Name"]?.AsString ?? "";
                    if (string.IsNullOrEmpty(goName)) continue;
                    if (!string.IsNullOrEmpty(prefabFilter)
                        && !goName.Contains(prefabFilter, StringComparison.OrdinalIgnoreCase)) continue;

                    var comps = go["m_Component.Array"];
                    if (comps.IsDummy) continue;

                    foreach (var c in comps.Children)
                    {
                        // each entry is a PPtr to a Component
                        var ptr = c["component"].IsDummy ? c : c["component"];
                        AssetTypeValueField compBase;
                        try
                        {
                            var ext = am.GetExtAsset(inst, ptr);
                            if (ext.baseField == null) continue;
                            compBase = ext.baseField;
                        }
                        catch { continue; }

                        var sda = compBase["skeletonDataAsset"];
                        if (sda.IsDummy) continue;

                        try
                        {
                            var sdaExt = am.GetExtAsset(inst, sda);
                            if (sdaExt.baseField == null) continue;
                            var json = sdaExt.baseField["skeletonJSON"];
                            var sdaName = sdaExt.baseField["m_Name"]?.AsString ?? "?";
                            string skelName = "?";
                            if (!json.IsDummy)
                            {
                                var jsonExt = am.GetExtAsset(sdaExt.file, json);
                                skelName = jsonExt.baseField?["m_Name"]?.AsString ?? "?";
                            }
                            Console.WriteLine($"{goName}  →  SkeletonDataAsset '{sdaName}'  →  skeleton '{skelName}'");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{goName}: skeletonDataAsset unreadable ({ex.GetType().Name})");
                        }
                    }
                }
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"FAILED {ex.GetType().Name}: {ex.Message}"); return 1; }
        finally { try { am.UnloadAll(); } catch { } }
    }

    /// <summary>
    /// For each bundle: the prefabs it ships (from the AssetBundle m_Container paths) and the
    /// Spine skeleton TextAssets inside it.
    ///
    /// Why: poolTagMapping resolves a PoolTag to a PREFAB name, while skinMappings are keyed by
    /// the SKELETON name, and for 37 pool tags those differ (PlayerLevelChestItem →
    /// ItemPlayerLevelChest, but the skeleton is RewardChests). This shows whether "prefab and
    /// skeleton live in the same bundle" is a sound basis for aliasing the two.
    /// </summary>
    private static int ProbeBundleSkeletons(string bundleDir, string tpkPath, string filter, string needle = "")
    {
        Console.WriteLine("=== ProbeBundleSkeletons ===");
        var files = Directory.GetFiles(bundleDir, "*.bundle")
            .Where(f => string.IsNullOrEmpty(filter)
                || Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f).ToList();
        Console.WriteLine($"{files.Count} bundle(s) matching '{filter}'\n");

        foreach (var path in files)
        {
            var am = new AssetsManager();
            try
            {
                am.LoadClassPackage(tpkPath);
                var bun = am.LoadBundleFile(path, unpackIfPacked: true);
                var containers = new List<string>();
                var gameObjects = new List<string>();
                var skeletons = new List<string>();

                var dirInfos = bun.file.BlockAndDirInfo.DirectoryInfos;
                for (int fi = 0; fi < dirInfos.Count; fi++)
                {
                    AssetsFileInstance? inst;
                    try { inst = am.LoadAssetsFileFromBundle(bun, fi); } catch { continue; }
                    if (inst?.file == null) continue;
                    am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);

                    // what the bundle advertises under addressable keys
                    foreach (var abInfo in inst.file.GetAssetsOfType(AssetClassID.AssetBundle))
                    {
                        AssetTypeValueField ab;
                        try { ab = am.GetBaseField(inst, abInfo); } catch { continue; }
                        var container = ab["m_Container.Array"];
                        if (container.IsDummy) continue;
                        foreach (var pair in container.Children)
                        {
                            var cpath = pair[0].AsString ?? "";
                            if (!string.IsNullOrEmpty(cpath)) containers.Add(cpath);
                        }
                    }

                    // the prefab roots themselves
                    foreach (var goInfo in inst.file.GetAssetsOfType(AssetClassID.GameObject))
                    {
                        AssetTypeValueField go;
                        try { go = am.GetBaseField(inst, goInfo); } catch { continue; }
                        var n = go["m_Name"]?.AsString;
                        if (!string.IsNullOrEmpty(n)) gameObjects.Add(n);
                    }

                    // skeletons: Spine skeleton JSON text assets
                    foreach (var taInfo in inst.file.GetAssetsOfType(AssetClassID.TextAsset))
                    {
                        AssetTypeValueField ta;
                        try { ta = am.GetBaseField(inst, taInfo); } catch { continue; }
                        var name = ta["m_Name"]?.AsString ?? "";
                        var script = ta["m_Script"]?.AsString ?? "";
                        if (script.Contains("\"skins\"") || script.Contains("\"bones\""))
                            skeletons.Add(name);
                    }
                }
                // With a needle: report only bundles that mention it, and always show what
                // skeletons sit next to it — that is the prefab→skeleton link we are after.
                if (!string.IsNullOrEmpty(needle))
                {
                    bool Match(IEnumerable<string> xs) =>
                        xs.Any(x => x.Contains(needle, StringComparison.OrdinalIgnoreCase));
                    if (!Match(containers) && !Match(gameObjects) && !Match(skeletons)) continue;
                    Console.WriteLine($"{Path.GetFileName(path)}   *** contains '{needle}' ***");
                    foreach (var c in containers.Distinct().Where(c => c.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"   container   {c}");
                    foreach (var g in gameObjects.Distinct().Where(g => g.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"   gameObject  {g}");
                    Console.WriteLine($"   skeletons in this bundle ({skeletons.Distinct().Count()}): "
                        + string.Join(", ", skeletons.Distinct().Take(12)));
                    Console.Out.Flush();
                    continue;
                }

                Console.WriteLine($"{Path.GetFileName(path)}");
                Console.WriteLine($"   containers ({containers.Distinct().Count()}): {string.Join(", ", containers.Distinct().Take(10))}");
                Console.WriteLine($"   gameObjects ({gameObjects.Distinct().Count()}): {string.Join(", ", gameObjects.Distinct().Take(10))}");
                Console.WriteLine($"   skeletons ({skeletons.Distinct().Count()}): {string.Join(", ", skeletons.Distinct().Take(12))}");
                Console.Out.Flush();
            }
            catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(path)}: FAILED {ex.GetType().Name}: {ex.Message}"); }
            finally { try { am.UnloadAll(); } catch { } }
        }
        return 0;
    }

    /// <summary>
    /// Dumps the [MetaMember] state of config entries exactly as the native dumpers observe it:
    /// TagId, visibility, runtime type, and MetaRef resolution/key — including the private and
    /// base-class members --dump-config-library cannot see, since it reads public properties only.
    /// See --probe-meta-members.
    /// </summary>
    private static int ProbeMetaMembers(string configPath, string languagePath, string libraryName, string keyFilter)
    {
        Console.WriteLine($"=== ProbeMetaMembers: {libraryName} (filter '{keyFilter}') ===");
        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(
                    ContentHash.ParseString(Path.GetFileName(languagePath)), File.ReadAllBytes(languagePath));
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var cfg = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
            ClientGlobal.SharedGameConfig = cfg;

            var prop = cfg.GetType().GetProperty(libraryName);
            if (prop == null) { Console.WriteLine($"No such library: {libraryName}"); return 1; }
            var lib = prop.GetValue(cfg);
            var entries = (System.Collections.IEnumerable)lib!.GetType().GetMethod("EnumerateAll")!.Invoke(lib, null)!;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly;

            foreach (var kv in entries)
            {
                var kvt = kv.GetType();
                var key = kvt.GetProperty("Key")?.GetValue(kv)?.ToString() ?? "?";
                if (!string.IsNullOrEmpty(keyFilter) && !key.Contains(keyFilter, StringComparison.OrdinalIgnoreCase)) continue;
                var val = kvt.GetProperty("Value")?.GetValue(kv);
                if (val == null) continue;
                Console.WriteLine();
                Console.WriteLine($"[{key}] ({val.GetType().Name})");
                for (var t = val.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (var m in t.GetFields(flags).Cast<System.Reflection.MemberInfo>().Concat(t.GetProperties(flags)))
                    {
                        var a = m.GetCustomAttributes(typeof(Metaplay.Core.Model.MetaMemberAttribute), false)
                            .Cast<Metaplay.Core.Model.MetaMemberAttribute>().FirstOrDefault();
                        if (a == null) continue;
                        object? v = null;
                        var isPublic = m is System.Reflection.FieldInfo fi0
                            ? fi0.IsPublic
                            : ((System.Reflection.PropertyInfo)m).GetMethod?.IsPublic == true;
                        try { v = m is System.Reflection.FieldInfo fi ? fi.GetValue(val) : ((System.Reflection.PropertyInfo)m).GetValue(val); }
                        catch (Exception ex) { Console.WriteLine($"   {a.TagId,3} {m.Name,-38} THROWS {ex.GetType().Name}"); continue; }
                        var extra = v is Metaplay.Core.IMetaRef mr
                            ? $"  ref resolved={mr.IsResolved} keyNull={mr.KeyObject == null} keyText='{mr.KeyObject?.ToString() ?? "<null>"}'"
                            : "";
                        Console.WriteLine($"   {a.TagId,3} {(isPublic ? "pub " : "priv")} {m.Name,-38} {(v == null ? "<C# null>" : v.GetType().Name)}{extra}");
                    }
                }
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }

    /// <summary>
    /// Prints the entries of one GameConfigLibrary — keys, and for a filtered key the whole entry
    /// expanded one level deep. Companion to --find-config-refs: that one answers "who references
    /// X", this one answers "what does X actually say".
    /// </summary>
    private static int DumpConfigLibrary(string configPath, string languagePath, string libraryName,
        string keyFilter, string patchPath = "", string branchFilter = "")
    {
        Console.WriteLine($"=== DumpConfigLibrary: {libraryName} (filter '{keyFilter}') ===");
        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var baseConfig = (SharedGameConfig)GameConfigFactory.Instance
                .ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
            var gameConfig = baseConfig;
            ClientGlobal.SharedGameConfig = gameConfig;

            if (!string.IsNullOrEmpty(patchPath))
            {
                var files = Directory.Exists(patchPath)
                    ? Directory.GetFiles(patchPath).OrderBy(File.GetLastWriteTimeUtc).ToList()
                    : new List<string> { patchPath };
                foreach (var pf in files)
                {
                    var sp = Metaplay.Core.Config.GameConfigSpecializationPatches.FromBytes(File.ReadAllBytes(pf));
                    var done = false;
                    foreach (var (expId, vs) in sp.Patches)
                    {
                        foreach (var (varId, bytes) in vs)
                        {
                            var label = $"{expId}_{varId}";
                            if (!label.Contains(branchFilter, StringComparison.OrdinalIgnoreCase)) continue;
                            var env = Metaplay.Core.Config.GameConfigPatchEnvelope.Deserialize(bytes ?? Array.Empty<byte>());
                            Console.WriteLine($"Branch: {label} (entries: {string.Join(",", env.EntryNames)})");
                            gameConfig = SharedGameConfig.ImportPatchedFrom(baseConfig,
                                new PatchedConfigArchive(archive, new[] { env }), env.EntryNames.ToArray());
                            ClientGlobal.SharedGameConfig = gameConfig;
                            done = true; break;
                        }
                        if (done) break;
                    }
                    if (done) break;
                }
            }

            var prop = gameConfig.GetType().GetProperties()
                .FirstOrDefault(p => string.Equals(p.Name, libraryName, StringComparison.OrdinalIgnoreCase));
            if (prop == null)
            {
                Console.WriteLine("Library not found. Available:");
                foreach (var p in gameConfig.GetType().GetProperties())
                    if (p.PropertyType.GetMethod("EnumerateAll") != null) Console.WriteLine("   " + p.Name);
                return 1;
            }

            var lib = prop.GetValue(gameConfig);
            var entries = (System.Collections.IEnumerable)lib!.GetType().GetMethod("EnumerateAll")!.Invoke(lib, null)!;
            int n = 0;
            foreach (var kv in entries)
            {
                var kvType = kv.GetType();
                var key = kvType.GetProperty("Key")?.GetValue(kv)?.ToString() ?? "?";
                var val = kvType.GetProperty("Value")?.GetValue(kv);
                n++;
                if (!string.IsNullOrEmpty(keyFilter)
                    && !key.Contains(keyFilter, StringComparison.OrdinalIgnoreCase)) continue;

                Console.WriteLine($"\n[{key}]  ({val?.GetType().Name})");
                if (val == null) continue;
                foreach (var p in val.GetType().GetProperties())
                {
                    object? v;
                    try { v = p.GetValue(val); } catch { continue; }
                    if (v == null) { Console.WriteLine($"    {p.Name,-34} = (null)"); continue; }
                    string text;
                    if (v is System.Collections.IEnumerable seq && v is not string)
                    {
                        var parts = new List<string>();
                        foreach (var item in seq) parts.Add(DescribeReward(item));
                        text = parts.Count == 0 ? "[]" : "[" + string.Join(", ", parts) + "]";
                    }
                    else text = v.ToString() ?? "";
                    if (text.Length > 400) text = text[..400] + "…";
                    Console.WriteLine($"    {p.Name,-34} = {text}");
                }
            }
            Console.WriteLine($"\n({n} entries in {libraryName})");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }

    /// <summary>
    /// Walks EVERY GameConfigLibrary in SharedGameConfig and reports each place a needle string
    /// occurs — as an ItemType/ConfigKey, a MetaRef key, or any plain string field.
    ///
    /// Why this exists: when an item shows up in the game (or in the Album) but appears in none of
    /// the JSON dumps, the question "which config actually references it?" cannot be answered from
    /// the dumps — they only cover the libraries the dumpers were written for. This walks the
    /// imported config itself, so a hit proves the reference exists and a clean run proves it does
    /// not (rather than proving only that no dumper looked).
    ///
    /// Traversal is reflection over public properties + IEnumerable, with a reference-identity
    /// visited set (the config graph is cyclic through MetaRefs) and a depth cap.
    /// </summary>
    private static int FindConfigRefs(string configPath, string languagePath, string needle,
        string patchPath = "", string branchFilter = "")
    {
        Console.WriteLine("=== FindConfigRefs ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Needle:   {needle}");

        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var baseConfig = (SharedGameConfig)GameConfigFactory.Instance
                .ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
            var gameConfig = baseConfig;
            ClientGlobal.SharedGameConfig = gameConfig;

            // Optional A/B branch: rewards that only exist inside an experiment (IdealFTUEPhase1_B…)
            // are invisible in the baseline archive, so scanning without the patch proves nothing.
            if (!string.IsNullOrEmpty(patchPath))
            {
                var patchFiles = Directory.Exists(patchPath)
                    ? Directory.GetFiles(patchPath).OrderBy(File.GetLastWriteTimeUtc).ToList()
                    : new List<string> { patchPath };
                var picked = new List<(string Label, Metaplay.Core.Config.GameConfigPatchEnvelope Env)>();
                foreach (var pf in patchFiles)
                {
                    var sp = Metaplay.Core.Config.GameConfigSpecializationPatches.FromBytes(File.ReadAllBytes(pf));
                    foreach (var (expId, vs) in sp.Patches)
                        foreach (var (varId, bytes) in vs)
                        {
                            var label = $"{expId}_{varId}";
                            if (!string.IsNullOrEmpty(branchFilter)
                                && !label.Contains(branchFilter, StringComparison.OrdinalIgnoreCase)) continue;
                            if (picked.Any(x => x.Label == label)) continue;
                            picked.Add((label, Metaplay.Core.Config.GameConfigPatchEnvelope
                                .Deserialize(bytes ?? Array.Empty<byte>())));
                        }
                }
                if (picked.Count == 0) { Console.WriteLine($"No patch branch matching '{branchFilter}'"); return 1; }
                var (lbl, env) = picked[0];
                Console.WriteLine($"Branch:   {lbl}  (entries: {string.Join(",", env.EntryNames)})");
                gameConfig = SharedGameConfig.ImportPatchedFrom(baseConfig,
                    new PatchedConfigArchive(archive, new[] { env }), env.EntryNames.ToArray());
                ClientGlobal.SharedGameConfig = gameConfig;
            }

            int hits = 0, libs = 0;
            foreach (var prop in gameConfig.GetType().GetProperties())
            {
                object libValue;
                try { libValue = prop.GetValue(gameConfig); }
                catch { continue; }
                if (libValue == null) continue;

                var enumerateAll = libValue.GetType().GetMethod("EnumerateAll");
                if (enumerateAll == null) continue;          // not a GameConfigLibrary
                libs++;

                System.Collections.IEnumerable entries;
                try { entries = (System.Collections.IEnumerable)enumerateAll.Invoke(libValue, null); }
                catch { continue; }

                int libHits = 0, entryCount = 0;
                foreach (var kv in entries)
                {
                    var kvType = kv.GetType();
                    var key = kvType.GetProperty("Key")?.GetValue(kv);
                    var val = kvType.GetProperty("Value")?.GetValue(kv);
                    if (val == null) continue;
                    entryCount++;

                    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    foreach (var path in SearchObject(val, needle, "", visited, 0))
                    {
                        Console.WriteLine($"HIT  {prop.Name}[{key}]{path}");
                        hits++; libHits++;
                    }
                }
                Console.WriteLine($"  [{libs,3}] {prop.Name,-42} {entryCount,6} entries"
                    + (libHits > 0 ? $"  ← {libHits} hit(s)" : ""));
                Console.Out.Flush();
            }
            Console.WriteLine($"\nScanned {libs} libraries — {hits} hit(s) for '{needle}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Yields a path for every field under <paramref name="obj"/> whose text contains the needle.</summary>
    private static IEnumerable<string> SearchObject(object obj, string needle, string path,
        HashSet<object> visited, int depth)
    {
        if (obj == null || depth > 6) yield break;

        var type = obj.GetType();

        // Leaf: anything whose text form carries the needle. Covers strings, StringId keys,
        // ItemType enums and MetaRef keys alike — they all render the item name in ToString().
        if (type.IsPrimitive || obj is string || type.IsEnum || type.Namespace == "System")
        {
            var s = obj.ToString();
            if (!string.IsNullOrEmpty(s) && s.Contains(needle, StringComparison.OrdinalIgnoreCase))
                yield return $"{path} = {s}";
            yield break;
        }

        if (!visited.Add(obj)) yield break;

        // A MetaRef points at an entry of ANOTHER library. Compare its key and STOP — following it
        // would re-walk the whole (cyclic) config graph once per entry, which is what made the
        // first version of this scan never finish.
        if (obj is Metaplay.Core.IMetaRef mref)
        {
            var k = mref.KeyObject?.ToString();
            if (!string.IsNullOrEmpty(k) && k.Contains(needle, StringComparison.OrdinalIgnoreCase))
                yield return $"{path} -> {k}";
            yield break;
        }

        // ItemDef is a numeric handle; it only names the item once resolved against the config.
        if (obj is GameLogic.Config.ItemDef idef)
        {
            string? itemName = null;
            try { itemName = idef.GetDef(ClientGlobal.SharedGameConfig)?.ItemType?.ToString(); } catch { }
            if (!string.IsNullOrEmpty(itemName) && itemName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                yield return $"{path} -> {itemName}";
            yield break;
        }

        // Another library's entry reached BY REFERENCE — identify it and stop.
        // depth > 0 is essential: the entry we were asked to scan is itself IGameConfigData, and
        // stopping on it would mean never looking at its fields (PlayerLevelData.Rewards was
        // silently skipped this way, making "not referenced anywhere" a false negative).
        if (depth > 0 && obj is IGameConfigData)
        {
            foreach (var idProp in new[] { "ItemType", "ConfigKey", "Id", "Name" })
            {
                string? v = null;
                try { v = type.GetProperty(idProp)?.GetValue(obj)?.ToString(); } catch { }
                if (!string.IsNullOrEmpty(v) && v.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"{path} -> {v}";
                    break;
                }
            }
            yield break;
        }

        // StringId / ItemType-like config keys render their name straight from ToString()
        var text = obj.ToString();
        if (!string.IsNullOrEmpty(text) && text.Contains(needle, StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith(type.FullName ?? "\0"))
        {
            yield return $"{path} = {text}";
            yield break;
        }

        if (obj is System.Collections.IEnumerable seq && obj is not string)
        {
            int i = 0;
            foreach (var item in seq)
            {
                foreach (var r in SearchObject(item, needle, $"{path}[{i}]", visited, depth + 1))
                    yield return r;
                if (++i > 512) break;                        // runaway guard
            }
            yield break;
        }

        foreach (var p in type.GetProperties())
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object child;
            try { child = p.GetValue(obj); }
            catch { continue; }
            foreach (var r in SearchObject(child, needle, $"{path}.{p.Name}", visited, depth + 1))
                yield return r;
        }
    }

    private static string DescribeReward(object reward)
    {
        if (reward == null)
            return "null";
        var type = reward.GetType();
        var parts = new List<string>();
        foreach (var propName in new[] { "Amount", "ItemDef", "EnergyType", "Progress", "CardCollectionPackId", "CardId", "Level", "RequiredLevel", "AreaId", "TaskId", "Value", "HotspotId" })
        {
            var prop = type.GetProperty(propName);
            if (prop == null) continue;
            var val = prop.GetValue(reward);
            if (val == null) continue;
            if (val is GameLogic.Config.ItemDef idef)
            {
                string itemName = null;
                try { itemName = idef.GetDef(ClientGlobal.SharedGameConfig)?.ItemType?.ToString(); } catch { }
                parts.Add($"Item={itemName ?? val.ToString()}");
                continue;
            }
            parts.Add($"{propName}={val}");
        }
        // Metaplay config classes often expose values as public FIELDS (PlayerLevelRequirement.Min/Max),
        // which GetProperty misses entirely — that made requirements print as a bare type name.
        foreach (var f in type.GetFields())
        {
            object val;
            try { val = f.GetValue(reward); } catch { continue; }
            if (val == null) continue;
            parts.Add($"{f.Name}={val}");
        }
        return parts.Count > 0 ? $"{type.Name}({string.Join(",", parts)})" : type.Name;
    }

    // Probe LocMan — load language file and list all translation keys matching regex pattern.
    // Useful for diagnosing missing HotspotDescription_* keys after enum rebuild.
    /// <summary>
    /// Reads the HotspotId enum straight from global-metadata.dat (inside an APK/XAPK or a raw
    /// .dat) via Il2CppMetadataEnumReader — the same code path the app uses before every dump.
    /// Optional second arg: a HotspotId.cs (Cpp2IL or repo stub) to diff against.
    /// </summary>
    private static int ProbeHotspotMap(string source, string? enumCsPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[] bytes = source.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(source)
            : GameLogic.Il2Cpp.Il2CppMetadataEnumReader.ExtractGlobalMetadata(source);
        var version = GameLogic.Il2Cpp.Il2CppMetadataEnumReader.ReadVersion(bytes);
        var members = GameLogic.Il2Cpp.Il2CppMetadataEnumReader.ReadEnum(bytes, "HotspotId");
        Console.WriteLine($"metadata v{version}, {bytes.Length / 1024 / 1024} MB, HotspotId members: {members.Count} ({sw.ElapsedMilliseconds} ms)");
        Console.WriteLine($"  first: {members[0].Name} = {members[0].Value}");
        Console.WriteLine($"  last:  {members[^1].Name} = {members[^1].Value}");

        if (enumCsPath != null)
        {
            var rx = new System.Text.RegularExpressions.Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(-?\d+),?\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
            var stub = new Dictionary<string, int>();
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(enumCsPath)))
                stub[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
            var fresh = members.ToDictionary(m => m.Name, m => m.Value);
            var added = fresh.Keys.Where(k => !stub.ContainsKey(k)).ToList();
            var removed = stub.Keys.Where(k => !fresh.ContainsKey(k)).ToList();
            var renumbered = fresh.Where(kv => stub.TryGetValue(kv.Key, out var v) && v != kv.Value).ToList();
            Console.WriteLine($"diff vs {Path.GetFileName(enumCsPath)} ({stub.Count} members): +{added.Count} new, -{removed.Count} removed, {renumbered.Count} renumbered");
            foreach (var a in added.Take(10)) Console.WriteLine($"  + {a} = {fresh[a]}");
            foreach (var r in removed.Take(10)) Console.WriteLine($"  - {r}");
            foreach (var (k, v) in renumbered.Take(10)) Console.WriteLine($"  ~ {k}: {stub[k]} -> {v}");
        }
        return 0;
    }

    private static int ProbeLoc(string configPath, string languagePath, string pattern)
    {
        Console.WriteLine($"=== ProbeLoc ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Language: {languagePath}");
        Console.WriteLine($"Pattern:  {pattern}");

        try
        {
            MetaplayCore.Initialize();

            if (string.IsNullOrEmpty(languagePath) || !File.Exists(languagePath))
            {
                Console.Error.WriteLine($"Language file not found: {languagePath}");
                return 2;
            }
            var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
            MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));

            var rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var translations = MetaplaySDK.ActiveLanguage.Translations;
            Console.WriteLine($"Total translations in language: {translations.Count}");

            var matches = new System.Collections.Generic.List<(string Key, string Value)>();
            foreach (var kv in translations)
            {
                var key = kv.Key.Value;
                if (rx.IsMatch(key))
                    matches.Add((key, kv.Value));
            }

            matches.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
            Console.WriteLine($"Matches: {matches.Count}");
            foreach (var (k, v) in matches)
            {
                var snippet = v.Length > 80 ? v.Substring(0, 80) + "..." : v;
                Console.WriteLine($"  {k} = {snippet}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int DumpStories(string configPath, string outPath)
    {
        Console.WriteLine("=== DumpStories ===");
        Console.WriteLine($"Config: {configPath}");
        try
        {
            MetaplayCore.Initialize();
            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
            var cfg = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
            if (cfg.StoryElements == null)
            {
                Console.Error.WriteLine("StoryElements is null (import failed for this entry)");
                return 3;
            }
            var stories = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in cfg.StoryElements.EnumerateAll())
            {
                var story = (GameLogic.Story.StoryElementInfo)kv.Value;
                var ids = new List<string>();
                if (story.DialogItems != null)
                    foreach (var d in story.DialogItems)
                        ids.Add(d.Key.ToString());
                var next = new List<string>();
                foreach (var action in story.OnComplete ?? Enumerable.Empty<GameLogic.Player.Director.Config.IDirectorAction>())
                    if (action is GameLogic.Player.Director.Config.TriggerDialogue td && td.StoryDefinitionId != null)
                        next.Add(td.StoryDefinitionId.ToString());
                stories[story.ConfigKey.ToString()] = new { DialogItems = ids, Music = story.Music, StealAllSteps = story.StealAllSteps, CompleteStories = next };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            File.WriteAllText(outPath, Newtonsoft.Json.JsonConvert.SerializeObject(new { Stories = stories }, Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
            Console.WriteLine($"Stories: {stories.Count} -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int DumpLoc(string languagePath, string outPath)
    {
        Console.WriteLine("=== DumpLoc ===");
        Console.WriteLine($"Language: {languagePath}");
        try
        {
            MetaplayCore.Initialize();
            if (string.IsNullOrEmpty(languagePath) || !File.Exists(languagePath))
            {
                Console.Error.WriteLine($"Language file not found: {languagePath}");
                return 2;
            }
            // L-files are named by their ContentHash; an en.mpc pulled out of an APK is not — the hash
            // is only metadata for ImportBinary, so fall back to ContentHash.None.
            ContentHash langHash;
            try { langHash = ContentHash.ParseString(Path.GetFileName(languagePath)); }
            catch { langHash = ContentHash.None; }
            var lang = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in lang.Translations)
                dict[kv.Key.Value] = kv.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(dict, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(outPath, json, new System.Text.UTF8Encoding(false));
            Console.WriteLine($"Translations: {dict.Count} -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
    private static int DumpChain(string configPath, string languagePath, string outputDir)
    {
        Console.WriteLine("=== DumpHarness --dump-chain (chain_item_odds.json only) ===");
        Console.WriteLine($"Config:   {configPath}");
        Console.WriteLine($"Language: {languagePath}");
        Console.WriteLine($"Output:   {outputDir}");
        Directory.CreateDirectory(outputDir);

        try
        {
            MetaplayCore.Initialize();
            if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
            {
                var langHash = ContentHash.ParseString(Path.GetFileName(languagePath));
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
            }

            var archiveBytes = File.ReadAllBytes(configPath);
            var archive = ConfigArchive.FromBytes(archiveBytes);
            var patchedArchive = PatchedConfigArchive.WithNoPatches(archive);
            var gameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
            ClientGlobal.SharedGameConfig = gameConfig;

            var outPath = Path.Combine(outputDir, "chain_item_odds.json");
            Engine.DumpChains(gameConfig, outPath);
            Console.WriteLine($"-> {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DumpChain failed: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}

// Extension for AssetsManager if LoadAllAssetsFromBundle isn't public
internal static class AssetsManagerExt
{
    public static System.Collections.Generic.IEnumerable<AssetsTools.NET.Extra.AssetsFileInstance> LoadAllAssetsFromBundle(
        this AssetsTools.NET.Extra.AssetsManager am, AssetsTools.NET.Extra.BundleFileInstance bunInst)
    {
        for (int i = 0; i < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            AssetsTools.NET.Extra.AssetsFileInstance fi = null;
            try { fi = am.LoadAssetsFileFromBundle(bunInst, i, loadDeps: false); } catch { }
            if (fi != null) yield return fi;
        }
    }
}
