using System.Text.Json;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>DailyTradeService parses events.json Data.DailyTasksV2* sections
/// (task templates, per-chain eligibility rules, global settings).</summary>
public class DailyTradeServiceTests
{
    private const string Json = """
    {
      "Data": {
        "DailyTasksV2": [
          {
            "TaskId": "EasyTask_1", "IsEnabled": true,
            "StreakCountMin": 0, "StreakCountMax": 0, "UIOrder": 0,
            "Steps": [
              { "RequirementItemValue": 10, "RewardItemValue": 8, "RefreshEnabled": true,
                "RefreshCosts": [0,0,1,5],
                "AlgorithmAttempts": ["HH","NH","LH","HN","NN","HL","NL","LN","LL"] }
            ]
          },
          { "TaskId": "Disabled_1", "IsEnabled": false, "StreakCountMin": 0, "StreakCountMax": 0, "UIOrder": 9, "Steps": [] }
        ],
        "DailyTasksV2MergeChains": [
          { "ConfigKey": "PlantedFlower", "MinLevel": 3, "CanBeRequirement": true, "CanBeReward": true,
            "RequirementMultiplier": 1.0, "RewardMultiplier": 1.0,
            "RewardOnlyIfInHotspotRequirement": false, "OnlyPossibleRequirementsHighPriority": false,
            "RequireOnlyIfHaveProducer": [] },
          { "ConfigKey": "DogAreaCarpenterTools", "CanBeRequirement": false, "CanBeReward": true,
            "RequirementMultiplier": 2.0, "RewardMultiplier": 2.0,
            "RewardOnlyIfInHotspotRequirement": false, "OnlyPossibleRequirementsHighPriority": false,
            "RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas": ["DogArea"],
            "RequireOnlyIfHaveProducer": [] }
        ],
        "DailyTasksV2Settings": {
          "NextHotspotsCount": 6, "MaxStreakCount": 4,
          "MaxLevelDifferenceFromHotspotToRemoveRequirementItem": 3,
          "LastGeneratedTaskRewardItemsHistoryCountMaxSize": 4,
          "AgeValues": [5,10,20],
          "AlgorithmAttemptsDefault": ["HH","HN"],
          "SortingRangeForPossibleRequirementsHighPriority": 5, "SortingRangeGeneric": 2
        }
      }
    }
    """;

    private static DailyTradeService Load()
    {
        var svc = new DailyTradeService();
        using var doc = JsonDocument.Parse(Json);
        svc.Parse(doc.RootElement);
        return svc;
    }

    [Fact]
    public void Parse_ReadsTaskTemplatesWithSteps()
    {
        var svc = Load();
        Assert.True(svc.HasData);
        Assert.Equal(2, svc.Tasks.Count);
        var t = svc.Tasks[0];
        Assert.Equal("EasyTask_1", t.TaskId);
        Assert.True(t.IsEnabled);
        Assert.Equal(0, t.StreakCountMin);
        var s = Assert.Single(t.Steps);
        Assert.Equal(10, s.RequirementItemValue);
        Assert.Equal(8, s.RewardItemValue);
        Assert.Equal(new[] { 0, 0, 1, 5 }, s.RefreshCosts);
        Assert.Equal(9, s.AlgorithmAttempts.Count);
    }

    [Fact]
    public void Parse_ReadsChainRules_MinLevelNullableAndAreaGate()
    {
        var svc = Load();
        Assert.Equal(2, svc.ChainRules.Count);
        var pf = svc.ChainRules["PlantedFlower"];
        Assert.True(pf.CanBeRequirement);
        Assert.Equal(3, pf.MinLevel);
        Assert.Null(pf.MaxLevel);
        var dog = svc.ChainRules["DogAreaCarpenterTools"];
        Assert.False(dog.CanBeRequirement);
        Assert.Equal(new[] { "DogArea" }, dog.RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas);
    }

    [Fact]
    public void Parse_ReadsSettings()
    {
        var svc = Load();
        Assert.NotNull(svc.Settings);
        Assert.Equal(6, svc.Settings!.NextHotspotsCount);
        Assert.Equal(3, svc.Settings.MaxLevelDifferenceFromHotspotToRemoveRequirementItem);
        Assert.Equal(new[] { 5, 10, 20 }, svc.Settings.AgeValues);
    }

    [Fact]
    public void Parse_MissingSections_HasDataFalse()
    {
        var svc = new DailyTradeService();
        using var doc = JsonDocument.Parse("""{ "Data": { "CollectibleBoards": [] } }""");
        svc.Parse(doc.RootElement);
        Assert.False(svc.HasData);
        Assert.Empty(svc.Tasks);
    }
}
