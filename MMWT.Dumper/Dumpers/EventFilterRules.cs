namespace MergeMansionWikiTools.Dumper.Dumpers;

/// <summary>
/// Which <see cref="EventFilters"/> flag an individual event belongs to. Three of the 36 categories
/// in <c>events.json</c> are filtered PER EVENT rather than per library — collectible boards,
/// core-support events and leaderboards all mix several event families in one config library — and
/// the classification is a pure function of the event's id (plus, for leaderboards, its raw config
/// <c>DisplayName</c>), which is why it lives here rather than inside the dumper.
/// <para>
/// Derived from the legacy <c>--filters</c> corpus of the newest archive: each single-flag dump
/// contains exactly the events this classifier assigns to that flag, and the three partitions add
/// up to the unfiltered totals (46 boards = 6 + 8 + 32 + 0, 94 core-support = 60 + 8 + 1 + 8 + 17,
/// 5 leaderboards = 1 + 1 + 3).
/// </para>
/// </summary>
public static class EventFilterRules
{
    /// <summary>
    /// Collectible board events: <c>LC_*</c> and <c>CBE_LuckyCatch</c> are Lucky Catch, <c>LS_*</c>
    /// is Lucky Snap, the gem-mine / Great Escape / Jailbreak family is Legacy, everything else
    /// (<c>CBE_*</c>, <c>LDE_*</c>, <c>SE_*</c>) is Seasonal.
    /// </summary>
    public static EventFilters ForCollectibleBoard(string? eventId)
    {
        var id = eventId ?? "";
        if (id.StartsWith("LC_", StringComparison.Ordinal) || id == "CBE_LuckyCatch") return EventFilters.LuckyCatch;
        if (id.StartsWith("LS_", StringComparison.Ordinal)) return EventFilters.LuckySnap;
        if (id.StartsWith("GM_", StringComparison.Ordinal) || id == "CBE_GemMine"
            || id.Contains("GreatEscape", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Jailbreak", StringComparison.OrdinalIgnoreCase)) return EventFilters.Legacy;
        return EventFilters.Seasonal;
    }

    /// <summary>
    /// Core-support events: <c>DE_*</c> is the Re-Archaeology dig event, <c>CR_*</c> the Horizons
    /// Cup classic races, <c>CSE_Dinner*</c> / anything mentioning RollTheDice is Roll The Dice,
    /// <c>AutoMerge*</c> is Auto Merge, and the rest (builder events, the Daily Challenges event
    /// shells) is Uncategorised.
    /// </summary>
    public static EventFilters ForCoreSupportEvent(string? configKey)
    {
        var id = configKey ?? "";
        if (id.StartsWith("DE_", StringComparison.Ordinal)) return EventFilters.ReArchaeology;
        if (id.StartsWith("CR_", StringComparison.Ordinal)) return EventFilters.HorizonsCup;
        if (id.StartsWith("CSE_Dinner", StringComparison.Ordinal)
            || id.Contains("RollTheDice", StringComparison.OrdinalIgnoreCase)) return EventFilters.RollTheDice;
        if (id.StartsWith("AutoMerge", StringComparison.Ordinal)) return EventFilters.AutoMerge;
        return EventFilters.Uncategorised;
    }

    /// <summary>
    /// Leaderboard events: the id alone does not separate them (<c>LBE_May2023</c> is the Bake-off),
    /// so the raw config <c>DisplayName</c> is matched too — "BakeOff Leaderboard Event" and
    /// "Bush Bonanza Leaderboard Event". Everything else is Legacy.
    /// </summary>
    public static EventFilters ForLeaderboard(string? eventId, string? displayName)
    {
        // The two halves are joined by a NUL separator so a match can never straddle them.
        // It is written as the C# ESCAPE SEQUENCE: a raw NUL byte in the source file makes git
        // classify the whole file as binary and refuse to diff it.
        var haystack = (eventId ?? "") + "\0" + (displayName ?? "");
        if (haystack.Contains("Bonanza", StringComparison.OrdinalIgnoreCase)) return EventFilters.Bonanza;
        if (haystack.Contains("BakeOff", StringComparison.OrdinalIgnoreCase)) return EventFilters.BakeOff;
        return EventFilters.Legacy;
    }
}
