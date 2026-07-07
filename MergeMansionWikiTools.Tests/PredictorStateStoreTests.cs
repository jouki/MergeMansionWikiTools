using System.IO;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class PredictorStateStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var state = new PredictorState
        {
            Cells =
            {
                new PredictorBoardCell { Row = 0, Col = 0, ChainKey = "Detergent", Level = 8 },
                new PredictorBoardCell { Row = 0, Col = 1, ChainKey = "Detergent", Level = 8 },
            },
            InventoryCells =
            {
                new PredictorBoardCell { Row = 0, Col = 0, ChainKey = "Detergent", Level = 8 },
                new PredictorBoardCell { Row = 0, Col = 1, ChainKey = "Wood", Level = 5 },
            },
            InventorySlotsOwned = 12, InventorySpBonus = 6,
            AreaInternalName = "Attic", ActiveTaskIds = { "task-a", "task-b" }, Streak = 2,
            Queue1StepIndex = 1, Queue2StepIndex = 3,
            Queue1Refreshes = 2, Queue2Refreshes = 5,
            Queue1Trade = new PredictorKnownTrade
            {
                Requirement = new PredictorTradeItem { ChainKey = "Paint", Level = 7 },
                Reward = new PredictorTradeItem { ChainKey = "Screws", Level = 7 },
            },
            LastRewardChainKeys = { "Vase", "Wood" },
        };
        PredictorStateStore.Save(path, state);
        var loaded = PredictorStateStore.Load(path);
        File.Delete(path);

        Assert.Equal(2, loaded.Cells.Count);
        Assert.Equal("Detergent", loaded.Cells[0].ChainKey);
        Assert.Equal(8, loaded.Cells[0].Level);
        // Board is derived from Cells + InventoryCells (both are part of the garage pool):
        // 2 board tiles + 1 inventory tile of Detergent L8 aggregate to Count = 3, plus Wood L5.
        Assert.Equal(2, loaded.InventoryCells.Count);
        Assert.Equal(12, loaded.InventorySlotsOwned);
        Assert.Equal(6, loaded.InventorySpBonus);
        Assert.Equal(2, loaded.Board.Count);
        var det = loaded.Board.Single(b => b.ChainKey == "Detergent");
        Assert.Equal(3, det.Count);
        Assert.Equal(8, det.Level);
        var wood = loaded.Board.Single(b => b.ChainKey == "Wood");
        Assert.Equal(1, wood.Count);
        Assert.Equal("Attic", loaded.AreaInternalName);
        Assert.Equal(new[] { "task-a", "task-b" }, loaded.ActiveTaskIds);
        Assert.Equal(1, loaded.Queue1StepIndex);
        Assert.Equal(3, loaded.Queue2StepIndex);
        Assert.Equal(2, loaded.Queue1Refreshes);
        Assert.Equal(5, loaded.Queue2Refreshes);
        Assert.Equal("Paint", loaded.Queue1Trade.Requirement!.ChainKey);
        Assert.Equal(7, loaded.Queue1Trade.Reward!.Level);
        Assert.Null(loaded.Queue2Trade.Requirement);
        Assert.Equal(new[] { "Vase", "Wood" }, loaded.LastRewardChainKeys);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyState()
    {
        var state = PredictorStateStore.Load(Path.Combine(Path.GetTempPath(), "nope-" + Path.GetRandomFileName()));
        Assert.Empty(state.Board);
        Assert.Empty(state.ActiveTaskIds);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyState()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, "{ not json");
        var state = PredictorStateStore.Load(path);
        File.Delete(path);
        Assert.Empty(state.Board);
    }
}
