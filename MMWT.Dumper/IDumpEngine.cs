using GameLogic.Config;

namespace MergeMansionWikiTools.Dumper;

public enum DumperEngineKind { Legacy, Native }

/// <summary>Which event families events.json contains. Bit values are ours; the Legacy adapter maps by member NAME.</summary>
[Flags]
public enum EventFilters
{
    None = 0,
    LuckyCatch = 1 << 0, LuckySnap = 1 << 1, Seasonal = 1 << 2, ReArchaeology = 1 << 3,
    HorizonsCup = 1 << 4, RollTheDice = 1 << 5, GarageCleanup = 1 << 6, Mysteries = 1 << 7,
    BoultonLeague = 1 << 8, Legacy = 1 << 9, Uncategorised = 1 << 10, BakeOff = 1 << 11,
    Bonanza = 1 << 12, Others = 1 << 13, SoloMilestone = 1 << 14, DailyTrades = 1 << 15,
    DailyScoop = 1 << 16, AutoMerge = 1 << 17, Shops = 1 << 18, MixABooster = 1 << 19,
    All = ~0,
}

/// <summary>Result of one dump call: the JSON text and whether a file was written (false = identical to baseline).</summary>
public readonly record struct DumpOutput(string Json, bool Written);

public interface IDumpEngine
{
    DumperEngineKind Kind { get; }
    DumpOutput DumpChains(SharedGameConfig config, string path, bool dropsAsPercent = true, string? baseline = null);
    DumpOutput DumpAreas(SharedGameConfig config, string path, string? baseline = null);
    DumpOutput DumpEvents(SharedGameConfig config, string path, EventFilters filters, string? baseline = null);
    DumpOutput DumpCardCollection(SharedGameConfig config, string path);
    DumpOutput DumpDialogues(SharedGameConfig config, string path);
    IReadOnlyList<(string Section, string Path)> DumpExperimental(SharedGameConfig config, string dir);
    string DumpPets(SharedGameConfig config, string path);
}
