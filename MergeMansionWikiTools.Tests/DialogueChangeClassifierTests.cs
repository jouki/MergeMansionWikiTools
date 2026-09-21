using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class DialogueChangeClassifierTests
{
    [Fact]
    public void Identical_text_is_unchanged()
        => Assert.Equal(DialogueChangeKind.Unchanged,
            DialogueChangeClassifier.Classify("Hello there.", "Hello there."));

    [Fact]
    public void Punctuation_and_markup_only_is_cosmetic()
        => Assert.Equal(DialogueChangeKind.Cosmetic,
            DialogueChangeClassifier.Classify("Hello there!", "Hello there..."));

    [Fact]
    public void Typographic_apostrophe_is_cosmetic()
        => Assert.Equal(DialogueChangeKind.Cosmetic,
            DialogueChangeClassifier.Classify("let's go", "let’s go"));

    [Fact]
    public void Different_meaning_is_a_rewrite()
        => Assert.Equal(DialogueChangeKind.Rewritten,
            DialogueChangeClassifier.Classify(
                "Now hold that thought, my dear. I've got a cherry pie cooling.",
                "The story is... that I'm going to go and bake a cake for your new friend."));

    [Fact]
    public void Colour_markup_alone_is_cosmetic()
        => Assert.Equal(DialogueChangeKind.Cosmetic,
            DialogueChangeClassifier.Classify("Check the <color=#338DFF>garage</color>", "Check the garage"));

    // Testy vyse vsechny projdou uz zkratkou "normalizovane texty jsou stejne" (interpunkce/markup se
    // normalizaci odstrani beze zbytku) - k porovnani podobnosti (Levenshtein) se vubec nedostanou.
    // Overeno i empiricky: s prahem docasne zvednutym na 0.999 (tj. archivoval by se kazdy preklep)
    // vsechny 3 kosmeticke testy porad projdou - dukaz, ze podobnost vubec netestuji.
    // Nasledujici dva testy prah skutecne cviči: text se lisi az PO normalizaci (pismena 'a'/'b',
    // ktera normalizace nezmeni), takze klasifikace zavisi na Levenshteinove podobnosti, ne na zkratce.

    [Fact]
    public void Just_above_the_similarity_threshold_is_cosmetic()
    {
        // 100 znaku, lisi se na 9 pozicich => podobnost 0,91 - tesne NAD prahem 0.90
        var a = new string('a', 100);
        var b = Mutate(a, 0, 11, 22, 33, 44, 55, 66, 77, 88);

        Assert.Equal(DialogueChangeKind.Cosmetic, DialogueChangeClassifier.Classify(a, b));
    }

    [Fact]
    public void Just_below_the_similarity_threshold_is_a_rewrite()
    {
        // stejny princip, 11 pozic ze 100 => podobnost 0,89 - tesne POD prahem 0.90
        var a = new string('a', 100);
        var b = Mutate(a, 0, 9, 18, 27, 36, 45, 54, 63, 72, 81, 90);

        Assert.Equal(DialogueChangeKind.Rewritten, DialogueChangeClassifier.Classify(a, b));
    }

    private static string Mutate(string s, params int[] positions)
    {
        var chars = s.ToCharArray();
        foreach (var p in positions) chars[p] = 'b';
        return new string(chars);
    }
}
