using GameLogic.Player.Requirements;
using GameLogic.Player.Rewards;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Support;

/// <summary>
/// Routes every <see cref="PlayerRequirement"/> through <see cref="RequirementModel"/>, so a
/// requirement list serializes as the golden single-key shape
/// (<c>{"HotspotCompleted": "LoungeUnlock"}</c>) instead of the empty object Newtonsoft's default
/// reflection would produce for these member-less-looking classes.
/// </summary>
public sealed class PlayerRequirementConverter : JsonConverter
{
    private readonly IDumpLog _log;
    private readonly MetaRefSkip _refSkip;
    private readonly Dictionary<string, int> _unsupportedCounts = new(StringComparer.Ordinal);

    /// <param name="refSkip">Which references a multi-member payload drops - see <see cref="MetaObjectPayload"/>.</param>
    public PlayerRequirementConverter(IDumpLog? log = null, MetaRefSkip refSkip = MetaRefSkip.EmptyKey)
    {
        _log = log ?? ConsoleDumpLog.Instance;
        _refSkip = refSkip;
    }

    /// <summary>
    /// How many requirements this instance wrote as a bare <c>{}</c>, per subtype name. One
    /// converter instance serves one dump file, so this is a per-file tally; the dumper reports it as
    /// a single summary line (<see cref="DumpConverters.LogRequirementSummary"/>) instead of one
    /// warning per occurrence — a full dump has tens of thousands of these and they are the
    /// expected, byte-identical shape, not a fault.
    /// </summary>
    public IReadOnlyDictionary<string, int> UnsupportedCounts => _unsupportedCounts;

    public override bool CanConvert(Type objectType) => typeof(PlayerRequirement).IsAssignableFrom(objectType);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not PlayerRequirement requirement) { writer.WriteNull(); return; }
        if (RequirementModel.IsUnsupported(requirement))
        {
            // Golden writes an unrecognised requirement subtype as a bare {} — see
            // RequirementModel.UnsupportedKinds for which ones and why. Counted, not warned:
            // the tally is reported once per file by DumpConverters.LogRequirementSummary.
            var kind = requirement.GetType().Name;
            _unsupportedCounts[kind] = _unsupportedCounts.TryGetValue(kind, out var n) ? n + 1 : 1;
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }
        serializer.Serialize(writer, RequirementModel.From(requirement, _log, _refSkip));
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(PlayerRequirementConverter)} is write-only (dumper never deserializes).");
}

/// <summary>
/// Routes every <see cref="PlayerReward"/> through <see cref="RewardModel"/>, giving the golden
/// single-key shape (<c>{"RewardEnergy": {"Amount": 3, ...}}</c>).
/// </summary>
public sealed class PlayerRewardConverter : JsonConverter
{
    private readonly IDumpLog _log;
    private readonly MetaRefSkip _refSkip;

    /// <param name="refSkip">Which references the payload drops - see <see cref="MetaObjectPayload"/>.</param>
    public PlayerRewardConverter(IDumpLog? log = null, MetaRefSkip refSkip = MetaRefSkip.EmptyKey)
    {
        _log = log ?? ConsoleDumpLog.Instance;
        _refSkip = refSkip;
    }

    public override bool CanConvert(Type objectType) => typeof(PlayerReward).IsAssignableFrom(objectType);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not PlayerReward reward) { writer.WriteNull(); return; }
        serializer.Serialize(writer, RewardModel.From(reward, _log, _refSkip));
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(PlayerRewardConverter)} is write-only (dumper never deserializes).");
}
