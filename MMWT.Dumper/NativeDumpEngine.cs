using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Dumpers;

namespace MergeMansionWikiTools.Dumper;

/// <summary>
/// The in-process native dump engine: reads <see cref="SharedGameConfig"/> directly instead of
/// shelling out to the legacy dumper, and produces byte-identical output. Each dump file is owned
/// by its own dumper under <c>Dumpers/</c>; the methods below are thin adapters that pick the right
/// one and hand the JSON to <see cref="DumpJson"/>.
/// <para>
/// Only the three files an A/B patch can change (chains, areas, events) take a baseline and can
/// report "identical, not written"; the other four always write, which is what the legacy adapter
/// does too.
/// </para>
/// </summary>
public sealed class NativeDumpEngine : IDumpEngine
{
    private readonly IDumpLog _log;
    public NativeDumpEngine(IDumpLog? log = null) => _log = log ?? ConsoleDumpLog.Instance;
    public DumperEngineKind Kind => DumperEngineKind.Native;

    public DumpOutput DumpChains(SharedGameConfig config, string path, bool dropsAsPercent = true, string? baseline = null)
    {
        var json = new ChainDumper(_log).Dump(config, dropsAsPercent);
        return new DumpOutput(json, DumpJson.WriteIfDifferent(path, json, baseline, DumpFileKind.Main));
    }

    public DumpOutput DumpAreas(SharedGameConfig config, string path, string? baseline = null)
    {
        var json = new AreaDumper(_log).Dump(config);
        return new DumpOutput(json, DumpJson.WriteIfDifferent(path, json, baseline, DumpFileKind.Main));
    }

    public DumpOutput DumpEvents(SharedGameConfig config, string path, EventFilters filters, string? baseline = null)
    {
        var json = new EventDumper(_log).Dump(config, filters);
        return new DumpOutput(json, DumpJson.WriteIfDifferent(path, json, baseline, DumpFileKind.Main));
    }

    public DumpOutput DumpCardCollection(SharedGameConfig config, string path)
    {
        var json = new CardCollectionDumper(_log).Dump(config);
        DumpJson.Write(path, json, DumpFileKind.Main);
        return new DumpOutput(json, true);
    }

    public DumpOutput DumpDialogues(SharedGameConfig config, string path)
    {
        var json = new DialogueDumper(_log).Dump(config);
        DumpJson.Write(path, json, DumpFileKind.Main);
        return new DumpOutput(json, true);
    }

    public IReadOnlyList<(string Section, string Path)> DumpExperimental(SharedGameConfig config, string dir)
        => new ExperimentalDumper(_log).WriteIndividualFiles(dir, config);

    public string DumpPets(SharedGameConfig config, string path) => ExperimentalDumper.WritePetsJson(path, config);
}
