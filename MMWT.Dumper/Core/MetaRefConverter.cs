using Metaplay.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Newtonsoft converter for Metaplay's <see cref="IMetaRef"/> reference wrapper: writes the
/// referenced key (<see cref="IMetaRefBase.KeyObject"/>, common to every closed
/// <c>MetaRef&lt;TItem&gt;</c> generic via the <see cref="IMetaRef"/>/<see cref="IMetaRefBase"/>
/// interface) instead of the wrapper object.
/// <para>
/// Resolution state is irrelevant: the key is written either way. Golden proof — under the
/// <c>IdealFTUEPhase1_B</c> patch the reward <c>RewardCollectibleBoardEventProgress</c> still
/// carries <c>"EventInfoRef": "CBE_JoysOfTheSea2023"</c> even though that event no longer resolves
/// in the patched config. What legacy really cannot survive is a *getter* that dereferences an
/// unresolved reference and throws, which <see cref="MetaObjectWriter"/> handles by catching.
/// </para>
/// </summary>
public sealed class MetaRefConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => typeof(IMetaRef).IsAssignableFrom(objectType);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not IMetaRef metaRef)
        {
            writer.WriteNull();
            return;
        }

        serializer.Serialize(writer, metaRef.KeyObject);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException("MetaRefConverter is write-only (dumper never deserializes).");
}
