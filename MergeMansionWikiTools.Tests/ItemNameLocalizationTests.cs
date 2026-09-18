using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GameLogic.Config;
using GameLogic.Player.Items;
using MergeMansionWikiTools.Services;
using MergeMansionWikiTools.Tests.NativeDumper;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Contract of the item-name localization override — <c>FullOverrideLocalizationItemKey</c>.
///
/// The game treats that member as a COMPLETE localization key: <c>LocMan.GetItemNameKey</c> returns
/// it unchanged when it is set (verified in the binary's ISIL — the non-empty branch jumps straight
/// to the return, and the <c>"Item_"</c> prefix is concatenated only in the fallback branch), and
/// <c>GetDescriptionId</c> is that same key plus <c>"_Description"</c>. Our reconstruction used to
/// prepend <c>"Item_"</c> to it, so the override never resolved and 241 of the 242 items carrying
/// one were dumped under a fallback name — either a raw key or, worse, a plausible but wrong name
/// (all five Season Pass chests came out as "Mystery Streak Chest").
///
/// Data backing the rule, config 26.07.01: all 242 override keys exist in the language file exactly
/// as written, none of them exists with an extra <c>Item_</c> in front, and the values carry no
/// common prefix (<c>Item_…</c>, <c>SP_…</c>, <c>TCE_…</c>, <c>ItemCategory_…</c>).
///
/// Needs local game data (<c>_DATA</c>); returns early without it, like the other data-backed tests.
/// </summary>
[Collection(GlobalGameStateCollection.Name)]
public class ItemNameLocalizationTests
{
    /// <summary>ItemType → the name the game shows, for items whose name comes from an override key.</summary>
    public static TheoryData<string, string> OverriddenNames => new()
    {
        // Same override key on the chain and on its items — this is the case that started the hunt:
        // the dump showed "Item_LDE_MurderAtTheMansion_VoyanceHouseSinkIA1", which is not a key at all.
        { "LDE_MurderAtTheMansion_VoyanceHouseSinkIA_01", "Investigation: Lady Voyance's House" },
        { "LDE_MurderAtTheMansion_GardenSinkIA_01", "Investigation: The Garden" },
        { "LDE_MurderAtTheMansion_GardenSinkSFTUE_01", "Seance: The Garden" },
        { "LDE_MurderAtTheMansion_GardenLockedIFTUE_01", "Location: The Garden" },
        // Override key with no "Item_" prefix at all — impossible to resolve by prepending one.
        { "TCE_WildCardBasic_01", "Wild Card" },
        { "TCE_WildCardSpecial_01", "Special Wild Card" },
        // Five distinct chests that the fallback collapsed into one name.
        { "SP_Halloween2024_MysteryPassChestA_01", "Challenge Chest 1" },
        { "SP_Halloween2024_MysteryPassChestE_01", "Challenge Chest 5" },
    };

    [Theory]
    [MemberData(nameof(OverriddenNames))]
    public void ItemName_usesTheOverrideKeyWhole(string itemType, string expected)
    {
        var config = LoadNewestConfig();
        if (config == null) return; // no local _DATA

        var item = Find(config, itemType);
        Assert.NotNull(item);
        Assert.Equal(expected, LocMan.GetItemName(item!.ItemType,
            item.OverrideLocalizationItemKey, item.FullOverrideLocalizationItemKey));
    }

    [Fact]
    public void ItemDescription_usesTheOverrideKeyPlusDescriptionSuffix()
    {
        var config = LoadNewestConfig();
        if (config == null) return;

        var item = Find(config, "LDE_MurderAtTheMansion_VoyanceHouseSinkIA_01");
        Assert.NotNull(item);

        var description = LocMan.GetDescription(item!.ItemType, item.LevelNumber,
            item.OverrideLocalizationItemKey, item.FullOverrideLocalizationItemKey);

        // Whatever the text is, it must be a real translation — not the key echoed back.
        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.DoesNotContain("_Description", description, StringComparison.Ordinal);
        Assert.NotEqual(item.FullOverrideLocalizationItemKey, description);
    }

    [Fact]
    public void ItemsWithoutAnOverride_keepTheirDerivedName()
    {
        var config = LoadNewestConfig();
        if (config == null) return;

        // Regression guard: the fallback path (Item_<type without level><level>) must stay intact.
        var item = Find(config, "LDE_MurderAtTheMansion_Shovel_01");
        Assert.NotNull(item);
        Assert.True(string.IsNullOrEmpty(item!.FullOverrideLocalizationItemKey));
        Assert.Equal("Shovel", LocMan.GetItemName(item.ItemType,
            item.OverrideLocalizationItemKey, item.FullOverrideLocalizationItemKey));
    }

    [Fact]
    public void EveryOverrideKeyInTheConfig_resolvesToARealTranslation()
    {
        var config = LoadNewestConfig();
        if (config == null) return;

        var overridden = config.Items.EnumerateAll()
            .Select(kv => (ItemDefinition)kv.Value)
            .Where(i => !string.IsNullOrEmpty(i.FullOverrideLocalizationItemKey))
            .ToList();

        Assert.NotEmpty(overridden);
        var unresolved = overridden
            .Where(i => LocMan.GetItemName(i.ItemType, i.OverrideLocalizationItemKey,
                            i.FullOverrideLocalizationItemKey) == i.FullOverrideLocalizationItemKey)
            .Select(i => i.ItemType)
            .ToList();

        Assert.True(unresolved.Count == 0,
            $"{unresolved.Count}/{overridden.Count} override keys came back as the key itself: "
            + string.Join(", ", unresolved.Take(5)));
    }

    // ── shared setup ──────────────────────────────────────────────────

    private static ItemDefinition? Find(SharedGameConfig config, string itemType)
        => config.Items.EnumerateAll().Select(kv => (ItemDefinition)kv.Value)
            .FirstOrDefault(i => string.Equals(i.ItemType, itemType, StringComparison.Ordinal));

    private static SharedGameConfig? LoadNewestConfig()
    {
        var data = DataDir();
        if (data == null) return null;

        var configPath = DumperService.SelectNewestConfigArchive(Path.Combine(data, "C"));
        if (configPath == null) return null;

        MetaplayCore.Initialize();
        var langPath = FirstFileOrNull(Path.Combine(data, "L"));
        if (langPath == null) return null; // names come from the language file; without it there is nothing to assert
        MetaplaySDK.ActiveLanguage = LocalizationLanguage.ImportBinary(
            ContentHash.ParseString(Path.GetFileName(langPath)), File.ReadAllBytes(langPath));

        var archive = ConfigArchive.FromBytes(File.ReadAllBytes(configPath));
        var config = (SharedGameConfig)GameConfigFactory.Instance.ImportSharedGameConfig(PatchedConfigArchive.WithNoPatches(archive));
        ClientGlobal.SharedGameConfig = config;
        return config;
    }

    private static string? DataDir()
    {
        var env = Environment.GetEnvironmentVariable("MMWT_DATA");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "C"))) return env;

        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
        var binDebug = Path.Combine(repoRoot, "bin", "Debug");
        if (!Directory.Exists(binDebug)) return null;
        foreach (var tfm in Directory.GetDirectories(binDebug))
        {
            var candidate = Path.Combine(tfm, "win-x64", "_DATA");
            if (Directory.Exists(Path.Combine(candidate, "C"))) return candidate;
        }
        return null;
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private static string? FirstFileOrNull(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir).FirstOrDefault() : null;
}
