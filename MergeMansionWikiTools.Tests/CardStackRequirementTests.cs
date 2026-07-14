using System.Text.Json;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Locks the contract between the dumper's resolved CardStackRef output and AreasService's
/// requirement extraction. Minigame tasks (Spy Room, Library, Speakeasy, Lounge) store their
/// requirement as a CardStack reference; the dumper (MetaAreaSerializer) resolves it to the
/// inline shape { Cards: [{ ItemDef: { ItemType }, Row }] }, and AreasService groups the cards
/// per Row and pair-cancels (identical pairs in a row cancel; only an odd count contributes 1).
/// Without the resolved cards the requirement column renders empty — the bug this guards against.
/// </summary>
public class CardStackRequirementTests
{
    private static JsonElement Hotspot(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void CardStackRef_resolvedCards_extractsRequirements_withPairCancellation()
    {
        // Row 0: SpyCard_01 x2 (cancel), SpyCard_02 x1 (odd -> 1). Row 1: SpyCard_03 x1.
        var hs = Hotspot(@"{
            ""Id"": ""SpyRoomTask"", ""Type"": ""CardStack"",
            ""RequirementsList"": [ { ""CardStack"": ""SpyRoom3"" } ],
            ""CardStackRef"": { ""Cards"": [
                { ""ItemDef"": { ""ItemType"": ""SpyCard_01"" }, ""Row"": 0 },
                { ""ItemDef"": { ""ItemType"": ""SpyCard_01"" }, ""Row"": 0 },
                { ""ItemDef"": { ""ItemType"": ""SpyCard_02"" }, ""Row"": 0 },
                { ""ItemDef"": { ""ItemType"": ""SpyCard_03"" }, ""Row"": 1 }
            ] }
        }");

        var reqs = AreasService.ParseHotspotRequirements(hs);

        Assert.False(reqs.ContainsKey("SpyCard_01")); // even count in the row cancels out
        Assert.Equal(1, reqs["SpyCard_02"]);
        Assert.Equal(1, reqs["SpyCard_03"]);
    }

    [Fact]
    public void CardStackRef_bareStringReference_yieldsNoRequirements()
    {
        // The pre-fix (broken) dump shape: CardStackRef is a bare id string, no cards -> empty.
        var hs = Hotspot(@"{
            ""Id"": ""SpyRoomTask"", ""Type"": ""CardStack"",
            ""RequirementsList"": [ { ""CardStack"": ""SpyRoom3"" } ],
            ""CardStackRef"": ""SpyRoom3""
        }");

        var reqs = AreasService.ParseHotspotRequirements(hs);

        Assert.Empty(reqs);
    }

    [Fact]
    public void ItemAcquired_requirement_stillParsed()
    {
        // Regression guard: ordinary item requirements are unaffected.
        var hs = Hotspot(@"{
            ""Id"": ""RegularTask"",
            ""RequirementsList"": [ { ""ItemAcquired"": [ { ""ItemRef"": ""SeedBagEmpty_01"", ""Requirement"": 3 } ] } ]
        }");

        var reqs = AreasService.ParseHotspotRequirements(hs);

        Assert.Equal(3, reqs["SeedBagEmpty_01"]);
    }
}
