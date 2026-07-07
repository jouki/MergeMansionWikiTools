using System;
using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>Covers the deterministic (Phase 1) predictor pipeline: hotspot blocking, task
/// selection, and each requirement/reward filter, plus the Predict orchestration.</summary>
public class DailyTradePredictorEngineTests
{
    private static DailyTradeChainRule Rule(string key, bool canReq = true, bool canRwd = true,
        int? min = null, int? max = null, bool hiPri = false,
        List<string>? producer = null, List<string>? visibleAreas = null) =>
        new()
        {
            ConfigKey = key, CanBeRequirement = canReq, CanBeReward = canRwd, MinLevel = min, MaxLevel = max,
            OnlyPossibleRequirementsHighPriority = hiPri, RequireOnlyIfHaveProducer = producer ?? new(),
            RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas = visibleAreas ?? new(),
        };

    private static PredictorBoardItem B(string k, int lvl, int n = 1) => new() { ChainKey = k, Level = lvl, Count = n };

    private static PredictorInput Input(List<PredictorBoardItem> board, List<DailyTradeChainRule> rules,
        Dictionary<string, HashSet<int>>? blocked = null, HashSet<string>? producers = null,
        HashSet<string>? lastRewards = null)
    {
        var map = rules.ToDictionary(r => r.ConfigKey, r => r, StringComparer.OrdinalIgnoreCase);
        return new PredictorInput
        {
            Board = board,
            BlockedLevels = blocked ?? new(StringComparer.OrdinalIgnoreCase),
            ChainsWithProducerHeld = producers ?? new(StringComparer.OrdinalIgnoreCase),
            LastRewardChainKeys = lastRewards ?? new(StringComparer.OrdinalIgnoreCase),
            RuleOf = k => map.TryGetValue(k, out var r) ? r : null,
        };
    }

    // ── ComputeBlockedLevels ──

    [Fact]
    public void ComputeBlockedLevels_BlocksRequirementLevelsWithinMaxDiff()
    {
        var hotspotTasks = new List<LuaTask>
        {
            new() { Requirements = new() { ["Detergent_08"] = 1 } },
            new() { Requirements = new() { ["Vase_03"] = 4 } },
        };
        string? ChainOf(string it) => it.Split('_')[0];
        int LevelOf(string it) => int.Parse(it.Split('_')[1]);

        var blocked = DailyTradePredictorEngine.ComputeBlockedLevels(hotspotTasks, maxLevelDiff: 3, ChainOf, LevelOf);

        Assert.Contains(8, blocked["Detergent"]);
        Assert.Contains(5, blocked["Detergent"]);
        Assert.Contains(11, blocked["Detergent"]);
        Assert.DoesNotContain(12, blocked["Detergent"]);
        Assert.Contains(3, blocked["Vase"]);
    }

    [Fact]
    public void ComputeBlockedLevels_OnlyBlocksFromGivenTasks()
    {
        var hotspotTasks = new List<LuaTask>
        {
            new() { Requirements = new() { ["Chain0_05"] = 1 } },
            new() { Requirements = new() { ["Chain2_05"] = 1 } },
        };
        string? ChainOf(string it) => it.Split('_')[0];
        int LevelOf(string it) => int.Parse(it.Split('_')[1]);

        var blocked = DailyTradePredictorEngine.ComputeBlockedLevels(hotspotTasks, maxLevelDiff: 1, ChainOf, LevelOf);

        Assert.True(blocked.ContainsKey("Chain0"));
        Assert.True(blocked.ContainsKey("Chain2"));
        Assert.False(blocked.ContainsKey("Chain3"));
    }

    // ── SelectTask ──

    [Fact]
    public void SelectTask_MatchesStreakAndQueue()
    {
        var tasks = new List<DailyTradeTask>
        {
            new() { TaskId = "EasyTask_3", UIOrder = 0, StreakCountMin = 2, StreakCountMax = 2 },
            new() { TaskId = "HardTask_3", UIOrder = 1, StreakCountMin = 2, StreakCountMax = 2 },
        };
        Assert.Equal("HardTask_3", DailyTradePredictorEngine.SelectTask(tasks, 2, uiOrder: 1)!.TaskId);
        Assert.Equal("EasyTask_3", DailyTradePredictorEngine.SelectTask(tasks, 2, uiOrder: 0)!.TaskId);
        Assert.Null(DailyTradePredictorEngine.SelectTask(tasks, 4, uiOrder: 0));
    }

