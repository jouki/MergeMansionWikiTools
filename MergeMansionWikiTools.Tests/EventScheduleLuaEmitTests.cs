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

    [Fact]
    public void Emit_includesParent_forGarageCleanup()
    {
        // A Garage Cleanup whose window overlaps two seasonal events must carry an explicit
        // `parent` so Module:Events nests it under the right one instead of date-overlap guessing.
        var group = new EventScheduleGroup
        {
            Name = "Legacy Lane Garage Cleanup",
            Category = "Garage Cleanup",
            Parent = "Legacy Lane"
        };
        group.Runs.Add(new EventScheduleRun(
            Start: new DateTime(2026, 7, 26, 8, 0, 0),
            Duration: TimeSpan.FromDays(3),
            SourceId: "GC_AmeliaBoulton2024",
            Parent: "Legacy Lane"));

        var lua = new LuaGeneratorService().GenerateEventScheduleLua(new List<EventScheduleGroup> { group }, "2026-06-25T00:00:00");

        Assert.Contains("parent = \"Legacy Lane\",", lua);
    }

    [Fact]
    public void Emit_headerLinesFirst_withGameVersion()
    {
        // createdAt/mmwtVersion (+ gameVersion) must be the very FIRST lines of the module:
        // the wiki-merge readers (WikiMappingService.ExtractCreatedAt) parse them from line 1.
        var lua = new LuaGeneratorService().GenerateEventScheduleLua(
            new List<EventScheduleGroup>(), Array.Empty<AutoMergeWindow>(),
            "2026-07-14T06:03:23.819", "26.06.01");

        var lines = lua.Split('\n');
        Assert.Equal("-- createdAt: 2026-07-14T06:03:23.819", lines[0].TrimEnd());
        Assert.StartsWith("-- mmwtVersion: v", lines[1]);
        Assert.Equal("-- gameVersion: 26.06.01", lines[2].TrimEnd());
    }

    [Fact]
    public void Emit_omitsGameVersionLine_whenUnknown()
    {
        var lua = new LuaGeneratorService().GenerateEventScheduleLua(
            new List<EventScheduleGroup>(), "2026-07-14T06:03:23.819");
        Assert.DoesNotContain("-- gameVersion:", lua);
    }
}
