using System.Collections.Generic;
using System.Linq;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Views;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for bulk level editing in Item Chains (v0.24.70): the mapping writer
/// (<see cref="ChainBrowserPage.ApplyLevel"/>) and the same-level collision rule
/// (<see cref="MoveItemsDialog.EvaluateLevelCollision"/>).
/// <para>
/// Both exist because a merge chain's <c>level</c> is NOT a unique key: aliases and variants are
/// parallel forms of one merge stage and share a level on purpose.
/// </para>
/// </summary>
public class ItemLevelEditingTests
{
    private const string Header = "return {\n";
    private const string Footer = "}\n";

    private static string Mapping(params string[] entries) =>
        Header + string.Join("", entries.Select(e => "\t" + e + "\n")) + Footer;

    private static ParsedItem Item(string type, int level, bool alias = false, bool variant = false) =>
        new() { ItemType = type, Name = type, Level = level, IsAlias = alias, IsVariant = variant };

    // ── ApplyLevel ────────────────────────────────────────────────────

    [Fact]
    public void ApplyLevel_replacesAnExistingOverride_andLeavesOtherFieldsAlone()
    {
        var lua = Mapping("[\"Widget_01\"] = {chainName = \"Widget\", level = 2, isVariant = true},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 5, gameLevel: 1);

        Assert.Contains("level = 5", result);
        Assert.DoesNotContain("level = 2", result);
        Assert.Contains("chainName = \"Widget\"", result);
        Assert.Contains("isVariant = true", result);
    }

    [Fact]
    public void ApplyLevel_appendsToAnEntryThatHasNoLevelYet()
    {
        var lua = Mapping("[\"Widget_01\"] = {chainName = \"Widget\"},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 3, gameLevel: 1);

        Assert.Contains("[\"Widget_01\"] = {chainName = \"Widget\", level = 3}", result);
    }

    [Fact]
    public void ApplyLevel_createsTheEntryWhenTheItemIsNotInTheMappingYet()
    {
        var lua = Mapping("[\"Other_01\"] = {chainName = \"Other\"},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 4, gameLevel: 1);

        Assert.Contains("[\"Widget_01\"] = {level = 4},", result);
        Assert.Contains("[\"Other_01\"]", result);   // existing entries survive
        Assert.EndsWith("}\n", result);
    }

    [Fact]
    public void ApplyLevel_neverWritesChainName_becauseTheChainComesFromGameData()
    {
        var lua = Mapping("[\"Other_01\"] = {chainName = \"Other\"},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 4, gameLevel: 1);

        Assert.DoesNotContain("[\"Widget_01\"] = {chainName", result);
    }

    [Fact]
    public void ApplyLevel_wantingTheGameLevel_dropsTheOverrideInsteadOfWritingARedundantOne()
    {
        var lua = Mapping("[\"Widget_01\"] = {chainName = \"Widget\", level = 7},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 2, gameLevel: 2);

        Assert.DoesNotContain("level", result);
        Assert.Contains("[\"Widget_01\"] = {chainName = \"Widget\"}", result);
    }

    [Fact]
    public void ApplyLevel_droppingTheOnlyFieldRemovesTheWholeEntry()
    {
        var lua = Mapping("[\"Widget_01\"] = {level = 7},", "[\"Other_01\"] = {chainName = \"Other\"},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 2, gameLevel: 2);

        Assert.DoesNotContain("Widget_01", result);
        Assert.Contains("Other_01", result);
    }

    [Fact]
    public void ApplyLevel_droppingALeadingLevelLeavesValidLua_notABodyStartingWithAComma()
    {
        var lua = Mapping("[\"Widget_01\"] = {level = 7, chainName = \"Widget\"},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 2, gameLevel: 2);

        Assert.DoesNotContain("{,", result);
        Assert.Contains("[\"Widget_01\"] = {chainName = \"Widget\"}", result);
    }

    [Fact]
    public void ApplyLevel_itemMissingFromMapping_andWantingTheGameLevel_isANoOp()
    {
        var lua = Mapping("[\"Other_01\"] = {chainName = \"Other\"},");

        Assert.Equal(lua, ChainBrowserPage.ApplyLevel(lua, "Widget_01", 3, gameLevel: 3));
    }

    [Fact]
    public void ApplyLevel_unknownGameLevel_alwaysWritesTheOverride()
    {
        // GameLevelOf returns -1 when the raw level table has no entry — an override must then be
        // written unconditionally rather than guessed away.
        var lua = Mapping("[\"Widget_01\"] = {chainName = \"Widget\"},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 1, gameLevel: -1);

        Assert.Contains("level = 1", result);
    }

    [Fact]
    public void ApplyLevel_appliedItemByItem_accumulatesWithoutDuplicatingEntries()
    {
        var lua = Mapping("[\"Widget_01\"] = {chainName = \"Widget\"},");

        var result = ChainBrowserPage.ApplyLevel(lua, "Widget_01", 2, gameLevel: 1);
        result = ChainBrowserPage.ApplyLevel(result, "Widget_02", 3, gameLevel: 1);
        result = ChainBrowserPage.ApplyLevel(result, "Widget_01", 4, gameLevel: 1);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, @"\[""Widget_01""\]"));
        Assert.Contains("level = 4", result);
        Assert.DoesNotContain("level = 2", result);
        Assert.Contains("[\"Widget_02\"] = {level = 3},", result);
    }

    // ── SetLevelsDialog.LevelRow (the per-item editor row) ────────────

    [Fact]
    public void LevelRow_startsAtTheItemsCurrentLevel_andReportsNoChange()
    {
        var row = new SetLevelsDialog.LevelRow(Item("Widget_01", 3));

        Assert.Equal(3, row.Level);
        Assert.Equal(3d, row.LevelValue);
        Assert.Equal("", row.ChangeLabel);
    }

    [Fact]
    public void LevelRow_editedLevel_showsTheBeforeAfterHint()
    {
        var row = new SetLevelsDialog.LevelRow(Item("Widget_01", 1));

        row.Level = 4;

        Assert.Equal("1 → 4", row.ChangeLabel);
        Assert.Equal(1, row.OriginalLevel);
    }

    [Fact]
    public void LevelRow_nullFromAnEmptiedNumberBox_keepsTheLastGoodLevel()
    {
        // NumberBox.Value is double?; an emptied box pushes null. Swallowing it is what stops the
        // row from silently losing the level while the box looks blank.
        var row = new SetLevelsDialog.LevelRow(Item("Widget_01", 2));

        row.LevelValue = null;

        Assert.Equal(2, row.Level);
    }

    [Fact]
    public void LevelRow_clampsToTheAllowedRange()
    {
        var row = new SetLevelsDialog.LevelRow(Item("Widget_01", 2));

        row.Level = 0;
        Assert.Equal(1, row.Level);

        row.Level = 500;
        Assert.Equal(99, row.Level);
    }

    [Fact]
    public void LevelRow_raisesChanged_soTheDialogSummaryCanFollow()
    {
        var row = new SetLevelsDialog.LevelRow(Item("Widget_01", 2));
        int fired = 0;
        row.Changed += () => fired++;

        row.Level = 3;
        row.Level = 3;   // same value — no event

        Assert.Equal(1, fired);
    }

    // ── EvaluateLevelCollision ────────────────────────────────────────

    [Fact]
    public void Collision_twoPlainItemsOnOneLevel_blocks()
    {
        var moving = Item("A_01", 1);
        var chain = new[] { moving, Item("B_02", 2) };

        var (blocks, collision, _) = MoveItemsDialog.EvaluateLevelCollision(chain, moving, 2, movingAsAlias: false);

        Assert.True(blocks);
        Assert.Equal("B_02", collision?.ItemType);
    }

    [Fact]
    public void Collision_movingItemIsAVariant_isAllowed()
    {
        // The reported case: a variant moved onto a level another variant already holds.
        var moving = Item("ActiveIA_01", 1, variant: true);
        var chain = new[] { moving, Item("ActiveIB_01", 2, variant: true) };

        var (blocks, collision, reason) = MoveItemsDialog.EvaluateLevelCollision(chain, moving, 2, movingAsAlias: false);

        Assert.False(blocks);
        Assert.Equal("ActiveIB_01", collision?.ItemType);
        Assert.Equal("both are aliases/variants/transients", reason);
    }

    [Fact]
    public void Collision_onlyTheOccupyingItemIsAVariant_isAllowed()
    {
        var moving = Item("A_01", 1);
        var chain = new[] { moving, Item("B_02", 2, variant: true) };

        var (blocks, _, reason) = MoveItemsDialog.EvaluateLevelCollision(chain, moving, 2, movingAsAlias: false);

        Assert.False(blocks);
        Assert.Equal("existing item is a variant", reason);
    }

    [Fact]
    public void Collision_onlyTheMovingItemIsAVariant_isAllowed()
    {
        var moving = Item("A_01", 1, variant: true);
        var chain = new[] { moving, Item("B_02", 2) };

        var (blocks, _, reason) = MoveItemsDialog.EvaluateLevelCollision(chain, moving, 2, movingAsAlias: false);

        Assert.False(blocks);
        Assert.Equal("moving item is a variant", reason);
    }

    [Fact]
    public void Collision_aliasStillExempts_asBefore()
    {
        var moving = Item("A_01", 1);
        var chain = new[] { moving, Item("B_02", 2, alias: true) };

        var (blocksTarget, _, targetReason) = MoveItemsDialog.EvaluateLevelCollision(chain, moving, 2, movingAsAlias: false);
        var (blocksSource, _, sourceReason) = MoveItemsDialog.EvaluateLevelCollision(
            new[] { moving, Item("C_02", 2) }, moving, 2, movingAsAlias: true);

        Assert.False(blocksTarget);
        Assert.Equal("existing item is an alias", targetReason);
        Assert.False(blocksSource);
        Assert.Equal("moving as alias", sourceReason);
    }

    [Fact]
    public void Collision_aRealClashBehindAVariant_isStillReported()
    {
        // Two items already on level 2: one variant (harmless) and one plain (a real clash). The
        // plain one must win, otherwise the variant hides it.
        var moving = Item("A_01", 1);
        var chain = new List<ParsedItem> { moving, Item("Variant_02", 2, variant: true), Item("Plain_02", 2) };

        var (blocks, collision, _) = MoveItemsDialog.EvaluateLevelCollision(chain, moving, 2, movingAsAlias: false);

        Assert.True(blocks);
        Assert.Equal("Plain_02", collision?.ItemType);
    }

    [Fact]
    public void Collision_emptyLevel_hasNothingToReport()
    {
        var moving = Item("A_01", 1);
        var chain = new[] { moving, Item("B_02", 2) };

        var (blocks, collision, _) = MoveItemsDialog.EvaluateLevelCollision(chain, moving, 3, movingAsAlias: false);

        Assert.False(blocks);
        Assert.Null(collision);
    }

    [Fact]
    public void Collision_theItemItself_neverCountsAsItsOwnCollision()
    {
        var moving = Item("A_02", 2);
        var chain = new[] { moving };

        var (blocks, collision, _) = MoveItemsDialog.EvaluateLevelCollision(chain, moving, 2, movingAsAlias: false);

        Assert.False(blocks);
        Assert.Null(collision);
    }

    [Fact]
    public void Collision_aTransientStage_isExemptLikeAnAliasOrVariant()
    {
        // A transient shares a level on purpose (the 3-second unlocked house sits beside the locked
        // one), so it must not be reported as a mapping mistake — v0.24.82.
        var moving = Item("A_01", 1);
        var transientHolder = Item("B_02", 2);
        transientHolder.IsTransient = true;

        var (blocks, _, reason) = MoveItemsDialog.EvaluateLevelCollision(
            new[] { moving, transientHolder }, moving, 2, movingAsAlias: false);

        Assert.False(blocks);
        Assert.Equal("existing item is a transient stage", reason);
    }
}
