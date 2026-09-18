using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GameLogic.Config;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using MergeMansionWikiTools.Tests.NativeDumper;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Contract of <see cref="ShopExtractor"/> — the coin/gem shop tables no dump engine carries.
///
/// The expectations below are pinned to the Murder at the Mansion flash sale shop as the game showed
/// it on 2026-09-17 (user screenshot: six slots, "10 Left" on each, 800 coins / 26 / 13 / 2 / 3 / 37
/// gems, "Refresh now: 20 gems"), which is what makes this more than a round-trip test: it fails if
/// the extractor ever stops reading the private [MetaMember]s the prices live in.
///
/// Like the parity test, it needs local game data (<c>_DATA</c>) and returns early without it, so the
/// suite stays green on a fresh clone.
/// </summary>
[Collection(GlobalGameStateCollection.Name)]
public class ShopExtractorTests
{
    private const string MurderShop = "LDEMurderAtTheMansionShop";

    [Fact]
    public void Extract_readsTheEventFlashSaleShopTheGameShows()
    {
        var config = LoadNewestConfig();
        if (config == null) return; // no local _DATA

        var dump = ShopExtractor.Extract(config);

        var shop = dump.FlashSaleShops.SingleOrDefault(s => s.Id == MurderShop);
        Assert.NotNull(shop);
        Assert.Equal("Diamonds", shop!.RefreshCurrency);
        Assert.Equal(new[] { 20 }, shop.RefreshCosts);
        Assert.False(shop.RefreshDisabled);
        Assert.Equal(6, shop.Slots.Count);

        // Slot order is display order in the game, and each Murder slot has exactly one candidate.
        var offerIds = shop.Slots.Select(s => Assert.Single(Assert.Single(s.Groups).Offers)).ToList();
        Assert.Equal(new[]
        {
            "LDE_MurderAtTheMansion_FlashSale1", "LDE_MurderAtTheMansion_FlashSale2",
            "LDE_MurderAtTheMansion_FlashSale5", "LDE_MurderAtTheMansion_FlashSale6",
            "LDE_MurderAtTheMansion_FlashSale7", "LDE_MurderAtTheMansion_FlashSale8",
        }, offerIds);

        // Slot 1: the one coin-priced item, whose ladder is shorter than its stock (4 costs, 10 stock).
        var first = dump.FlashSaleOffers[offerIds[0]];
        Assert.Equal(10, first.Quantity);
        Assert.Equal("EventTimeSkipBoosterSingle_01", first.Reward?.Id);
        Assert.Equal(new[] { 800L, 1600L, 3200L, 6400L }, first.Costs.Select(c => c.Amount));
        Assert.All(first.Costs, c => Assert.Equal("Coins", c.Currency));

        // Slot 2: the shovel the screenshot shows at 26 gems, with the full ten-step price ladder.
        var shovel = dump.FlashSaleOffers[offerIds[1]];
        Assert.Equal("LDE_MurderAtTheMansion_Shovel_01", shovel.Reward?.Id);
        Assert.Equal("LDE_MurderAtTheMansion_Board", shovel.Reward?.Board);
        Assert.Equal(1, shovel.Reward?.Amount);
        Assert.Equal(new[] { 26L, 32L, 38L, 44L, 51L, 58L, 65L, 75L, 85L, 95L }, shovel.Costs.Select(c => c.Amount));
        Assert.All(shovel.Costs, c => Assert.Equal("Diamonds", c.Currency));

        // The prices the player actually sees first, in slot order.
        Assert.Equal(new[] { 800L, 26L, 13L, 2L, 3L, 37L },
            offerIds.Select(id => dump.FlashSaleOffers[id].Costs[0].Amount));
    }

    [Fact]
    public void Extract_readsGarageFlashSalesAndBoardShopItems()
    {
        var config = LoadNewestConfig();
        if (config == null) return;

        var dump = ShopExtractor.Extract(config);

        // The garage flash sale rolls from weighted groups, unlike the fixed event slots.
        var garage = dump.FlashSaleShops.SingleOrDefault(s => s.Id == "GarageShop");
        Assert.NotNull(garage);
        Assert.Contains(garage!.Slots, s => s.Groups.Count > 1);
        Assert.Contains(dump.FlashSaleOffers.Values, o => o.Scope == "Garage");
        Assert.Contains(dump.FlashSaleOffers.Values, o => o.Scope == "Event");

        // ShopItems: the gem-priced Red Chest of the garage shop, one per day.
        var redChest = dump.ShopItems.SingleOrDefault(i => i.Id == "RedChest_01");
        Assert.NotNull(redChest);
        Assert.Equal("Garage", redChest!.Board);
        Assert.Equal("BoardShopItem", redChest.Kind);
        Assert.Equal("RedChest_01", redChest.Item);
        Assert.Equal("ConstantPriceCurve", redChest.Price?.Type);
        Assert.Equal("Diamonds", redChest.Price?.Currency);
        Assert.Equal(200, redChest.Price?.Values["Price"]);
        Assert.Equal("DailyPurchaseLimiter", redChest.Limit?.Type);
        Assert.Equal(1, redChest.Limit?.Values["PurchasesPerDay"]);
    }

    [Fact]
    public void WriteAndLoad_roundTripsThroughTheFile()
    {
        var config = LoadNewestConfig();
        if (config == null) return;

        var path = Path.Combine(Path.GetTempPath(), "mmwt_tests", "shop_items.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            Assert.Equal(path, ShopExtractor.Write(config, path));
            var loaded = ShopExtractor.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(ShopExtractor.Extract(config).FlashSaleShops.Count, loaded!.FlashSaleShops.Count);
            Assert.Contains(loaded.FlashSaleShops, s => s.Id == MurderShop);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_returnsNullForAMissingOrBrokenFile()
    {
        Assert.Null(ShopExtractor.Load(Path.Combine(Path.GetTempPath(), "mmwt_tests", "no_such_shop_file.json")));

        var broken = Path.Combine(Path.GetTempPath(), "mmwt_tests", "broken_shop.json");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "{ not json");
        try { Assert.Null(ShopExtractor.Load(broken)); }
        finally { File.Delete(broken); }
    }

    // ── shared setup ──────────────────────────────────────────────────

    private static SharedGameConfig? LoadNewestConfig()
    {
        var data = DataDir();
        if (data == null) return null;

        var configPath = DumperService.SelectNewestConfigArchive(Path.Combine(data, "C"));
        if (configPath == null) return null;

        MetaplayCore.Initialize();
        var langPath = FirstFileOrNull(Path.Combine(data, "L"));
        if (langPath != null)
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
