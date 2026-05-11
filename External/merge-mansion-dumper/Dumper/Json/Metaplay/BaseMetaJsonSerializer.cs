using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using GameLogic.Player.Director.Config;
using Metaplay.Core;
using System.Reflection;
using GameLogic.Player.Items;
using Metaplay.Core.Model;
using Metaplay.Core.IO;

namespace merge_mansion_dumper.Dumper.Json.Metaplay
{
    public abstract class BaseMetaJsonSerializer : JsonConverter
    {
        protected abstract Type[] GetTypes();

        public override bool CanConvert(Type objectType)
        {
            var types = GetTypes();
            return types.Contains(objectType) || types.Any(objectType.IsAssignableTo);
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        protected void WriteEmptyObject(JsonWriter writer)
        {
            new JObject().WriteTo(writer);
        }

        protected void WriteObject(JsonWriter writer, Type type, object @object, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            WriteCustomObjectMembers(writer, @object, serializer);

            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            // Build (Info, TagId) pairs first — do NOT call GetValue() yet. Some getters
            // (e.g. RewardDecoration.Decoration => DecorationRef.Ref, DecorationInfo.LayeredDecorationSetInfo)
            // are derived from MetaRefs and throw InvalidOperationException when the underlying
            // ref hasn't been resolved (common in patched-config contexts). We MUST filter on
            // MetaMember.TagId > 0 BEFORE invoking getters so unsafe derived members never run.
            var propPairs = type.GetProperties(flags)
                .Select(p => (MemberName: p.Name.Trim('_'), TagId: p.GetCustomAttribute<MetaMemberAttribute>()?.TagId ?? -1, Getter: (Func<object>)(() => p.GetValue(@object))));
            var fieldPairs = type.GetFields(flags)
                .Select(f => (MemberName: f.Name.Trim('_'), TagId: f.GetCustomAttribute<MetaMemberAttribute>()?.TagId ?? -1, Getter: (Func<object>)(() => f.GetValue(@object))));

            var ordered = propPairs.Concat(fieldPairs)
                .Where(x => x.TagId > 0)
                .OrderBy(x => x.TagId)
                .ToArray();

            foreach (var member in ordered)
            {
                object value;
                try
                {
                    value = member.Getter();
                }
                catch (System.Reflection.TargetInvocationException tie)
                    when (tie.InnerException is InvalidOperationException)
                {
                    // Defensive: even MetaMember-tagged getter can throw if it dereferences
                    // an unresolved MetaRef. Skip the field rather than crash the whole dump.
                    continue;
                }

                if (value == null && serializer.NullValueHandling == NullValueHandling.Ignore)
                    continue;

                WriteObjectMember(writer, member.MemberName, type, value, serializer);
            }

            writer.WriteEndObject();
        }

        protected virtual void WriteCustomObjectMembers(JsonWriter writer, object value, JsonSerializer serializer)
        {
        }

        protected virtual void WriteObjectMember(JsonWriter writer, string name, Type type, object value, JsonSerializer serializer)
        {
            if (value is IMetaRef { IsResolved: false })
                return;

            WriteProperty(writer, name, value, serializer);
        }

        protected void WriteProperty(JsonWriter writer, string name, object value, JsonSerializer serializer)
        {
            writer.WritePropertyName(name);
            WriteValue(writer, value, serializer);
        }

        protected void WriteValue(JsonWriter writer, object value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, value);
        }

        record ObjectValue(object Value, string Name, int Order);
    }
}
