using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Parses events.json → Data.CollectibleBoards section and resolves event chains.
/// </summary>
public class EventService
{
    public List<CollectibleBoardEvent> Events { get; } = new();

    public async Task LoadAsync(string filePath)
    {
        Events.Clear();

        await using var stream = File.OpenRead(filePath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        if (!root.TryGetProperty("Data", out var data)) return;
        if (!data.TryGetProperty("CollectibleBoards", out var boards)) return;

        foreach (var board in boards.EnumerateArray())
        {
            var eventId = GetString(board, "CollectibleBoardEventId");
            if (string.IsNullOrEmpty(eventId)) continue;

            // Prefer "Name" (human-readable like "Sweet Mess Express") over "DisplayName" (internal)
            var displayName = GetString(board, "Name");
            if (string.IsNullOrEmpty(displayName))
                displayName = GetString(board, "DisplayName");
            if (string.IsNullOrEmpty(displayName))
                displayName = eventId;

            // Parse schedule
            DateTime? startDate = null;
            TimeSpan? duration = null;
            if (board.TryGetProperty("ActivableParams", out var ap) &&
                ap.TryGetProperty("Schedule", out var sched))
            {
                if (sched.TryGetProperty("Start", out var startEl))
                {
                    var startStr = startEl.GetString();
                    if (DateTime.TryParse(startStr, out var dt))
                        startDate = dt;
                }
                if (sched.TryGetProperty("Duration", out var durEl))
                    duration = ParseDuration(durEl.GetString());
            }

            // Parse board items (seed for flood-fill chain detection)
            var boardItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (board.TryGetProperty("BoardRefs", out var boardRefs))
            {
                foreach (var br in boardRefs.EnumerateArray())
                {
                    if (br.TryGetProperty("BoardLayout", out var layout))
                    {
                        foreach (var cell in layout.EnumerateArray())
                        {
                            var itemId = GetString(cell, "ItemId");
                            if (!string.IsNullOrEmpty(itemId))
                                boardItems.Add(itemId);
                        }
                    }
                }
            }

            Events.Add(new CollectibleBoardEvent
            {
                EventId = eventId,
                DisplayName = displayName,
                ChainPrefix = eventId + "_",
                StartDate = startDate,
                Duration = duration,
                BoardItemTypes = boardItems,
            });
        }

        // Sort by start date descending (newest first)
        Events.Sort((a, b) => (b.StartDate ?? DateTime.MinValue).CompareTo(a.StartDate ?? DateTime.MinValue));
    }

    /// <summary>
    /// Resolves which chains belong to each event.
    /// Primary: flood-fill from board items (spawn/drop/decay/sink traversal).
    /// Fallback: prefix matching on ConfigKey.
    /// </summary>
    public void ResolveChains(DataService ds)
    {
        // Build lookup: ItemType → chain
        var itemToChain = new Dictionary<string, ParsedChain>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in ds.Chains)
            foreach (var item in chain.Items)
                itemToChain.TryAdd(item.ItemType, chain);

        foreach (var ev in Events)
        {
            if (ev.BoardItemTypes.Count > 0)
            {
                // Flood-fill from board items
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>();

                // Seed: board item types → chains
                foreach (var itemType in ev.BoardItemTypes)
                {
                    if (itemToChain.TryGetValue(itemType, out var chain) && found.Add(chain.ConfigKey))
                        queue.Enqueue(chain.ConfigKey);
                }

                // Flood: follow spawn/drop/decay/sink references
                while (queue.Count > 0)
                {
                    var ck = queue.Dequeue();
                    var chain = ds.Chains.FirstOrDefault(c => c.ConfigKey == ck);
                    if (chain == null) continue;

                    foreach (var item in chain.Items)
                    {
                        void TryAdd(string? targetItemType)
                        {
                            if (string.IsNullOrEmpty(targetItemType)) return;
                            if (itemToChain.TryGetValue(targetItemType, out var tc) && found.Add(tc.ConfigKey))
                                queue.Enqueue(tc.ConfigKey);
                        }

                        // Spawn/Drop
                        if (item.DropOdds != null) foreach (var k in item.DropOdds.Keys) TryAdd(k);
                        if (item.SpawnOdds != null) foreach (var k in item.SpawnOdds.Keys) TryAdd(k);
                        TryAdd(item.SpawnItemType);

                        // Decay
                        TryAdd(item.DecayIntoItemType);
                        TryAdd(item.SpawnDecayIntoItemType);
                        TryAdd(item.DecayAfterLastCycleItemType);
                        if (item.DecayAfterLastCycleOdds != null)
                            foreach (var k in item.DecayAfterLastCycleOdds.Keys) TryAdd(k);

                        // Sink output
                        TryAdd(item.SinkRewardItemType);

                        // Order required/reward items
                        if (item.OrderRequiredItems != null)
                            foreach (var k in item.OrderRequiredItems.Keys) TryAdd(k);
                        if (item.OrderRewardItems != null)
                            foreach (var k in item.OrderRewardItems.Keys) TryAdd(k);
                    }
                }

                ev.Chains = ds.Chains.Where(c => found.Contains(c.ConfigKey)).ToList();
            }
            else
            {
                // Fallback: prefix matching
                ev.Chains = ds.Chains
                    .Where(c => c.ConfigKey.StartsWith(ev.ChainPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }
    }

    private static string GetString(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
    }

    /// <summary>Parses duration strings like "4d 0h 0min 0s" or "107h 0min 0s".</summary>
    private static TimeSpan? ParseDuration(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;

        double totalMs = 0;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.EndsWith("d") && double.TryParse(part[..^1], out var days))
                totalMs += days * 86400000;
            else if (part.EndsWith("h") && double.TryParse(part[..^1], out var hours))
                totalMs += hours * 3600000;
            else if (part.EndsWith("min") && double.TryParse(part[..^3], out var mins))
                totalMs += mins * 60000;
            else if (part.EndsWith("s") && !part.EndsWith("ms") && double.TryParse(part[..^1], out var secs))
                totalMs += secs * 1000;
            else if (part.EndsWith("ms") && double.TryParse(part[..^2], out var ms))
                totalMs += ms;
        }

        return totalMs > 0 ? TimeSpan.FromMilliseconds(totalMs) : null;
    }
}
