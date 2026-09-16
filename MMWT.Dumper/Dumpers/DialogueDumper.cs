using GameLogic;
using GameLogic.Config;
using GameLogic.Story;
using MergeMansionWikiTools.Dumper.Core;

namespace MergeMansionWikiTools.Dumper.Dumpers;

/// <summary>
/// Produces <c>dialogues.json</c>: every dialogue line in the game, the character-name table, and
/// the item/decoration triggers that fire a dialogue.
/// <para>
/// The game stores dialogue lines in TWO places, and both have to be walked: the global
/// <c>DialogItems</c> registry, and the per-story <c>StoryElementInfo.DialogItems</c> maps, which
/// can reference lines the global registry does not list. The second pass therefore skips every id
/// the first pass already exported and resolves the reference for the rest.
/// </para>
/// </summary>
public sealed class DialogueDumper
{
    private readonly IDumpLog _log;

    public DialogueDumper(IDumpLog? log = null) => _log = log ?? ConsoleDumpLog.Instance;

    public string Dump(SharedGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var data = new Dictionary<string, object?>
        {
            ["Dialogues"] = CollectDialogues(config),
            ["CharacterNames"] = CollectCharacterNames(),
            ["CollectibleDialogueMapping"] = CollectTriggers(config),
        };

        // No file-specific shape: every value here is a plain string, bool or dictionary the shared
        // stack already writes correctly (the only game types that reach it are the id lists on
        // CollectibleDialoguesInfo).
        var settings = DumpConverters.MainFileSettings(config, _log, MetaRefSkip.EmptyKey);
        var json = DumpJson.Serialize(data, config.ArchiveCreatedAt, DumpFileKind.Main, settings);
        DumpConverters.LogRequirementSummary(settings, _log, "dialogues.json");
        return json;
    }

    private object[] CollectDialogues(SharedGameConfig config)
    {
        var all = new List<Dictionary<string, object?>>();

        // 1. The global registry, in library order.
        if (config.DialogItems != null)
            foreach (var x in config.DialogItems.EnumerateAll())
                all.Add(SerializeDialogItem((DialogItemInfo)x.Value));

        // 2. Story elements, which may carry lines the registry above does not.
        if (config.StoryElements != null)
        {
            // Built once, from every id pass 1 exported, and grown as pass 2 adds more. A story entry
            // whose id is already here is a duplicate of a line the global registry supplied.
            var seen = new HashSet<string>(all.Select(d => d["DialogItemId"]?.ToString() ?? ""), StringComparer.OrdinalIgnoreCase);
            var unresolved = 0;

            foreach (var story in config.StoryElements.EnumerateAll())
            {
                var storyInfo = (StoryElementInfo)story.Value;
                if (storyInfo.DialogItems == null) continue;

                foreach (var (dialogId, dialogRef) in storyInfo.DialogItems)
                {
                    var idStr = dialogId?.ToString();
                    if (!string.IsNullOrEmpty(idStr) && seen.Contains(idStr)) continue;

                    // Reaching here means the id is NOT in the global registry, so resolving the
                    // reference is the only way to get the line: MetaRef.Ref throws when it does not
                    // resolve, which is why the read is guarded rather than null-checked.
                    // (A "look the id up in config.DialogItems instead" fallback would be dead code:
                    // `seen` already holds every global id, so anything that gets past the guard
                    // above cannot be in that library either.)
                    DialogItemInfo? resolved = null;
                    try { resolved = dialogRef?.Ref; }
                    catch { /* unresolved — counted below, never logged per line: thousands of these */ }

                    if (resolved == null) { unresolved++; continue; }

                    all.Add(SerializeDialogItem(resolved));
                    if (!string.IsNullOrEmpty(idStr)) seen.Add(idStr);
                }
            }

            if (unresolved > 0)
                _log.Trace($"Story dialogue entries not exported (reference does not resolve): {unresolved}");
        }

        _log.Trace($"Dialogues: {all.Count}");
        return all.ToArray<object>();
    }

    /// <summary>
    /// The <c>DialogCharacterType</c> -&gt; localized-name table, from the <c>Dialog_Title_{Name}</c>
    /// keys. The three non-character members are skipped, and so is any member whose key has no
    /// translation.
    /// </summary>
    private static Dictionary<string, string> CollectCharacterNames()
    {
        var names = new Dictionary<string, string>();
        foreach (var enumVal in Enum.GetValues(typeof(DialogCharacterType)))
        {
            var name = enumVal?.ToString();
            if (name == null || IsNonCharacter(name)) continue;
            var displayName = Loc.Translate($"Dialog_Title_{name}");
            if (!string.IsNullOrEmpty(displayName)) names[name] = displayName;
        }
        return names;
    }

