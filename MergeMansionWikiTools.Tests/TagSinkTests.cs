using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tag-based sinks must reach the generated wiki page the same way ordinary sinks do.
/// <para>
/// Reported on <c>DNA Samples</c>: the page carried no Fuel column, no "Transforms To" and no
/// <c>needs</c> row, so nothing said that a DNA Kit is fuelled by a Murder Weapon. The cause is
/// that a sink can state its fuel two ways and only one was handled:
/// </para>
/// <list type="bullet">
/// <item><c>SinkFeatures.Factory.ScoreTargets</c> — named items, all of them required (Flower Bed
/// needs 1x Shovel).</item>
/// <item><c>SinkFeatures.Factory.Tag</c> + <c>InputCount</c> + <c>RewardTagName</c> — <b>any one</b>
/// item carrying the tag, and the result is looked up by the summed <c>SinkPoints</c> of what was
/// consumed. Eight sinks in the whole game use this form.</item>
/// </list>
/// <para>
/// The fix resolves the tag form into the same fields the ScoreTargets form fills, so the Lua
/// datatable, the Merge Stages table and the infobox all work unchanged — the only new behaviour
/// is <see cref="ParsedItem.SinkIsAnyOf"/>, which makes the requirement render as an OR.
/// </para>
/// </summary>
public class TagSinkTests
{
    private const string Tag = "MurderWeapons";

    private static ParsedItem Weapon(string type, string configKey, int points) => new()
    {
        ItemType = type,
        Name = type,
        Level = 1,
        NumericConfigKey = configKey,
        SinkTag = Tag,
        SinkPoints = points,
    };

    private static ParsedChain Chain(string configKey, string display, params ParsedItem[] items)
    {
        var c = new ParsedChain { ConfigKey = configKey, DisplayName = display, OriginalName = display };
        c.Items.AddRange(items);
        return c;
    }

    /// <summary>One TagRewards row: which fuel owns it, what it makes and how many drops that is.</summary>
    private static ParsedItem.SinkTagReward Reward(
        int points, string fuelItemType, string resultItemType, int drops,
        Dictionary<string, double>? odds = null) =>
        new(points, new List<string> { fuelItemType },
            new List<ParsedItem.SinkTagProduct> { new(resultItemType, drops, odds) });

    /// <summary>
    /// LoadAsync fills ChainNames and ItemLevels; a hand-built DataService has neither, so the
    /// resolvers fall back to the raw ConfigKey and level 0. Register both.
    /// </summary>
    private static void Register(DataService data, params ParsedChain[] chains)
    {
        foreach (var c in chains)
        {
            data.ChainNames[c.ConfigKey] = c.DisplayName;
            foreach (var i in c.Items)
                if (!string.IsNullOrEmpty(i.ItemType))
                    data.ItemLevels[i.ItemType] = i.Level;
        }
    }

    /// <summary>The DNA Kit shape: one tag sink plus the four weapons that can fuel it.</summary>
    /// <param name="decayBackFrom">Set to make a producer decay back into the kit (the fallback path).</param>
    /// <param name="withDumpTables">Populate Factory.TagFuel/TagRewards as the dumper now resolves them.</param>
    private static (DataService Data, ParsedItem Kit) BuildKitScenario(
        string? decayBackFrom = null, bool withDumpTables = false)
    {
        var kit = new ParsedItem
        {
            ItemType = "LDE_MurderAtTheMansion_DNAKit_08",
            Name = "DNA Kit",
            Level = 8,
            NumericConfigKey = "9001",
            IsSink = true,
            SinkTagName = Tag,
            SinkInputCount = 1,
            SinkRewardTagName = "DNAKitRewards",
        };

        var weapons = Chain("LDE_MurderAtTheMansion_MW", "Murder Weapon",
            Weapon("LDE_MurderAtTheMansion_MW_01", "101", 1),
            Weapon("LDE_MurderAtTheMansion_MW_02", "102", 2),
            Weapon("LDE_MurderAtTheMansion_MW_03", "103", 3),
            Weapon("LDE_MurderAtTheMansion_MW_04", "104", 4));
        // Levels 1-4 of one merged chain, which is how the wiki names them.
        for (int i = 0; i < weapons.Items.Count; i++) weapons.Items[i].Level = i + 1;

        var kitChain = Chain("LDE_MurderAtTheMansion_DNAKit", "DNA Samples", kit);

        var collected = new ParsedItem
        {
            ItemType = "LDE_MurderAtTheMansion_DNAKitActive_01",
            Name = "DNA Samples",
            Level = 1,
            NumericConfigKey = "9002",
            DecayIntoItemType = decayBackFrom,
        };
        var collectedChain = Chain("LDE_MurderAtTheMansion_DNAKitActive", "DNA Samples Collected", collected);

        if (withDumpTables)
        {
            kit.SinkTagFuel = weapons.Items
                .Select(w => (w.ItemType, SinkPoints: w.Level))
                .ToList();
            kit.SinkTagRewards = new List<ParsedItem.SinkTagReward>
            {
                Reward(1, "LDE_MurderAtTheMansion_MW_01", "LDE_MurderAtTheMansion_DNAKitActive_01", 8),
                Reward(2, "LDE_MurderAtTheMansion_MW_02", "LDE_MurderAtTheMansion_DNAKitActiveB_01", 10),
                Reward(3, "LDE_MurderAtTheMansion_MW_03", "LDE_MurderAtTheMansion_DNAKitActiveC_01", 12),
                Reward(4, "LDE_MurderAtTheMansion_MW_04", "LDE_MurderAtTheMansion_DNAKitActiveD_01", 20),
            };
        }

        var data = new DataService(new ChainNameService());
        data.Chains.AddRange(new[] { kitChain, weapons, collectedChain });
        Register(data, kitChain, weapons, collectedChain);
        return (data, kit);
    }

