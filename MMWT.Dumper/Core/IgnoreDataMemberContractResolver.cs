using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Contract resolver that makes members tagged with <see cref="IgnoreDataMemberAttribute"/> get
/// skipped during serialization (mirrors the legacy dumper's behavior for fields that shouldn't
/// hit the JSON output). Plugged into every dumper via <see cref="DumpJson.CreateSettings"/>.
/// </summary>
public sealed class IgnoreDataMemberContractResolver : DefaultContractResolver
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
