using System.Collections.Generic;
using System.Linq;
using GameLogic.Area;
using GameLogic.Config;
using merge_mansion_dumper.Dumper.Base;
using merge_mansion_dumper.Dumper.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace merge_mansion_dumper.Dumper
{
    public class AreaDumper : JsonDumper<IList<AreaInfo>>
    {
        public override IList<AreaInfo> Dump(SharedGameConfig config)
        {
            if (config.Areas == null)
                throw new System.InvalidOperationException("Areas is null — import may have failed for this entry. Check import log for errors.");
            return config.Areas.EnumerateAll().Select(x => (AreaInfo)x.Value).ToList();
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
