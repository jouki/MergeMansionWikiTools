using MergeMansionWikiTools.Models;
using static MergeMansionWikiTools.Services.AssetExtractionService;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Resolves which atlas texture each item of a chain lives in (via skin mapping ItemType→sprite→
/// skeleton) and detects chains whose items span MULTIPLE textures (natively like InfiniteEnergy →
/// UnlimitedEnergyA/C/D, or via wiki-merge of originally separate chains). Single source of truth for
/// that decision, shared by the icon extraction (FlowchartImageService) and the Image Optimiser
/// chain-mode loader.
/// </summary>
internal static class AtlasStitcher
{
    public record ItemTextureRef(string ItemType, int Level, string TextureName);

    /// <summary>Per-item texture, resolved from each item's OWN PoolTag via the game's PoolTag→texture
    /// mapping (image_atlas_data.json). This is the clean data link: e.g. the InfiniteEnergy chain's
    /// items carry PoolTags LimitedItemInfiniteEnergyA/B/C → UnlimitedEnergyD/C/A. Items with an empty
    /// or unresolvable PoolTag are omitted. <paramref name="poolTagToTexture"/> is injected so the
    /// core stays I/O-free and testable (callers pass SpriteMetadataService.ResolveSkeletonForPoolTag).</summary>
    public static List<ItemTextureRef> ResolveItemTextures(
        ParsedChain chain, System.Func<string, string?> poolTagToTexture)
    {
        var result = new List<ItemTextureRef>();
        if (chain?.Items == null || poolTagToTexture == null) return result;
        foreach (var item in chain.Items)
        {
            if (string.IsNullOrEmpty(item.ItemType) || string.IsNullOrEmpty(item.PoolTag)) continue;
            var tex = poolTagToTexture(item.PoolTag);
            if (!string.IsNullOrEmpty(tex))
                result.Add(new ItemTextureRef(item.ItemType, item.Level, tex!));
        }
        return result;
    }

    /// <summary>True when the chain's items resolve into 2+ distinct textures.</summary>
    public static bool IsMultiTexture(IReadOnlyList<ItemTextureRef> refs) =>
        refs.Select(r => r.TextureName).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;

    /// <summary>Distinct textures in order of first appearance by ascending item Level.</summary>
    public static List<string> DistinctTextures(IReadOnlyList<ItemTextureRef> refs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var r in refs.OrderBy(r => r.Level))
            if (seen.Add(r.TextureName)) ordered.Add(r.TextureName);
        return ordered;
    }
}
