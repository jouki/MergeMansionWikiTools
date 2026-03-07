using System.IO;
using GameLogic.Config;
using Newtonsoft.Json;

namespace merge_mansion_dumper.Dumper.Base
{
    public abstract class JsonDumper<TDump> : BaseDumper<TDump>
    {
        public void WriteJson(string file, SharedGameConfig config)
        {
            var dump = Dump(config);
            var data = new { CreatedAt = config.ArchiveCreatedAt, Data = dump };

            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var serializer = JsonSerializer.Create(CreateSettings(config));
            serializer.Formatting = Formatting.Indented;

            using var sw = new StreamWriter(file, false, System.Text.Encoding.UTF8, 65536);
            using var jw = new JsonTextWriter(sw) { Formatting = Formatting.Indented };
            serializer.Serialize(jw, data);
        }

        protected virtual JsonSerializerSettings CreateSettings(SharedGameConfig config) => new() { NullValueHandling = NullValueHandling.Ignore };
    }
}
