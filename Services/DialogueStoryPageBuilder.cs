using System.Text;
using System.Text.RegularExpressions;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Builds the wikitext of an {Area}/Story page. The structure mirrors event pages (Murder at the
/// Mansion): an outer {{Tabber}} with an inner &lt;tabber&gt; of scenes. Every scene carries an anchor
/// derived from its id so that links from character pages survive a regeneration.
/// </summary>
public static class DialogueStoryPageBuilder
{
    public static string Build(string areaName, List<DialogueScene> scenes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Dialogue from {{{{Area|{areaName}}}}}, in the order the game presents it.");
        sb.AppendLine();
        sb.AppendLine("{{Tabber");
        sb.AppendLine("| tableTabber = true");
        sb.AppendLine("| class       = lesserTabberElements");
        sb.AppendLine("| Story");
        sb.AppendLine("|");
        sb.AppendLine("<tabber>");

        // jmena zalozek nejsou jedinecna (popis hotspotu se opakuje, napr. "Plant vines" 5x v Bludisti) -
        // stejne jmeno dvou zalozek ve stejnem tabberu znamena, ze druha je nedosazitelna
        var tabNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var scene in scenes)
        {
            var printableLines = scene.Lines.Where(l => !string.IsNullOrEmpty(l.Text)).ToList();
            if (printableLines.Count == 0) continue;   // scena bez jedine tisknutelne repliky by dala prazdnou zalozku

            sb.AppendLine($"|-| {UniqueTabName(scene, tabNameCounts)} =");
            sb.AppendLine($"{{{{Anchor|{scene.Id}}}}}");
            foreach (var line in printableLines)
                sb.AppendLine(RenderLine(line));
            sb.AppendLine();
        }

        sb.AppendLine("</tabber>");
        sb.AppendLine("}}");
        return sb.ToString();
    }

    /// <summary>Base tab name (hotspot description). Falls back to the hotspot id, camelCase-split into
    /// readable words, when the scene has no task description; the raw scene id is a last resort for
    /// the rare scene that has neither.</summary>
    private static string TabName(DialogueScene s)
    {
        if (!string.IsNullOrWhiteSpace(s.Task)) return s.Task!;
        if (!string.IsNullOrWhiteSpace(s.Hotspot)) return SplitCamelCase(s.Hotspot!);
        return s.Id;
    }

    /// <summary>"CorridorUnlock" → "Corridor Unlock" — same camelCase-splitting idea as
    /// <see cref="AreaOrderingService.FallbackNameFromAreaId"/>, kept local since it applies to a
    /// hotspot id here, not an area id.</summary>
    private static string SplitCamelCase(string id) => Regex.Replace(id, "([A-Z])", " $1").Trim();

    /// <summary>Disambiguates a repeated tab name within the same page by appending " (2)", " (3)", …
    /// — readable for a human, unlike falling back to the raw scene id.</summary>
    private static string UniqueTabName(DialogueScene scene, Dictionary<string, int> countsSoFar)
    {
        var name = TabName(scene);
        countsSoFar.TryGetValue(name, out var count);
        count++;
        countsSoFar[name] = count;
        return count == 1 ? name : $"{name} ({count})";
    }

    private static string RenderLine(DialogueLineInfo l)
    {
        var side = l.Side == DialogueSide.Right ? "R" : l.Side == DialogueSide.Left ? "L" : "-";
        var parts = new List<string> { "{{DialogueLine", $"id={l.Id}" };
        if (l.Speaker != null) parts.Add($"speaker={l.Speaker}");
        parts.Add($"side={side}");
        if (l.Left != null) parts.Add($"left={l.Left}");
        if (l.Right != null) parts.Add($"right={l.Right}");
        parts.Add($"expr={l.Expression}");
        parts.Add($"variant={l.Variant}");
        parts.Add($"text={DialogueWikiText.ForTemplateParameter(l.Text)}");
        return string.Join("|", parts) + "}}";
    }
}
