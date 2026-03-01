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
        AppSettings settings;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
            }
        }
        catch
        {
            settings = new AppSettings();
        }

        // Apply defaults for fields added after initial save
        ApplyDefaults(settings);
        return settings;
    }

    private static void ApplyDefaults(AppSettings s)
    {
        // Auto-skip OOBE for existing users who already have data configured
        if (!s.OobeCompleted && !string.IsNullOrEmpty(s.ChainItemOddsPath))
            s.OobeCompleted = true;
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
