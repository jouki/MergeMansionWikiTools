using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// The infobox's <c>transforms_to</c> must never claim an item turns into itself.
/// <para>
/// Reported on Seance: Lady Voyance's House, whose infobox listed both "(L1)" and "(L2)": the FTUE
/// alias pair (<c>VoyanceHouseSinkSFTUE_01</c> → <c>VoyanceHouseActiveSFTUE_01</c>) carries no
/// <c>level</c> override in Items/Mapping, so BOTH sides fell back to the level in their item id
/// (<c>_01</c> = 1) — while the non-FTUE twins are mapped 1 → 2. The alias sink therefore
/// "transformed" into its own chain and level.
/// </para>
/// <para>
/// The alias having no level is INTENDED, not a gap to fix: the game scripts an event's opening with
/// FTUE item types and swaps them for the ordinary ones once the real mechanics begin, and the player
/// sees one continuous item. Giving those types levels would invent pseudo-items, so the infobox just
/// drops the row — silently for aliases, with a warning for anything else.
/// </para>
/// <para>
/// Level equality alone is not the test: a genuine cross-chain transform to another chain's L1 is
/// ordinary (Bigger Pile of Seed Bags → Golden Seed). Only same chain AND same level is impossible.
/// </para>
/// </summary>
public class InfoboxSelfTransformTests
{
    private static ParsedItem Sink(string type, int level, string rewardType, bool isAlias = false) => new()
    {
        ItemType = type,
        Name = type,
        Level = level,
        Description = "d",
        IsSink = true,
        SinkRewardItemType = rewardType,
        IsAlias = isAlias,
    };

    private static ParsedItem Plain(string type, int level, bool isAlias = false) => new()
    {
        ItemType = type,
        Name = type,
        Level = level,
        Description = "d",
        IsAlias = isAlias,
    };

    private static ParsedChain Chain(string key, string display, params ParsedItem[] items) => new()
    {
        ConfigKey = key,
        DisplayName = display,
        Items = new List<ParsedItem>(items),
    };

    /// <summary>Runs the generator with no DataService, so levels come straight from the fixture.</summary>
    private static (string Lua, List<string> Warnings) Generate(ParsedChain chain, params ParsedChain[] others)
    {
        var svc = new InfoboxGeneratorService();
        var all = new List<ParsedChain> { chain };
        all.AddRange(others);
        var lua = svc.Generate(chain, all, new Dictionary<string, string>(),
            new InfoboxGeneratorOptions(), new List<string>());
        return (lua, svc.Warnings);
    }

    [Fact]
    public void OrdinaryFueledTransform_isListed()
    {
        var chain = Chain("Seance", "Seance: Lady Voyance's House",
            Sink("SinkSA_01", 1, "ActiveSA_01"),
            Plain("ActiveSA_01", 2));

        var (lua, warnings) = Generate(chain);

        Assert.Contains("{{Item|Seance: Lady Voyance's House|2}}", lua);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AliasWhoseRewardResolvesToItsOwnLevel_isDroppedSILENTLY()
    {
        // The FTUE pair: both sides fall back to level 1, so the transform points at itself. An FTUE
        // alias having no level of its own is by design — the game swaps the scripted intro item for
        // the ordinary one and the player never sees a difference — so this is not worth a warning.
        var chain = Chain("Seance", "Seance: Lady Voyance's House",
            Sink("SinkSFTUE_01", 1, "ActiveSFTUE_01", isAlias: true),
            Plain("ActiveSFTUE_01", 1, isAlias: true));

        var (lua, warnings) = Generate(chain);

        Assert.DoesNotContain("{{Item|Seance: Lady Voyance's House|1}}", lua);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheRealTransformSurvives_whenAnAliasSelfReferenceSitsBesideIt()
    {
        // Exactly the reported chain: the alias pair AND the correctly mapped pair together.
        var chain = Chain("Seance", "Seance: Lady Voyance's House",
            Sink("SinkSFTUE_01", 1, "ActiveSFTUE_01", isAlias: true),
            Plain("ActiveSFTUE_01", 1, isAlias: true),
            Sink("SinkSA_01", 1, "ActiveSA_01"),
            Plain("ActiveSA_01", 2));

        var (lua, _) = Generate(chain);

        Assert.Contains("{{Item|Seance: Lady Voyance's House|2}}", lua);
        Assert.DoesNotContain("{{Item|Seance: Lady Voyance's House|1}}", lua);
    }

    [Fact]
    public void ATransformToAnotherChainsSameLevel_isStillListed()
    {
        // Same level number, different chain — a normal chain-terminal transform, must survive.
        var target = Chain("Golden", "Golden Seed", Plain("GoldRoot_01", 1));
        var chain = Chain("SeedBags", "Bigger Pile of Seed Bags",
            Sink("SeedBagEmpty_01", 1, "GoldRoot_01"));

        var (lua, warnings) = Generate(chain, target);

        Assert.Contains("{{Item|Golden Seed|1}}", lua);
        Assert.Empty(warnings);
    }

    [Fact]
    public void DecayIntoAnAliasOfTheSameItem_isNotListed()
    {
        // Lady Voyance's House: alias MGSpB decays into alias LockedB, and BOTH are the same wiki
        // item — an under-the-hood swap the player never sees, so "decays into itself" must not ship.
        var decaying = Plain("MGSpB_01", 1, isAlias: true);
        decaying.DecayAfterLastCycleItemType = "LockedB_01";
        var chain = Chain("LadyVoyance", "Lady Voyance's House",
            decaying,
            Plain("LockedB_01", 1, isAlias: true));

        var (lua, warnings) = Generate(chain);

        Assert.DoesNotContain("decays_into", lua);
        Assert.Empty(warnings);
    }

    [Fact]
    public void DecayIntoAnotherItem_isStillListed()
    {
        var target = Chain("Locked", "Voyance's House Locked", Plain("LockedA_01", 1));
        var decaying = Plain("MGSpA_01", 1);
        decaying.DecayAfterLastCycleItemType = "LockedA_01";
        var chain = Chain("LadyVoyance", "Lady Voyance's House", decaying);

        var (lua, _) = Generate(chain, target);

        Assert.Contains("{{Item|Voyance's House Locked|1}}", lua);
    }

    [Fact]
    public void ANonAliasSelfReference_isAlsoSkipped_withAGenericHint()
    {
        var chain = Chain("Loop", "Looping Item",
            Sink("Loop_01", 1, "LoopTwin_01"),
            Plain("LoopTwin_01", 1));

        var (lua, warnings) = Generate(chain);

        Assert.DoesNotContain("{{Item|Looping Item|1}}", lua);
        Assert.Contains(warnings, w => w.Contains("Check the level overrides"));
    }
}
