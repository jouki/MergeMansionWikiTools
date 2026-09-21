using System;
using System.Linq;
using Code.GameLogic.GameEvents;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// The <c>ActiveDecoration</c> block a collectible board carries next to its
/// <c>ActiveDecorationRef</c>: from which decoration layer the player keeps the decoration for good,
/// and which Event Level first awards that layer.
///
/// The threshold (<c>LayeredDecorationSetInfo.RequiredProgressToOwn</c>) sits two references away
/// from the board, so nothing that was dumped could see it. Before this existed the only way to
/// learn it was to read the sentence out of the localized popup
/// (<c>&lt;EventId&gt;_LayeredDecoInfoPopup_Description</c>) — which does not exist for every event
/// and breaks whenever MetaCore rewords it.
///
/// The expectations below were cross-checked BOTH ways when the block was added: the config field
/// and the popup text agree on all eleven events that have both.
///
/// Needs the local <c>_DATA</c> pull; returns early without it.
/// </summary>
[Collection(GlobalGameStateCollection.Name)]
public class ActiveDecorationTests
{
    /// <summary>Board id → owning layer, and the Event Level that first awards it.</summary>
    public static TheoryData<string, int, int> Thresholds => new()
    {
        // The three newest 8-layer events share a ladder: owned on layer 5, at Event Level 35.
        { "LDE_MurderAtTheMansion", 5, 35 },
        { "LDE_HolidayOddities2025", 5, 35 },
        { "LDE_Rumors2025", 5, 35 },
        // The older 13/15-layer ladders own on layer 7, at levels that differ per event — which is
        // why the level has to be resolved per board and cannot be derived from the layer alone.
        { "LDE_Hopeberry2024", 7, 25 },
        { "LDE_GreenAcresQuest2024", 7, 26 },
        { "LDE_PiratesOfHopewellBay2023", 7, 30 },
        { "LDE_HolidayCarols2023", 7, 30 },
        { "LDE_GrandmasBirthday2023", 7, 20 },
        // Re-runs point at the PREVIOUS year's decoration set, so the level comes from this board's
        // own level list, not from the set.
        { "LDE_Hopeberry2025", 7, 25 },
        { "LDE_PiratesOfHopewellBay2024", 7, 30 },
        { "LDE_PicnicConfusion2024", 7, 20 },
    };

    [Theory]
    [MemberData(nameof(Thresholds))]
    public void Board_states_the_layer_and_level_at_which_the_decoration_is_kept(
        string boardId, int expectedProgress, int expectedLevel)
    {
        var config = LiveConfig.Load();
        if (config == null) return; // no local _DATA

        var decoration = Serialize(config, Board(config, boardId))["ActiveDecoration"];
        Assert.NotNull(decoration);
        Assert.Equal(expectedProgress, (int)decoration!["RequiredProgressToOwn"]!);
        Assert.Equal(expectedLevel, (int)decoration["EventLevelToOwn"]!);
    }

    [Fact]
    public void Re_runs_name_the_decoration_set_they_inherit()
    {
        var config = LiveConfig.Load();
        if (config == null) return;

        // Hopeberry 2025 upgrades the 2024 decoration. The set id is what makes that visible; the
        // board id alone would suggest a set of its own.
        var decoration = Serialize(config, Board(config, "LDE_Hopeberry2025"))["ActiveDecoration"];
        Assert.Equal("LDE_Hopeberry2024", (string?)decoration!["LayeredDecorationSetId"]);
    }

    [Fact]
    public void Only_boards_that_have_a_decoration_carry_the_block()
    {
        var config = LiveConfig.Load();
        if (config == null) return;

        var boards = config.CollectibleBoardEvents.EnumerateAll()
            .Select(kv => (CollectibleBoardEventInfo)kv.Value)
            .Select(b => Serialize(config, b))
            .ToList();

        // The block never appears on its own: it is written by the same hook that writes the ref,
        // so a board has both or neither.
        foreach (var board in boards)
            Assert.Equal(board["ActiveDecorationRef"] != null, board["ActiveDecoration"] != null);

        // And it is not an empty feature — the newest event is in there.
        Assert.Contains(boards, b => (string?)b["CollectibleBoardEventId"] == "LDE_MurderAtTheMansion"
                                     && b["ActiveDecoration"] != null);
    }

    private static CollectibleBoardEventInfo Board(SharedGameConfig config, string boardId)
    {
        var board = config.CollectibleBoardEvents.EnumerateAll()
            .Select(kv => (CollectibleBoardEventInfo)kv.Value)
            .FirstOrDefault(b => string.Equals(b.ConfigKey?.ToString(), boardId, StringComparison.Ordinal));
        Assert.NotNull(board);
        return board!;
    }

    /// <summary>
    /// Serializes one board with the SAME converter stack <c>EventDumper</c> uses and returns its
    /// JSON object. The full stack is required, not just the event serializer: a board also holds
    /// cutscene and product references, and without <c>MetaRefConverter</c> Newtonsoft walks into
    /// their unresolved <c>Ref</c> and throws.
    /// </summary>
    private static JObject Serialize(SharedGameConfig config, object board)
    {
        var settings = DumpConverters.MainFileSettings(config, ConsoleDumpLog.Instance,
            MetaRefSkip.Unresolved, new EventSerializer(config, ConsoleDumpLog.Instance));
        settings.Formatting = Formatting.None;
        return JObject.Parse(JsonConvert.SerializeObject(board, settings));
    }
}
