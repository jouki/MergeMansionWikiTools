namespace MergeMansionWikiTools.Models;

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

    // Wiki Data Parser — area chunk sizes (default: one chunk of 40)
    public List<int> AreaChunkSizes { get; set; } = new() { 40 };

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

    // Appearance
    public string ThemePreference { get; set; } = "System"; // "System", "Light", "Dark"
}
