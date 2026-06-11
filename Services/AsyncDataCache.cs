namespace MergeMansionWikiTools.Services;

/// <summary>
/// Per-path cache for an async-loaded data service (areas.json, events.json, ...).
/// Thread-safe; concurrent callers requesting the same path share a single load Task
/// (dedup — the JSON is parsed exactly once). A faulted load is NOT kept — the next
/// call retries from disk.
///
/// IMPORTANT: the cache keys on path only, not on file content. When the underlying
/// file may have been rewritten in place (Game Data Dumper re-run writes to the same
/// path), call <see cref="Invalidate"/> — MainWindow does this in its Set*Path
/// methods, which the dumper invokes even when the path itself is unchanged.
/// </summary>
public sealed class AsyncDataCache<T> where T : class
{
    private readonly Func<string, Task<T>> _factory;
    private readonly object _lock = new();
    private string? _path;
    private Task<T>? _task;

    public AsyncDataCache(Func<string, Task<T>> factory) => _factory = factory;

    /// <summary>
    /// Returns the cached load for <paramref name="path"/>, or starts a new one
    /// (off-thread via Task.Run, so the factory never blocks the UI thread) when the
    /// path changed, nothing is cached yet, or the previous load faulted.
    /// </summary>
    public Task<T> GetOrLoadAsync(string path)
    {
        lock (_lock)
        {
            if (_task != null && _path == path && !_task.IsFaulted) return _task;
            _path = path;
            _task = Task.Run(() => _factory(path));
            return _task;
        }
    }

    /// <summary>Drops the cached instance — the next GetOrLoadAsync re-parses from disk.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _path = null;
            _task = null;
        }
    }
}
