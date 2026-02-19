using System.Text.Json.Serialization;

namespace MergeMansionWikiTools.Models;

// ── Top-level JSON wrapper ──────────────────────────────────────────

public class ChainDataRoot
{
    public string CreatedAt { get; set; } = "";
    public List<ChainDefinition> Data { get; set; } = new();
}

// ── Chain definition ────────────────────────────────────────────────

public class ChainDefinition
{
    public string Name { get; set; } = "";
    public string ConfigKey { get; set; } = "";
    public string? CodexCategory { get; set; }
    public string? DiscoveryRewardRef { get; set; }
    public List<ChainEntry> PrimaryChain { get; set; } = new();
    public List<ChainEntry> FallbackChain { get; set; } = new();
}

public class ChainEntry
{
    public ItemDefinition? Item { get; set; }
}

// ── Item definition ─────────────────────────────────────────────────

public class ItemDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int SellCoins { get; set; }
    public string ConfigKey { get; set; } = "";
    public string ItemType { get; set; } = "";
    public int LevelNumber { get; set; }
    public bool Unsellable { get; set; }
    public int ExperienceValue { get; set; }
    public string? Rarity { get; set; }

    // Features
    public ActivationFeaturesData? ActivationFeatures { get; set; }
    public SpawnFeaturesData? SpawnFeatures { get; set; }
    public DecayFeaturesData? DecayFeatures { get; set; }
    public MergeFeaturesData? MergeFeatures { get; set; }

    // Tags
    public List<string>? Tags { get; set; }
}

// ── Activation features (generators) ────────────────────────────────

public class ActivationFeaturesData
{
    public ActivationSpawnData? ActivationSpawn { get; set; }
    public ActivationCycleData? ActivationCycle { get; set; }
    public int StorageMax { get; set; }
}

public class ActivationSpawnData
{
    public string? Marker { get; set; }
    public ProducerData? BaseProducer { get; set; }
}

public class ProducerData
{
    public string? Constant { get; set; }
    public ControlledRandomData? ControlledRandom { get; set; }
    public RandomOddsData? Random { get; set; }
}

public class ControlledRandomData
{
    public string? RollType { get; set; }
    public string? ItemType { get; set; }
    public Dictionary<string, double>? Odds { get; set; }
}

public class RandomOddsData
{
    public Dictionary<string, double>? Odds { get; set; }
}

public class ActivationCycleData
{
    public int ActivationDelay { get; set; }
    public int FirstCycleStartDelay { get; set; }
    public int HowManyCycles { get; set; } = -1;
    public DailyActivationCyclesData? DailyActivationCyclesData { get; set; }
}

public class DailyActivationCyclesData
{
    public List<long> DelaysBetweenCycles { get; set; } = new();
    public List<double> TimerSkipMultiplier { get; set; } = new();
    public List<int> ActivationAmountInCycle { get; set; } = new();
    public List<int> HowManyAreGeneratedInCycle { get; set; } = new();
}

// ── Spawn features ──────────────────────────────────────────────────

public class SpawnFeaturesData
{
    public ProducerData? Spawn { get; set; }
    public SpawnCycleData? SpawnCycle { get; set; }
    public int StorageMax { get; set; }
    public ProducerData? DecayProducer { get; set; }
    public string? SpawnVisibility { get; set; }
    public bool Spawnable { get; set; }
    public bool DecaysWhenCyclesAreDone { get; set; }
}

public class SpawnCycleData
{
    public int SpawnDelay { get; set; }
    public int FirstCycleStartDelay { get; set; }
    public long DelayBetweenCycles { get; set; }
    public int HowManyAreGeneratedPerSpawn { get; set; }
    public int SpawnAmountInCycle { get; set; }
    public int HowManyCycles { get; set; } = -1;
}

// ── Decay features ──────────────────────────────────────────────────

public class DecayFeaturesData
{
    public bool DoesDecay { get; set; }
    public long Lifetime { get; set; }
    public ProducerData? ItemProducer { get; set; }
    public string? DecayMergeMode { get; set; }
    public bool ShowDecayTimer { get; set; }
}

// ── Merge features ──────────────────────────────────────────────────

public class MergeFeaturesData
{
    // Simplified — extend if needed
    public bool IsMergeable { get; set; }
}

// ── Parsed chain model (for UI) ─────────────────────────────────────

public class ParsedChain
{
    public string ConfigKey { get; set; } = "";

    /// <summary>Display name: custom name → chain Name → ConfigKey</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Original name from JSON (may be empty)</summary>
    public string OriginalName { get; set; } = "";

    /// <summary>Whether the original name looks "natural" (not internal ID)</summary>
    public bool HasNaturalName { get; set; }

    /// <summary>Custom user-assigned name, if any</summary>
    public string? CustomName { get; set; }

    public List<ParsedItem> Items { get; set; } = new();

    /// <summary>Compact summary like "12 levels, Generator"</summary>
    public string Summary => BuildSummary();

    /// <summary>Whether any item is a generator (has ActivationFeatures)</summary>
    public bool HasGenerators => Items.Any(i => i.IsGenerator);

    /// <summary>Whether any item has SpawnFeatures</summary>
    public bool HasSpawners => Items.Any(i => i.IsSpawner);

    /// <summary>Whether this looks like an event chain</summary>
    public bool IsEventChain => ConfigKey.StartsWith("CBE_") || ConfigKey.StartsWith("LDE_");

    /// <summary>Whether the original name is truly human-readable (no underscores, not empty)</summary>
    public bool HasHumanReadableName =>
        !string.IsNullOrWhiteSpace(OriginalName)
        && !OriginalName.Contains('_')
        && OriginalName.Length > 0;

    private string BuildSummary()
    {
        var parts = new List<string> { $"{Items.Count} lvl" };

        if (HasGenerators) parts.Add("Generator");
        else if (HasSpawners) parts.Add("Spawner");
        else parts.Add("Product");

        if (IsEventChain) parts.Add("Event");

        return string.Join(" · ", parts);
    }
}

public class ParsedItem
{
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public string ItemType { get; set; } = "";
    public int SellCoins { get; set; }
    public bool Unsellable { get; set; }

    // Generator info
    public bool IsGenerator { get; set; }
    public Dictionary<string, double>? DropOdds { get; set; }
    public int ActivationAmountInCycle { get; set; }
    public int StorageMax { get; set; }
    public long RechargeTimeMs { get; set; }
    public long FirstCycleStartDelayMs { get; set; }
    public string? DecayAfterLastCycleItemType { get; set; }

    // Spawner info
    public bool IsSpawner { get; set; }
    public string? SpawnItemType { get; set; }
    public Dictionary<string, double>? SpawnOdds { get; set; }
    public int SpawnHowManyCycles { get; set; } = -1;
    public int SpawnAmountInCycle { get; set; }
    public long SpawnDelayMs { get; set; }
    public int SpawnStorageMax { get; set; }

    // Decay info
    public bool HasDecay { get; set; }
    public string? DecayIntoItemType { get; set; }
    public string? SpawnDecayIntoItemType { get; set; }

    // Lua generation extras
    public string Description { get; set; } = "";
    public bool IsTemporary { get; set; }

    // Sink (Transformative Item)
    public bool IsSink { get; set; }
    public List<string>? SinkRequirementItemTypes { get; set; }

    // Depleting spawner
    public bool DecaysWhenCyclesAreDone { get; set; }

    // Original definition reference
    public ItemDefinition? Source { get; set; }
}
