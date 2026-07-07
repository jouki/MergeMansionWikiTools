using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

// ── Predictor output models ──

public class PredictorCandidate
{
    public string ChainKey { get; set; } = "";
    public int Level { get; set; }
    public int? Value { get; set; }
    /// <summary>"High" / "Normal" (Low is not distinguished in Phase 1).</summary>
    public string Priority { get; set; } = "Normal";
}

public class PredictorRejection
{
    public string ChainKey { get; set; } = "";
    public int Level { get; set; }
    public string Reason { get; set; } = "";
}

public class PredictorResult
{
    public List<PredictorCandidate> ReqHigh { get; } = new();
    public List<PredictorCandidate> ReqNormal { get; } = new();
    public List<PredictorCandidate> ReqLow { get; } = new();
    public List<PredictorCandidate> RwdHigh { get; } = new();
    public List<PredictorCandidate> RwdNormal { get; } = new();
    public List<PredictorCandidate> RwdLow { get; } = new();
    public List<PredictorRejection> RejectedRequirements { get; } = new();
    public List<PredictorRejection> RejectedRewards { get; } = new();
    public List<string> Notes { get; } = new();
}

public class PredictorInput
{
    public DailyTradeSettings Settings { get; set; } = new();
    public List<PredictorBoardItem> Board { get; set; } = new();
    public Dictionary<string, HashSet<int>> BlockedLevels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ChainsWithProducerHeld { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> LastRewardChainKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Chains already used as requirement / reward by the currently visible trades (both
    /// queues). Mirrors the game's existingRequirement/RewardItemIds → RemoveExisting* filters.</summary>
    public HashSet<string> ExistingRequirementChainKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExistingRewardChainKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, DailyTradeChainRule?> RuleOf { get; set; } = _ => null;
}

/// <summary>
/// Deterministic (Phase 1) Daily Trade predictor. Produces eligible requirement/reward candidate
/// lists from board + progress + config, WITHOUT the numeric scoring/ordering (Phase 2). Filters
/// mirror the reverse-engineered DailyTasksV2Utils pipeline; exact H/N/L split and Score are
/// deferred (config curves + seeded sampler — see DailyTrade.md §15 / RESUME).
/// </summary>
public class DailyTradePredictorEngine
{
    /// <summary>chainKey → set of levels blocked because one of the player's active hotspot tasks
    /// needs that chain within ±maxLevelDiff. The caller passes the tasks the player currently sees
    /// as their hotspots (selected in the UI), not a fixed look-ahead window.</summary>
    public static Dictionary<string, HashSet<int>> ComputeBlockedLevels(
        IEnumerable<LuaTask> hotspotTasks, int maxLevelDiff,
        Func<string, string?> chainOf, Func<string, int> levelOf)
    {
        var blocked = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in hotspotTasks)
            foreach (var itemType in task.Requirements.Keys)
            {
                var chain = chainOf(itemType);
                if (string.IsNullOrEmpty(chain)) continue;
                var lvl = levelOf(itemType);
                if (!blocked.TryGetValue(chain!, out var set))
                    blocked[chain!] = set = new HashSet<int>();
                for (int l = lvl - maxLevelDiff; l <= lvl + maxLevelDiff; l++) set.Add(l);
            }
        return blocked;
    }

    /// <summary>Selects the task template for the given streak + queue. The two daily queues are
    /// distinguished by UIOrder (0 = Queue 1, 1 = Queue 2), not by the internal Easy/Hard name.</summary>
    public static DailyTradeTask? SelectTask(IReadOnlyList<DailyTradeTask> tasks, int streak, int uiOrder)
    {
        return tasks.FirstOrDefault(t =>
            t.UIOrder == uiOrder && streak >= t.StreakCountMin && streak <= t.StreakCountMax);
    }

    /// <summary>Applies the deterministic requirement filter pipeline and populates
    /// res.ReqHigh/ReqNormal + res.RejectedRequirements (with reasons).</summary>
    public static void BuildRequirementLists(PredictorInput input, PredictorResult res)
    {
        // Pass 1: per-item filters (config eligibility, level bounds, producer gate, hotspot block).
        var survivors = new List<(PredictorBoardItem item, DailyTradeChainRule rule)>();
        foreach (var b in input.Board)
        {
            var rule = input.RuleOf(b.ChainKey);
            if (rule == null || !rule.CanBeRequirement)
            { res.RejectedRequirements.Add(Rej(b, "not a requirement chain")); continue; }
            if ((rule.MinLevel is int mn && b.Level < mn) || (rule.MaxLevel is int mx && b.Level > mx))
            { res.RejectedRequirements.Add(Rej(b, $"level out of {rule.MinLevel?.ToString() ?? "-"}..{rule.MaxLevel?.ToString() ?? "-"}")); continue; }
            if (rule.RequireOnlyIfHaveProducer.Count > 0 && !input.ChainsWithProducerHeld.Contains(b.ChainKey))
            { res.RejectedRequirements.Add(Rej(b, "needs producer (RequireOnlyIfHaveProducer)")); continue; }
            if (input.BlockedLevels.TryGetValue(b.ChainKey, out var blk) && blk.Contains(b.Level))
            { res.RejectedRequirements.Add(Rej(b, "blocked by upcoming hotspot (±level)")); continue; }
            if (input.ExistingRequirementChainKeys.Contains(b.ChainKey))
            { res.RejectedRequirements.Add(Rej(b, "already requested by a current trade (RemoveExistingRequirementItems)")); continue; }
            survivors.Add((b, rule));
        }

        // Pass 2: same-chain under-leveled dedupe — keep highest surviving level per chain.
        var maxLevelPerChain = survivors
            .GroupBy(s => s.item.ChainKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(s => s.item.Level), StringComparer.OrdinalIgnoreCase);

        foreach (var (item, rule) in survivors)
        {
            if (item.Level < maxLevelPerChain[item.ChainKey])
            { res.RejectedRequirements.Add(Rej(item, "under-leveled duplicate (higher held)")); continue; }

            var cand = new PredictorCandidate { ChainKey = item.ChainKey, Level = item.Level };
            if (rule.OnlyPossibleRequirementsHighPriority) { cand.Priority = "High"; res.ReqHigh.Add(cand); }
            else { cand.Priority = "Normal"; res.ReqNormal.Add(cand); }
        }
    }

    /// <summary>Reward filter pipeline. hotspotRequiredChains → High; visibleHotspotAreas gates
    /// RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas; LastRewardChainKeys anti-repeat.</summary>
    public static void BuildRewardLists(PredictorInput input, HashSet<string> hotspotRequiredChains,
        HashSet<string> visibleHotspotAreas, PredictorResult res)
    {
        // One reward candidate per distinct chain held (level = highest held).
        var byChain = input.Board
            .GroupBy(b => b.ChainKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PredictorBoardItem { ChainKey = g.Key, Level = g.Max(x => x.Level) });

        foreach (var b in byChain)
        {
            var rule = input.RuleOf(b.ChainKey);
            if (rule == null || !rule.CanBeReward)
            { res.RejectedRewards.Add(Rej(b, "not a reward chain")); continue; }
            if (rule.RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas.Count > 0
                && !rule.RewardOnlyIfHasAtLeastOneVisibleHotspotInAreas.Any(visibleHotspotAreas.Contains))
            { res.RejectedRewards.Add(Rej(b, "no visible hotspot in required areas")); continue; }
            if (input.LastRewardChainKeys.Contains(b.ChainKey))
            { res.RejectedRewards.Add(Rej(b, "in last N rewards (anti-repeat)")); continue; }
            if (input.ExistingRewardChainKeys.Contains(b.ChainKey))
            { res.RejectedRewards.Add(Rej(b, "already rewarded by a current trade (RemoveExistingRewardItems)")); continue; }

            var cand = new PredictorCandidate { ChainKey = b.ChainKey, Level = b.Level };
            if (hotspotRequiredChains.Contains(b.ChainKey)) { cand.Priority = "High"; res.RwdHigh.Add(cand); }
            else { cand.Priority = "Normal"; res.RwdNormal.Add(cand); }
        }
    }

    /// <summary>Full deterministic (Phase 1) prediction: eligible requirement + reward candidate
    /// lists. Candidates depend only on the board, the player's active hotspot tasks, and the chain
    /// rules — NOT on the queue or trade step (those only change the displayed target values), so a
    /// single result covers both daily queues.</summary>
    public static PredictorResult Predict(
        DailyTradeSettings settings, PredictorState state, IReadOnlyList<LuaArea> areas,
        Func<string, string?> chainOf, Func<string, int> levelOf,
        Func<string, DailyTradeChainRule?> ruleOf, Func<string, int, int?> reqValueOf,
        Func<string, int, int?> rwdValueOf, HashSet<string> producerChains)
    {
        var res = new PredictorResult();
        if (state.Board.Count == 0) res.Notes.Add("Board is empty — add items to see candidates.");

        // Active hotspot tasks = the ones the player selected as currently visible in-game.
        var area = areas.FirstOrDefault(a =>
            string.Equals(a.InternalName, state.AreaInternalName, StringComparison.OrdinalIgnoreCase));
        var activeIds = new HashSet<string>(state.ActiveTaskIds, StringComparer.OrdinalIgnoreCase);
        var hotspotTasks = area?.Tasks.Where(t => activeIds.Contains(t.Id)).ToList() ?? new List<LuaTask>();
        if (area != null && hotspotTasks.Count == 0)
            res.Notes.Add("No active tasks selected — hotspot blocking is off.");

        var blocked = ComputeBlockedLevels(hotspotTasks,
            settings.MaxLevelDifferenceFromHotspotToRemoveRequirementItem, chainOf, levelOf);

        // Items already used by the currently visible trades exclude themselves from the next roll
        // (game: existingRequirement/RewardItemIds — both queues never ask for / reward the same item).
        var exReq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exRwd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in new[] { state.Queue1Trade, state.Queue2Trade })
        {
            if (t?.Requirement?.ChainKey is { Length: > 0 } rk) exReq.Add(rk);
            if (t?.Reward?.ChainKey is { Length: > 0 } wk) exRwd.Add(wk);
        }

        var input = new PredictorInput
        {
            Settings = settings, Board = state.Board, BlockedLevels = blocked,
            ChainsWithProducerHeld = producerChains, RuleOf = ruleOf,
            LastRewardChainKeys = new HashSet<string>(state.LastRewardChainKeys, StringComparer.OrdinalIgnoreCase),
            ExistingRequirementChainKeys = exReq,
            ExistingRewardChainKeys = exRwd,
        };

        BuildRequirementLists(input, res);

        // Reward High = chains an active hotspot task currently needs; visible areas = the current area.
        var hotspotRequiredChains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (area != null) visibleAreas.Add(area.InternalName);
        foreach (var t in hotspotTasks)
            foreach (var it in t.Requirements.Keys)
            {
                var ch = chainOf(it);
                if (!string.IsNullOrEmpty(ch)) hotspotRequiredChains.Add(ch!);
            }

        BuildRewardLists(input, hotspotRequiredChains, visibleAreas, res);

        foreach (var c in res.ReqHigh.Concat(res.ReqNormal).Concat(res.ReqLow)) c.Value = reqValueOf(c.ChainKey, c.Level);
        foreach (var c in res.RwdHigh.Concat(res.RwdNormal).Concat(res.RwdLow)) c.Value = rwdValueOf(c.ChainKey, c.Level);
        return res;
    }

    private static PredictorRejection Rej(PredictorBoardItem b, string reason) =>
        new() { ChainKey = b.ChainKey, Level = b.Level, Reason = reason };
}
