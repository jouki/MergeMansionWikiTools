using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MergeMansionWikiTools.Models;

// ── Reward type enum ─────────────────────────────────────────────

public enum MysteryRewardType
{
    Energy, Diamonds, Coins, Item, Decoration, Experience, CardPack, Perk, Pet, InformantTip
}

// ── Mystery type ─────────────────────────────────────────────────

public enum MysteryType { Standard, Pet }

// ── Single reward entry ──────────────────────────────────────────

public class MysteryReward
{
    public MysteryRewardType Type { get; set; }
    public int Amount { get; set; }
    public string? ItemKey { get; set; }
    public string? ItemDisplayName { get; set; }
    public int? ItemLevel { get; set; }
    public string? DecorationId { get; set; }
    public string? DecorationName { get; set; }
    public string? EnergyType { get; set; }
    public string? PerkId { get; set; }
    public string? CardPackId { get; set; }
    public string? PetName { get; set; }
    public string? InformantTipCardId { get; set; }
    public long? DurationMs { get; set; }
}

// ── One level in a tier ──────────────────────────────────────────

public class MysteryRewardLevel
{
    public int Level { get; set; }
    public int XpRequired { get; set; }
    public List<MysteryReward> Rewards { get; set; } = new();
}

// ── Perk data for level 0 paid tiers ────────────────────────────

public class MysteryPerkData
{
    public int ExtraInventorySlots { get; set; }
    public int FreeDailyGems { get; set; }
    public int EventXpBonus { get; set; }
}

// ── Wiki check state ─────────────────────────────────────────────

public enum WikiCheckState { Unknown, Missing, Mismatch, Match, Confirmed }

// ── Wiki page status ─────────────────────────────────────────────

public class WikiPageStatus
{
    public bool? EventPageExists { get; set; }
    public bool? EventPageContentMatches { get; set; }
    public bool? EventItemPageExists { get; set; }
    public bool? EventItemPageContentMatches { get; set; }

    public int ImagesTotalExpected { get; set; }
    public int ImagesExistOnWiki { get; set; }

    public WikiCheckState ImagesState =>
        ManualConfirm.ImagesConfirmed ? WikiCheckState.Confirmed
        : ImagesTotalExpected == 0 ? WikiCheckState.Unknown
        : ImagesExistOnWiki == 0 ? WikiCheckState.Missing
        : ImagesExistOnWiki >= ImagesTotalExpected ? WikiCheckState.Match
        : WikiCheckState.Mismatch;

    public bool? WikiMainPageListed { get; set; }
    public bool? WikiMysteryTableListed { get; set; }
    public bool? WikiModuleListed { get; set; }

    /// <summary>False when mystery is too old for main page (older than last year).
    /// Set by MysteryWikiService during status checks, or from MysteryEvent.StartDate.</summary>
    public bool MainPageEligible { get; set; } = true;

    /// <summary>Sets MainPageEligible based on mystery start date.</summary>
    public void UpdateMainPageEligibility(DateTime? startDate)
    {
        int currentYear = DateTime.Now.Year;
        MainPageEligible = !(startDate.HasValue && startDate.Value.Year < currentYear - 1);
    }

    public int WikiListedTotal => MainPageEligible ? 3 : 2;

    public int WikiListedCount =>
        (MainPageEligible && WikiMainPageListed == true ? 1 : 0) +
        (WikiMysteryTableListed == true ? 1 : 0) +
        (WikiModuleListed == true ? 1 : 0);

    public WikiCheckState WikiListedState
    {
        get
        {
            bool mainUnknown = MainPageEligible ? WikiMainPageListed == null : false;
            if (mainUnknown && WikiMysteryTableListed == null && WikiModuleListed == null)
                return WikiCheckState.Unknown;
            int count = WikiListedCount;
            if (count >= WikiListedTotal) return WikiCheckState.Match;
            if (count == 0) return WikiCheckState.Missing;
            return WikiCheckState.Mismatch;
        }
    }

