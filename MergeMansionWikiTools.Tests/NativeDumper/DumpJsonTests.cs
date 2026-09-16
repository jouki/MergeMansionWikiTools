using System.IO;
using System.Text;
using MergeMansionWikiTools.Dumper.Core;
using Metaplay.Core;
using Newtonsoft.Json;
using Xunit;

namespace MergeMansionWikiTools.Tests.NativeDumper;

public class DumpJsonTests
{
    private static readonly MetaTime At = MetaTime.FromDateTime(new System.DateTime(2026, 9, 2, 12, 12, 19, 371, System.DateTimeKind.Utc));

    [Fact]
    public void Main_file_has_bom_crlf_two_space_indent_and_iso_created_at()
    {
        var json = DumpJson.Serialize(new[] { 1 }, At, DumpFileKind.Main, DumpJson.CreateSettings(System.Array.Empty<JsonConverter>()));
        Assert.StartsWith("{\r\n  \"CreatedAt\": \"2026-09-02T12:12:19.371\",\r\n  \"Data\": [", json);
        var path = Path.Combine(Path.GetTempPath(), "mmwt_dumpjson_main.json");
        DumpJson.Write(path, json, DumpFileKind.Main);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }

    [Fact]
    public void Main_created_at_trims_trailing_zeros_while_aux_keeps_three_digits()
    {
        // Golden: the archive created at 08:10:58.980 is "2026-08-27T08:10:58.98" in the main files
        // but "2026-08-27 08:10:58.980 Z" in Pets/Experimental.
        var at = MetaTime.FromDateTime(new System.DateTime(2026, 8, 27, 8, 10, 58, 980, System.DateTimeKind.Utc));
        Assert.Equal("2026-08-27T08:10:58.98", DumpJson.FormatCreatedAt(at, DumpFileKind.Main));
        Assert.Equal("2026-08-27 08:10:58.980 Z", DumpJson.FormatCreatedAt(at, DumpFileKind.Aux));
    }

    [Fact]
    public void Aux_file_has_no_bom_and_space_z_created_at()
    {
        var json = DumpJson.Serialize(new[] { 1 }, At, DumpFileKind.Aux, DumpJson.CreateSettings(System.Array.Empty<JsonConverter>()));
        Assert.StartsWith("{\r\n  \"CreatedAt\": \"2026-09-02 12:12:19.371 Z\",", json);
        var path = Path.Combine(Path.GetTempPath(), "mmwt_dumpjson_aux.json");
        DumpJson.Write(path, json, DumpFileKind.Aux);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void Section_file_has_no_bom_but_keeps_the_main_created_at_format()
    {
        // Experimental/*.json is the third combination: NO BOM (like Pets.json) yet the "T" CreatedAt
        // of the main files, because those sections go through the same converter stack and only the
        // writer differs. Getting this wrong makes all six section files differ on line 2.
        var json = DumpJson.Serialize(new[] { 1 }, At, DumpFileKind.Section, DumpJson.CreateSettings(System.Array.Empty<JsonConverter>()));
        Assert.StartsWith("{\r\n  \"CreatedAt\": \"2026-09-02T12:12:19.371\",", json);
        var path = Path.Combine(Path.GetTempPath(), "mmwt_dumpjson_section.json");
        DumpJson.Write(path, json, DumpFileKind.Section);
        Assert.Equal((byte)'{', File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void WriteIfDifferent_skips_identical_and_writes_different()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmwt_dumpjson_diff.json");
        File.Delete(path);
        Assert.False(DumpJson.WriteIfDifferent(path, "abc", "abc", DumpFileKind.Main));
        Assert.False(File.Exists(path));
        Assert.True(DumpJson.WriteIfDifferent(path, "abc", "abd", DumpFileKind.Main));
        Assert.True(File.Exists(path));
        Assert.True(DumpJson.WriteIfDifferent(path, "abc", null, DumpFileKind.Main));
    }

    [Fact]
    public void Write_with_a_bare_filename_does_not_throw()
    {
        // A path with no directory component (Path.GetDirectoryName returns "") must not blow up
        // on Directory.CreateDirectory(""). Writes into the test host's own CWD rather than
        // switching process-wide CWD (which xUnit's parallel test classes would race on).
        const string path = "mmwt_bare_test.json";
        try
        {
            DumpJson.Write(path, "abc", DumpFileKind.Aux);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
