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
}
