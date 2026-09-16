using Game.Cloud.Config;
using GameLogic.Player.Requirements;
using Metaplay.Core;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Support;

/// <summary>
/// Golden dump shape for one <see cref="PlayerRequirement"/> entry (hotspot
/// <c>RequirementsList</c>/<c>UnlockRequirementsList</c>, event <c>UnlockRequirement</c>): a
/// single-key JSON object whose key is the subtype's discriminator and whose value is that
/// subtype's own payload — e.g. <c>{"CardStack": "Lounge2"}</c>, <c>{"RequiredCost": {"Type":
/// "Coins", "Currency": "Coins", "CurrencyAmount": 1500}}</c>, or the bare string
/// <c>"HasAnyPet"</c> for a subtype with no payload at all.
/// </summary>
/// <remarks>
/// <para>
/// Golden shapes confirmed against <c>Dump 2/areas.json</c> (26.07.01) and its
/// <c>UnlockRequirementsList</c> / <c>events.json</c> <c>UnlockRequirement</c> siblings, across
/// every hotspot of every area. Confidence levels (fix-round-1, see task-4-report.md "Fix round 1"
/// section for the full story of how the first pass mis-scored <see cref="HotspotCompletedRequirement"/>):
/// </para>
/// <list type="bullet">
/// <item><description><b>VERIFIED BY EXECUTION</b> (a throwaway harness ran the actual
/// <see cref="From"/> code against a constructed instance and diffed the JSON output against the
/// literal golden string):
/// <see cref="CardStackRequirement"/> -&gt; <c>CardStack</c> (14 hits, bare string payload);
/// <see cref="CompleteIllustrationRequirement"/> -&gt; <c>CompleteIllustration</c> (3 hits, bare
/// string payload); <see cref="HotspotCompletedRequirement"/> -&gt; <c>HotspotCompleted</c> (in
/// <c>UnlockRequirementsList</c>, 1 hit, bare string payload — this one required
/// <see cref="ConfigDefinitionConverter"/> to be registered on the caller's serializer settings;
/// without it, the payload wrongly renders as <c>{"ConfigKey": ...}</c> instead of the bare
/// resolved name, which is what fix round 1 was about); <see cref="HasAnyPetRequirement"/> -&gt;
/// the bare string <c>HasAnyPet</c> (0-member subtype, no payload at all).</description></item>
/// <item><description><b>Shape matched by source inspection only, NOT execution-verified:</b>
/// <see cref="CostRequirement"/> -&gt; <c>RequiredCost</c> (8 hits) — the sole confirmed exception
/// to the default naming rule below (its own <c>[MetaMember]</c> property is literally named
/// <c>RequiredCost</c>, not the stripped class name "Cost"); the nested <c>ICost</c> payload shape
/// (<c>{"Type": "Coins", "Currency": "Coins", "CurrencyAmount": 1500}</c>) is rendered by whatever
/// serializes <c>ICost</c> elsewhere, which is out of this task's scope, so it could not be
/// execution-verified here. <see cref="AreaCompletedRequirement"/> -&gt; <c>AreaCompleted</c> (in
/// events.json <c>UnlockRequirement</c>, bare string payload — the resolved <c>AreaRef</c> key,
/// via the pre-existing, unmodified <c>MetaRefConverter</c>); constructing a *resolved*
/// <c>MetaRef&lt;AreaInfo&gt;</c> needs an <c>IGameConfigDataResolver</c> stub the harness didn't
/// build, so this one was reasoned about but not run.</description></item>
/// <item><description><b>No matching subtype at all — UNVERIFIED, cannot be produced by
/// <see cref="From"/> today:</b> <c>ItemAcquired</c>: [{"ItemRef": "...", "Requirement": 1}] —
/// 14360 hits, by far the most common requirement shape in the golden dump. No subtype in
/// <c>GameLogic/Player/Requirements/*.cs</c> matches it (closest by intent is
/// <see cref="ItemNeededRequirement"/>, but its stub only exposes a single <c>ItemDef</c> member,
/// not a list of ItemRef/amount pairs). <c>LevelNeeded</c>: {"Min": 20} (events.json
/// <c>UnlockRequirement</c> for CollectibleBoards/SoloMilestoneEvents/GarageCleanups/Leaderboards/
/// MixABoosterEvents/ProgressionPackEvents/CoreSupportEvents/Progressions) — closest by shape is
/// <see cref="PlayerLevelRequirement"/> (<c>Min</c>/<c>Max</c> members), but its default
/// discriminator would be "PlayerLevel", not "LevelNeeded". <c>TimeNeeded</c>:
/// {"StartInclusive": "..."} (354 hits, in <c>UnlockRequirementsList</c>) — no matching subtype
/// found either. Use <see cref="Of"/> as an explicit escape hatch for these until a task with
/// access to the real game assembly can wire up the correct subclass.</description></item>
/// </list>
/// <para>
/// Every other subtype in <c>GameLogic/Player/Requirements/*.cs</c> (and
/// <c>Code/GameLogic/Player/Requirements/*.cs</c>) falls back to the default rule below with no
/// golden example to verify against at all — see task-4-report.md for the full list.
/// </para>
/// <para>
/// Default rule (applied by <see cref="From"/>): the JSON key is the subtype's class name with a
/// trailing "Requirement" stripped (e.g. <c>ImpossibleRequirement</c> -&gt; "Impossible";
/// <c>ProgressionEventCurrencyPerkActive</c>, which doesn't end in "Requirement", is used as-is).
/// The payload is: nothing (bare Kind string) when the subtype declares zero
/// <c>[MetaMember]</c>s; that single member's own value when it declares exactly one (matches
/// every confirmed single-member case above); or the full member set as a nested object
/// (<see cref="MetaObjectPayload"/>) when it declares two or more — none of the golden examples
/// exercise this third branch, so it is unverified but the most defensible generalization of the
/// observed pattern.
/// </para>
/// <para>
/// <b>Caller responsibility:</b> when a single-member subtype's value is a
/// <c>Game.Cloud.Config.ConfigDefinition&lt;,&gt;</c> (currently only <see cref="HotspotDef"/>
/// among confirmed requirement shapes), the caller's <c>JsonSerializerSettings.Converters</c> MUST
/// include a <see cref="ConfigDefinitionConverter"/> or the payload silently reverts to the wrong
/// <c>{"ConfigKey": ...}</c> shape — this is exactly the bug fix round 1 fixed for
/// <see cref="HotspotCompletedRequirement"/>.
/// </para>
/// </remarks>
[JsonConverter(typeof(SingleKeyJsonConverter))]
public sealed class RequirementModel : ISingleKeyJson
{
    /// <summary>
    /// Subtypes whose golden discriminator is not simply the class name minus "Requirement".
    /// <list type="bullet">
    /// <item><description><see cref="CostRequirement"/> -&gt; <c>RequiredCost</c> — its own
    /// <c>[MetaMember]</c> is literally named <c>RequiredCost</c>.</description></item>
    /// <item><description><see cref="PlayerCurrentTimeRequirement"/> -&gt; <c>TimeNeeded</c> —
    /// confirmed by execution against the 26.07.01 chain golden, where the three
    /// <c>ActivationRequirements</c> entries read <c>{"TimeNeeded": {"StartInclusive":
    /// "2024-12-01T08:00:00.001"}}</c> (the second member, <c>EndExclusive</c>, is null in all
    /// three and therefore omitted).</description></item>
    /// <item><description><see cref="PlayerLevelRequirement"/> -&gt; <c>LevelNeeded</c> — the payload
    /// is its own member set (<c>{"Min": 5}</c>, <c>Max</c> null and therefore omitted), only the
    /// discriminator differs from the default "PlayerLevel".</description></item>
    /// <item><description><see cref="PlayerItemRequirement"/> -&gt; <c>ItemAcquired</c>,
    /// <see cref="PlayerSeenItemRequirement"/> -&gt; <c>ItemSeen</c> and
    /// <see cref="ItemNeededAndConsumeRequirement"/> -&gt; <c>ItemNeededAndConsumed</c> — these three
    /// also carry a hand-built payload, see <see cref="TryItemPayload"/>.</description></item>
    /// </list>
    /// All four were confirmed against 26.07.01 <c>areas.json</c> and hold across all six live
    /// archives and their patch labels.
    /// </summary>
    private static readonly Dictionary<string, string> RenamedKinds = new(StringComparer.Ordinal)
    {
        [nameof(CostRequirement)] = "RequiredCost",
        [nameof(PlayerCurrentTimeRequirement)] = "TimeNeeded",
        [nameof(PlayerItemRequirement)] = "ItemAcquired",
        [nameof(PlayerSeenItemRequirement)] = "ItemSeen",
        [nameof(ItemNeededAndConsumeRequirement)] = "ItemNeededAndConsumed",
        [nameof(PlayerLevelRequirement)] = "LevelNeeded",
    };

