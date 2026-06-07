using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MergeMansionWikiTools.Helpers;

/// <summary>
/// Editor-style hotkeys for a plain WPF <see cref="TextBox"/>:
///   * Ctrl+B / Ctrl+I / Ctrl+U  — wrap selection (or insert markers at caret) with wiki bold
///     <c>'''…'''</c> / italic <c>''…''</c> / underline <c>&lt;u&gt;…&lt;/u&gt;</c>.
///   * Alt+Up / Alt+Down         — swap the line(s) under the selection with the adjacent line.
/// Attach once via <see cref="Attach"/>; the instance hooks <c>PreviewKeyDown</c> on the TextBox.
/// </summary>
public class WikitextEditorHotkeys
{
    private readonly TextBox _textBox;

    public static WikitextEditorHotkeys Attach(TextBox textBox) => new(textBox);

    private WikitextEditorHotkeys(TextBox textBox)
    {
        _textBox = textBox;
        _textBox.PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // When Alt is held, WPF reports e.Key=Key.System with the real key in e.SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ctrl+B/I/U: wiki formatting wrap/unwrap
        if (ctrl && !alt && !shift)
        {
            switch (key)
            {
                case Key.B: ToggleWrap("'''", "'''", isItalic: false); e.Handled = true; return;
                case Key.I: ToggleWrap("''", "''", isItalic: true); e.Handled = true; return;
                case Key.U:
                    {
                        int ss = _textBox.SelectionStart;
                        int sl = _textBox.SelectionLength;
                        var t = _textBox.Text;
                        int ctxS = Math.Max(0, ss - 30);
                        int ctxE = Math.Min(t.Length, ss + sl + 30);
                        string ctxBefore = t.Substring(ctxS, ctxE - ctxS).Replace("\n", "\\n").Replace("\r", "\\r");
                        MergeMansionWikiTools.Services.AppLogger.Info(
                            $"[HOTKEY-U] BEFORE selStart={ss} selLen={sl} sel='{t.Substring(ss, sl).Replace("\n", "\\n")}' context='{ctxBefore}'");
                        ToggleTagWrap("<u>", "</u>");
                        int ss2 = _textBox.SelectionStart;
                        int sl2 = _textBox.SelectionLength;
                        var t2 = _textBox.Text;
                        int ctxS2 = Math.Max(0, ss2 - 30);
                        int ctxE2 = Math.Min(t2.Length, ss2 + sl2 + 30);
                        string ctxAfter = t2.Substring(ctxS2, ctxE2 - ctxS2).Replace("\n", "\\n").Replace("\r", "\\r");
                        MergeMansionWikiTools.Services.AppLogger.Info(
                            $"[HOTKEY-U] AFTER  selStart={ss2} selLen={sl2} context='{ctxAfter}'");
                        e.Handled = true; return;
                    }
            }
        }

        // Alt+Up/Down: move line(s) — works on multi-line selection AND on bare caret position
        if (alt && !ctrl && !shift)
        {
            if (key == Key.Up) { MoveLines(-1); e.Handled = true; return; }
            if (key == Key.Down) { MoveLines(+1); e.Handled = true; return; }
        }
    }

