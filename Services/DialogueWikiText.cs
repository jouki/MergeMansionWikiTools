using System.Text.RegularExpressions;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Turns an in-game dialogue line into text safe for a wiki template parameter.
/// The game uses Unity rich text (`&lt;color=#RRGGBB&gt;`, `&lt;i&gt;`) and typographic
/// punctuation; a template parameter additionally must survive `|`, `}}` and line breaks.
/// `&lt;b&gt;` is deliberately left untouched — see <see cref="ToWikitext"/>.
/// </summary>
internal static class DialogueWikiText
{
    private static readonly Regex Color = new(@"<color=#[0-9A-Fa-f]{6,8}>(.*?)</color>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"<i>(.*?)</i>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Rich text and typography only — safe for running wikitext, not for a parameter.</summary>
    public static string ToWikitext(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var t = text
            .Replace('’', '\'').Replace('‘', '\'')
            .Replace('“', '"').Replace('”', '"')
            .Replace("…", "...")
            .Replace('–', '-').Replace('—', '-');

        // barva neni platne HTML mimo hru - syrova by se na wiki vypsala i s hex kodem,
        // proto se prevadi na tucne pismo. <b> uz platne HTML je a wiki ho vykresli samo,
        // takze zustava netknute - prevod by byl jen kosmeticky diff na zivych strankach.
        t = Color.Replace(t, "'''$1'''");
        t = Italic.Replace(t, "''$1''");
        return t;
    }

    /// <summary>As <see cref="ToWikitext"/>, plus escaping so the result survives inside a template parameter.</summary>
    public static string ForTemplateParameter(string? text)
    {
        var t = ToWikitext(text);

        // konec radku uvnitr parametru rozbije sablonu; projekt pouziva vsude <br>, nikdy <br/>.
        // V realnych datech se "\r\n" nevyskytuje vubec, jen osamocene "\n" (37x) - VSECHNY varianty
        // konce radku se proto nejdriv sjednoti na "\n" a teprve pak prevedou na <br>, jinak by se
        // (puvodni chyba) osamocene konce radku jen slepily mezerou misto zalomeni.
        t = t.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>");
        t = t.Replace("<br> ", "<br>");

        // poradi je dulezite: "}}" se escapuje PRED "|", jinak by druhy replace znovu
        // poskodil "{{!}}" ktery prvni replace prave vytvoril (obsahuje "}}")
        t = t.Replace("}}", "}&#125;").Replace("|", "{{!}}");

        // orezani okrajovych mezer patri jen sem, do pripravy parametru sablony - ToWikitext
        // volá i puvodni FormatAsWikiTabber a orezavani by tam zmenilo bajtovy vystup
        return Regex.Replace(t, @"[ \t]{2,}", " ").Trim();
    }
}
