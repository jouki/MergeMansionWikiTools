using MergeMansionWikiTools.Dumper;
using MergeMansionWikiTools.Dumper.Core;

namespace MergeMansionWikiTools.Services.Dumping;

/// <summary>Resolves the AppSettings.DumperEngine string into an engine; unknown values fall back to Legacy.</summary>
internal static class DumpEngineFactory
{
    /// <param name="warn">
    /// The caller's progress sink. Besides the unknown-engine warning below it also becomes the
    /// Native engine's log, so the dumper's own <c>[WARN]</c>/<c>[ERROR]</c> lines reach the dump
    /// window instead of a console the app never shows. Without a sink the engine keeps its default
    /// <see cref="ConsoleDumpLog"/> (harness/tests).
    /// </param>
    public static IDumpEngine Create(string? setting, Action<string>? warn = null)
    {
        switch ((setting ?? "Legacy").Trim())
        {
            case "Native": return new NativeDumpEngine(warn == null ? null : new DelegatingDumpLog(warn));
            case "Legacy": return new LegacyDumpEngine();
            default:
                warn?.Invoke($"[WARN] Unknown DumperEngine '{setting}', using Legacy");
                return new LegacyDumpEngine();
        }
    }
}
