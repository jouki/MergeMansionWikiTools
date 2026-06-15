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
using merge_mansion_dumper.Dumper;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Localization;
using Metaplay.Unity;

namespace DumpHarness;

// Minimal console harness that runs only ExperimentalDumper and prints SpeedUpCostBehavior.
// Used to validate SharedGlobals serialization diagnostics without booting the WPF app.
internal static class Program
{
    public static int Main(string[] args)
    {
        // Sub-command: probe Unity bundles for minigame icon assets
        if (args.Length >= 2 && args[0] == "--probe-minigame-bundles")
        {
            return ProbeBundles(args[1]);
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
        if (args.Length >= 4 && args[0] == "--probe-hotspot")
        {
            // --probe-hotspot <configPath> <languagePath> <idSubstring>
            // Prints HotspotDefinition fields relevant for description resolution
            // (DescriptionLocalizationId, MultistepGroupId) + all loc key attempts.
            // Used to diagnose tasks with missing Description in areas.json.
            return ProbeHotspot(args[1], args[2], args[3]);
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
            var written = new ExperimentalDumper().WriteIndividualFiles(outputDir, gameConfig);
            foreach (var (section, path) in written)
                Console.WriteLine($"        -> {section}.json ({new FileInfo(path).Length / 1024} KB)");

            // Also dump areas.json so we can verify Theme field for IllustrationTask/CardStack hotspots.
            Console.WriteLine("[4b/4] Running AreaDumper...");
            var areasPath = Path.Combine(outputDir, "areas.json");
            new AreaDumper().WriteJson(areasPath, gameConfig);
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
            new EventDumper().WriteJson(basePath, masterConfig);
            Console.WriteLine($"        -> events.json ({new FileInfo(basePath).Length / 1024} KB)");

            if (!string.IsNullOrEmpty(patchPath))
            {
                // Support both file mode (legacy) and directory mode (union across all snapshots).
                var patchFiles = new List<string>();
                if (Directory.Exists(patchPath))
                    patchFiles.AddRange(Directory.GetFiles(patchPath).OrderBy(File.GetLastWriteTimeUtc));
                else if (File.Exists(patchPath))
                    patchFiles.Add(patchPath);

                Console.WriteLine($"[5] Loading {patchFiles.Count} patch file(s)...");
                // Equal-mtime dedup fix (mirror DumperService): fresh pull writes all snapshots with
                // the same mtime → "last write wins" is non-deterministic and can clobber the complete
                // patch with a stale stub. Keep the RICHEST payload (largest raw bytes) per label;
                // tiebreak newer mtime. See memory/dumper-equal-mtime-dedup-bug.md.
                var merged = new Dictionary<string, (Metaplay.Core.Player.PlayerExperimentId, Metaplay.Core.Player.ExperimentVariantId, Metaplay.Core.Config.GameConfigPatchEnvelope, int RawSize, DateTime Mtime)>(StringComparer.Ordinal);
                foreach (var pf in patchFiles)
                {
                    var mtime = File.GetLastWriteTimeUtc(pf);
                    var specPatches = Metaplay.Core.Config.GameConfigSpecializationPatches.FromBytes(File.ReadAllBytes(pf));
                    foreach (var (expId, vs) in specPatches.Patches)
                        foreach (var (varId, bytes) in vs)
                        {
                            var label = $"{expId}_{varId}";
                            int rawSize = bytes?.Length ?? 0;
                            if (merged.TryGetValue(label, out var prev))
                            {
                                bool replace = rawSize > prev.RawSize || (rawSize == prev.RawSize && mtime > prev.Mtime);
                                if (!replace) continue;
                                if (rawSize != prev.RawSize)
                                    Console.WriteLine($"        [dedup] {label}: richer payload {rawSize} B > {prev.RawSize} B wins");
                            }
                            merged[label] = (expId, varId, Metaplay.Core.Config.GameConfigPatchEnvelope.Deserialize(bytes), rawSize, mtime);
                        }
                }
                var configPatches = merged.Values.Select(v => (v.Item1, v.Item2, v.Item3)).ToArray();
                Console.WriteLine($"        Found {configPatches.Length} unique patches across {patchFiles.Count} file(s)");

                foreach (var (expId, varId, env) in configPatches)
                {
                    var label = $"{expId}_{varId}";
                    var entries = env.EntryNames.ToArray();
                    Console.WriteLine($"  Patch {label}: entries=[{string.Join(",", entries)}]");

                    var pa = new PatchedConfigArchive(archive, new[] { env });
                    try
                    {
                        var patchedConfig = SharedGameConfig.ImportPatchedFrom(masterConfig, pa, entries);
                        var subDir = Path.Combine(outputDir, label);
                        Directory.CreateDirectory(subDir);
                        var subPath = Path.Combine(subDir, "events.json");
                        new EventDumper().WriteJson(subPath, patchedConfig);
                        var size = new FileInfo(subPath).Length / 1024;
                        Console.WriteLine($"     -> {label}/events.json OK ({size} KB)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"     !! {label} FAILED: {ex.GetType().Name}: {ex.Message}");
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
                            var resolver = new merge_mansion_dumper.Dumper.Base.IgnoreDataMemberContractResolver();
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

    private static string DescribeReward(object reward)
    {
        if (reward == null)
            return "null";
        var type = reward.GetType();
        var parts = new List<string>();
        foreach (var propName in new[] { "Amount", "ItemDef", "EnergyType", "Progress" })
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
        return parts.Count > 0 ? $"{type.Name}({string.Join(",", parts)})" : type.Name;
    }

    // Probe LocMan — load language file and list all translation keys matching regex pattern.
    // Useful for diagnosing missing HotspotDescription_* keys after enum rebuild.
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
            new MergeChainDumper(dropsAsPercent: true).WriteJson(outPath, gameConfig);
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
