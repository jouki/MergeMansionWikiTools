using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Parses dialogues.json and extracts mystery event dialogues grouped by wiki tab name.
/// Lazy-loads and caches the dialogue data.
/// </summary>
public class DialogueService
{
    private Dictionary<string, List<RawDialogueEntry>>? _dialoguesByGroup;
    private string? _loadedPath;
    private string? _currentPetDisplayName;

    /// <summary>
    /// Loads dialogues.json from the given path. Caches result — subsequent calls
    /// with the same path return immediately.
    /// </summary>
    public async Task LoadAsync(string filePath)
    {
        if (_loadedPath == filePath && _dialoguesByGroup != null)
            return;

        _dialoguesByGroup = new Dictionary<string, List<RawDialogueEntry>>(StringComparer.OrdinalIgnoreCase);

        await using var stream = File.OpenRead(filePath);
        var doc = await JsonDocument.ParseAsync(stream);

        // Structure: {"CreatedAt": "...", "Data": {"Dialogues": [...]}} or {"Dialogues": [...]}
        var root = doc.RootElement;
        if (root.TryGetProperty("Data", out var dataEl))
            root = dataEl;
        if (!root.TryGetProperty("Dialogues", out var dialogues))
            return;

        foreach (var entry in dialogues.EnumerateArray())
        {
            var id = GetString(entry, "DialogItemId");
            if (string.IsNullOrEmpty(id)) continue;

            // Extract group key: everything before the last _NN suffix
            var groupKey = ExtractGroupKey(id);
            if (string.IsNullOrEmpty(groupKey)) continue;

            var raw = new RawDialogueEntry
            {
                DialogItemId = id,
                Text = GetString(entry, "Text"),
                LeftCharacter = GetString(entry, "LeftCharacter"),
                RightCharacter = GetString(entry, "RightCharacter"),
                LeftSpeaks = GetBool(entry, "LeftSpeaks"),
                RightSpeaks = GetBool(entry, "RightSpeaks"),
            };

            if (!_dialoguesByGroup.TryGetValue(groupKey, out var list))
            {
                list = new List<RawDialogueEntry>();
                _dialoguesByGroup[groupKey] = list;
            }
            list.Add(raw);
        }

        _loadedPath = filePath;
        AppLogger.Info($"DialogueService: loaded {_dialoguesByGroup.Count} dialogue groups");
    }

    /// <summary>
    /// Gets mystery dialogues grouped into wiki tabs.
    /// Standard mystery: Intro, LastCollectibleItemDiscovered, Decoration_Slot1-5, AllRewardsCompleted.
    /// Pet mystery: Intro, TA1 (pet), TA2-TA3 (decorations), LastCollectibleItemDiscovered, AllRewardsCompleted.
    /// </summary>
    public List<DialogueGroup> GetMysteryDialogues(string progressionEventId, MysteryType mysteryType, string? petName)
    {
        if (_dialoguesByGroup == null)
            return new List<DialogueGroup>();

        // Set pet display name for "Pet" speaker replacement
        _currentPetDisplayName = petName;

        var prefix = progressionEventId;
        var groups = new List<DialogueGroup>();

        if (mysteryType == MysteryType.Pet)
            BuildPetGroups(prefix, petName ?? "Pet", groups);
        else
            BuildStandardGroups(prefix, groups);

        return groups;
    }

    /// <summary>
    /// Checks whether any dialogues exist for the given progression event.
    /// </summary>
    public bool HasDialogues(string progressionEventId)
    {
        if (_dialoguesByGroup == null) return false;
        return _dialoguesByGroup.Keys.Any(k =>
            k.StartsWith(progressionEventId, StringComparison.OrdinalIgnoreCase));
    }

    // ── Standard mystery tab mapping ────────────────────────────

    private void BuildStandardGroups(string prefix, List<DialogueGroup> groups)
    {
        TryAddGroupFuzzy(groups, prefix, "Intro", "Event Intro");
        TryAddGroupFuzzy(groups, prefix, "LastCollectibleItemDiscovered", "Getting Event Item L4");

        var decoSlots = FindDecorationSlots(prefix);
        for (int i = 0; i < decoSlots.Count; i++)
            TryAddGroupFuzzy(groups, prefix, decoSlots[i], $"Decoration Level {i + 1}");

        TryAddGroupFuzzy(groups, prefix, "AllRewardsCompleted", "Event Outro");
    }

    // ── Pet mystery tab mapping ─────────────────────────────────

    private void BuildPetGroups(string prefix, string petName, List<DialogueGroup> groups)
    {
        TryAddGroupFuzzy(groups, prefix, "Intro", "Event Intro");
        TryAddGroupFuzzy(groups, prefix, "LastCollectibleItemDiscovered", "Getting Event Item L4");
        TryAddGroupFuzzy(groups, prefix, "TA1", $"Getting {petName}");
        TryAddGroupFuzzy(groups, prefix, "TA2", "Decoration Level 1");
        TryAddGroupFuzzy(groups, prefix, "TA3", "Decoration Level 2");

        // Event Outro
        TryAddGroupFuzzy(groups, prefix, "AllRewardsCompleted", "Event Outro");
    }

