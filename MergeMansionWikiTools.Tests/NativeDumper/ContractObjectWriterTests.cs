using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// <see cref="ContractObjectWriter"/> has to reproduce Newtonsoft's own default reflection exactly —
/// public properties, declaration order, <c>[IgnoreDataMember]</c> honoured, nulls dropped — while
/// still letting a caller take one member over. Two golden types depend on both halves at once
/// (<c>CoreSupportEventInfo.DisplayName</c> becoming <c>Name</c>,
/// <c>CardCollectionEvidenceBoxInfo.ItemDef</c> expanding), so both are tested here.
/// </summary>
public class ContractObjectWriterTests
{
    private sealed class NullLog : IDumpLog { public void Info(string m) {} public void Warn(string m) {} public void Trace(string m) {} public void Error(string m) {} }

    private class Sample
    {
        public string First { get; set; } = "a";
        public string? Missing { get; set; }
        [IgnoreDataMember] public string Ignored => "no";
        public string Boom => throw new InvalidOperationException("unresolved");
        public string Last { get; set; } = "z";
    }

    private static string Write(object o, Func<JsonWriter, Func<string, object?, bool>>? hookFactory = null)
    {
        var sb = new StringBuilder();
        using var sw = new StringWriter(sb);
        using var jw = new JsonTextWriter(sw);
        var serializer = JsonSerializer.Create(DumpJson.CreateSettings(Array.Empty<JsonConverter>()));
        serializer.Formatting = Formatting.None;
        ContractObjectWriter.WriteObject(jw, o, serializer, new NullLog(), hookFactory?.Invoke(jw));
        return sb.ToString();
    }

    [Fact]
    public void Writes_public_properties_in_declaration_order_skipping_ignored_null_and_throwing_ones()
    {
        // Declaration order, not alphabetical; Missing is null (NullValueHandling.Ignore applied by
        // hand on this manual path), Ignored carries [IgnoreDataMember], Boom's getter throws.
        Assert.Equal("{\"First\":\"a\",\"Last\":\"z\"}", Write(new Sample()));
    }

    [Fact]
    public void Hook_can_replace_a_member_in_place_keeping_its_position()
    {
        // The CoreSupportEventInfo shape: DisplayName is written as "Name" where DisplayName sat.
        var json = Write(new Sample(), w => (member, _) =>
        {
            if (member != "First") return false;
            w.WritePropertyName("Renamed");
            w.WriteValue(1);
            return true;
        });
        Assert.Equal("{\"Renamed\":1,\"Last\":\"z\"}", json);
    }

    [Fact]
    public void Hook_runs_before_the_null_check_so_it_can_write_a_member_whose_value_is_null()
    {
        // Without this ordering a core-support event with no DisplayName would silently lose its
        // "Name" key instead of getting "Name": null.
        var json = Write(new Sample(), w => (member, value) =>
        {
            if (member != "Missing") return false;
            Assert.Null(value);
            w.WritePropertyName("Name");
            w.WriteNull();
            return true;
        });
        Assert.Equal("{\"First\":\"a\",\"Name\":null,\"Last\":\"z\"}", json);
    }

    [Fact]
    public void Hook_also_gets_a_member_whose_getter_threw_and_can_still_write_it()
    {
        // A hook that replaces a member outright does not need the original value, so a throwing
        // getter must not silently cost the replacement its slot. Unhandled, the same member is
        // logged and skipped (that is the first test above, where Boom is absent).
        var json = Write(new Sample(), w => (member, value) =>
        {
            if (member != "Boom") return false;
            Assert.Null(value);
            w.WritePropertyName("Replaced");
            w.WriteValue("ok");
            return true;
        });
        Assert.Equal("{\"First\":\"a\",\"Replaced\":\"ok\",\"Last\":\"z\"}", json);
    }
}
