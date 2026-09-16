using System.Reflection;
using Metaplay.Core;
using Metaplay.Core.Model;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// When a <see cref="IMetaRef"/> member is dropped rather than written. Both rules are golden-driven
/// and they genuinely differ per dump file, so the caller picks:
/// <list type="bullet">
/// <item><description><see cref="EmptyKey"/> — drop only a reference whose key renders empty, keep an
/// unresolved one with a real key. <c>chain_item_odds.json</c>: a chain without a codex category ends
/// at <c>FallbackChain</c>, while a reward under the <c>IdealFTUEPhase1_B</c> patch keeps
/// <c>"EventInfoRef": "CBE_JoysOfTheSea2023"</c> for an event that patch removed.</description></item>
/// <item><description><see cref="Unresolved"/> — drop anything that does not resolve, empty key or
/// not. <c>areas.json</c>: under the same patch a hotspot's <c>MapSpotRef</c> ("LayeredPetHome",
/// a real key that no longer resolves) disappears from the object entirely, and
/// <c>CustomHotspotTableInfoRef</c> is absent on all but the 3 hotspots that really have
/// one.</description></item>
/// </list>
/// Either way the decision is made AFTER the caller's member hook, so a member the caller writes
/// itself is never dropped by this rule (that is how the area dump keeps its
/// <c>"AreaRef": null</c>).
/// </summary>
public enum MetaRefSkip
{
    /// <summary>Drop a reference only when its key renders empty.</summary>
    EmptyKey,

    /// <summary>Drop any reference that does not resolve.</summary>
    Unresolved,
}

