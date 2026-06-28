using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class GarageCleanupNameServiceTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Garage Cleanup", true)]
    [InlineData("CBE_Easter2025", true)]
    [InlineData("GC_SweetMess", true)]
    [InlineData("Sweet Mess Express", false)]
    public void IsPlaceholderName_Classifies(string? name, bool expected)
        => Assert.Equal(expected, GarageCleanupNameService.IsPlaceholderName(name));

    [Fact]
    public void BuildGlobalCbeMap_PrefersNonPlaceholderAcrossDumps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gcns_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var older = Path.Combine(dir, "a.json");
        File.WriteAllText(older, "{\"Data\":{\"CollectibleBoards\":[{\"CollectibleBoardEventId\":\"CBE_Easter2025\",\"Name\":\"Sweet Mess Express\"}]}}");
        var newer = Path.Combine(dir, "b.json");
        File.WriteAllText(newer, "{\"Data\":{\"CollectibleBoards\":[{\"CollectibleBoardEventId\":\"CBE_Easter2025\",\"Name\":\"CBE_Easter2025\"}]}}");

        var map = GarageCleanupNameService.BuildGlobalCbeMap(new[] { older, newer });

        Assert.Equal("Sweet Mess Express", map["CBE_Easter2025"]);
    }

    [Fact]
    public void ResolveGcCanonicalName_FromParentCbe()
    {
        var cbe = new Dictionary<string, string>(System.StringComparer.Ordinal) { ["CBE_SweetMess"] = "Sweet Mess Express" };
        using var doc = JsonDocument.Parse("{\"MergeBoardId\":\"CBE_SweetMess_Board\",\"Name\":\"Garage Cleanup\"}");
        Assert.Equal("Sweet Mess Express Garage Cleanup",
            GarageCleanupNameService.ResolveGcCanonicalName(doc.RootElement, cbe));
    }

    [Fact]
    public void YearFromGc_PrefersIdYearThenScheduleStart()
    {
        using var a = JsonDocument.Parse("{\"GarageCleanupEventId\":\"GC_MaddieInParis2025\"}");
        Assert.Equal(2025, GarageCleanupNameService.YearFromGc(a.RootElement));
        using var b = JsonDocument.Parse("{\"GarageCleanupEventId\":\"GC_X\",\"ActivableParams\":{\"Schedule\":{\"Start\":\"2024-07-07T00:00:00\"}}}");
        Assert.Equal(2024, GarageCleanupNameService.YearFromGc(b.RootElement));
    }

    [Fact]
    public void NormalizeEventsJson_SuffixesDistinctYearCollisionOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gcns2_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cbe = new Dictionary<string, string>(System.StringComparer.Ordinal)
            { ["CBE_Maddie"] = "Maddie In Paris", ["CBE_Green"] = "Green Acres Quest" };
        var src = Path.Combine(dir, "ev.json");
        File.WriteAllText(src, "{\"Data\":{\"CollectibleBoards\":[]," +
            "\"GarageCleanups\":[" +
            "{\"GarageCleanupEventId\":\"GC_Maddie2025\",\"MergeBoardId\":\"CBE_Maddie_Board\",\"Name\":\"Garage Cleanup\"}," +
            "{\"GarageCleanupEventId\":\"GC_Maddie2026\",\"MergeBoardId\":\"CBE_Maddie_Board\",\"Name\":\"Garage Cleanup\"}," +
            "{\"GarageCleanupEventId\":\"GC_Green2024\",\"MergeBoardId\":\"CBE_Green_Board\",\"Name\":\"Garage Cleanup\"}," +
            "{\"GarageCleanupEventId\":\"GC_Green2024_01\",\"MergeBoardId\":\"CBE_Green_Board\",\"Name\":\"Garage Cleanup\"}]}}");

        var outPath = GarageCleanupNameService.NormalizeEventsJson(src, cbe, dir, "t");

        using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
        var names = new List<string>();
        foreach (var g in doc.RootElement.GetProperty("Data").GetProperty("GarageCleanups").EnumerateArray())
            names.Add(g.GetProperty("Name").GetString()!);
        Assert.Contains("Maddie In Paris Garage Cleanup (2025)", names);
        Assert.Contains("Maddie In Paris Garage Cleanup (2026)", names);
        Assert.Equal(2, names.FindAll(n => n == "Green Acres Quest Garage Cleanup").Count);
    }
}
