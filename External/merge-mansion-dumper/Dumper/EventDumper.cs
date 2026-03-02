// Modified by Jouki (2026) — Null-conditional for all event categories (BoardEvents removed in v26)
using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Config;
using merge_mansion_dumper.Dumper.Base;
using merge_mansion_dumper.Dumper.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace merge_mansion_dumper.Dumper
{
    public class EventDumper : JsonDumper<IDictionary<string, object>>
    {
        public override IDictionary<string, object> Dump(SharedGameConfig config)
        {
            var events = new Dictionary<string, object>
            {
                //["Boards"] = config.BoardEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["CollectibleBoards"] = config.CollectibleBoardEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["Progressions"] = config.ProgressionEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["Leaderboards"] = config.LeaderboardEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["GarageCleanups"] = config.GarageCleanupEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["Shops"] = config.ShopEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["DailyTasks"] = config.DailyTasks?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
                ["DailyTasksV2"] = config.DailyTasksV2?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>()
            };

            return events;
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
