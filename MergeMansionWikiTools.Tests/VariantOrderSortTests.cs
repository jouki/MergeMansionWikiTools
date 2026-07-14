using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class VariantOrderSortTests
{
    private static ParsedItem It(string id, int? order) =>
        new() { ItemType = id, MappingVariantOrder = order };

    [Fact]
    public void SortVariants_OrdersByVariantOrder_ThenUnorderedByItemType()
    {
        var items = new[] { It("C", 2), It("A", null), It("B", 1), It("D", null) };
        var sorted = WikiTableGenerator.SortVariants(items).Select(i => i.ItemType).ToList();
        // ordered (B=1, C=2) first, then unordered A/D by ItemType
        Assert.Equal(new[] { "B", "C", "A", "D" }, sorted);
    }

    [Fact]
    public void SortVariants_AllUnordered_FallsBackToItemType()
    {
        var items = new[] { It("Z", null), It("A", null) };
        var sorted = WikiTableGenerator.SortVariants(items).Select(i => i.ItemType).ToList();
        Assert.Equal(new[] { "A", "Z" }, sorted);
    }
}
