using MergeMansionWikiTools.Views;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// ChainBrowser "Set as/Remove alias|variant" surgical edits of Module:Datatable/Items/Mapping.
/// The flag regex only consumes a PRECEDING comma, so entries with the flag as their FIRST
/// field ({isAlias = true, chainName = ...}) used to end up as "{, chainName = ...}" — invalid
/// Lua that Fandom's Scribunto validation rejected on save (user report 2026-07-10).
/// </summary>
public class MappingFlagToggleTests
{
    private const string Header = "local p = {}\np.multinameMappings = {\n";
    private const string Footer = "}\n\nreturn p";

    private static string Module(params string[] entries)
        => Header + string.Join("\n", entries) + "\n" + Footer;

    [Fact]
    public void RemoveFlag_WhenFlagIsFirstField_DoesNotLeaveLeadingComma()
    {
        var lua = Module("\t[\"DailyTasksV2ChestCards1_01\"] = {isAlias = true, chainName = \"Daily Trades Chest\", level = 1},");

        var result = ChainBrowserPage.ApplyFlagToggle(lua, "DailyTasksV2ChestCards1_01", "isAlias", remove: true);

        Assert.Contains("[\"DailyTasksV2ChestCards1_01\"] = {chainName = \"Daily Trades Chest\", level = 1},", result);
        Assert.DoesNotContain("{,", result);
        Assert.DoesNotContain("isAlias", result);
    }

    [Fact]
    public void RemoveFlag_WhenFlagIsMiddleOrLastField_RemovesCleanly()
    {
        var lua = Module("\t[\"X_01\"] = {chainName = \"X\", isAlias = true, isVariant = true},");

        var result = ChainBrowserPage.ApplyFlagToggle(lua, "X_01", "isAlias", remove: true);

        Assert.Contains("[\"X_01\"] = {chainName = \"X\", isVariant = true},", result);
    }

    [Fact]
    public void RemoveFlag_WhenFlagIsOnlyField_DropsWholeEntry()
    {
        var lua = Module(
            "\t[\"X_01\"] = {isAlias = true},",
            "\t[\"Y_01\"] = {chainName = \"Y\"},");

        var result = ChainBrowserPage.ApplyFlagToggle(lua, "X_01", "isAlias", remove: true);

        Assert.DoesNotContain("X_01", result);
        Assert.Contains("Y_01", result);
    }

    [Fact]
    public void SetFlag_WhenFlagAlreadyFirstField_DoesNotLeaveLeadingComma()
    {
        var lua = Module("\t[\"X_01\"] = {isVariant = false, chainName = \"X\"},");

        var result = ChainBrowserPage.ApplyFlagToggle(lua, "X_01", "isVariant", remove: false);

        Assert.Contains("[\"X_01\"] = {isVariant = true, chainName = \"X\"},", result);
        Assert.DoesNotContain("{,", result);
    }

    [Fact]
    public void SetFlag_OnEntryWithoutFlag_AppendsWithComma()
    {
        var lua = Module("\t[\"X_01\"] = {chainName = \"X\", level = 2},");

        var result = ChainBrowserPage.ApplyFlagToggle(lua, "X_01", "isVariant", remove: false);

        Assert.Contains("[\"X_01\"] = {chainName = \"X\", level = 2, isVariant = true},", result);
    }

