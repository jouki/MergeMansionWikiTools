using Metaplay.Core.Math;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Newtonsoft converter for Metaplay's fixed-point <see cref="F32"/>/<see cref="F64"/> types:
/// writes the value's <c>Double</c> accessor, optionally rounded up to the nearest integer. Which
/// fields need the ceiling behavior (vs. the raw double) is a per-call decision made by the
/// dumper that registers this converter, hence the constructor flag rather than a hardcoded
/// policy here.
/// </summary>
public sealed class MetaMathConverter : JsonConverter
{
    private readonly bool _ceiling;

    public MetaMathConverter(bool ceiling = true)
    {
        _ceiling = ceiling;
    }

    public override bool CanConvert(Type objectType) => objectType == typeof(F32) || objectType == typeof(F64);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        double d = value switch
        {
            F32 f32 => f32.Double,
            F64 f64 => f64.Double,
            _ => throw new JsonSerializationException($"MetaMathConverter cannot convert value of type {value?.GetType()}"),
        };
        if (!_ceiling) { writer.WriteValue(d); return; }

        // Rounding a small negative up lands on IEEE negative zero, which Newtonsoft faithfully
        // writes as "-0.0"; golden has plain "0.0" there, so the sign is dropped.
        var ceiled = System.Math.Ceiling(d);
        writer.WriteValue(ceiled == 0d ? 0d : ceiled);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException("MetaMathConverter is write-only (dumper never deserializes).");
}
