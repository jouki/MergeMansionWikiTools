using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Config;
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

            return new Dictionary<string, object>
            {
                ["Dialogues"] = allDialogues.ToArray()
            };
        }

        private Dictionary<string, object> SerializeDialogItem(DialogItemInfo d)
        {
            return new Dictionary<string, object>
            {
                ["DialogItemId"] = d.DialogItemId?.ToString(),
                ["LocalizationId"] = d.LocalizationId,
                ["Text"] = Localize(d.LocalizationId),
                ["DialogMode"] = d.DialogMode.ToString(),
                ["LeftCharacter"] = d.LeftCharacter.ToString(),
                ["LeftCharacterState"] = d.LeftCharacterState.ToString(),
                ["LeftSpeaks"] = d.LeftSpeaks,
                ["RightCharacter"] = d.RightCharacter.ToString(),
                ["RightCharacterState"] = d.RightCharacterState.ToString(),
                ["RightSpeaks"] = d.RightSpeaks,
                ["WaitConfirmation"] = d.WaitConfirmation,
            };
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
