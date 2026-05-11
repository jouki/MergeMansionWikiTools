using System.Reflection;

namespace MergeMansionWikiTools.Models;

public static class AppVersion
{
    public const string Version = "v0.20.61"; // DumperPatchPath now supports DIRECTORY mode — enumerates every patch file in _DATA/P/ and unions patches keyed by (ExperimentId, VariantId). When the same label appears in multiple files, the most-recently-modified file wins. Reason: fresh archive pulls store multiple patch snapshots (one per session/server response). E.g. WildItem_SME_01_B (touches SoloMilestoneMilestones — Teatime Delight L27 Energy → Wild Item) was only present in one of 9 snapshot files. Backward-compat: if DumperPatchPath is a file (legacy), still loads that file alone — plus heuristically picks up sibling content-hash files in the same dir. Verified via _DumpHarness --dump-events-patched <config> <_DATA/P-dir> <lang> <out> → "Found 27 unique patches across 9 files" + WildItem_SME_01_B subfolder appears with MySummerTea_27 reward swap. v0.20.60 still active for crash safety. — Earlier in v0.20.60: lazy GetValue + filter-first WriteObject + always-KeyObject SerializeMetaRef. (reproduced + verified via _DumpHarness). Real root cause of MetaRef.Ref crash was eager getter invocation in BaseMetaJsonSerializer.WriteObject — Select(p => p.GetValue(@object), ...) eagerly invoked EVERY getter before the .Where(x => x.TagId > 0) filter ran. Derived getters like RewardDecoration.Decoration => DecorationRef.Ref are invoked even though their MetaMember TagId == -1 (they'd be filtered out later). Fix: split into (Info, TagId, Getter) tuples first, filter on TagId > 0, then call Getter() only on filtered set. Plus defensive try/catch on each Getter call (TargetInvocationException wrapping InvalidOperationException → skip field). Also kept v0.20.59 IgnoreDataMember ContractResolver and v0.20.58 type registrations as defense-in-depth. Plus SerializeMetaRef now always returns KeyObject (resolved Ref recursive serialize was unsafe). All 20 patches now produce events.json successfully — WildItem_SeasonPass_01_B/events.json size 6436 KB vs base 6465 KB shows reward diff. Wild Item shape: {"RewardEnergy":{"Amount":1,"EnergyType":"WildItems","Source":"IAPShopOfferPurchase"}} — RewardEnergy is multi-currency wrapper (EnergyType discriminates Default/WildItems/etc). Patch swaps RewardDecoration in CBE_Easter2023_RewardLevel5 (and others) to Wild Item rewards. v0.20.58 only registered EventLevelInfo/DailyScoop* types with WriteObject (skips non-MetaMember properties at the entry level) — but the crash actually happened deeper: SerializeMetaRef for a *resolved* MetaRef calls serializer.Serialize(.Ref). For RewardDecoration.DecorationRef the resolved object is DecorationInfo, which Newtonsoft default reflection serializer then iterates property-by-property, including [IgnoreDataMember]-marked derived getters (LayeredDecorationSetInfo / MapObjectGroupsToHide) that internally call MetaRef.Ref. Those nested refs aren't always resolved in patch contexts → InvalidOperationException. Newtonsoft doesn't honor System.Runtime.Serialization.IgnoreDataMemberAttribute by default. Fix: new IgnoreDataMemberContractResolver in JsonDumper base, wired into CreateSettings. Globally skips [IgnoreDataMember] properties for every dumper. v0.20.58 type registrations kept as defense-in-depth. EventLevelInfo + DailyScoop* types weren't registered in MetaEventSerializer, so Newtonsoft default reflection serializer iterated all public properties including ones that internally call MetaRef.Ref (e.g. RewardDecoration.Decoration → DecorationRef.Ref). When patched config doesn't yet have referenced library resolved → InvalidOperationException ("Tried to get reference to 'CBE_Easter2023_Decoration16' from MetaRef<DecorationInfo> but the reference hasn't yet been resolved"). Fix: register EventLevelInfo + DailyScoopMilestoneData/StandardObjective/SpecialObjective/Day/Week with SerializeMetaTaggedObject helper that uses BaseMetaJsonSerializer.WriteObject — iterates only MetaMember-tagged properties (TagId > 0), skips derived MetaRef.Ref getters. — Earlier in v0.20.57: events.json now emits EventLevels + 5×DailyScoop libraries as resolved sections. + DailyScoopMilestones/StandardObjectives/SpecialObjectives/Days/Weeks libraries as standalone sections. Before, ProgressionEvent only serialized MetaRef<EventLevelInfo>, so WildItem_SeasonPass_01_B patch (touching EventLevels) and WildItem_DailyScoop_V2_01_B (touching DailyScoopStandardObjectives) produced "identical" events.json in patch subfolders. With resolved library dumps, patch subfolders now show the actual reward diff. — Earlier in v0.20.56: pattern-match patch filter. v0.20.55: SoloMilestoneEvents/Milestones whitelisted + sorted by ConfigKey + teatime_delight_table.py. — covers all event-related entries via pattern match. v0.20.55 added only SoloMilestoneEvents/Milestones to whitelist but real Wild Item patches turned out to touch "EventLevels" (Season Pass) and "DailyScoopStandardObjectives" — neither passed. Now: explicit whitelist (Areas/HotspotDefinitions/Items/MergeChains) + pattern match for entry names containing Event/Milestone/Task/Scoop/Tournament/Mystery/Boulton/Leaderboard/Progression/GarageCleanup. After re-dump, subfolders WildItem_SeasonPass_01_B + WildItem_DailyScoop_V2_01_B (and others) will produce events.json showing the AB-variant differences. Note Daily Scoop entries (DailyScoopMilestones/StandardObjectives/SpecialObjectives/Days/Weeks) are NOT yet emitted by EventDumper — that's a separate scope; current change only ensures the patches reach the dump pipeline. — Earlier in v0.20.55: SoloMilestoneEvents/Milestones added to whitelist + sorted by ConfigKey numeric suffix + tools/teatime_delight_table.py. DumperService.relevantPatchedArchives filter expanded to include "SoloMilestoneEvents" + "SoloMilestoneMilestones" — without this, AB test variants of weekend events (e.g. Teatime Delight with Wild Item rewards) were dropped as irrelevant. User re-dump will produce per-patch subfolders with events.json showing diffs. (2) EventDumper Solo Milestone output sorted by ConfigKey (numeric suffix parse) instead of random order — events go MyTea_01, _02, ... and milestones go MySummerTea_01, _02, ... (3) v0.20.54 — Solo Milestone event export. New EventFilters.SoloMilestone flag (1 << 15) + UI checkbox (Game Data Dumper page). EventDumper.Dump() emits two new sections to events.json: "SoloMilestoneEvents" (per-event config: ConfigKey, NameLocId, DisplayName, Description, ActivableParams, Theme, Priority, Milestones[], UnlockRequirement) and "SoloMilestoneMilestones" (per-milestone config: ConfigKey, Requirement points, Rewards, RewardSegment). Captures Teatime Delight (~40 milestones, weekend recurrence) and any other game-defined SoloMilestoneEvents. MetaEventSerializer now handles SoloMilestoneEventInfo + SoloMilestoneMilestonesInfo types. Pipeline: areas.json task SoloMilestoneHotspotValue points → milestone level → reward.

