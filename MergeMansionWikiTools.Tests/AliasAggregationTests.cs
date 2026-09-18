using System.Collections.Generic;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Alias values must reach the merged row, and a transform that resolves back onto the row itself
/// must not (v0.24.79, design `_plans/2026-09-18-alias-variant-emission-design.md`).
/// <para>
/// An alias is a second ItemType for a row that already exists — from the player's side it is the
/// same item, the game just swaps the scripted/AB variant underneath. So its values belong in the
/// one row (<see cref="WikiTableGenerator.VariantSetForLevel"/> says as much), but until v0.24.79 the
/// aggregating cell builders were handed the ROW set, which has aliases filtered out. A value only an
/// alias carried therefore opened a column it could never fill — the "phantom column".
/// </para>
/// <para>
/// Feeding aliases back in immediately creates the opposite hazard: on Voyance's Black Cat the alias
/// transforms into the very row it belongs to, so both the cell and the column visibility have to
/// drop self-references.
/// </para>
/// </summary>
public class AliasAggregationTests
{
    private static ParsedItem Item(string type, int level, bool alias = false, bool variant = false) => new()
    {
        ItemType = type,
        Name = type,
        Level = level,
        Description = "d",
        IsAlias = alias,
        IsVariant = variant,
    };

    private static ParsedChain Chain(string key, string display, params ParsedItem[] items) => new()
    {
        ConfigKey = key,
        DisplayName = display,
        Items = new List<ParsedItem>(items),
    };

    /// <summary>Generates with a DataService that knows the given chains (needed for target lookup).</summary>
    private static string Generate(ParsedChain page, params ParsedChain[] others)
    {
        var data = new DataService(new ChainNameService());
        data.Chains.Add(page);
        data.Chains.AddRange(others);
        return new WikiTableGenerator(data).Generate(page, page.DisplayName, lowPrices: false);
    }

    [Fact]
    public void AliasDrops_reachTheMergedRow()
    {
        // The point of the test: ONLY the alias produces, so its drops have to reach the one row.
        var generator = Item("CatActive_01", 1, alias: true);
        generator.IsGenerator = true;
        generator.DropOdds = new Dictionary<string, double> { ["CatClues_01"] = 100 };
        var page = Chain("Cat", "Voyance's Black Cat", Item("Cat_01", 1), generator);

        var lua = Generate(page, Chain("Clues", "Cat Clues", Item("CatClues_01", 1)));

        // Asserted on the cell being FILLED rather than on the target's display name: resolving that
        // name needs the lookup tables DataService builds during a real load, which this fixture has
        // no business reproducing. A dash here is exactly the phantom-column symptom.
        Assert.Contains("! Drops", lua);
        Assert.DoesNotContain("{{Dash}}", lua);
    }

    [Fact]
    public void AliasTransform_toAnotherPage_fillsTheCell()
    {
        // Lady Voyance's House: only the alias carries the sink reward, and it leaves the page.
        var alias = Item("LockedB_01", 1, alias: true);
        alias.IsSink = true;
        alias.SinkRewardItemType = "SinkIB_01";
        var page = Chain("LadyVoyance", "Lady Voyance's House", Item("MGSpA_01", 1), alias);

        var lua = Generate(page, Chain("Investigation", "Investigation: Lady Voyance's House", Item("SinkIB_01", 1)));

        Assert.Contains("! Transforms To", lua);
        Assert.Contains("{{Item|Investigation: Lady Voyance's House|1}}", lua);
    }

    [Fact]
    public void AliasTransform_ontoItsOwnRow_producesNoColumnAtAll()
    {
        // Voyance's Black Cat: alias Cat_01 "transforms into" CatActive_01 — same page, same level.
        var alias = Item("Cat_01", 1, alias: true);
        alias.IsSink = true;
        alias.SinkRewardItemType = "CatActive_01";
        var page = Chain("Cat", "Voyance's Black Cat", alias, Item("CatActive_01", 1));

        var lua = Generate(page);

        Assert.DoesNotContain("Transforms To", lua);
    }

    [Fact]
    public void AliasDecay_ontoItsOwnRow_isDropped()
    {
        // Lady Voyance's House: alias MGSpB decays into alias LockedB, both the same row.
        var alias = Item("MGSpB_01", 1, alias: true);
        alias.DecayAfterLastCycleItemType = "LockedB_01";
        var page = Chain("LadyVoyance", "Lady Voyance's House", alias, Item("LockedB_01", 1, alias: true));

        var lua = Generate(page);

        Assert.DoesNotContain("Decays Into", lua);
    }

    [Fact]
    public void AliasDecay_toAnotherPage_survives()
    {
        var alias = Item("MGSpA_01", 1, alias: true);
        alias.DecayAfterLastCycleItemType = "LockedA_01";
        var page = Chain("LadyVoyance", "Lady Voyance's House", Item("Primary_01", 1), alias);

        var lua = Generate(page, Chain("Locked", "Voyance's House Locked", Item("LockedA_01", 1)));

        Assert.Contains("! Decays Into", lua);
        Assert.Contains("{{Item|Voyance's House Locked|1}}", lua);
    }

    [Fact]
    public void VariantTransform_ontoTheSamePage_isKept()
    {
        // Variants render as their own sub-rows, so a step between them is something the reader can
        // follow — only aliases collapse into one row and must therefore hide it.
        var variant = Item("VarA_01", 1, variant: true);
        variant.IsSink = true;
        variant.SinkRewardItemType = "VarB_01";
        var page = Chain("Thing", "Thing", variant, Item("VarB_01", 1, variant: true));

        var lua = Generate(page);

        Assert.Contains("! Transforms To", lua);
        Assert.Contains("{{Item|Thing|1}}", lua);
    }
}