    public bool? RewardTemplateMatches { get; set; }
    public bool? RewardContentMatches { get; set; }
    public string? MatchingVariant { get; set; }
    public string? MatchingGalleryVariant { get; set; }
    /// <summary>If set, this gallery template needs to be created on wiki during publish.</summary>
    public string? PendingGalleryTemplateContent { get; set; }
    public string? SuggestedPageTitle { get; set; }

    public MysteryManualConfirmFlags ManualConfirm { get; set; } = new();
    public int? MysteryTableIndex { get; set; }

    // ── Derived 3-state properties ──

    public WikiCheckState EventPageState =>
        ManualConfirm.EventPageConfirmed ? WikiCheckState.Confirmed
        : EventPageExists != true ? (EventPageExists == false ? WikiCheckState.Missing : WikiCheckState.Unknown)
        : EventPageContentMatches == true ? WikiCheckState.Match
        : EventPageContentMatches == false ? WikiCheckState.Mismatch
        : WikiCheckState.Unknown;

    public WikiCheckState EventItemPageState =>
        ManualConfirm.ItemPageConfirmed ? WikiCheckState.Confirmed
        : EventItemPageExists != true ? (EventItemPageExists == false ? WikiCheckState.Missing : WikiCheckState.Unknown)
        : EventItemPageContentMatches == true ? WikiCheckState.Match
        : EventItemPageContentMatches == false ? WikiCheckState.Mismatch
        : WikiCheckState.Unknown;

    public WikiCheckState RewardTemplateState =>
        ManualConfirm.RewardsConfirmed != null ? WikiCheckState.Confirmed
        : RewardTemplateMatches != true
            ? (RewardTemplateMatches == false ? WikiCheckState.Missing : WikiCheckState.Unknown)
        : RewardContentMatches == true ? WikiCheckState.Match
        : RewardContentMatches == false ? WikiCheckState.Mismatch
        : WikiCheckState.Unknown;
}

// ── Full mystery event ───────────────────────────────────────────

public class MysteryEvent
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Original name from JSON (e.g. "Season Pass - Buzzing with Purpose") before any
    /// prefix stripping. Used by wiki migration to locate the legacy page/file titles.
    /// Equal to <see cref="Name"/> when no stripping applied.
    /// </summary>
    public string RawJsonName { get; set; } = "";

    /// <summary>True when the JSON name began with the "Season Pass - " prefix that we strip.</summary>
    public bool HadSeasonPassPrefix => !string.Equals(RawJsonName, Name, StringComparison.Ordinal);

    public string ProgressionEventId { get; set; } = "";
    public long EventItemNumericId { get; set; }
    public string? EventItemName { get; set; }
    public string? EventItemType { get; set; }
    public string? EventItemPoolTag { get; set; }
    public DateTime? StartDate { get; set; }
    public TimeSpan? Duration { get; set; }

    public int? DurationDays
    {
        get
        {
            if (!Duration.HasValue) return null;
            if (!StartDate.HasValue) return (int)Math.Ceiling(Duration.Value.TotalDays);
            return ((StartDate.Value + Duration.Value).Date - StartDate.Value.Date).Days;
        }
    }

    public DateTime? EndDate =>
        StartDate.HasValue && Duration.HasValue ? (StartDate.Value + Duration.Value).Date : null;

    public MysteryType MysteryType { get; set; } = MysteryType.Standard;
    public string? PetName { get; set; }

    public List<MysteryRewardLevel> FreeTier { get; set; } = new();
    public List<MysteryRewardLevel> SilverTier { get; set; } = new();
    public List<MysteryRewardLevel> GoldTier { get; set; } = new();
    public List<MysteryRewardLevel> BonusTier { get; set; } = new();
    public List<MysteryRewardLevel> RecurringFreeTier { get; set; } = new();
    public List<MysteryRewardLevel> RecurringPremiumTier { get; set; } = new();
    public bool HasZeroLevel { get; set; } = true;
    public bool IsV2 { get; set; }
    public MysteryPerkData? Track1PerkData { get; set; }
    public MysteryPerkData? Track2PerkData { get; set; }

    public WikiPageStatus WikiStatus { get; set; } = new();

    /// <summary>Name used for wiki image filenames. Uses SuggestedPageTitle when available.</summary>
    public string WikiImageName => WikiStatus.SuggestedPageTitle ?? Name;
}

