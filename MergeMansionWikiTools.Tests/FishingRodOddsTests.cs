using System.Collections.Generic;
using System.Text.Json;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// FishingRodFeatures pipeline: Lucky Snap cameras (and Lucky Catch rods) carry their drops in
/// FishingRodFeatures.ItemOdds ([{Type, Weight}, …]) — a producer shape the parser never read,
/// so camera pages had no Drop Odds section (user report 2026-07-11, Basic Camera). The weights
/// are normalized to percentages, emitted through the shared <c>odds</c> Lua field with an
/// <c>isFishingRod</c> flag, and the Drop Odds section gate accepts fishing chains.
/// </summary>
public class FishingRodOddsTests
{
    // ── DataService.ParseItem: FishingRodFeatures → normalized FishingOdds ──

    private static ParsedItem Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ds = new DataService(new ChainNameService());
        return ds.ParseItem(doc.RootElement)!;
    }

    [Fact]
    public void ParseItem_FishingRod_NormalizesWeightsToPercent()
    {
        // Real shape (LS_Summer_BasicCamera_05, weights 112+88 → 56% / 44%)
        var item = Parse("""
        {
            "ItemType": "LS_Summer_BasicCamera_05",
            "Name": "Basic Camera",
            "LevelNumber": 5,
            "FishingRodFeatures": {
                "IsFishingRod": true,
                "ItemOdds": [
                    { "Type": { "ItemType": "LS_Common_HoodedWarbler_01" }, "Weight": 112 },
                    { "Type": { "ItemType": "LS_Uncommon_Eider_01" }, "Weight": 88 }
                ]
            }
        }
        """);

        Assert.True(item.IsFishingRod);
        Assert.NotNull(item.FishingOdds);
        Assert.Equal(56.0, item.FishingOdds!["LS_Common_HoodedWarbler_01"], 10);
        Assert.Equal(44.0, item.FishingOdds!["LS_Uncommon_Eider_01"], 10);
    }

    [Fact]
    public void ParseItem_NoFishingRodFeatures_LeavesFieldsUnset()
    {
        var item = Parse("""{ "ItemType": "Toolbox_01", "Name": "Toolbox", "LevelNumber": 1 }""");

        Assert.False(item.IsFishingRod);
        Assert.Null(item.FishingOdds);
        Assert.Null(item.FishingDropletConfigKey);
    }

    [Fact]
    public void ParseItem_WaterDropletOverride_StoresNumericConfigKey()
    {
        // Real shape: the droplet ref is a NUMERIC item ConfigKey (LS_Summer_MissedPhotos_01)
        var item = Parse("""
        {
            "ItemType": "LS_Summer_BasicCamera_05",
            "LevelNumber": 5,
            "FishingRodFeatures": {
                "IsFishingRod": true,
                "ItemOdds": [ { "Type": { "ItemType": "LS_Common_HoodedWarbler_01" }, "Weight": 112 } ],
                "WaterDropletOverride": 15130636
            }
        }
        """);

        Assert.Equal("15130636", item.FishingDropletConfigKey);
    }

    // ── Lua emit: odds through the shared field + isFishingRod flag ──

    [Fact]
    public void LuaEmit_FishingRod_EmitsOddsAndFlag()
    {
        var rod = new ParsedItem
        {
            ItemType = "LS_Summer_BasicCamera_05",
            Name = "Basic Camera",
            Level = 5,
            Description = "d",
            IsFishingRod = true,
            FishingOdds = new Dictionary<string, double>
            {
                ["LS_Common_HoodedWarbler_01"] = 56.0,
                ["LS_Uncommon_Eider_01"] = 44.0,
            },
        };
        var chain = new ParsedChain { ConfigKey = "LS_Summer_BasicCamera", DisplayName = "Basic Camera" };
        chain.Items.Add(rod);

        var lua = new LuaGeneratorService().GenerateCombinedItemsAndChainNamesLua(
            new List<ParsedChain> { chain });

        Assert.Contains("isFishingRod = true", lua);
        Assert.Contains(
            "odds = {{id = \"LS_Common_HoodedWarbler_01\", value = 56}, " +
            "{id = \"LS_Uncommon_Eider_01\", value = 44}}",
            lua);
    }

    // ── Drop Odds section gate accepts fishing chains ──

    [Fact]
    public void DropOddsSection_FishingChain_EmitsInvoke()
    {
        var chain = new ParsedChain { ConfigKey = "LS_Summer_BasicCamera", DisplayName = "Basic Camera" };
        chain.Items.Add(new ParsedItem
        {
            ItemType = "LS_Summer_BasicCamera_05",
            Level = 5,
            IsFishingRod = true,
            FishingOdds = new Dictionary<string, double> { ["LS_Common_HoodedWarbler_01"] = 100.0 },
        });

        var gen = new WikiTableGenerator(new DataService(new ChainNameService()));
        var section = gen.GenerateDropOddsSection(chain);

        Assert.NotNull(section);
        Assert.Contains("=== Drop Odds ===", section);
        Assert.Contains("GetItemOddsTableFromChainName", section);
    }
}
