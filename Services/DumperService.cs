using System.IO;
using System.Text;
using GameLogic.Config;
using merge_mansion_dumper.Dumper;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Localization;
using Metaplay.Core.Player;
using Metaplay.Unity;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// TextWriter that forwards each line to IProgress and collects all output.
/// </summary>
internal sealed class ProgressTextWriter : TextWriter
{
    private readonly IProgress<string>? _progress;
    private readonly List<string> _lines = new();
    private readonly StringBuilder _current = new();

    public override Encoding Encoding => Encoding.UTF8;
    public IReadOnlyList<string> Lines => _lines;

    public ProgressTextWriter(IProgress<string>? progress)
    {
        _progress = progress;
    }

    public override void Write(char value)
    {
        if (value == '\n')
            FlushLine();
        else if (value != '\r')
            _current.Append(value);
    }

    public override void WriteLine(string? value)
    {
        _current.Append(value);
        FlushLine();
    }

    private void FlushLine()
    {
        var line = _current.ToString().Trim();
        _current.Clear();
        if (string.IsNullOrEmpty(line)) return;

        _lines.Add(line);

        // Forward to UI — show [PROGRESS] as clean status, others as-is
        if (line.StartsWith("[PROGRESS]"))
            _progress?.Report(line[11..]); // strip "[PROGRESS] "
        else if (!line.StartsWith("[TRACE]")) // skip stack traces in UI
            _progress?.Report(line);
    }
}

internal static class DumperService
{
    public record DumpResult(
        string? ChainItemOddsPath,
        string? AreasPath,
        string? EventsPath,
        string? CardCollectionPath,
        string? ExperimentalPath,
        List<string> Warnings,
        List<string> Errors
    );

    public enum DumpMode { All, Chains, Areas, Events, CardCollection, Experimental }

    private static readonly object _initLock = new();
    private static bool _initialized;