    [Fact]
    public void TagSink_resolves_every_tagged_item_as_fuel()
    {
        var (data, kit) = BuildKitScenario();

        data.ResolveTagSinks();

        Assert.True(kit.SinkIsAnyOf);
        Assert.Equal(new[] { "101", "102", "103", "104" }, kit.SinkRequirementConfigKeys);
        Assert.All(kit.SinkRequirementAmounts!.Values, amount => Assert.Equal(1, amount));
    }

    [Fact]
    public void TagSink_inputCount_is_carried_into_the_amounts()
    {
        var (data, kit) = BuildKitScenario();
        kit.SinkInputCount = 3;   // the Tarot Table / Kitchen Tools shape

        data.ResolveTagSinks();

        Assert.All(kit.SinkRequirementAmounts!.Values, amount => Assert.Equal(3, amount));
    }

    [Fact]
    public void TagSink_without_tag_rewards_falls_back_to_the_producer_that_decays_back_into_it()
    {
        // A dump predating Factory.TagRewards. The producer whose decay puts the kit back on the
        // board is still unambiguously the kit's fueled result.
        var (data, kit) = BuildKitScenario(decayBackFrom: "LDE_MurderAtTheMansion_DNAKit_08");

        data.ResolveTagSinks();

        Assert.Equal("LDE_MurderAtTheMansion_DNAKitActive_01", kit.SinkRewardItemType);
    }

    [Fact]
    public void TagSink_with_no_resolvable_result_warns_instead_of_going_silent()
    {
        var (data, kit) = BuildKitScenario();   // nothing decays back, no library

        data.ResolveTagSinks();

        Assert.Null(kit.SinkRewardItemType);
        Assert.Contains(data.Warnings, w => w.Contains("DNAKit_08") && w.Contains("Factory.TagRewards"));
    }

    [Fact]
    public void TagSink_missing_fuel_items_warns()
    {
        var (data, kit) = BuildKitScenario();
        kit.SinkTagName = "NoSuchTag";

        data.ResolveTagSinks();

        Assert.Contains(data.Warnings, w => w.Contains("NoSuchTag"));
        Assert.Null(kit.SinkRequirementConfigKeys);
    }

    [Fact]
    public void ScoreTargets_sinks_stay_AND_and_are_left_alone()
    {
        var shovel = new ParsedItem
        {
            ItemType = "LDE_MurderAtTheMansion_Shovel_01", Name = "Shovel", Level = 1, NumericConfigKey = "15209374",
        };
        var bed = new ParsedItem
        {
            ItemType = "LDE_MurderAtTheMansion_DirtPatch_01", Name = "Disturbed Soil", Level = 1,
            NumericConfigKey = "7001", IsSink = true,
            SinkRequirementConfigKeys = new List<string> { "15209374" },
            SinkRequirementAmounts = new Dictionary<string, int> { ["15209374"] = 1 },
            SinkRewardItemType = "LDE_MurderAtTheMansion_DirtPatch_02",
        };
        var data = new DataService(new ChainNameService());
        data.Chains.Add(Chain("LDE_MurderAtTheMansion_Shovel", "Shovel", shovel));
        data.Chains.Add(Chain("LDE_MurderAtTheMansion_DirtPatch", "Flower Bed", bed));

        data.ResolveTagSinks();

        Assert.False(bed.SinkIsAnyOf);
        Assert.Equal("LDE_MurderAtTheMansion_DirtPatch_02", bed.SinkRewardItemType);
        Assert.Single(bed.SinkRequirementConfigKeys!);
    }

