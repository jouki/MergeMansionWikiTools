using Game.Cloud.Config;
using GameLogic;
using GameLogic.Area;
using GameLogic.Config;
using GameLogic.Hotspots;
using MergeMansionWikiTools.Dumper.Core;
using Metaplay.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// <para>
/// Owns every shape in <c>areas.json</c> that plain <c>[MetaMember]</c> reflection cannot produce:
/// the area wrapper (localized <c>Name</c> + the Graphviz <c>TaskDependencies</c> graph), the
/// hotspot object (localized description, per-task event-token values, minigame theme, inlined card
/// stack). The <c>IDirectorAction</c> union it embeds lives in <see cref="DirectorActionSerializer"/>,
/// which <c>events.json</c> needs too.
/// </para>
/// <para>
/// Everything else is left to the shared converters: requirements/rewards to
/// <c>PlayerRequirementConverter</c>/<c>PlayerRewardConverter</c>, references to
/// <c>MetaRefConverter</c>, <c>HotspotDef</c>/<c>ItemDef</c> keys to
/// <see cref="ConfigDefinitionConverter"/> (which is what makes <c>UnlockingParentRefs</c> come out
/// as a plain list of hotspot names), and any embedded <see cref="GameLogic.Player.Items.ItemDefinition"/>
/// to <see cref="ChainSerializer"/>.
/// </para>
/// </summary>
public sealed partial class AreaSerializer : JsonConverter
{
    private readonly SharedGameConfig _config;
    private readonly IDumpLog _log;