    // ── Requirement filters ──

    [Fact]
    public void BuildRequirementLists_RejectsNonRequirementChains()
    {
        var input = Input(new() { B("Butterfly", 5) }, new() { Rule("Butterfly", canReq: false) });
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRequirementLists(input, res);
        Assert.Empty(res.ReqHigh.Concat(res.ReqNormal).Concat(res.ReqLow));
        Assert.Contains(res.RejectedRequirements, r => r.ChainKey == "Butterfly");
    }

    [Fact]
    public void BuildRequirementLists_RejectsOutOfLevelBounds()
    {
        var input = Input(new() { B("PlantedFlower", 2) }, new() { Rule("PlantedFlower", min: 3) });
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRequirementLists(input, res);
        Assert.Contains(res.RejectedRequirements, r => r.ChainKey == "PlantedFlower" && r.Reason.Contains("level out"));
    }

    [Fact]
    public void BuildRequirementLists_BlocksHotspotLevels()
    {
        var blocked = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase)
            { ["Detergent"] = new() { 5, 6, 7, 8 } };
        var input = Input(new() { B("Detergent", 7) }, new() { Rule("Detergent") }, blocked);
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRequirementLists(input, res);
        Assert.DoesNotContain(res.ReqNormal, c => c.ChainKey == "Detergent");
        Assert.Contains(res.RejectedRequirements, r => r.ChainKey == "Detergent" && r.Reason.Contains("hotspot"));
    }

    [Fact]
    public void BuildRequirementLists_DedupesUnderLeveledSameChain()
    {
        var input = Input(new() { B("Peony", 10), B("Peony", 12) }, new() { Rule("Peony") });
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRequirementLists(input, res);
        Assert.Contains(res.ReqNormal, c => c.ChainKey == "Peony" && c.Level == 12);
        Assert.DoesNotContain(res.ReqNormal, c => c.ChainKey == "Peony" && c.Level == 10);
        Assert.Contains(res.RejectedRequirements, r => r.ChainKey == "Peony" && r.Level == 10);
    }

    [Fact]
    public void BuildRequirementLists_HighPriorityExclusiveGoesToHigh()
    {
        var input = Input(new() { B("Scarab", 3), B("Wood", 4) },
            new() { Rule("Scarab", hiPri: true), Rule("Wood") });
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRequirementLists(input, res);
        Assert.Contains(res.ReqHigh, c => c.ChainKey == "Scarab");
        Assert.DoesNotContain(res.ReqNormal, c => c.ChainKey == "Scarab");
        Assert.Contains(res.ReqNormal, c => c.ChainKey == "Wood");
    }

    [Fact]
    public void BuildRequirementLists_ProducerGate()
    {
        var input = Input(new() { B("GardenBench", 2) },
            new() { Rule("GardenBench", producer: new() { "SomeProducer" }) });
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRequirementLists(input, res);
        Assert.Contains(res.RejectedRequirements, r => r.ChainKey == "GardenBench" && r.Reason.Contains("producer"));

        var input2 = Input(new() { B("GardenBench", 2) },
            new() { Rule("GardenBench", producer: new() { "SomeProducer" }) },
            producers: new(StringComparer.OrdinalIgnoreCase) { "GardenBench" });
        var res2 = new PredictorResult();
        DailyTradePredictorEngine.BuildRequirementLists(input2, res2);
        Assert.Contains(res2.ReqNormal, c => c.ChainKey == "GardenBench");
    }

    // ── Reward filters ──

    [Fact]
    public void BuildRewardLists_RejectsNonRewardChains()
    {
        var input = Input(new() { B("Wood", 3) }, new() { Rule("Wood", canRwd: false) });
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRewardLists(input, new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase), res);
        Assert.Contains(res.RejectedRewards, r => r.ChainKey == "Wood");
    }

    [Fact]
    public void BuildRewardLists_VisibleHotspotAreaGate()
    {
        var input = Input(new() { B("DogTools", 2) },
            new() { Rule("DogTools", visibleAreas: new() { "DogArea" }) });
        var resBlocked = new PredictorResult();
        DailyTradePredictorEngine.BuildRewardLists(input, new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase), resBlocked);
        Assert.Contains(resBlocked.RejectedRewards, r => r.ChainKey == "DogTools");

        var resOk = new PredictorResult();
        DailyTradePredictorEngine.BuildRewardLists(input, new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase) { "DogArea" }, resOk);
        Assert.Contains(resOk.RwdNormal, c => c.ChainKey == "DogTools");
    }

    [Fact]
    public void BuildRewardLists_AntiRepeat()
    {
        var input = Input(new() { B("Vase", 3) }, new() { Rule("Vase") },
            lastRewards: new(StringComparer.OrdinalIgnoreCase) { "Vase" });
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRewardLists(input, new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase), res);
        Assert.Contains(res.RejectedRewards, r => r.ChainKey == "Vase" && r.Reason.Contains("last"));
    }

    [Fact]
    public void BuildRewardLists_HotspotRequiredGoesToHigh()
    {
        var input = Input(new() { B("Screws", 4) }, new() { Rule("Screws") });
        var res = new PredictorResult();
        DailyTradePredictorEngine.BuildRewardLists(input,
            new(StringComparer.OrdinalIgnoreCase) { "Screws" }, new(StringComparer.OrdinalIgnoreCase), res);
        Assert.Contains(res.RwdHigh, c => c.ChainKey == "Screws");
    }

    // ── Predict orchestration ──

    [Fact]
    public void Predict_ExcludesItemsUsedByCurrentTrades()
    {
        // Queue 1 currently asks for Paint and rewards Screws → the next roll must not pick them.
        var state = new PredictorState
        {
            Cells =
            {
                new PredictorBoardCell { Row = 0, Col = 0, ChainKey = "Paint", Level = 7 },
                new PredictorBoardCell { Row = 0, Col = 1, ChainKey = "Screws", Level = 7 },
                new PredictorBoardCell { Row = 0, Col = 2, ChainKey = "Wood", Level = 4 },
            },
            Queue1Trade = new PredictorKnownTrade
            {
                Requirement = new PredictorTradeItem { ChainKey = "Paint", Level = 7 },
                Reward = new PredictorTradeItem { ChainKey = "Screws", Level = 7 },
            },
        };
        DailyTradeChainRule Rule(string k) => new() { ConfigKey = k, CanBeRequirement = true, CanBeReward = true };
        var rules = new[] { "Paint", "Screws", "Wood" }.ToDictionary(k => k, Rule, StringComparer.OrdinalIgnoreCase);

        var res = DailyTradePredictorEngine.Predict(new DailyTradeSettings(), state,
            Array.Empty<LuaArea>(), _ => null, _ => 0,
            k => rules.TryGetValue(k, out var r) ? r : null,
            (_, _) => null, (_, _) => null, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain(res.ReqNormal, c => c.ChainKey == "Paint");
        Assert.Contains(res.RejectedRequirements, r => r.ChainKey == "Paint" && r.Reason.Contains("current trade"));
        Assert.DoesNotContain(res.RwdNormal, c => c.ChainKey == "Screws");
        Assert.Contains(res.RejectedRewards, r => r.ChainKey == "Screws" && r.Reason.Contains("current trade"));
        // Wood is untouched by the exclusions.
        Assert.Contains(res.ReqNormal, c => c.ChainKey == "Wood");
        Assert.Contains(res.RwdNormal, c => c.ChainKey == "Wood");
    }

    [Fact]
    public void Predict_EmptyBoard_AddsNote()
    {
        var state = new PredictorState { Streak = 0 };
        var res = DailyTradePredictorEngine.Predict(new DailyTradeSettings(), state,
            Array.Empty<LuaArea>(), _ => null, _ => 0, _ => null, (_, _) => null, (_, _) => null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(res.ReqNormal);
        Assert.Contains(res.Notes, n => n.Contains("Board", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Predict_FillsCandidateValues()
    {
        var state = new PredictorState
        {
            Streak = 0,
            Cells = { new PredictorBoardCell { Row = 0, Col = 0, ChainKey = "Wood", Level = 4 } },
        };
        DailyTradeChainRule? RuleOf(string k) => k == "Wood"
            ? new DailyTradeChainRule { ConfigKey = "Wood", CanBeRequirement = true, CanBeReward = true } : null;
        var res = DailyTradePredictorEngine.Predict(new DailyTradeSettings(), state,
            Array.Empty<LuaArea>(), _ => null, _ => 0, RuleOf,
            (c, l) => c == "Wood" ? 42 : null, (c, l) => c == "Wood" ? 33 : null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(res.ReqNormal, c => c.ChainKey == "Wood" && c.Value == 42);
        Assert.Contains(res.RwdNormal, c => c.ChainKey == "Wood" && c.Value == 33);
    }
}
