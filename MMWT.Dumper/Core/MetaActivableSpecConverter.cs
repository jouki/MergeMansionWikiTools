using Metaplay.Core.Activables;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Collapses the two activable "spec" unions — <see cref="MetaActivableLifetimeSpec"/> and
/// <see cref="MetaActivableCooldownSpec"/> — the way golden writes them: a variant with no
/// <c>[MetaMember]</c>s of its own becomes the bare variant name (<c>"ScheduleBased"</c>,
/// <c>"Forever"</c>), and a variant with exactly one becomes that member's value alone — so a fixed
/// one-day lifetime is the number <c>86400000</c>, not <c>{"Duration": 86400000}</c> and not
/// <c>{"Fixed": …}</c>.
/// <para>
/// Both shapes appear in <c>events.json</c>: 273 <c>"Lifetime": "ScheduleBased"</c> against 88
/// numeric lifetimes (Clue Rush runs one day inside a three-day schedule window), and every
/// <c>Cooldown</c> in the corpus is <c>"ScheduleBased"</c>. Without this the variants would render
/// as <c>{}</c> / <c>{"Duration": …}</c>, since Newtonsoft sees only their public members.
/// </para>
/// </summary>
public sealed class MetaActivableSpecConverter : JsonConverter
{
    private readonly IDumpLog _log;

    /// <param name="log">Diagnostics sink for the member-dump fallback below; defaults to the console.</param>
    public MetaActivableSpecConverter(IDumpLog? log = null) => _log = log ?? ConsoleDumpLog.Instance;

    public override bool CanConvert(Type objectType) =>
        typeof(MetaActivableLifetimeSpec).IsAssignableFrom(objectType)
        || typeof(MetaActivableCooldownSpec).IsAssignableFrom(objectType);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null) { writer.WriteNull(); return; }

        var members = MetaObjectWriter.Members(value);
        if (members.Count == 0)
        {
            writer.WriteValue(value.GetType().Name);
            return;
        }

        if (members.Count == 1)
        {
            serializer.Serialize(writer, members[0].Get());
            return;
        }

        // No such variant exists today; a member dump beats silently writing the type name.
        MetaObjectWriter.WriteObject(writer, value, serializer, _log);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(MetaActivableSpecConverter)} is write-only (dumper never deserializes).");
}
