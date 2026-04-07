namespace MergeMansionWikiTools.Models;

// ── Collectible Board Event ───────────────────────────────────────────

public class CollectibleBoardEvent
{
    public string EventId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ChainPrefix { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public TimeSpan? Duration { get; set; }
    public int? DurationDays => Duration.HasValue ? (int)Math.Ceiling(Duration.Value.TotalDays) : null;

    /// <summary>Item types found on the event board (seed for flood-fill chain detection).</summary>
    public HashSet<string> BoardItemTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chains detected via board flood-fill (or prefix fallback).</summary>
    public List<ParsedChain> Chains { get; set; } = new();
}

// ── Chain Relationship Graph ──────────────────────────────────────────

public class ChainGraphNode
{
    public string ChainKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int ItemCount { get; set; }
    public bool HasGenerators { get; set; }
    public bool HasSpawners { get; set; }
    public bool IsSinkChain { get; set; }
}

public enum ChainEdgeType
{
    SpawnDrop,    // black — DropOdds, SpawnOdds, SpawnItemType
    Decay,        // purple — DecayIntoItemType, SpawnDecayIntoItemType, DecayAfterLastCycle*
    SinkInput,    // red — SinkRequirementConfigKeys (input ingredient)
    SinkOutput    // green — SinkRewardItemType (output product)
}

public class ChainGraphEdge
{
    public string SourceChainKey { get; set; } = "";
    public string TargetChainKey { get; set; } = "";
    public ChainEdgeType EdgeType { get; set; }
    public string Label { get; set; } = "";  // e.g. "L5" or "L3, L5"
}
