using System.Globalization;
using MergeMansionWikiTools.Dumper.Core;
using Metaplay.Core.Math;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

public class MetaMathConverterTests
{
    private static string Serialize(object value, bool ceiling)
    {
        var settings = DumpJson.CreateSettings(new JsonConverter[] { new MetaMathConverter(ceiling) });
        return JsonConvert.SerializeObject(value, settings);
    }

    [Fact]
    public void Ceiling_true_rounds_f32_up_to_the_next_whole_number()
    {
        var f = F32.FromFloat(2.3f);
        Assert.Equal("3.0", Serialize(f, ceiling: true));
    }

    [Fact]
    public void Ceiling_false_writes_the_raw_double_for_f32()
    {
        var f = F32.FromFloat(2.3f);
        var json = Serialize(f, ceiling: false);
        Assert.Equal(f.Double, double.Parse(json, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Ceiling_true_rounds_f64_up_to_the_next_whole_number()
    {
        var f = F64.FromDouble(2.3);
        Assert.Equal("3.0", Serialize(f, ceiling: true));
    }

    [Fact]
    public void Ceiling_false_writes_the_raw_double_for_f64()
    {
        var f = F64.FromDouble(2.3);
        var json = Serialize(f, ceiling: false);
        Assert.Equal(f.Double, double.Parse(json, CultureInfo.InvariantCulture));
    }
}
