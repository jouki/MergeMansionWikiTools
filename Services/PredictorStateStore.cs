using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>Persists the Predictor page's user-entered state (board, progress, streak)
/// to its own JSON file next to settings.json. Never throws — corrupt/missing → empty state.</summary>
public static class PredictorStateStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>Default location: predictor_state.json next to the app (same dir as settings.json).</summary>
    public static string DefaultPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "predictor_state.json");

    public static PredictorState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PredictorState>(File.ReadAllText(path), Opts) ?? new PredictorState();
        }
        catch { /* corrupt → empty */ }
        return new PredictorState();
    }

    public static void Save(string path, PredictorState state)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(state, Opts)); }
        catch { /* non-critical */ }
    }
}
