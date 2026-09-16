using Metaplay.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Newtonsoft converter for <see cref="MetaDuration"/> (and <c>MetaDuration?</c>, which Newtonsoft
/// asks about by its nullable type — <c>OverrideItemFeatures.TimeContainerInitialTime</c> in
/// <c>events.json</c> is one, and without the nullable arm it would fall through to reflection and
/// write <c>{"Milliseconds": 21600000}</c>): writes the raw millisecond count as a JSON number. Without it Newtonsoft's default reflection would emit the struct's single public
/// property as an object (<c>{"Milliseconds": 60000}</c>); the golden dump has the bare number
/// (e.g. <c>"BubbleDuration": 60000</c>).
/// </summary>
public sealed class MetaDurationConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => Nullable.GetUnderlyingType(objectType) == typeof(MetaDuration) || objectType == typeof(MetaDuration);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not MetaDuration d) { writer.WriteNull(); return; }
        writer.WriteValue(d.Milliseconds);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(MetaDurationConverter)} is write-only (dumper never deserializes).");
}

/// <summary>
/// <para>
/// Newtonsoft converter for <see cref="MetaTime"/>: writes <see cref="MetaTime.ToDateTime"/> and
/// lets Newtonsoft render that <see cref="DateTime"/> with its default ISO-8601 format. The
/// resulting token matches the golden dump exactly, e.g. <c>"2024-12-01T08:00:00.001"</c>.
/// </para>
/// <para>
/// Two details make that string what it is, and both come from the game's own
/// <see cref="MetaTime"/>: its epoch is <c>1970-01-01T00:00:00.<b>001</b></c> (hence the stray
/// millisecond on every timestamp), and <see cref="MetaTime.ToDateTime"/> builds a
/// <see cref="DateTimeKind.Unspecified"/> value, so Newtonsoft appends no <c>Z</c> and trims
/// trailing zeros from the fractional part.
/// </para>
/// </summary>
public sealed class MetaTimeConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => Nullable.GetUnderlyingType(objectType) == typeof(MetaTime) || objectType == typeof(MetaTime);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not MetaTime t) { writer.WriteNull(); return; }
        writer.WriteValue(t.ToDateTime());
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(MetaTimeConverter)} is write-only (dumper never deserializes).");
}

/// <summary>
/// Newtonsoft converter for <see cref="Metaplay.Core.Schedule.MetaCalendarDateTime"/>, the wall-clock
/// start of an activable's schedule: writes <c>ToDateTime()</c> and lets Newtonsoft render that
/// <see cref="DateTime"/>, which gives golden's <c>"2024-02-09T08:00:00"</c>. The type declares no
/// <c>TypeConverter</c> at all, so without this it would render as an object of its six int members.
/// Its sibling <c>MetaCalendarPeriod</c> does declare one, but the reference API's implementation of
/// it is an empty stub, so it needs a converter of its own too — see
/// <see cref="MetaCalendarPeriodConverter"/>.
/// </summary>
public sealed class MetaCalendarDateTimeConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => Nullable.GetUnderlyingType(objectType) == typeof(Metaplay.Core.Schedule.MetaCalendarDateTime) || objectType == typeof(Metaplay.Core.Schedule.MetaCalendarDateTime);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not Metaplay.Core.Schedule.MetaCalendarDateTime t) { writer.WriteNull(); return; }
        writer.WriteValue(t.ToDateTime());
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(MetaCalendarDateTimeConverter)} is write-only (dumper never deserializes).");
}

/// <summary>
/// Newtonsoft converter for <see cref="Metaplay.Core.Schedule.MetaCalendarPeriod"/>, the
/// duration/preview/review spans of an activable's schedule. The struct declares a
/// <c>TypeConverter</c>, but the reference API's implementation of it is a stub, so Newtonsoft would
/// fall back to <c>ToString()</c> and write the type name.
/// <para>
/// Golden format: the units run from the highest NON-ZERO one down to seconds, all of them present
/// once the run has started and none of them normalized — <c>"4d 0h 0min 0s"</c>,
/// <c>"12h 0min 0s"</c>, <c>"300min 0s"</c> (five hours, written as minutes because that is what the
/// config says), and the empty string for an all-zero period. The <c>y</c>/<c>m</c> suffixes are the
/// natural continuation of that pattern but have no witness in the corpus: no schedule in any of the
/// six live archives uses years or months.
/// </para>
/// </summary>
public sealed class MetaCalendarPeriodConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => Nullable.GetUnderlyingType(objectType) == typeof(Metaplay.Core.Schedule.MetaCalendarPeriod) || objectType == typeof(Metaplay.Core.Schedule.MetaCalendarPeriod);

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not Metaplay.Core.Schedule.MetaCalendarPeriod p) { writer.WriteNull(); return; }

        var parts = new List<string>(6);
        void Unit(int amount, string suffix)
        {
            if (parts.Count == 0 && amount == 0) return;
            parts.Add(amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix);
        }

        // Hours carry into days — and ONLY hours do. A 24-hour "ending soon" window reads
        // "1d 0h 0min 0s" (98 schedules), a 4800-hour one "200d 0h 0min 0s", but a 300-minute
        // preview stays "300min 0s" rather than becoming five hours, so minutes and seconds are
        // written exactly as configured.
        Unit(p.Years, "y");
        Unit(p.Months, "m");
        Unit(p.Days + p.Hours / 24, "d");
        Unit(p.Hours % 24, "h");
        Unit(p.Minutes, "min");
        Unit(p.Seconds, "s");
        writer.WriteValue(string.Join(" ", parts));
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(MetaCalendarPeriodConverter)} is write-only (dumper never deserializes).");
}
