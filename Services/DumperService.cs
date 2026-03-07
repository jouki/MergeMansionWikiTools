using System.Diagnostics;
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
        string? DialoguesPath,
        string? ExperimentalPath,
        List<string> Warnings,
        List<string> Errors
    );

    [Flags]
    public enum DumpMode
    {
        None = 0,
        Chains = 1,
        Areas = 2,
        Events = 4,
        CardCollection = 8,
        Experimental = 16,
        Dialogues = 32,
        All = Chains | Areas | Events | CardCollection | Dialogues
    }

    private static readonly object _initLock = new();
    private static bool _initialized;

    public static async Task<DumpResult> DumpAsync(
        string configPath,
        string? patchPath,
        string? languagePath,
        string outputDir,
        DumpMode mode = DumpMode.All,
        EventFilters eventFilters = EventFilters.All,
        IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            string? chainPath = null, areasPath = null, eventsPath = null, cardCollectionPath = null, dialoguesPath = null, experimentalPath = null;

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
                var sw = Stopwatch.StartNew();
                string T() => $"[{sw.ElapsedMilliseconds}ms]";

                AppLogger.Info($"=== Dump started: mode={mode}, config={configPath} ===");

                // 1. Initialize MetaplayCore (once)
                progress?.Report("Initializing MetaplayCore...");
                EnsureInitialized();
                progress?.Report($"{T()} MetaplayCore initialized");
                AppLogger.Info($"{T()} MetaplayCore initialized");

                // 2. Load language file (optional)
                if (!string.IsNullOrEmpty(languagePath) && File.Exists(languagePath))
                {
                    progress?.Report("Loading language file...");
                    try
                    {
                        var langFileName = Path.GetFileName(languagePath);
                        var langHash = ContentHash.ParseString(langFileName);
                        MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(langHash, File.ReadAllBytes(languagePath));
                        progress?.Report($"{T()} Language loaded: {langFileName}");
                        AppLogger.Info($"{T()} Language loaded: {langFileName}");
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
                progress?.Report($"{T()} Config archive loaded ({archive.Entries.Count} entries)");
                AppLogger.Info($"{T()} Config archive: {archive.Entries.Count} entries");

                // Log all archive entry names for diagnostics
                AppLogger.Info("Archive entries: " + string.Join(", ", archive.Entries.Select(e => e.Name)));

                // 4. Load patches (optional)
                PatchedConfigArchive patchedArchive;
                var patchedArchives = new List<(PlayerExperimentId, ExperimentVariantId, PatchedConfigArchive, string[])>();

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
                            var patchEntryNames = configPatch.Item3.EntryNames.ToArray();
                            patchedArchives.Add((configPatch.Item1, configPatch.Item2, pa, patchEntryNames));

                            // Diagnostics: log all entry names in each patch envelope
                            var label = $"{configPatch.Item1}_{configPatch.Item2}";
                            AppLogger.Info($"Patch {label} entries: [{string.Join(", ", patchEntryNames)}]");
                        }

                        progress?.Report($"{T()} Loaded {configPatches.Length} patch(es)");
                        AppLogger.Info($"{T()} Patches loaded: {configPatches.Length}");
                    }
                    catch (Exception ex)
                    {
                        Log("WARN", $"Patch file skipped: {ex.Message}", ex);
                    }
                }

                patchedArchive = PatchedConfigArchive.WithNoPatches(archive);

                // 5. Import SharedGameConfig (master)
                progress?.Report($"{T()} Importing SharedGameConfig...");
                AppLogger.Info($"{T()} Starting SharedGameConfig import...");

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
                progress?.Report($"{T()} {diagMsg}");
                AppLogger.Info($"{T()} {diagMsg}");

                progress?.Report($"{T()} SharedGameConfig imported");

                // 6. Create output directory
                Directory.CreateDirectory(outputDir);

                // 7. Filter relevant patches early (before parallel section)
                var relevantPatchedArchives = patchedArchives.Where(x =>
                    x.Item3.ContainsPatch("Areas")
                    || x.Item3.ContainsPatch("HotspotDefinitions")
                    || x.Item3.ContainsPatch("Items")
                    || x.Item3.ContainsPatch("MergeChains"))
                    .Select(x => (x.Item1, x.Item2, x.Item3, x.Item4))
                    .ToArray();

                if (relevantPatchedArchives.Length < patchedArchives.Count)
                {
                    var skipped = patchedArchives.Count - relevantPatchedArchives.Length;
                    AppLogger.Info($"Patch filter: {relevantPatchedArchives.Length} relevant, {skipped} skipped (total {patchedArchives.Count})");
                    progress?.Report($"Skipped {skipped} irrelevant patch(es)");
                }

                // 8. Run master dumps + patch import/dumps all in parallel
                progress?.Report($"{T()} Dumping game data ({relevantPatchedArchives.Length} patches)...");
                var masterConfig = ClientGlobal.SharedGameConfig;
                var allTasks = new List<Action>();

                // Master dumpers
                if (mode.HasFlag(DumpMode.Chains))
                    allTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "chain_item_odds.json");
                        try
                        {
                            new MergeChainDumper(true).WriteJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} chain_item_odds.json written ({size} KB)");
                            AppLogger.Info($"{T()} chain_item_odds.json: {size} KB");
                            chainPath = p;
                        }
                        catch (Exception ex) { Log("ERROR", $"Merge chains dump failed: {ex.Message}", ex); }
                    });

                if (mode.HasFlag(DumpMode.Areas))
                    allTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "areas.json");
                        try
                        {
                            new AreaDumper().WriteJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} areas.json written ({size} KB)");
                            AppLogger.Info($"{T()} areas.json: {size} KB");
                            areasPath = p;
                        }
                        catch (Exception ex) { Log("ERROR", $"Areas dump failed: {ex.Message}", ex); }
                    });

                if (mode.HasFlag(DumpMode.Events))
                    allTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "events.json");
                        try
                        {
                            new EventDumper(eventFilters).WriteJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} events.json written ({size} KB)");
                            AppLogger.Info($"{T()} events.json: {size} KB");
                            eventsPath = p;
                        }
                        catch (Exception ex) { Log("ERROR", $"Events dump failed: {ex.Message}", ex); }
                    });

                if (mode.HasFlag(DumpMode.CardCollection))
                    allTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "card_collection.json");
                        try
                        {
                            new CardCollectionDumper().WriteJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} card_collection.json written ({size} KB)");
                            AppLogger.Info($"{T()} card_collection.json: {size} KB");
                            cardCollectionPath = p;
                        }
                        catch (Exception ex) { Log("ERROR", $"Card collection dump failed: {ex.Message}", ex); }
                    });

                if (mode.HasFlag(DumpMode.Dialogues))
                    allTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "dialogues.json");
                        try
                        {
                            new DialogueDumper().WriteJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} dialogues.json written ({size} KB)");
                            AppLogger.Info($"{T()} dialogues.json: {size} KB");
                            dialoguesPath = p;
                        }
                        catch (Exception ex) { Log("ERROR", $"Dialogues dump failed: {ex.Message}", ex); }
                    });

                if (mode.HasFlag(DumpMode.Experimental))
                    allTasks.Add(() =>
                    {
                        var expDir = Path.Combine(outputDir, "Experimental");
                        try
                        {
                            var written = new ExperimentalDumper().WriteIndividualFiles(expDir, masterConfig);
                            foreach (var (section, filePath) in written)
                            {
                                var size = new FileInfo(filePath).Length / 1024;
                                progress?.Report($"  {section}.json ({size} KB)");
                            }
                            progress?.Report($"{T()} Experimental: {written.Count} files written");
                            AppLogger.Info($"{T()} Experimental: {written.Count} files into {expDir}");
                            experimentalPath = expDir;
                        }
                        catch (Exception ex)
                        {
                            Log("ERROR", $"Experimental dump failed: {ex.Message}", ex);
                        }
                    });

                // Patch import+dump tasks (each patch imports & dumps independently)
                foreach (var (experimentId, variantId, pa, patchEntryNames) in relevantPatchedArchives)
                {
                    var patchLabel = $"{experimentId}_{variantId}";
                    var capturedPa = pa;
                    var capturedEntryNames = patchEntryNames;

                    allTasks.Add(() =>
                    {
                        try
                        {
                            var patchConfig = SharedGameConfig.ImportPatchedFrom(masterConfig, capturedPa, capturedEntryNames);
                            progress?.Report($"{T()} Patch {patchLabel} imported ({capturedEntryNames.Length} entries)");
                            AppLogger.Info($"{T()} Patch {patchLabel} imported");

                            var patchDir = Path.Combine(outputDir, patchLabel);
                            Directory.CreateDirectory(patchDir);

                            var needsChains = capturedPa.ContainsPatch("Items") || capturedPa.ContainsPatch("MergeChains");
                            var needsAreas = capturedPa.ContainsPatch("Areas") || capturedPa.ContainsPatch("HotspotDefinitions");

                            if (mode.HasFlag(DumpMode.Chains) && needsChains)
                            {
                                try { new MergeChainDumper(true).WriteJson(Path.Combine(patchDir, "chain_item_odds.json"), patchConfig); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} chains: {ex.Message}", ex); }
                            }
                            if (mode.HasFlag(DumpMode.Areas) && needsAreas)
                            {
                                try { new AreaDumper().WriteJson(Path.Combine(patchDir, "areas.json"), patchConfig); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} areas: {ex.Message}", ex); }
                            }
                            if (mode.HasFlag(DumpMode.Events) && needsAreas)
                            {
                                try { new EventDumper(eventFilters).WriteJson(Path.Combine(patchDir, "events.json"), patchConfig); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} events: {ex.Message}", ex); }
                            }

                            progress?.Report($"{T()} Patch {patchLabel} dumped");
                            AppLogger.Info($"{T()} Patch {patchLabel} dumped");
                        }
                        catch (Exception ex)
                        {
                            Log("WARN", $"Patch {patchLabel} failed: {ex.Message}", ex);
                        }
                    });
                }

                // Suppress console output from parallel imports
                var patchWriter = new ProgressTextWriter(null);
                Console.SetOut(patchWriter);

                try
                {
                    if (allTasks.Count > 0)
                        Parallel.Invoke(allTasks.ToArray());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                progress?.Report($"{T()} Done. Total: {sw.ElapsedMilliseconds}ms");
                AppLogger.Info($"=== Dump completed in {sw.ElapsedMilliseconds}ms ===");
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Fatal: {ex.Message}", ex);
            }

            return new DumpResult(chainPath, areasPath, eventsPath, cardCollectionPath, dialoguesPath, experimentalPath, warnings, errors);
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
