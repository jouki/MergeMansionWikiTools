using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using GameLogic.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace merge_mansion_dumper.Dumper.Base
{
    /// <summary>
    /// Contract resolver that honors System.Runtime.Serialization's [IgnoreDataMember]
    /// attribute. Newtonsoft only respects its own [JsonIgnore]; game-side API classes
    /// (DecorationInfo.LayeredDecorationSetInfo, RewardDecoration.Decoration, etc.) use
    /// [IgnoreDataMember] on derived getters that internally call MetaRef.Ref. Without
    /// this resolver Newtonsoft default reflection serializer invokes those getters and
    /// crashes when the underlying MetaRef hasn't been resolved in the current archive.
    /// </summary>
    public class IgnoreDataMemberContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            if (member.GetCustomAttribute<IgnoreDataMemberAttribute>() != null)
            {
                property.Ignored = true;
                property.ShouldSerialize = _ => false;
            }
            return property;
        }
    }

    public abstract class JsonDumper<TDump> : BaseDumper<TDump>
    {
        /// <summary>Serializes and writes to file. Returns the JSON string for baseline comparison.</summary>
        public string WriteJson(string file, SharedGameConfig config)
        {
            var json = SerializeToString(config);

            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8, 65536);
            sw.Write(json);

            return json;
        }

        /// <summary>
        /// Serializes and compares against <paramref name="baselineContent"/> in memory.
        /// Writes to file only if content differs. Returns true if written.
        /// </summary>
        public bool WriteJsonIfDifferent(string file, SharedGameConfig config, string? baselineContent)
        {
            var json = SerializeToString(config);

            if (baselineContent != null && string.Equals(json, baselineContent, System.StringComparison.Ordinal))
                return false;

            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(file, json, System.Text.Encoding.UTF8);
            return true;
        }

        private string SerializeToString(SharedGameConfig config)
        {
            var dump = Dump(config);
            var data = new { CreatedAt = config.ArchiveCreatedAt, Data = dump };

            var serializer = JsonSerializer.Create(CreateSettings(config));
            serializer.Formatting = Formatting.Indented;

            using var sw = new StringWriter();
            using var jw = new JsonTextWriter(sw) { Formatting = Formatting.Indented };
            serializer.Serialize(jw, data);
            return sw.ToString();
        }

        protected virtual JsonSerializerSettings CreateSettings(SharedGameConfig config) => new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new IgnoreDataMemberContractResolver(),
        };
    }
}
