using System.Globalization;
using System.Text;
using Metaplay.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Distinguishes the three dump-file formats used across the native dumpers. BOM and CreatedAt
/// format are two independent axes and golden uses three of their four combinations:
/// <list type="bullet">
/// <item><description><see cref="Main"/> — the 5 primary dump files + patch folders: UTF-8 BOM and an
/// ISO-8601-ish "T" separated CreatedAt (no trailing "Z" designator).</description></item>
/// <item><description><see cref="Section"/> — <c>Experimental/*.json</c>: NO BOM, but the same "T"
/// CreatedAt as Main, because those files go through the same converter stack and only the
/// BOM-less writer differs.</description></item>
/// <item><description><see cref="Aux"/> — <c>Pets.json</c> alone: no BOM and a space-separated
/// "yyyy-MM-dd HH:mm:ss.fff Z" CreatedAt, which is <c>MetaTime.ToString()</c>'s own shape (that
/// file is written with no converters at all, so the MetaTime falls through to its
/// TypeConverter).</description></item>
/// </list>
/// </summary>
public enum DumpFileKind { Main, Section, Aux }

/// <summary>
/// Shared JSON envelope/formatting layer used by every dumper: wraps payloads in a
/// { CreatedAt, Data } envelope, applies the fixed indentation/newline/encoding rules,
/// and offers a "write only if content changed" helper so unrelated re-dumps don't
/// touch files (and thus git diffs / mtimes) unnecessarily.
/// </summary>
public static class DumpJson
{
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Builds the JsonSerializerSettings shared by all dumpers: indented formatting, nulls
    /// omitted, the IgnoreDataMember-aware contract resolver, plus any converters the caller
    /// needs (e.g. HotspotAwareStringEnumConverter).
    /// </summary>
    public static JsonSerializerSettings CreateSettings(IEnumerable<JsonConverter> converters)
    {
        var s = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new IgnoreDataMemberContractResolver(),
        };
        foreach (var c in converters) s.Converters.Add(c);
        return s;
    }

    /// <summary>
    /// Serializes <paramref name="data"/> wrapped in the { CreatedAt, Data } envelope, using
    /// CRLF newlines and 2-space indentation regardless of platform, and the CreatedAt format
    /// appropriate for <paramref name="kind"/>.
    /// </summary>
    public static string Serialize(object? data, MetaTime createdAt, DumpFileKind kind, JsonSerializerSettings settings)
    {
        var envelope = new Envelope { CreatedAt = FormatCreatedAt(createdAt, kind), Data = data };
        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb) { NewLine = "\r\n" })
        using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.Indented, Indentation = 2, IndentChar = ' ' })
        {
            JsonSerializer.Create(settings).Serialize(jw, envelope);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Formats a MetaTime for the CreatedAt field.
    /// <para>
    /// Main and Section files reproduce what Newtonsoft would write for the plain
    /// <see cref="DateTime"/> behind the MetaTime: ISO-8601 with a "T" separator, no zone designator
    /// (MetaTime.ToDateTime returns an <see cref="DateTimeKind.Unspecified"/> value) and, crucially,
    /// <b>trailing zeros trimmed off the fractional part</b> — the "FFFFFFF" specifier, not "fff".
    /// Golden proof: the archive created at 08:10:58.980 is written as
    /// <c>"2026-08-27T08:10:58.98"</c>.
    /// </para>
    /// <para>
    /// Aux files (<c>Pets.json</c>) use <see cref="MetaTime.ToString"/>'s own shape instead: space
    /// separator, always three fractional digits, trailing " Z" — the same instant reads
    /// <c>"2026-08-27 08:10:58.980 Z"</c> there.
    /// </para>
    /// </summary>
    public static string FormatCreatedAt(MetaTime t, DumpFileKind kind)
    {
        var dt = t.ToDateTime();
        return kind == DumpFileKind.Aux
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.fff' Z'", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes <paramref name="json"/> to <paramref name="path"/> unless it is byte-for-byte
    /// identical (ordinal string comparison) to <paramref name="baseline"/> (e.g. the previous
    /// dump's content). Returns false when skipped (identical), true when written (different,
    /// or no baseline was available).
    /// </summary>
    public static bool WriteIfDifferent(string path, string json, string? baseline, DumpFileKind kind)
    {
        if (baseline != null && string.Equals(json, baseline, StringComparison.Ordinal)) return false;
        Write(path, json, kind);
        return true;
    }

    /// <summary>
    /// Writes <paramref name="json"/> to <paramref name="path"/> unconditionally, creating the
    /// containing directory if needed, using a BOM for Main files and no BOM for Section/Aux files.
    /// </summary>
    public static void Write(string path, string json, DumpFileKind kind)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, json, kind == DumpFileKind.Main ? Utf8Bom : Utf8NoBom);
    }

    private sealed class Envelope
    {
        public string CreatedAt { get; set; } = "";
        public object? Data { get; set; }
    }
}
