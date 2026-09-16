using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// A JSON object whose key order is exactly the insertion order, guaranteed by contract rather than
/// by a dictionary implementation detail. <c>events.json</c>'s top level is 36 categories in a fixed
/// order, and several of its element shapes are hand-built objects whose golden order is neither
/// TagId nor declaration order, so both need a container that cannot silently reorder.
/// <para>
/// Unlike a <c>Dictionary&lt;string, object&gt;</c>, a null value is written as JSON <c>null</c>
/// rather than being dropped by <c>NullValueHandling.Ignore</c> — golden keeps
/// <c>"ObjectiveParameter": null</c>, <c>"Recipes": null</c> and friends. Use <see cref="AddIfNotNull"/>
/// where the golden omits a null instead.
/// </para>
/// </summary>
[JsonConverter(typeof(OrderedMapConverter))]
public sealed class OrderedMap
{
    private readonly List<KeyValuePair<string, object?>> _entries = new();

    public IReadOnlyList<KeyValuePair<string, object?>> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>Appends a key/value pair; a null value is written as JSON <c>null</c>.</summary>
    public OrderedMap Add(string key, object? value)
    {
        _entries.Add(new KeyValuePair<string, object?>(key, value));
        return this;
    }

    /// <summary>Appends the pair only when <paramref name="value"/> is not null.</summary>
    public OrderedMap AddIfNotNull(string key, object? value)
    {
        if (value != null) _entries.Add(new KeyValuePair<string, object?>(key, value));
        return this;
    }
}

/// <summary>Write-only converter backing <see cref="OrderedMap"/>.</summary>
public sealed class OrderedMapConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(OrderedMap);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not OrderedMap map) { writer.WriteNull(); return; }

        writer.WriteStartObject();
        foreach (var (key, item) in map.Entries)
        {
            writer.WritePropertyName(key);
            if (item is null) writer.WriteNull(); else serializer.Serialize(writer, item);
        }
        writer.WriteEndObject();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(OrderedMapConverter)} is write-only (dumper never deserializes).");
}