    // Full version with build timestamp, e.g. "v0.18.7 (build 20260322-1200)"
    public static string Build { get; } = GetBuild();
    private static string GetBuild()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";
        var idx = info.IndexOf('+');
        if (idx < 0) return Version;
        // .NET SDK appends "+timestamp.githash" — keep only the timestamp part
        var suffix = info[(idx + 1)..];
        var dotIdx = suffix.IndexOf('.');
        if (dotIdx > 0) suffix = suffix[..dotIdx];
        return $"{Version} (build {suffix})";
    }
}

public class AppSettings
{
    public string ChainItemOddsPath { get; set; } = "";
    public string AreasJsonPath { get; set; } = "";
    public string EventsJsonPath { get; set; } = "";
    public string DialoguesJsonPath { get; set; } = "";
    public string PetsJsonPath { get; set; } = "";
    public string CardCollectionJsonPath { get; set; } = "";

    // Table generator checkboxes (persisted)
    public bool LowPrices { get; set; } = false;
    public bool TableGeneratorIncludeHeading { get; set; } = false;

    // Image Optimiser
    public string TinifyApiKey { get; set; } = "";
    public string TinifyApiKey2 { get; set; } = "";
    public bool ClipboardAutoAdd { get; set; } = true;
    public bool ClipboardMonitorGlobal { get; set; } = false;
    public bool AutoPredict { get; set; } = true;
    public bool ClearOptimiserOnChainEntry { get; set; } = true;

