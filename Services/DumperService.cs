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

        // Forward to UI — show [PROGRESS] as clean status, skip noise
        if (line.StartsWith("[PROGRESS]"))
            _progress?.Report(line[11..]); // strip "[PROGRESS] "
        else if (line.Contains("not in archive, skipping"))
            { } // log-only, not relevant for user
        else if (!line.StartsWith("[TRACE]"))
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
        Pets = 64,
        All = Chains | Areas | Events | CardCollection | Dialogues | Pets
    }

    private static readonly object _initLock = new();
    private static bool _initialized;

    /// <summary>
    /// Reads CreatedAt timestamp from a binary config archive WITHOUT doing a full import.
    /// Fast — only parses the archive header.
    /// </summary>
    public static DateTimeOffset? ReadConfigCreatedAt(string configPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(configPath);
            var archive = ConfigArchive.FromBytes(bytes);
            var metaTime = archive.CreatedAt;
            // MetaTime.MillisecondsSinceEpoch is milliseconds since Unix epoch
            return DateTimeOffset.FromUnixTimeMilliseconds(metaTime.MillisecondsSinceEpoch);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ReadConfigCreatedAt failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Picks the config archive with the newest embedded CreatedAt from a directory.
    /// "Pull from Phone" copies EVERY config version the game has cached under _DATA/C/
    /// (content-addressed names, all written with the same mtime), so selecting by file
    /// name or file mtime is non-deterministic and routinely picks a stale archive — the
    /// root cause of a dump silently missing the current airing (e.g. an event whose Start
    /// was moved to today only in the newest config build). We must read each archive's
    /// header CreatedAt and take the latest. Returns null if no readable archive is found.
    /// </summary>
    public static string? SelectNewestConfigArchive(string? configDir)
    {
        if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir)) return null;
        string? best = null;
        DateTimeOffset bestAt = DateTimeOffset.MinValue;
        foreach (var f in Directory.GetFiles(configDir))
        {
            var at = ReadConfigCreatedAt(f);
            if (at.HasValue && (best == null || at.Value > bestAt))
            {
                best = f;
                bestAt = at.Value;
            }
        }
        if (best != null)
            AppLogger.Info($"SelectNewestConfigArchive: {Path.GetFileName(best)} (CreatedAt {bestAt:yyyy-MM-dd HH:mm:ss}Z) from {Directory.GetFiles(configDir).Length} archive(s)");
        return best;
    }

    public static async Task<DumpResult> DumpAsync(
        string configPath,
        string? patchPath,
        string? languagePath,
        string outputDir,
        DumpMode mode = DumpMode.All,
        EventFilters eventFilters = EventFilters.All,
        bool includeStaleBranches = true,
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
                //
                // Game stores multiple patch snapshots in _DATA/P/ — one per session/server
                // response. Each file contains a different set of `(ExperimentId, VariantId)`
                // tuples and a patch like WildItem_SME_01_B may exist in only one of them.
                // Resolution strategy:
                //   - If DumperPatchPath points to a file → load that file only (legacy).
                //   - If it points to a directory → load every file in it and union patches,
                //     keyed by (ExperimentId, VariantId). On duplicate keys keep the version
                //     from the most-recently-modified file (newest server response).
                //
                // This ensures patches like WildItem_SME_01_B (only in newer patch snapshot)
                // get picked up automatically without manual settings.json editing.
                PatchedConfigArchive patchedArchive;
                var patchedArchives = new List<(PlayerExperimentId, ExperimentVariantId, PatchedConfigArchive, string[], string FolderLabel)>();

                if (!string.IsNullOrEmpty(patchPath))
                {
                    var patchFilesToLoad = new List<string>();
                    if (Directory.Exists(patchPath))
                    {
                        // Directory mode — enumerate, sort by mtime ascending so newer files
                        // overwrite older entries in the merge dictionary below.
                        patchFilesToLoad.AddRange(Directory.GetFiles(patchPath)
                            .OrderBy(f => File.GetLastWriteTimeUtc(f)));
                    }
                    else if (File.Exists(patchPath))
                    {
                        patchFilesToLoad.Add(patchPath);

                        // If the user pointed at a file inside a _DATA/P-style directory,
                        // also pull in any sibling patch files so multi-snapshot archives
                        // load the same way directory mode does. Sibling files must look
                        // like Metaplay content hashes (no extension, 32+ alphanumeric chars).
                        var parentDir = Path.GetDirectoryName(patchPath);
                        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        {
                            foreach (var sibling in Directory.GetFiles(parentDir)
                                .OrderBy(f => File.GetLastWriteTimeUtc(f)))
                            {
                                if (sibling == patchPath) continue;
                                var name = Path.GetFileName(sibling);
                                // Heuristic: Metaplay content hash files have no extension and contain a dash.
                                if (Path.GetExtension(name) == string.Empty && name.Contains('-'))
                                    patchFilesToLoad.Add(sibling);
                            }
                        }
                    }

                    if (patchFilesToLoad.Count > 0)
                    {
                        progress?.Report($"Loading patches from {patchFilesToLoad.Count} file(s)...");
                        AppLogger.Info($"Patch files to scan: {patchFilesToLoad.Count}");

                        // Merge across all files, keeping EVERY distinct-content version of each label.
                        //
                        // BACKGROUND: a fresh "Pull from Phone" writes multiple snapshots to _DATA/P/, all
                        // with the same mtime. The same label (e.g. Rebalance_SeasonPass_May2026_01_B) can
                        // appear in several snapshots with DIFFERENT byte content — one version changes
                        // event X, another does not. The previous "richer payload wins" heuristic silently
                        // dropped the smaller version, which could be the only one that changes a specific
                        // event (e.g. Soccer / SP_WorldCup2026 rewards).
                        //
                        // New rule (2026-06-26):
                        //   • Dedup key = (label, SHA-256 of raw bytes) — byte-identical copies collapse.
                        //   • All DISTINCT-CONTENT versions of a label survive into patchedArchives.
                        //   • Folder naming: the version with the largest raw payload keeps the bare label
                        //     name (preserving the old primary-wins behaviour for single-version labels);
                        //     additional distinct versions get __v2, __v3, … suffixes (ordered by
                        //     descending raw size then ascending source filename for determinism).
                        //
                        // The empty-folder cleanup at the end of Phase B still applies: any version whose
                        // output is byte-identical to master produces no files → empty folder → deleted.
                        // Net effect: every distinct branch that changes ANY tracked output keeps its folder;
                        // no branch is ever lost to a size heuristic.

                        // Key: (label, content-hash) → dedup byte-identical copies across snapshots.
                        var distinctPatches = new Dictionary<string, (PlayerExperimentId ExpId, ExperimentVariantId VarId, byte[] RawBytes, GameConfigPatchEnvelope Envelope, string SourceFile, int RawSize, DateTime Mtime)>(StringComparer.Ordinal);
                        // Per-file experiment-id sets — used to identify the "current" patch blob
                        // (the one covering the account's live memberships) for stale-branch filtering.
                        var fileExpIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

                        foreach (var pf in patchFilesToLoad)
                        {
                            try
                            {
                                var mtime = File.GetLastWriteTimeUtc(pf);
                                var fileBytes = File.ReadAllBytes(pf);
                                var specializationPatches = GameConfigSpecializationPatches.FromBytes(fileBytes);
                                fileExpIds[pf] = new HashSet<string>(
                                    specializationPatches.Patches.Keys.Select(k => k.ToString() ?? ""), StringComparer.Ordinal);

                                foreach (var (expId, variantMap) in specializationPatches.Patches)
                                    foreach (var (varId, rawBytes) in variantMap)
                                    {
                                        var label = $"{expId}_{varId}";
                                        var actualBytes = rawBytes ?? Array.Empty<byte>();
                                        // Compute SHA-256 of the raw patch bytes for content-based dedup.
                                        var hashBytes = System.Security.Cryptography.SHA256.HashData(actualBytes);
                                        var hashHex = Convert.ToHexString(hashBytes);
                                        var dedupKey = $"{label}\0{hashHex}";

                                        if (!distinctPatches.ContainsKey(dedupKey))
                                        {
                                            var envelope = GameConfigPatchEnvelope.Deserialize(actualBytes);
                                            distinctPatches[dedupKey] = (expId, varId, actualBytes, envelope, Path.GetFileName(pf), actualBytes.Length, mtime);
                                            AppLogger.Debug($"Patch {label}: new distinct version ({actualBytes.Length} B) from {Path.GetFileName(pf)} [{hashHex[..8]}]");
                                        }
                                        // else: byte-identical copy — silently skip (true dedup).
                                    }
                            }
                            catch (Exception ex)
                            {
                                Log("WARN", $"Patch file {Path.GetFileName(pf)} skipped: {ex.Message}", ex);
                            }
                        }

                        // Group distinct versions per label; assign folder names.
                        // Primary version (largest raw size, tiebreak ascending filename) → bare label.
                        // Additional versions → label__v2, label__v3, …
                        var byLabel = distinctPatches.Values
                            .GroupBy(v => $"{v.ExpId}_{v.VarId}", StringComparer.Ordinal)
                            .ToDictionary(
                                g => g.Key,
                                g => g.OrderByDescending(v => v.RawSize).ThenBy(v => v.SourceFile, StringComparer.Ordinal).ToList(),
                                StringComparer.Ordinal);

                        int totalDistinct = 0;
                        foreach (var (label, versions) in byLabel)
                        {
                            if (versions.Count > 1)
                                AppLogger.Info($"Patch {label}: {versions.Count} distinct versions kept ({string.Join(", ", versions.Select(v => $"{v.RawSize} B from {v.SourceFile}"))}) — folders: {label}, {string.Join(", ", Enumerable.Range(2, versions.Count - 1).Select(i => $"{label}__v{i}"))}");

                            for (int vi = 0; vi < versions.Count; vi++)
                            {
                                var v = versions[vi];
                                var folderLabel = vi == 0 ? label : $"{label}__v{vi + 1}";
                                var pa = new PatchedConfigArchive(archive, new[] { v.Envelope });
                                var patchEntryNames = v.Envelope.EntryNames.ToArray();
                                patchedArchives.Add((v.ExpId, v.VarId, pa, patchEntryNames, folderLabel));

                                AppLogger.Info($"Patch {folderLabel} entries: [{string.Join(", ", patchEntryNames)}] (from {v.SourceFile})");
                                totalDistinct++;
                            }
                        }

                        progress?.Report($"{T()} Loaded {totalDistinct} distinct patch version(s) ({byLabel.Count} unique labels) across {patchFilesToLoad.Count} file(s)");
                        AppLogger.Info($"{T()} Patches loaded: {totalDistinct} distinct versions, {byLabel.Count} labels, across {patchFilesToLoad.Count} files");

                        // Stale-branch filter: _DATA/P is a content-addressed cache that never deletes
                        // old snapshots, so the union can contain branches no longer live (e.g.
                        // NewSegments_01 superseded by _02). The account's LastSessionGameConfig.dat
                        // memberships are the authoritative "current" set. The patch blob(s) that cover
                        // ALL current memberships are the live snapshots; branches present only in
                        // non-covering (older) blobs are stale. When includeStaleBranches is false, keep
                        // only branches whose experiment id lives in a covering (current) blob.
                        if (!includeStaleBranches)
                        {
                            var datPath = AbGroupsService.ResolveDatPathFromConfig(configPath);
                            var memberships = AbGroupsService.TryParseMembershipsFromFile(datPath);
                            if (memberships != null && memberships.Count > 0)
                            {
                                var live = new HashSet<string>(memberships.Select(m => m.Experiment), StringComparer.Ordinal);
                                var coveringFiles = fileExpIds.Where(kv => live.IsSubsetOf(kv.Value)).Select(kv => kv.Key).ToList();
                                if (coveringFiles.Count > 0)
                                {
                                    var currentExps = new HashSet<string>(StringComparer.Ordinal);
                                    foreach (var f in coveringFiles) currentExps.UnionWith(fileExpIds[f]);
                                    var removed = patchedArchives.Where(p => !currentExps.Contains(p.Item1.ToString() ?? "")).Select(p => p.Item5).ToList();
                                    patchedArchives.RemoveAll(p => !currentExps.Contains(p.Item1.ToString() ?? ""));
                                    if (removed.Count > 0)
                                    {
                                        progress?.Report($"{T()} Excluded {removed.Count} non-live branch(es): {string.Join(", ", removed)}");
                                        AppLogger.Info($"{T()} Stale-branch filter: excluded {removed.Count} ({string.Join(", ", removed)}); {coveringFiles.Count} covering blob(s)");
                                    }
                                }
                                else
                                {
                                    Log("WARN", "Cannot filter non-live branches: no patch blob covers all account memberships — exporting all branches.");
                                }
                            }
                            else
                            {
                                Log("WARN", "Cannot filter non-live branches: LastSessionGameConfig.dat unavailable — exporting all branches.");
                            }
                        }
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
                    else if (line.StartsWith("[WARN]") && !line.Contains("not in archive, skipping"))
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

                // 7. Extract ALL patches — no filter.
                // Patches that do not change Items/MergeChains/Areas/Events output are still imported;
                // WriteJsonIfDifferent suppresses their files on byte-identical match and the empty
                // subfolder is deleted at the end of the patch task. Net effect: zero false negatives
                // (any patch that mutates any tracked field survives), tiny throughput cost for the
                // irrelevant ones.
                var relevantPatchedArchives = patchedArchives
                    .Select(x => (ExpId: x.Item1, VarId: x.Item2, Archive: x.Item3, EntryNames: x.Item4, FolderLabel: x.FolderLabel))
                    .ToArray();

                AppLogger.Info($"Patch extraction: {relevantPatchedArchives.Length} distinct patch version(s) (unfiltered, all branches)");

                // 8. Run master dumps first, then patch dumps (patches need baseline content)
                progress?.Report($"{T()} Dumping game data ({relevantPatchedArchives.Length} patches)...");
                var masterConfig = ClientGlobal.SharedGameConfig;

                // Baseline content for patch comparison (populated by master dumps)
                string? baselineChains = null, baselineAreas = null, baselineEvents = null;

                // ── Phase A: Master dumps (parallel) ──
                var masterTasks = new List<Action>();

                if (mode.HasFlag(DumpMode.Chains))
                    masterTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "chain_item_odds.json");
                        try
                        {
                            baselineChains = new MergeChainDumper(true).WriteJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} chain_item_odds.json written ({size} KB)");
                            AppLogger.Info($"{T()} chain_item_odds.json: {size} KB");
                            chainPath = p;
                        }
                        catch (Exception ex) { Log("ERROR", $"Merge chains dump failed: {ex.Message}", ex); }
                    });

                if (mode.HasFlag(DumpMode.Areas))
                    masterTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "areas.json");
                        try
                        {
                            baselineAreas = new AreaDumper().WriteJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} areas.json written ({size} KB)");
                            AppLogger.Info($"{T()} areas.json: {size} KB");
                            areasPath = p;
                        }
                        catch (Exception ex) { Log("ERROR", $"Areas dump failed: {ex.Message}", ex); }
                    });

                if (mode.HasFlag(DumpMode.Events))
                    masterTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "events.json");
                        try
                        {
                            baselineEvents = new EventDumper(eventFilters).WriteJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} events.json written ({size} KB)");
                            AppLogger.Info($"{T()} events.json: {size} KB");
                            eventsPath = p;
                        }
                        catch (Exception ex) { Log("ERROR", $"Events dump failed: {ex.Message}", ex); }
                    });

                if (mode.HasFlag(DumpMode.CardCollection))
                    masterTasks.Add(() =>
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
                    masterTasks.Add(() =>
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

                if (mode.HasFlag(DumpMode.Pets))
                    masterTasks.Add(() =>
                    {
                        var p = Path.Combine(outputDir, "Pets.json");
                        try
                        {
                            ExperimentalDumper.WritePetsJson(p, masterConfig);
                            var size = new FileInfo(p).Length / 1024;
                            progress?.Report($"{T()} Pets.json written ({size} KB)");
                            AppLogger.Info($"{T()} Pets.json: {size} KB");
                        }
                        catch (Exception ex) { Log("ERROR", $"Pets dump failed: {ex.Message}", ex); }
                    });

                if (masterTasks.Count > 0)
                    Parallel.Invoke(masterTasks.ToArray());

                // ── Phase B: Patch dumps (parallel, after master completes) ──
                var allTasks = new List<Action>();

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

                            // Pets.json is now dumped separately via DumpMode.Pets
                        }
                        catch (Exception ex)
                        {
                            Log("ERROR", $"Experimental dump failed: {ex.Message}", ex);
                        }
                    });

                // Patch import+dump tasks (each patch imports & dumps independently).
                // patchLabel is the folder-safe identifier: for single-version labels it equals
                // "{expId}_{varId}"; for multi-version labels subsequent versions get "__v2", "__v3", …
                // suffixes so they never collide on the same output folder.
                foreach (var (experimentId, variantId, pa, patchEntryNames, folderLabel) in relevantPatchedArchives)
                {
                    var patchLabel = folderLabel;
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
                            var diffFiles = new List<string>();
                            var sameFiles = new List<string>();

                            void RecordResult(string fileName, bool differs)
                            { if (differs) diffFiles.Add(fileName); else sameFiles.Add(fileName); }

                            if (mode.HasFlag(DumpMode.Chains))
                            {
                                try { RecordResult("chain_item_odds.json", new MergeChainDumper(true).WriteJsonIfDifferent(Path.Combine(patchDir, "chain_item_odds.json"), patchConfig, baselineChains)); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} chains: {ex.Message}", ex); }
                            }
                            if (mode.HasFlag(DumpMode.Areas))
                            {
                                try { RecordResult("areas.json", new AreaDumper().WriteJsonIfDifferent(Path.Combine(patchDir, "areas.json"), patchConfig, baselineAreas)); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} areas: {ex.Message}", ex); }
                            }
                            if (mode.HasFlag(DumpMode.Events))
                            {
                                try { RecordResult("events.json", new EventDumper(eventFilters).WriteJsonIfDifferent(Path.Combine(patchDir, "events.json"), patchConfig, baselineEvents)); }
                                catch (Exception ex) { Log("WARN", $"Patch {patchLabel} events: {ex.Message}", ex); }
                            }

                            if (diffFiles.Count == 0 && Directory.Exists(patchDir) && !Directory.EnumerateFileSystemEntries(patchDir).Any())
                                Directory.Delete(patchDir);

                            var parts = new List<string>();
                            if (diffFiles.Count > 0) parts.Add($"differs: {string.Join(", ", diffFiles)}");
                            if (sameFiles.Count > 0) parts.Add($"identical: {string.Join(", ", sameFiles)}");
                            var summary = $"{T()} Patch {patchLabel}: {string.Join(" · ", parts)}";
                            progress?.Report(summary);
                            AppLogger.Info(summary);
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

                // ── Metadata.txt: created/game/unity version header + AB memberships + patch catalog ──
                try
                {
                    var datPath = AbGroupsService.ResolveDatPathFromConfig(configPath);
                    var memberships = AbGroupsService.TryParseMembershipsFromFile(datPath);
                    var catalog = patchedArchives
                        .Select(p => (p.Item1.ToString() ?? "", p.Item2.ToString() ?? ""))
                        .ToList();
                    var createdAt = DiscordDumpService.ReadCreatedAtFromDump(outputDir);
                    var gameVersion = AbGroupsService.ReadValueFile(
                        AbGroupsService.ResolveDataFile(configPath, PhoneDetectionService.GameVersionFileName));
                    var unityVersion = AbGroupsService.ReadValueFile(
                        AbGroupsService.ResolveDataFile(configPath, PhoneDetectionService.UnityVersionFileName));
                    var metaPath = Path.Combine(outputDir, AbGroupsService.OutputFileName);
                    AbGroupsService.Write(metaPath, createdAt, gameVersion, unityVersion, memberships, catalog);
                    var memCount = memberships?.Count ?? 0;
                    progress?.Report($"{T()} {AbGroupsService.OutputFileName} written (game {gameVersion ?? "?"}, {memCount} memberships, {catalog.Select(c => c.Item1).Distinct().Count()} catalog experiments)");
                    AppLogger.Info($"{T()} {AbGroupsService.OutputFileName}: createdAt={createdAt ?? "?"}, game={gameVersion ?? "?"}, unity={unityVersion ?? "?"}, {memCount} memberships, {catalog.Count} catalog entries (dat: {(datPath != null && File.Exists(datPath) ? "found" : "missing")})");
                }
                catch (Exception ex)
                {
                    Log("WARN", $"Metadata.txt generation failed: {ex.Message}", ex);
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
