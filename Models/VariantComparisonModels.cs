namespace MergeMansionWikiTools.Models;

/// <summary>
/// Represents a discovered AB test variant directory.
/// </summary>
public class VariantInfo
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
}

/// <summary>
/// Direction of a diff: buff (green), nerf (red), or changed (orange).
/// </summary>
public enum DiffDirection
{
    Buff,
    Nerf,
    Changed
}

/// <summary>
/// A single field difference between base and a variant.
/// </summary>
public class FieldDiff
{
    public string FieldName { get; set; } = "";
    public string BaseValue { get; set; } = "";
    public string VariantValue { get; set; } = "";
    public DiffDirection Direction { get; set; }
    /// <summary>Human-readable delta like "+3", "-20%", "−1h". Empty for string changes.</summary>
    public string DeltaText { get; set; } = "";
}

/// <summary>
/// All diffs for one item level across all variants.
/// </summary>
public class ItemVariantDiff
{
    public int Level { get; set; }
    public string ItemName { get; set; } = "";
    public string ItemType { get; set; } = "";

    /// <summary>VariantName → list of field diffs for this level.</summary>
    public Dictionary<string, List<FieldDiff>> DiffsByVariant { get; set; } = new();
}

/// <summary>
/// Full comparison result for a chain across all variants.
/// </summary>
public class VariantComparisonResult
{
    public string ChainName { get; set; } = "";
    public string ConfigKey { get; set; } = "";
    public int LevelCount { get; set; }
    public List<VariantInfo> Variants { get; set; } = new();
    public List<ItemVariantDiff> Diffs { get; set; } = new();
    public bool HasAnyDiffs => Diffs.Count > 0;
}
