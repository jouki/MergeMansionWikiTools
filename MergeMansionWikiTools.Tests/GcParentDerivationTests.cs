using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class GcParentDerivationTests
{
    [Fact]
    public void DeriveParent_stripsSuffix_andLeavesNonGcUnchanged()
    {
        Assert.Equal("Legacy Lane", GarageCleanupNameService.DeriveParent("Legacy Lane Garage Cleanup"));
        Assert.Equal("Some Other Event", GarageCleanupNameService.DeriveParent("Some Other Event"));
    }
}
