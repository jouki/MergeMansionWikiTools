using System;
using System.Collections.Generic;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class EventScheduleLuaEmitTests
{
    [Fact]
    public void Emit_includesIdenticalTo()
    {
        // Arrange: construct an EventScheduleGroup for a GC event with one run that has IdenticalTo set
        var group = new EventScheduleGroup
        {
            Name = "Spooktacular Backyard Bash",
            Category = "Garage Cleanup"
        };

        var run = new EventScheduleRun(
            Start: new DateTime(2024, 11, 1, 8, 0, 0),
            Duration: TimeSpan.FromDays(3),
            SourceId: "x",
            IdenticalTo: "18.10.2024"
        );
        group.Runs.Add(run);

        // Act
        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup> { group }, "2026-06-25T00:00:00");

        // Assert
        Assert.Contains("identicalTo = \"18.10.2024\"", lua);
        Assert.DoesNotContain("parent =", lua);
    }
}
