using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic;
using GameLogic.Config;
using GameLogic.Player.Items;
using GameLogic.Story;
using merge_mansion_dumper.Dumper.Base;
using merge_mansion_dumper.Dumper.Json;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace merge_mansion_dumper.Dumper
{
    public class DialogueDumper : JsonDumper<IDictionary<string, object>>
    {
        public override IDictionary<string, object> Dump(SharedGameConfig config)
        {
            var allDialogues = new List<Dictionary<string, object>>();

            // 1. Global DialogItems registry
            if (config.DialogItems != null)
            {
                foreach (var x in config.DialogItems.EnumerateAll())
                {
                    var d = (DialogItemInfo)x.Value;
                    allDialogues.Add(SerializeDialogItem(d));
                }
            }

            // 2. StoryElements — may reference DialogItems by key.
            //    Some dialogues are only reachable through StoryElements, not in the global registry.
            //    Try both: resolved MetaRef and key-based lookup from global DialogItems.
            if (config.StoryElements != null)
            {
                var globalIds = new HashSet<string>(
                    allDialogues.Select(d => d["DialogItemId"]?.ToString() ?? ""),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var story in config.StoryElements.EnumerateAll())
                {
                    var storyInfo = (StoryElementInfo)story.Value;
                    if (storyInfo.DialogItems == null) continue;

                    foreach (var kvp in storyInfo.DialogItems)
                    {
                        var dialogId = kvp.Key;
                        var dialogRef = kvp.Value;
                        var idStr = dialogId?.ToString();

                        // Skip if already exported
                        if (!string.IsNullOrEmpty(idStr) && globalIds.Contains(idStr))
                            continue;

                        // Try 1: resolved MetaRef
                        DialogItemInfo resolved = null;
                        try { resolved = dialogRef?.Ref; } catch { }

                        // Try 2: lookup from global DialogItems by key
                        if (resolved == null && dialogId != null && config.DialogItems != null)
                        {
                            try
                            {
                                foreach (var global in config.DialogItems.EnumerateAll())
                                {
                                    var gi = (DialogItemInfo)global.Value;
                                    if (gi.DialogItemId?.ToString() == idStr)
                                    {
                                        resolved = gi;
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }

                        if (resolved == null) continue;

                        allDialogues.Add(SerializeDialogItem(resolved));
                        if (!string.IsNullOrEmpty(idStr))
                            globalIds.Add(idStr);
                    }
                }
            }

            // Export all Dialog_Title_ character name mappings from localization
            var characterNames = new Dictionary<string, string>();
            foreach (var enumVal in Enum.GetValues(typeof(DialogCharacterType)))
            {
                var name = enumVal.ToString();
                if (name == "NoChange" || name == "None" || name == "Empty") continue;
                var displayName = Localize($"Dialog_Title_{name}");
                if (!string.IsNullOrEmpty(displayName))
                    characterNames[name] = displayName;
            }

            // 4. CollectibleDialoguesInfo — maps items/decorations to dialogue triggers
            var collectibleDialogueMapping = new List<Dictionary<string, object>>();
            if (config.CollectibleDialoguesInfo != null)
            {
                foreach (var kvp in config.CollectibleDialoguesInfo.EnumerateAll())
                {
                    var info = (CollectibleDialoguesInfo)kvp.Value;
                    var entry = new Dictionary<string, object>
                    {
                        ["ConfigKey"] = info.ConfigKey?.ToString(),
                        ["RequiredBoardEventIds"] = info.RequiredBoardEventIds
                    };

                    // Item dialogues — resolve int ConfigKey hashes to ItemType strings
                    if (info.ItemDialogues != null)
                    {
                        var itemEntries = new List<Dictionary<string, object>>();
                        foreach (var itemDialogue in info.ItemDialogues)
                        {
                            var itemEntry = new Dictionary<string, object>
                            {
                                ["StoryDefinitionId"] = itemDialogue.StoryInfo?.KeyObject?.ToString(),
                                ["GroupId"] = itemDialogue.GroupId?.ToString()
                            };

                            // Resolve ItemTypes (int hashes) to readable names
                            if (itemDialogue.ItemTypes != null)
                            {
                                var resolvedItems = new List<string>();
                                foreach (var itemTypeHash in itemDialogue.ItemTypes)
                                {
                                    if (config.Items != null && config.Items.TryGetValue(itemTypeHash, out var itemDef))
                                        resolvedItems.Add(itemDef.ItemType);
                                    else
                                        resolvedItems.Add(itemTypeHash.ToString());
                                }
                                itemEntry["ItemTypes"] = resolvedItems;
                            }

                            itemEntries.Add(itemEntry);
                        }
                        entry["ItemDialogues"] = itemEntries;
                    }

                    // Decoration dialogues
                    if (info.DecorationsDialogues != null)
                    {
                        var decoEntries = new List<Dictionary<string, object>>();
                        foreach (var decoDialogue in info.DecorationsDialogues)
                        {
                            var decoEntry = new Dictionary<string, object>
                            {
                                ["StoryDefinitionId"] = decoDialogue.StoryInfo?.KeyObject?.ToString(),
                                ["GroupId"] = decoDialogue.GroupId?.ToString()
                            };

                            try
                            {
                                var decoInfo = decoDialogue.DecorationInfo?.Ref;
                                if (decoInfo != null)
                                    decoEntry["DecorationConfigKey"] = decoInfo.ConfigKey?.ToString();
                            }
                            catch { }

                            decoEntries.Add(decoEntry);
                        }
                        entry["DecorationsDialogues"] = decoEntries;
                    }

                    collectibleDialogueMapping.Add(entry);
                }
            }

            return new Dictionary<string, object>
            {
                ["Dialogues"] = allDialogues.ToArray(),
                ["CharacterNames"] = characterNames,
                ["CollectibleDialogueMapping"] = collectibleDialogueMapping
            };
        }

        private Dictionary<string, object> SerializeDialogItem(DialogItemInfo d)
        {
            var leftName = d.LeftCharacter.ToString();
            var rightName = d.RightCharacter.ToString();

            var dict = new Dictionary<string, object>
            {
                ["DialogItemId"] = d.DialogItemId?.ToString(),
                ["LocalizationId"] = d.LocalizationId,
                ["Text"] = Localize(d.LocalizationId),
                ["DialogMode"] = d.DialogMode.ToString(),
                ["LeftCharacter"] = leftName,
                ["LeftCharacterState"] = d.LeftCharacterState.ToString(),
                ["LeftSpeaks"] = d.LeftSpeaks,
                ["RightCharacter"] = rightName,
                ["RightCharacterState"] = d.RightCharacterState.ToString(),
                ["RightSpeaks"] = d.RightSpeaks,
                ["WaitConfirmation"] = d.WaitConfirmation,
            };

            // Resolve display names from localization (Dialog_Title_{CharacterType})
            var leftDisplay = LocalizeCharacterName(leftName);
            if (leftDisplay != null)
                dict["LeftCharacterDisplayName"] = leftDisplay;
            var rightDisplay = LocalizeCharacterName(rightName);
            if (rightDisplay != null)
                dict["RightCharacterDisplayName"] = rightDisplay;

            if (!string.IsNullOrEmpty(d.LeftCharacterConfigId))
                dict["LeftCharacterConfigId"] = d.LeftCharacterConfigId;
            if (!string.IsNullOrEmpty(d.RightCharacterConfigId))
                dict["RightCharacterConfigId"] = d.RightCharacterConfigId;

            return dict;
        }

        private static string LocalizeCharacterName(string characterType)
        {
            if (string.IsNullOrEmpty(characterType) || characterType == "NoChange" || characterType == "None" || characterType == "Empty")
                return null;
            return Localize($"Dialog_Title_{characterType}");
        }

        private static string Localize(string locId)
        {
            if (string.IsNullOrEmpty(locId))
                return null;

            var lang = MetaplaySDK.ActiveLanguage;
            if (lang?.Translations == null)
                return null;

            return lang.Translations.TryGetValue(TranslationId.FromString(locId), out var translation)
                ? translation
                : null;
        }

        protected override JsonSerializerSettings CreateSettings(SharedGameConfig config)
        {
            return new JsonSerializerSettings(base.CreateSettings(config))
            {
                Converters =
                {
                    new MergeMansionJsonConverter(config, Output, false),
                    new StringEnumConverter()
                }
            };
        }
    }
}
