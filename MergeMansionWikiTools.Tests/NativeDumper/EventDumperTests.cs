using System.Linq;
using MergeMansionWikiTools.Dumper;
using MergeMansionWikiTools.Dumper.Dumpers;
using MergeMansionWikiTools.Dumper.Serializers;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// The pure, config-free pieces of the event dump: which <see cref="EventFilters"/> flag an event id
/// belongs to, how a garage cleanup finds its parent event, and how solo-milestone ids sort. Every
/// expectation here is read off the legacy <c>--filters</c> corpus of the newest archive.
/// </summary>
public class EventDumperTests
{
    [Theory]
    // Lucky Catch: the LC_ family plus the one CBE_-prefixed member.
    [InlineData("LC_Spring", EventFilters.LuckyCatch)]
    [InlineData("CBE_LuckyCatch", EventFilters.LuckyCatch)]
    // Lucky Snap.
    [InlineData("LS_Winter2024", EventFilters.LuckySnap)]
    // Legacy: gem mine, Great Escape, Jailbreak — regardless of prefix.
    [InlineData("GM_Something", EventFilters.Legacy)]
    [InlineData("CBE_GemMine", EventFilters.Legacy)]
    [InlineData("CBE_TheGreatEscape2022", EventFilters.Legacy)]
    [InlineData("LDE_Jailbreak2022", EventFilters.Legacy)]
    // Everything else is Seasonal, across all three seasonal prefixes.
    [InlineData("CBE_VeilOfFate2024", EventFilters.Seasonal)]
    [InlineData("LDE_MurderAtTheMansion", EventFilters.Seasonal)]
    [InlineData("SE_DoubleDateDisaster2024", EventFilters.Seasonal)]
    public void Collectible_board_events_are_classified_by_id(string id, EventFilters expected)
        => Assert.Equal(expected, EventFilterRules.ForCollectibleBoard(id));

    [Theory]
    [InlineData("DE_StoneAge_06", EventFilters.ReArchaeology)]
    [InlineData("CR_Sailing_01", EventFilters.HorizonsCup)]
    [InlineData("CSE_DinnerRolls2025", EventFilters.RollTheDice)]
    [InlineData("CSE_SomethingRollTheDice", EventFilters.RollTheDice)]
    [InlineData("AutoMerge_01", EventFilters.AutoMerge)]
    [InlineData("AutoMerge_Daily_04", EventFilters.AutoMerge)]
    [InlineData("CSE_Builder_icesculpt_wheel_01", EventFilters.Uncategorised)]
    [InlineData("DailyChallenges_10", EventFilters.Uncategorised)]
    public void Core_support_events_are_classified_by_id(string id, EventFilters expected)
        => Assert.Equal(expected, EventFilterRules.ForCoreSupportEvent(id));

    [Theory]
    // Neither of the two special leaderboards can be recognised from its id alone — LBE_May2023 is
    // the Bake-off — so the raw config DisplayName decides.
    [InlineData("LBE_May2023", "BakeOff Leaderboard Event", EventFilters.BakeOff)]
    [InlineData("LBE_BushBonanza", "Bush Bonanza Leaderboard Event", EventFilters.Bonanza)]
    [InlineData("LBE_Halloween2023", "Halloween 2023 Leaderboard Event", EventFilters.Legacy)]
    [InlineData("LBE_Xmas2024", "Xmas Leaderboard 2023", EventFilters.Legacy)]
    public void Leaderboards_are_classified_by_id_and_display_name(string id, string displayName, EventFilters expected)
        => Assert.Equal(expected, EventFilterRules.ForLeaderboard(id, displayName));

    [Theory]
    [InlineData("GC_JoysOfTheSea2023", "JoysOfTheSea2023")]
    [InlineData("GC_AmeliaBoulton2024_01", "AmeliaBoulton2024")]
    // "_Main" is not a round number, so it stays part of the suffix (and finds no parent).
    [InlineData("GC_Xmas2023_Main", "Xmas2023_Main")]
    // A cleanup that does not use the GC_ prefix at all still gets its round number cut.
    [InlineData("Bingo_01", "Bingo")]
    public void Garage_cleanup_parent_suffix_strips_prefix_and_round_number(string cleanupId, string expected)
        => Assert.Equal(expected, EventSerializer.GarageCleanupParentSuffix(cleanupId));

    [Fact]
    public void Solo_milestone_ids_sort_by_base_then_numeric_suffix()
    {
        // The golden order of the newest archive's SoloMilestoneMilestones, in shuffled input order.
        var input = new[]
        {
            "MySummerTea_C_01", "MySummerTea_10", "MySummerTeaSME_40", "MySummerTea_2",
            "MySummerTea_19_Onfire", "MySummerTeaCards_40", "MySummerTea_1",
        };

        var sorted = input.OrderBy(x => x, SoloMilestoneOrder.Comparer).ToArray();

        Assert.Equal(new[]
        {
            // numeric suffixes sort as numbers, so _2 comes before _10
            "MySummerTea_1", "MySummerTea_2", "MySummerTea_10",
            // then the bases that extend the name, ORDINAL: 'C' < 'S' < '_'
            "MySummerTeaCards_40", "MySummerTeaSME_40",
            "MySummerTea_19_Onfire", "MySummerTea_C_01",
        }, sorted);
    }

    [Fact]
    public void Solo_milestone_split_reads_the_trailing_number()
    {
        Assert.Equal(("MySummerTea", 38), SoloMilestoneOrder.Split("MySummerTea_38"));
        // No trailing number: the whole id is the base and the index is 0.
        Assert.Equal(("MyTea_02_Onfire", 0), SoloMilestoneOrder.Split("MyTea_02_Onfire"));
    }
}
