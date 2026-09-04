using System.IO;
using MergeMansionWikiTools.Services;
using Xunit;
using Xunit.Abstractions;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Integration check for the PoolTag → prefab mapping against real game files in the local
/// workspace (26.07.01 switched PoolConfig from a path-based <c>prefabRef</c> to an Addressables
/// <c>prefabAssetReference</c> GUID, which silently produced 0 mappings → no chain images).
/// Skipped (passes vacuously) when the workspace files are not present on this machine.
/// </summary>
public class PoolTagMappingIntegrationTests
{
    private const string Workspace = @"D:\_BACKUP_2.0\Adobe Photoshop - Savy\Merge Mansion\APKs";
    private readonly ITestOutputHelper _out;
    public PoolTagMappingIntegrationTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("26.06.01", 1300)] // path-based prefabRef
    [InlineData("26.07.01", 1300)] // GUID-based prefabAssetReference
    public void Pool_mapping_resolves_prefab_names_for_real_bundle(string version, int minEntries)
    {
        var bundleDir = Path.Combine(Workspace, version, "Game Files", "APK");
        var tpk = Path.Combine(Workspace, "classdata.tpk");
        if (!File.Exists(Path.Combine(bundleDir, "startup_scenes_all.bundle")) || !File.Exists(tpk)
            || !File.Exists(Path.Combine(Workspace, version, "catalog.bin")))
        {
            _out.WriteLine($"SKIP: workspace files for {version} not present");
            return;
        }

        var map = AssetExtractionService.ExtractPoolTagMapping(bundleDir, tpk, Path.Combine(Workspace, version, "Export - PNGs"));

        Assert.True(map.Count >= minEntries, $"only {map.Count} pool tags resolved for {version}");
        Assert.Equal("AdventCalendar2023_Calendar", map["AdventCalendar2023_Calendar"]);
        if (version == "26.07.01")
        {
            // Prefab FILE is FirstFloorPantry_*.prefab but the catalog ADDRESS (= skeleton/texture) is Pantry_*
            Assert.Equal("Pantry_Fruit", map["FirstFloorPantry_Fruit"]);
            Assert.Equal("Pantry_Jam", map["FirstFloorPantry_Jam"]);
            Assert.Equal("Pantry_Spices", map["FirstFloorPantry_Spices"]);
        }
    }
}
