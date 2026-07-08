using System;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Phase B of the season-pass-items-into-p.mysteries refactor: the row builders in
/// <see cref="MysteryWikiService"/> must emit an `eventItem = "&lt;chain&gt;"` field on the
/// p.mysteries Lua row whenever <see cref="MysteryEvent.EventItemName"/> has been resolved
/// (by <c>MysteryService.ResolveEventItems</c>), and omit it when absent.
/// </summary>
public class MysteryEventItemTests
{
    // Minimal Module:Datatable/Various fixture that InsertMysteryIntoModule accepts:
    // a p.mysteries table with a "-- 2026" year comment matching the test mystery's year,
    // and no existing entries (exercises the list.Count == 0 insertion branch).
    private const string ModuleFixture = @"local p = {}

p.mysteries = {
	-- 2026
}

return p
";

    [Fact]
    public void InsertedMysteryRow_includes_eventItem_when_resolved()
    {
        var mystery = new MysteryEvent { Name = "Buzzing with Purpose", StartDate = new DateTime(2026, 5, 15), EventItemName = "Sweet Beginnings" };

        var (_, updated) = MysteryWikiService.InsertMysteryIntoModule(ModuleFixture, mystery);

        Assert.NotNull(updated);
        Assert.Contains("eventItem = \"Sweet Beginnings\"", updated!);
    }

    [Fact]
    public void InsertedMysteryRow_omits_eventItem_when_absent()
    {
        var mystery = new MysteryEvent { Name = "No Item Pass", StartDate = new DateTime(2026, 5, 15), EventItemName = null };

        var (_, updated) = MysteryWikiService.InsertMysteryIntoModule(ModuleFixture, mystery);

        Assert.NotNull(updated);
        Assert.DoesNotContain("eventItem", updated!);
    }
}