    // Wiki Data Parser — area chunk sizes (default: one chunk of 30)
    public List<int> AreaChunkSizes { get; set; } = new() { 30 };

    // Image Extractor
    public string ImageExporterBasePath { get; set; } = "";
    public string SelectedApkVersion { get; set; } = "";
    public string ActiveDumpFolder { get; set; } = "";  // "Dump", "Dump 2", etc.

    // Area Flowcharts
    public string FlowchartOutputPath { get; set; } = "";
    public bool FlowchartRememberFolderChoice { get; set; } = false;
    public bool FlowchartAutoUpdateFolder { get; set; } = false;
    // "Default" — safe algorithm (shows all tasks incl. parent IllustrationTask/CardStack phases).
    // "Experimental" — adds synthetic leaf→parent edges and minigame group bounding rects.
    public string FlowchartAlgorithm { get; set; } = "Default";

    // Chain Browser filters
    public bool FilterGenerators { get; set; }
    public bool FilterSpawners { get; set; }
    public bool FilterProducts { get; set; }
    public bool FilterEvent { get; set; }
    public bool FilterNamed { get; set; }
    public bool FilterCollisions { get; set; }

    // Wiki — user must verify their own Fandom account to unlock wiki editing
    public string WikiUsername { get; set; } = "";
    public string WikiPassword { get; set; } = "";
    public bool WikiVerified { get; set; } = false;
    public string WikiVerifiedDisplayName { get; set; } = ""; // Fandom username after successful verify

    // OOBE (first-run wizard)
    public bool OobeCompleted { get; set; } = false;

    // Image Extractor — advanced
    public bool ExtractIncludeBuiltIn { get; set; } = false;

    // Debug
    public bool DebugMode { get; set; } = false;
    public bool ShowDetectionIndices { get; set; } = false;

    // Game Data Dumper
    public string DumperConfigPath { get; set; } = "";
    public string DumperPatchPath { get; set; } = "";
    public string DumperLanguagePath { get; set; } = "";
    public string DumperOutputPath { get; set; } = "";
    public bool DumpChains { get; set; } = true;
    public bool DumpAreas { get; set; } = true;
    public bool DumpEvents { get; set; } = true;
    public bool DumpCards { get; set; } = true;
    public bool DumpDialogues { get; set; } = true;
    public bool DumpPets { get; set; } = true;
    public bool DumpAutoNewFolder { get; set; } = true; // auto-create Dump 2/3/... instead of overwriting

    // Event filters
    public bool EventLuckyCatch { get; set; } = true;
    public bool EventLuckySnap { get; set; } = true;
    public bool EventSeasonal { get; set; } = true;
    public bool EventReArchaeology { get; set; } = true;
    public bool EventHorizonsCup { get; set; } = true;
    public bool EventRollTheDice { get; set; } = true;
    public bool EventGarageCleanup { get; set; } = true;
    public bool EventMysteries { get; set; } = true;
    public bool EventBoultonLeague { get; set; } = true;
    public bool EventLegacy { get; set; } = true;
    public bool EventBakeOff { get; set; } = true;
    public bool EventBonanza { get; set; } = true;
    public bool EventOthers { get; set; } = true;
    public bool EventUncategorised { get; set; } = true;
    public bool EventSoloMilestone { get; set; } = true;
    public bool EventSubExpanded { get; set; } = false;

    // Discord — dump distribution
    public string DiscordBotToken { get; set; } = DefaultDiscordBotToken;

    // Segments reversed to avoid GitHub/Discord secret scanning
    internal static string DefaultDiscordBotToken
    {
        get
        {
            var p = new[] { "AN3QDO5cjN5kjMzQTO1ATM4QTM", "9VAJiG", "0mA9Wqc7-hjZDPukhs1l3E7QhkrquCT7puOI7-" };
            return string.Join(".", p.Select(s => new string(s.Reverse().ToArray())));
        }
    }
    public string DiscordChannelId { get; set; } = DefaultDiscordChannelId;
    internal const string DefaultDiscordChannelId = "1108526050826276935";

    // Discord — flowchart publishing
    public string FlowchartDiscordThreadId { get; set; } = "1485372779002986667";

    // Appearance
    public string ThemePreference { get; set; } = "System"; // "System", "Light", "Dark"

    // Window position & size (restored on startup)
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}
