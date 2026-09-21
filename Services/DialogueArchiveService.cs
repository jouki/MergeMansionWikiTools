using System.Text;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public record ArchivedLine(string LineId, string Text, string Until);

/// <summary>
/// Result of merging the existing wiki archive with newly detected rewrites. Mirrors
/// <see cref="ItemsArchiveService"/>'s ArchiveDiff shape on purpose: <see cref="FinalArchive"/> is
/// the complete, merged archive ready to hand straight to <see cref="DialogueArchiveService.RenderArchive"/>.
/// Callers must never render from <see cref="Added"/> alone — that would drop all previously
/// archived history, since <see cref="Added"/> only holds what changed *this* run.
/// </summary>
public class DialogueArchiveDiff
{
    /// Lines whose old wording was just superseded this run (freshly archived).
    public List<ArchivedLine> Added { get; init; } = new();
    /// Rewrites classified as cosmetic (typo/punctuation/markup) — skipped, not archived.
    public int CosmeticSkipped { get; init; }
    /// Pre-existing archive entries carried forward unchanged, including lines absent from the current dump.
    public List<ArchivedLine> Carried { get; init; } = new();
    /// Complete merged archive (carried-over history + newly archived entries) — feed this to RenderArchive.
    public Dictionary<string, List<ArchivedLine>> FinalArchive { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// History of rewritten dialogue lines. The app only ever sees one dump, so the wiki module itself
/// is the archive: whatever changed non-cosmetically since last time is stored here with the game
/// version the old wording ended in. Same principle as <see cref="ItemsArchiveService"/> for items.
/// </summary>
public static class DialogueArchiveService
{
    public const string ArchiveModuleTitle = "Module:Datatable/Dialogues/Archive";

    // Non-greedy body capture stops at the first "}," it sees, which would be an inner item's
    // closing brace, not the entry's — both render identically. RenderArchive always puts a
    // newline right after the entry's own closing "},", but a space after an item's, so anchoring
    // the terminator to end-of-line disambiguates the two without a full brace-counting parser.
    private static readonly Regex Entry = new(
        @"\[""(?<id>[^""]+)""\]\s*=\s*\{(?<body>.*?)\},(?=[ \t]*\r?\n)",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Item = new(
        @"\{\s*text\s*=\s*""(?<text>(?:[^""\\]|\\.)*)""\s*,\s*until\s*=\s*""(?<until>[^""]*)""\s*\}",
        RegexOptions.Compiled);
    // Cheap "how many entries should be here" marker: just the id-bracket opening, with none of
    // Entry's stricter end-of-line anchoring. If a module got hand-edited or reformatted by
    // something else, Entry can fail to match an id that's clearly still there by shape — this
    // catches that instead of silently returning a thinner (or empty) dictionary.
    private static readonly Regex EntryMarker = new(
        @"\[""[^""]+""\]\s*=\s*\{", RegexOptions.Compiled);

    // One rendered scene LINE (as LuaGeneratorService.RenderDialogueScene emits it): "{ id = "...",
    // ... text = "..." },", all on one source line. Distinct from Entry/Item above — those parse the
    // archive module's own shape (scene-less, id-keyed, text+until pairs); this parses a LIVE main/
    // event Dialogues chunk to recover what each line said before this run overwrites it.
    private static readonly Regex LiveLine = new(
        @"\{\s*id\s*=\s*""(?<id>(?:[^""\\]|\\.)*)"".*?text\s*=\s*""(?<text>(?:[^""\\]|\\.)*)""\s*\}\s*,",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses `Module:Datatable/Dialogues/Archive` content into line id → archived wordings.
    /// Null, empty or whitespace-only input (first run — no archive on wiki yet) yields an empty
    /// dictionary. A module with the table present but no entries at all (legitimately emptied
    /// archive) also yields an empty dictionary. Neither case throws.
    /// A module where entries are visibly present by shape (<see cref="EntryMarker"/> count) but
    /// fewer were actually parsed — hand-edited, reformatted, otherwise corrupted — throws
    /// <see cref="InvalidOperationException"/> instead of returning a partial or empty result, so a
    /// caller can never mistake "couldn't read it" for "there's nothing to keep" and overwrite the
    /// wiki archive with less history than it already had.
    /// </summary>
    public static Dictionary<string, List<ArchivedLine>> ParseArchive(string? lua)
    {
        var result = new Dictionary<string, List<ArchivedLine>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(lua)) return result;

        var expectedCount = EntryMarker.Matches(lua).Count;
        if (expectedCount == 0) return result; // legitimately empty archive table

        foreach (Match m in Entry.Matches(lua))
        {
            var id = m.Groups["id"].Value;
            var list = new List<ArchivedLine>();
            foreach (Match i in Item.Matches(m.Groups["body"].Value))
                list.Add(new ArchivedLine(id, Unescape(i.Groups["text"].Value), i.Groups["until"].Value));
            if (list.Count > 0) result[id] = list;
        }

        if (result.Count != expectedCount)
            throw new InvalidOperationException(
                $"{ArchiveModuleTitle} looks corrupted: {expectedCount} entr" +
                (expectedCount == 1 ? "y" : "ies") + " found by shape but only " + result.Count +
                " could be parsed. Refusing to return a partial archive — that would silently " +
                "delete wiki history on the next render. The module may have been hand-edited or reformatted.");

        return result;
    }

    /// <summary>
    /// Merges the existing archive with lines rewritten this run. <paramref name="existingArchive"/>
    /// is what <see cref="ParseArchive"/> read from the live wiki module; every entry in it carries
    /// forward into <see cref="DialogueArchiveDiff.FinalArchive"/> as-is, including ids that no
    /// longer appear in <paramref name="scenes"/> at all — a line dropped from the current dump must
    /// not lose the history it already had. Newly rewritten lines are appended to that same merged
    /// result, keyed by line id, so <see cref="DialogueArchiveDiff.FinalArchive"/> is always ready to
    /// pass straight to <see cref="RenderArchive"/>.
    /// </summary>
    public static DialogueArchiveDiff Compute(
        IReadOnlyDictionary<string, List<ArchivedLine>> existingArchive,
        IReadOnlyDictionary<string, string> liveTextsBefore,
        List<DialogueScene> scenes,
        string gameVersion)
    {
        var added = new List<ArchivedLine>();
        var carried = new List<ArchivedLine>();
        var cosmetic = 0;

        // Seed the merged result with everything already archived — copies, so callers mutating
        // the returned dictionary never reach back into the parsed input.
        var finalArchive = new Dictionary<string, List<ArchivedLine>>(StringComparer.Ordinal);
        foreach (var (lineId, entries) in existingArchive)
        {
            var copy = new List<ArchivedLine>(entries);
            finalArchive[lineId] = copy;
            carried.AddRange(copy);
        }

        foreach (var line in scenes.SelectMany(s => s.Lines))
        {
            if (!liveTextsBefore.TryGetValue(line.Id, out var before)) continue;
            switch (DialogueChangeClassifier.Classify(before, line.Text))
            {
                case DialogueChangeKind.Rewritten:
                    var entry = new ArchivedLine(line.Id, before, gameVersion);
                    added.Add(entry);
                    if (!finalArchive.TryGetValue(line.Id, out var bucket))
                        finalArchive[line.Id] = bucket = new List<ArchivedLine>();
                    bucket.Add(entry);
                    break;
                case DialogueChangeKind.Cosmetic:
                    cosmetic++;
                    break;
            }
        }

        return new DialogueArchiveDiff
        {
            Added = added,
            CosmeticSkipped = cosmetic,
            Carried = carried,
            FinalArchive = finalArchive,
        };
    }

    /// <summary>
    /// Extracts line id → text out of a LIVE <c>Module:Datatable/Dialogues/*</c> chunk (main or events),
    /// as rendered by <c>LuaGeneratorService.RenderDialogueScene</c>. Feeds <see cref="Compute"/>'s
    /// <c>liveTextsBefore</c> — the wording each line had on the wiki right before this push overwrites
    /// it. Null/empty input (chunk not yet on wiki, e.g. a brand-new part) yields an empty dictionary
    /// rather than throwing: unlike <see cref="ParseArchive"/>, a line missing here just never gets
    /// compared for a rewrite — it cannot silently discard history, since this is not itself a store of
    /// anything, only the "before" side of a diff.
    /// </summary>
    public static Dictionary<string, string> ExtractLiveLineTexts(string? moduleLua)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(moduleLua)) return result;
        foreach (Match m in LiveLine.Matches(moduleLua))
            result[m.Groups["id"].Value] = Unescape(m.Groups["text"].Value);
        return result;
    }

    public static string RenderArchive(Dictionary<string, List<ArchivedLine>> archive)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- Previous wordings of dialogue lines that were rewritten in a later game version.");
        sb.AppendLine("-- Generated by MergeMansionWikiTools; do not edit by hand.");
        sb.AppendLine("return {");
        foreach (var (id, entries) in archive.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append($"  [\"{id}\"] = {{ ");
            foreach (var e in entries.OrderBy(e => e.Until, StringComparer.Ordinal))
                sb.Append($"{{ text = \"{Escape(e.Text)}\", until = \"{e.Until}\" }}, ");
            sb.AppendLine("},");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // nezaviresny retezec by shodil cely modul - "\n" uz se escapoval, "\r" ne, a jediny nezaviresny
    // vozik v archivovanem textu by tak Lua parseru rozbil radek uprostred stringu
    private static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    private static string Unescape(string v) => v.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\");
}