    public string Kind { get; }
    public object? Payload { get; }

    private RequirementModel(string kind, object? payload)
    {
        Kind = kind;
        Payload = payload;
    }

    /// <summary>Builds a wrapper with an explicit kind/payload pair (bypassing subtype inference).</summary>
    public static RequirementModel Of(string kind, object? payload) => new(kind, payload);

    /// <summary>Maps a <see cref="PlayerRequirement"/> instance to its golden JSON shape (see remarks above).</summary>
    public static RequirementModel From(PlayerRequirement requirement, IDumpLog? log = null, MetaRefSkip refSkip = MetaRefSkip.EmptyKey)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var effectiveLog = log ?? ConsoleDumpLog.Instance;

        var kind = RenamedKinds.TryGetValue(requirement.GetType().Name, out var renamed)
            ? renamed
            : StripSuffix(requirement.GetType().Name, "Requirement");

        if (TryItemPayload(requirement, effectiveLog, out var itemPayload))
            return new RequirementModel(kind, itemPayload);

        var members = MetaObjectWriter.Members(requirement);
        if (members.Count == 0)
            return new RequirementModel(kind, null);

        if (members.Count > 1)
            return new RequirementModel(kind, new MetaObjectPayload(requirement, effectiveLog, refSkip));

        var (name, _, get) = members[0];
        object? value;
        try
        {
            value = get();
        }
        catch (Exception ex)
        {
            effectiveLog.Warn($"{requirement.GetType().Name}.{name}: {ex.GetType().Name} — {ex.Message} — skipped");
            return new RequirementModel(kind, null);
        }