    [Fact]
    public void TagSink_takes_the_fuel_list_the_dumper_resolved()
    {
        var (data, kit) = BuildKitScenario(withDumpTables: true);

        data.ResolveTagSinks();

        Assert.True(kit.SinkIsAnyOf);
        Assert.Equal(new[] { "101", "102", "103", "104" }, kit.SinkRequirementConfigKeys);
    }

    [Fact]
    public void TagSink_reward_ladder_comes_from_the_dump_lowest_points_first()
    {
        // The whole ladder stays on the item — Murder Weapon #1 gives an 8-drop run, #4 a 20-drop
        // one — while SinkRewardItemType keeps the baseline the single-valued consumers expect.
        var (data, kit) = BuildKitScenario(withDumpTables: true);

        data.ResolveTagSinks();

        Assert.Equal("LDE_MurderAtTheMansion_DNAKitActive_01", kit.SinkRewardItemType);
        Assert.Equal(4, kit.SinkTagRewards!.Count);
        Assert.Equal("LDE_MurderAtTheMansion_DNAKitActiveD_01", kit.SinkTagRewards[3].Produces[0].ItemType);
        Assert.Equal(20, kit.SinkTagRewards[3].Produces[0].Drops);
    }

    // ── rendering ────────────────────────────────────────────────────

    [Fact]
    public void AnyOf_fuel_collapses_one_chain_into_a_single_group_range()
    {
        var weapons = Chain("MW", "Murder Weapon",
            Weapon("a", "101", 1), Weapon("b", "102", 2), Weapon("c", "103", 3), Weapon("d", "104", 4));
        for (int i = 0; i < weapons.Items.Count; i++) weapons.Items[i].Level = i + 1;

        var text = SinkFuelFormatter.Format(
            weapons.Items.Select(i => (weapons, i)), inputCount: 1, resolve: (c, i) => (c.DisplayName, i.Level));

        Assert.Equal("{{Item/Group|Murder Weapon|4|min=1|max=4}}", text);
    }

    [Fact]
    public void AnyOf_fuel_groups_by_the_MAPPED_name_not_the_game_chain()
    {
        // The real Murder Weapons: four separate game chains, each a lone level 1, which the
        // mapping module merges into "Murder Weapon" levels 1-4. Grouping on the game chain
        // rendered four identical "Murder Weapon (L1)" entries on the live page.
        var chains = new[] { "MWKnife", "MWCandlestick", "MWYarn", "MWPoison" }
            .Select((key, idx) => (Chain: Chain(key, key, Weapon($"{key}_01", $"{idx}", 1)), Level: idx + 1))
            .ToList();

        var text = SinkFuelFormatter.Format(
            chains.Select(c => (c.Chain, c.Chain.Items[0])), inputCount: 1,
            resolve: (c, i) => ("Murder Weapon", chains.First(x => x.Chain == c).Level));

        Assert.Equal("{{Item/Group|Murder Weapon|4|min=1|max=4}}", text);
    }

    [Fact]
    public void AnyOf_fuel_joins_several_chains_with_a_slash_and_states_the_count_once()
    {
        var a = Chain("A", "Spaghetti Hoops", Weapon("a", "1", 1));
        var b = Chain("B", "Sliced Carrots", Weapon("b", "2", 1));

        var text = SinkFuelFormatter.Format(
            new[] { (a, a.Items[0]), (b, b.Items[0]) }, inputCount: 3, resolve: (c, i) => (c.DisplayName, i.Level));

        Assert.Equal("3x {{Item|Spaghetti Hoops|1}} / {{Item|Sliced Carrots|1}}", text);
    }

    [Fact]
    public void AnyOf_fuel_splits_non_consecutive_levels_into_separate_runs()
    {
        var c = Chain("C", "Tarot Cards", Weapon("a", "1", 1), Weapon("b", "2", 1), Weapon("c", "3", 1));
        c.Items[0].Level = 1;
        c.Items[1].Level = 2;
        c.Items[2].Level = 7;

        var text = SinkFuelFormatter.Format(
            c.Items.Select(i => (c, i)), inputCount: 1, resolve: (ch, i) => (ch.DisplayName, i.Level));

        Assert.Equal("{{Item/Group|Tarot Cards|2|min=1|max=2}} / {{Item|Tarot Cards|7}}", text);
    }