    private List<Dictionary<string, object?>> CollectTriggers(SharedGameConfig config)
    {
        var mapping = new List<Dictionary<string, object?>>();
        if (config.CollectibleDialoguesInfo == null) return mapping;

        foreach (var kvp in config.CollectibleDialoguesInfo.EnumerateAll())
        {
            var info = (CollectibleDialoguesInfo)kvp.Value;
            var entry = new Dictionary<string, object?>
            {
                ["ConfigKey"] = info.ConfigKey?.ToString(),
                ["RequiredBoardEventIds"] = info.RequiredBoardEventIds,
            };

            if (info.ItemDialogues != null)
            {
                var itemEntries = new List<Dictionary<string, object?>>();
                foreach (var itemDialogue in info.ItemDialogues)
                {
                    var itemEntry = new Dictionary<string, object?>
                    {
                        ["StoryDefinitionId"] = itemDialogue.StoryInfo?.KeyObject?.ToString(),
                        ["GroupId"] = itemDialogue.GroupId?.ToString(),
                    };

                    // The config stores trigger items as int hashes; the readable ItemType is what
                    // consumers need, since a dialogue's own id does NOT name its trigger item.
                    if (itemDialogue.ItemTypes != null)
                    {
                        var resolvedItems = new List<string>();
                        foreach (var itemTypeHash in itemDialogue.ItemTypes)
                        {
                            resolvedItems.Add(config.Items != null && config.Items.TryGetValue(itemTypeHash, out var itemDef)
                                ? itemDef.ItemType
                                : itemTypeHash.ToString());
                        }
                        itemEntry["ItemTypes"] = resolvedItems;
                    }

                    itemEntries.Add(itemEntry);
                }
                entry["ItemDialogues"] = itemEntries;
            }

            if (info.DecorationsDialogues != null)
            {
                var decoEntries = new List<Dictionary<string, object?>>();
                foreach (var decoDialogue in info.DecorationsDialogues)
                {
                    var decoEntry = new Dictionary<string, object?>
                    {
                        ["StoryDefinitionId"] = decoDialogue.StoryInfo?.KeyObject?.ToString(),
                        ["GroupId"] = decoDialogue.GroupId?.ToString(),
                    };

                    try
                    {
                        var decoInfo = decoDialogue.DecorationInfo?.Ref;
                        if (decoInfo != null) decoEntry["DecorationConfigKey"] = decoInfo.ConfigKey?.ToString();
                    }
                    catch
                    {
                        // An unresolved decoration simply loses the DecorationConfigKey key, as in
                        // golden; not logged per entry, this is an expected shape, not a failure.
                    }

                    decoEntries.Add(decoEntry);
                }
                entry["DecorationsDialogues"] = decoEntries;
            }

            mapping.Add(entry);
        }

        return mapping;
    }

    private static Dictionary<string, object?> SerializeDialogItem(DialogItemInfo d)
    {
        var leftName = d.LeftCharacter.ToString();
        var rightName = d.RightCharacter.ToString();

        var dict = new Dictionary<string, object?>
        {
            ["DialogItemId"] = d.DialogItemId?.ToString(),
            ["LocalizationId"] = d.LocalizationId,
            ["Text"] = Loc.Translate(d.LocalizationId),
            ["DialogMode"] = d.DialogMode.ToString(),
            ["LeftCharacter"] = leftName,
            ["LeftCharacterState"] = d.LeftCharacterState.ToString(),
            ["LeftSpeaks"] = d.LeftSpeaks,
            ["RightCharacter"] = rightName,
            ["RightCharacterState"] = d.RightCharacterState.ToString(),
            ["RightSpeaks"] = d.RightSpeaks,
            ["WaitConfirmation"] = d.WaitConfirmation,
        };

        // The two display names are appended only when they resolve, so a line with no speaker on
        // one side carries no empty key — which is also why they sit after WaitConfirmation.
        var leftDisplay = LocalizeCharacterName(leftName);
        if (leftDisplay != null) dict["LeftCharacterDisplayName"] = leftDisplay;
        var rightDisplay = LocalizeCharacterName(rightName);
        if (rightDisplay != null) dict["RightCharacterDisplayName"] = rightDisplay;

        if (!string.IsNullOrEmpty(d.LeftCharacterConfigId)) dict["LeftCharacterConfigId"] = d.LeftCharacterConfigId;
        if (!string.IsNullOrEmpty(d.RightCharacterConfigId)) dict["RightCharacterConfigId"] = d.RightCharacterConfigId;

        return dict;
    }

    private static string? LocalizeCharacterName(string? characterType)
        => string.IsNullOrEmpty(characterType) || IsNonCharacter(characterType)
            ? null
            : Loc.Translate($"Dialog_Title_{characterType}");

    /// <summary>The three <c>DialogCharacterType</c> members that are states, not people.</summary>
    private static bool IsNonCharacter(string name) => name is "NoChange" or "None" or "Empty";
}
