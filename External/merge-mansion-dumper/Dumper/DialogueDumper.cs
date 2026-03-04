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
            return new Dictionary<string, object>
            {
                ["Dialogues"] = config.DialogItems?.EnumerateAll().Select(x =>
                {
                    var d = (DialogItemInfo)x.Value;
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
                }).ToArray() ?? Array.Empty<object>()
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
