namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>Logging sink. Prefixes match what DumperService.ProgressTextWriter classifies ([INFO]/[WARN]/[TRACE]/[ERROR]).</summary>
public interface IDumpLog
{
    void Info(string message);
    void Warn(string message);
    void Trace(string message);
    void Error(string message);
}

public sealed class ConsoleDumpLog : IDumpLog
{
    public static readonly ConsoleDumpLog Instance = new();
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");
    public void Warn(string message) => Console.WriteLine($"[WARN] {message}");
    public void Trace(string message) => Console.WriteLine($"[TRACE] {message}");
    public void Error(string message) => Console.WriteLine($"[ERROR] {message}");
}

/// <summary>
/// Forwards the dumper's own diagnostics to a caller-supplied sink, using the SAME <c>[INFO]</c> /
/// <c>[WARN]</c> / <c>[ERROR]</c> prefixes <see cref="ConsoleDumpLog"/> writes — that is what the
/// app's progress reporter classifies on, so a Native warning ends up highlighted in the dump log
/// exactly like a Legacy one instead of disappearing into a console nobody sees.
/// <para>
/// <see cref="Trace"/> is deliberately dropped: it is the per-item detail stream (thousands of lines
/// on a full dump) and has no place in the UI progress list.
/// </para>
/// </summary>
public sealed class DelegatingDumpLog : IDumpLog
{
    private readonly Action<string> _sink;

    public DelegatingDumpLog(Action<string> sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public void Info(string message) => _sink($"[INFO] {message}");
    public void Warn(string message) => _sink($"[WARN] {message}");
    public void Trace(string message) { /* per-item detail — not surfaced in the UI */ }
    public void Error(string message) => _sink($"[ERROR] {message}");
}