    /// <summary>
    /// Toggle bold ('''…''') or italic (''…'') wrap around selection. Smart detection handles
    /// the bold+italic combo ('''''…''''') so toggling italic on a bold-italic span removes
    /// exactly the outer 2 apostrophes (bold stays), and toggling bold on a bold-italic span
    /// removes exactly the inner 3 apostrophes (italic stays).
    ///
    /// Algorithm: count consecutive apostrophes IMMEDIATELY around the selection (suffix-count
    /// before, prefix-count after). Italic is "present" when both counts are 2 or 5; bold is
    /// "present" when both counts are 3 or 5. Toggle removes the matching pair; otherwise wraps.
    /// </summary>
    private void ToggleWrap(string open, string close, bool isItalic)
    {
        var text = _textBox.Text;
        int selStart = _textBox.SelectionStart;
        int selLen = _textBox.SelectionLength;
        int selEnd = selStart + selLen;

        // Peel "transparent" wrappers outward — apostrophe wraps can sit OUTSIDE other markup.
        // For Ctrl+I we also need to peel BOLD (3-apostrophe) wraps to find the outermost italic.
        // For Ctrl+B we peel ITALIC (2-apostrophe) wraps to find the outermost bold.
        // Both peel <u>/</u>.
        int peelStart = selStart;
        int peelEnd = selEnd;
        while (true)
        {
            // <u>...</u> wrap
            if (peelStart >= 3 && peelEnd + 4 <= text.Length
                && text.Substring(peelStart - 3, 3).Equals("<u>", StringComparison.OrdinalIgnoreCase)
                && text.Substring(peelEnd, 4).Equals("</u>", StringComparison.OrdinalIgnoreCase))
            {
                peelStart -= 3; peelEnd += 4;
                continue;
            }
            // For italic toggle: peel EXACTLY-3 apostrophe (bold) wrap. Exactness check
            // (next char beyond is NOT an apostrophe) prevents confusion with 5-apostrophe stacks
            // — those are detected directly via the leftQuotes/rightQuotes count below.
            if (isItalic
                && peelStart >= 3 && peelEnd + 3 <= text.Length
                && text[peelStart - 1] == '\'' && text[peelStart - 2] == '\'' && text[peelStart - 3] == '\''
                && (peelStart - 4 < 0 || text[peelStart - 4] != '\'')
                && text[peelEnd] == '\'' && text[peelEnd + 1] == '\'' && text[peelEnd + 2] == '\''
                && (peelEnd + 3 >= text.Length || text[peelEnd + 3] != '\''))
            {
                peelStart -= 3; peelEnd += 3;
                continue;
            }
            // For bold toggle: peel EXACTLY-2 apostrophe (italic) wrap.
            if (!isItalic
                && peelStart >= 2 && peelEnd + 2 <= text.Length
                && text[peelStart - 1] == '\'' && text[peelStart - 2] == '\''
                && (peelStart - 3 < 0 || text[peelStart - 3] != '\'')
                && text[peelEnd] == '\'' && text[peelEnd + 1] == '\''
                && (peelEnd + 2 >= text.Length || text[peelEnd + 2] != '\''))
            {
                peelStart -= 2; peelEnd += 2;
                continue;
            }
            break;
        }

        // Count consecutive apostrophes adjacent to the (possibly peeled) bounds
        int leftQuotes = 0;
        for (int i = peelStart - 1; i >= 0 && text[i] == '\''; i--) leftQuotes++;
        int rightQuotes = 0;
        for (int i = peelEnd; i < text.Length && text[i] == '\''; i++) rightQuotes++;

        bool present;
        int stripCount;  // how many apostrophes to remove from each side when unwrapping
        if (isItalic)
        {
            // Italic = 2 around. Also detected in 5-around (bold+italic) — outer layer.
            present = (leftQuotes == 2 && rightQuotes == 2) || (leftQuotes == 5 && rightQuotes == 5);
            stripCount = 2;
        }
        else
        {
            // Bold = 3 around. Also detected in 5-around (bold+italic) — inner layer.
            present = (leftQuotes == 3 && rightQuotes == 3) || (leftQuotes == 5 && rightQuotes == 5);
            stripCount = 3;
        }

        if (present)
        {
            // Unwrap — strip stripCount apostrophes adjacent to the (peeled) bounds.
            // For italic in a 5-stack: outer 2 are at [peelStart-5..peelStart-3] and
            // [peelEnd+3..peelEnd+5]. Inner 3 (bold) stay.
            int newStart;
            if (isItalic && leftQuotes == 5)
            {
                string before = text.Substring(0, peelStart - 5)
                              + text.Substring(peelStart - 3, 3);   // keep inner 3
                string after = text.Substring(peelEnd, 3)             // keep inner 3
                             + text.Substring(peelEnd + 5);
                _textBox.Text = before + text.Substring(peelStart, peelEnd - peelStart) + after;
                newStart = before.Length + (selStart - peelStart);
            }
            else
            {
                // Strip stripCount apostrophes immediately adjacent to peeled bounds
                string before = text.Substring(0, peelStart - stripCount);
                string after = text.Substring(peelEnd + stripCount);
                _textBox.Text = before + text.Substring(peelStart, peelEnd - peelStart) + after;
                newStart = before.Length + (selStart - peelStart);
            }
            _textBox.SelectionStart = newStart;
            _textBox.SelectionLength = selLen;
        }
        else
        {
            // Wrap — applies around the ORIGINAL selection (not the peeled bounds)
            string before = text.Substring(0, selStart);
            string mid = text.Substring(selStart, selLen);
            string after = text.Substring(selEnd);
            _textBox.Text = before + open + mid + close + after;
            _textBox.SelectionStart = selStart + open.Length;
            _textBox.SelectionLength = selLen;
        }
    }