        // An UNRESOLVED reference still writes its key; only an EMPTY one collapses the payload.
        // Golden proof: under the races_on_off_01_B patch, which removes the area, the classic-races
        // events still read {"AreaCompleted": "MansionSideEntrance"}.
        if (value is IMetaRef metaRef && (metaRef.KeyObject is null || metaRef.KeyObject.ToString() is null))
            return new RequirementModel(kind, null);

        return new RequirementModel(kind, value);
    }

    /// <summary>
    /// The three item-flavoured requirement subtypes whose golden payload is neither their single
    /// member's value nor their whole member set. All three are confirmed against 26.07.01
    /// <c>areas.json</c> (and identical across all six live archives and their patches):
    /// <list type="bullet">
    /// <item><description><see cref="PlayerItemRequirement"/> -&gt; <c>"ItemAcquired": [{"ItemRef":
    /// "SeedBagEmpty_01", "Requirement": 1}]</c> — an ARRAY, one entry per <c>ItemRefs</c> element,
    /// each repeating the requirement's single <c>Requirement</c> count. 14 360 hits in the newest
    /// archive; exactly one of them has two items (the DogArea circular saw station, empty +
    /// producing), which is what proves it is a per-item list rather than a single object.
    /// The member is called <c>ItemRefs</c> in the config but <c>ItemRef</c> in the dump.</description></item>
    /// <item><description><see cref="PlayerSeenItemRequirement"/> -&gt; <c>"ItemSeen": {"ItemRef":
    /// "LoveStory_03", "Requirement": 0}</c> — same <c>ItemRef</c> spelling for its <c>ItemDef</c>
    /// member, but a single object, not a list.</description></item>
    /// <item><description><see cref="ItemNeededAndConsumeRequirement"/> -&gt;
    /// <c>"ItemNeededAndConsumed": "CorridorKey_01"</c> — the bare first entry of its
    /// <c>ItemDefs</c> list (all 8 golden hits are single-item); the sibling <c>ItemTypes</c> list
    /// of raw ids never appears in the dump.</description></item>
    /// </list>
    /// The <c>ItemDef</c> values are handed to the caller's serializer as-is, so — like every other
    /// <c>ConfigDefinition</c> payload here — they need a <see cref="ConfigDefinitionConverter"/>
    /// registered to come out as item-type strings.
    /// </summary>
    private static bool TryItemPayload(PlayerRequirement requirement, IDumpLog log, out object? payload)
    {
        switch (requirement)
        {
            case PlayerItemRequirement itemReq:
                payload = (MetaObjectWriter.GetMember(itemReq, "ItemRefs", log) as System.Collections.IEnumerable ?? Array.Empty<object>())
                    .Cast<object?>()
                    .Where(r => r is not null)
                    .Select(r => (object)new ItemRefPayload(r!, itemReq.Requirement))
                    .ToList();
                return true;

            case PlayerSeenItemRequirement seenReq:
                payload = seenReq.ItemDef is null ? null : new ItemRefPayload(seenReq.ItemDef, seenReq.Requirement);
                return true;

            case ItemNeededAndConsumeRequirement consumeReq:
                payload = consumeReq.ItemDefs is { Count: > 0 } defs ? defs[0] : null;
                return true;

            default:
                payload = null;
                return false;
        }
    }

    /// <summary>The <c>{ItemRef, Requirement}</c> pair shared by ItemAcquired and ItemSeen.</summary>
    private sealed class ItemRefPayload
    {
        public ItemRefPayload(object itemRef, int requirement)
        {
            ItemRef = itemRef;
            Requirement = requirement;
        }

        public object ItemRef { get; }
        public int Requirement { get; }
    }

    private static string StripSuffix(string name, string suffix) =>
        name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;

    /// <summary>
    /// Requirement subtypes the golden dump does NOT recognise: it writes them as a bare empty
    /// object <c>{}</c> — no discriminator, no payload — and logs "Unknown requirement &lt;type&gt;".
    /// All four are the newest members of the union (<c>[MetaSerializableDerived]</c> 65, 66, 68, 69),
    /// so the reading is that the reference implementation's kind switch simply predates them.
    /// <para>
    /// Only <c>events.json</c> exercises this: 1 830 of the <c>DailyChallenges*Objectives</c>
    /// <c>requirements</c> entries are <c>{}</c>, against 205 real <c>ItemAcquired</c> ones. Neither
    /// <c>chain_item_odds.json</c> nor <c>areas.json</c> contains any of the four, and both stayed
    /// byte-identical after this was added.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> UnsupportedKinds = new(StringComparer.Ordinal)
    {
        "HasActivableKindActiveForDurationRequirement",
        "HasUncompletedAreasRequirement",
        "HasEnoughItemTimeRequirement",
        "HasEnoughPendingTradesRequirement",
    };

    /// <summary>
    /// True when <paramref name="requirement"/> is one of the subtypes the dump writes as <c>{}</c>
    /// (see <see cref="UnsupportedKinds"/>); the caller writes the empty object itself, since the
    /// single-key wrapper cannot express "no key at all".
    /// </summary>
    public static bool IsUnsupported(PlayerRequirement requirement) =>
        requirement != null && UnsupportedKinds.Contains(requirement.GetType().Name);
}
