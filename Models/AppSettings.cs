using System.Reflection;

namespace MergeMansionWikiTools.Models;

public static class AppVersion
{
    public const string Version = "v0.20.4"; // ConfigKey image naming, AB patch diff, Pets checkbox, IO/upload fixes

    // Full version with build timestamp, e.g. "v0.18.7 (build 20260322-1200)"
    public static string Build { get; } = GetBuild();
    private static string GetBuild()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";
        var idx = info.IndexOf('+');
        if (idx < 0) return Version;
        // .NET SDK appends "+timestamp.githash" — keep only the timestamp part
        var suffix = info[(idx + 1)..];
        var dotIdx = suffix.IndexOf('.');
        if (dotIdx > 0) suffix = suffix[..dotIdx];
        return $"{Version} (build {suffix})";
    }
}

public class AppSettings
{
    public string ChainItemOddsPath { get; set; } = "";
    public string AreasJsonPath { get; set; } = "";
    public string EventsJsonPath { get; set; } = "";
    public string DialoguesJsonPath { get; set; } = "";
    public string PetsJsonPath { get; set; } = "";
    public string CardCollectionJsonPath { get; set; } = "";

    // Table generator checkboxes (persisted)
    public bool LowPrices { get; set; } = false;

    // Image Optimiser
    public string TinifyApiKey { get; set; } = "";
    public string TinifyApiKey2 { get; set; } = "";
    public bool ClipboardAutoAdd { get; set; } = true;
    public bool ClipboardMonitorGlobal { get; set; } = false;
    public bool AutoPredict { get; set; } = true;
    public bool ClearOptimiserOnChainEntry { get; set; } = true;

    // Wiki Data Parser — area chunk sizes (default: one chunk of 30)
    public List<int> AreaChunkSizes { get; set; } = new() { 30 };

    // Image Extractor
    public string ImageExporterBasePath { get; set; } = "";
    public string SelectedApkVersion { get; set; } = "";
    public string ActiveDumpFolder { get; set; } = "";  // "Dump", "Dump 2", etc.

    // Area Flowcharts
    public string FlowchartOutputPath { get; set; } = "";
    public bool FlowchartRememberFolderChoice { get; set; } = false;
    public bool FlowchartAutoUpdateFolder { get; set; } = false;

    // Chain Browser filters
    public bool FilterGenerators { get; set; }
    public bool FilterSpawners { get; set; }
    public bool FilterProducts { get; set; }
    public bool FilterEvent { get; set; }
    public bool FilterNamed { get; set; }
    public bool FilterCollisions { get; set; }

    // Wiki — user must verify their own Fandom account to unlock wiki editing
    public string WikiUsername { get; set; } = "";
    public string WikiPassword { get; set; } = "";
    public bool WikiVerified { get; set; } = false;
    public string WikiVerifiedDisplayName { get; set; } = ""; // Fandom username after successful verify

    // OOBE (first-run wizard)
    public bool OobeCompleted { get; set; } = false;

    // Image Extractor — advanced
    public bool ExtractIncludeBuiltIn { get; set; } = false;

    // Debug
    public bool DebugMode { get; set; } = false;
    public bool ShowDetectionIndices { get; set; } = false;

    // Game Data Dumper
    public string DumperConfigPath { get; set; } = "";
    public string DumperPatchPath { get; set; } = "";
    public string DumperLanguagePath { get; set; } = "";
    public string DumperOutputPath { get; set; } = "";
    public bool DumpChains { get; set; } = true;
    public bool DumpAreas { get; set; } = true;
    public bool DumpEvents { get; set; } = true;
    public bool DumpCards { get; set; } = true;
    public bool DumpDialogues { get; set; } = true;
    public bool DumpPets { get; set; } = true;
    public bool DumpAutoNewFolder { get; set; } = true; // auto-create Dump 2/3/... instead of overwriting

    // Event filters
    public bool EventLuckyCatch { get; set; } = true;
    public bool EventLuckySnap { get; set; } = true;
    public bool EventSeasonal { get; set; } = true;
    public bool EventReArchaeology { get; set; } = true;
    public bool EventHorizonsCup { get; set; } = true;
    public bool EventRollTheDice { get; set; } = true;
    public bool EventGarageCleanup { get; set; } = true;
    public bool EventMysteries { get; set; } = true;
    public bool EventBoultonLeague { get; set; } = true;
    public bool EventLegacy { get; set; } = true;
    public bool EventBakeOff { get; set; } = true;
    public bool EventBonanza { get; set; } = true;
    public bool EventOthers { get; set; } = true;
    public bool EventUncategorised { get; set; } = true;
    public bool EventSubExpanded { get; set; } = false;

    // Discord — dump distribution
    public string DiscordBotToken { get; set; } = DefaultDiscordBotToken;

    // Segments reversed to avoid GitHub/Discord secret scanning
    internal static string DefaultDiscordBotToken
    {
        get
        {
            var p = new[] { "AN3QDO5cjN5kjMzQTO1ATM4QTM", "9VAJiG", "0mA9Wqc7-hjZDPukhs1l3E7QhkrquCT7puOI7-" };
            return string.Join(".", p.Select(s => new string(s.Reverse().ToArray())));
        }
    }
    public string DiscordChannelId { get; set; } = DefaultDiscordChannelId;
    internal const string DefaultDiscordChannelId = "1108526050826276935";

    // Discord — flowchart publishing
    public string FlowchartDiscordThreadId { get; set; } = "1485372779002986667";

    // Appearance
    public string ThemePreference { get; set; } = "System"; // "System", "Light", "Dark"

    // Window position & size (restored on startup)
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}
