using System.Linq;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class EventScheduleDiffTests
{
    // Minimal Module:Datatable/Events shape the diff parses.
    private static string Module(string eventsBody) =>
        "return {\n\tevents = {\n" + eventsBody + "\t},\n}\n";

    private static string Event(string name, string category, string runs, string? parent = null, string? badge = null) =>
        "\t\t{\n" +
        $"\t\t\tname = \"{name}\",\n" +
        $"\t\t\tcategory = \"{category}\",\n" +
        (parent != null ? $"\t\t\tparent = \"{parent}\",\n" : "") +
        (badge != null ? $"\t\t\tbadge = \"{badge}\",\n" : "") +
        "\t\t\truns = {\n" + runs + "\t\t\t},\n" +
        "\t\t},\n";

    private static string Run(int y, int m, int d, int days, bool disabled = false) =>
        $"\t\t\t\t{{ start = {{ year = {y}, month = {m}, day = {d} }}, durationDays = {days}"
        + (disabled ? ", disabled = true" : "") + " },\n";

    [Fact]
    public void NewEvent_detected()
    {
        var oldLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5)));
        var newLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5))
                          + Event("Beta", "Lucky Event", Run(2026, 7, 10, 4)));

        var cs = EventScheduleDiff.Compute(oldLua, newLua);

        Assert.True(cs.HasChanges);
        var ne = Assert.Single(cs.NewEvents);
        Assert.Equal("Beta", ne.Name);
        Assert.Empty(cs.NewRuns);
        Assert.Empty(cs.RemovedEvents);
    }

    [Fact]
    public void NewRun_onExistingEvent_detected()
    {
        var oldLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5)));
        var newLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5) + Run(2026, 7, 20, 5)));

        var cs = EventScheduleDiff.Compute(oldLua, newLua);

        var nr = Assert.Single(cs.NewRuns);
        Assert.Equal("Alpha", nr.EventName);
        Assert.Equal(20, nr.Start.Day);
        Assert.Empty(cs.NewEvents);
    }

    [Fact]
    public void ParentAdded_detected()
    {
        // The v0.24.24 motivating case: parent field appears on a Garage Cleanup entry.
        var oldLua = Module(Event("Legacy Lane Garage Cleanup", "Garage Cleanup", Run(2026, 7, 26, 3)));
        var newLua = Module(Event("Legacy Lane Garage Cleanup", "Garage Cleanup", Run(2026, 7, 26, 3), parent: "Legacy Lane"));

        var cs = EventScheduleDiff.Compute(oldLua, newLua);

        var fc = Assert.Single(cs.FieldChanges);
        Assert.Equal("parent", fc.Field);
        Assert.Equal("", fc.OldValue);
        Assert.Equal("Legacy Lane", fc.NewValue);
    }

    [Fact]
    public void DisabledFlip_detected()
    {
        var oldLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5, disabled: true)));
        var newLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5, disabled: false)));

        var cs = EventScheduleDiff.Compute(oldLua, newLua);

        var fc = Assert.Single(cs.FieldChanges);
        Assert.Equal("disabled", fc.Field);
        Assert.Equal("disabled", fc.OldValue);
        Assert.Equal("enabled", fc.NewValue);
        Assert.NotNull(fc.RunStart);
    }

    [Fact]
    public void RemovedEvent_detected()
    {
        var oldLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5))
                          + Event("Gone", "Core Support Event", Run(2026, 6, 1, 2)));
        var newLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5)));

        var cs = EventScheduleDiff.Compute(oldLua, newLua);

        var re = Assert.Single(cs.RemovedEvents);
        Assert.Equal("Gone", re.Name);
    }

    [Fact]
    public void Identical_yieldsNoChanges()
    {
        var lua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5) + Run(2026, 7, 20, 5), parent: null));

        var cs = EventScheduleDiff.Compute(lua, lua);

        Assert.False(cs.HasChanges);
        Assert.Empty(cs.NewEvents);
        Assert.Empty(cs.NewRuns);
        Assert.Empty(cs.FieldChanges);
    }

    [Fact]
    public void OldModuleMissing_allNew_flagSet()
    {
        var newLua = Module(Event("Alpha", "Seasonal Event", Run(2026, 7, 1, 5)));

        var cs = EventScheduleDiff.Compute(null, newLua);

        Assert.True(cs.OldModuleMissing);
        Assert.Single(cs.NewEvents);
    }
}
