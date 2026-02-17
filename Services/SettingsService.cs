using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { /* Return defaults on any error */ }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* Silently fail — non-critical */ }
    }
}
