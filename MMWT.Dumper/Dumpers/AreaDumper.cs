using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;

namespace MergeMansionWikiTools.Dumper.Dumpers;

/// <summary>
/// Produces <c>areas.json</c>: every area in the config with its localized name, its whole task
/// (hotspot) list expanded in place, and a Graphviz rendering of the task unlock graph.
/// </summary>
public sealed class AreaDumper
{
    private readonly IDumpLog _log;

    public AreaDumper(IDumpLog? log = null) => _log = log ?? ConsoleDumpLog.Instance;

    public string Dump(SharedGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var areas = config.Areas?.EnumerateAll().Select(kv => kv.Value).ToList()
            ?? throw new InvalidOperationException("Areas is null — config import may have failed.");

        var settings = DumpConverters.MainFileSettings(config, _log, MetaRefSkip.EmptyKey,
            new AreaSerializer(config, _log),
            new DirectorActionSerializer(config, _log),
            // Hotspot director actions can carry a whole ItemDefinition (ReplaceItemsOnBoard's
            // ReplacementItem), and golden writes it in exactly the chain dump's item shape — down
            // to the computed SellCoins/trade values and the producer wrappers inside its features.
            // Reusing ChainSerializer is therefore not an optimisation but the specification.
            new ChainSerializer(config, dropsAsPercent: true, _log));

        _log.Trace($"Areas: {areas.Count}");
        var json = DumpJson.Serialize(areas, config.ArchiveCreatedAt, DumpFileKind.Main, settings);
        DumpConverters.LogRequirementSummary(settings, _log, "areas.json");
        return json;
    }
}
