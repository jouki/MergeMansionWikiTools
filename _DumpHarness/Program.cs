using System;
using System.IO;
using System.Linq;
using GameLogic.Config;
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
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: DumpHarness <configPath> <languagePath> <outputDir>");
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
}
