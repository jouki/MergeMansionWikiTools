using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// The Decay Odds section belongs to any roll with SEVERAL outcomes; which layout it gets is the
/// Lua's business (v0.24.84): a variant set renders as A/B/C columns of one item, a cross-item roll
/// as one column per target, like Drop Odds.
/// <para>
/// History: v0.24.77 limited the section to single-page variant sets, because
/// <c>GetItemDecayOddsTableFromChainName</c> only knew the variant layout and rendered Lady Voyance's
/// 50/50 into <i>Investigation</i> + <i>Seance</i> as one lonely "50 %" column pointing at the page
/// itself. With the Lua taught the cross-item layout, that restriction would only hide real data —
/// Unfortunate Events L9 (60% Voyance's Black Cat / 40% Murder Weapon) needs it.
/// </para>
/// </summary>
public class DecayOddsVariantSetTests
{
    /// <summary>Mapping that both flags the targets as variants and pins the page each resolves to.</summary>
    private static WikiMappingCache Mapping(params (string ItemType, string Page, bool IsVariant)[] rows)
    {
        var cache = new WikiMappingCache();
        foreach (var (itemType, page, isVariant) in rows)
        {
            var fields = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["chainName"] = page,
            };
            if (isVariant) fields["isVariant"] = true;
            cache.Mappings[itemType] = new WikiMappingEntry { Fields = fields };
        }
        return cache;
    }

    private static ParsedChain Roller(string display, Dictionary<string, double> odds) => new()
    {
        ConfigKey = "Roller",
        DisplayName = display,
        Items = new List<ParsedItem>
        {
            new()
            {
                ItemType = "Roller_01",
                Name = display,
                Level = 1,
                Description = "d",
                DecayIntoOdds = odds,
            },
        },
    };

    private static string? Section(ParsedChain chain, WikiMappingCache mapping) =>
        new WikiTableGenerator(new DataService(new ChainNameService()), mapping)
            .GenerateDecayOddsSection(chain);

    [Fact]
    public void TargetsOnOnePage_areAVariantSet_soTheSectionIsEmitted()
    {
        var chain = Roller("Flower Bed (Green Acres Quest)", new Dictionary<string, double>
        {
            ["FlowerBedA_06"] = 65, ["FlowerBedB_06"] = 30, ["FlowerBedC_06"] = 5,
        });
        var mapping = Mapping(
            ("FlowerBedA_06", "Flower Bed (Green Acres Quest)", true),
            ("FlowerBedB_06", "Flower Bed (Green Acres Quest)", true),
            ("FlowerBedC_06", "Flower Bed (Green Acres Quest)", true));

        var section = Section(chain, mapping);

        Assert.NotNull(section);
        Assert.Contains("=== Decay Odds ===", section);
        Assert.Contains("GetItemDecayOddsTableFromChainName", section);
    }

    [Fact]
    public void TargetsSpreadOverSeveralPages_alsoGetTheSection()
    {
        // Unfortunate Events L9: the roll leaves this page entirely, and those odds are exactly what
        // the reader needs. The Lua renders a column per target.
        var chain = Roller("Unfortunate Events", new Dictionary<string, double>
        {
            ["Cat_01"] = 60, ["MWPoison_01"] = 40,
        });
        var mapping = Mapping(
            ("Cat_01", "Voyance's Black Cat", false),
            ("MWPoison_01", "Murder Weapon", false));

        Assert.NotNull(Section(chain, mapping));
    }

    [Fact]
    public void NoVariantFlagAnywhere_isStillASection_becauseTheOddsAreReal()
    {
        var chain = Roller("Something", new Dictionary<string, double>
        {
            ["Plain_01"] = 50, ["Plain_02"] = 50,
        });
        var mapping = Mapping(("Plain_01", "Something Else", false), ("Plain_02", "Something Else", false));

        Assert.NotNull(Section(chain, mapping));
    }

    [Fact]
    public void ASingleOutcome_getsNoSection()
    {
        // One certain outcome is not a roll — a one-column 100% table says nothing.
        var chain = Roller("Certain", new Dictionary<string, double> { ["Only_01"] = 100 });
        var mapping = Mapping(("Only_01", "Only Thing", true));

        Assert.Null(Section(chain, mapping));
    }

    [Fact]
    public void NoDecayRollAtAll_getsNoSection()
    {
        var chain = new ParsedChain
        {
            ConfigKey = "Plain",
            DisplayName = "Plain",
            Items = new List<ParsedItem> { new() { ItemType = "Plain_01", Name = "Plain", Level = 1, Description = "d" } },
        };

        Assert.Null(Section(chain, new WikiMappingCache()));
    }

    [Fact]
    public void ARollOnATransientStage_isHeadedTransformOdds()
    {
        // The player fuels the locked location and it becomes the Investigation or the Seance. The
        // decay is plumbing inside a 3-second stage, so "Decay Odds" would name the mechanism rather
        // than the move (user report, 2026-09-18).
        var chain = Roller("Location: The Mansion", new Dictionary<string, double>
        {
            ["SinkIA_01"] = 50, ["SinkSB_01"] = 50,
        });
        chain.Items[0].IsTransient = true;
        var mapping = Mapping(("SinkIA_01", "Investigation: The Mansion", false),
                              ("SinkSB_01", "Seance: The Mansion", false));

        var section = Section(chain, mapping);

        Assert.NotNull(section);
        Assert.Contains("=== Transform Odds ===", section);
        Assert.DoesNotContain("=== Decay Odds ===", section);
    }

    [Fact]
    public void AnOrdinaryTimedRoll_keepsTheDecayOddsHeading()
    {
        // Unfortunate Events L9 really does decay on its own once spent — nothing transforms it.
        var chain = Roller("Unfortunate Events", new Dictionary<string, double>
        {
            ["Cat_01"] = 60, ["MWPoison_01"] = 40,
        });
        var mapping = Mapping(("Cat_01", "Voyance's Black Cat", false), ("MWPoison_01", "Murder Weapon", false));

        Assert.Contains("=== Decay Odds ===", Section(chain, mapping));
    }

    [Fact]
    public void AFoldedRollsPercentage_isAnInvoke_notABakedInNumber()
    {
        // The odds table on the same page reads live data, so a hardcoded "50%" in the Merge Stages
        // cell would disagree with it after any rebalance (user report, 2026-09-18).
        var chain = Roller("Location: The Garden", new Dictionary<string, double>
        {
            ["SinkIA_01"] = 50, ["SinkSB_01"] = 50,
        });
        chain.Items[0].IsTransient = true;

        var formatted = Services.TransientFold.Format("Investigation: The Garden", 1, 50, null, 1);

        // The source chain is left out on purpose -- the Lua defaults it to the page it renders on.
        Assert.Contains("{{#Invoke:Items|GetTransformOddsFromChainName|1|Investigation: The Garden|1}}", formatted);
        // Bound to the item with &nbsp; so the narrow column cannot break the line between them.
        Assert.Contains("}}&nbsp;{{Item|", formatted);
        Assert.DoesNotContain("50%", formatted);
        Assert.Contains("{{Item|Investigation: The Garden|1}}", formatted);
    }

    [Fact]
    public void WithoutASourceLevel_theLiteralPercentageIsKept()
    {
        // Callers that cannot name their row must still render something truthful.
        var formatted = Services.TransientFold.Format("Investigation: The Garden", 1, 50);

        Assert.Contains("50%&nbsp;{{Item|", formatted);
        Assert.DoesNotContain("#Invoke", formatted);
    }

    [Fact]
    public void ACertainOutcome_getsNoPercentageAtAll()
    {
        var formatted = Services.TransientFold.Format("Investigation: The Garden", 1, null, null, 1);

        Assert.Equal("{{Item|Investigation: The Garden|1}}", formatted);
    }
}
