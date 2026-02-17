using System.IO;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Manages custom chain names stored in chain_names.txt next to the exe.
/// Format: ConfigKey=CustomName (one per line)
/// </summary>
public class ChainNameService
{
    private static readonly string FilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "chain_names.txt");

    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public ChainNameService()
    {
        Load();
    }

    public void Load()
    {
        _names.Clear();

        if (!File.Exists(FilePath)) return;

        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var idx = line.IndexOf('=');
                if (idx > 0)
                {
                    var key = line[..idx].Trim();
                    var val = line[(idx + 1)..].Trim();
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                        _names[key] = val;
                }
            }
        }
        catch { /* ignore */ }
    }

    public string? GetCustomName(string configKey)
    {
        return _names.TryGetValue(configKey, out var name) ? name : null;
    }

    public void SetCustomName(string configKey, string name)
    {
        _names[configKey] = name;
        Save();
    }

    public IReadOnlyDictionary<string, string> GetAll() => _names;

    /// <summary>Returns all known custom names (values only, distinct, sorted).</summary>
    public List<string> GetAllNames()
    {
        return _names.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList();
    }

    private void Save()
    {
        try
        {
            var lines = _names.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                              .Select(kv => $"{kv.Key}={kv.Value}");
            File.WriteAllLines(FilePath, lines);
        }
        catch { /* ignore */ }
    }
}
