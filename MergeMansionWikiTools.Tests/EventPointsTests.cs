using System.IO;
using System.Threading.Tasks;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Event points are TWO numbers per item and the table showed neither (user report, 2026-09-18:
/// "Suspect Interviews nemá žádnou zmínku o bodech v tabulce").
/// <para>
/// Creating the item by merging pays <c>Rewards[].RewardCollectibleBoardEventProgress.Amount</c>,
/// tapping it pays <c>CollectableFeatures.CollectAction.Progress</c>. Verified row by row against the
/// hand-written Amelia Boulton Memorabilia table, where create is half of tap; Suspect Interviews
/// pays the same either way, so the two columns can also be equal.
/// </para>
/// </summary>
public class EventPointsTests
{
    private static async Task<(DataService Data, ParsedChain Chain)> LoadAsync(int createPoints, int tapPoints)
    {
        var json = $$"""
        {
          "CreatedAt": "2026-09-18T00:00:00",
          "Data": [
            {
              "Name": "Points Chain", "ConfigKey": "PointsChain",
              "PrimaryChain": [
                {
                  "Item": {
                    "Name": "Token", "ItemType": "Token_01", "ConfigKey": 1, "LevelNumber": 1,
                    "Description": "d",
                    "CollectableFeatures": { "Collectable": true, "CollectAction": { "Progress": {{tapPoints}} } },
                    "Rewards": [ { "RewardCollectibleBoardEventProgress": { "Amount": {{createPoints}} } } ]
                  }
                }
              ]
            }
          ]
        }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"mmwt-points-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var data = new DataService(new ChainNameService());
            await data.LoadAsync(path);
            return (data, data.Chains[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BothNumbers_areParsed_andRenderedAsTwoColumns()
    {
        var (data, chain) = await LoadAsync(createPoints: 15, tapPoints: 30);

        Assert.Equal(15, chain.Items[0].EventPointsOnCreate);
        Assert.Equal(30, chain.Items[0].EventPointsOnTap);

        var table = new WikiTableGenerator(data, new WikiMappingCache())
            .Generate(chain, "Points Chain", lowPrices: true);

        Assert.Contains("! Create to Collect", table);
        Assert.Contains("! Tap to Collect", table);
        Assert.Contains("{{Green Coins}} 15", table);
        Assert.Contains("{{Green Coins}} 30", table);
    }

    [Fact]
    public async Task APointsChain_isTaggedPointsItem_withoutTheCheckbox()
    {
        var (data, chain) = await LoadAsync(createPoints: 1, tapPoints: 1);

        var infobox = new InfoboxGeneratorService(data, new WikiMappingCache())
            .Generate(chain, data.Chains, data.ItemNames, new InfoboxGeneratorOptions(), new string[0]);

        Assert.Contains("Points Item", infobox);
    }

    [Fact]
    public async Task AChainWithoutPoints_getsNeitherColumnNorType()
    {
        var json = """
        { "CreatedAt": "2026-09-18T00:00:00", "Data": [ { "Name": "Plain", "ConfigKey": "Plain",
          "PrimaryChain": [ { "Item": { "Name": "Plain", "ItemType": "Plain_01", "ConfigKey": 1,
          "LevelNumber": 1, "Description": "d" } } ] } ] }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"mmwt-nopoints-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var data = new DataService(new ChainNameService());
            await data.LoadAsync(path);
            var chain = data.Chains[0];

            var table = new WikiTableGenerator(data, new WikiMappingCache())
                .Generate(chain, "Plain", lowPrices: true);
            var infobox = new InfoboxGeneratorService(data, new WikiMappingCache())
                .Generate(chain, data.Chains, data.ItemNames, new InfoboxGeneratorOptions(), new string[0]);

            Assert.DoesNotContain("Collect", table);
            Assert.DoesNotContain("Points Item", infobox);
        }
        finally { File.Delete(path); }
    }
}
