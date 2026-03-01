using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace MergeMansionWikiTools.Services;

public static class AppLogger
{
    private static readonly ConcurrentQueue<string> _buffer = new();
    private static Timer? _flushTimer;
    private static string? _logPath;
    private static readonly object _writeLock = new();

    private const int MaxLogFiles = 5;
    private const int FlushIntervalMs = 250;

    public static void Init()
    {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        _logPath = Path.Combine(logDir, $"app_{DateTime.Now:yyyy-MM-dd}.log");

        // Auto-cleanup old log files
        CleanupOldLogs(logDir);

        _flushTimer = new Timer(_ => FlushBuffer(), null, FlushIntervalMs, FlushIntervalMs);

        Info("AppLogger initialized");
    }

    public static void Info(string msg) => Enqueue("INFO", msg);

    public static void Warn(string msg) => Enqueue("WARN", msg);

    public static void Error(string msg, Exception? ex = null)
    {
        if (ex != null)
            Enqueue("ERROR", $"{msg} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        else
            Enqueue("ERROR", msg);
    }

    /// <summary>
    /// Returns a disposable that logs elapsed time on Dispose.
    /// Usage: using var _ = AppLogger.Timed("Operation");
    /// </summary>
    public static TimedOperation Timed(string operation) => new(operation);

    /// <summary>
    /// Synchronous flush — call in App.OnExit to ensure all buffered messages are written.
    /// </summary>
    public static void Flush()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        FlushBuffer();
        Info("AppLogger flushed — shutting down");
        FlushBuffer();
    }

    private static void Enqueue(string level, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level} {msg}";
        _buffer.Enqueue(line);
    }

    private static void FlushBuffer()
    {
        if (_logPath == null || _buffer.IsEmpty) return;

        var lines = new List<string>();
        while (_buffer.TryDequeue(out var line))
            lines.Add(line);

        if (lines.Count == 0) return;

        lock (_writeLock)
        {
            try
            {
                File.AppendAllLines(_logPath, lines);
            }
            catch
            {
                // Can't log a logging failure — silently drop
            }
        }
    }

    private static void CleanupOldLogs(string logDir)
    {
        try
        {
            var files = Directory.GetFiles(logDir, "app_*.log")
                .OrderByDescending(f => f)
                .Skip(MaxLogFiles)
                .ToList();

            foreach (var f in files)
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public struct TimedOperation : IDisposable
    {
        private readonly string _operation;
        private readonly Stopwatch _sw;

        public TimedOperation(string operation)
        {
            _operation = operation;
            _sw = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _sw.Stop();
            if (_sw.ElapsedMilliseconds >= 1)
                Info($"{_operation} completed in {_sw.ElapsedMilliseconds}ms");
        }
    }
}
