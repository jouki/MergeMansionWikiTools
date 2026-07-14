using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class MappingFieldParseTests
{
    private static WikiMappingEntry Entry(params (string, object?)[] fields)
    {
        var e = new WikiMappingEntry();
        foreach (var (k, v) in fields) e.Fields[k] = v;
        return e;
    }

    [Fact]
    public void VariantOrder_ParsesNumericField()
    {
        Assert.Equal(3, Entry(("variantOrder", 3.0)).VariantOrder);
        Assert.Null(Entry(("chainName", "X")).VariantOrder);
    }

    [Fact]
    public void GroupOdds_ParsesBoolField()
    {
        Assert.True(Entry(("groupOdds", true)).GroupOdds);
        Assert.False(Entry(("chainName", "X")).GroupOdds);
    }
}
