namespace MergeMansionWikiTools.Models;

/// <summary>
/// Contents of <c>daily_scoop.json</c> — the Daily Scoop (DailyChallenges V2) task ladder of the
/// week set the game currently serves, as written by <c>DailyScoopExtractor</c> and rendered into
/// <c>Module:Datatable/DailyScoop</c> by <c>DailyScoopDatatableService</c>.
/// <para>
/// Ids are kept in their full config form (<c>LT500_HardWeek_Day3_Task1_v2</c>) because the
/// renderer walks the five player segments by swapping the id's segment prefix, and the fallback
/// lists reference tasks across weeks by id.
/// </para>
/// </summary>
public sealed class DailyScoopDump
{
    /// <summary>
    /// Revision suffix of the extracted week set (<c>"_v2"</c>, or <c>""</c> for the original set).
    /// The game re-cuts the whole set occasionally, keeping the difficulties and rewards but
    /// replacing the daily tasks — see <c>_CONTEXT/Game/DailyScoop.md</c> §2b.
    /// </summary>
    public string Revision { get; set; } = "";

    /// <summary>Weekly event the revision was read from, e.g. <c>"DailyChallenges_21 (HardWeek_v2)"</c>.</summary>
    public string SelectedFrom { get; set; } = "";

    /// <summary>Player segments, in the positional order every goals/points array uses.</summary>
    public List<SegmentEntry> Segments { get; set; } = new();

    /// <summary>The four week types of the extracted set.</summary>
    public List<WeekEntry> Weeks { get; set; } = new();

    /// <summary>Standard objectives by config id — primary tasks AND <c>Fallback_*</c> definitions.</summary>
    public Dictionary<string, TaskEntry> Tasks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Special objectives (the repeatable per-day bonus ladder) by config id.</summary>
    public Dictionary<string, SpecialEntry> Specials { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Days by config id.</summary>
    public Dictionary<string, DayEntry> Days { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Anything the extractor could not resolve; surfaced in the Update dialog's notes.</summary>
    public List<string> Warnings { get; set; } = new();

    public sealed class SegmentEntry
    {
        /// <summary>Wiki-side short key (<c>l51</c>, <c>lt5</c>, …).</summary>
        public string Key { get; set; } = "";

        /// <summary>Config id prefix (<c>PlayerLevels51above</c>, <c>LT500</c>, …).</summary>
        public string Id { get; set; } = "";
    }

    public sealed class WeekEntry
    {
        /// <summary>Config week type without the revision suffix (<c>HardWeek</c>).</summary>
        public string Type { get; set; } = "";

        /// <summary>Wiki tab key (<c>Hard</c>).</summary>
        public string Key { get; set; } = "";

        /// <summary>Wiki tab label (<c>Hard Week</c>).</summary>
        public string Name { get; set; } = "";

        /// <summary>Segment key -&gt; the week's seven day ids, in day order.</summary>
        public Dictionary<string, List<string>> Days { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class TaskEntry
    {
        public string Type { get; set; } = "";

        /// <summary>Target amount ("Merge <b>150</b> times").</summary>
        public int Req { get; set; }

        public int Prio { get; set; }

        /// <summary>Objective parameter(s), comma-joined (<c>TertiaryEnergy</c>, <c>CBE_</c>, …).</summary>
        public string Params { get; set; } = "";

        /// <summary>Localisation key; its <c>Minutes</c> flavour is what makes a goal a duration.</summary>
        public string Loc { get; set; } = "";

        /// <summary>Daily Scoop points awarded (reward slot 0).</summary>
        public int Points { get; set; }

        /// <summary>The item/currency reward shown next to the points (reward slot 1).</summary>
        public RewardEntry? Reward { get; set; }

        /// <summary>Ordered fallback objective ids, used when this task's own gate is not met.</summary>
        public List<string> Fallbacks { get; set; } = new();

        /// <summary>
        /// Event gate: the id-family prefix (<c>LBE_</c>, <c>LC_</c>, <c>DE_</c>, …) of the event
        /// that must be running for this task to be offered. Null when the task is unconditional.
        /// </summary>
        public string? Event { get; set; }
    }

    public sealed class SpecialEntry
    {
        public string Type { get; set; } = "";
        public string Params { get; set; } = "";
        public string Loc { get; set; } = "";

        /// <summary>The ten escalating targets of the repeatable bonus objective.</summary>
        public List<int> Reqs { get; set; } = new();

        /// <summary>Energy granted per step (reward slot 0).</summary>
        public List<int> Energy { get; set; } = new();

        /// <summary>Daily Scoop points granted per step when catching up.</summary>
        public List<int> CatchUp { get; set; } = new();

        /// <summary>The box granted per step (reward slot 1).</summary>
        public RewardEntry? Reward { get; set; }
    }

    public sealed class DayEntry
    {
        /// <summary>Tasks that must be completed for the day box.</summary>
        public int ReqCompleted { get; set; }

        /// <summary><c>"daily"</c> or <c>"weekly"</c> — which box the day awards.</summary>
        public string Reward { get; set; } = "daily";

        /// <summary>The day's standard objective ids, in display order.</summary>
        public List<string> Std { get; set; } = new();
    }

    public sealed class RewardEntry
    {
        /// <summary>Reward id (<c>Coins</c>, <c>Energy</c>, <c>TCE_CardPackBasic_1Stars_01</c>, …).</summary>
        public string Id { get; set; } = "";

        /// <summary>Reward type (<c>Currency</c>, <c>Item</c>, <c>CardCollectionPack</c>, <c>OnFire</c>, …).</summary>
        public string Type { get; set; } = "";

        /// <summary>Auxiliary qualifier — the merge board for currencies, the duration for timed buffs.</summary>
        public string Aux { get; set; } = "";

        public int Amount { get; set; } = 1;
    }
}
