using System.IO;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper;
using Upstream = merge_mansion_dumper.Dumper;   // the ONLY using of the upstream namespace in the app

namespace MergeMansionWikiTools.Services.Dumping;

/// <summary>
/// Adapter over External/merge-mansion-dumper. Every dump the app performs goes through
/// <see cref="IDumpEngine"/>; this implementation simply forwards to the upstream dumper classes so
/// the Legacy path stays byte-for-byte what it always was. Kept until phase 2 removes External/.
/// </summary>
internal sealed class LegacyDumpEngine : IDumpEngine
{
    public DumperEngineKind Kind => DumperEngineKind.Legacy;

    public DumpOutput DumpChains(SharedGameConfig config, string path, bool dropsAsPercent = true, string? baseline = null)
    {
        var d = new Upstream.MergeChainDumper(dropsAsPercent);
        return Run(path, baseline, p => d.WriteJson(p, config), (p, b) => d.WriteJsonIfDifferent(p, config, b));
    }

    public DumpOutput DumpAreas(SharedGameConfig config, string path, string? baseline = null)
    {
        var d = new Upstream.AreaDumper();
        return Run(path, baseline, p => d.WriteJson(p, config), (p, b) => d.WriteJsonIfDifferent(p, config, b));
    }

    public DumpOutput DumpEvents(SharedGameConfig config, string path, EventFilters filters, string? baseline = null)
    {
        var d = new Upstream.EventDumper(MapFilters(filters));
        return Run(path, baseline, p => d.WriteJson(p, config), (p, b) => d.WriteJsonIfDifferent(p, config, b));
    }

    public DumpOutput DumpCardCollection(SharedGameConfig config, string path)
        => new(new Upstream.CardCollectionDumper().WriteJson(path, config), true);

    public DumpOutput DumpDialogues(SharedGameConfig config, string path)
        => new(new Upstream.DialogueDumper().WriteJson(path, config), true);

    public IReadOnlyList<(string Section, string Path)> DumpExperimental(SharedGameConfig config, string dir)
        => new Upstream.ExperimentalDumper().WriteIndividualFiles(dir, config);

    public string DumpPets(SharedGameConfig config, string path)
    {
        Upstream.ExperimentalDumper.WritePetsJson(path, config);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Shared write path for the three dumpers that support baseline comparison. Without a baseline
    /// we always write; with one, WriteJsonIfDifferent suppresses the file on a byte-identical match
    /// and the JSON text is re-read only when something was actually written.
    /// </summary>
    private static DumpOutput Run(
        string path,
        string? baseline,
        Func<string, string> writeJson,
        Func<string, string, bool> writeJsonIfDifferent)
    {
        if (baseline == null) return new(writeJson(path), true);
        var written = writeJsonIfDifferent(path, baseline);
        return new(written ? File.ReadAllText(path) : baseline, written);
    }

    /// <summary>Map by member name so our bit values never have to match upstream's.</summary>
    internal static Upstream.EventFilters MapFilters(EventFilters f)
    {
        if (f == EventFilters.All) return Upstream.EventFilters.All;
        Upstream.EventFilters r = Upstream.EventFilters.None;
        foreach (EventFilters v in Enum.GetValues(typeof(EventFilters)))
        {
            if (v == EventFilters.None || v == EventFilters.All || !f.HasFlag(v)) continue;
            if (Enum.TryParse<Upstream.EventFilters>(v.ToString(), out var u)) r |= u;
            else AppLogger.Warn($"[WARN] EventFilters.{v} has no upstream equivalent — ignored");
        }
        return r;
    }
}
