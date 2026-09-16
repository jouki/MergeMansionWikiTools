using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Support;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// The converter stack every main dump file shares. Each dumper differs only in the file-specific
/// serializer(s) it puts in FRONT of that stack (Newtonsoft picks the first converter whose
/// <c>CanConvert</c> matches, so order is part of the contract, not a detail), which is why this is
/// a factory taking the leading converters rather than a constant.
/// </summary>
public static class DumpConverters
{
    /// <summary>
    /// Builds the settings for a main dump file: <paramref name="leading"/> first, then the shared
    /// tail in the exact order below. <paramref name="payloadRefSkip"/> is the reference rule the
    /// requirement/reward payloads use (see <see cref="MetaObjectPayload"/>): <c>EmptyKey</c> for the
    /// chain and area dumps, <c>Unresolved</c> for the event dump.
    /// <list type="number">
    /// <item><description><c>PlayerRequirementConverter</c> / <c>PlayerRewardConverter</c> — the
    /// single-key <c>{Kind: payload}</c> union shapes.</description></item>
    /// <item><description><see cref="ConfigDefinitionConverter"/> — <c>HotspotDef</c>/<c>ItemDef</c>
    /// keys as their resolved names. Needs the config, which is why this factory takes one.</description></item>
    /// <item><description><see cref="MetaRefConverter"/> — references as their key.</description></item>
    /// <item><description><see cref="MetaMathConverter"/> with <c>ceiling: true</c>, then the
    /// duration/time converters and the three activable/calendar ones. Those last three matter only
    /// to the files that contain a <c>MetaActivableParams</c> block (events, card collection,
    /// experimental offers) — no <c>MetaActivableSpec</c>, <c>MetaCalendarDateTime</c> or
    /// <c>MetaCalendarPeriod</c> occurs anywhere in a chain or area dump, which is why they can live
    /// in the shared tail rather than being passed in per dumper.</description></item>
    /// <item><description><see cref="HotspotAwareStringEnumConverter"/> before the plain
    /// <c>StringEnumConverter</c>, so <c>HotspotId</c> resolves through the runtime name map and
    /// every other enum still writes as a string.</description></item>
    /// </list>
    /// </summary>
    public static JsonSerializerSettings MainFileSettings(SharedGameConfig config, IDumpLog log,
        MetaRefSkip payloadRefSkip, params JsonConverter[] leading)
    {
        var converters = new List<JsonConverter>(leading)
        {
            new PlayerRequirementConverter(log, payloadRefSkip),
            new PlayerRewardConverter(log, payloadRefSkip),
            new ConfigDefinitionConverter(config, log),
            new MetaRefConverter(),
            new MetaMathConverter(ceiling: true),
            new MetaDurationConverter(),
            new MetaTimeConverter(),
            new MetaActivableSpecConverter(log),
            new MetaCalendarDateTimeConverter(),
            new MetaCalendarPeriodConverter(),
            new HotspotAwareStringEnumConverter(),
            new StringEnumConverter(),
        };
        return DumpJson.CreateSettings(converters);
    }

    /// <summary>
    /// Reports, as ONE <c>[INFO]</c> line, how many requirements <paramref name="settings"/>'
    /// <see cref="PlayerRequirementConverter"/> wrote as a bare <c>{}</c> while serializing
    /// <paramref name="fileLabel"/> — silent when there were none. Call it right after
    /// <see cref="DumpJson.Serialize"/>. This replaces a per-occurrence warning: those kinds are the
    /// expected, byte-identical shape (see <c>RequirementModel.UnsupportedKinds</c>), and a real
    /// events dump contains ~1 700 of them, times one file per patch label.
    /// </summary>
    public static void LogRequirementSummary(JsonSerializerSettings settings, IDumpLog log, string fileLabel)
    {
        var converter = settings.Converters.OfType<PlayerRequirementConverter>().FirstOrDefault();
        if (converter == null || converter.UnsupportedCounts.Count == 0) return;
        var total = converter.UnsupportedCounts.Values.Sum();
        var kinds = string.Join(", ", converter.UnsupportedCounts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} ×{kv.Value}"));
        log.Info($"{fileLabel}: {total} requirement(s) written as {{}} — subtypes the reference dumper does not model ({kinds})");
    }
}
