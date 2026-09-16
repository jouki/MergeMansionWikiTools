using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

/// <summary>
/// xunit runs test CLASSES in parallel, but several dumper tests reach process-global game state and
/// would otherwise see each other's writes:
/// <list type="bullet">
/// <item><description><c>NativeDumperParityTests</c> loads a real language file into
/// <c>MetaplaySDK.ActiveLanguage</c> (and sets <c>ClientGlobal.SharedGameConfig</c>).</description></item>
/// <item><description><c>AreaSerializerTests</c> asserts on DOT labels produced with NO language
/// loaded — the live language file does translate the hotspot/item keys those tests use, so a
/// concurrent parity run would silently change the expected strings.</description></item>
/// <item><description><c>HotspotIdNamesTests</c> clears and repopulates the static
/// <c>HotspotIdNames</c> registry, which the area DOT labels also read.</description></item>
/// </list>
/// Putting all three in ONE collection makes xunit run them sequentially, which is the only thing
/// that makes the outcome independent of class order.
/// </summary>
[CollectionDefinition(Name)]
public class GlobalGameStateCollection
{
    public const string Name = "GlobalGameState";
}
