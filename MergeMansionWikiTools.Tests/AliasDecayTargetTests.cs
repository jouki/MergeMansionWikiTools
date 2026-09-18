using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// An item decays into exactly ONE thing, so the Decays Into cell must not list two targets with no
/// percentages — that reads as a choice the game never offers. Aliases produce exactly that when they
/// disagree, so the primary answers first and aliases only fill in when it produced nothing.
/// <para>
/// Both halves are real: Investigation: The Mansion L2 decays into the opened location on every cycle
/// while its scripted FTUE copy drops back to the locked one once ever; and Scarab Box L7 holds a
/// primary that decays into its own row (filtered as a self-reference) next to the alias carrying the
/// only real target (user reports, 2026-09-18).
/// </para>
/// </summary>
public class AliasDecayTargetTests
{
    private static string Decaying(string itemType, int level, string into) => $$"""
        {
          "Item": {
            "Name": "Stage", "ItemType": "{{itemType}}", "ConfigKey": 1, "LevelNumber": {{level}},
            "Description": "d",
            "DecayFeatures": {
              "DoesDecay": true, "Lifetime": 5000,
              "ItemProducer": { "Constant": [ { "Item": "{{into}}", "Quantity": 1 } ] }
            }
          }
        }
        """;

    private static string Plain(string name, string configKey, string itemType, int level) => $$"""
        {
          "Name": "{{name}}", "ConfigKey": "{{configKey}}",
          "PrimaryChain": [ { "Item": {
            "Name": "{{name}}", "ItemType": "{{itemType}}", "ConfigKey": 2,
            "LevelNumber": {{level}}, "Description": "d"
          } } ]
        }
        """;

    private static async Task<string> TableAsync(string chainJson, string? ftue = null,
        params (string ItemType, int Level)[] aliases)
    {
        var json = $$"""
        { "CreatedAt": "2026-09-18T00:00:00", "Data": [ {{chainJson}} ] }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"mmwt-decay-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var data = new DataService(new ChainNameService());
            await data.LoadAsync(path);

            var mapping = new WikiMappingCache();
            foreach (var (itemType, level) in aliases)
                mapping.Mappings[itemType] = new WikiMappingEntry
                {
                    Fields = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase)
                    {
                        ["chainName"] = "Stage", ["isAlias"] = true, ["level"] = (double)level,
                    },
                };
            if (ftue != null)
                mapping.Mappings[ftue] = new WikiMappingEntry
                {
                    Fields = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase)
                    {
                        ["chainName"] = "Stage", ["isAlias"] = true, ["level"] = 2d, ["isFtue"] = true,
                    },
                };
            ChainMergeService.ApplyWikiMapping(data, mapping);

            var chain = data.Chains.Find(c => c.DisplayName == "Stage")!;
            return new WikiTableGenerator(data, mapping).Generate(chain, "Stage", lowPrices: false);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WhenAliasesDisagree_thePrimaryTargetWins()
    {
        var table = await TableAsync($$"""
            {
              "Name": "Stage", "ConfigKey": "Stage",
              "PrimaryChain": [
                {{Decaying("ActiveA_01", 2, "Normal_01")}},
                {{Decaying("ActiveFTUE_01", 2, "Tutorial_01")}}
              ]
            },
            {{Plain("Normal Outcome", "Normal", "Normal_01", 1)}},
            {{Plain("Tutorial Outcome", "Tutorial", "Tutorial_01", 1)}}
            """, null, ("ActiveFTUE_01", 2));

        Assert.Contains("Normal Outcome", table);
        Assert.DoesNotContain("Tutorial Outcome", table);
    }

    [Fact]
    public async Task WhenThePrimaryOnlyPointsAtItself_theAliasTargetSurvives()
    {
        // Primary decays into the alias, which sits on the same page AND level → self-reference,
        // filtered. Without the fallback pass the cell would come out empty.
        var table = await TableAsync($$"""
            {
              "Name": "Stage", "ConfigKey": "Stage",
              "PrimaryChain": [
                {{Decaying("Box_07", 7, "Spawner_01")}},
                {{Decaying("Spawner_01", 1, "Scarab_01")}}
              ]
            },
            {{Plain("Scarab", "Scarab", "Scarab_01", 1)}}
            """, null, ("Spawner_01", 7));

        Assert.Contains("Scarab", table);
    }

    [Fact]
    public async Task AnFtueCopy_contributesNeitherDropsNorDecayTargets()
    {
        // isFtue marks the scripted copy the game swaps in for the first playthrough. It stays an
        // alias of the row (no phantom page, no level collision) but its values describe a run that
        // happens once, so no aggregated cell may show them (user decision, 2026-09-18).
        var json = $$"""
            {
              "Name": "Stage", "ConfigKey": "Stage",
              "PrimaryChain": [
                {
                  "Item": {
                    "Name": "Stage", "ItemType": "ActiveA_01", "ConfigKey": 1, "LevelNumber": 2,
                    "Description": "d",
                    "ActivationFeatures": {
                      "Activable": true,
                      "ActivationSpawn": { "ControlledRandomSequence": { "Odds": { "Normal_01": 100 } } },
                      "ActivationCycle": { "MiniChargesInSingleCharge": 5, "DropsInSingleMiniCharge": 1, "HowManyCycles": 1 },
                      "MaxCharges": 1
                    },
                    "DecayFeatures": {
                      "DoesDecay": true, "Lifetime": 5000,
                      "ItemProducer": { "Constant": [ { "Item": "Normal_01", "Quantity": 1 } ] }
                    }
                  }
                },
                {
                  "Item": {
                    "Name": "Stage", "ItemType": "ActiveFTUE_01", "ConfigKey": 3, "LevelNumber": 2,
                    "Description": "d",
                    "ActivationFeatures": {
                      "Activable": true,
                      "ActivationSpawn": { "PredefinedSequence": { "Odds": { "Normal_01": 90, "Tutorial_01": 10 } } },
                      "ActivationCycle": { "MiniChargesInSingleCharge": 5, "DropsInSingleMiniCharge": 1, "HowManyCycles": 1 },
                      "MaxCharges": 1
                    },
                    "DecayFeatures": {
                      "DoesDecay": true, "Lifetime": 5000,
                      "ItemProducer": { "Constant": [ { "Item": "Tutorial_01", "Quantity": 1 } ] }
                    }
                  }
                }
              ]
            },
            {{Plain("Normal Outcome", "Normal", "Normal_01", 1)}},
            {{Plain("Tutorial Outcome", "Tutorial", "Tutorial_01", 1)}}
            """;
        var table = await TableAsync(json, ftue: "ActiveFTUE_01");

        Assert.Contains("Normal Outcome", table);
        Assert.DoesNotContain("Tutorial Outcome", table);
    }
}
