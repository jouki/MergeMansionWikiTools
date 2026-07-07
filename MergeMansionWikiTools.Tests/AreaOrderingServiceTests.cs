using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Services;
using Models = MergeMansionWikiTools.Models;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for stale/renamed area detection in Module:Datatable/Areas/Mapping and for the
/// module patcher's rename/delete passes. Real-world case: the 26.05.01 dump carried the
/// unresolved name "HotspotTitle_FirstFloorPantry" (missing localization) which the app
/// prettified to "First Floor Pantry"; the 26.06.01 dump brought the real localization
/// "Pantry" for the SAME AreaId "FirstFloorPantry" → the mapping row must be renamed
/// (keeping its orderingIndex), not duplicated.
/// </summary>
public class AreaOrderingServiceTests
{
    private static AreaUnlockInfo Area(string name, string areaId)
        => new(name, areaId, null, null, null, null, false);

    // ── FallbackNameFromAreaId ──

    [Theory]
    [InlineData("FirstFloorPantry", "First Floor Pantry")]
    [InlineData("FactoryOffice", "Factory Office")]
    [InlineData("Pantry", "Pantry")]
    public void FallbackNameFromAreaId_splits_camel_case(string areaId, string expected)
        => Assert.Equal(expected, AreaOrderingService.FallbackNameFromAreaId(areaId));

    // ── DetectStaleEntries ──

    [Fact]
    public void Detects_rename_of_active_entry_via_area_id_fallback()
    {
        var areas = new List<AreaUnlockInfo> { Area("Pantry", "FirstFloorPantry") };
        var active = new Dictionary<string, double> { ["First Floor Pantry"] = 60 };

        var (renames, deletes) = AreaOrderingService.DetectStaleEntries(
            areas, active, new List<RemovedCommentedEntry>());

        var r = Assert.Single(renames);
        Assert.Equal("First Floor Pantry", r.OldName);
        Assert.Equal("Pantry", r.NewName);
        Assert.Equal(60, r.OrderingIndex);
        Assert.False(r.IsCommented);
        Assert.Empty(deletes);
    }

    [Fact]
    public void Unmatched_stale_active_entry_is_a_delete()
    {
        var areas = new List<AreaUnlockInfo> { Area("Kitchen", "FirstFloorKitchen") };
        var active = new Dictionary<string, double> { ["Kitchen"] = 59, ["Ghost Room"] = 60 };

        var (renames, deletes) = AreaOrderingService.DetectStaleEntries(
            areas, active, new List<RemovedCommentedEntry>());

        Assert.Empty(renames);
        var d = Assert.Single(deletes);
        Assert.Equal("Ghost Room", d.Name);
        Assert.Equal(60, d.OrderingIndex);
        Assert.False(d.IsCommented);
    }

    [Fact]
    public void Detects_rename_and_delete_of_commented_entries()
    {
        var areas = new List<AreaUnlockInfo> { Area("Parlor", "FirstFloorParlor") };
        var commented = new List<RemovedCommentedEntry>
        {
            new("First Floor Parlor", 61),  // rename → "Parlor"
            new("Old Cellar", 62),          // gone from data → delete
        };

        var (renames, deletes) = AreaOrderingService.DetectStaleEntries(
            areas, new Dictionary<string, double>(), commented);

        var r = Assert.Single(renames);
        Assert.Equal("Parlor", r.NewName);
        Assert.True(r.IsCommented);
        var d = Assert.Single(deletes);
        Assert.Equal("Old Cellar", d.Name);
        Assert.True(d.IsCommented);
    }

    [Fact]
    public void Entries_matching_current_names_or_skip_list_are_untouched()
    {
        var areas = new List<AreaUnlockInfo> { Area("Kitchen", "FirstFloorKitchen") };
        var active = new Dictionary<string, double>
        {
            ["Kitchen"] = 59,
            ["Maddie Meets Mansion"] = 1, // SkipNames
        };

        var (renames, deletes) = AreaOrderingService.DetectStaleEntries(
            areas, active, new List<RemovedCommentedEntry>());

        Assert.Empty(renames);
        Assert.Empty(deletes);
    }

    [Fact]
    public void Rename_target_already_mapped_under_new_name_is_not_a_rename()
    {
        // "Pantry" is already in the mapping → the stale "First Floor Pantry" has no free
        // target and must fall back to delete (would otherwise create a duplicate key).
        var areas = new List<AreaUnlockInfo> { Area("Pantry", "FirstFloorPantry") };
        var active = new Dictionary<string, double>
        {
            ["Pantry"] = 60,
            ["First Floor Pantry"] = 61,
        };

        var (renames, deletes) = AreaOrderingService.DetectStaleEntries(
            areas, active, new List<RemovedCommentedEntry>());

        Assert.Empty(renames);
        var d = Assert.Single(deletes);
        Assert.Equal("First Floor Pantry", d.Name);
    }

    // ── PatchModuleContent: renames + deletes ──

    private const string Module = "local p = {\n" +
        "\t[\"Kitchen\"]            = {orderingIndex = 59},\n" +
        "\t[\"First Floor Pantry\"] = {orderingIndex = 60, right = -14, bot = 6},\n" +
        "\t--[\"Factory Office\"]   = {orderingIndex = 61},\n" +
        "\t--[\"Old Cellar\"]       = {orderingIndex = 62},\n" +
        "}\n" +
        "return p";

    [Fact]
    public void Patch_renames_active_row_in_place_preserving_extras()
    {
        var renames = new List<RenamedEntry> { new("First Floor Pantry", "Pantry", 60, false) };
        var patched = AreaOrderingService.PatchModuleContent(
            Module, new List<DeducedEntry>(), renames, new List<StaleEntry>());

        Assert.DoesNotContain("First Floor Pantry", patched);
        Assert.Contains("[\"Pantry\"]", patched);
        // extras (right/bot) and index survive untouched
        Assert.Contains("{orderingIndex = 60, right = -14, bot = 6},", patched);
        // commented rows untouched
        Assert.Contains("--[\"Factory Office\"]", patched);
    }

