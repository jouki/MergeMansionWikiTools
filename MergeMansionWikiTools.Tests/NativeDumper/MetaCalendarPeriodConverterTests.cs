using System.IO;
using System.Text;
using MergeMansionWikiTools.Dumper.Core;
using Metaplay.Core.Schedule;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// The activable-schedule period format used throughout <c>events.json</c>. Every expected string
/// below is a literal value from the newest archive's legacy dump.
/// </summary>
public class MetaCalendarPeriodConverterTests
{
    private static string Write(MetaCalendarPeriod period)
    {
        var sb = new StringBuilder();
        using var sw = new StringWriter(sb);
        using var jw = new JsonTextWriter(sw);
        JsonSerializer.Create(new JsonSerializerSettings { Converters = { new MetaCalendarPeriodConverter() } })
            .Serialize(jw, period);
        return sb.ToString();
    }

    private static MetaCalendarPeriod Period(int days = 0, int hours = 0, int minutes = 0, int seconds = 0)
        => new() { Days = days, Hours = hours, Minutes = minutes, Seconds = seconds };

    [Fact]
    public void Units_run_from_the_highest_non_zero_one_down_to_seconds()
    {
        Assert.Equal("\"4d 0h 0min 0s\"", Write(Period(days: 4)));
        Assert.Equal("\"12h 0min 0s\"", Write(Period(hours: 12)));
        Assert.Equal("\"2d 13h 0min 0s\"", Write(Period(days: 2, hours: 13)));
    }

    [Fact]
    public void An_all_zero_period_is_the_empty_string()
        => Assert.Equal("\"\"", Write(Period()));

    [Fact]
    public void Hours_carry_into_days_but_minutes_do_not_carry_into_hours()
    {
        // 98 schedules configure a 24-hour "ending soon" window and golden reads it as one day.
        Assert.Equal("\"1d 0h 0min 0s\"", Write(Period(hours: 24)));
        Assert.Equal("\"200d 0h 0min 0s\"", Write(Period(hours: 4800)));
        Assert.Equal("\"4d 5h 0min 0s\"", Write(Period(hours: 101)));
        // ...but a 300-minute preview stays 300 minutes rather than becoming five hours.
        Assert.Equal("\"300min 0s\"", Write(Period(minutes: 300)));
    }
}