    [Fact]
    public void AnyOf_fuel_of_nothing_is_empty_not_a_stray_prefix()
    {
        Assert.Equal("", SinkFuelFormatter.Format(
            System.Array.Empty<(ParsedChain, ParsedItem)>(), inputCount: 3, resolve: (c, i) => (c.DisplayName, i.Level)));
    }

    // ── Variant column label ─────────────────────────────────────────

    /// <summary>
    /// The Variant column used to print the mapping's bare <c>isVariant</c> string. <c>variantItem</c>
    /// names the item instead, and it has to be resolved THROUGH the mapping: the DNA Kit runs are
    /// labelled by Murder Weapons, four one-level game chains the mapping merges into levels 1-4.
    /// </summary>
    [Fact]
    public void VariantItem_label_renders_the_item_resolved_through_the_mapping()
    {
        var weapons = Chain("LDE_MurderAtTheMansion_MW", "Murder Weapon",
            Weapon("LDE_MurderAtTheMansion_MW_01", "101", 1),
            Weapon("LDE_MurderAtTheMansion_MW_04", "104", 4));
        weapons.Items[1].Level = 4;
        var data = new DataService(new ChainNameService());
        data.Chains.Add(weapons);
        Register(data, weapons);

        var runs = new[] { "LDE_MurderAtTheMansion_MW_01", "LDE_MurderAtTheMansion_MW_04" }
            .Select(w => new ParsedItem { IsVariant = true, MappingVariantItemType = w })
            .ToList();
        var gen = new WikiTableGenerator(data);

        Assert.Equal("{{Item|Murder Weapon|1}}", gen.VariantColLabel(0, runs));
        Assert.Equal("{{Item|Murder Weapon|4}}", gen.VariantColLabel(1, runs));
    }

    [Fact]
    public void VariantItem_label_falls_back_to_the_plain_string_then_to_letters()
    {
        var data = new DataService(new ChainNameService());
        var gen = new WikiTableGenerator(data);
        var items = new List<ParsedItem>
        {
            new() { IsVariant = true, MappingVariantLabel = "Spring" },
            new() { IsVariant = true },
        };

        Assert.Equal("Spring", gen.VariantColLabel(0, items));
        Assert.Equal("B", gen.VariantColLabel(1, items));
    }

    /// <summary>
    /// The DNA Kit's Transforms To has no one variant: SinkRewardItemType is just the lowest-point
    /// baseline, so a Variant column would claim the kit always makes Murder Weapon #1's run.
    /// </summary>
    [Fact]
    public void FuelDependent_reward_is_recognised_so_no_single_transform_variant_is_claimed()
    {
        var (data, kit) = BuildKitScenario(withDumpTables: true);
        data.ResolveTagSinks();
        Assert.True(WikiTableGenerator.HasFuelDependentReward(kit));

        // All four fuels landing on the same producer is an ordinary single-target sink again.
        kit.SinkTagRewards = kit.SinkTagRewards!
            .Select(r => Reward(r.TotalPoints, r.FuelItemTypes[0], "LDE_MurderAtTheMansion_DNAKitActive_01", 8))
            .ToList();
        Assert.False(WikiTableGenerator.HasFuelDependentReward(kit));
    }

    // ── Fuel Rewards section ─────────────────────────────────────────

    /// <summary>
    /// The four weapons are not interchangeable, and a page that only shows one drop count hides
    /// that. The section pairs each fuel with what it actually produces.
    /// </summary>
    [Fact]
    public void FuelRewards_section_pairs_each_fuel_with_its_own_run()
    {
        var (data, kit) = BuildKitScenario(withDumpTables: true);
        // Drop counts come from the dump rows, not from the chain items — the real targets are
        // frequently in no PrimaryChain at all.
        foreach (var suffix in new[] { "B", "C", "D" })
        {
            var variant = new ParsedItem
            {
                ItemType = $"LDE_MurderAtTheMansion_DNAKitActive{suffix}_01",
                Name = "DNA Samples", Level = 1,
            };
            var variantChain = Chain($"LDE_MurderAtTheMansion_DNAKitActive{suffix}", "DNA Samples Collected", variant);
            data.Chains.Add(variantChain);
            Register(data, variantChain);
        }
        data.ResolveTagSinks();

        var section = new WikiTableGenerator(data)
            .GenerateFuelRewardsSection(data.Chains.Single(c => c.ConfigKey.EndsWith("DNAKit")));

        Assert.NotNull(section);
        Assert.StartsWith("=== Fuel Rewards ===", section);
        Assert.Contains("{{Item|Murder Weapon|1}}", section);
        Assert.Contains("{{Item|Murder Weapon|4}}", section);
        Assert.Contains("{{Item|DNA Samples Collected|1}}", section);
        Assert.Contains("| 8", section);
        Assert.Contains("| 20", section);
    }