    [Fact]
    public void SetFlag_WhenIsVariantIsStringLabel_PreservesLabel_NoDuplicate()
    {
        // Bug: re-toggling "set variant" on an item that already has a NAMED label wiped the label —
        // the bool-only regex missed the string, so a second `isVariant = true` was appended (dup key,
        // Lua keeps the last → label lost). Must be a no-op that keeps `isVariant = "Autumn"`.
        var lua = Module("\t[\"X_05\"] = {chainName = \"X\", isVariant = \"Autumn\", groupOdds = true, variantOrder = 3},");

        var result = ChainBrowserPage.ApplyFlagToggle(lua, "X_05", "isVariant", remove: false);

        Assert.Contains("isVariant = \"Autumn\"", result);
        Assert.DoesNotContain("isVariant = true", result);          // no duplicate bool appended
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(result, "isVariant").Count);
    }

    [Fact]
    public void RemoveFlag_StripsStringLabelVariant()
    {
        var lua = Module("\t[\"X_05\"] = {chainName = \"X\", isVariant = \"Autumn\"},");

        var result = ChainBrowserPage.ApplyFlagToggle(lua, "X_05", "isVariant", remove: true);

        Assert.DoesNotContain("isVariant", result);
        Assert.Contains("[\"X_05\"] = {chainName = \"X\"},", result);
    }

    // ── SetVariantLabel: named variants (isVariant = "Spring") ──

    [Fact]
    public void SetVariantLabel_NewEntry_WritesQuotedString()
    {
        var lua = Module("\t[\"Other_01\"] = {isAlias = true},");

        var result = ChainBrowserPage.SetVariantLabel(lua, "LS_Summer_BasicCamera_01", "Summer");

        Assert.Contains("[\"LS_Summer_BasicCamera_01\"] = {isVariant = \"Summer\"},", result);
    }

    [Fact]
    public void SetVariantLabel_UpgradesBoolToLabel()
    {
        var lua = Module("\t[\"LS_Summer_BasicCamera_01\"] = {isVariant = true},");

        var result = ChainBrowserPage.SetVariantLabel(lua, "LS_Summer_BasicCamera_01", "Summer");

        Assert.Contains("[\"LS_Summer_BasicCamera_01\"] = {isVariant = \"Summer\"},", result);
        Assert.DoesNotContain("isVariant = true", result);
    }

    [Fact]
    public void SetVariantLabel_ReplacesExistingLabel()
    {
        var lua = Module("\t[\"X_01\"] = {isVariant = \"Sprnig\", chainName = \"X\"},");

        var result = ChainBrowserPage.SetVariantLabel(lua, "X_01", "Spring");

        Assert.Contains("[\"X_01\"] = {isVariant = \"Spring\", chainName = \"X\"},", result);
        Assert.DoesNotContain("Sprnig", result);
        Assert.DoesNotContain("{,", result);
    }

    [Fact]
    public void SetVariantLabel_EmptyLabel_WritesBareTrue()
    {
        var lua = Module("\t[\"X_01\"] = {isVariant = \"Spring\"},");

        var result = ChainBrowserPage.SetVariantLabel(lua, "X_01", "");

        Assert.Contains("[\"X_01\"] = {isVariant = true},", result);
    }

    [Fact]
    public void SetVariantLabel_EscapesQuotesInLabel()
    {
        var lua = Module("\t[\"X_01\"] = {isVariant = true},");

        var result = ChainBrowserPage.SetVariantLabel(lua, "X_01", "The \"Big\" One");

        Assert.Contains("isVariant = \"The \\\"Big\\\" One\"", result);
    }

    // ── SetVariantField: generic numeric/bool fields (variantOrder, groupOdds) ──

    [Fact]
    public void SetVariantField_AddsNumericField_WhenMissing()
    {
        var lua = Module("\t[\"X_01\"] = {chainName = \"X\", isVariant = \"Spring\"},");
        var result = ChainBrowserPage.SetVariantField(lua, "X_01", "variantOrder", "2");
        Assert.Contains("variantOrder = 2", result);
        Assert.Contains("isVariant = \"Spring\"", result);
    }

    [Fact]
    public void SetVariantField_ReplacesExistingValue()
    {
        var lua = Module("\t[\"X_01\"] = {variantOrder = 1, chainName = \"X\"},");
        var result = ChainBrowserPage.SetVariantField(lua, "X_01", "variantOrder", "5");
        Assert.Contains("variantOrder = 5", result);
        Assert.DoesNotContain("variantOrder = 1", result);
    }

    [Fact]
    public void SetVariantField_RemovesField_WhenValueEmpty_NoLeadingComma()
    {
        var lua = Module("\t[\"X_01\"] = {variantOrder = 3, chainName = \"X\"},");
        var result = ChainBrowserPage.SetVariantField(lua, "X_01", "variantOrder", "");
        Assert.Contains("[\"X_01\"] = {chainName = \"X\"},", result);
        Assert.DoesNotContain("{,", result);
        Assert.DoesNotContain("variantOrder", result);
    }

    [Fact]
    public void SetVariantField_BoolTrue_CreatesEntry_WhenItemMissing()
    {
        var lua = Module("\t[\"Y_01\"] = {chainName = \"Y\"},");
        var result = ChainBrowserPage.SetVariantField(lua, "X_01", "groupOdds", "true");
        Assert.Contains("[\"X_01\"] = {groupOdds = true}", result);
    }
}
