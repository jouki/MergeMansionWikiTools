using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;
using static MergeMansionWikiTools.Services.AssetExtractionService;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Derives the outfit variant of a portrait from the atlas. The names lie: `MaddieDefault_Winter2023_2`
/// is the plain outfit despite its name — the export dedups the second bundle with a `_2` suffix while
/// the atlas names both after the seasonal one. `_Clean` is Babylon's canonical look, not a skin.
/// Ported from `Codex/build/portraits.py` (`sprite_score`).
/// </summary>
internal static class PortraitVariantResolver
{
    // slouží už jen skórování (Score) — řadí sezónní skin až za obyčejný outfit; určení varianty
    // (VariantFromTexture) na sezónnosti jména nezávisí, seznam je shodný jako Codex
    private static readonly Regex Seasonal = new(
        @"(Winter|Summer|Spring|Autumn|Xmas|Christmas|Halloween|Easter|Valentine|Season|Skin|Outfit|Event|_20\d\d|Beach|Party|Paris|Japan)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // mezi nekanonickými bundly je tenhle přesto ten správný vzhled (Babylon po umytí)
    private static readonly string[] CanonicalOutfits = { "Clean" };

    /// <summary>Outfit variant encoded in a texture name, or `Default` for the plain bundle.</summary>
    public static string VariantFromTexture(string spriteName, string textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return "Default";
        if (string.Equals(textureName, spriteName, StringComparison.Ordinal)) return "Default";
        if (string.Equals(textureName, spriteName + "_Default", StringComparison.Ordinal)) return "Default";

        var suffix = textureName.StartsWith(spriteName, StringComparison.Ordinal)
            ? textureName[spriteName.Length..].TrimStart('_')
            : textureName;
        if (suffix.Length == 0) return "Default";

        foreach (var c in CanonicalOutfits)
            if (suffix.EndsWith(c, StringComparison.OrdinalIgnoreCase)) return "Default";

        // přípona _<číslo> je vždy druhý export téže obyčejné textury (sdílí Unity základ s jiným
        // bundlem) — platí to pro každý outfit, ne jen sezónní: MaddieJoyous_Default se v jiné verzi
        // atlasu exportuje jako MaddieJoyous_Tourist_2, "Tourist" v tom jméně nic neznamená
        if (Regex.IsMatch(suffix, @"_\d+$")) return "Default";

        // neznámý outfit NEpropadá na Default — jinak by se sezónní skin vykreslil jako kanonický vzhled
        return suffix;
    }

    /// <summary>
    /// The variant the game shows: the best-scoring bundle of character plus expression.
    /// The atlas names a sprite two ways — most characters concatenate name and expression
    /// directly (`MaddieThinking`), but 98 sprites underscore them instead (`Voyance_Thinking`).
    /// A few characters also only exist in the atlas under their raw in-game id, not the wiki
    /// display name (`speakerId`) — e.g. "Voyance" in the atlas vs. "Lady Voyance" on the wiki.
    /// Tried from most to least specific: wiki name, wiki name with underscore, game id, game id
    /// with underscore.
    /// </summary>
    public static string Resolve(IReadOnlyList<SpriteInfo> sprites, string character, string expression, string? speakerId = null)
    {
        foreach (var spriteName in CandidateNames(character, speakerId, expression))
        {
            var candidates = sprites.Where(s => string.Equals(s.Name, spriteName, StringComparison.Ordinal)).ToList();
            if (candidates.Count > 0)
                return ResolveFromCandidates(spriteName, candidates);
        }
        return "Default";
    }

    /// <summary>Candidate sprite names for a character/expression pair, most specific first.</summary>
    private static IEnumerable<string> CandidateNames(string character, string? speakerId, string expression)
    {
        yield return character + expression;
        yield return character + "_" + expression;
        if (!string.IsNullOrEmpty(speakerId) && speakerId != character)
        {
            yield return speakerId + expression;
            yield return speakerId + "_" + expression;
        }
    }

    private static string ResolveFromCandidates(string spriteName, List<SpriteInfo> candidates)
    {
        var best = candidates
            .OrderBy(s => Score(s.Name, s.TextureName ?? ""))
            .ThenBy(s => s.TextureName ?? "", StringComparer.Ordinal)
            .First();

        // Sólo textura není varianta k výběru, je to jediný vzhled, který postava v tomhle výrazu má
        // (Julius s vousy, Heikki jako Santa) — hra nemá z čeho vybírat, takže i sprite pojmenovaný
        // jako sezónní skin je bez druhé textury kanonický. Jinak by wiki hledala neexistující soubor
        // "<Postava> <Výraz> <Outfit>.png", zatímco šablona odkazuje na "<Postava> <Výraz>.png".
        var distinctTextureCount = candidates.Select(s => s.TextureName ?? "").Distinct(StringComparer.Ordinal).Count();
        if (distinctTextureCount == 1) return "Default";

        return VariantFromTexture(spriteName, best.TextureName ?? "");
    }

    /// <summary>Fills `Variant` on every line that names a speaker. Reads the atlas through the existing service.</summary>
    public static void Apply(List<DialogueScene> scenes, string exportDir)
        => ApplyTo(scenes, SpriteMetadataService.Load(exportDir));

    /// <summary>Same as <see cref="Apply"/> but against sprites already in memory, so tests need no atlas file.</summary>
    public static void ApplyTo(List<DialogueScene> scenes, IReadOnlyList<SpriteInfo> sprites)
    {
        // jeden lookup na trojici postava+herni id+výraz — scén jsou tisíce, atlas má desetitisíce sprite záznamů
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in scenes.SelectMany(s => s.Lines))
        {
            if (string.IsNullOrEmpty(line.Speaker)) continue;
            var key = line.Speaker + "\u0000" + (line.SpeakerId ?? "") + "\u0000" + line.Expression;
            if (!cache.TryGetValue(key, out var variant))
                cache[key] = variant = Resolve(sprites, line.Speaker, line.Expression, line.SpeakerId);
            line.Variant = variant;
        }
    }

    /// <summary>Lower is better, mirroring `sprite_score` in the Codex: plain beats twin beats outfit beats skin.</summary>
    private static int Score(string spriteName, string textureName)
    {
        if (string.Equals(textureName, spriteName, StringComparison.Ordinal)
            || string.Equals(textureName, spriteName + "_Default", StringComparison.Ordinal)) return 0;

        var suffix = textureName.StartsWith(spriteName, StringComparison.Ordinal)
            ? textureName[spriteName.Length..].TrimStart('_')
            : textureName;

        foreach (var c in CanonicalOutfits)
            if (suffix.EndsWith(c, StringComparison.OrdinalIgnoreCase)) return 1;

        // číselné dvojče obyčejné textury musí dostat stejné skóre jako sezónní dvojče, jinak by ho
        // v pořadí porazil jeho vlastní sourozenec bez čísla (kratší jméno vyhrává na abecedě)
        if (Regex.IsMatch(suffix, @"_\d+$")) return 1;

        if (Seasonal.IsMatch(suffix)) return 3;
        return 2;
    }
}