    /// <summary>
    /// The Drops column answers WHAT is produced, not just how many: a certain drop is the bare
    /// item, a rolled one carries its percentage, and the count lives in its own column.
    /// </summary>
    [Fact]
    public void FuelRewards_Drops_column_names_the_item_and_adds_odds_only_when_rolled()
    {
        var (data, kit) = BuildKitScenario(withDumpTables: true);
        // A certain drop (the DNA Kit's runs all yield DNA Evidence L1).
        kit.SinkTagRewards![0] = Reward(1, "LDE_MurderAtTheMansion_MW_01",
            "LDE_MurderAtTheMansion_DNAKitActive_01", 8,
            new Dictionary<string, double> { ["LDE_MurderAtTheMansion_MW_01"] = 100 });
        // A rolled one (the Ghost Hunting shape).
        kit.SinkTagRewards[3] = Reward(4, "LDE_MurderAtTheMansion_MW_04",
            "LDE_MurderAtTheMansion_DNAKitActiveD_01", 20,
            new Dictionary<string, double> { ["LDE_MurderAtTheMansion_MW_01"] = 70, ["LDE_MurderAtTheMansion_MW_02"] = 30 });
        data.ResolveTagSinks();

        var section = Section(data);
        Assert.Contains("! Drops", section);
        Assert.Contains("! Total Drops", section);
        Assert.DoesNotContain("! Drop Odds", section);
        Assert.Contains("{{Item|Murder Weapon|1}} 70 %", section);
        Assert.Contains("{{Item|Murder Weapon|2}} 30 %", section);
        // a certain drop is the bare item, with no percentage
        Assert.DoesNotContain("100 %", section);
        Assert.Contains("{{Item|Murder Weapon|1}}", section);
    }

    /// <summary>A target that is in no PrimaryChain still has to produce a usable row.</summary>
    [Fact]
    public void FuelRewards_section_survives_a_result_that_is_not_in_the_chain_dump()
    {
        var (data, kit) = BuildKitScenario(withDumpTables: true);
        kit.SinkTagRewards![3] = Reward(4, "LDE_MurderAtTheMansion_MW_04",
            "LDE_GreenAcresQuest2024_FlowerCompost_Producing_01", 14);
        data.ResolveTagSinks();

        var section = Section(data);
        Assert.Contains("LDE_GreenAcresQuest2024_FlowerCompost_Producing_01", section);
        Assert.Contains("| 14", section);
    }

    private static string? Section(DataService data) =>
        new WikiTableGenerator(data)
            .GenerateFuelRewardsSection(data.Chains.Single(c => c.ConfigKey.EndsWith("DNAKit")));

    [Fact]
    public void FuelRewards_section_is_skipped_when_the_choice_does_not_matter()
    {
        // Every fuel lands on the same producer — nothing to tell the reader.
        var (data, kit) = BuildKitScenario(withDumpTables: true);
        kit.SinkTagRewards = kit.SinkTagRewards!
            .Select(r => Reward(r.TotalPoints, r.FuelItemTypes[0], "LDE_MurderAtTheMansion_DNAKitActive_01", 8))
            .ToList();
        data.ResolveTagSinks();

        Assert.Null(new WikiTableGenerator(data)
            .GenerateFuelRewardsSection(data.Chains.Single(c => c.ConfigKey.EndsWith("DNAKit"))));
    }

    [Fact]
    public void FuelRewards_section_is_skipped_when_a_total_is_a_sum_of_several_items()
    {
        // InputCount 3 (Tarot Table, Kitchen Tools): a point total is a sum over three cards, so no
        // single fuel maps to a row and pairing them would be a lie.
        var (data, kit) = BuildKitScenario(withDumpTables: true);
        kit.SinkInputCount = 3;
        data.ResolveTagSinks();

        Assert.Null(new WikiTableGenerator(data)
            .GenerateFuelRewardsSection(data.Chains.Single(c => c.ConfigKey.EndsWith("DNAKit"))));
    }
}
