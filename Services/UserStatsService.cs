using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public static class UserStatsService
{
    private static readonly string StatsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "user_stats.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static UserStats Load()
    {
        try
        {
            if (File.Exists(StatsPath))
            {
                var json = File.ReadAllText(StatsPath);
                return JsonSerializer.Deserialize<UserStats>(json, JsonOpts) ?? new UserStats();
            }
        }
        catch (Exception ex) { AppLogger.Error("UserStatsService.Load failed", ex); }
        return new UserStats();
    }

    public static void Save(UserStats stats)
    {
        try
        {
            File.WriteAllText(StatsPath, JsonSerializer.Serialize(stats, JsonOpts));
        }
        catch (Exception ex) { AppLogger.Error("UserStatsService.Save failed", ex); }
    }

    public static void Increment(Action<UserStats> update)
    {
        var stats = Load();
        update(stats);
        Save(stats);
    }
}