    public static async Task<DumpResult> DumpAsync(
        string configPath,
        string? patchPath,
        string? languagePath,
        string outputDir,
        DumpMode mode = DumpMode.All,
        IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            string? chainPath = null, areasPath = null, eventsPath = null, cardCollectionPath = null, experimentalPath = null;

            void Log(string level, string msg, Exception? ex = null)
            {
                var line = $"[{level}] {msg}";
                if (level == "ERROR")
                {
                    errors.Add(line);
                    if (ex != null)
                        AppLogger.Error($"Dumper: {msg}", ex);
                    else
                        AppLogger.Error($"Dumper: {msg}");
                }
                else
                {
                    warnings.Add(line);
                    AppLogger.Warn($"Dumper: {msg}");
                }
                progress?.Report(line);

                // Log full stack trace for errors
                if (ex != null)
                {
                    var trace = $"[TRACE] {ex.GetType().FullName}: {ex.Message}";
                    if (ex.InnerException != null)
                        trace += $"\n  Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n  {ex.InnerException.StackTrace}";
                    else
                        trace += $"\n  {ex.StackTrace}";
                    AppLogger.Error($"Dumper trace: {trace}");
                    progress?.Report(trace);
                }
            }

            try
            {
                AppLogger.Info($"=== Dump started: mode={mode}, config={configPath} ===");

                // 1. Initialize MetaplayCore (once)
                progress?.Report("Initializing MetaplayCore...");
                EnsureInitialized();
                AppLogger.Info("MetaplayCore initialized");

                // 2. Load language file (optional)
                if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
                {
                    progress?.Report("Loading language file...");
                    try
                    {
                        var langFileName = Path.GetFileName(languagePath);
                        var langHash = ContentHash.ParseString(langFileName);
                        MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
                        progress?.Report($"Language loaded: {langFileName}");
                        AppLogger.Info($"Language loaded: {langFileName}");
                    }
                    catch (Exception ex)
                    {
                        Log("WARN", $"Language file skipped: {ex.Message}", ex);
                    }
                }

                // 3. Load config archive
                progress?.Report("Loading config archive...");
                var archiveBytes = File.ReadAllBytes(configPath);
                var archive = ConfigArchive.FromBytes(archiveBytes);
                progress?.Report($"Config archive loaded ({archive.Entries.Count} entries)");
                AppLogger.Info($"Config archive: {archive.Entries.Count} entries");

                // Log all archive entry names for diagnostics
                AppLogger.Info("Archive entries: " + string.Join(", ", archive.Entries.Select(e => e.Name)));

                // 4. Load patches (optional)
                PatchedConfigArchive patchedArchive;
                var patchedArchives = new List<(PlayerExperimentId, ExperimentVariantId, PatchedConfigArchive)>();

                if (!string.IsNullOrEmpty(patchPath) && File.Exists(patchPath))
                {
                    progress?.Report("Loading patch config...");
                    try
                    {
                        var specializationPatches = GameConfigSpecializationPatches.FromBytes(File.ReadAllBytes(patchPath));
                        var configPatches = specializationPatches.Patches
                            .SelectMany(y => y.Value.Select(z => (y.Key, z.Key, GameConfigPatchEnvelope.Deserialize(z.Value))))
                            .ToArray();

                        foreach (var configPatch in configPatches)
                        {
                            var pa = new PatchedConfigArchive(archive, new[] { configPatch.Item3 });
                            patchedArchives.Add((configPatch.Item1, configPatch.Item2, pa));
                        }

                        progress?.Report($"Loaded {configPatches.Length} patch(es)");
                        AppLogger.Info($"Patches loaded: {configPatches.Length}");
                    }
                    catch (Exception ex)
                    {
                        Log("WARN", $"Patch file skipped: {ex.Message}", ex);
                    }
                }

                patchedArchive = PatchedConfigArchive.WithNoPatches(archive);

                // 5. Import SharedGameConfig (master)
                progress?.Report("Importing SharedGameConfig...");
                AppLogger.Info("Starting SharedGameConfig import...");

                // Capture console output with real-time forwarding to UI
                var consoleWriter = new ProgressTextWriter(progress);
                var originalOut = Console.Out;
                Console.SetOut(consoleWriter);

                try
                {
                    var gameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
                    ClientGlobal.SharedGameConfig = gameConfig;
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                // Classify collected console output into warnings/errors
                foreach (var line in consoleWriter.Lines)
                {
                    AppLogger.Info($"Import: {line}");

                    if (line.StartsWith("[ERROR]") || line.StartsWith("[TRACE]"))
                        errors.Add(line);
                    else if (line.StartsWith("[WARN]"))
                        warnings.Add(line);
                    // [INFO] and [PROGRESS] lines are informational — not warnings
                }

                // Diagnostic: check which key properties were populated
                var config = ClientGlobal.SharedGameConfig;
                var diag = new[]
                {
                    $"MergeChains={config.MergeChains != null}",
                    $"Areas={config.Areas != null}",
                    $"Items={config.Items != null}",
                    $"HotspotDefinitions={config.HotspotDefinitions != null}",
                };
                var diagMsg = $"Config state: {string.Join(", ", diag)}";
                progress?.Report(diagMsg);
                AppLogger.Info(diagMsg);

                progress?.Report("SharedGameConfig imported");

                // 6. Create output directory
                Directory.CreateDirectory(outputDir);

                // 7. Run dumpers
                if (mode == DumpMode.All || mode == DumpMode.Chains)
                {
                    chainPath = Path.Combine(outputDir, "chain_item_odds.json");
                    progress?.Report("Dumping merge chains...");
                    try
                    {
                        new MergeChainDumper(true).WriteJson(chainPath, ClientGlobal.SharedGameConfig);
                        var size = new FileInfo(chainPath).Length / 1024;
                        progress?.Report($"chain_item_odds.json written ({size} KB)");
                        AppLogger.Info($"chain_item_odds.json: {size} KB");
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR", $"Merge chains dump failed: {ex.Message}", ex);
                        chainPath = null;
                    }
                }

                if (mode == DumpMode.All || mode == DumpMode.Areas)
                {
                    areasPath = Path.Combine(outputDir, "areas.json");
                    progress?.Report("Dumping areas...");
                    try
                    {
                        new AreaDumper().WriteJson(areasPath, ClientGlobal.SharedGameConfig);
                        var size = new FileInfo(areasPath).Length / 1024;
                        progress?.Report($"areas.json written ({size} KB)");
                        AppLogger.Info($"areas.json: {size} KB");
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR", $"Areas dump failed: {ex.Message}", ex);
                        areasPath = null;
                    }
                }

                if (mode == DumpMode.All || mode == DumpMode.Events)
                {
                    eventsPath = Path.Combine(outputDir, "events.json");
                    progress?.Report("Dumping events...");
                    try
                    {
                        new EventDumper().WriteJson(eventsPath, ClientGlobal.SharedGameConfig);
                        var size = new FileInfo(eventsPath).Length / 1024;
                        progress?.Report($"events.json written ({size} KB)");
                        AppLogger.Info($"events.json: {size} KB");
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR", $"Events dump failed: {ex.Message}", ex);
                        eventsPath = null;
                    }
                }

                if (mode == DumpMode.All || mode == DumpMode.CardCollection)
                {
                    cardCollectionPath = Path.Combine(outputDir, "card_collection.json");
                    progress?.Report("Dumping card collection...");
                    try
                    {
                        new CardCollectionDumper().WriteJson(cardCollectionPath, ClientGlobal.SharedGameConfig);
                        var size = new FileInfo(cardCollectionPath).Length / 1024;
                        progress?.Report($"card_collection.json written ({size} KB)");
                        AppLogger.Info($"card_collection.json: {size} KB");
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR", $"Card collection dump failed: {ex.Message}", ex);
                        cardCollectionPath = null;
                    }
                }

                if (mode == DumpMode.Experimental)
                {
                    var expDir = Path.Combine(outputDir, "Experimental");
                    experimentalPath = expDir;
                    progress?.Report("Dumping experimental data...");
                    try
                    {
                        var written = new ExperimentalDumper().WriteIndividualFiles(expDir, ClientGlobal.SharedGameConfig);
                        foreach (var (section, filePath) in written)
                        {
                            var size = new FileInfo(filePath).Length / 1024;
                            progress?.Report($"  {section}.json ({size} KB)");
                        }
                        progress?.Report($"Experimental: {written.Count} files written");
                        AppLogger.Info($"Experimental: {written.Count} files into {expDir}");
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR", $"Experimental dump failed: {ex.Message}", ex);
                        experimentalPath = null;
                    }
                }

                // 8. Dump patched variants
                var relevantPatchedArchives = patchedArchives.Where(x =>
                    x.Item3.ContainsPatch("Areas")
                    || x.Item3.ContainsPatch("HotspotDefinitions")
                    || x.Item3.ContainsPatch("Items")).ToArray();

                if (relevantPatchedArchives.Length > 0)
                {
                    progress?.Report($"Processing {relevantPatchedArchives.Length} experiment patch(es)...");
                    AppLogger.Info($"Processing {relevantPatchedArchives.Length} experiment patches");

                    foreach (var (experimentId, variantId, pa) in relevantPatchedArchives)
                    {
                        var patchLabel = $"{experimentId}_{variantId}";
                        try
                        {
                            ClientGlobal.SharedGameConfig = null;

                            var patchWriter = new ProgressTextWriter(null); // silent — no UI spam
                            Console.SetOut(patchWriter);
                            try
                            {
                                ClientGlobal.SharedGameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(pa);
                            }
                            finally
                            {
                                Console.SetOut(originalOut);
                            }

                            var patchDir = Path.Combine(outputDir, patchLabel);
                            Directory.CreateDirectory(patchDir);

                            if (mode == DumpMode.All || mode == DumpMode.Chains)
                            {
                                try { new MergeChainDumper(true).WriteJson(Path.Combine(patchDir, "chain_item_odds.json"), ClientGlobal.SharedGameConfig); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} chains: {ex.Message}", ex); }
                            }
                            if (mode == DumpMode.All || mode == DumpMode.Areas)
                            {
                                try { new AreaDumper().WriteJson(Path.Combine(patchDir, "areas.json"), ClientGlobal.SharedGameConfig); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} areas: {ex.Message}", ex); }
                            }
                            if (mode == DumpMode.All || mode == DumpMode.Events)
                            {
                                try { new EventDumper().WriteJson(Path.Combine(patchDir, "events.json"), ClientGlobal.SharedGameConfig); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} events: {ex.Message}", ex); }
                            }

                            progress?.Report($"Patch {patchLabel} dumped");
                            AppLogger.Info($"Patch {patchLabel} dumped");
                        }
                        catch (Exception ex)
                        {
                            Log("WARN", $"Patch {patchLabel} failed: {ex.Message}", ex);
                        }
                    }

                    // Restore master config
                    try
                    {
                        var restoreWriter = new ProgressTextWriter(null);
                        Console.SetOut(restoreWriter);
                        try
                        {
                            ClientGlobal.SharedGameConfig = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(patchedArchive);
                        }
                        finally
                        {
                            Console.SetOut(originalOut);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("WARN", $"Restoring master config failed: {ex.Message}", ex);
                    }
                }

                progress?.Report("Done.");
                AppLogger.Info("=== Dump completed ===");
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Fatal: {ex.Message}", ex);
            }

            return new DumpResult(chainPath, areasPath, eventsPath, cardCollectionPath, experimentalPath, warnings, errors);
        });
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            MetaplayCore.Initialize();
            _initialized = true;
        }
    }
}
