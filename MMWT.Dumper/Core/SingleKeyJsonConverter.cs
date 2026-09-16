using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Implemented by discriminated-union wrapper models (e.g. <c>RequirementModel</c>,
/// <c>RewardModel</c> in <c>Support/</c>) whose golden dump shape is a single-key JSON object:
/// <c>{ "&lt;Kind&gt;": &lt;Payload&gt; }</c> — for example <c>{"RewardItem": {"Amount": 1, ...}}</c>
/// or <c>{"CardStack": "Lounge2"}</c>.
/// </summary>
public interface ISingleKeyJson
{
    /// <summary>The wrapper's sole JSON property name (the requirement/reward subtype discriminator).</summary>
    string Kind { get; }

    /// <summary>
    /// The wrapper's sole JSON property value. When <c>null</c>, the whole object collapses to
    /// the bare <see cref="Kind"/> string instead of <c>{Kind: null}</c> — this matches
    /// zero-member requirement subtypes in the golden dump (e.g. <c>HasAnyPetRequirement</c>
    /// appears as the literal list element <c>"HasAnyPet"</c>, not an object).
    /// </summary>
    object? Payload { get; }
}

/// <summary>
/// Writes any <see cref="ISingleKeyJson"/> as <c>{ Kind: Payload }</c>, or as the bare
/// <see cref="ISingleKeyJson.Kind"/> string when <see cref="ISingleKeyJson.Payload"/> is null.
/// Write-only: the native dumper never deserializes its own output.
/// </summary>
public sealed class SingleKeyJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => typeof(ISingleKeyJson).IsAssignableFrom(objectType);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not ISingleKeyJson m)
        {
            writer.WriteNull();
            return;
        }

        if (m.Payload is null)
        {
            writer.WriteValue(m.Kind);
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName(m.Kind);
        serializer.Serialize(writer, m.Payload);
        writer.WriteEndObject();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(SingleKeyJsonConverter)} is write-only (dumper never deserializes).");
}