    [Fact]
    public void Patch_deletes_stale_rows_active_and_commented()
    {
        var deletes = new List<StaleEntry>
        {
            new("Old Cellar", 62, true),
            new("First Floor Pantry", 60, false),
        };
        var patched = AreaOrderingService.PatchModuleContent(
            Module, new List<DeducedEntry>(), new List<RenamedEntry>(), deletes);

        Assert.DoesNotContain("Old Cellar", patched);
        Assert.DoesNotContain("First Floor Pantry", patched);
        Assert.Contains("[\"Kitchen\"]", patched);
        Assert.Contains("--[\"Factory Office\"]", patched);
    }

    [Fact]
    public void Patch_combines_rename_with_new_additions()
    {
        var renames = new List<RenamedEntry> { new("First Floor Pantry", "Pantry", 60, false) };
        var adds = new List<DeducedEntry> { new("Parlor", 62, true) };
        var patched = AreaOrderingService.PatchModuleContent(
            Module, adds, renames, new List<StaleEntry>());

        Assert.Contains("[\"Pantry\"]", patched);
        Assert.Contains("--[\"Parlor\"]", patched);
        Assert.Contains("orderingIndex = 62", patched);
        // addition lands after the last active row (renamed Pantry row), before return p
        int pantryIdx = patched.IndexOf("[\"Pantry\"]", System.StringComparison.Ordinal);
        int parlorIdx = patched.IndexOf("--[\"Parlor\"]", System.StringComparison.Ordinal);
        Assert.True(parlorIdx > pantryIdx);
    }

    [Fact]
    public void Patch_without_changes_returns_original()
    {
        var patched = AreaOrderingService.PatchModuleContent(
            Module, new List<DeducedEntry>(), new List<RenamedEntry>(), new List<StaleEntry>());
        Assert.Equal(Module, patched);
    }

    [Fact]
    public void Patch_inserts_additions_after_trailing_commented_rows()
    {
        // New indices are always > existing max, so the file stays orderingIndex-sorted only
        // when additions land AFTER trailing in-prep commented rows (61, 62), not before them.
        var adds = new List<DeducedEntry> { new("Parlor", 63, true) };
        var patched = AreaOrderingService.PatchModuleContent(
            Module, adds, new List<RenamedEntry>(), new List<StaleEntry>());

        int cellarIdx = patched.IndexOf("--[\"Old Cellar\"]", System.StringComparison.Ordinal);
        int parlorIdx = patched.IndexOf("--[\"Parlor\"]", System.StringComparison.Ordinal);
        Assert.True(cellarIdx >= 0 && parlorIdx > cellarIdx);
    }

    // ── LoadFromAreasJsonAsync: display-name trim ──

    [Fact]
    public async System.Threading.Tasks.Task Load_trims_whitespace_in_display_names()
    {
        // Real case: 26.06.01 areas.json carries "Name": " Walk-in Closet" (leading space) —
        // without a trim the mapping row "Walk-in Closet" reads as stale and gets a bogus delete.
        var tmp = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(tmp,
                "{\"Data\":[{\"Name\":\" Walk-in Closet\",\"AreaId\":\"WalkInCloset\"}]}");
            var areas = await AreaOrderingService.LoadFromAreasJsonAsync(tmp);
            var a = Assert.Single(areas);
            Assert.Equal("Walk-in Closet", a.Name);
        }
        finally { System.IO.File.Delete(tmp); }
    }

    // ── BuildDiffPreview ──

    [Fact]
    public void DiffPreview_orders_by_module_and_includes_unchanged_context_between_changes()
    {
        var renames = new List<RenamedEntry> { new("First Floor Pantry", "Pantry", 60, false) };
        var adds = new List<DeducedEntry> { new("Parlor", 63, true), new("Atelier", 64, true) };

        var diff = AreaOrderingService.BuildDiffPreview(
            Module, adds, renames, new List<StaleEntry>());

        // Window: starts at the renamed row (60), NOT at Kitchen (59) or the table header
        Assert.DoesNotContain(diff, d => d.Text.Contains("Kitchen"));
        Assert.DoesNotContain(diff, d => d.Text.Contains("local p"));

        // Order: -old(60), +new(60), ctx --Factory Office(61), ctx --Old Cellar(62), +adds
        Assert.Equal(Models.DiffLineType.Removed, diff[0].Type);
        Assert.Contains("First Floor Pantry", diff[0].Text);
        Assert.Equal(Models.DiffLineType.Added, diff[1].Type);
        Assert.Contains("[\"Pantry\"]", diff[1].Text);
        var factoryOffice = diff.Single(d => d.Text.Contains("Factory Office"));
        Assert.Equal(Models.DiffLineType.Match, factoryOffice.Type);
        var parlor = diff.Single(d => d.Text.Contains("Parlor"));
        Assert.Equal(Models.DiffLineType.Added, parlor.Type);
        Assert.True(diff.IndexOf(parlor) > diff.IndexOf(factoryOffice));
    }

    [Fact]
    public void DiffPreview_delete_only_shows_just_that_row()
    {
        var deletes = new List<StaleEntry> { new("Old Cellar", 62, true) };
        var diff = AreaOrderingService.BuildDiffPreview(
            Module, new List<DeducedEntry>(), new List<RenamedEntry>(), deletes);

        var d = Assert.Single(diff);
        Assert.Equal(Models.DiffLineType.Removed, d.Type);
        Assert.Contains("Old Cellar", d.Text);
    }
}
