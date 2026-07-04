using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Builds the <c>p.garageCleanupGrids</c> table (Module:Datatable/Various) from the GarageCleanups
/// section of events.json, and detects whether a grid changed vs. the live module (replay events
/// can change their item layout — both versions must be kept on the wiki).
///
/// Grid cell format: "chainIndex.level" where chainIndex indexes into the per-board <c>chains</c>
/// list. Item resolution reuses <see cref="DataService.ResolveChainDisplayNameFromItemType"/>
/// (wiki chain name incl. disambiguation) and <see cref="DataService.ItemLevels"/>.
///
/// Phase 1 (this file): build + generate Lua + detect. Phase 2 (later): (YYYY) split, event-page
/// table wrap, run-list, image moves — see _CONTEXT/App/GarageCleanupGrids.md.
/// </summary>
public sealed class GarageCleanupGridService
{
    private readonly DataService _data;
    private readonly WikiMappingCache? _wikiMapping;

    public GarageCleanupGridService(DataService data, WikiMappingCache? wikiMapping)
    {
        _data = data;
        _wikiMapping = wikiMapping;
    }

    /// <summary>One board's grid: ordered distinct chains (wiki names) + rows of "idx.level" cells.</summary>
    public sealed class BoardGrid
    {
        public List<string> Chains { get; } = new();          // Chains[0] is cell-index "1"
        public List<List<string>> Rows { get; } = new();      // each cell "chainIndex.level" or "" (empty)
    }

    /// <summary>
    /// What a built grid implies for the live module / event page (Phase 2b):
    /// <list type="bullet">
    /// <item><b>New</b> — base name unknown live → add plain "X Garage Cleanup" (no year).</item>
    /// <item><b>Identical</b> — new grid == the most-recent existing version → no change.</item>
    /// <item><b>NoteIdentical</b> — new grid == an OLDER version (not the latest) → add a bullet
    /// note on the page ("Garage Cleanup for &lt;new&gt; is identical to &lt;old&gt;."), no data added.</item>
    /// <item><b>Split</b> — first collision: rename the plain entry → "(OldYear)" and add "(NewYear)".</item>
    /// <item><b>AddVersion</b> — subsequent collision (year variants already exist): add "(NewYear)".</item>
    /// </list>
    /// Years come from the live <c>Module:Datatable/Events</c> archive (not events.json, which churns).
    /// </summary>
    public enum GridAction { New, Identical, NoteIdentical, Split, AddVersion }

    public sealed class GridChange
    {
        public string EventName { get; init; } = "";   // base name, e.g. "Sweet Mess Express Garage Cleanup"
        public GridAction Action { get; init; }
        public string? Detail { get; init; }
        public int? NewYear { get; init; }             // year of the new grid (Split/AddVersion/NoteIdentical)
        public int? OldYear { get; init; }             // year to assign the existing plain entry (Split)
        public int? IdenticalToYear { get; init; }     // NoteIdentical: which older year the new grid matches
        public bool YearUnresolved { get; init; }      // a needed year couldn't be derived from Datatable/Events
    }

    // ──────────────────────────────────────────────────────────── Build