    /// <summary>Toggle simple tag wrap like <c>&lt;u&gt;…&lt;/u&gt;</c>. Per user spec: peel ANY
    /// number of surrounding apostrophes (transparent for tag detection — bold ''', italic '',
    /// combo ''''', or anything else). Then if `&lt;u&gt;/&lt;/u&gt;` is present at the peeled
    /// bounds, unwrap; otherwise wrap. This handles `'''&lt;u&gt;text&lt;/u&gt;'''`,
    /// `''&lt;u&gt;text&lt;/u&gt;''`, `'''''&lt;u&gt;text&lt;/u&gt;'''''`, plain `&lt;u&gt;text&lt;/u&gt;`
    /// uniformly without per-pattern guards.</summary>
    private void ToggleTagWrap(string open, string close)
    {
        var text = _textBox.Text;
        int selStart = _textBox.SelectionStart;
        int selLen = _textBox.SelectionLength;
        int selEnd = selStart + selLen;

        // Greedy peel — strip ALL apostrophes touching the bounds. Counts on each side don't
        // need to match (user might have asymmetric markup). This is more permissive than the
        // previous exact-2/exact-3 approach but matches user's mental model: "ignore apostrophes
        // around my selection when deciding whether U is on or off".
        int peelStart = selStart;
        int peelEnd = selEnd;
        while (peelStart > 0 && text[peelStart - 1] == '\'') peelStart--;
        while (peelEnd < text.Length && text[peelEnd] == '\'') peelEnd++;

        bool hasOpen = peelStart >= open.Length
            && text.Substring(peelStart - open.Length, open.Length).Equals(open, StringComparison.OrdinalIgnoreCase);
        bool hasClose = peelEnd + close.Length <= text.Length
            && text.Substring(peelEnd, close.Length).Equals(close, StringComparison.OrdinalIgnoreCase);

        MergeMansionWikiTools.Services.AppLogger.Info(
            $"[HOTKEY-U-DBG] selStart={selStart} selEnd={selEnd} peelStart={peelStart} peelEnd={peelEnd} hasOpen={hasOpen} hasClose={hasClose}");

        if (hasOpen && hasClose)
        {
            // Unwrap: <u> sits at [peelStart-open.Length .. peelStart], </u> at [peelEnd .. peelEnd+close.Length].
            // Apostrophes (if any) at [peelStart .. selStart] and [selEnd .. peelEnd] are preserved.
            string before = text.Substring(0, peelStart - open.Length);
            string middle = text.Substring(peelStart, peelEnd - peelStart);  // apos + selection + apos
            string after = text.Substring(peelEnd + close.Length);
            _textBox.Text = before + middle + after;
            // Original selection shifts left by open.Length (= 3 for "<u>")
            _textBox.SelectionStart = selStart - open.Length;
            _textBox.SelectionLength = selLen;
        }
        else
        {
            // Wrap around ORIGINAL selection (preserve user's exact selection)
            string before = text.Substring(0, selStart);
            string mid = text.Substring(selStart, selLen);
            string after = text.Substring(selEnd);
            _textBox.Text = before + open + mid + close + after;
            _textBox.SelectionStart = selStart + open.Length;
            _textBox.SelectionLength = selLen;
        }
    }

