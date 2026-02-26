namespace MergeMansionWikiTools.Models;

// ── Reward type enum ─────────────────────────────────────────────

public enum MysteryRewardType
{
    Energy,
    Diamonds,
    Coins,
    Item,
    Decoration,
    Experience,
    CardPack,
    Perk,
    Pet,
    InformantTip
}

// ── Mystery type ─────────────────────────────────────────────────

public enum MysteryType
{
    Standard,
    Pet
}

// ── Single reward entry ──────────────────────────────────────────

public class MysteryReward
{
    public MysteryRewardType Type { get; set; }
    public int Amount { get; set; }

    /// <summary>Raw item key from JSON (e.g., "Lamp_09")</summary>
    public string? ItemKey { get; set; }

    /// <summary>Resolved display name (from wiki mapping / DataService / manual override)</summary>
    public string? ItemDisplayName { get; set; }

    /// <summary>Item level (for {{Item/Group|Name|Level}} templates)</summary>
    public int? ItemLevel { get; set; }

    /// <summary>Decoration ID for Pet mystery rewards</summary>
    public string? DecorationId { get; set; }
    public string? DecorationName { get; set; }

    /// <summary>Energy subtype (e.g., "AreaEnergy", "EventEnergy")</summary>
    public string? EnergyType { get; set; }

    /// <summary>Perk ID (skipped in wiki output)</summary>
    public string? PerkId { get; set; }

    /// <summary>Card pack ID</summary>
    public string? CardPackId { get; set; }

    /// <summary>Pet ConfigKey (e.g., "McLeod")</summary>
    public string? PetName { get; set; }

    /// <summary>Informant tip card ID</summary>
    public string? InformantTipCardId { get; set; }

    /// <summary>Duration in ms for hourglass rewards (from OverrideItemFeatures)</summary>
    public long? DurationMs { get; set; }
}

// ── One level in a tier ──────────────────────────────────────────

public class MysteryRewardLevel
{
    public int Level { get; set; }
    public int XpRequired { get; set; }
    public List<MysteryReward> Rewards { get; set; } = new();
}

// ── Wiki check state ─────────────────────────────────────────────

/// <summary>Three-state check result for wiki indicators.</summary>
public enum WikiCheckState
{
    /// <summary>Not checked yet.</summary>
    Unknown,
    /// <summary>Page/template does not exist (red ✗).</summary>
    Missing,
    /// <summary>Page exists but content doesn't match expected format (yellow ⚠).</summary>
    Mismatch,
    /// <summary>Page exists and content matches (green ✓).</summary>
    Match
}

// ── Wiki page status ─────────────────────────────────────────────

public class WikiPageStatus
{
    /// <summary>Does the event page exist on the wiki?</summary>
    public bool? EventPageExists { get; set; }

    /// <summary>Does the event page content match (excluding Dialogue)?</summary>
    public bool? EventPageContentMatches { get; set; }

    /// <summary>Does the event item page exist?</summary>
    public bool? EventItemPageExists { get; set; }

    /// <summary>Does the event item page content match (EventName vardefine)?</summary>
    public bool? EventItemPageContentMatches { get; set; }

    /// <summary>Does a matching reward template variant exist (by XP progression)?</summary>
    public bool? RewardTemplateMatches { get; set; }

    /// <summary>Does the matched template's full content match the generated output?</summary>
    public bool? RewardContentMatches { get; set; }

    /// <summary>Which variant matches (e.g., "Pet/3" for Rewards/Pet/3)</summary>
    public string? MatchingVariant { get; set; }

    /// <summary>Suggested page title (with disambiguation if needed)</summary>
    public string? SuggestedPageTitle { get; set; }

    // ── Derived 3-state properties ──

    /// <summary>Event page: Missing → Mismatch → Match</summary>
    public WikiCheckState EventPageState =>
        EventPageExists != true ? (EventPageExists == false ? WikiCheckState.Missing : WikiCheckState.Unknown)
        : EventPageContentMatches == true ? WikiCheckState.Match
        : EventPageContentMatches == false ? WikiCheckState.Mismatch
        : WikiCheckState.Match; // exists but not yet content-checked → treat as match

    /// <summary>Event item page: Missing → Mismatch → Match</summary>
    public WikiCheckState EventItemPageState =>
        EventItemPageExists != true ? (EventItemPageExists == false ? WikiCheckState.Missing : WikiCheckState.Unknown)
        : EventItemPageContentMatches == true ? WikiCheckState.Match
        : EventItemPageContentMatches == false ? WikiCheckState.Mismatch
        : WikiCheckState.Match; // exists but not yet content-checked → treat as match

    /// <summary>Reward template: Missing (red) → Mismatch/formatting (yellow) → Match (green).</summary>
    public WikiCheckState RewardTemplateState =>
        RewardTemplateMatches != true
            ? (RewardTemplateMatches == false ? WikiCheckState.Missing : WikiCheckState.Unknown)
        : RewardContentMatches == true ? WikiCheckState.Match
        : RewardContentMatches == false ? WikiCheckState.Mismatch
        : WikiCheckState.Match; // XP matched but content not yet checked → treat as match
}

// ── Full mystery event ───────────────────────────────────────────

public class MysteryEvent
{
    public string Name { get; set; } = "";
    public string ProgressionEventId { get; set; } = "";

    /// <summary>Numeric ConfigKey of the event item (for resolution via DataService)</summary>
    public long EventItemNumericId { get; set; }

    /// <summary>Resolved event item name</summary>
    public string? EventItemName { get; set; }

    /// <summary>Resolved event item type key</summary>
    public string? EventItemType { get; set; }

    /// <summary>Start date from Schedule.Start</summary>
    public DateTime? StartDate { get; set; }

    public MysteryType MysteryType { get; set; } = MysteryType.Standard;

    /// <summary>Pet name from RewardPet.PetRef.ConfigKey (Pet mysteries only)</summary>
    public string? PetName { get; set; }

    public List<MysteryRewardLevel> FreeTier { get; set; } = new();
    public List<MysteryRewardLevel> SilverTier { get; set; } = new();
    public List<MysteryRewardLevel> GoldTier { get; set; } = new();

    /// <summary>Number of premium (Silver+Gold) levels</summary>
    public int PremiumLevels { get; set; }

    public WikiPageStatus WikiStatus { get; set; } = new();
}

// ── Item mapping overrides ───────────────────────────────────────

public class MysteryItemMapping
{
    /// <summary>ItemKey → display name override</summary>
    public Dictionary<string, string> Overrides { get; set; } = new();
}

// ── Wiki status cache ───────────────────────────────────────────

/// <summary>
/// Persisted cache of wiki page status checks.
/// Only true values are trusted (page confirmed to exist / template confirmed to match).
/// false/null values are always re-checked.
/// </summary>
public class MysteryWikiStatusCache
{
    /// <summary>ProgressionEventId → cached status</summary>
    public Dictionary<string, CachedMysteryStatus> Entries { get; set; } = new();
}

public class CachedMysteryStatus
{
    public bool EventPageExists { get; set; }
    public bool EventPageContentMatches { get; set; }
    public bool EventItemPageExists { get; set; }
    public bool EventItemPageContentMatches { get; set; }
    public bool RewardTemplateMatches { get; set; }
    public bool RewardContentMatches { get; set; }
    public string? MatchingVariant { get; set; }
    public string? SuggestedPageTitle { get; set; }
}
