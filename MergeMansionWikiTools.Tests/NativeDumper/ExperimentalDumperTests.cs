using System;
using System.Collections.Generic;
using MergeMansionWikiTools.Dumper.Dumpers;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// The <c>SharedGlobals</c> section keys its entries <c>"Name (TypeName)"</c>. The type half is the
/// raw CLR name, which is easy to "helpfully" prettify and thereby break every key of a generic
/// property — 20 of the 64 golden keys end in <c>List`1</c>.
/// </summary>
public class ExperimentalDumperTests
{
    [Theory]
    [InlineData("DefaultActivationCost", typeof(int), "DefaultActivationCost (Int32)")]
    [InlineData("ForcePlayerName", typeof(bool), "ForcePlayerName (Boolean)")]
    [InlineData("MergeMansionURL", typeof(string), "MergeMansionURL (String)")]
    [InlineData("MergeHintWaitTimeMilliseconds", typeof(long), "MergeHintWaitTimeMilliseconds (Int64)")]
    public void GlobalsKey_uses_the_clr_type_name(string name, Type type, string expected)
        => Assert.Equal(expected, ExperimentalDumper.GlobalsKey(name, type));

    [Fact]
    public void GlobalsKey_keeps_the_backtick_arity_of_a_generic_type()
    {
        // Golden: "ItemSellPrices (List`1)", "AdsSoftLaunchSegments (List`1)" — NOT "List<Int32>".
        Assert.Equal("ItemSellPrices (List`1)", ExperimentalDumper.GlobalsKey("ItemSellPrices", typeof(List<int>)));
        Assert.Equal("AdsSoftLaunchSegments (List`1)", ExperimentalDumper.GlobalsKey("AdsSoftLaunchSegments", typeof(List<string>)));
    }
}