// ── Manual confirmation flags ────────────────────────────────────

public class MysteryManualConfirmFlags
{
    public bool EventPageConfirmed { get; set; }
    /// <summary>null = not confirmed, "true" = confirmed with current wiki template, "Pet/3" = confirmed with specific variant</summary>
    public string? RewardsConfirmed { get; set; }
    public bool ItemPageConfirmed { get; set; }
    public bool ImagesConfirmed { get; set; }
    public bool AnyConfirmed => EventPageConfirmed || RewardsConfirmed != null || ItemPageConfirmed || ImagesConfirmed;
}

// ── Diff models ──────────────────────────────────────────────────

public enum DiffLineType { Match, Added, Removed, Modified }

public class DiffLine
{
    public DiffLineType Type { get; set; }
    public string Text { get; set; } = "";
    public string? OldText { get; set; }
}

// ── Diff section scope ───────────────────────────────────────────

public enum MysteryDiffScope { Rewards, EventPage, EventItemPage }

// ── Decoration detection models ──────────────────────────────────

public class DetectedDecorationFile
{
    public string SourcePath { get; set; } = "";
    public string WikiFilename { get; set; } = "";
    public string Category { get; set; } = "";
    public SpriteRect? AtlasRect { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool? ExistsOnWiki { get; set; }
    public string? ProcessedPath { get; set; }
    public long? OptimizedSize { get; set; }
}

public class SpriteRect { public int X { get; set; } public int Y { get; set; } public int Width { get; set; } public int Height { get; set; } }

// ── Dialogue models ──────────────────────────────────────────────

public class DialogueGroup
{
    public string TabName { get; set; } = "";
    public List<DialogueLine> Lines { get; set; } = new();
}

public class DialogueLine
{
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
}

// ── Item mapping overrides ───────────────────────────────────────

public class MysteryItemMapping
{
    public Dictionary<string, string> Overrides { get; set; } = new();
}

// ── Wiki status cache ────────────────────────────────────────────

public class MysteryWikiStatusCache
{
    public Dictionary<string, CachedMysteryStatus> Entries { get; set; } = new();
}

public class CachedMysteryStatus
{
    public bool? EventPageExists { get; set; }
    public bool? EventPageContentMatches { get; set; }
    public bool? EventItemPageExists { get; set; }
    public bool? EventItemPageContentMatches { get; set; }
    public bool? RewardTemplateMatches { get; set; }
    public bool? RewardContentMatches { get; set; }
    public string? MatchingVariant { get; set; }
    public string? SuggestedPageTitle { get; set; }
    public int ImagesTotalExpected { get; set; }
    public int ImagesExistOnWiki { get; set; }
    public bool EventPageConfirmed { get; set; }
    [JsonConverter(typeof(BoolOrStringJsonConverter))]
    public string? RewardsConfirmed { get; set; }
    public bool ItemPageConfirmed { get; set; }
    public bool ImagesConfirmed { get; set; }
    public int MysteryTableIndex { get; set; }
    public bool? WikiMainPageListed { get; set; }
    public bool? WikiMysteryTableListed { get; set; }
    public bool? WikiModuleListed { get; set; }
}

// ── Update preview step ──────────────────────────────────────────

public class MysteryUpdateStep
{
    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool IsNoChange { get; set; }
    public string? WikiUrl { get; set; }
    public string? ContentPreview { get; set; }
    public string? ContextAbove { get; set; }
    public string? ContextBelow { get; set; }
    public bool IsEnabled { get; set; } = true;
    /// <summary>When set, the step is disabled (unchecked + grayed) with this reason shown.</summary>
    public string? DisabledReason { get; set; }
}

/// <summary>JSON converter for backward-compatible deserialization of RewardsConfirmed (bool→string migration).</summary>
public class BoolOrStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.True) return "true";
        if (reader.TokenType == JsonTokenType.False) return null;
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        throw new JsonException($"Unexpected token {reader.TokenType} for BoolOrStringJsonConverter");
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
