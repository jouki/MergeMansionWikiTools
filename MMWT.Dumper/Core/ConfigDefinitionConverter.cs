using Game.Cloud.Config;
using GameLogic;
using GameLogic.Config;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// <para>
/// Newtonsoft converter for the <c>ConfigDefinition&lt;TKeyObject,TValueObject&gt;</c> family
/// (<c>Game.Cloud.Config.ConfigDefinition{TKey,TValue}</c>; concrete subclasses: <see cref="HotspotDef"/>,
/// <see cref="ItemDef"/>, <c>AreaInfoDef</c>, <c>MergeChainDef</c>, <c>ConfigId&lt;,&gt;</c>). Without this
/// converter, Newtonsoft's default reflection writes these as <c>{"ConfigKey": ...}</c>; the golden
/// dump instead writes the bare resolved identifier directly (no wrapper object at all) —
/// confirmed by execution against 26.07.01 golden data for the two subclasses that appear there:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="HotspotDef"/> -&gt; the <c>HotspotId</c> NAME (e.g.
/// "LoungeRemoveCurtainDirtHb2"), via <see cref="HotspotIdNames.Resolve"/> — the same runtime name
/// registry <see cref="HotspotAwareStringEnumConverter"/> uses (per its own doc comment: "Single
/// source of truth ... requirement/JSON output all go through Resolve").</description></item>
/// <item><description><see cref="ItemDef"/> -&gt; the item type string (e.g. "SimpleBrownBox_01"),
/// resolved through the loaded <see cref="SharedGameConfig.Items"/> library
/// (<c>ItemDefinition.ItemType</c>). Requires a <see cref="SharedGameConfig"/> instance; without
/// one (or when the key isn't found in it) this falls back to the raw int <c>ConfigKey</c> —
/// degraded output, not confirmed against any golden example.</description></item>
/// </list>
/// <para>
/// Every other <c>ConfigDefinition</c> subclass has no golden example anywhere in this repo's
/// reference dump — falls back to its raw <c>ConfigKey</c> value (unverified).
/// </para>
/// <para>
/// This converter needs a live <see cref="SharedGameConfig"/> to resolve <see cref="ItemDef"/>, so
/// (per the brief) it is NOT added to <see cref="DumpJson.CreateSettings"/>'s default converter
/// list — a dumper that has a loaded config constructs its own
/// <c>new ConfigDefinitionConverter(config)</c> and adds it to its own converters list (Task 7/8).
/// </para>
/// </summary>
public sealed class ConfigDefinitionConverter : JsonConverter
{
    private readonly SharedGameConfig? _config;
    private readonly IDumpLog _log;

    /// <param name="config">
    /// The loaded game config, used only to resolve <see cref="ItemDef"/> -&gt; item type string.
    /// Pass null to still get correct <see cref="HotspotDef"/> output (no config needed for that
    /// one); <see cref="ItemDef"/> then degrades to its raw int <c>ConfigKey</c>.
    /// </param>
    /// <param name="log">Diagnostics sink; the <see cref="ItemDef"/> fallback is traced through it.</param>
    public ConfigDefinitionConverter(SharedGameConfig? config = null, IDumpLog? log = null)
    {
        _config = config;
        _log = log ?? ConsoleDumpLog.Instance;
    }

    public override bool CanConvert(Type objectType)
    {
        for (var t = objectType; t != null && t != typeof(object); t = t.BaseType)
        {
            if (!t.IsGenericType) continue;
            var generic = t.GetGenericTypeDefinition();
            // ConfigId<,> is the one subclass golden does NOT flatten: the DailyChallenges week /
            // objective references in events.json read {"ConfigKey": "EasyWeek"}, i.e. exactly
            // Newtonsoft's default reflection (its second, private TypeCode member stays hidden).
            // Leaving it unclaimed is what produces that.
            if (generic == typeof(ConfigId<,>)) return false;
            if (generic == typeof(ConfigDefinition<,>)) return true;
        }
        return false;
    }

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        switch (value)
        {
            case null:
                writer.WriteNull();
                return;

            case HotspotDef hotspot:
                writer.WriteValue(HotspotIdNames.Resolve(hotspot.ConfigKey));
                return;

            case ItemDef item:
                if (_config?.Items != null && _config.Items.TryGetValue(item.ConfigKey, out var def) && def != null)
                {
                    writer.WriteValue(def.ItemType);
                    return;
                }
                // Degraded fallback: no config loaded, or the key is not in Items. The raw int is a
                // valid JSON value, so nothing downstream fails — it just silently stops being an
                // item type, which is why it is worth a trace line.
                _log.Trace($"ConfigDefinitionConverter: ItemDef key {item.ConfigKey} not resolved to an item type"
                           + $" ({(_config?.Items == null ? "no Items library" : "key not in Items")}); writing the raw key");
                writer.WriteValue(item.ConfigKey);
                return;

            default:
                // Unverified: no golden example for this ConfigDefinition subclass. Best-effort
                // fallback mirrors what Newtonsoft's default reflection would have written for the
                // ConfigKey member alone, minus the wrapper object.
                var configKey = value.GetType().GetProperty("ConfigKey")?.GetValue(value);
                serializer.Serialize(writer, configKey);
                return;
        }
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(ConfigDefinitionConverter)} is write-only (dumper never deserializes).");
}
