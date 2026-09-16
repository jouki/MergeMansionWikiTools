using System.Collections.Generic;
using System.Linq;
using GameLogic.Player.Requirements;
using Code.GameLogic.Player.Requirements.Activables;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Support;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// Requirement kinds the reference dumper never modelled are written as <c>{}</c>. That is the
/// expected, byte-identical shape — not an anomaly — so the converter must NOT warn once per
/// occurrence (a real dump has ~31 000 of them across master + patches, which flooded the app log).
/// It counts them and the dumper reports one summary line per file.
/// </summary>
public class PlayerRequirementConverterTests
{
    private sealed class RecordingLog : IDumpLog
    {
        public readonly List<string> Info = new(), Warn = new(), Trace = new(), Error = new();
        void IDumpLog.Info(string m) => Info.Add(m);
        void IDumpLog.Warn(string m) => Warn.Add(m);
        void IDumpLog.Trace(string m) => Trace.Add(m);
        void IDumpLog.Error(string m) => Error.Add(m);
    }

    [Fact]
    public void Unsupported_kinds_are_written_as_empty_objects_without_per_occurrence_warnings()
    {
        var log = new RecordingLog();
        var settings = DumpJson.CreateSettings(new JsonConverter[] { new PlayerRequirementConverter(log) });
        var list = new List<PlayerRequirement>
        {
            new HasActivableKindActiveForDurationRequirement(),
            new HasActivableKindActiveForDurationRequirement(),
            new HasActivableKindActiveForDurationRequirement(),
        };

        var json = JsonConvert.SerializeObject(list, Formatting.None, settings);

        Assert.Equal("[{},{},{}]", json);
        Assert.Empty(log.Warn);

        DumpConverters.LogRequirementSummary(settings, log, "events.json");

        var line = Assert.Single(log.Info);
        Assert.Contains("events.json", line);
        Assert.Contains("3", line);
        Assert.Contains(nameof(HasActivableKindActiveForDurationRequirement), line);
    }

    [Fact]
    public void Summary_is_silent_when_nothing_was_written_as_empty_object()
    {
        var log = new RecordingLog();
        var settings = DumpJson.CreateSettings(new JsonConverter[] { new PlayerRequirementConverter(log) });

        DumpConverters.LogRequirementSummary(settings, log, "areas.json");

        Assert.Empty(log.Info);
        Assert.Empty(log.Warn);
    }
}
