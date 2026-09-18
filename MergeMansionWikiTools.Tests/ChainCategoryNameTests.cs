using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;
using MergeMansionWikiTools.Services;
using MergeMansionWikiTools.Tests.NativeDumper;
using Metaplay.Core;
using Metaplay.Core.Config;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Contract of the chain display name — the localized item CATEGORY of the chain's first item.
///
/// Three keys can produce it and the ORDER is what this file pins down:
/// <c>ItemCategory_&lt;chainId&gt;</c>, then the item's <c>OverrideLocalizationItemCategory</c> used
/// as a whole key, then <c>ItemCategory_&lt;pool tag&gt;</c>.
///
/// The override sits in the middle because of how the game resolves it: the ISIL of
/// <c>LocMan.GetItemCategoryName(IItemDefinition)</c> reads the override first and only falls back to
/// <c>Concat("ItemCategory_", PoolTag)</c> when it is empty — so the override outranks the pool tag.
/// The chain-id key stays ahead of both: it belongs to the game's other resolution path
/// (<c>GetItemCategoryName(MergeChainId)</c>), and dropping it would regress names that are more
/// specific than the category (<c>OrangeFlower</c> → "A beautiful Orange Flower", not "Flower").
///
/// Until v0.24.73 the override came last, which cost 171 chains their name: 155 Season Pass chests
/// collapsed onto their shared pool tag ("Mystery Streak Chest" five times over instead of
/// "Challenge Chest 1"…"5").
///
/// Needs local game data (<c>_DATA</c>); returns early without it.
/// </summary>
[Collection(GlobalGameStateCollection.Name)]
public class ChainCategoryNameTests
{
    /// <summary>Chain config key → the name the game shows for it.</summary>
    public static TheoryData<string, string> ChainNames => new()
    {
        // Override wins over the shared pool tag (ItemCategory_MysteryPassChest = "Mystery Streak Chest").
        { "SP_Halloween2024_MysteryPassChestA", "Challenge Chest 1" },
        { "SP_Halloween2024_MysteryPassChestC", "Challenge Chest 3" },
        { "SP_Halloween2024_MysteryPassChestE", "Challenge Chest 5" },
        // The chain-id key still outranks everything: pool tag "Flower" would be a regression.
        { "OrangeFlower", "A beautiful Orange Flower" },
        // No chain-id key → falls through to the pool tag, as before.
        { "TimeSkipBooster2", "Time Skip Booster" },
        // Neither chain-id key nor pool tag key — only the override resolves this one.
        { "TCE_WildCardSpecial", "Special Wild Card" },
    };

    [Theory]
    [MemberData(nameof(ChainNames))]
    public void ChainName_prefersChainKeyThenOverrideThenPoolTag(string chainKey, string expected)
    {
        var config = LoadNewestConfig();
        if (config == null) return; // no local _DATA

        var chain = config.MergeChains.EnumerateAll()
            .Select(kv => kv.Value)
            .Cast<GameLogic.MergeChains.MergeChainDefinition>()
            .FirstOrDefault(c => string.Equals(c.ConfigKey?.Value, chainKey, StringComparison.Ordinal));
        Assert.NotNull(chain);

        Assert.Equal(expected, (string?)Serialize(config, chain!)["Name"]);
    }

    /// <summary>Serializes one chain through the real dump converter and returns its JSON object.</summary>
    private static JObject Serialize(SharedGameConfig config, object chain)
    {
        var settings = DumpJson.CreateSettings(new JsonConverter[]
        {
            new ChainSerializer(config, dropsAsPercent: true, ConsoleDumpLog.Instance),
        });
        settings.Formatting = Formatting.None;
        return JObject.Parse(JsonConvert.SerializeObject(chain, settings));
    }

    private static SharedGameConfig? LoadNewestConfig()
    {
        var data = DataDir();
        if (data == null) return null;

        var configPath = DumperService.SelectNewestConfigArchive(Path.Combine(data, "C"));
        if (configPath == null) return null;

        MetaplayCore.Initialize();
        var langPath = FirstFileOrNull(Path.Combine(data, "L"));
        if (langPath == null) return null; // category names come from the language file
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
