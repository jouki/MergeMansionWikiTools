using System.Windows;
using System.Windows.Media;
using MergeMansionWikiTools.Helpers;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Single source of truth for "chain → display name + thumbnails", "item → name + thumbnail" and
/// "area → thumbnail" in the Daily Trade Predictor. Reuses the wiki name resolution
/// (<see cref="WikiTableGenerator.GetWikiChainName"/>) and the image data the autocomplete prewarmed
/// (<see cref="AutocompleteData"/>), plus the shared crop logic (<see cref="SpriteImageBrushBuilder"/>).
/// Brushes are built lazily and cached. Event chains are excluded from the pickable lists but still
/// resolvable by name/icon.
/// </summary>
public class ChainDisplayResolver
{
    private readonly DataService? _data;
    private readonly string? _exportDir;
    private readonly string? _processedDir;
    private readonly HashSet<string> _ensured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _name = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _maxLevel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int), ImageBrush?> _levelBrushCache = new();
    private readonly Dictionary<string, ImageBrush?> _areaBrushCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<PickRow> _chainRows = new();
    private readonly List<PickRow> _itemRows = new();

    /// <summary>Non-event chains as pickable rows (leads to the level step), sorted by name.</summary>
    public IReadOnlyList<PickRow> ChainRows => _chainRows;

    /// <summary>Areas as pickable options (InternalName + DisplayName + icon), sorted by name.</summary>
    public IReadOnlyList<AreaOption> AreaOptions { get; } = new List<AreaOption>();

    /// <summary>Empty resolver — names fall back to the key, no icons.</summary>
    public ChainDisplayResolver() { }

    public ChainDisplayResolver(DataService data, WikiMappingCache? wikiMapping,
        AutocompleteData? autocomplete, IReadOnlyList<LuaArea>? areas,
        string? exportDir = null, string? processedDir = null)
    {
        _data = data;
        _exportDir = exportDir;
        _processedDir = processedDir;
        var probe = new WikiTableGenerator(data, wikiMapping);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in data.Chains)
        {
            if (string.IsNullOrEmpty(c.ConfigKey) || _name.ContainsKey(c.ConfigKey)) continue;

            var wiki = probe.GetWikiChainName(c);
            var name = !string.IsNullOrEmpty(wiki) ? wiki
                     : !string.IsNullOrEmpty(c.OriginalName) ? c.OriginalName : c.ConfigKey;

            // Per-level thumbnails are read DIRECTLY from "Processed Images" per (ConfigKey, level) in
            // GetLevelBrush — the canonical flood-fill crops. No atlas-rect re-crop, no wiki-name map.
            _maxLevel[c.ConfigKey] = c.Items.Count > 0 ? c.Items.Max(i => i.Level) : 0;

            _name[c.ConfigKey] = name;

            // Reward boxes / chests are MAIN-BOARD items even when their ConfigKey looks event-y
            // (EventBox*, LBE_*, CBE_*) — the player holds them on the board, so they must be pickable
            // despite the event-chain exclusion.
            bool rewardBoxLike = c.Items.Any(i => i.IsChest)
                || name.Contains("Reward Box", StringComparison.OrdinalIgnoreCase);

            // Pickable when it's a real, non-test chain with a proper wiki name, and either not an
            // event chain OR a reward box/chest. Dedup by display name so the many identically-named
            // chains (15× "Reward Box", two "Gold Coins") collapse to one clean entry.
            bool pickable = (!c.IsEventChain || rewardBoxLike) && !c.HasTestTag && !string.IsNullOrEmpty(wiki);
            if (pickable && seenNames.Add(name))
            {
                _chainRows.Add(new PickRow(this, c.ConfigKey, null, name, ""));
                foreach (var it in c.Items)
                {
                    var itemName = !string.IsNullOrWhiteSpace(it.Name) ? it.Name : name;
                    _itemRows.Add(new PickRow(this, c.ConfigKey, it.Level, itemName, $"{name} · L{it.Level}"));
                }
            }
        }
        _chainRows.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

        if (areas != null)
        {
            var areaOpts = new List<AreaOption>();
            foreach (var a in areas)
            {
                if (string.IsNullOrEmpty(a.InternalName)) continue;
                string? path = null; Rect frac = new(0, 0, 0.9, 0.9);
                if (autocomplete != null && autocomplete.AreaImagePaths.TryGetValue(a.DisplayName, out var p))
                {
                    path = p;
                    if (autocomplete.AreaFractionalRects.TryGetValue(a.DisplayName, out var fr)) frac = fr;
                }
                areaOpts.Add(new AreaOption(this, a.InternalName, a.DisplayName, path, frac));
            }
            AreaOptions = areaOpts.OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Search rows for the picker: chains AND individual items whose name matches the query.
    /// Chain rows lead to the level step; item rows (with a level) add that exact item directly.</summary>
    public List<PickRow> Search(string query, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(query)) return _chainRows.Take(max).ToList();
        var q = query.Trim();
        var chains = _chainRows.Where(r => r.Label.Contains(q, StringComparison.OrdinalIgnoreCase));
        var items = _itemRows.Where(r => r.Label.Contains(q, StringComparison.OrdinalIgnoreCase));
        return chains.Concat(items).Take(max).ToList();
    }

    /// <summary>Human-readable chain name for a ConfigKey (falls back to the key itself).</summary>
    public string GetName(string? configKey)
        => !string.IsNullOrEmpty(configKey) && _name.TryGetValue(configKey, out var n) ? n : (configKey ?? "");

    /// <summary>Chain thumbnail — the max level's clean crop.</summary>
    public ImageBrush? GetBrush(string? configKey)
    {
        if (string.IsNullOrEmpty(configKey)) return null;
        var max = _maxLevel.TryGetValue(configKey, out var m) ? m : 0;
        return GetLevelBrush(configKey, max);
    }

    /// <summary>Per-level thumbnail for a specific (chain, level): reads the clean per-item crop
    /// DIRECTLY from "Processed Images" as {ConfigKey}{suffix:00}.png (suffix = the item's ItemType
    /// number, exactly how FlowchartImageService/Image Optimiser names them — no wiki-name indirection,
    /// so display-level ↔ file always stay aligned). Null until the crop exists;
    /// <see cref="EnsureLevelImages"/> flood-fill-crops the missing ones, then a re-render picks them up.</summary>
    public ImageBrush? GetLevelBrush(string? configKey, int level)
    {
        if (string.IsNullOrEmpty(configKey)) return null;
        var cacheKey = (configKey, level);
        if (_levelBrushCache.TryGetValue(cacheKey, out var cached) && cached != null) return cached;

        var brush = LoadLevelFile(configKey, level);
        if (brush != null) _levelBrushCache[cacheKey] = brush; // cache only hits → re-read after extraction
        return brush;
    }

    private ImageBrush? LoadLevelFile(string configKey, int level)
    {
        if (string.IsNullOrEmpty(_processedDir)) return null;
        // Preferred key = DISPLAY level (item.Level) — what the multi-texture extraction writes and
        // what avoids the suffix collapse (InfiniteEnergySmall/Mid/Big all end _01). Fallback key =
        // ItemType numeric suffix — what the single-atlas extraction writes; differs from display
        // level only for alias/variant items, which the fallback keeps working.
        if (level > 0)
        {
            var path = System.IO.Path.Combine(_processedDir, $"{configKey}{level:00}.png");
            if (System.IO.File.Exists(path)) return SpriteImageBrushBuilder.BuildWhole(path);
        }
        var item = _data?.Chains.FirstOrDefault(c =>
                string.Equals(c.ConfigKey, configKey, StringComparison.OrdinalIgnoreCase))
            ?.Items.FirstOrDefault(i => i.Level == level);
        if (item != null && !string.IsNullOrEmpty(item.ItemType))
        {
            int suffix = DataService.GetLevelFromItemType(item.ItemType);
            if (suffix > 0 && suffix != level)
            {
                var alt = System.IO.Path.Combine(_processedDir, $"{configKey}{suffix:00}.png");
                if (System.IO.File.Exists(alt)) return SpriteImageBrushBuilder.BuildWhole(alt);
            }
        }
        return null;
    }

    /// <summary>Area thumbnail brush (center-square crop of the area background), or null.</summary>
    public ImageBrush? GetAreaBrush(string? internalName, string? imagePath, Rect fractional)
    {
        if (string.IsNullOrEmpty(internalName)) return null;
        if (_areaBrushCache.TryGetValue(internalName, out var cached)) return cached;
        var brush = string.IsNullOrEmpty(imagePath) ? null : SpriteImageBrushBuilder.BuildFractional(imagePath, fractional);
        _areaBrushCache[internalName] = brush;
        return brush;
    }

    /// <summary>
    /// Flood-fill-crops the MISSING per-level icons for the given chains from the atlas (via the
    /// canonical <see cref="FlowchartImageService.ExtractItemIcons"/> — atlas indices → flood-fill →
    /// tight crop; existing files reused, NOT optimized). Saves them to "Processed Images" as
    /// {ConfigKey}{level:00}.png so <see cref="GetLevelBrush"/> reads them directly on the next render.
    /// Runs the heavy ImageSharp work — call from a background thread. Returns true when it produced
    /// icons (caller should re-render). Each chain is attempted at most once.
    /// </summary>
    public bool EnsureLevelImages(IEnumerable<string> configKeys)
    {
        if (_data == null || string.IsNullOrEmpty(_exportDir)) return false;

        var todo = configKeys
            .Where(k => !string.IsNullOrEmpty(k) && !_ensured.Contains(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (todo.Count == 0) return false;

        var itemTypes = new List<string>();
        foreach (var k in todo)
        {
            var chain = _data.Chains.FirstOrDefault(c =>
                string.Equals(c.ConfigKey, k, StringComparison.OrdinalIgnoreCase));
            if (chain == null) continue;
            foreach (var it in chain.Items)
                if (!string.IsNullOrEmpty(it.ItemType)) itemTypes.Add(it.ItemType);
        }
        foreach (var k in todo) _ensured.Add(k); // don't retry a chain that yields nothing

        if (itemTypes.Count == 0) return false;
        try
        {
            var produced = FlowchartImageService.ExtractItemIcons(itemTypes, _data, _exportDir!, _processedDir);
            return produced.Count > 0;
        }
        catch { return false; } // atlas/ImageSharp failure — leave the icons blank
    }

    /// <summary>Level options for a chain's picker (level number + per-level thumbnail).</summary>
    public List<LevelOption> LevelOptions(string? configKey)
    {
        if (_data == null || string.IsNullOrEmpty(configKey)) return new();
        var chain = _data.Chains.FirstOrDefault(c =>
            string.Equals(c.ConfigKey, configKey, StringComparison.OrdinalIgnoreCase));
        if (chain == null) return new();
        return chain.Items
            .Select(i => i.Level)
            .Distinct()
            .OrderBy(l => l)
            .Select(l => new LevelOption(l, GetLevelBrush(configKey, l)))
            .ToList();
    }
}

/// <summary>A pickable row: a chain (Level == null → go to level step) or a specific item
/// (Level set → add directly). Icon resolves lazily via the resolver.</summary>
public class PickRow
{
    private readonly ChainDisplayResolver _resolver;
    public string ChainKey { get; }
    public int? Level { get; }
    public string Label { get; }
    public string Sub { get; }
    public bool IsItem => Level.HasValue;
    public ImageBrush? Icon => IsItem ? _resolver.GetLevelBrush(ChainKey, Level!.Value) : _resolver.GetBrush(ChainKey);

    public PickRow(ChainDisplayResolver resolver, string chainKey, int? level, string label, string sub)
    {
        _resolver = resolver;
        ChainKey = chainKey;
        Level = level;
        Label = label;
        Sub = sub;
    }
}

/// <summary>A pickable level in the board cell picker — level number + per-level icon.</summary>
public class LevelOption
{
    public int Level { get; }
    public ImageBrush? Icon { get; }
    public string Display => $"Level {Level}";
    public LevelOption(int level, ImageBrush? icon) { Level = level; Icon = icon; }
}

/// <summary>A pickable area in the progress selector — Name + area icon.</summary>
public class AreaOption
{
    private readonly ChainDisplayResolver _resolver;
    private readonly string? _imagePath;
    private readonly Rect _fractional;
    public string InternalName { get; }
    public string DisplayName { get; }
    public ImageBrush? Icon => _resolver.GetAreaBrush(InternalName, _imagePath, _fractional);

    public AreaOption(ChainDisplayResolver resolver, string internalName, string displayName,
        string? imagePath, Rect fractional)
    {
        _resolver = resolver;
        InternalName = internalName;
        DisplayName = displayName;
        _imagePath = imagePath;
        _fractional = fractional;
    }
}
