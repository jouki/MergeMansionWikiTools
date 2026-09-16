using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Writes an object exactly the way Newtonsoft's own default reflection would — its contract's
/// readable, non-ignored properties, in declaration order, nulls omitted — but member by member, so
/// a caller can take over individual members.
/// <para>
/// This is the counterpart to <see cref="MetaObjectWriter"/>, and the choice between the two is
/// per type, read off golden: <see cref="MetaObjectWriter"/> reproduces a <c>[MetaMember]</c> dump
/// (TagId order, private and inherited members included), this one reproduces plain reflection
/// (public properties only, declaration order, <c>[IgnoreDataMember]</c> honoured through
/// <see cref="IgnoreDataMemberContractResolver"/>). Two golden types need the plain shape *and* a
/// per-member exception, which is the only reason this exists rather than simply not registering a
/// converter: <c>CoreSupportEventInfo</c> in <c>events.json</c> (its <c>DisplayName</c> becomes the
/// resolved <c>Name</c> in place) and <c>CardCollectionEvidenceBoxInfo</c> in
/// <c>card_collection.json</c> (its <c>ItemDef</c> expands to the full item definition).
/// </para>
/// </summary>
public static class ContractObjectWriter
{
    /// <summary>
    /// Writes <paramref name="obj"/> as a JSON object; see <see cref="WriteMembers"/> for the rules.
    /// </summary>
    public static void WriteObject(JsonWriter w, object obj, JsonSerializer s, IDumpLog log,
        Func<string, object?, bool>? overrideMember = null)
    {
        w.WriteStartObject();
        WriteMembers(w, obj, s, log, overrideMember);
        w.WriteEndObject();
    }

    /// <summary>
    /// The property-writing half of <see cref="WriteObject"/>, without the surrounding
    /// <c>StartObject</c>/<c>EndObject</c>.
    /// <para>
    /// Order of operations per property: read the value, offer it to
    /// <paramref name="overrideMember"/>, then drop it if the getter threw or the value is
    /// <c>null</c>. The hook runs BEFORE both of those on purpose, so a member can be rewritten even
    /// when its own value is null or unreadable — that is how a core-support event with no
    /// <c>DisplayName</c> still gets <c>"Name": null</c>. A getter that throws and is NOT taken over
    /// by the hook is logged as a warning and the member skipped, never rethrown — the same policy as
    /// <see cref="MetaObjectWriter.WriteMembers"/>.
    /// </para>
    /// <para>
    /// <b>The hook ordering here is the OPPOSITE of <see cref="MetaObjectWriter.WriteMembers"/>'s</b>,
    /// and both orderings are golden-driven, so do NOT port a hook from one writer to the other
    /// without re-reading this: here the hook runs BEFORE the null check (it sees null members), in
    /// <see cref="MetaObjectWriter"/> it runs AFTER it (it never sees a null member, only a non-null
    /// one it may take over ahead of the <see cref="MetaRefSkip"/> rule).
    /// </para>
    /// </summary>
    public static void WriteMembers(JsonWriter w, object obj, JsonSerializer s, IDumpLog log,
        Func<string, object?, bool>? overrideMember = null)
    {
        // Only object contracts have a property list. A caller that points this at a collection or
        // dictionary type has made a mistake that would otherwise surface as a bare InvalidCastException.
        if (s.ContractResolver.ResolveContract(obj.GetType()) is not JsonObjectContract contract)
            throw new InvalidOperationException(
                $"{nameof(ContractObjectWriter)} needs an object contract; {obj.GetType().Name} does not have one.");

        foreach (var property in contract.Properties)
        {
            if (property.Ignored || property.Readable == false) continue;
            var name = property.PropertyName!;

            object? value = null;
            Exception? failure = null;
            try
            {
                value = property.ValueProvider?.GetValue(obj);
            }
            catch (Exception ex)
            {
                failure = ex; // reported below, only if the hook does not take the member over
            }

            // The hook gets first refusal on EVERY member — including one whose getter threw, since
            // a hook that replaces a member outright (DisplayName -> the resolved Name) does not
            // need the original value at all.
            if (overrideMember != null && overrideMember(name, value)) continue; // hook wrote it itself

            if (failure != null)
            {
                log.Warn($"{obj.GetType().Name}.{name}: {failure.GetType().Name} — {failure.Message} — skipped");
                continue;
            }

            if (value is null) continue; // NullValueHandling.Ignore, applied by hand on this manual path

            w.WritePropertyName(name);
            s.Serialize(w, value);
        }
    }
}
