using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Parses events.json → Data.DailyTasksV2 / DailyTasksV2MergeChains / DailyTasksV2Settings.
/// Sections are present only when the dump was made with the Daily Trades filter (v0.23.41+);
/// HasData is false otherwise and the Predictor page shows a hint.
/// </summary>
public class DailyTradeService
{
    public List<DailyTradeTask> Tasks { get; } = new();
    public Dictionary<string, DailyTradeChainRule> ChainRules { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DailyTradeSettings? Settings { get; private set; }

    public bool HasData => Tasks.Count > 0 && ChainRules.Count > 0;

    public async Task LoadAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        using var doc = await JsonDocument.ParseAsync(stream);
        Parse(doc.RootElement);
    }

    public void Parse(JsonElement root)
    {
        Tasks.Clear();
        ChainRules.Clear();
        Settings = null;

        if (!root.TryGetProperty("Data", out var data)) return;

        if (data.TryGetProperty("DailyTasksV2", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tasks.EnumerateArray())
            {
                var task = new DailyTradeTask
                {
                    TaskId = DataService.GetString(t, "TaskId"),
                    IsEnabled = DataService.GetBool(t, "IsEnabled", true),
                    StreakCountMin = DataService.GetInt(t, "StreakCountMin"),
                    StreakCountMax = DataService.GetInt(t, "StreakCountMax"),
                    UIOrder = DataService.GetInt(t, "UIOrder"),
                };
                if (t.TryGetProperty("Steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in steps.EnumerateArray())
                    {
                        task.Steps.Add(new DailyTradeStep
                        {
                            RequirementItemValue = DataService.GetInt(s, "RequirementItemValue"),
                            RewardItemValue = DataService.GetInt(s, "RewardItemValue"),
                            RefreshEnabled = DataService.GetBool(s, "RefreshEnabled"),
                            RefreshCosts = ReadIntArray(s, "RefreshCosts"),
                            AlgorithmAttempts = ReadStringArray(s, "AlgorithmAttempts"),
                        });
                    }
                }
                Tasks.Add(task);
            }
        }

        if (data.TryGetProperty("DailyTasksV2MergeChains", out var chains) && chains.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in chains.EnumerateArray())
            {
                var rule = new DailyTradeChainRule
                {
                    ConfigKey = DataService.GetString(c, "ConfigKey"),
                    CanBeRequirement = DataService.GetBool(c, "CanBeRequirement"),
                    CanBeReward = DataService.GetBool(c, "CanBeReward"),
                    MinLevel = ReadNullableInt(c, "MinLevel"),
                    MaxLevel = ReadNullableInt(c, "MaxLevel"),
                    RequirementMultiplier = ReadDouble(c, "RequirementMultiplier", 1.0),
                    RewardMultiplier = ReadDouble(c, "RewardMultiplier", 1.0),
                    RewardOnlyIfInHotspotRequirement = DataService.GetBool(c, "RewardOnlyIfInHotspotRequirement"),
                    OnlyPossibleRequirementsHighPriority = DataService.GetBool(c, "OnlyPossibleRequirementsHighPriority"),
                    RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas = ReadStringArray(c, "RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas"),
                    RequireOnlyIfHaveProducer = ReadStringArray(c, "RequireOnlyIfHaveProducer"),
                };
                if (!string.IsNullOrEmpty(rule.ConfigKey))
                    ChainRules[rule.ConfigKey] = rule;
            }
        }

        if (data.TryGetProperty("DailyTasksV2Settings", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            Settings = new DailyTradeSettings
            {
                NextHotspotsCount = DataService.GetInt(st, "NextHotspotsCount", 6),
                MaxLevelDifferenceFromHotspotToRemoveRequirementItem =
                    DataService.GetInt(st, "MaxLevelDifferenceFromHotspotToRemoveRequirementItem", 3),
                LastGeneratedTaskRewardItemsHistoryCountMaxSize =
                    DataService.GetInt(st, "LastGeneratedTaskRewardItemsHistoryCountMaxSize", 4),
                MaxStreakCount = DataService.GetInt(st, "MaxStreakCount", 4),
                AgeValues = ReadIntArray(st, "AgeValues"),
                AlgorithmAttemptsDefault = ReadStringArray(st, "AlgorithmAttemptsDefault"),
                SortingRangeForPossibleRequirementsHighPriority =
                    DataService.GetInt(st, "SortingRangeForPossibleRequirementsHighPriority", 5),
                SortingRangeGeneric = DataService.GetInt(st, "SortingRangeGeneric", 2),
            };
        }
    }

    private static List<int> ReadIntArray(JsonElement el, string prop)
    {
        var list = new List<int>();
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) list.Add(v.GetInt32());
        return list;
    }

    private static List<string> ReadStringArray(JsonElement el, string prop)
    {
        var list = new List<string>();
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }

    private static int? ReadNullableInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double ReadDouble(JsonElement el, string prop, double def)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
}
