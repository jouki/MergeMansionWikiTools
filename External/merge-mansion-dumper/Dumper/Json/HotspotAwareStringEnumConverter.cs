using System;
using GameLogic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace merge_mansion_dumper.Dumper.Json
{
    /// <summary>
    /// CUSTOM: <see cref="StringEnumConverter"/> that writes <see cref="HotspotId"/> values through
    /// <see cref="HotspotIdNames"/>, so members the compiled enum doesn't know (new game version)
    /// still serialize as their string name instead of an integer. All other enums behave exactly
    /// like the stock converter. Used by every JSON dumper in place of <c>new StringEnumConverter()</c>.
    /// </summary>
    public class HotspotAwareStringEnumConverter : StringEnumConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is HotspotId id)
            {
                var name = HotspotIdNames.TryResolveOverride(id);
                if (name != null)
                {
                    writer.WriteValue(name);
                    return;
                }
            }
            base.WriteJson(writer, value, serializer);
        }
    }
}
