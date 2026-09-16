using MergeMansionWikiTools.Dumper.Core;
using Metaplay.Core;
using Metaplay.Core.Config;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

public class MetaRefConverterTests
{
    // Smallest possible IGameConfigData implementation so MetaRef<TestConfigItem> can be
    // constructed and resolved through the real public MetaRef<T> API.
    private sealed class TestConfigItem : IGameConfigData<string>
    {
        public string ConfigKey { get; init; } = "";
    }

    private sealed class FakeResolver : IGameConfigDataResolver
    {
        public object TryResolveReference(System.Type type, object configKey) => new TestConfigItem { ConfigKey = (string)configKey };
    }

    private static string Serialize(object? value)
    {
        var settings = DumpJson.CreateSettings(new JsonConverter[] { new MetaRefConverter() });
        return JsonConvert.SerializeObject(value, settings);
    }

    [Fact]
    public void Unresolved_ref_still_serializes_to_its_key_object()
    {
        // Golden (26.07.01, IdealFTUEPhase1_B patch) keeps the key of a reference whose target the
        // patched config no longer contains: "EventInfoRef": "CBE_JoysOfTheSea2023".
        var metaRef = MetaRef<TestConfigItem>.FromKey("k1");
        Assert.False(metaRef.IsResolved);
        Assert.Equal("\"k1\"", Serialize(metaRef));
    }

    [Fact]
    public void Resolved_ref_serializes_to_its_key_object()
    {
        var unresolved = MetaRef<TestConfigItem>.FromKey("k1");
        var resolved = unresolved.CreateResolved(new FakeResolver());
        Assert.True(resolved.IsResolved);
        Assert.Equal("\"k1\"", Serialize(resolved));
    }
}
