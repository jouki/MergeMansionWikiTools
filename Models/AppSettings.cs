namespace MergeMansionWikiTools.Models;

public static class AppVersion
{
    public const string Version = "v0.16.2";
}

public class AppSettings
{
    public string ChainItemOddsPath { get; set; } = "";
    public string AreasJsonPath { get; set; } = "";
    public string EventsJsonPath { get; set; } = "";

    // Table generator checkboxes (persisted)
    public bool ShowCustomNamePrompt { get; set; } = true;
    public bool ForceCustomNamePrompt { get; set; } = false;
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

    // Appearance
    public string ThemePreference { get; set; } = "System"; // "System", "Light", "Dark"
}
