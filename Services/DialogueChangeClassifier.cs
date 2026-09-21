using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

public enum DialogueChangeKind { Unchanged, Cosmetic, Rewritten }

/// <summary>
/// Tells a typo fix apart from a real rewrite. The 0.90 threshold comes from the Codex
/// (`Codex/build/reruns.py`), where across 44 game versions it split 404 cosmetic edits from 203 rewrites.
/// Only `Rewritten` reaches the wiki archive — otherwise moved commas would flood it.
/// </summary>
public static class DialogueChangeClassifier
{
    private const double CosmeticRatio = 0.90;
    private static readonly Regex Markup = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex NonWord = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static DialogueChangeKind Classify(string? oldText, string? newText)
    {
        oldText ??= ""; newText ??= "";
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return DialogueChangeKind.Unchanged;

        var a = Normalize(oldText);
        var b = Normalize(newText);
        if (a == b) return DialogueChangeKind.Cosmetic;
        return Similarity(a, b) >= CosmeticRatio ? DialogueChangeKind.Cosmetic : DialogueChangeKind.Rewritten;
    }

    /// <summary>Lowercase, game markup and punctuation stripped, apostrophes and spacing unified.</summary>
    public static string Normalize(string text)
    {
        var s = Markup.Replace(text, " ")
            .Replace('\u2019', '\'').Replace('\u2018', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"')
            .Replace("\u2026", "...")
            .ToLowerInvariant();
        return Spaces.Replace(NonWord.Replace(s, " "), " ").Trim();
    }

    /// <summary>Similarity from Levenshtein distance, 0..1.</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
