using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

public class DialogueWikiTextTests
{
    [Fact]
    public void Curly_typography_is_normalized_to_ascii()
    {
        var t = DialogueWikiText.ToWikitext("‘Well’... “I’m fine,” she said – or so I thought — really…");

        Assert.Equal("'Well'... \"I'm fine,\" she said - or so I thought - really...", t);
    }

    [Fact]
    public void Color_markup_becomes_bold()
    {
        var t = DialogueWikiText.ToWikitext("Look at <color=#FF0000>this</color>!");

        Assert.Equal("Look at '''this'''!", t);
    }

    [Fact]
    public void Bold_markup_is_left_untouched_because_it_is_already_valid_wiki_html()
    {
        var t = DialogueWikiText.ToWikitext("This is <b>important</b>.");

        Assert.Equal("This is <b>important</b>.", t);
    }

    [Fact]
    public void Italic_markup_becomes_wiki_italic()
    {
        var t = DialogueWikiText.ToWikitext("This is <i>whispered</i>.");

        Assert.Equal("This is ''whispered''.", t);
    }

    [Fact]
    public void Null_or_empty_text_yields_empty_string()
    {
        Assert.Equal("", DialogueWikiText.ToWikitext(null));
        Assert.Equal("", DialogueWikiText.ToWikitext(""));
    }

    [Fact]
    public void ToWikitext_does_not_trim_padding_because_FormatAsWikiTabber_also_calls_it()
    {
        var t = DialogueWikiText.ToWikitext("  padded  ");

        Assert.Equal("  padded  ", t);
    }

    [Fact]
    public void ForTemplateParameter_trims_leading_and_trailing_whitespace()
    {
        var t = DialogueWikiText.ForTemplateParameter("  padded  ");

        Assert.Equal("padded", t);
    }

    [Fact]
    public void Windows_line_break_becomes_br_tag_never_self_closed()
    {
        var t = DialogueWikiText.ForTemplateParameter("First line\r\nSecond line");

        Assert.Equal("First line<br>Second line", t);
        Assert.DoesNotContain("<br/>", t);
        Assert.DoesNotContain("<br />", t);
    }

    [Fact]
    public void Lone_unix_line_break_also_becomes_a_br_tag()
    {
        // v realnych datech se "\r\n" nevyskytuje vubec, jen osamocene "\n" (37x) - tahle vetev je
        // tedy ta jedina, ktera se v produkci skutecne pouzije
        var t = DialogueWikiText.ForTemplateParameter("First line\nSecond line");

        Assert.Equal("First line<br>Second line", t);
    }

    [Fact]
    public void Lone_carriage_return_also_becomes_a_br_tag()
    {
        var t = DialogueWikiText.ForTemplateParameter("First line\rSecond line");

        Assert.Equal("First line<br>Second line", t);
    }

    [Fact]
    public void Pipe_is_escaped_so_it_cannot_end_the_template_parameter()
    {
        var t = DialogueWikiText.ForTemplateParameter("Choose: yes|no");

        Assert.Equal("Choose: yes{{!}}no", t);
        Assert.DoesNotContain("|", t);
    }

    [Fact]
    public void Double_closing_brace_is_escaped_so_it_cannot_end_the_template()
    {
        var t = DialogueWikiText.ForTemplateParameter("That's a }} weird thing to say.");

        Assert.DoesNotContain("}}", t);
        Assert.Contains("}&#125;", t);
    }

    [Fact]
    public void Result_never_spans_multiple_lines()
    {
        var t = DialogueWikiText.ForTemplateParameter("Line one\nLine two\r\nLine three");

        Assert.DoesNotContain("\n", t);
        Assert.DoesNotContain("\r", t);
    }
}
