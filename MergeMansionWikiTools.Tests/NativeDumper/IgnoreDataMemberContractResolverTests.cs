using System.Runtime.Serialization;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

public class IgnoreDataMemberContractResolverTests
{
    private sealed class Poco
    {
        public string Kept { get; set; } = "yes";

        [IgnoreDataMember]
        public string Dropped { get; set; } = "no";
    }

    [Fact]
    public void IgnoreDataMember_tagged_property_is_excluded_from_output()
    {
        var json = JsonConvert.SerializeObject(new Poco(), DumpJson.CreateSettings(System.Array.Empty<JsonConverter>()));
        Assert.Contains("\"Kept\"", json);
        Assert.DoesNotContain("Dropped", json);
    }
}
