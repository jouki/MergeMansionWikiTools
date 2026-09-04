using System.Collections.Generic;
using System.IO;
using GameLogic;
using merge_mansion_dumper.Dumper.Json;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// The runtime HotspotId name map must make values the compiled enum doesn't know (new game
/// version) behave like defined members everywhere the dumper looks: IsKnown, Resolve and the
/// JSON converter that writes task "Id" fields. Tests run sequentially within the class since
/// the registry is static.
/// </summary>
[Collection("HotspotIdNames")]
public class HotspotIdNamesTests
{
    private const int UnknownValue = 987654321; // not a compiled enum member

    private static string Serialize(object value)
    {
        var settings = new JsonSerializerSettings { Converters = { new HotspotAwareStringEnumConverter() } };
        return JsonConvert.SerializeObject(value, settings);
    }

    [Fact]
    public void Unknown_value_without_map_is_integer_and_not_known()
    {
        HotspotIdNames.Clear();
        var id = (HotspotId)UnknownValue;

        Assert.False(HotspotIdNames.IsKnown(id));
        Assert.Equal(UnknownValue.ToString(), HotspotIdNames.Resolve(id));
        Assert.Equal(UnknownValue.ToString(), Serialize(id)); // stock StringEnumConverter behaviour
    }

    [Fact]
    public void Loaded_map_names_unknown_value_everywhere()
    {
        HotspotIdNames.Load(new[] { new KeyValuePair<int, string>(UnknownValue, "FirstFloorPantryPrepTableCleanFloor") }, "test");
        try
        {
            var id = (HotspotId)UnknownValue;
            Assert.True(HotspotIdNames.IsKnown(id));
            Assert.Equal("FirstFloorPantryPrepTableCleanFloor", HotspotIdNames.Resolve(id));
            Assert.Equal("\"FirstFloorPantryPrepTableCleanFloor\"", Serialize(id));
            Assert.Equal("\"FirstFloorPantryPrepTableCleanFloor\"", Serialize((HotspotId?)id));

            // Compiled members keep working (map doesn't hide the enum) and other enums are untouched
            Assert.True(HotspotIdNames.IsKnown(HotspotId.None));
            Assert.Equal("None", HotspotIdNames.Resolve(HotspotId.None));
            Assert.Equal("\"Indented\"", Serialize(Formatting.Indented));
        }
        finally { HotspotIdNames.Clear(); }
    }
}