    /// <summary>
    /// Finds decoration slot keys for a given mystery prefix.
    /// Handles both old format (Decoration_Slot1) and new format (Decoration_Slot25).
    /// Returns sorted list of slot key suffixes (e.g., "Decoration_Slot25", "Decoration_Slot26").
    /// </summary>
    private List<string> FindDecorationSlots(string prefix)
    {
        if (_dialoguesByGroup == null) return new List<string>();

        return _dialoguesByGroup.Keys
            .Where(k => k.StartsWith($"{prefix}_Decoration_Slot", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith($"{prefix}_Decoration_Slot", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[(prefix.Length + 1)..]) // strip prefix + underscore → "Decoration_SlotNN" or "Decoration_SlotNN_Dialogue"
            .Select(k => k.EndsWith("_Dialogue", StringComparison.OrdinalIgnoreCase)
                ? k[..^"_Dialogue".Length] : k) // strip _Dialogue suffix
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Tries to find a dialogue group with fuzzy matching:
    /// tries "{prefix}_{suffix}" first, then "{prefix}_{suffix}_Dialogue".
    /// </summary>
    private void TryAddGroupFuzzy(List<DialogueGroup> groups, string prefix, string suffix, string tabName)
    {
        // Try exact key first
        var key = $"{prefix}_{suffix}";
        if (_dialoguesByGroup != null && _dialoguesByGroup.ContainsKey(key))
        {
            TryAddGroup(groups, key, tabName);
            return;
        }

        // Try with _Dialogue suffix
        var keyWithDialogue = $"{prefix}_{suffix}_Dialogue";
        TryAddGroup(groups, keyWithDialogue, tabName);
    }

    private void TryAddGroup(List<DialogueGroup> groups, string groupKey, string tabName)
    {
        if (_dialoguesByGroup == null) return;
        if (!_dialoguesByGroup.TryGetValue(groupKey, out var entries)) return;

        var lines = new List<DialogueLine>();
        foreach (var e in entries)
        {
            // Skip empty/NoChange state markers
            if (string.IsNullOrWhiteSpace(e.Text)) continue;

            var speaker = e.LeftSpeaks ? e.LeftCharacter : e.RightCharacter;
            if (string.IsNullOrEmpty(speaker) || speaker == "NoChange") continue;

            // Replace "Pet" with actual pet display name (from Pets.json)
            if (speaker == "Pet" && !string.IsNullOrEmpty(_currentPetDisplayName))
                speaker = _currentPetDisplayName;
            else
                speaker = FormatCharacterName(speaker);

            lines.Add(new DialogueLine { Speaker = speaker, Text = e.Text });
        }

        if (lines.Count > 0)
            groups.Add(new DialogueGroup { TabName = tabName, Lines = lines });
    }

    // ── Wiki formatting ─────────────────────────────────────────

    /// <summary>
    /// Formats dialogue groups as wiki tabber content.
    /// </summary>
    public static string FormatAsWikiTabber(List<DialogueGroup> groups)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<tabber>");

        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            sb.AppendLine($"|-| {group.TabName} =");

            for (int j = 0; j < group.Lines.Count; j++)
            {
                var line = group.Lines[j];
                // Normalize curly/typographic apostrophes to ASCII straight quote
                var text = line.Text.Replace('\u2019', '\'').Replace('\u2018', '\'')
                    .Replace('\u201C', '"').Replace('\u201D', '"');
                sb.AppendLine($"'''{line.Speaker}''': {text}");

                // Empty line between replies (but not after the last one)
                if (j < group.Lines.Count - 1)
                    sb.AppendLine();
            }

            sb.AppendLine();
        }

        sb.AppendLine("</tabber>");
        return sb.ToString();
    }

    // ── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Extracts group key from DialogItemId by removing trailing _NN numeric suffix.
    /// E.g., "SP_Omoide2026_Intro_01" → "SP_Omoide2026_Intro"
    /// </summary>
    private static string ExtractGroupKey(string dialogItemId)
    {
        // Remove trailing _digits (e.g., _01, _12)
        var match = Regex.Match(dialogItemId, @"^(.+?)_(\d{2,})$");
        return match.Success ? match.Groups[1].Value : dialogItemId;
    }

    /// <summary>
    /// Converts PascalCase character names to space-separated display names.
    /// E.g., "GrandmaUrsula" → "Grandma Ursula", "Maddie" → "Maddie"
    /// </summary>
    /// <summary>
    /// Maps game internal character names to wiki display names.
    /// Most characters use their internal name, but some differ.
    /// </summary>
    private static readonly Dictionary<string, string> CharacterDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grandma"] = "Ursula",
        ["Voyance"] = "Lady Voyance",
        ["AntiqueDealer"] = "Julius",
        ["Malzar"] = "Malzar",
        ["SgtPepper"] = "Sgt. Pepper",
        ["MysteryMachine"] = "Mystery Machine",
        ["PrisonerBluetooth"] = "Bluetooth",
        ["PrisonerGrace"] = "Grace",
        ["PrisonerIzzy"] = "Izzy",
        ["Pet"] = "Pet",
        ["Phone"] = "Phone",
        ["Ghost"] = "Ghost",
        ["Dog"] = "Dog",
        ["Empty"] = "",
    };

    /// <summary>Public accessor for character name formatting (used by MysteryWikiService for pet names).</summary>
    public static string FormatCharacterNamePublic(string name) => FormatCharacterName(name);

    private static string FormatCharacterName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Check explicit mapping first
        if (CharacterDisplayNames.TryGetValue(name, out var displayName))
            return displayName;

        // PascalCase → space-separated (e.g., "KleptoAndBandit" → "Klepto And Bandit")
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            else if (i > 0 && char.IsUpper(name[i]) && i + 1 < name.Length
                     && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    private static string GetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? "";
        return "";
    }

    private static bool GetBool(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return false;
    }

    private class RawDialogueEntry
    {
        public string DialogItemId { get; set; } = "";
        public string Text { get; set; } = "";
        public string LeftCharacter { get; set; } = "";
        public string RightCharacter { get; set; } = "";
        public bool LeftSpeaks { get; set; }
        public bool RightSpeaks { get; set; }
    }
}
