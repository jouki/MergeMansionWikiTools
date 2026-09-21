using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class PortraitUploadServiceTests
{
    [Fact]
    public void Local_file_name_maps_to_the_wiki_convention()
        => Assert.Equal("Julius Thinking.png", PortraitUploadService.WikiFileName("AntiqueDealer__Thinking.png", "Julius"));

    [Fact]
    public void Variant_goes_after_the_expression()
        => Assert.Equal("Maddie Surprised Winter2023.png",
            PortraitUploadService.WikiFileName("Maddie__Surprised.png", "Maddie", variant: "Winter2023"));

    [Fact]
    public void Files_already_on_the_wiki_are_skipped()
    {
        var plan = PortraitUploadService.PlanUploads(
            local: new Dictionary<string, string> { ["Julius Thinking.png"] = "c:/x/a.png", ["Julius Joyous.png"] = "c:/x/b.png" },
            existingOnWiki: new HashSet<string> { "Julius Thinking.png" });

        Assert.Single(plan);
        Assert.Equal("Julius Joyous.png", plan.Single().WikiName);
    }
}
