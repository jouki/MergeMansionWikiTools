using System.IO;
using System.Linq;
using System.Text;
using MergeMansionWikiTools.Dumper.Core;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Model;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

public class MetaObjectWriterTests
{
    private class Base { [MetaMember(2)] private int _hidden = 7; [MetaMember(9)] public string Late = "z"; }
    private class Derived : Base
    {
        [MetaMember(1)] public string First = "a";
        [MetaMember(5)] public string Boom => throw new System.InvalidOperationException("unresolved");
        [System.Runtime.Serialization.IgnoreDataMember] public string Ignored => "no";
        public string NotMeta = "no";
    }
    // Mirrors RewardItem: one member (NotNull) that's always present, one (Null) that's genuinely
    // C# null (should be omitted entirely — RewardItem.OverrideItemFeatures/OverridePoolTag in the
    // golden dump), and one (WrapperWithNullValue) that is a NON-null wrapper object whose own
    // string conversion renders as JSON null (should still be written as "WrapperWithNullValue":
    // null — RewardItem.MergeBoardId in 466/1782 golden entries, a non-null StringId whose inner
    // Value is null).
    [JsonConverter(typeof(NullValueWrapperConverter))]
    private class NullValueWrapper { }
    private class NullValueWrapperConverter : JsonConverter
    {
        public override bool CanConvert(System.Type objectType) => objectType == typeof(NullValueWrapper);
        public override bool CanRead => false;
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => writer.WriteNull();
        public override object? ReadJson(JsonReader reader, System.Type objectType, object? existingValue, JsonSerializer serializer)
            => throw new System.NotSupportedException();
    }
    private class HasNullMember
    {
        [MetaMember(1)] public string NotNull = "kept";
        [MetaMember(2)] public string? Null = null;
        [MetaMember(3)] public NullValueWrapper WrapperWithNullValue = new();
    }
    private sealed class NullLog : IDumpLog { public void Info(string m) {} public void Warn(string m) {} public void Trace(string m) {} public void Error(string m) {} }

    private static string Write(object o) => Write(o, MetaRefSkip.EmptyKey);

    /// <param name="hookFactory">
    /// Builds the per-member override hook once the JsonWriter exists, so a hook can write into the
    /// very writer the member would have been written to.
    /// </param>
    private static string Write(object o, MetaRefSkip refSkip,
        System.Func<JsonWriter, System.Func<string, object?, bool>>? hookFactory = null,
        params JsonConverter[] converters)
    {
        var sb = new StringBuilder();
        using var sw = new StringWriter(sb);
        using var jw = new JsonTextWriter(sw);
        // DumpJson.CreateSettings uses Formatting.Indented (the real dumper output format); align
        // the serializer to the raw JsonTextWriter's default Formatting.None here so this test's
        // plain-compact expectation isn't affected by that setting.
        var serializer = JsonSerializer.Create(DumpJson.CreateSettings(converters));
        serializer.Formatting = Formatting.None;
        MetaObjectWriter.WriteObject(jw, o, serializer, new NullLog(), hookFactory?.Invoke(jw), refSkip);
        return sb.ToString();
    }

    // ── MetaRef handling ────────────────────────────────────────────────────────────────────────

    /// <summary>Smallest IGameConfigData so a real MetaRef&lt;T&gt; can be built (never resolved).</summary>
    private sealed class RefTarget : IGameConfigData<string> { public string ConfigKey { get; init; } = ""; }

    /// <summary>A key object that renders as nothing — what a StringId with a null Value does.</summary>
    private sealed class EmptyKey { public override string? ToString() => null; }
    private sealed class EmptyKeyTarget : IGameConfigData<EmptyKey> { public EmptyKey ConfigKey { get; init; } = new(); }

    private class HasRefs
    {
        // Unresolved but carrying a real key — golden keeps this one in chain_item_odds.json.
        [MetaMember(1)] public MetaRef<RefTarget> Keyed = MetaRef<RefTarget>.FromKey("k1");
        // Non-null wrapper whose key renders empty — the CodexCategory / AreaRef shape.
        [MetaMember(2)] public MetaRef<EmptyKeyTarget> Empty = MetaRef<EmptyKeyTarget>.FromKey(new EmptyKey());
    }

    [Fact]
    public void EmptyKey_rule_drops_an_empty_reference_but_keeps_an_unresolved_keyed_one()
    {
        // chain_item_odds.json's rule: a MergeChainDefinition with no codex category ends at
        // FallbackChain (no "CodexCategory": null), while a reward under the IdealFTUEPhase1_B patch
        // still carries "EventInfoRef": "CBE_JoysOfTheSea2023" for an event that patch removed.
        Assert.Equal(
            "{\"Keyed\":\"k1\"}",
            Write(new HasRefs(), MetaRefSkip.EmptyKey, null, new MetaRefConverter()));
    }

    [Fact]
    public void Unresolved_rule_drops_the_keyed_reference_too()
    {
        // areas.json's rule: under the same patch a hotspot's MapSpotRef ("LayeredPetHome", a real
        // key that no longer resolves) disappears from the object entirely.
        Assert.Equal(
            "{}",
            Write(new HasRefs(), MetaRefSkip.Unresolved, null, new MetaRefConverter()));
    }

    [Fact]
    public void Override_hook_sees_a_reference_before_the_rule_can_drop_it()
    {
        // The ordering that lets areas.json keep "AreaRef": null on every hotspot: those members are
        // unresolved references the file's own rule would otherwise drop, and only the hook saves
        // them. Both members here would vanish under MetaRefSkip.Unresolved without it (see the
        // test above), so seeing them at all proves the hook runs first.
        var json = Write(new HasRefs(), MetaRefSkip.Unresolved, writer => (name, value) =>
        {
            if (value is not IMetaRef) return false;
            writer.WritePropertyName(name);
            writer.WriteValue("hooked");
            return true;
        }, new MetaRefConverter());

        Assert.Equal("{\"Keyed\":\"hooked\",\"Empty\":\"hooked\"}", json);
    }

    [Fact]
    public void Members_are_ordered_by_tag_and_include_private_base_members()
    {
        var names = MetaObjectWriter.Members(new Derived()).Select(m => m.Name).ToArray();
        Assert.Equal(new[] { "First", "_hidden", "Boom", "Late" }, names);
    }

    [Fact]
    public void Throwing_getter_is_skipped_and_non_meta_members_are_not_written()
    {
        var json = Write(new Derived());
        Assert.Equal("{\"First\":\"a\",\"_hidden\":7,\"Late\":\"z\"}", json);
    }

    [Fact]
    public void Null_member_is_omitted_but_non_null_wrapper_rendering_as_json_null_is_kept()
    {
        var json = Write(new HasNullMember());
        Assert.Equal("{\"NotNull\":\"kept\",\"WrapperWithNullValue\":null}", json);
    }
}
