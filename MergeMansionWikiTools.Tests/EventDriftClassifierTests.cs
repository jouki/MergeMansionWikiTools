using System;
using System.Collections.Generic;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class EventDriftClassifierTests
{
    [Theory]
    [InlineData("2026-05-29", 5, "2026-05-31", DriftVerdict.AutoUpdate)]    // new start within [start, start+dur]
    [InlineData("2026-05-29", 5, "2026-06-25", DriftVerdict.NeedsDecision)] // 27d after start, beyond dur, <60d
    [InlineData("2026-05-29", 5, "2026-08-15", DriftVerdict.AutoSeparate)]  // >=60d after start
    public void Classify_bands(string existingStart, double dur, string newStart, DriftVerdict expected)
        => Assert.Equal(expected, EventDriftClassifier.Classify(
            DateTime.Parse(existingStart), dur, DateTime.Parse(newStart)));

    [Fact]
    public void Nearest_picksClosestByStart()
    {
        var runs = new List<(DateTime Start, double Dur)> {
            (new DateTime(2026, 5, 29), 5), (new DateTime(2026, 8, 15), 5)
        };
        Assert.Equal(new DateTime(2026, 5, 29), EventDriftClassifier.Nearest(runs, new DateTime(2026, 6, 2))!.Value.Start);
    }
}