/// <summary>
/// Reflection-driven writer for Metaplay <c>[MetaMember]</c>-tagged objects: enumerates the
/// tagged fields/properties (including private and inherited ones) ordered by <c>TagId</c>, and
/// writes them out as a flat JSON object. This is the shared imperative-write path every native
/// dumper uses instead of relying on Newtonsoft's automatic reflection, which cannot honour the
/// TagId ordering, cannot see private/base-class members, and offers no per-dumper member
/// overrides.
/// <para>
/// Null policy, in the order the checks run: a true C# <c>null</c> member is always skipped
/// (matching <c>NullValueHandling.Ignore</c>, which does not reach this manual write path); a
/// non-null wrapper whose own conversion renders as JSON <c>null</c> — a <c>StringId</c> with a
/// null inner <c>Value</c>, say — keeps its key and gets a <c>null</c> value; an
/// <see cref="IMetaRef"/> is dropped according to the caller's <see cref="MetaRefSkip"/> rule, and
/// anything the rule keeps is written as its key — resolved or not (see
/// <see cref="MetaRefConverter"/>).
/// </para>
/// </summary>
public static class MetaObjectWriter
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Returns the <c>[MetaMember]</c>-tagged members of <paramref name="obj"/>'s type and all
    /// its base types, ordered by TagId. Private and base-class members are included. Value
    /// access is lazy (wrapped in a <see cref="Func{TResult}"/>) so callers can catch getter
    /// exceptions per member. When a derived type declares a member with the same name as one
    /// further up the hierarchy, only the most-derived declaration is kept.
    /// </summary>
    public static IReadOnlyList<(string Name, int TagId, Func<object?> Get)> Members(object obj)
    {
        var list = new List<(string Name, int TagId, Func<object?> Get)>();
        var seenNames = new HashSet<string>();

        for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(Flags))
            {
                var a = f.GetCustomAttribute<MetaMemberAttribute>();
                if (a == null) continue;
                if (!seenNames.Add(f.Name)) continue; // a more-derived declaration already won
                list.Add((f.Name, a.TagId, () => f.GetValue(obj)));
            }
            foreach (var p in t.GetProperties(Flags))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                if (!p.CanRead) continue;
                var a = p.GetCustomAttribute<MetaMemberAttribute>();
                if (a == null) continue;
                if (!seenNames.Add(p.Name)) continue;
                list.Add((p.Name, a.TagId, () => p.GetValue(obj)));
            }
        }

        return list.OrderBy(m => m.TagId).ToList();
    }

    /// <summary>
    /// Reads a single <c>[MetaMember]</c> by name — the one place that does so, because several of
    /// the members the dumpers need (a hotspot's <c>CardStackRef</c>, a requirement's
    /// <c>ItemRefs</c>, a dialogue action's <c>DialogueId</c>) are PRIVATE and only reachable
    /// through <see cref="Members"/>. Exception policy matches <see cref="WriteMembers"/>: a getter
    /// that throws is logged as a warning and reported as <c>null</c>, never rethrown — a derived
    /// getter dereferencing an unresolved reference must not abort the dump. An unknown name is also
    /// <c>null</c> (no exception), so a member the game later removes degrades rather than crashes.
    /// </summary>
    public static object? GetMember(object obj, string name, IDumpLog log)
    {
        foreach (var (memberName, _, get) in Members(obj))
        {
            if (memberName != name) continue;
            try
            {
                return get();
            }
            catch (Exception ex)
            {
                log.Warn($"{obj.GetType().Name}.{name}: {ex.GetType().Name} — {ex.Message} — treated as null");
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Writes <paramref name="obj"/> as a JSON object: members in TagId order, true C# <c>null</c>
    /// values skipped entirely (matching the shared <c>NullValueHandling.Ignore</c> serializer
    /// settings, which don't otherwise apply here since this writes the property name manually
    /// rather than through Newtonsoft's own reflection — confirmed against golden:
    /// <c>RewardItem.OverrideItemFeatures</c>/<c>OverridePoolTag</c> never appear when null, while a
    /// non-null wrapper whose own conversion happens to render as JSON <c>null</c>, such as an empty
    /// <c>MergeBoardId</c>, still gets its key written with a <c>null</c> value), references dropped
    /// per <paramref name="refSkip"/>, getter exceptions caught and logged via <paramref name="log"/>
    /// (member skipped, dump continues), and an optional per-member hook that can take over writing a
    /// given member itself (return true to suppress the default write).
    /// <para>
    /// <b>The hook runs AFTER the null check and BEFORE the reference rule</b> — it therefore never
    /// sees a null member, only a non-null one it may claim ahead of <see cref="MetaRefSkip"/>. That
    /// is the OPPOSITE of <see cref="ContractObjectWriter.WriteMembers"/>, whose hook runs before its
    /// null check; both orderings are golden-driven, so a hook must not be moved between the two
    /// writers unchanged.
    /// </para>
    /// </summary>
    /// <param name="refSkip">Which <see cref="IMetaRef"/> members to drop — see <see cref="MetaRefSkip"/>.</param>
    public static void WriteObject(JsonWriter w, object obj, JsonSerializer s, IDumpLog log, Func<string, object?, bool>? overrideMember = null, MetaRefSkip refSkip = MetaRefSkip.EmptyKey)
    {
        w.WriteStartObject();
        WriteMembers(w, obj, s, log, overrideMember, refSkip);
        w.WriteEndObject();
    }

    /// <summary>
    /// The property-writing half of <see cref="WriteObject"/>, without the surrounding
    /// <c>StartObject</c>/<c>EndObject</c>. Callers that need to emit their own members *before* the
    /// TagId-ordered ones (the chain dumper writes localized <c>Name</c>/<c>Description</c> and
    /// several computed values ahead of the reflected block) open the object themselves, write their
    /// prefix, call this, and close the object.
    /// </summary>
    /// <param name="refSkip">Which <see cref="IMetaRef"/> members to drop — see <see cref="MetaRefSkip"/>.</param>
    public static void WriteMembers(JsonWriter w, object obj, JsonSerializer s, IDumpLog log, Func<string, object?, bool>? overrideMember = null, MetaRefSkip refSkip = MetaRefSkip.EmptyKey)
    {
        foreach (var (name, _, get) in Members(obj))
        {
            object? value;
            try
            {
                value = get();
            }
            catch (Exception ex)
            {
                log.Warn($"{obj.GetType().Name}.{name}: {ex.GetType().Name} — {ex.Message} — skipped");
                continue;
            }

            if (value is null) continue; // true C# null: matches NullValueHandling.Ignore, which a
                                          // manual WritePropertyName+Serialize call bypasses (that
                                          // setting only applies to Newtonsoft's own automatic
                                          // property-writing, not an explicit write like this one).
                                          // A non-null wrapper whose own string conversion happens
                                          // to be null (e.g. a StringId with a null inner Value,
                                          // like an empty MergeBoardId) is NOT skipped here — the
                                          // member itself is still written, with that null value.
            // The hook gets first refusal on every non-null member, BEFORE the reference rule
            // below: the area dump has to keep "AreaRef": null / "AreaInfoOverride": null, and those
            // are unresolved references its rule would otherwise drop.
            if (overrideMember != null && overrideMember(name, value)) continue; // hook wrote the member itself

            // A reference the rule rejects is dropped like a plain null; anything else is written as
            // its key, resolved or not — see MetaRefConverter and MetaRefSkip.
            if (value is IMetaRef metaRef && (refSkip == MetaRefSkip.Unresolved
                    ? !metaRef.IsResolved
                    : metaRef.KeyObject is null || metaRef.KeyObject.ToString() is null))
                continue;

            w.WritePropertyName(name);
            s.Serialize(w, value);
        }
    }
}
