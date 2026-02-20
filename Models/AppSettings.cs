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

    // Image Splitter
    public string TinifyApiKey { get; set; } = "";
    public string TinifyApiKey2 { get; set; } = "";

    // Wiki Data Parser — area chunk sizes (default: one chunk of 40)
    public List<int> AreaChunkSizes { get; set; } = new() { 40 };

    // Image Extractor
    public string ImageExporterBasePath { get; set; } = "";
    public string ImageExporterCustomOutputPath { get; set; } = "";
    public string BundleDownloaderCustomOutputPath { get; set; } = "";

    // Appearance
    public string ThemePreference { get; set; } = "System"; // "System", "Light", "Dark"
}
