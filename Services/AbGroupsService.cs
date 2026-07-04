using System.IO;
using System.Text;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Reads the player's AB-experiment memberships from <c>Metaplay_LastSessionGameConfig.dat</c>
/// (pulled from the phone alongside config/patch/lang) and writes a human-readable
/// <c>AB Groups.txt</c> during a dump. The file has two sections:
///   1. Account memberships — which experiments this account is enrolled in + the assigned
///      variant (CONTROL / B / …), read from the LastSessionGameConfig blob.
///   2. Patch catalog — every experiment branch that has a patch in this dump (from the
///      SharedGameConfigPatches blobs the dumper already loaded), regardless of membership.
/// </summary>
internal static class AbGroupsService
{
    /// <summary>Local file name the phone pull writes the LastSessionGameConfig blob to (under _DATA).</summary>
    public const string LastSessionFileName = "LastSessionGameConfig.dat";

    /// <summary>Remote file under the game's <c>cache\</c> on the device.</summary>
    public const string RemoteFileName = "Metaplay_LastSessionGameConfig.dat";

    /// <summary>Output file name written into the dump folder (metadata header + AB groups).</summary>
    public const string OutputFileName = "Metadata.txt";

    /// <summary>
    /// Parses experiment memberships (ExperimentId + assigned variant) from the MetaSerialized
    /// LastSessionGameConfig blob via tag-walking — no full deserialization, so it is robust to
    /// the exact persisted wrapper type.
    /// <para>
    /// Wire format per string member: <c>0x0C</c>, &lt;memberTag&gt;, &lt;len*2&gt;, &lt;UTF-8 bytes&gt;.
    /// Member tag <c>0x02</c> = ExperimentId, <c>0x08</c> = variant label (CONTROL / B / …).
    /// The leading two ContentHash blobs and the trailing environment string are binary / a
    /// different tag, so they are naturally skipped.
    /// </para>
    /// </summary>
    public static List<(string Experiment, string Variant)> ParseMemberships(byte[] data)
    {
        var result = new List<(string, string)>();
        string? curExp = null;
        int i = 0;
        while (i < data.Length - 2)
        {
            if (data[i] == 0x0C)
            {
                int tag = data[i + 1];
                int len = data[i + 2] >> 1; // length is stored as actualLength*2
                if (len > 0 && i + 3 + len <= data.Length)
                {
                    bool printable = true;
                    for (int k = i + 3; k < i + 3 + len; k++)
                        if (data[k] < 32 || data[k] >= 127) { printable = false; break; }

                    if (printable)
                    {
                        var s = Encoding.ASCII.GetString(data, i + 3, len);
                        if (tag == 0x02)
                            curExp = s;
                        else if (tag == 0x08 && curExp != null)
                        {
                            result.Add((curExp, s));
                            curExp = null;
                        }
                        i += 3 + len;
                        continue;
                    }
                }
            }
            i++;
        }
        return result;
    }

    /// <summary>Reads + parses the LastSessionGameConfig blob; returns null if the file is missing/unreadable.</summary>
    public static List<(string Experiment, string Variant)>? TryParseMembershipsFromFile(string? datPath)
    {
        if (string.IsNullOrEmpty(datPath) || !File.Exists(datPath))
            return null;
        try { return ParseMemberships(File.ReadAllBytes(datPath)); }
        catch { return null; }
    }

    /// <summary>
    /// Derives the expected LastSessionGameConfig path from a dump's config path.
    /// The phone pull saves it as <c>_DATA\LastSessionGameConfig.dat</c>; the config path is
    /// <c>_DATA\C\&lt;hash&gt;</c>, so it lives in the config file's grandparent directory.
    /// </summary>
    public static string? ResolveDatPathFromConfig(string? configPath)
        => ResolveDataFile(configPath, LastSessionFileName);

    /// <summary>
    /// Resolves a file under the _DATA dir from a dump's config path. The phone pull saves these
    /// next to C/P/L; the config path is <c>_DATA\C\&lt;hash&gt;</c>, so they live in its grandparent.
    /// </summary>
    public static string? ResolveDataFile(string? configPath, string fileName)
    {
        if (string.IsNullOrEmpty(configPath)) return null;
        var dataDir = Path.GetDirectoryName(Path.GetDirectoryName(configPath));
        return string.IsNullOrEmpty(dataDir) ? null : Path.Combine(dataDir, fileName);
    }

    /// <summary>Reads a single-line value file (e.g. game_version.txt); null if missing/empty.</summary>
    public static string? ReadValueFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { var s = File.ReadAllText(path).Trim(); return string.IsNullOrEmpty(s) ? null : s; }
        catch { return null; }
    }

    /// <summary>Writes Metadata.txt: header (created/game/unity version) + AB groups sections.</summary>
    public static void Write(
        string outputPath,
        string? createdAt,
        string? gameVersion,
        string? unityVersion,
        List<(string Experiment, string Variant)>? memberships,
        IEnumerable<(string Experiment, string Variant)> catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Created At: {createdAt ?? "(unknown)"}");
        sb.AppendLine($"Game Version: {gameVersion ?? "(unknown)"}");
        sb.AppendLine($"Unity Version: {unityVersion ?? "(unknown)"}");
        sb.AppendLine();

        sb.AppendLine("== Account memberships (from Metaplay_LastSessionGameConfig.dat) ==");
        if (memberships == null)
            sb.AppendLine("(LastSessionGameConfig.dat not available — re-pull from phone to populate this section)");
        else if (memberships.Count == 0)
            sb.AppendLine("(no experiment memberships found in the file)");
        else
        {
            foreach (var (exp, variant) in memberships.OrderBy(m => m.Experiment, StringComparer.OrdinalIgnoreCase))
            {
                var mark = variant.Equals("CONTROL", StringComparison.OrdinalIgnoreCase) ? "" : "   <-- TREATMENT";
                sb.AppendLine($"{exp,-46} {variant}{mark}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("== Patch catalog (experiment branches with a patch in this dump) ==");
        var cat = catalog
            .Where(c => !string.IsNullOrEmpty(c.Experiment))
            .Distinct()
            .OrderBy(c => c.Experiment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cat.Count == 0)
            sb.AppendLine("(no patches present in this dump)");
        else
            foreach (var (exp, variant) in cat)
                sb.AppendLine($"{exp,-46} {variant}");

        File.WriteAllText(outputPath, sb.ToString());
    }
}
