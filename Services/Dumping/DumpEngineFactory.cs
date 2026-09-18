using MergeMansionWikiTools.Dumper;
using MergeMansionWikiTools.Dumper.Core;

namespace MergeMansionWikiTools.Services.Dumping;

/// <summary>
/// Creates the dump engine. Since v0.24.73 there is only one: the in-house <c>MMWT.Dumper</c>. The
/// factory stays because every caller goes through it and because the engine needs its log sink
/// wired up — see <paramref name="warn"/>.
/// </summary>
internal static class DumpEngineFactory
{
    /// <param name="warn">
    /// The caller's progress sink, which becomes the engine's log so the dumper's own
    /// <c>[WARN]</c>/<c>[ERROR]</c> lines reach the dump window instead of a console the app never
    /// shows. Without a sink the engine keeps its default <see cref="ConsoleDumpLog"/>
    /// (harness/tests).
    /// </param>
    public static IDumpEngine Create(Action<string>? warn = null)
        => new NativeDumpEngine(warn == null ? null : new DelegatingDumpLog(warn));
}
