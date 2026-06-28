using System;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class EventScheduleRunTests
{
    [Fact]
    public void EventScheduleRun_IdenticalTo_defaultsNull_andSettable()
    {
        var r = new EventScheduleRun(new DateTime(2026, 6, 25), TimeSpan.FromDays(3), "x");
        Assert.Null(r.IdenticalTo);
        Assert.Equal("18.10.2024", (r with { IdenticalTo = "18.10.2024" }).IdenticalTo);
    }
}
