using System.IO;
using System.Threading.Tasks;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// <c>DecayAfterLastCycleProducer</c> must be read whichever randomizer wrapper the config uses.
/// <para>
/// Only <c>ControlledRandom</c> was handled, so a <c>ControlledRandomSequence</c> roll parsed as
/// nothing. Consequence on the wiki: Unfortunate Events L9 rolls 60% Voyance's Black Cat / 40%
/// Poison once it is spent — that never reached the data, so the cat's page listed no source at all
/// and pointed only at the Cat Clues it produces itself, a closed loop with no way in (user report,
/// 2026-09-18). Every other odds reader in DataService already accepted all three spellings.
/// </para>
/// </summary>
public class DecayAfterCycleOddsTests
{
    private static async Task<DataService> LoadAsync(string decayProducerJson)
    {
        var json = $$"""
        {
          "CreatedAt": "2026-09-18T00:00:00",
          "Data": [
            {
              "Name": "Spent Thing",
              "ConfigKey": "SpentThing",
              "PrimaryChain": [
                {
                  "Item": {
                    "Name": "Spent Thing",
                    "ItemType": "SpentThing_01",
                    "ConfigKey": 1,
                    "LevelNumber": 1,
                    "Description": "d",
                    "ActivationFeatures": {
                      "Activable": true,
                      "DecayAfterLastCycleProducer": {{decayProducerJson}}
                    }
                  }
                }
              ]
            }
          ]
        }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"mmwt-decay-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var data = new DataService(new ChainNameService());
            await data.LoadAsync(path);
            return data;
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("ControlledRandom")]
    [InlineData("ControlledRandomSequence")]
    [InlineData("Random")]
    public async Task EveryRandomizerWrapper_yieldsTheDecayOdds(string wrapper)
    {
        var data = await LoadAsync($$"""
            { "{{wrapper}}": { "Odds": { "Cat_01": 60.0, "Poison_01": 40.0 } } }
            """);

        var item = data.Chains[0].Items[0];

        Assert.NotNull(item.DecayAfterLastCycleOdds);
        Assert.Equal(60.0, item.DecayAfterLastCycleOdds!["Cat_01"]);
        Assert.Equal(40.0, item.DecayAfterLastCycleOdds["Poison_01"]);
    }

    [Fact]
    public async Task AConstantDecay_stillYieldsASingleTarget_notOdds()
    {
        var data = await LoadAsync("""
            { "Constant": [ { "Item": "Cat_01", "Quantity": 1 } ] }
            """);

        var item = data.Chains[0].Items[0];

        Assert.Equal("Cat_01", item.DecayAfterLastCycleItemType);
        Assert.Null(item.DecayAfterLastCycleOdds);
    }

    [Fact]
    public async Task ADecayAfterCycleRoll_reachesTheLuaDatatable_asDecayInto()
    {
        // Reading the roll is only half the job: LuaGeneratorService emitted `decayInto` from the
        // LIFETIME decay only, so a spent-generator roll never reached the wiki and the Decay Odds
        // section stayed empty even after the parser fix (v0.24.85).
        var data = await LoadAsync("""
            { "ControlledRandomSequence": { "Odds": { "Cat_01": 60.0, "Poison_01": 40.0 } } }
            """);

        var lua = new LuaGeneratorService().GenerateRawItemsAndChainNamesLua(data.Chains);

        Assert.Contains("decayInto", lua);
        Assert.Contains("Cat_01", lua);
        Assert.Contains("Poison_01", lua);
    }
}
