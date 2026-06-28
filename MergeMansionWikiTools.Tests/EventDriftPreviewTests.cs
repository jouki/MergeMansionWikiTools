using System;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class EventDriftPreviewTests
{
    [Fact]
    public void BuildPreview_variantA_adds_variantB_replaces()
    {
        var d = new EventDriftDecision("Legacy Lane", new DateTime(2026, 5, 29), new DateTime(2026, 6, 25), 5);
        var (a, b) = EventDriftPreview.BuildPreview(d);
        // Variant A (two separate runs): adds the new run, keeps the existing one.
        Assert.Contains("+", a);
        Assert.Contains("25.06.2026", a);
        Assert.Contains("29.05.2026", a);
        // Variant B (update / drift): removes the existing run, adds the new one.
        Assert.Contains("- ", b);
        Assert.Contains("29.05.2026", b);
        Assert.Contains("+ ", b);
        Assert.Contains("25.06.2026", b);
    }
}
