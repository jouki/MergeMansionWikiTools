using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Temp-area tags appear in two shapes: prefix "Temp&lt;Area&gt;" (TempCinema, the common case) and
/// suffix "&lt;Area&gt;Temp" (MaintenanceRoomTemp, ConservatoryTemp). Regression: the suffix form was
/// missed, so Maintenance Room Valuables / Conservatory chains lost their temporary flag and the
/// Infobox intro never resolved their area ("... used on the Main Board in {{Area|X}}").
/// </summary>
public class TemporaryTagDetectionTests
{
    [Theory]
    [InlineData("TempCinema")]
    [InlineData("TempAttic")]
    [InlineData("TempSaunaRebalance")]
    public void PrefixTempTag_IsTemporary(string tag) =>
        Assert.True(DataService.HasTemporaryTag(new[] { tag }));

    [Theory]
    [InlineData("MaintenanceRoomTemp")]
    [InlineData("ConservatoryTemp")]
    public void SuffixTempTag_IsTemporary(string tag) =>
        Assert.True(DataService.HasTemporaryTag(new[] { tag }));

    [Theory]
    [InlineData("CanUseBlueCard")]
    [InlineData("AlwaysShowCanBeFoundIn")]
    [InlineData("Common")] // "Temp" appears nowhere as prefix or suffix
    public void UnrelatedTag_IsNotTemporary(string tag) =>
        Assert.False(DataService.HasTemporaryTag(new[] { tag }));

    [Fact]
    public void MixedTags_TempPresent_IsTemporary() =>
        Assert.True(DataService.HasTemporaryTag(new[] { "CanUseBlueCard", "MaintenanceRoomTemp", "CanUseScissors" }));
}
