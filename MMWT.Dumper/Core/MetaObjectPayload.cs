using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Wraps a <c>[MetaMember]</c>-tagged object so that serializing it (through the shared
/// JsonSerializerSettings every dumper uses) writes its members via <see cref="MetaObjectWriter"/>
/// instead of Newtonsoft's default reflection — the same TagId ordering, reference handling and
/// getter-exception logging every other dump path already relies on.
/// <para>
/// The reference rule is per instance and defaults to <see cref="MetaRefSkip.EmptyKey"/>: the area
/// dump's "drop unresolved references" policy stops at the hotspot/area objects and does NOT reach
/// inside a requirement or reward payload, which keeps those payloads byte-identical between
/// <c>areas.json</c> and <c>chain_item_odds.json</c>. <c>events.json</c> is the exception and asks
/// for <see cref="MetaRefSkip.Unresolved"/>: under the <c>SPNoDecorations_01_B</c> patch its
/// <c>RewardDecoration</c> payloads lose the <c>DecorationRef</c> member entirely rather than
/// keeping the key of a decoration that patch removed.
/// </para>
/// </summary>
[JsonConverter(typeof(MetaObjectPayloadConverter))]
public sealed class MetaObjectPayload
{
    public object Value { get; }
    public IDumpLog Log { get; }
    public MetaRefSkip RefSkip { get; }

    public MetaObjectPayload(object value, IDumpLog log, MetaRefSkip refSkip = MetaRefSkip.EmptyKey)
    {
        Value = value;
        Log = log;
        RefSkip = refSkip;
    }
}

/// <summary>Write-only converter backing <see cref="MetaObjectPayload"/>.</summary>
public sealed class MetaObjectPayloadConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(MetaObjectPayload);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not MetaObjectPayload payload)
        {
            writer.WriteNull();
            return;
        }

        MetaObjectWriter.WriteObject(writer, payload.Value, serializer, payload.Log, refSkip: payload.RefSkip);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(MetaObjectPayloadConverter)} is write-only (dumper never deserializes).");
}