    public AreaSerializer(SharedGameConfig config, IDumpLog log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? ConsoleDumpLog.Instance;
    }

    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) =>
        typeof(AreaInfo).IsAssignableFrom(objectType)
        || typeof(HotspotDefinition).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        switch (value)
        {
            case null: writer.WriteNull(); return;
            case AreaInfo area: WriteArea(writer, area, serializer); return;
            case HotspotDefinition hotspot: WriteHotspot(writer, hotspot, serializer); return;
            default: MetaObjectWriter.WriteObject(writer, value, serializer, _log, refSkip: MetaRefSkip.Unresolved); return;
        }
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(AreaSerializer)} is write-only (dumper never deserializes).");

    // ── area ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An area is two computed members — the localized <c>Name</c> and the <c>TaskDependencies</c>
    /// DOT graph — followed by <see cref="AreaInfo"/>'s own <c>[MetaMember]</c>s in TagId order.
    /// <para>
    /// <c>Name</c> is <c>LocMan.GetHotspotTitle(areaId)</c>, i.e. the <c>HotspotTitle_&lt;AreaId&gt;</c>
    /// key — NOT the area's own <c>TitleLocalizationId</c> member. The two differ for exactly three
    /// areas in golden and the AreaId spelling always wins: <c>GreatHall</c> carries
    /// <c>HotspotTitle_GrandHall</c> ("Great Hall") yet is named the unresolved key
    /// <c>"HotspotTitle_GreatHall"</c>, and so are <c>LandingRoom</c> and <c>MaddieMeetsMansion</c>
    /// (the latter has no <c>TitleLocalizationId</c> at all). An unresolved key is written verbatim
    /// because that is what <c>LocMan.Get</c> returns for a missing translation.
    /// </para>
    /// </summary>
    private void WriteArea(JsonWriter w, AreaInfo area, JsonSerializer s)
    {
        w.WriteStartObject();

        w.WritePropertyName("Name");
        var areaId = area.AreaId?.Value ?? string.Empty;
        w.WriteValue(Loc.SafeLoc(() => LocMan.GetHotspotTitle(areaId), string.Empty, _log, $"area title of '{areaId}'"));

        w.WritePropertyName("TaskDependencies");
        w.WriteValue(BuildTaskDependencies(area));

        MetaObjectWriter.WriteMembers(w, area, s, _log, (name, value) =>
        {
            if (TryWriteAreaReference(w, name, value, s)) return true;
            switch (name)
            {
                case "HotspotsRefs":
                    // The area's task list is expanded in place: every HotspotDef becomes the full
                    // hotspot object. (Inside a hotspot the same type stays a bare name — see
                    // WriteHotspot — which is what keeps the parent links from recursing forever.)
                    w.WritePropertyName(name);
                    w.WriteStartArray();
                    foreach (var def in (value as IEnumerable<HotspotDef>) ?? Array.Empty<HotspotDef>())
                        WriteResolvedHotspot(w, def, s);
                    w.WriteEndArray();
                    return true;

                case "UnlockingHotspotRef":
                    // Always null — on all 7 668 area rows of the 108-file corpus, including the
                    // areas whose member is a live HotspotDef with a key that resolves perfectly
                    // well. It is written rather than dropped, so it is not the null/reference rule
                    // either: the dump deliberately blanks the back-link (the unlocking task belongs
                    // to a DIFFERENT area, where it is already expanded in full, so following it
                    // would duplicate a whole task — and for a self-unlocking area, recurse).
                    w.WritePropertyName(name);
                    w.WriteNull();
                    return true;

                default:
                    return false;
            }
        }, MetaRefSkip.Unresolved);

        w.WriteEndObject();
    }

    /// <summary>
    /// Every <c>MetaRef&lt;AreaInfo&gt;</c> member is written even when it points at nothing, which
    /// is what makes them the exception to the file's "drop unresolved references" rule. Golden has
    /// four of them and all four behave the same way — <c>"AreaRef": null</c> on all 9 714 hotspots
    /// and <c>"AreaInfoOverride": null</c> on all but 6, <c>"UnlockInstructionAreaInfoRefs"</c> and
    /// <c>"NextAreaToUnlockRefs"</c> null on the areas that have no such link and the area key on
    /// the ones that do — while the sibling references of other types (<c>MapSpotRef</c>,
    /// <c>CustomHotspotTableInfoRef</c>, <c>TaskGroupRef</c>) simply disappear when they do not
    /// resolve. Returns false for anything that is not one, leaving it to the default rule.
    /// </summary>
    private static bool TryWriteAreaReference(JsonWriter w, string name, object? value, JsonSerializer s)
    {
        if (value is not IMetaRef) return false;
        var type = value.GetType();
        if (!type.IsGenericType || type.GetGenericArguments()[0] != typeof(AreaInfo)) return false;

        w.WritePropertyName(name);
        s.Serialize(w, value); // MetaRefConverter: the key, or null when there is none
        return true;
    }

    /// <summary>
    /// Writes a <see cref="HotspotDef"/> as the hotspot it points at. A reference that resolves to
    /// nothing (<c>HotspotId.None</c>, or a key the library doesn't have) becomes an EMPTY OBJECT,
    /// not a null: golden carries <c>"CompleteFocusHotspotRef": {}</c> on 9 684 of its 9 714
    /// hotspots.
    /// </summary>
    private void WriteResolvedHotspot(JsonWriter w, HotspotDef? def, JsonSerializer s)
    {
        var resolved = ResolveHotspot(def);
        if (resolved != null && _focusRefDepth < MaxFocusRefDepth)
        {
            _focusRefDepth++;
            try { WriteHotspot(w, resolved, s); }
            finally { _focusRefDepth--; }
            return;
        }

        if (resolved != null)
            _log.Warn($"AreaSerializer: CompleteFocusHotspotRef nesting deeper than {MaxFocusRefDepth}"
                      + $" at {def?.ConfigKey}; writing {{}} instead of expanding further (config cycle?)");

        w.WriteStartObject();
        w.WriteEndObject();
    }

    /// <summary>
    /// A hotspot's <c>CompleteFocusHotspotRef</c> is expanded in full, and that expanded hotspot has
    /// a <c>CompleteFocusHotspotRef</c> of its own — so the config, not the code, decides how deep
    /// the recursion goes. No chain in any dumped version is longer than one link (golden's deepest
    /// is a single expansion), which is why this guard cannot change today's output; it exists so a
    /// future config cycle produces a warning and a <c>{}</c> rather than a StackOverflow that takes
    /// the whole app down (an uncatchable crash in .NET).
    /// </summary>
    private const int MaxFocusRefDepth = 8;

    private int _focusRefDepth;

    /// <summary>
    /// <see cref="HotspotDef"/> -&gt; <see cref="HotspotDefinition"/>, treating <c>HotspotId.None</c>
    /// as "no hotspot". The library really does contain a <c>None</c> entry (an empty placeholder
    /// row), so a plain lookup would succeed and golden would then have to show that placeholder
    /// expanded — it never does.
    /// </summary>
    private HotspotDefinition? ResolveHotspot(HotspotDef? def)
    {
        if (def is null || def.ConfigKey == HotspotId.None) return null;
        if (_config.HotspotDefinitions == null) return null;
        return _config.HotspotDefinitions.TryGetValue(def.ConfigKey, out var hotspot) ? hotspot : null;
    }
}
