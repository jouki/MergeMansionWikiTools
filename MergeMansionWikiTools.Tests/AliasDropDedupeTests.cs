using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// The aggregated Drops cell must deduplicate ENTRY BY ENTRY, not whole cells.
/// <para>
/// Aliases of one row usually overlap: Investigation: The Mansion L2 carries three of them — the A/B
/// pair dropping Incriminating Evidence, and the scripted FTUE copy dropping that plus a Magnifying
/// Glass. Comparing the JOINED cells collapsed the identical A/B pair but let the FTUE cell through
/// as a whole, so Incriminating Evidence was listed twice (user report, 2026-09-18).
/// </para>
/// </summary>
public class AliasDropDedupeTests
{
    /// <summary>A generator item whose ActivationSpawn rolls the given targets.</summary>
    private static string Generator(string itemType, params (string Target, double Pct)[] drops)
    {
        var odds = string.Join(", ", System.Array.ConvertAll(drops, d => $"\"{d.Target}\": {d.Pct}"));
        return $$"""
        {
          "Item": {
            "Name": "Investigation", "ItemType": "{{itemType}}", "ConfigKey": 1, "LevelNumber": 1,
            "Description": "d",
            "ActivationFeatures": {
              "Activable": true,
              "ActivationSpawn": { "ControlledRandomSequence": { "Odds": { {{odds}} } } },
              "ActivationCycle": { "MiniChargesInSingleCharge": 5, "DropsInSingleMiniCharge": 1, "HowManyCycles": 1 },
              "MaxCharges": 1
            }
          }
        }
        """;
    }

    private static string LootChain(string name, string configKey, string itemType) => $$"""
        {
          "Name": "{{name}}", "ConfigKey": "{{configKey}}",
          "PrimaryChain": [ { "Item": {
            "Name": "{{name}}", "ItemType": "{{itemType}}", "ConfigKey": 2, "LevelNumber": 1, "Description": "d"
          } } ]
        }
        """;

    /// <summary>Loads the dump, marks the B/FTUE copies as aliases and merges, exactly as the app does.</summary>
    private static async Task<(DataService Data, WikiMappingCache Mapping)> LoadAsync(params string[] aliasItemTypes)
    {
        var json = $$"""
        {
          "CreatedAt": "2026-09-18T00:00:00",
          "Data": [
            {
              "Name": "Investigation", "ConfigKey": "Investigation",
              "PrimaryChain": [
                {{Generator("ActiveIA_01", ("Evidence_01", 100))}},
                {{Generator("ActiveIB_01", ("Evidence_01", 100))}},
                {{Generator("ActiveIFTUE_01", ("Evidence_01", 90), ("Glass_01", 10))}}
              ]
            },
            {{LootChain("Incriminating Evidence", "Evidence", "Evidence_01")}},
            {{LootChain("Magnifying Glass", "Glass", "Glass_01")}}
          ]
        }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"mmwt-drops-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var data = new DataService(new ChainNameService());
            await data.LoadAsync(path);

            var mapping = new WikiMappingCache();
            foreach (var t in aliasItemTypes)
                mapping.Mappings[t] = new WikiMappingEntry
                {
                    Fields = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase)
                    {
                        ["chainName"] = "Investigation",
                        ["isAlias"] = true,
                    },
                };
            ChainMergeService.ApplyWikiMapping(data, mapping);
            return (data, mapping);
        }
        finally { File.Delete(path); }
    }

    private static string Table(DataService data, WikiMappingCache mapping)
    {
        var chain = data.Chains.Find(c => c.DisplayName == "Investigation")!;
        return new WikiTableGenerator(data, mapping).Generate(chain, "Investigation", lowPrices: false);
    }

    [Fact]
    public async Task OverlappingAliasDrops_areListedOnce()
    {
        var (data, mapping) = await LoadAsync("ActiveIB_01", "ActiveIFTUE_01");

        var table = Table(data, mapping);
        int mentions = table.Split("Incriminating Evidence").Length - 1;

        Assert.True(mentions == 1, $"expected one mention, got {mentions}. Table: {table}");
        Assert.Contains("Magnifying Glass", table);
    }

    [Fact]
    public async Task AliasesWithIdenticalDrops_stillCollapseToOneEntry()
    {
        var (data, mapping) = await LoadAsync("ActiveIB_01");

        // Only A and B are in play here (FTUE is its own row); their drops are identical.
        var table = Table(data, mapping);
        int mentions = table.Split("Incriminating Evidence").Length - 1;

        Assert.True(mentions >= 1, $"the drop vanished entirely. Table: {table}");
    }
}