    /// <summary>Builds grids keyed by event Name from the events.json <c>Data</c> element.</summary>
    public Dictionary<string, List<BoardGrid>> Build(JsonElement data)
    {
        var result = new Dictionary<string, List<BoardGrid>>(StringComparer.Ordinal);
        if (!data.TryGetProperty("GarageCleanups", out var gcs) || gcs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var ev in gcs.EnumerateArray())
        {
            if (ev.ValueKind != JsonValueKind.Object) continue;
            var name = (ev.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var boardGrids = BuildGridForGc(ev);
            if (boardGrids.Count > 0) result[name] = boardGrids;
        }
        return result;
    }

    /// <summary>Builds the per-board grids for a SINGLE GarageCleanup element (the per-GC core of
    /// <see cref="Build"/>). Used by the one-off replay bootstrap, which enumerates airings by GC id
    /// (§3.5) rather than by name (Build is name-keyed and would collapse same-name same-dump GCs).</summary>
    public List<BoardGrid> BuildGridForGc(JsonElement ev)
    {
        var boardGrids = new List<BoardGrid>();
        if (ev.ValueKind != JsonValueKind.Object
            || !ev.TryGetProperty("Boards", out var boards) || boards.ValueKind != JsonValueKind.Array)
            return boardGrids;

        foreach (var board in boards.EnumerateArray())
        {
            var grid = new BoardGrid();

            // Pass 1: resolve every cell to (chain, level); count per-chain frequency on this
            // board + remember first-appearance order (tie-breaker).
            var resolved = new List<List<(string? Chain, int Level)>>();
            var freq = new Dictionary<string, int>(StringComparer.Ordinal);
            var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            int seq = 0;
            if (board.TryGetProperty("Rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var rcells = new List<(string?, int)>();
                    if (row.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in items.EnumerateArray())
                        {
                            var itemType = it.ValueKind == JsonValueKind.String ? it.GetString() : null;
                            if (string.IsNullOrEmpty(itemType)) { rcells.Add((null, 0)); continue; }

                            var chain = _data.ResolveChainDisplayNameFromItemType(itemType, _wikiMapping);
                            var level = ResolveLevel(itemType);
                            rcells.Add((chain, level));
                            freq[chain] = freq.GetValueOrDefault(chain) + 1;
                            if (!firstSeen.ContainsKey(chain)) firstSeen[chain] = seq++;
                        }
                    }
                    resolved.Add(rcells);
                }
            }

            // Chain index order = most frequent first (ties keep first-appearance order).
            grid.Chains.AddRange(freq.Keys.OrderByDescending(k => freq[k]).ThenBy(k => firstSeen[k]));
            var idxMap = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < grid.Chains.Count; i++) idxMap[grid.Chains[i]] = i + 1;

            // Pass 2: emit cells with the frequency-ordered indices.
            foreach (var rcells in resolved)
            {
                var cells = new List<string>();
                foreach (var (chain, level) in rcells)
                    cells.Add(chain == null ? "" : $"{idxMap[chain]}.{level}");
                grid.Rows.Add(cells);
            }
            boardGrids.Add(grid);
        }
        return boardGrids;
    }

    private int ResolveLevel(string itemType)
    {
        if (_data.ItemLevels.TryGetValue(itemType, out var lvl)) return lvl;
        var m = Regex.Match(itemType, @"_(\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    // ──────────────────────────────────────────────────────────── Lua emit

    /// <summary>Emits the combined <c>p.garageCleanupGrids = { ... }</c> block (chains + rows + opaque
    /// per-level <c>rewards</c> body), events sorted by name. The reward body is re-indented to sit at
    /// the rewards field depth; a level with no rewards omits the field.</summary>
    public string GenerateLua(Dictionary<string, List<GcLevel>> levels)
    {
        var sb = new StringBuilder();
        sb.AppendLine("p.garageCleanupGrids = {");
        foreach (var (name, list) in levels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"\t[{Quote(name)}] = {{");
            for (int b = 0; b < list.Count; b++)
            {
                var lvl = list[b];
                sb.AppendLine($"\t\t-- Level {b + 1}");
                sb.AppendLine($"\t\t[{b + 1}] = {{");
                sb.AppendLine("\t\t\tchains = {");
                for (int c = 0; c < lvl.Chains.Count; c++)
                    sb.AppendLine($"\t\t\t\t[{c + 1}] = {Quote(lvl.Chains[c])},");
                sb.AppendLine("\t\t\t},");
                sb.AppendLine("\t\t\trows = {");
                for (int r = 0; r < lvl.Rows.Count; r++)
                    sb.AppendLine($"\t\t\t\t[{r + 1}] = {{{string.Join(", ", lvl.Rows[r].Select(Quote))}}},");
                sb.Append("\t\t\t}");
                if (lvl.RewardsBody != null)
                {
                    sb.AppendLine(",");
                    // Re-indent the opaque body structurally (by brace depth) so it sits at the rewards
                    // field depth REGARDLESS of the source indentation — a freshly-built body (1-tab base)
                    // and a round-tripped live body (deeper base) both normalize to the same canonical form.
                    sb.AppendLine($"\t\t\trewards = {ReindentRewardsBody(lvl.RewardsBody, "\t\t\t")}");
                }
                else sb.AppendLine();
                sb.AppendLine("\t\t},");
            }
            sb.AppendLine("\t},");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Composes the Module:Datatable/Various GC payload (<c>garageCleanupGrids</c> ONLY) from each
    /// event's airings (start + duration + disabled + combined grid). Naming per §3.2 via
    /// <see cref="GarageCleanupHistory.GroupRounds"/> (Round N) + <see cref="GarageCleanupHistory.DeriveVariantNames"/>
    /// (year / Month Year). A re-air whose grid is identical (grid+rewards+patterns) to an earlier holder
    /// stores NO grid. Run history (start/durationDays/disabled/identicalTo) lives in Module:Datatable/Events
    /// (A3/A4), not here. Shared by the one-off harness (airings from all dumps) and the live app
    /// (airings from wiki + active dump) — §3.5.</summary>
    public string ComposeGarageCleanup(Dictionary<string, List<GcAiring>> byBase)
    {
        var grids = ComposeGarageCleanupBlocks(byBase);
        return "local p = {}\n\n" + grids + "\nreturn p\n";
    }

    /// <summary>Same as <see cref="ComposeGarageCleanup"/> but returns ONLY the <c>p.garageCleanupGrids</c>
    /// block, so the live app (mode B) can splice it into the existing Module:Datatable/Various via
    /// <see cref="SpliceVarious"/> without touching its other data. Run data is emitted into
    /// Module:Datatable/Events via <see cref="BuildGcEventGroups"/> (A3c).</summary>
    public string ComposeGarageCleanupBlocks(Dictionary<string, List<GcAiring>> byBase)
    {
        var gridsOut = new Dictionary<string, List<GcLevel>>(StringComparer.Ordinal);

        foreach (var d in DescribeVariants(byBase))
            if (d.IdenticalTo == null) gridsOut[d.VariantName] = d.Grid;   // holder stores grid; identical re-air none

        return GenerateLua(gridsOut);
    }

    /// <summary>One airing's derived placement: its start, the wiki variant name (Various/invoke key) the
    /// naming logic assigns it, whether it reuses an earlier grid (identicalTo), and its grid. Used by
    /// <see cref="ComposeGarageCleanup"/> and by verification tooling that needs the name↔date mapping.</summary>
    public sealed record VariantDesc(string BaseName, DateTime Start, string VariantName, string? IdenticalTo, List<GcLevel> Grid);

    /// <summary>Derives, per airing, its variant name (year / Month Year / Round N) + identicalTo pointer —
    /// the single source of truth for naming, shared by emit and verification. See §3.2 / §3.5.</summary>
    public List<VariantDesc> DescribeVariants(Dictionary<string, List<GcAiring>> byBase)
    {
        var result = new List<VariantDesc>();
        foreach (var (baseName, airings) in byBase)
        {
            if (airings.Count == 0) continue;
            var ordered = airings.OrderBy(a => a.Start).ToList();
            var runs = BuildEventRuns(ordered.Select(a => (a.Start, a.Grid)).ToList());
            var reairStarts = runs.Where(r => r.IdenticalTo != null).Select(r => r.Start.Date).ToHashSet();
            var roundInputs = ordered.Select((a, i) =>
                new GarageCleanupHistory.GcRoundInput(i.ToString(CultureInfo.InvariantCulture), baseName, a.Start)).ToList();
            var rounds = GarageCleanupHistory.GroupRounds(roundInputs);
            var variants = ordered.Select((a, i) =>
                new GarageCleanupHistory.GcVariant(a.Start, rounds[i.ToString(CultureInfo.InvariantCulture)])).ToList();
            var names = GarageCleanupHistory.DeriveVariantNames(baseName, variants, reairStarts);
            for (int i = 0; i < ordered.Count; i++)
            {
                var run = runs.First(r => r.Start == ordered[i].Start);
                result.Add(new VariantDesc(baseName, ordered[i].Start, names[variants[i]], run.IdenticalTo, ordered[i].Grid));
            }
        }
        return result;
    }

    /// <summary>Builds the GC <see cref="EventScheduleGroup"/>s for Module:Datatable/Events (A3c). For each base
    /// name: sort airings chronologically, run <see cref="BuildEventRuns"/> (computes <c>identicalTo</c> against
    /// the earliest identical-grid holder), and emit ONE group (Category "Garage Cleanup", no Parent — the parent
    /// is derived from the name) whose runs carry Start, Duration (from DurationDays), Disabled, and IdenticalTo
    /// ("DD.MM.YYYY", null for the grid holder). This replaces the old <c>garageCleanupRuns</c> table (A4).</summary>
    public List<EventScheduleGroup> BuildGcEventGroups(Dictionary<string, List<GcAiring>> mergedAirings)
    {
        var groups = new List<EventScheduleGroup>();
        foreach (var (baseName, airings) in mergedAirings)
        {
            if (airings.Count == 0) continue;
            var ordered = airings.OrderBy(a => a.Start).ToList();
            var runs = BuildEventRuns(ordered.Select(a => (a.Start, a.Grid)).ToList());
            var idByStart = runs.ToDictionary(r => r.Start, r => r.IdenticalTo);

            var group = new EventScheduleGroup { Name = baseName, Category = "Garage Cleanup" };
            foreach (var a in ordered)
                group.Runs.Add(new EventScheduleRun(
                    a.Start, TimeSpan.FromDays(a.DurationDays), "gc",
                    Disabled: a.Disabled,
                    IdenticalTo: idByStart.GetValueOrDefault(a.Start)));
            groups.Add(group);
        }
        return groups;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Re-indents an opaque Lua table body (the per-level rewards block) to canonical form: each
    /// line's indent = its brace-nesting depth (closing-brace lines dedent by one), prefixed with
    /// <paramref name="baseIndent"/> on every line except the first (which sits right after "rewards = ").
    /// String-aware brace counting; blank lines dropped. Normalizes any source indentation so repeated
    /// parse→emit cycles stay stable instead of accreting tabs.</summary>
    private static string ReindentRewardsBody(string body, string baseIndent)
    {
        var lines = body.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var sb = new StringBuilder();
        int depth = 0;
        bool first = true;
        foreach (var raw in lines)
        {
            var trimmed = raw.TrimStart('\t', ' ');
            if (trimmed.Length == 0) continue;
            int indent = (trimmed[0] == '}' || trimmed[0] == ')') ? Math.Max(0, depth - 1) : depth;
            if (first) { sb.Append(trimmed); first = false; }
            else { sb.Append('\n').Append(baseIndent).Append('\t', indent).Append(trimmed); }
            bool inStr = false; char sc = '\0';
            for (int c = 0; c < trimmed.Length; c++)
            {
                char ch = trimmed[c];
                if (inStr) { if (ch == sc && trimmed[c - 1] != '\\') inStr = false; continue; }
                if (ch == '"' || ch == '\'') { inStr = true; sc = ch; }
                else if (ch == '{' || ch == '(') depth++;
                else if (ch == '}' || ch == ')') depth--;
            }
        }
        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────── Rewards (Phase 3)

    public sealed class RewardEntry
    {
        public string? Chain { get; init; }     // wiki chain name (item reward)
        public int Level { get; init; }
        public int Amount { get; init; }
        public string? Currency { get; init; }  // "Coins"/"Gems" for currency rewards (Chain null)
    }

    public sealed class RewardGroup
    {
        public string Type { get; init; } = "";  // "Lines" / "Diagonals" / "Full"
        public int Count { get; init; }          // coefficient = number of placements enumerated by the game
        public List<RewardEntry> Rewards { get; } = new();
    }

    public sealed class LevelRewards
    {
        public List<RewardGroup> Groups { get; } = new();
        public List<RewardEntry> SlotFill { get; } = new();   // per-cleared-slot reward (e.g. 10 coins)
    }

    /// <summary>One GC board level: grid (chains+rows) + its rewards as an opaque Lua body. The reward
    /// body is kept verbatim (no reward parser) — mirror of the replay harness; identity comparison
    /// (<see cref="FirstDifference(List{GcLevel}, List{GcLevel})"/>) ignores it.</summary>
    public sealed class GcLevel
    {
        public List<string> Chains { get; } = new();
        public List<List<string>> Rows { get; } = new();
        public string? RewardsBody { get; set; }   // "{ groups = {...}, slotFill = {...} }" or null
    }

    /// <summary>Renders a level's rewards as the opaque body stored in <see cref="GcLevel.RewardsBody"/>.
    /// One field per line at a single tab of base indent; <see cref="GenerateLua(Dictionary{string, List{GcLevel}})"/>
    /// re-indents to the embedding depth. Format mirrors the replay reference (Various_replay.lua).</summary>
    internal static string RewardsBodyLua(LevelRewards lr)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("\tgroups = {\n");
        foreach (var g in lr.Groups)
            sb.Append($"\t\t{{ type = {Quote(g.Type)}, count = {g.Count}, rewards = {{{string.Join(", ", g.Rewards.Select(RewardLua))}}} }},\n");
        sb.Append("\t},\n");
        if (lr.SlotFill.Count > 0)
            sb.Append($"\tslotFill = {{{string.Join(", ", lr.SlotFill.Select(RewardLua))}}},\n");
        sb.Append("}");
        return sb.ToString();
    }

    /// <summary>Zips grids (<see cref="Build"/>) + rewards (<see cref="BuildRewards"/>) into per-level
    /// <see cref="GcLevel"/> by (name, level index). A level with no matching reward → RewardsBody null.</summary>
    public static Dictionary<string, List<GcLevel>> Combine(
        Dictionary<string, List<BoardGrid>> grids,
        Dictionary<string, List<LevelRewards>> rewards)
    {
        var result = new Dictionary<string, List<GcLevel>>(StringComparer.Ordinal);
        foreach (var (name, boards) in grids)
        {
            rewards.TryGetValue(name, out var rw);
            result[name] = CombineLevels(boards, rw ?? new List<LevelRewards>());
        }
        return result;
    }

    /// <summary>Zips one GC's per-board grids + per-level rewards into <see cref="GcLevel"/> list (a level
    /// with no matching reward → RewardsBody null). Per-GC core of <see cref="Combine"/>; used by the
    /// one-off replay bootstrap which works per GC id (§3.5).</summary>
    public static List<GcLevel> CombineLevels(List<BoardGrid> grids, List<LevelRewards> rewards)
    {
        var levels = new List<GcLevel>(grids.Count);
        for (int i = 0; i < grids.Count; i++)
        {
            var lvl = new GcLevel();
            lvl.Chains.AddRange(grids[i].Chains);
            lvl.Rows.AddRange(grids[i].Rows);
            if (i < rewards.Count) lvl.RewardsBody = RewardsBodyLua(rewards[i]);
            levels.Add(lvl);
        }
        return levels;
    }

    /// <summary>
    /// Builds reward tables keyed by event Name. Per board (= level) reads each pattern group
    /// (Pattern1..4 in the level's PatternSet): the coefficient is the number of placements in the
    /// group — the game pre-enumerates every row/column/diagonal as a separate pattern entry, so
    /// "Lines" = 10 (5 rows + 5 columns), "Diagonals" = 2, "Full" = 1 — and the reward is the
    /// group's (uniform) reward. Requires events.json dumped with resolved PatternSets/SlotFillRewards
    /// (dumper Phase 3: MetaEventSerializer resolves the MetaRefs).
    /// </summary>
    public Dictionary<string, List<LevelRewards>> BuildRewards(JsonElement data)
    {
        var result = new Dictionary<string, List<LevelRewards>>(StringComparer.Ordinal);
        if (!data.TryGetProperty("GarageCleanups", out var gcs) || gcs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var ev in gcs.EnumerateArray())
        {
            if (ev.ValueKind != JsonValueKind.Object) continue;
            var name = (ev.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var levels = BuildRewardsForGc(ev);
            if (levels.Count > 0) result[name] = levels;
        }
        return result;
    }

    /// <summary>Builds per-level reward data for a single GarageCleanup element (events.json or an old
    /// dump). Returns empty if PatternSets are unresolved (bare id strings) or absent.</summary>
    public List<LevelRewards> BuildRewardsForGc(JsonElement ev)
    {
        var levels = new List<LevelRewards>();
        if (ev.ValueKind != JsonValueKind.Object) return levels;
        if (!ev.TryGetProperty("PatternSets", out var sets) || sets.ValueKind != JsonValueKind.Array) return levels;
        // Skip un-resolved dumps (PatternSets still bare id strings → dumper Phase 3 not applied).
        if (sets.GetArrayLength() > 0 && sets[0].ValueKind != JsonValueKind.Object) return levels;

        var slotFills = ev.TryGetProperty("SlotFillRewards", out var sf) && sf.ValueKind == JsonValueKind.Array
            ? sf.EnumerateArray().ToList() : new List<JsonElement>();

        int lvlIdx = 0;
        foreach (var set in sets.EnumerateArray())
        {
            var lr = new LevelRewards();
            if (set.ValueKind == JsonValueKind.Object)
            {
                foreach (var grpName in new[] { "Pattern1", "Pattern2", "Pattern3", "Pattern4" })
                {
                    if (!set.TryGetProperty(grpName, out var grp) || grp.ValueKind != JsonValueKind.Array) continue;
                    var patterns = grp.EnumerateArray().ToList();
                    if (patterns.Count == 0) continue;
                    var group = new RewardGroup { Type = ResolvePatternType(patterns[0]), Count = patterns.Count };
                    if (patterns[0].TryGetProperty("Reward", out var reward))   // uniform within a group
                        group.Rewards.AddRange(ParseRewards(reward));
                    lr.Groups.Add(group);
                }
            }
            if (lvlIdx < slotFills.Count)
                lr.SlotFill.AddRange(ParseRewards(slotFills[lvlIdx]));
            levels.Add(lr);
            lvlIdx++;
        }
        return levels;
    }

    private static string ResolvePatternType(JsonElement pattern)
    {
        var loc = pattern.TryGetProperty("NameLocKey", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (string.IsNullOrEmpty(loc)) return "Pattern";
        var i = loc.LastIndexOf('_');   // "GarageCleanup_Pattern_Lines" → "Lines"
        return i >= 0 && i + 1 < loc.Length ? loc[(i + 1)..] : loc;
    }

    /// <summary>Parses a GarageCleanupRewardInfo element's Rewards list into resolved entries.</summary>
    private List<RewardEntry> ParseRewards(JsonElement rewardInfo)
    {
        var list = new List<RewardEntry>();
        if (rewardInfo.ValueKind != JsonValueKind.Object) return list;
        if (!rewardInfo.TryGetProperty("Rewards", out var rewards) || rewards.ValueKind != JsonValueKind.Array) return list;

        foreach (var r in rewards.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in r.EnumerateObject())   // single-key: RewardItem / RewardCoins / RewardGems
            {
                var body = prop.Value;
                int amount = body.TryGetProperty("Amount", out var a) && a.TryGetInt32(out var av) ? av : 1;
                // Reward item key differs across dumper versions: "ItemDef" (current dumper) vs
                // "ItemRef" (older dumps that already resolved PatternSets). Accept either.
                if (prop.Name == "RewardItem"
                    && (body.TryGetProperty("ItemDef", out var itemDef) || body.TryGetProperty("ItemRef", out itemDef))
                    && itemDef.ValueKind == JsonValueKind.String)
                {
                    var itemType = itemDef.GetString()!;
                    list.Add(new RewardEntry
                    {
                        Chain = _data.ResolveChainDisplayNameFromItemType(itemType, _wikiMapping),
                        Level = ResolveLevel(itemType),
                        Amount = amount,
                    });
                }
                else if (prop.Name == "RewardCoins") list.Add(new RewardEntry { Currency = "Coins", Amount = amount });
                else if (prop.Name == "RewardGems") list.Add(new RewardEntry { Currency = "Gems", Amount = amount });
                break;
            }
        }
        return list;
    }

    private static string RewardLua(RewardEntry r) =>
        r.Currency != null
            ? $"{{ currency = {Quote(r.Currency)}, amount = {r.Amount} }}"
            : $"{{ chain = {Quote(r.Chain ?? "")}, level = {r.Level}, amount = {r.Amount} }}";

    /// <summary>
    /// Builds the merged <c>p.garageCleanupGrids</c> block for writing: starts from the LIVE entries
    /// (preserves historical year-suffixed versions not in events.json) and overlays the generated
    /// ones. <b>Changed</b> grids are SKIPPED — a replay that changed the layout needs a version
    /// split (Phase 2b), so we never silently overwrite the old grid here.
    /// </summary>
    public string MergeForWrite(Dictionary<string, List<GcLevel>> generated, string liveModuleContent, List<GridChange> changes)
    {
        var merged = new Dictionary<string, List<GcLevel>>(ParseLive(liveModuleContent), StringComparer.Ordinal);
        var byName = changes.ToDictionary(c => c.EventName, c => c, StringComparer.Ordinal);

        foreach (var (name, grid) in generated)
        {
            if (!byName.TryGetValue(name, out var ch)) { merged[name] = grid; continue; }
            switch (ch.Action)
            {
                case GridAction.New:
                    merged[name] = grid;                       // brand-new plain entry
                    break;

                case GridAction.Identical:
                case GridAction.NoteIdentical:
                    break;                                     // no data change (note is a page-only edit)

                case GridAction.Split:
                    // First collision: rename plain → "(OldYear)" and add "(NewYear)".
                    // If a needed year is missing, leave the data untouched (the dialog flags it).
                    if (ch.YearUnresolved || ch.NewYear == null || ch.OldYear == null) break;
                    if (merged.TryGetValue(name, out var plainGrid))
                    {
                        merged.Remove(name);
                        merged[$"{name} ({ch.OldYear})"] = plainGrid;
                    }
                    merged[$"{name} ({ch.NewYear})"] = grid;
                    break;

                case GridAction.AddVersion:
                    if (ch.YearUnresolved || ch.NewYear == null) break;
                    merged[$"{name} ({ch.NewYear})"] = grid;
                    break;
            }
        }

        NormalizeRedundantPlain(merged);
        return GenerateLua(merged);
    }

    /// <summary>
    /// Self-heals the transitional inconsistency where a plain "X Garage Cleanup" key coexists with
    /// year-variant keys: the plain is the un-migrated current run. If it is logically identical to
    /// an existing year variant, drop it (the variant already captures it). Leaves plain-only events
    /// untouched.
    /// </summary>
    private static void NormalizeRedundantPlain(Dictionary<string, List<GcLevel>> merged)
    {
        var baseNames = merged.Keys.Where(k => !TryParseYearKey(k, out _, out _)).ToList();
        foreach (var bn in baseNames)
        {
            if (!merged.TryGetValue(bn, out var plainGrid)) continue;
            var variants = merged
                .Where(kv => TryParseYearKey(kv.Key, out var b, out _) && string.Equals(b, bn, StringComparison.Ordinal))
                .Select(kv => kv.Value)
                .ToList();
            if (variants.Count == 0) continue;                      // plain-only event — keep
            if (variants.Any(v => FirstDifference(plainGrid, v) == null))
                merged.Remove(bn);                                 // redundant plain — a variant already holds it
        }
    }

    /// <summary>Replaces the existing <c>p.&lt;varName&gt; = {...}</c> block in the module with
    /// <paramref name="newBlock"/> (or inserts it before <c>return p</c> if absent). Null on failure.
    /// Used for both <c>garageCleanupGrids</c> and <c>garageCleanupRewards</c>.</summary>
    public static string? SpliceVarious(string moduleContent, string newBlock, string varName = "garageCleanupGrids")
    {
        int start = moduleContent.IndexOf("p." + varName, StringComparison.Ordinal);
        if (start < 0)
        {
            int ret = moduleContent.LastIndexOf("return p", StringComparison.Ordinal);
            return ret < 0
                ? moduleContent.TrimEnd() + "\n\n" + newBlock.TrimEnd() + "\n"
                : moduleContent[..ret] + newBlock.TrimEnd() + "\n\n" + moduleContent[ret..];
        }
        int brace = moduleContent.IndexOf('{', start);
        var inside = ExtractBalanced(moduleContent, brace);
        if (inside == null) return null;
        int end = brace + 1 + inside.Length + 1;   // index just past the closing '}'
        return moduleContent[..start] + newBlock.TrimEnd() + moduleContent[end..];
    }

    /// <summary>Removes the whole <c>p.&lt;varName&gt; = { ... }</c> statement from the module (balanced-brace
    /// aware). Used at rollout to drop the obsolete <c>garageCleanupRewards</c> table once rewards live inside
    /// <c>garageCleanupGrids</c>. No-op if absent.</summary>
    public static string RemoveVar(string moduleContent, string varName)
    {
        int i = moduleContent.IndexOf("p." + varName, StringComparison.Ordinal);
        if (i < 0) return moduleContent;
        int brace = moduleContent.IndexOf('{', i);
        if (brace < 0) return moduleContent;
        var inside = ExtractBalanced(moduleContent, brace);
        if (inside == null) return moduleContent;
        int end = brace + 1 + inside.Length + 1;
        while (end < moduleContent.Length && (moduleContent[end] == '\n' || moduleContent[end] == '\r')) end++;
        return moduleContent[..i].TrimEnd() + "\n\n" + moduleContent[end..];
    }

    // ──────────────────────────────────────────────────────────── Detection

    /// <summary>
    /// Compares each built grid against the live <c>p.garageCleanupGrids</c>. Comparison is by
    /// (chainName, level) at each position — chain INDICES are reconstructed through each version's
    /// own <c>chains</c> table, so a differently-numbered but identical chain is NOT a false diff.
    /// </summary>
    public List<GridChange> Detect(Dictionary<string, List<GcLevel>> grids, string liveModuleContent,
        string? datatableEventsContent = null)
    {
        var live = ParseLive(liveModuleContent);
        var years = string.IsNullOrEmpty(datatableEventsContent)
            ? new Dictionary<string, List<int>>(StringComparer.Ordinal)
            : ParseEventYears(datatableEventsContent!);
        var changes = new List<GridChange>();

        foreach (var (name, mine) in grids.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // Gather every live version of this base name: the plain key + any "Name (YYYY)" keys.
            var plain = live.TryGetValue(name, out var pg) ? pg : null;
            var variants = new List<(int Year, List<GcLevel> Grid)>();
            foreach (var (k, g) in live)
                if (TryParseYearKey(k, out var bn, out var yr) && string.Equals(bn, name, StringComparison.Ordinal))
                    variants.Add((yr, g));
            variants.Sort((a, b) => a.Year.CompareTo(b.Year));

            if (plain == null && variants.Count == 0)
            {
                changes.Add(new GridChange { EventName = name, Action = GridAction.New });
                continue;
            }

            // Archive years for this event (fallback to the parent seasonal event's years).
            var evYears = ResolveYears(name, years);
            int? newYear = evYears.Count > 0 ? evYears.Max() : (int?)null;

            // The most-recent existing version is the PLAIN entry when present (the un-suffixed key
            // always holds the current run); only fall back to the highest-year variant if there is
            // no plain. (Comparing only against the highest variant would miss mine == plain and
            // spuriously add a duplicate "(year)" — the inconsistency seen on Sweet Mess Express.)
            var mostRecentGrid = plain ?? variants[^1].Grid;

            // Identical to the most-recent existing version → nothing to do.
            if (FirstDifference(mine, mostRecentGrid) == null)
            {
                changes.Add(new GridChange { EventName = name, Action = GridAction.Identical });
                continue;
            }

            // Identical to an existing (older) year variant → page-only bullet note, no data added.
            var olderMatch = variants
                .Where(v => FirstDifference(mine, v.Grid) == null)
                .Select(v => (int?)v.Year).Max();
            if (olderMatch != null)
            {
                changes.Add(new GridChange
                {
                    EventName = name,
                    Action = GridAction.NoteIdentical,
                    NewYear = newYear,
                    IdenticalToYear = olderMatch,
                    YearUnresolved = newYear == null,
                    Detail = $"identical to {olderMatch}",
                });
                continue;
            }

            // Genuinely different from every existing version. If a plain entry exists it is the
            // previous run → migrate it to its year (Split) and add the new year; otherwise just add
            // the new version (variants already exist, no plain to migrate).
            bool hasPlain = plain != null;
            int? oldYear = null;
            if (hasPlain)
            {
                // Latest archive year strictly before the new year = the plain entry's run year.
                var prior = evYears.Where(y => newYear == null || y < newYear).ToList();
                oldYear = prior.Count > 0 ? prior.Max() : (int?)null;
            }
            changes.Add(new GridChange
            {
                EventName = name,
                Action = hasPlain ? GridAction.Split : GridAction.AddVersion,
                NewYear = newYear,
                OldYear = oldYear,
                YearUnresolved = newYear == null || (hasPlain && oldYear == null),
                Detail = FirstDifference(mine, mostRecentGrid),
            });
        }
        return changes;
    }

    /// <summary>Archive years for an event, falling back to its parent seasonal event ("X Garage Cleanup" → "X").</summary>
    private static List<int> ResolveYears(string gcName, Dictionary<string, List<int>> years)
    {
        if (years.TryGetValue(gcName, out var direct) && direct.Count > 0) return direct;
        const string suffix = " Garage Cleanup";
        if (gcName.EndsWith(suffix, StringComparison.Ordinal))
        {
            var parent = gcName[..^suffix.Length];
            if (years.TryGetValue(parent, out var pe) && pe.Count > 0) return pe;
        }
        return new List<int>();
    }

    /// <summary>Returns null if grids are logically identical, else a human-readable first diff. Compares
    /// by (chainName, level) at each position — chain INDICES are reconstructed through each level's own
    /// <c>chains</c> table, so a differently-numbered but identical chain is NOT a false diff. The opaque
    /// <see cref="GcLevel.RewardsBody"/> is intentionally NOT compared (reward identity = Plan 2).</summary>
    private static string? FirstDifference(List<GcLevel> mine, List<GcLevel> theirs)
    {
        if (mine.Count != theirs.Count)
            return $"board count {theirs.Count} → {mine.Count}";

        for (int b = 0; b < mine.Count; b++)
        {
            var a = mine[b]; var t = theirs[b];
            if (a.Rows.Count != t.Rows.Count)
                return $"board {b + 1}: row count {t.Rows.Count} → {a.Rows.Count}";

            for (int r = 0; r < a.Rows.Count; r++)
            {
                if (a.Rows[r].Count != t.Rows[r].Count)
                    return $"board {b + 1}, row {r + 1}: column count {t.Rows[r].Count} → {a.Rows[r].Count}";

                for (int c = 0; c < a.Rows[r].Count; c++)
                {
                    var an = ResolveCell(a, a.Rows[r][c]);
                    var tn = ResolveCell(t, t.Rows[r][c]);
                    if (an != tn)
                        return $"board {b + 1}, row {r + 1}, col {c + 1}: {tn} → {an}";
                }
            }
        }

        // Identity also covers rewards + pattern groups (RewardsBody encodes group Type/Count + rewards).
        // Canonicalize indentation before comparing so a re-indented live body isn't a false diff.
        for (int b = 0; b < mine.Count; b++)
        {
            var ar = mine[b].RewardsBody == null ? "" : ReindentRewardsBody(mine[b].RewardsBody!, "");
            var tr = theirs[b].RewardsBody == null ? "" : ReindentRewardsBody(theirs[b].RewardsBody!, "");
            if (ar != tr) return $"board {b + 1}: rewards differ";
        }
        return null;
    }

    /// <summary>Test bridge for the private grid+reward identity comparison.</summary>
    internal static string? FirstDifferenceForTest(List<GcLevel> a, List<GcLevel> b) => FirstDifference(a, b);

    /// <summary>One run of a GC event on the wiki: its start (DD.MM.YYYY identity) and, when it reuses an
    /// earlier run's grid verbatim, the holder run's start date (§ identicalTo). No gridKey — names are
    /// derived elsewhere (GarageCleanupHistory.DeriveVariantNames).</summary>
    public sealed class GcRun
    {
        public DateTime Start { get; init; }
        public string? IdenticalTo { get; init; }
    }

    /// <summary>One GC airing carried through the mode-B / harness pipeline: its start, run duration (days),
    /// disabled flag, and combined per-level grid. Duration + disabled were added (A3a) so the run data the
    /// pipeline writes into Module:Datatable/Events carries the same fields the live module stores.</summary>
    public readonly record struct GcAiring(DateTime Start, double DurationDays, bool Disabled, List<GcLevel> Grid);

    /// <summary>Builds Events runs from chronologically-ordered airings (start + grid). A run whose grid
    /// equals an earlier run's grid (grid+rewards identity via FirstDifference) carries identicalTo = the
    /// EARLIEST matching holder's start (DD.MM.YYYY); chaining always points at the original holder.</summary>
    public static List<GcRun> BuildEventRuns(IReadOnlyList<(DateTime Start, List<GcLevel> Grid)> airingsChrono)
    {
        var runs = new List<GcRun>();
        var holders = new List<(DateTime Start, List<GcLevel> Grid)>();
        foreach (var (start, grid) in airingsChrono.OrderBy(a => a.Start))
        {
            var holder = holders.FirstOrDefault(h => FirstDifference(grid, h.Grid) == null);
            if (holder.Grid != null)
                runs.Add(new GcRun { Start = start, IdenticalTo = holder.Start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) });
            else { runs.Add(new GcRun { Start = start }); holders.Add((start, grid)); }
        }
        return runs;
    }

    /// <summary>"3.4" → "Machine Assembly L4" using the level's own chains table (index reconstruction).</summary>
    private static string ResolveCell(GcLevel grid, string cell)
    {
        if (string.IsNullOrEmpty(cell)) return "(empty)";
        var dot = cell.IndexOf('.');
        if (dot <= 0) return cell;
        if (!int.TryParse(cell.AsSpan(0, dot), out var idx)) return cell;
        var level = cell[(dot + 1)..];
        var chain = (idx >= 1 && idx <= grid.Chains.Count) ? grid.Chains[idx - 1] : $"chain#{idx}";
        return $"{chain} L{level}";
    }

    // ──────────────────────────────────────────────────────────── Live parser

    /// <summary>
    /// Parses the live <c>p.garageCleanupGrids</c> block into the same model. Tolerant of formatting;
    /// reads per-event boards (<c>[n] = { chains = {...}, rows = {...} }</c>).
    /// </summary>
    public static Dictionary<string, List<GcLevel>> ParseLive(string moduleContent)
    {
        var result = new Dictionary<string, List<GcLevel>>(StringComparer.Ordinal);
        int start = moduleContent.IndexOf("garageCleanupGrids", StringComparison.Ordinal);
        if (start < 0) return result;
        int brace = moduleContent.IndexOf('{', start);
        if (brace < 0) return result;
        var body = ExtractBalanced(moduleContent, brace);
        if (body == null) return result;

        // Each top-level event: ["Name"] = { ... }
        foreach (var ev in EnumerateEntries(body))
        {
            var key = MatchQuotedKey(ev.Key);
            if (key == null) continue;
            var evInside = ExtractBalanced(ev.Value, ev.Value.IndexOf('{'));
            if (evInside == null) continue;
            var boards = new List<GcLevel>();
            // boards: [n] = { chains = {...}, rows = {...}, rewards = { groups = {...}, slotFill = {...} } }
            foreach (var boardEntry in EnumerateEntries(evInside))
            {
                var boardInside = ExtractBalanced(boardEntry.Value, boardEntry.Value.IndexOf('{'));
                if (boardInside == null) continue;
                var grid = new GcLevel();
                var chainsBody = ExtractNamedTable(boardInside, "chains");
                var rowsBody = ExtractNamedTable(boardInside, "rows");
                if (chainsBody != null)
                    foreach (var ce in EnumerateEntries(chainsBody))
                    {
                        var v = MatchQuotedValue(ce.Value);
                        if (v != null) grid.Chains.Add(v);
                    }
                if (rowsBody != null)
                    foreach (var re in EnumerateEntries(rowsBody))
                    {
                        var inner = ExtractBalanced(re.Value, re.Value.IndexOf('{'));
                        var cells = new List<string>();
                        if (inner != null)
                            foreach (Match m in Regex.Matches(inner, "\"([^\"]*)\""))
                                cells.Add(m.Groups[1].Value);
                        grid.Rows.Add(cells);
                    }
                // Preserve the opaque rewards body verbatim (no reward parser). ExtractNamedTable strips
                // the outer braces, so re-wrap to keep RewardsBody a self-contained "{ ... }".
                var rewardsBody = ExtractNamedTable(boardInside, "rewards");
                if (rewardsBody != null) grid.RewardsBody = "{" + rewardsBody + "}";
                boards.Add(grid);
            }
            result[key] = boards;
        }
        return result;
    }

    /// <summary>One stored GC run read back from Module:Datatable/Events: its start, run duration (days),
    /// disabled flag, and (when it reuses an earlier run's grid) the holder run's start date as
    /// <c>identicalTo</c> ("DD.MM.YYYY"). The Events-module counterpart of <see cref="BuildGcEventGroups"/>.</summary>
    public readonly record struct GcEventRun(DateTime Start, double DurationDays, bool Disabled, DateTime? IdenticalTo);

    /// <summary>Parses the GC run history out of the live <c>Module:Datatable/Events</c> (A3d): the events whose
    /// <c>category == "Garage Cleanup"</c>, returning base name → ordered run list (start / durationDays / disabled
    /// / identicalTo). Run data now lives in Events, not in Various's old <c>garageCleanupRuns</c>. Reuses
    /// <see cref="LuaTableReader"/>.</summary>
    public static Dictionary<string, List<GcEventRun>> ParseGcRunsFromEvents(string eventsLua)
    {
        var result = new Dictionary<string, List<GcEventRun>>(StringComparer.Ordinal);
        var root = LuaTableReader.Parse(eventsLua);
        var events = root?.Tbl("events");
        if (events == null) return result;

        foreach (var obj in events.Array)
        {
            if (obj is not LuaTable e) continue;
            var category = e.Str("category")?.Trim();
            if (!string.Equals(category, "Garage Cleanup", StringComparison.Ordinal)) continue;
            var name = e.Str("name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var runs = e.Tbl("runs");
            if (runs == null) continue;

            var list = new List<GcEventRun>();
            foreach (var ro in runs.Array)
            {
                if (ro is not LuaTable run) continue;
                var st = run.Tbl("start");
                if (st == null) continue;
                int y = (int)(st.Num("year") ?? 0);
                if (y == 0) continue;
                int mo = (int)(st.Num("month") ?? 1);
                int d = (int)(st.Num("day") ?? 1);
                int h = (int)(st.Num("hour") ?? 0);
                int mi = (int)(st.Num("min") ?? 0);
                double dur = run.Num("durationDays") ?? 0;
                bool disabled = run.Get("disabled") is true;
                var idStr = run.Str("identicalTo");
                DateTime? id = string.IsNullOrEmpty(idStr) ? null : ParseDmy(idStr);
                try { list.Add(new GcEventRun(new DateTime(y, mo, d, h, mi, 0), dur, disabled, id)); }
                catch { /* invalid date → skip */ }
            }
            if (list.Count > 0) result[name] = list.OrderBy(r => r.Start).ToList();
        }
        return result;
    }

    /// <summary>Reconstructs the per-event airing set (start + duration + disabled + grid) from the LIVE wiki:
    /// run history from <c>Module:Datatable/Events</c> (A3d) and the grid layout from <c>garageCleanupGrids</c>
    /// in <paramref name="liveVarious"/>. The inverse of <see cref="ComposeGarageCleanup"/> + <see cref="BuildGcEventGroups"/>,
    /// so the live app (mode B, §3.5) can merge the active dump's airings with the existing wiki history and
    /// recompose. Each run's variant name is re-derived with the SAME naming logic
    /// (<see cref="GarageCleanupHistory.GroupRounds"/> + <see cref="GarageCleanupHistory.DeriveVariantNames"/>),
    /// so its grid is looked up deterministically; an identicalTo run reuses the holder run's grid.</summary>
    public static Dictionary<string, List<GcAiring>> ReconstructAirings(string liveVarious, string liveEvents)
    {
        var grids = ParseLive(liveVarious);
        var runs = ParseGcRunsFromEvents(liveEvents);
        var result = new Dictionary<string, List<GcAiring>>(StringComparer.Ordinal);

        foreach (var (baseName, runList) in runs)
        {
            var parsed = runList.OrderBy(r => r.Start).ToList();
            if (parsed.Count == 0) continue;

            var reairStarts = parsed.Where(r => r.IdenticalTo != null).Select(r => r.Start.Date).ToHashSet();
            var roundInputs = parsed.Select((r, i) =>
                new GarageCleanupHistory.GcRoundInput(i.ToString(CultureInfo.InvariantCulture), baseName, r.Start)).ToList();
            var rounds = GarageCleanupHistory.GroupRounds(roundInputs);
            var variants = parsed.Select((r, i) =>
                new GarageCleanupHistory.GcVariant(r.Start, rounds[i.ToString(CultureInfo.InvariantCulture)])).ToList();
            var names = GarageCleanupHistory.DeriveVariantNames(baseName, variants, reairStarts);
            var nameByStart = new Dictionary<DateTime, string>();
            for (int i = 0; i < parsed.Count; i++) nameByStart[parsed[i].Start] = names[variants[i]];

            var airings = new List<GcAiring>();
            foreach (var r in parsed)
            {
                var gridStart = r.IdenticalTo ?? r.Start;   // identicalTo → use the holder run's grid
                if (nameByStart.TryGetValue(gridStart, out var vn) && grids.TryGetValue(vn, out var g))
                    airings.Add(new GcAiring(r.Start, r.DurationDays, r.Disabled, g));
            }
            if (airings.Count > 0) result[baseName] = airings;
        }
        return result;
    }

    private static DateTime? ParseDmy(string s) =>
        DateTime.TryParseExact(s, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;

    private static DateTime? GcStartOf(JsonElement gc)
    {
        if (gc.TryGetProperty("ActivableParams", out var ap) && ap.ValueKind == JsonValueKind.Object
            && ap.TryGetProperty("Schedule", out var sc) && sc.ValueKind == JsonValueKind.Object
            && sc.TryGetProperty("Start", out var st) && st.ValueKind == JsonValueKind.String
            && DateTime.TryParse(st.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return null;
    }

    private static bool GcEnabledOf(JsonElement gc) =>
        gc.TryGetProperty("ActivableParams", out var ap) && ap.ValueKind == JsonValueKind.Object
        && ap.TryGetProperty("IsEnabled", out var e) && e.ValueKind == JsonValueKind.True;

    /// <summary>Run duration (days) from the GC's <c>ActivableParams.Schedule.Duration</c> (e.g. "5.00:00:00").
    /// 0 when absent/unparseable.</summary>
    private static double GcDurationDaysOf(JsonElement gc)
    {
        if (gc.TryGetProperty("ActivableParams", out var ap) && ap.ValueKind == JsonValueKind.Object
            && ap.TryGetProperty("Schedule", out var sc) && sc.ValueKind == JsonValueKind.Object
            && sc.TryGetProperty("Duration", out var du) && du.ValueKind == JsonValueKind.String)
        {
            var ts = EventService.ParseDuration(du.GetString());
            if (ts != null) return ts.Value.TotalDays;
        }
        return 0;
    }

    /// <summary>Mode B (§3.5): collects the ACTIVE dump's GC airings (start + duration + disabled + combined grid)
    /// per base name. Single snapshot, so no cross-dump CreatedAt reasoning — each GC's <c>Schedule.Start</c> is its
    /// current airing. Includes a GC if it's enabled OR already started (by <paramref name="now"/>); future-disabled
    /// (unconfirmed) GCs are skipped. DurationDays/Disabled come from <c>ActivableParams</c> (A3a). Base name via
    /// parent CBE (<see cref="GarageCleanupNameService"/>).</summary>
    public Dictionary<string, List<GcAiring>> CollectActiveAirings(JsonElement data, DateTime now)
    {
        var globalCbe = GarageCleanupNameService.BuildCbeMapFromData(data);
        var result = new Dictionary<string, List<GcAiring>>(StringComparer.Ordinal);
        if (!data.TryGetProperty("GarageCleanups", out var gcs) || gcs.ValueKind != JsonValueKind.Array) return result;

        foreach (var gc in gcs.EnumerateArray())
        {
            var baseName = GarageCleanupNameService.ResolveGcCanonicalName(gc, globalCbe);
            if (baseName == null) continue;
            var start = GcStartOf(gc);
            if (start == null) continue;
            bool enabled = GcEnabledOf(gc);
            if (!enabled && start.Value > now) continue;   // future-disabled / unconfirmed → skip
            var grid = CombineLevels(BuildGridForGc(gc), BuildRewardsForGc(gc));
            if (grid.Count == 0) continue;
            if (!result.TryGetValue(baseName, out var l)) result[baseName] = l = new();
            l.Add(new GcAiring(start.Value, GcDurationDaysOf(gc), !enabled, grid));
        }
        return result;
    }

    /// <summary>Mode B (§3.5/§2.9): ADD-ONLY merge of the active dump's airings into the existing wiki
    /// history. An active airing is identified by its (baseName, parent-run); if that parent-run is NOT
    /// already present for the base name it is added (all its rounds together); existing airings are NEVER
    /// removed or overwritten. This satisfies the integrity rules by construction — no deletion, no
    /// overwrite of older runs, no round loss when a dump only carries part of an old airing. (Retro
    /// rebalanc-update of a changed existing airing is a deliberate later refinement.)
    ///
    /// Orphan cleanup: after the add step, any merged airing whose start is not bracketed by ANY
    /// present parent run for that base name is dropped (its parent run was superseded). If the
    /// parent has zero runs in the events lua, all of the base's airings are dropped as pointless orphans.</summary>
    public Dictionary<string, List<GcAiring>> MergeAirings(
        Dictionary<string, List<GcAiring>> existing,
        Dictionary<string, List<GcAiring>> active,
        string liveEventsLua)
    {
        var parentRuns = ParseEventRuns(liveEventsLua);

        // Orphan cleanup on existing: drop any existing airing whose start is not strictly
        // bracketed by any present parent run before the add step. This is done first so that
        // a superseded airing (orphan) cannot block the matching active airing from being
        // added (the add step uses parent-run identity, and the nearest-fallback in
        // MatchParentRun would otherwise map the orphan to the new run, preventing the add).
        // No guard: a base with ZERO parent runs has no parent event at all, so its airings are
        // pointless orphans and are dropped (an empty runs list brackets nothing).
        var prunedExisting = new Dictionary<string, List<GcAiring>>(StringComparer.Ordinal);
        foreach (var (baseName, airings) in existing)
        {
            var parentName = GarageCleanupNameService.DeriveParent(baseName);
            var pr = parentRuns.TryGetValue(parentName, out var found)
                ? found : new List<(DateTime Start, double DurationDays)>();
            prunedExisting[baseName] = airings.Where(a => IsStrictlyBracketedByAnyRun(a.Start, pr)).ToList();
        }

        var merged = prunedExisting.ToDictionary(
            kv => kv.Key, kv => new List<GcAiring>(kv.Value), StringComparer.Ordinal);

        foreach (var (baseName, activeAirings) in active)
        {
            var parentName = GarageCleanupNameService.DeriveParent(baseName);
            parentRuns.TryGetValue(parentName, out var pr);
            DateTime PR(DateTime s) =>
                pr != null && GarageCleanupHistory.MatchParentRun(s, pr) is { } m ? m.Start : new DateTime(s.Year, 1, 1);

            if (!merged.TryGetValue(baseName, out var list)) merged[baseName] = list = new();
            var existingPRs = new HashSet<DateTime>(list.Select(a => PR(a.Start)));

            foreach (var prGroup in activeAirings.GroupBy(a => PR(a.Start)))
                if (existingPRs.Add(prGroup.Key))     // new parent-run → add all its rounds; existing untouched
                    list.AddRange(prGroup);
        }

        return merged;
    }

    /// <summary>Returns true if <paramref name="gcStart"/> falls within the strict window
    /// [r.Start − 1d, r.Start + r.DurationDays + 1d] of ANY run in <paramref name="runs"/>.
    /// This is the same per-run test used in <see cref="GarageCleanupHistory.MatchParentRun"/>
    /// but WITHOUT the nearest-run fallback — a vanished run must read as "no match".</summary>
    private static bool IsStrictlyBracketedByAnyRun(
        DateTime gcStart, IReadOnlyList<(DateTime Start, double DurationDays)> runs)
    {
        foreach (var r in runs)
            if (gcStart >= r.Start.AddDays(-1) && gcStart <= r.Start.AddDays(r.DurationDays + 1))
                return true;
        return false;
    }

    // ──────────────────────────────────────────────────────────── Lua-ish helpers

    /// <summary>Returns the content INSIDE the braces starting at openBraceIndex (excluding braces).</summary>
    private static string? ExtractBalanced(string s, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= s.Length || s[openBraceIndex] != '{') return null;
        int depth = 0; bool inStr = false; char strCh = '\0';
        for (int i = openBraceIndex; i < s.Length; i++)
        {
            char ch = s[i];
            if (inStr) { if (ch == strCh && s[i - 1] != '\\') inStr = false; continue; }
            if (ch == '"' || ch == '\'') { inStr = true; strCh = ch; }
            else if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) return s.Substring(openBraceIndex + 1, i - openBraceIndex - 1); }
        }
        return null;
    }

    /// <summary>Splits a table body into top-level "key = value" entries (value may be a {..} table).</summary>
    private static IEnumerable<(string Key, string Value)> EnumerateEntries(string body)
    {
        int i = 0, n = body.Length;
        while (i < n)
        {
            int eq = FindTopLevelEquals(body, i);
            if (eq < 0) yield break;
            var key = body.Substring(i, eq - i).Trim();
            int vstart = eq + 1;
            while (vstart < n && char.IsWhiteSpace(body[vstart])) vstart++;
            int vend;
            if (vstart < n && body[vstart] == '{')
            {
                var inner = ExtractBalanced(body, vstart);
                vend = inner == null ? n : vstart + 1 + inner.Length + 1;
            }
            else
            {
                vend = FindTopLevelComma(body, vstart);
                if (vend < 0) vend = n;
            }
            yield return (key, body.Substring(vstart, Math.Min(vend, n) - vstart).Trim());
            int next = FindTopLevelComma(body, Math.Min(vend, n));
            i = next < 0 ? n : next + 1;
        }
    }

    private static int FindTopLevelEquals(string s, int from)
    {
        int depth = 0; bool inStr = false; char sc = '\0';
        for (int i = from; i < s.Length; i++)
        {
            char ch = s[i];
            if (inStr) { if (ch == sc && s[i - 1] != '\\') inStr = false; continue; }
            if (ch == '"' || ch == '\'') { inStr = true; sc = ch; }
            else if (ch == '{' || ch == '(') depth++;
            else if (ch == '}' || ch == ')') depth--;
            else if (ch == '=' && depth == 0) return i;
        }
        return -1;
    }

    private static int FindTopLevelComma(string s, int from)
    {
        int depth = 0; bool inStr = false; char sc = '\0';
        for (int i = from; i < s.Length; i++)
        {
            char ch = s[i];
            if (inStr) { if (ch == sc && s[i - 1] != '\\') inStr = false; continue; }
            if (ch == '"' || ch == '\'') { inStr = true; sc = ch; }
            else if (ch == '{' || ch == '(') depth++;
            else if (ch == '}' || ch == ')') depth--;
            else if (ch == ',' && depth == 0) return i;
        }
        return -1;
    }

    private static string? ExtractNamedTable(string body, string field)
    {
        var m = Regex.Match(body, @"\b" + Regex.Escape(field) + @"\s*=\s*\{");
        if (!m.Success) return null;
        return ExtractBalanced(body, m.Index + m.Length - 1);
    }

    private static string? MatchQuotedKey(string key)
    {
        var m = Regex.Match(key, "\\[\\s*\"([^\"]*)\"\\s*\\]");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? MatchQuotedValue(string val)
    {
        var m = Regex.Match(val, "\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Generates the <c>== Garage Cleanup ==</c> section wikitext for a seasonal event page from the
    /// live <c>garageCleanupGrids</c> — one <c>=== year ===</c> sub-section per stored version (newest
    /// first), each with an Infobox, the per-level grid invokes, and the reward-table invoke. Level
    /// count per year is read from the live data. Returns "" if the event has no Garage Cleanup grids.
    /// </summary>
    /// <summary>One run/year of a Garage Cleanup, with the reward data needed to emit static reward tables.</summary>
    public sealed class GcYearData
    {
        public int Year { get; init; }
        public int LevelCount { get; init; }
        public List<LevelRewards> Rewards { get; init; } = new();   // index 0 = level 1
        public DateTime? Start { get; init; }
        public double DurationDays { get; init; }
        public bool ForcePlain { get; init; }                       // single unique run → plain name (no year)
    }

    private const int GcCellCount = 25;   // 5×5 board — total slots (slot-fill coin multiplier)

    /// <summary>
    /// Builds the full <c>== Garage Cleanup ==</c> section wikitext exactly in the wiki's layout:
    /// one Infobox, intro bullet + run-list, then per-year <c>=== year ===</c> sections (newest first;
    /// older years collapsed) — each an article-table with the per-level grid invokes, a collapsible
    /// Requirements row (invokes), STATIC per-level reward lists (computed from the dumped reward data),
    /// and a Total. Naming follows variant B: <c>(year)</c> for every run unless this is the event's
    /// single unique run (then plain).
    /// </summary>
    public static string GenerateSectionWikitext(List<GcYearData> years)
    {
        if (years == null || years.Count == 0) return "";
        bool plain = years.Count == 1 && years[0].ForcePlain;
        const string page = "{{PAGENAME}} Garage Cleanup";
        string NameFor(GcYearData y) => plain ? page : $"{page} ({y.Year})";
        int levelCount = years.Max(y => y.LevelCount);

        int newestYear = years.Max(y => y.Year);
        var sb = new StringBuilder();
        sb.AppendLine("== Garage Cleanup ==");
        // Multi-run → Infobox points at the newest run "(year)"; single unique run → plain.
        sb.AppendLine(InfoboxCall(plain ? null : $"{page} ({newestYear})"));
        sb.AppendLine($"* The '''Event''' is accompanied by {{{{Item/Group|{page}|link=Garage Cleanup}}}} Event with {levelCount} levels.");
        foreach (var y in years.OrderBy(y => y.Year))
            sb.AppendLine($"** {y.Year}: {RunListLine(y)}");
        sb.AppendLine();

        var desc = years.OrderByDescending(y => y.Year).ToList();
        for (int i = 0; i < desc.Count; i++)
        {
            bool collapsed = !plain && i > 0;        // newest expanded, older collapsed
            sb.Append(YearSection(desc[i], NameFor(desc[i]), collapsed, showHeader: !plain));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>One stored GC grid variant for the page section: its full Various key (= invoke name) +
    /// level count. Year / month / round are parsed from the key.</summary>
    public sealed class GcSectionVariant
    {
        public string Name { get; init; } = "";   // "X Garage Cleanup (2024) Round 1" / "X … (2025)" / plain "X …"
        public int LevelCount { get; init; }
    }

    private static readonly Regex RoundSuffixRe = new(@" Round (\d+)$", RegexOptions.Compiled);
    private static readonly Regex AiringSuffixRe = new(@" \(([^)]*)\)$", RegexOptions.Compiled);

    /// <summary>Generates the <c>== Garage Cleanup ==</c> section from the stored grid variants of one
    /// event — those with their OWN grid (identicalTo re-airs have no grid → covered by the
    /// <c>GenerateGarageCleanupRuns</c> run-list invoke, not a table). Variants group into airings
    /// (name minus <c> Round N</c>); a multi-round airing shows ONE Infobox + an <c>==== Round N ====</c>
    /// table per round. Newest airing expanded, older collapsed. Naming/headers per §3.2.</summary>
    public static string GenerateSectionWikitext(string baseName, List<GcSectionVariant> variants)
    {
        if (variants == null || variants.Count == 0) return "";

        var airings = variants
            .GroupBy(v => RoundSuffixRe.Replace(v.Name, ""))            // airing = grid key minus round
            .Select(g =>
            {
                var lm = AiringSuffixRe.Match(g.Key);
                var label = lm.Success ? lm.Groups[1].Value : "";       // "2024" / "March 2025" / ""
                var ym = Regex.Match(label, @"\d{4}");
                // "(Month YYYY)" labels sort by the actual month — ordinal name order would put
                // "February" above "December" within one year (F > D) despite being older.
                int sortMonth = 0;
                var mm = Regex.Match(label, @"^([A-Za-z]+) \d{4}$");
                if (mm.Success && DateTime.TryParseExact(mm.Groups[1].Value, "MMMM",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var md))
                    sortMonth = md.Month;
                return new
                {
                    AiringName = g.Key,
                    Label = label,
                    SortYear = ym.Success ? int.Parse(ym.Value, CultureInfo.InvariantCulture) : 0,
                    SortMonth = sortMonth,
                    Rounds = g.OrderBy(v =>
                    {
                        var rm = RoundSuffixRe.Match(v.Name);
                        return rm.Success ? int.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    }).ToList(),
                    LevelCount = g.Max(v => v.LevelCount),
                };
            })
            .OrderByDescending(a => a.SortYear).ThenByDescending(a => a.SortMonth)
            .ThenByDescending(a => a.AiringName, StringComparer.Ordinal)
            .ToList();

        bool plain = airings.Count == 1 && airings[0].Label == "";
        int levelCount = airings.Max(a => a.LevelCount);

        var sb = new StringBuilder();
        sb.AppendLine("== Garage Cleanup ==");
        sb.AppendLine(InfoboxCall(plain ? null : airings[0].AiringName));    // newest airing (or plain)
        sb.AppendLine($"* The '''Event''' is accompanied by {{{{Item/Group|{baseName}|link=Garage Cleanup}}}} Event with {levelCount} levels.");
        sb.AppendLine("{{#Invoke:Various|GenerateGarageCleanupRuns|name=" + baseName + "}}");   // run-list incl. identicalTo
        sb.AppendLine();

        for (int i = 0; i < airings.Count; i++)
        {
            var a = airings[i];
            bool collapsed = !plain && i > 0;          // newest expanded, older collapsed
            if (!plain) sb.AppendLine($"=== {a.Label} ===");
            if (collapsed) sb.AppendLine(InfoboxCall(a.AiringName));   // older airings carry their own infobox
            bool multiRound = a.Rounds.Count > 1;
            for (int r = 0; r < a.Rounds.Count; r++)
            {
                if (multiRound) sb.AppendLine($"==== Round {r + 1} ====");
                sb.Append(VariantTable(a.Rounds[r].Name, Math.Max(1, a.Rounds[r].LevelCount), collapsed, a.Label));
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>The article-table for one grid variant: per-level Grid invokes, a collapsible Requirements
    /// row, Rewards, and a Total — all Lua-rendered by <paramref name="name"/>. No <c>===</c> header (the
    /// caller emits the airing/round heading). Mirrors the single-run table of <see cref="YearSection"/>.</summary>
    private static string VariantTable(string name, int lc, bool collapsed, string rewardsLabel)
    {
        string Grid(int lvl) => "{{#Invoke:Various|GenerateGarageCleanupGrid|name=" + name + "|level=" + lvl + "}}";
        string Reqs(int lvl) => "{{#Invoke:Various|GenerateGarageCleanupRequirements|name=" + name + "|level=" + lvl + "}}";
        string Rew(string lvl) => "{{#Invoke:Various|GenerateGarageCleanupRewards|name=" + name + "|level=" + lvl + "}}";

        var sb = new StringBuilder();
        sb.AppendLine(collapsed
            ? "{| class=\"mw-collapsible mw-collapsed article-table\""
            : "{| class = \"article-table\"");
        if (collapsed && rewardsLabel != "") { sb.AppendLine($"! colspan = {lc} | {rewardsLabel} Rewards"); sb.AppendLine("|-"); }

        for (int lvl = 1; lvl <= lc; lvl++) sb.AppendLine($"! style = \"text-align: center;\" | Level {lvl}");
        sb.AppendLine("|-");
        for (int lvl = 1; lvl <= lc; lvl++) sb.AppendLine("| " + Grid(lvl));

        sb.AppendLine("|-");
        sb.AppendLine($"| colspan = {lc} class = \"padding0\" |");
        sb.AppendLine("<div class=\"fadePreview\">");
        sb.AppendLine("{| class = \"mw-collapsible mw-collapsed article-table\" style = \"width:100%; margin:0;\"");
        sb.AppendLine($"! colspan = {lc} class = \"center\" | Requirements");
        sb.AppendLine("|- class = \"valignTop\"");
        for (int lvl = 1; lvl <= lc; lvl++) sb.AppendLine("| " + Reqs(lvl));
        sb.AppendLine("|}");
        sb.AppendLine("</div>");

        sb.AppendLine("|-");
        sb.AppendLine($"! colspan = {lc} class = \"center\" | Rewards");
        sb.AppendLine("|- class = \"valignTop\"");
        for (int lvl = 1; lvl <= lc; lvl++) sb.AppendLine("| " + Rew(lvl.ToString()));

        sb.AppendLine("|-");
        sb.AppendLine($"! colspan = {lc} class = \"center\" | Total");
        sb.AppendLine("|-");
        sb.AppendLine($"| colspan = {lc} | " + Rew("total"));
        sb.AppendLine("|}");
        sb.AppendLine("{{-}}");
        return sb.ToString();
    }

    /// <summary>Builds an Infobox call. <paramref name="nameParam"/> is the positional name
    /// ("{{PAGENAME}} Garage Cleanup (year)") or null for a plain single-run infobox. Dates are NOT
    /// emitted here at all — the Infobox Garage Cleanup template looks up start/end itself from
    /// Module:Datatable/Events using this name (or {{PAGENAME}} when omitted), so the automation never
    /// passes startingDate/endingDate. Manual startingDate=/endingDate= remain a template-side fallback.</summary>
    private static string InfoboxCall(string? nameParam)
    {
        var sb = new StringBuilder("{{Infobox Garage Cleanup");
        if (nameParam != null) sb.Append('|').Append(nameParam);
        sb.Append("}}");
        return sb.ToString();
    }

    private static string RunListLine(GcYearData y)
    {
        // English month names always (wiki text must not pick up the OS/UI culture, e.g. "červen").
        static string Mon(DateTime d) => d.ToString("MMMM", CultureInfo.InvariantCulture);
        int days = Math.Max(1, (int)Math.Round(y.DurationDays));
        string dayWord = days == 1 ? "day" : "days";
        if (y.Start is not { } st) return $"Available for {days} {dayWord}.";
        var end = st.AddDays(days - 1);
        string range = st.Month == end.Month
            ? $"{Mon(st)} {st.Day}-{end.Day}, {st.Year}"
            : $"{Mon(st)} {st.Day}-{Mon(end)} {end.Day}, {st.Year}";
        return $"Available for {days} {dayWord} from {range}.";
    }

    private static string YearSection(GcYearData y, string name, bool collapsed, bool showHeader)
    {
        string Grid(int lvl) => "{{#Invoke:Various|GenerateGarageCleanupGrid|name=" + name + "|level=" + lvl + "}}";
        string Reqs(int lvl) => "{{#Invoke:Various|GenerateGarageCleanupRequirements|name=" + name + "|level=" + lvl + "}}";
        string Rew(string lvl) => "{{#Invoke:Various|GenerateGarageCleanupRewards|name=" + name + "|level=" + lvl + "}}";
        int lc = Math.Max(1, y.LevelCount);

        var sb = new StringBuilder();
        if (showHeader) sb.AppendLine($"=== {y.Year} ===");
        // Older (collapsed) runs get their own Infobox; the newest run uses the top-of-section one.
        if (collapsed) sb.AppendLine(InfoboxCall(name));
        sb.AppendLine(collapsed
            ? "{| class=\"mw-collapsible mw-collapsed article-table\""
            : "{| class = \"article-table\"");
        if (collapsed) { sb.AppendLine($"! colspan = {lc} | {y.Year} Rewards"); sb.AppendLine("|-"); }

        for (int lvl = 1; lvl <= lc; lvl++) sb.AppendLine($"! style = \"text-align: center;\" | Level {lvl}");
        sb.AppendLine("|-");
        for (int lvl = 1; lvl <= lc; lvl++) sb.AppendLine("| " + Grid(lvl));

        // Requirements (collapsible inner table)
        sb.AppendLine("|-");
        sb.AppendLine($"| colspan = {lc} class = \"padding0\" |");
        sb.AppendLine("<div class=\"fadePreview\">");
        sb.AppendLine("{| class = \"mw-collapsible mw-collapsed article-table\" style = \"width:100%; margin:0;\"");
        sb.AppendLine($"! colspan = {lc} class = \"center\" | Requirements");
        sb.AppendLine("|- class = \"valignTop\"");
        for (int lvl = 1; lvl <= lc; lvl++) sb.AppendLine("| " + Reqs(lvl));
        sb.AppendLine("|}");
        sb.AppendLine("</div>");

        // Rewards (Lua-rendered from the module's garageCleanupRewards data, per level).
        sb.AppendLine("|-");
        sb.AppendLine($"! colspan = {lc} class = \"center\" | Rewards");
        sb.AppendLine("|- class = \"valignTop\"");
        for (int lvl = 1; lvl <= lc; lvl++)
            sb.AppendLine("| " + Rew(lvl.ToString()));

        // Total (Lua-rendered, aggregated across levels).
        sb.AppendLine("|-");
        sb.AppendLine($"! colspan = {lc} class = \"center\" | Total");
        sb.AppendLine("|-");
        sb.AppendLine($"| colspan = {lc} | " + Rew("total"));
        sb.AppendLine("|}");
        sb.AppendLine("{{-}}");   // clear the right-floated infobox at the end of the subsection
        return sb.ToString();
    }

    /// <summary>"{{Item/Group|Ingredients|2|min=2}}" — boosters drop the min= range.</summary>
    private static string ItemGroupRef(RewardEntry r)
    {
        var chain = r.Chain ?? "";
        bool booster = chain.Contains("Booster", StringComparison.OrdinalIgnoreCase);
        return booster
            ? $"{{{{Item/Group|{chain}|{r.Level}}}}}"
            : $"{{{{Item/Group|{chain}|{r.Level}|min={r.Level}}}}}";
    }

    private static int CoinsOf(LevelRewards lr) =>
        lr.SlotFill.Where(r => r.Currency == "Coins").Sum(r => r.Amount) * GcCellCount;

    /// <summary>Static per-level reward list: "{count}x {{Item/Group|…}}<br>" lines + the coins line.</summary>
    private static string RewardCell(LevelRewards lr)
    {
        var lines = new List<string>();
        foreach (var g in lr.Groups)
        {
            var item = g.Rewards.FirstOrDefault(r => r.Chain != null);
            if (item == null) continue;
            lines.Add($"{g.Count * Math.Max(1, item.Amount)}x {ItemGroupRef(item)}<br>");
        }
        int coins = CoinsOf(lr);
        if (coins > 0) lines.Add($"{coins}x {{{{Coins}}}} [[Coins]]");
        return string.Join("\n", lines);
    }

    /// <summary>Aggregated total across all levels: per (chain, level) summed counts + total coins.</summary>
    private static string TotalCell(List<LevelRewards> levels)
    {
        var order = new List<(string Chain, int Level)>();
        var sums = new Dictionary<(string, int), int>();
        int coins = 0;
        foreach (var lr in levels)
        {
            foreach (var g in lr.Groups)
            {
                var item = g.Rewards.FirstOrDefault(r => r.Chain != null);
                if (item == null) continue;
                var key = (item.Chain!, item.Level);
                if (!sums.ContainsKey(key)) { sums[key] = 0; order.Add(key); }
                sums[key] += g.Count * Math.Max(1, item.Amount);
            }
            coins += CoinsOf(lr);
        }
        // Group by chain (keep first-seen chain order), levels ascending within a chain.
        var chainOrder = new List<string>();
        foreach (var (c, _) in order) if (!chainOrder.Contains(c)) chainOrder.Add(c);

        var lines = new List<string>();
        foreach (var c in chainOrder)
            foreach (var (chain, level) in order.Where(k => k.Chain == c).OrderBy(k => k.Level).Distinct())
                lines.Add($"{sums[(chain, level)]}x {ItemGroupRef(new RewardEntry { Chain = chain, Level = level, Amount = 1 })}<br>");
        if (coins > 0) lines.Add($"{coins}x {{{{Coins}}}} [[Coins]]");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Splices the generated <c>== Garage Cleanup ==</c> section into an event page: replaces an
    /// existing GC section if present, else inserts it after the <c>== Rewards ==</c> section (or
    /// <c>== Progress Bar ==</c>), else appends at the end. Returns the new page content + a short
    /// description of what was done. Operates on level-2 headings only.
    /// </summary>
    // NOTE: trailing \r? is required — the generated section uses StringBuilder.AppendLine (CRLF on
    // Windows), so a bare [ \t]*$ would never match "=== 2026 ===\r\n" and l3.Count would read 0.
    private static readonly Regex Level3HeadingRe = new(@"(?m)^===(?!=)[ \t]*(.+?)[ \t]*===(?!=)[ \t]*\r?$");

    /// <summary>
    /// Preserves old hand-written GC content when replacing the section:
    /// <list type="bullet">
    /// <item>The descriptive <b>prose</b> (paragraph before the first table) moves under the previous
    /// run's <c>=== year ===</c> heading (after its Infobox, before its table).</item>
    /// <item>If the previous run has <b>no generated section</b> (only the current run was generated —
    /// i.e. the module has no data for that older run), the original screenshot <b>table is kept too</b>
    /// (appended, unchanged) instead of being dropped — there's nothing to regenerate it from.</item>
    /// <item>Otherwise the old table is dropped (the generated invoke grids supersede it).</item>
    /// </list>
    /// </summary>
    private static string InjectPreservedContent(string gcSection, string oldBody)
    {
        int t = oldBody.IndexOf("{|", StringComparison.Ordinal);     // start of the old table, if any
        var prose = (t >= 0 ? oldBody[..t] : oldBody).Trim();
        if (prose.StartsWith("* ", StringComparison.Ordinal)) prose = prose[2..].TrimStart();
        var table = t >= 0 ? oldBody[t..].Trim() : "";

        var l3 = Level3HeadingRe.Matches(gcSection);
        AppLogger.Debug($"[GC.Inject] l3.Count={l3.Count} prose.Len={prose.Length} table.Len={table.Length}");

        // Only the current run was generated (no separate previous-run section) → the older run has no
        // module data: keep the original screenshot table (and prose) so it isn't lost.
        if (l3.Count < 2)
        {
            var keep = prose;
            if (l3.Count == 1 && table.Length > 0)
                keep = (prose.Length > 0 ? prose + "\n\n" : "") + table;
            AppLogger.Debug("[GC.Inject] BRANCH <2 → prose appended at END of section");
            return keep.Length == 0 ? gcSection : gcSection.TrimEnd() + "\n\n" + keep + "\n";
        }

        // Previous run has a generated section → move just the prose under it (drop the old table).
        if (prose.Length == 0) return gcSection;
        int tableAt = gcSection.IndexOf("{|", l3[1].Index, StringComparison.Ordinal);   // previous run = 2nd-newest
        if (tableAt < 0) tableAt = gcSection.Length;
        AppLogger.Debug($"[GC.Inject] BRANCH >=2 → prose inserted ABOVE prev-run table (l3[1].Index={l3[1].Index}, tableAt={tableAt})");
        return gcSection[..tableAt].TrimEnd() + "\n" + prose + "\n\n" + gcSection[tableAt..];
    }

    private static readonly Regex Level2HeadingRe = new(@"(?m)^==(?!=)[ \t]*(.+?)[ \t]*==(?!=)[ \t]*\r?$");

    /// <summary>Level-2 section headings (== Title ==) of a page, in order.</summary>
    public static List<string> GetLevel2Sections(string page) =>
        Level2HeadingRe.Matches(page).Select(m => m.Groups[1].Value.Trim()).ToList();

    /// <summary>Extracts the <c>== Garage Cleanup ==</c> level-2 section from a page (heading through the
    /// next level-2 heading or EOF), trimmed. Returns null if the page has no such section. Used to build a
    /// WYSIWYG preview that shows exactly what the upload will produce (incl. preserved prose).</summary>
    public static string? ExtractGcSection(string page)
    {
        Match? gc = null;
        foreach (Match m in Level2HeadingRe.Matches(page))
            if (m.Groups[1].Value.Trim().Equals("Garage Cleanup", StringComparison.OrdinalIgnoreCase)) { gc = m; break; }
        if (gc == null) return null;
        int end = page.Length;
        foreach (Match h in Level2HeadingRe.Matches(page)) if (h.Index > gc.Index) { end = h.Index; break; }
        return page[gc.Index..end].TrimEnd();
    }

    /// <summary>
    /// Splices the generated <c>== Garage Cleanup ==</c> section into an event page: replaces an existing
    /// GC section if present; else inserts after <paramref name="insertAfter"/> if given; else after
    /// <c>== Rewards ==</c> or <c>== Progress Bar ==</c>. If none of those apply, returns
    /// <c>(null, ...)</c> to signal the caller must ask the user which section to insert after.
    /// </summary>
    public static (string? Content, string Action) SpliceEventPageSection(string page, string gcSection, string? insertAfter = null)
    {
        gcSection = gcSection.TrimEnd();
        var matches = Level2HeadingRe.Matches(page);

        int NextHeading(int afterIndex)
        {
            foreach (Match m in matches) if (m.Index > afterIndex) return m.Index;
            return page.Length;
        }
        bool IsHeading(Match m, string name) =>
            m.Groups[1].Value.Trim().Equals(name, StringComparison.OrdinalIgnoreCase);

        // 1) Replace an existing Garage Cleanup section — but preserve any hand-written old content
        //    (prose, screenshots) by moving it under the previous run's subsection so nothing is lost.
        foreach (Match m in matches)
            if (IsHeading(m, "Garage Cleanup"))
            {
                int end = NextHeading(m.Index);
                var oldBody = page[(m.Index + m.Length)..end].Trim();
                var newSection = gcSection;
                var act = "replaced existing Garage Cleanup section";
                bool oldHasInvoke = oldBody.Contains("GenerateGarageCleanupGrid", StringComparison.Ordinal);
                AppLogger.Debug($"[GC.Splice] found existing GC section; oldBody.Len={oldBody.Length} oldHasInvoke={oldHasInvoke} gcSection '=== ' headings={Regex.Matches(gcSection, "(?m)^=== ").Count}");
                // Only preserve genuinely manual content (skip if the old section was itself generated).
                if (oldBody.Length > 0 && !oldHasInvoke)
                {
                    newSection = InjectPreservedContent(gcSection, oldBody);
                    act = "replaced GC section (old content moved under the previous run)";
                }
                else AppLogger.Debug("[GC.Splice] preservation SKIPPED (oldBody empty or already generated)");
                var tail = end < page.Length ? page[end..] : "";
                return ((page[..m.Index].TrimEnd() + "\n\n" + newSection + "\n\n" + tail.TrimStart('\n')).TrimEnd() + "\n", act);
            }

        // 2) Insert after an explicit choice, else Rewards, else Progress Bar.
        var targets = insertAfter != null ? new[] { insertAfter } : new[] { "Rewards", "Progress Bar" };
        foreach (var target in targets)
            foreach (Match m in matches)
                if (IsHeading(m, target))
                {
                    int end = NextHeading(m.Index);
                    var before = page[..end].TrimEnd();
                    var after = end < page.Length ? page[end..].TrimStart('\n') : "";
                    var content = (before + "\n\n" + gcSection + (after.Length > 0 ? "\n\n" + after : "")).TrimEnd() + "\n";
                    return (content, $"inserted after the {target} section");
                }

        // 3) No suitable anchor — caller must pick a section.
        return (null, "no Rewards / Progress Bar section");
    }

    private static readonly Regex YearKeyRe = new(@"^(.*) \((\d{4})\)$", RegexOptions.Compiled);

    /// <summary>Strips variant suffixes from a stored grid key to get the bare base event name, so a new
    /// airing can be matched against existing variants by base name (keys are full names — §2.7). Removes a
    /// trailing " Round N" then a trailing " (…)" (year or "Month year"). E.g.
    /// "Legacy Lane Garage Cleanup (2024) Round 1" → "Legacy Lane Garage Cleanup".</summary>
    public static string StripVariantSuffix(string key)
    {
        var s = Regex.Replace(key, @" Round \d+$", "");
        s = Regex.Replace(s, @" \([^)]*\)$", "");
        return s;
    }

    /// <summary>Parses each event's runs (start date + duration) from the Events module Lua (Datatable/Events),
    /// for matching a GC airing to its parent event's run window (§2.7). Reuses <see cref="LuaTableReader"/>.
    /// Returns event name → list of (Start, DurationDays).</summary>
    public static Dictionary<string, List<(DateTime Start, double DurationDays)>> ParseEventRuns(string eventsLua)
    {
        var result = new Dictionary<string, List<(DateTime, double)>>(StringComparer.Ordinal);
        var root = LuaTableReader.Parse(eventsLua);
        var events = root?.Tbl("events");
        if (events == null) return result;

        foreach (var obj in events.Array)
        {
            if (obj is not LuaTable e) continue;
            var name = e.Str("name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var runs = e.Tbl("runs");
            if (runs == null) continue;

            var list = new List<(DateTime, double)>();
            foreach (var ro in runs.Array)
            {
                if (ro is not LuaTable run) continue;
                var st = run.Tbl("start");
                if (st == null) continue;
                int y = (int)(st.Num("year") ?? 0);
                if (y == 0) continue;
                int mo = (int)(st.Num("month") ?? 1);
                int d = (int)(st.Num("day") ?? 1);
                int h = (int)(st.Num("hour") ?? 0);
                int mi = (int)(st.Num("min") ?? 0);
                double dur = run.Num("durationDays") ?? 0;
                try { list.Add((new DateTime(y, mo, d, h, mi, 0), dur)); } catch { /* invalid date → skip */ }
            }
            if (list.Count > 0) result[name] = list;
        }
        return result;
    }

    /// <summary>"Sweet Mess Express Garage Cleanup (2025)" → ("Sweet Mess Express Garage Cleanup", 2025).</summary>
    private static bool TryParseYearKey(string key, out string baseName, out int year)
    {
        var m = YearKeyRe.Match(key);
        if (m.Success) { baseName = m.Groups[1].Value; year = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); return true; }
        baseName = key; year = 0; return false;
    }

    /// <summary>
    /// Reads the run years per event from the live <c>Module:Datatable/Events</c> (the reliable
    /// archive of start dates — events.json churns as devs delete old events). Reuses
    /// <see cref="LuaTableReader"/>. Returns event name → ascending distinct years.
    /// </summary>
    public static Dictionary<string, List<int>> ParseEventYears(string datatableEventsContent)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var root = LuaTableReader.Parse(datatableEventsContent);
        var events = root?.Tbl("events");
        if (events == null) return result;

        foreach (var obj in events.Array)
        {
            if (obj is not LuaTable e) continue;
            var name = e.Str("name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var runs = e.Tbl("runs");
            if (runs == null) continue;

            var ys = new SortedSet<int>();
            foreach (var ro in runs.Array)
            {
                if (ro is not LuaTable run) continue;
                var y = run.Tbl("start")?.Num("year");
                if (y.HasValue) ys.Add((int)y.Value);
            }
            if (ys.Count > 0) result[name] = ys.ToList();
        }
        return result;
    }
}
