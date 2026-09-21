using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GameLogic.Config;
using MergeMansionWikiTools.Services;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Localization;
using Metaplay.Unity;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// Loads the newest <c>SharedGameConfig</c> out of the local <c>_DATA</c> pull, for the tests that
/// assert against REAL game data rather than hand-built objects.
/// <para>
/// <c>_DATA</c> is not in the repository and cannot be regenerated on a machine without the phone
/// pull, so every entry point returns <c>null</c> instead of throwing: a test that needs it returns
/// early and the suite still passes on a bare checkout.
/// </para>
/// <para>
/// This used to be copy-pasted into every such test class. The copies had already drifted — two
/// treated a missing language file as fatal, one as optional — which is the whole argument for
/// having one of them: <see cref="Load"/> takes that as a parameter instead.
/// </para>
/// <para>
/// Callers must be in <see cref="GlobalGameStateCollection"/>: this writes
/// <c>MetaplaySDK.ActiveLanguage</c> and <c>ClientGlobal.SharedGameConfig</c>, both process-global.
/// </para>
/// </summary>
internal static class LiveConfig
{
    /// <param name="requireLanguage">
    /// When true (the default), a missing language file is treated as "no data": anything asserting
    /// on a localized name has nothing to compare against and should return early rather than
    /// assert on raw keys.
    /// </param>
    public static SharedGameConfig? Load(bool requireLanguage = true)
    {
        var data = DataDir();
        if (data == null) return null;

        var configPath = DumperService.SelectNewestConfigArchive(Path.Combine(data, "C"));
        if (configPath == null) return null;

        MetaplayCore.Initialize();
        var langPath = FirstFileOrNull(Path.Combine(data, "L"));
        if (langPath == null && requireLanguage) return null;
        if (langPath != null)
            MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(
                ContentHash.ParseString(Path.GetFileName(langPath)), File.ReadAllBytes(langPath));

        var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
        var config = (SharedGameConfig)GameConfigFactory.Instance
            .ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
        ClientGlobal.SharedGameConfig = config;
        return config;
    }

    /// <summary>
    /// The <c>_DATA</c> directory: <c>MMWT_DATA</c> if set, else the one under the app's build
    /// output, whichever target framework produced it.
    /// </summary>
    private static string? DataDir()
    {
        var env = Environment.GetEnvironmentVariable("MMWT_DATA");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "C"))) return env;

        // ThisFile() is <repo>/MergeMansionWikiTools.Tests/NativeDumper/LiveConfig.cs — two levels down.
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
