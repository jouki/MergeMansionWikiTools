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
    private Dictionary<string, string>? _characterNames; // from CharacterNames in dialogues.json (localized Dialog_Title_)
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
                LeftCharacterConfigId = GetString(entry, "LeftCharacterConfigId"),
                RightCharacterConfigId = GetString(entry, "RightCharacterConfigId"),
                LeftCharacterDisplayName = GetString(entry, "LeftCharacterDisplayName"),
                RightCharacterDisplayName = GetString(entry, "RightCharacterDisplayName"),
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

        // Load CharacterNames mapping (Dialog_Title_ localization, exported by dumper)
        _characterNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("CharacterNames", out var charNames) && charNames.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in charNames.EnumerateObject())
            {
                var displayName = prop.Value.GetString();
                if (!string.IsNullOrEmpty(displayName))
                    _characterNames[prop.Name] = displayName;
            }
            AppLogger.Info($"DialogueService: loaded {_characterNames.Count} character name mappings");
        }

        _loadedPath = filePath;
        AppLogger.Info($"DialogueService: loaded {_dialoguesByGroup.Count} dialogue groups");
    }

    /// <summary>
    /// Gets mystery dialogues grouped into wiki tabs.
    /// Standard mystery: Intro, LastCollectibleItemDiscovered, Decoration_Slot1-5, AllRewardsCompleted.
    /// Pet mystery: Intro, TA1 (pet), TA2-TA3 (decorations), LastCollectibleItemDiscovered, AllRewardsCompleted.
    /// </summary>
    public List<DialogueGroup> GetMysteryDialogues(string progressionEventId, MysteryType mysteryType, string? petName, int decoCount = 0, List<string>? orderedDecoSlotIds = null)
    {
        if (_dialoguesByGroup == null)
            return new List<DialogueGroup>();

        // Set pet display name for "Pet" speaker replacement
        _currentPetDisplayName = petName;

        var prefix = ResolvePrefix(progressionEventId);
        var groups = new List<DialogueGroup>();

        if (mysteryType == MysteryType.Pet)
            BuildPetGroups(prefix, petName ?? "Pet", groups);
        else
            BuildStandardGroups(prefix, groups, decoCount, orderedDecoSlotIds);

        return groups;
    }

    /// <summary>
    /// Checks whether any dialogues exist for the given progression event.
    /// </summary>
    public bool HasDialogues(string progressionEventId)
    {
        if (_dialoguesByGroup == null) return false;
        var prefix = ResolvePrefix(progressionEventId);
        return _dialoguesByGroup.Keys.Any(k =>
            k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the actual dialogue prefix for a progressionEventId.
    /// Handles alt prefix mapping for pet mysteries where dialogue keys
    /// may use a different prefix (e.g., SP_BunnyPet2025 → SP_PigBirthday2025).
    /// </summary>
    private string ResolvePrefix(string progressionEventId)
    {
        if (_dialoguesByGroup == null) return progressionEventId;

        // Direct match — most common case
        if (_dialoguesByGroup.Keys.Any(k =>
            k.StartsWith(progressionEventId, StringComparison.OrdinalIgnoreCase)))
            return progressionEventId;

        // Alt prefix: extract keyword from event ID and search keys
        // SP_BunnyPet2025 → keyword "Bunny"
        var stripped = progressionEventId.Replace("SP_", "", StringComparison.OrdinalIgnoreCase);
        stripped = Regex.Replace(stripped, @"Pet\d*$", ""); // remove "Pet2025"
        stripped = Regex.Replace(stripped, @"\d+$", ""); // remove trailing year

        if (string.IsNullOrEmpty(stripped)) return progressionEventId;

        // Find an SP_ key that contains this keyword and has _Intro suffix
        var altKey = _dialoguesByGroup.Keys
            .FirstOrDefault(k => k.StartsWith("SP_", StringComparison.OrdinalIgnoreCase)
                && k.Contains(stripped, StringComparison.OrdinalIgnoreCase)
                && k.Contains("_Intro", StringComparison.OrdinalIgnoreCase));

        if (altKey != null)
        {
            // Extract prefix: everything before "_Intro"
            var introIdx = altKey.IndexOf("_Intro", StringComparison.OrdinalIgnoreCase);
            if (introIdx > 0)
            {
                var altPrefix = altKey[..introIdx];
                AppLogger.Info($"DialogueService: alt prefix '{altPrefix}' for '{progressionEventId}'");
                return altPrefix;
            }
        }

        return progressionEventId;
    }

    // ── Standard mystery tab mapping ────────────────────────────

    private void BuildStandardGroups(string prefix, List<DialogueGroup> groups, int decoCount, List<string>? orderedDecoSlotIds = null)
    {
        TryAddGroupFuzzy(groups, prefix, "Intro", "Event Intro");
        TryAddGroupFuzzy(groups, prefix, "LastCollectibleItemDiscovered", "Getting Event Item L4");

        // Decoration levels: use reward-tier ordering when available, fallback to alphabetical
        var dialogueSlots = FindDecorationSlots(prefix);
        if (orderedDecoSlotIds != null && orderedDecoSlotIds.Count > 0)
        {
            // Map dialogue slots to their reward-tier position
            // orderedDecoSlotIds contains full IDs like "SP_Pickleball2025_Decoration_Slot33"
            // dialogueSlots contains suffixes like "Decoration_Slot33"
            var orderedSuffixes = new List<string>();
            foreach (var fullId in orderedDecoSlotIds)
            {
                // Extract suffix: strip progressionEventId prefix → "Decoration_SlotNN"
                var suffix = dialogueSlots.FirstOrDefault(s =>
                    fullId.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                if (suffix != null)
                    orderedSuffixes.Add(suffix);
            }
            // Only include slots present in rewards — extra dialogue slots (not in rewards) are excluded
            dialogueSlots = orderedSuffixes;
        }

        for (int i = 0; i < dialogueSlots.Count; i++)
            TryAddGroupFuzzy(groups, prefix, dialogueSlots[i], $"Decoration Level {i + 1}");
        for (int i = dialogueSlots.Count; i < decoCount; i++)
            AddEmptyGroup(groups, $"Decoration Level {i + 1}");

        TryAddGroupFuzzy(groups, prefix, "AllRewardsCompleted", "Event Outro");
    }

    // ── Pet mystery tab mapping ─────────────────────────────────

    private void BuildPetGroups(string prefix, string petName, List<DialogueGroup> groups)
    {
        TryAddGroupFuzzy(groups, prefix, "Intro", "Event Intro");
        TryAddGroupFuzzy(groups, prefix, "LastCollectibleItemDiscovered", $"Getting {petName}");

        // TA mapping depends on how many TAs exist:
        // 3 TAs (newer pets): TA1=Getting Pet, TA2=Deco1, TA3=Deco2
        // 2 TAs (older pets, no TA3): TA1=Deco1, TA2=Deco2
        //   (3 oldest pets have no "Getting Pet" dialogue in game data — wiki content was added manually)
        bool hasTA3 = HasGroupFuzzy(prefix, "TA3");
        if (hasTA3)
        {
            TryAddGroupFuzzy(groups, prefix, "TA1", $"Getting {petName}");
            TryAddGroupFuzzy(groups, prefix, "TA2", "Decoration Level 1");
            TryAddGroupFuzzy(groups, prefix, "TA3", "Decoration Level 2");
        }
        else
        {
            // Older pets: no "Getting Pet" dialogue in game data — add empty header for merge from wiki
            AddEmptyGroup(groups, $"Getting {petName}");
            TryAddGroupFuzzy(groups, prefix, "TA1", "Decoration Level 1");
            TryAddGroupFuzzy(groups, prefix, "TA2", "Decoration Level 2");
        }

        TryAddGroupFuzzy(groups, prefix, "AllRewardsCompleted", "Event Outro");
    }

    /// <summary>Checks if a dialogue group exists for the given prefix + suffix.</summary>
    private bool HasGroupFuzzy(string prefix, string suffix)
    {
        if (_dialoguesByGroup == null) return false;
        var key = $"{prefix}_{suffix}";
        var keyD = $"{prefix}_{suffix}_Dialogue";
        return _dialoguesByGroup.ContainsKey(key) || _dialoguesByGroup.ContainsKey(keyD);
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
    /// Always adds the tab header even if no dialogues are found (empty placeholder for merge from wiki).
    /// </summary>
    private void TryAddGroupFuzzy(List<DialogueGroup> groups, string prefix, string suffix, string tabName)
    {
        // Try exact key first — but fall through to _Dialogue variant if all entries have null Text
        var key = $"{prefix}_{suffix}";
        if (_dialoguesByGroup != null && _dialoguesByGroup.ContainsKey(key))
        {
            var lines = BuildDialogueLines(_dialoguesByGroup[key]);
            if (lines.Count > 0)
            {
                groups.Add(new DialogueGroup { TabName = tabName, Lines = lines });
                return;
            }
        }

        // Try with _Dialogue suffix (StoryElements entries — may have resolved text when global ones don't)
        var keyWithDialogue = $"{prefix}_{suffix}_Dialogue";
        if (_dialoguesByGroup != null && _dialoguesByGroup.ContainsKey(keyWithDialogue))
        {
            TryAddGroup(groups, keyWithDialogue, tabName);
            return;
        }

        // No dialogue found — still add empty tab header
        AddEmptyGroup(groups, tabName);
    }

    private List<DialogueLine> BuildDialogueLines(List<RawDialogueEntry> entries)
    {
        var lines = new List<DialogueLine>();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) continue;

            var speaker = e.LeftSpeaks ? e.LeftCharacter : e.RightCharacter;
            if (string.IsNullOrEmpty(speaker) || speaker == "NoChange") continue;

            speaker = ResolveSpeaker(e, speaker);

            lines.Add(new DialogueLine { Speaker = speaker, Text = e.Text });
        }
        return lines;
    }

    /// <summary>
    /// Resolves a speaker's display name using all available data sources.
    /// Priority: 1) Per-entry DisplayName from dump (localized Dialog_Title_),
    /// 2) CharacterNames table from dump (Dialog_Title_ for all enum values),
    /// 3) For Pet: CharacterConfigId → Pets.json display name,
    /// 4) For Pet: _currentPetDisplayName (from mystery.PetName),
    /// 5) CharacterDisplayNames fallback (for characters missing from localization entirely).
    /// </summary>
    private string ResolveSpeaker(RawDialogueEntry entry, string characterType)
    {
        // 1. Per-entry localized display name (resolved at dump time from Dialog_Title_{type})
        //    Skip if value equals the enum name (localization just echoes it — e.g. Dog→"Dog", not useful)
        var displayName = entry.LeftSpeaks ? entry.LeftCharacterDisplayName : entry.RightCharacterDisplayName;
        if (!string.IsNullOrEmpty(displayName)
            && !string.Equals(displayName, characterType, StringComparison.OrdinalIgnoreCase))
            return displayName;

        // 2. CharacterNames table from dialogues.json (covers all known Dialog_Title_ entries)
        //    Skip if value equals the enum name (no useful mapping — e.g. Dog→"Dog")
        if (_characterNames != null && _characterNames.TryGetValue(characterType, out var charName)
            && !string.Equals(charName, characterType, StringComparison.OrdinalIgnoreCase))
            return charName;

        // 2. For Pet character: resolve via CharacterConfigId → Pets.json
        if (characterType == "Pet")
        {
            var configId = entry.LeftSpeaks ? entry.LeftCharacterConfigId : entry.RightCharacterConfigId;
            if (!string.IsNullOrEmpty(configId))
                return MysteryWikiService.FormatPetDisplayName(configId);

            // Search sibling group for ConfigId (parallel non-_Dialogue / _Dialogue entries)
            if (_dialoguesByGroup != null)
            {
                var groupKey = ExtractGroupKey(entry.DialogItemId);
                var siblingKey = groupKey.EndsWith("_Dialogue", StringComparison.OrdinalIgnoreCase)
                    ? groupKey[..^"_Dialogue".Length]
                    : groupKey + "_Dialogue";

                if (_dialoguesByGroup.TryGetValue(siblingKey, out var siblingEntries))
                {
                    foreach (var sib in siblingEntries)
                    {
                        var sibSpeaker = sib.LeftSpeaks ? sib.LeftCharacter : sib.RightCharacter;
                        if (sibSpeaker != "Pet") continue;
                        var sibConfigId = sib.LeftSpeaks ? sib.LeftCharacterConfigId : sib.RightCharacterConfigId;
                        if (!string.IsNullOrEmpty(sibConfigId))
                            return MysteryWikiService.FormatPetDisplayName(sibConfigId);
                    }
                }
            }

            // Pet mystery fallback (_currentPetDisplayName from mystery.PetName)
            if (!string.IsNullOrEmpty(_currentPetDisplayName))
                return _currentPetDisplayName;
        }

        // 3. Hardcoded fallback (for old dumps without DisplayName fields)
        return FormatCharacterName(characterType);
    }

    private void TryAddGroup(List<DialogueGroup> groups, string groupKey, string tabName)
    {
        if (_dialoguesByGroup == null) return;
        if (!_dialoguesByGroup.TryGetValue(groupKey, out var entries))
        {
            AddEmptyGroup(groups, tabName);
            return;
        }

        groups.Add(new DialogueGroup { TabName = tabName, Lines = BuildDialogueLines(entries) });
    }

    private static void AddEmptyGroup(List<DialogueGroup> groups, string tabName)
        => groups.Add(new DialogueGroup { TabName = tabName, Lines = new List<DialogueLine>() });

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
                // Normalize curly/typographic characters to ASCII
                var text = line.Text
                    .Replace('\u2019', '\'').Replace('\u2018', '\'')  // curly apostrophes
                    .Replace('\u201C', '"').Replace('\u201D', '"')    // curly quotes
                    .Replace("\u2026", "...")                          // ellipsis → three dots
                    .Replace("\u2013", "-").Replace("\u2014", "-");   // en/em dash → hyphen
                text = Regex.Replace(text, "<i>(.*?)</i>", "''$1''", RegexOptions.IgnoreCase);
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
    /// <summary>
    /// Fallback mapping for characters missing from Dialog_Title_ localization.
    /// Priority 1 is always the localized DisplayName from dump.
    /// This dictionary covers characters where the language file is incomplete.
    /// </summary>
    private static readonly Dictionary<string, string> CharacterDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grandma"] = "Ursula",
        ["Voyance"] = "Lady Voyance",
        ["AntiqueDealer"] = "Julius",
        ["SgtPepper"] = "Sgt. Pepper",
        ["MysteryMachine"] = "Mystery Machine",
        ["PrisonerBluetooth"] = "Bluetooth",
        ["PrisonerGrace"] = "Grace",
        ["PrisonerIzzy"] = "Izzy",
        ["Dog"] = "Rufus",
        ["Ringleader"] = "Fiona DuVal",
        ["Ghost"] = "Ghost",
        ["Phone"] = "Phone",
        ["Pet"] = "Pet",
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
        public string LeftCharacterConfigId { get; set; } = "";
        public string RightCharacterConfigId { get; set; } = "";
        public string LeftCharacterDisplayName { get; set; } = "";
        public string RightCharacterDisplayName { get; set; } = "";
        public bool LeftSpeaks { get; set; }
        public bool RightSpeaks { get; set; }
    }
}
