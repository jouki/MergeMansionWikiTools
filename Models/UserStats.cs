namespace MergeMansionWikiTools.Models;

public class UserStats
{
    public int SessionCount { get; set; }
    public int DataLoads { get; set; }
    public int TablesGenerated { get; set; }
    public int InfoboxesGenerated { get; set; }
    public int LuaAreaChunksGenerated { get; set; }
    public int LuaItemsGenerated { get; set; }
    public int ImagesOptimized { get; set; }
    public int ImagesExtracted { get; set; }
    public int DialoguesExtracted { get; set; }
    public int MysteryTemplatesGenerated { get; set; }
    public int MysteryPagesPublished { get; set; }
    public int WikiImagesUploaded { get; set; }
    public DateTime FirstLaunch { get; set; } = DateTime.UtcNow;
}
