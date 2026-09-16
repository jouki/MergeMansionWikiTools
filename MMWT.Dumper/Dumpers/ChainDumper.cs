using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;

namespace MergeMansionWikiTools.Dumper.Dumpers;

/// <summary>
/// Produces <c>chain_item_odds.json</c>: every merge chain in the config, each step's full item
/// definition, and the drop odds of everything those items produce.
/// </summary>
public sealed class ChainDumper
{
    private readonly IDumpLog _log;

    public ChainDumper(IDumpLog? log = null) => _log = log ?? ConsoleDumpLog.Instance;

    /// <param name="dropsAsPercent">
    /// Write producer odds as percentages (0–100) rather than fractions. The app always asks for
    /// percentages; the flag exists because the numbers are consumed directly by the wiki tables.
    /// </param>
    public string Dump(SharedGameConfig config, bool dropsAsPercent)
    {
        ArgumentNullException.ThrowIfNull(config);
        var chains = config.MergeChains?.EnumerateAll().Select(kv => kv.Value).ToList()
            ?? throw new InvalidOperationException("MergeChains is null — config import may have failed.");

        var settings = DumpConverters.MainFileSettings(config, _log, MetaRefSkip.EmptyKey,
            new ChainSerializer(config, dropsAsPercent, _log));

        _log.Trace($"Chains: {chains.Count}");
        var json = DumpJson.Serialize(chains, config.ArchiveCreatedAt, DumpFileKind.Main, settings);
        DumpConverters.LogRequirementSummary(settings, _log, "chain_item_odds.json");
        return json;
    }
}
