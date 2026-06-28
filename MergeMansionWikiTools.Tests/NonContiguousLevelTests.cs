using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Page generation must use a chain's ACTUAL item levels, never an assumed contiguous 1..maxLevel
/// range. Regression for "Cherished Item With Label" (Attic_CherishedItemLabel): a single item at
/// game level 4 (no level 1). Item Descriptions must emit only level 4; the infobox intro subject
/// must pin level 4 so the image resolves.
/// </summary>
public class NonContiguousLevelTests
{
    private static WikiTableGenerator NewGen() => new(new DataService(new ChainNameService()));

    private static ParsedChain SingleLevel4() => new()
    {
        DisplayName = "Cherished Item With Label",
        Items = { new ParsedItem { ItemType = "Attic_CherishedItemLabel_04", Level = 4, Description = "A label." } },
    };

    [Fact]
    public void ItemDescriptions_SingleLevel4_OnlyEmitsLevel4()
    {
        var section = NewGen().GenerateItemDescriptionsSection(SingleLevel4());

        Assert.NotNull(section);
        Assert.Contains("{{Item/Icon|{{PAGENAME}}|4}}", section);
        Assert.Contains("GetItemDescFromChainName|4", section);
        // Must NOT fabricate levels the chain doesn't have.
        Assert.DoesNotContain("|1}}", section);
        Assert.DoesNotContain("|2}}", section);
        Assert.DoesNotContain("|3}}", section);
    }

    [Fact]
    public void ItemDescriptions_MultiLevelFrom1_EmitsEachActualLevel()
    {
        var chain = new ParsedChain
        {
            DisplayName = "Detergent",
            Items =
            {
                new ParsedItem { ItemType = "Detergent_01", Level = 1, Description = "d1" },
                new ParsedItem { ItemType = "Detergent_02", Level = 2, Description = "d2" },
            },
        };

        var section = NewGen().GenerateItemDescriptionsSection(chain);

        Assert.Contains("GetItemDescFromChainName|1", section);
        Assert.Contains("GetItemDescFromChainName|2", section);
        Assert.DoesNotContain("GetItemDescFromChainName|3", section);
    }

    [Fact]
    public void InfoboxIntro_SingleLevel4_PinsLevel4OnSubject()
    {
        var intro = NewGen().GenerateInfoboxSectionIntro(SingleLevel4(), null, null, null);

        Assert.NotNull(intro);
        Assert.Contains("{{Item|{{PAGENAME}}|4}}", intro);
        Assert.Contains("It is", intro);
    }

    [Fact]
    public void InfoboxIntro_SingleLevel1_NoRedundantLevelSuffix()
    {
        var chain = new ParsedChain
        {
            DisplayName = "Plain Box",
            Items = { new ParsedItem { ItemType = "PlainBox_01", Level = 1, Description = "b" } },
        };

        var intro = NewGen().GenerateInfoboxSectionIntro(chain, null, null, null);

        Assert.Contains("{{Item|{{PAGENAME}}}}", intro);
        Assert.DoesNotContain("{{Item|{{PAGENAME}}|1}}", intro);
    }
}
