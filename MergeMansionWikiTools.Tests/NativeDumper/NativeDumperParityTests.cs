using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper;
using MergeMansionWikiTools.Services;
using MergeMansionWikiTools.Services.Dumping;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// End-to-end contract of the Native engine: on real game data it must produce byte-identical files
/// to the Legacy engine. The unit tests around it cover single serializers; this one covers the whole
/// pipeline (config import, dumper, JSON envelope, encoding) on the newest archive in <c>_DATA/C</c>.
///
/// The full matrix (every archive, every A/B patch, the EventFilters matrix, Experimental) lives in
/// <c>DumpHarness --compare-engines</c>; this test is the cheap subset that runs in the suite: one
/// archive, no patches, the six main output files.
///
/// It needs local game data, which is not in the repository. When <c>_DATA</c> is missing (CI, fresh
/// clone) the test returns early and reports as passed — a deliberate trade-off so that the suite
/// needs no extra NuGet package for skippable tests.
/// </summary>
[Collection(GlobalGameStateCollection.Name)]
public class NativeDumperParityTests
{
    /// <summary>The six files both engines must agree on: the five main dumps plus the Pets aux dump.</summary>
    private static readonly string[] ComparedFiles =
    {
        "chain_item_odds.json", "areas.json", "events.json", "card_collection.json", "dialogues.json", "Pets.json",
    };

    [Fact]
    public void Native_matches_legacy_on_newest_live_archive()
    {
        var data = DataDir();
        if (data == null) return; // no local _DATA — nothing to compare (see class remarks)

        var configPath = DumperService.SelectNewestConfigArchive(Path.Combine(data, "C"));
        Assert.NotNull(configPath);

        MetaplayCore.Initialize();

        // Both globals below are process-wide. The collection keeps the other game-state tests from
        // running at the same time; restoring the previous values in the finally keeps this test from
        // leaking a loaded language (or config) into whatever runs after it in the same process.
        var previousLanguage = MetaplaySDK.ActiveLanguage;
        var previousConfig = ClientGlobal.SharedGameConfig;
        try
        {
            // Localization mirrors the app: dumped names come out localized, so load the language file
            // when one is present. Both engines see the same state either way.
            var langPath = FirstFileOrNull(Path.Combine(data, "L"));
            if (langPath != null)
                MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(
                    ContentHash.ParseString(Path.GetFileName(langPath)), File.ReadAllBytes(langPath));

            var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath!));
            var master = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
            ClientGlobal.SharedGameConfig = master; // several dumpers resolve MetaRefs through the global

            // Same %TEMP%\mmwt_compare\… tree the harness writes to, so both parity runs are cleaned up together.
            var tmp = Path.Combine(Path.GetTempPath(), "mmwt_compare", "parity");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);

            var legacy = DumpEngineFactory.Create("Legacy");
            var native = DumpEngineFactory.Create("Native");
            Assert.Equal(DumperEngineKind.Legacy, legacy.Kind);
            Assert.Equal(DumperEngineKind.Native, native.Kind);

            foreach (var file in ComparedFiles)
            {
                var legacyPath = Path.Combine(tmp, "legacy", file);
                var nativePath = Path.Combine(tmp, "native", file);
                Run(legacy, master, legacyPath, file);
                Run(native, master, nativePath, file);

                var expected = File.ReadAllBytes(legacyPath);
                var actual = File.ReadAllBytes(nativePath);
                Assert.True(expected.AsSpan().SequenceEqual(actual),
                    $"{file} differs: legacy {expected.Length} B, native {actual.Length} B ({legacyPath} vs {nativePath})");
            }
        }
        finally
        {
            MetaplaySDK.ActiveLanguage = previousLanguage;
            ClientGlobal.SharedGameConfig = previousConfig;
        }
    }

    private static void Run(IDumpEngine engine, SharedGameConfig config, string path, string file)
    {
        switch (file)
        {
            case "chain_item_odds.json": engine.DumpChains(config, path); break;
            case "areas.json": engine.DumpAreas(config, path); break;
            case "events.json": engine.DumpEvents(config, path, EventFilters.All); break;
            case "card_collection.json": engine.DumpCardCollection(config, path); break;
            case "dialogues.json": engine.DumpDialogues(config, path); break;
            case "Pets.json": engine.DumpPets(config, path); break;
            default: throw new ArgumentOutOfRangeException(nameof(file), file, "no dump call mapped");
        }
    }

    /// <summary>
    /// <c>MMWT_DATA</c> wins (same override as the harness); otherwise the repository copy under
    /// <c>bin\Debug\&lt;tfm&gt;\win-x64\_DATA</c>. The repository root comes from the compile-time path of
    /// this file, because the test binary is built into a temp directory and cannot find it relatively.
    /// </summary>
    private static string? DataDir()
    {
        var env = Environment.GetEnvironmentVariable("MMWT_DATA");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "C"))) return env;

        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));
        var binDebug = Path.Combine(repoRoot, "bin", "Debug");
        if (!Directory.Exists(binDebug)) return null;
        foreach (var tfm in Directory.GetDirectories(binDebug))
        {
            var candidate = Path.Combine(tfm, "win-x64", "_DATA");
            if (Directory.Exists(Path.Combine(candidate, "C"))) return candidate;
        }
        return null;
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private static string? FirstFileOrNull(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir).FirstOrDefault() : null;
}
