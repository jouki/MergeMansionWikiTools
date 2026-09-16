using System.Collections.Generic;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Dumpers;
using MergeMansionWikiTools.Dumper.Serializers;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// <c>Percent</c> is the one computed number in <c>card_collection.json</c> whose formula is not
/// visible in the golden bytes (weight and total are both there, but the rounding is not), so it
/// gets its own tests: normalized share × 100, rounded to 4 decimals.
/// </summary>
public class CardCollectionDumperTests
{
    [Theory]
    // The golden PackActivations weights: 45/25/16/10/4 out of 100 come out as round numbers…
    [InlineData(45.0, 100.0, 45.0)]
    [InlineData(4.0, 100.0, 4.0)]
    // …but a non-100 total is where the 4-decimal rounding shows.
    [InlineData(1.0, 3.0, 33.3333)]
    [InlineData(2.0, 3.0, 66.6667)]
    [InlineData(1.0, 7.0, 14.2857)]
    public void Percent_is_the_normalized_share_rounded_to_four_decimals(double weight, double total, double expected)
        => Assert.Equal(expected, CardCollectionDumper.Percent(weight, total));

    [Fact]
    public void Percent_of_an_all_zero_weight_list_is_zero_not_NaN()
    {
        // Dividing by a zero total would give NaN, which Newtonsoft cannot even write.
        Assert.Equal(0.0, CardCollectionDumper.Percent(0.0, 0.0));
        Assert.Equal(0.0, CardCollectionDumper.Percent(5.0, 0.0));
    }

    [Fact]
    public void Percent_normalizes_before_scaling_so_a_full_roll_sums_to_100()
    {
        var weights = new List<double> { 45.0, 25.0, 16.0, 10.0, 4.0 };
        double total = 0;
        foreach (var w in weights) total += w;

        double sum = 0;
        foreach (var w in weights) sum += CardCollectionDumper.Percent(w, total);

        // Tolerance, not exact equality: the summands are themselves 4-decimal roundings, so the
        // total is only guaranteed to 100 within floating-point slack.
        Assert.Equal(100.0, sum, 6);
    }

    /// <summary>
    /// <c>ItemDef</c> key 0 is the <c>Items</c> library's blank "None" row: it RESOLVES, so the
    /// sentinel has to be checked before the lookup or 1 395 of the 1 397 golden cards would carry a
    /// full, meaningless item instead of <c>{}</c>.
    /// </summary>
    [Fact]
    public void Expand_writes_an_empty_object_for_the_no_item_sentinel_key()
    {
        // A null ItemDef member is a different thing and stays null.
        Assert.Null(CardCollectionSerializer.Expand(null, null));

        // Key 0 => {} without ever touching the config (which is why null config is safe here).
        var none = CardCollectionSerializer.Expand(null, new ItemDef(0) { ConfigKey = 0 });
        Assert.NotNull(none);
        Assert.Equal("{}", JsonConvert.SerializeObject(none));

        // An unresolvable non-zero key is the same empty object, never null and never a placeholder.
        var missing = CardCollectionSerializer.Expand(null, new ItemDef(0) { ConfigKey = 4711 });
        Assert.Equal("{}", JsonConvert.SerializeObject(missing));

        // Fresh instance per call — the value escapes into caller-owned structures.
        Assert.NotSame(none, missing);
    }
}