    /// <summary>
    /// Moves the line(s) under the current selection up (delta=-1) or down (delta=+1) by one row.
    /// Multi-line selection moves as a block. No-op when at the edge.
    /// </summary>
    private void MoveLines(int delta)
    {
        var text = _textBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        // Identify selection's line span
        int selStart = _textBox.SelectionStart;
        int selEnd = selStart + _textBox.SelectionLength;

        int firstLineStart = LineStartOffset(text, selStart);
        int lastLineEnd = LineEndOffset(text, selEnd);
        string blockText = text.Substring(firstLineStart, lastLineEnd - firstLineStart);

        // Caret/selection offsets relative to blockText (we restore them after swap)
        int selStartRel = selStart - firstLineStart;
        int selLenRel = _textBox.SelectionLength;

        if (delta < 0)
        {
            // Up: need a line above
            if (firstLineStart == 0) return;
            int prevLineStart = LineStartOffset(text, firstLineStart - 1);
            string prevLine = text.Substring(prevLineStart, firstLineStart - prevLineStart);
            // Reorder: [...prev start...] [BLOCK + newline] [PREV LINE WITHOUT trailing newline?]
            // Simpler: swap the two regions.
            //   before = text up to prevLineStart
            //   region1 = prevLine (with its trailing \n)
            //   region2 = blockText (with its trailing \n if not EOF)
            // After: before + region2 + region1 + after
            string before = text.Substring(0, prevLineStart);
            string after = text.Substring(lastLineEnd);
            // Ensure blockText ends with \n when there's content after — otherwise we'd glue lines
            string region2 = blockText;
            if (!region2.EndsWith("\n"))
            {
                // blockText is the LAST chunk → it had no trailing newline; preserve original prev's trailing newline
                region2 = blockText + "\n";
                // And drop prevLine's trailing newline (so it becomes the last line)
                if (prevLine.EndsWith("\n")) prevLine = prevLine.TrimEnd('\n');
            }
            _textBox.Text = before + region2 + prevLine + after;
            int newBlockStart = prevLineStart;
            _textBox.SelectionStart = newBlockStart + selStartRel;
            _textBox.SelectionLength = selLenRel;
        }
        else
        {
            // Down: need a line below
            if (lastLineEnd >= text.Length) return;
            int nextLineEnd = LineEndOffset(text, lastLineEnd + 1);
            string nextLine = text.Substring(lastLineEnd, nextLineEnd - lastLineEnd);
            string before = text.Substring(0, firstLineStart);
            string after = text.Substring(nextLineEnd);
            string region1 = blockText;
            string region2 = nextLine;
            // If region2 has no trailing newline (= it was the last line of the doc), borrow
            // the trailing newline from region1 so the order swaps cleanly.
            if (!region2.EndsWith("\n") && region1.EndsWith("\n"))
            {
                region2 = region2 + "\n";
                region1 = region1.TrimEnd('\n');
            }
            _textBox.Text = before + region2 + region1 + after;
            int newBlockStart = firstLineStart + region2.Length;
            _textBox.SelectionStart = newBlockStart + selStartRel;
            _textBox.SelectionLength = selLenRel;
        }
    }

    /// <summary>Offset of the start of the line containing <paramref name="pos"/>.</summary>
    private static int LineStartOffset(string text, int pos)
    {
        pos = Math.Clamp(pos, 0, text.Length);
        int i = pos - 1;
        while (i >= 0 && text[i] != '\n') i--;
        return i + 1;
    }

    /// <summary>Offset of the END of the line containing <paramref name="pos"/> — INCLUDING
    /// its terminating '\n' if present (or end-of-text otherwise).</summary>
    private static int LineEndOffset(string text, int pos)
    {
        pos = Math.Clamp(pos, 0, text.Length);
        int i = pos;
        while (i < text.Length && text[i] != '\n') i++;
        if (i < text.Length) i++;  // include the '\n'
        return i;
    }
}
