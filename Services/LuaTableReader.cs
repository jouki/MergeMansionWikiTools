using System.Globalization;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// A Lua table value: holds both the array part (positional entries) and the
/// hash part (named fields), mirroring how Lua tables mix the two.
/// </summary>
public sealed class LuaTable
{
    public List<object?> Array { get; } = new();
    public Dictionary<string, object?> Hash { get; } = new(StringComparer.Ordinal);

    public object? Get(string key) => Hash.TryGetValue(key, out var v) ? v : null;

    public double? Num(string key) => Get(key) switch
    {
        double d => d,
        _ => null,
    };

    public string? Str(string key) => Get(key) as string;
    public LuaTable? Tbl(string key) => Get(key) as LuaTable;
}

/// <summary>
/// Minimal reader for the constrained Lua table literals used by
/// <c>Module:Datatable/Events</c>. Supports nested tables (array + hash parts),
/// string / number / boolean / nil values, simple numeric expressions
/// (<c>+ - * /</c> and parentheses, e.g. <c>6 + 22 / 24</c>), and both line
/// (<c>--</c>) and block (<c>--[[ ]]</c>) comments.
///
/// This is NOT a general Lua parser — it deliberately handles only the shapes the
/// schedule module uses, so it can fail fast on anything unexpected.
/// </summary>
public static class LuaTableReader
{
    /// <summary>
    /// Parses a module body. Tolerates a leading <c>local p = ... return p</c> or a
    /// bare <c>return { ... }</c>; returns the first table literal that follows
    /// <c>return</c>, or the first top-level table literal found.
    /// Returns null if no table can be parsed.
    /// </summary>
    public static LuaTable? Parse(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        try
        {
            var p = new Parser(src);
            // Prefer the value after `return`; otherwise the first `{` at top level.
            var idx = FindReturnTable(src);
            if (idx >= 0) p.Seek(idx);
            else p.SeekToFirstBrace();
            p.SkipTrivia();
            return p.ParseValue() as LuaTable;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Index of the `{` that begins the table following a top-level `return`.</summary>
    private static int FindReturnTable(string src)
    {
        // Scan for the LAST `return` keyword (module epilogue), respecting strings/comments
        // is overkill here; the schedule module's only `return` precedes its table.
        var p = new Parser(src);
        int lastReturnBrace = -1;
        while (!p.Eof)
        {
            p.SkipTrivia();
            if (p.Eof) break;
            if (p.MatchKeyword("return"))
            {
                p.SkipTrivia();
                if (!p.Eof && p.Peek == '{') { lastReturnBrace = p.Pos; }
                continue;
            }
            p.Advance();
        }
        return lastReturnBrace;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) { _s = s; _i = 0; }

        public int Pos => _i;
        public bool Eof => _i >= _s.Length;
        public char Peek => _s[_i];
        public void Advance() => _i++;
        public void Seek(int i) => _i = i;

        public void SeekToFirstBrace()
        {
            while (!Eof && Peek != '{') _i++;
        }

        /// <summary>Skips whitespace and Lua comments (line and block).</summary>
        public void SkipTrivia()
        {
            while (!Eof)
            {
                var c = Peek;
                if (char.IsWhiteSpace(c)) { _i++; continue; }
                if (c == '-' && _i + 1 < _s.Length && _s[_i + 1] == '-')
                {
                    _i += 2;
                    // Block comment --[[ ... ]] (no long-bracket levels needed here)
                    if (_i + 1 < _s.Length && _s[_i] == '[' && _s[_i + 1] == '[')
                    {
                        _i += 2;
                        var end = _s.IndexOf("]]", _i, StringComparison.Ordinal);
                        _i = end < 0 ? _s.Length : end + 2;
                    }
                    else
                    {
                        while (!Eof && Peek != '\n') _i++;
                    }
                    continue;
                }
                break;
            }
        }

        /// <summary>Consumes <paramref name="kw"/> if it appears here as a whole word.</summary>
        public bool MatchKeyword(string kw)
        {
            if (_i + kw.Length > _s.Length) return false;
            if (string.CompareOrdinal(_s, _i, kw, 0, kw.Length) != 0) return false;
            var after = _i + kw.Length;
            if (after < _s.Length && (char.IsLetterOrDigit(_s[after]) || _s[after] == '_')) return false;
            _i = after;
            return true;
        }

        public object? ParseValue()
        {
            SkipTrivia();
            if (Eof) throw new FormatException("unexpected end of input");
            var c = Peek;
            if (c == '{') return ParseTable();
            if (c == '"' || c == '\'') return ParseString();
            if (MatchKeyword("true")) return true;
            if (MatchKeyword("false")) return false;
            if (MatchKeyword("nil")) return null;
            // number or arithmetic expression
            if (c == '-' || c == '+' || c == '.' || c == '(' || char.IsDigit(c))
                return ParseNumberExpr();
            throw new FormatException($"unexpected character '{c}' at {_i}");
        }

        private LuaTable ParseTable()
        {
            Expect('{');
            var t = new LuaTable();
            SkipTrivia();
            while (!Eof && Peek != '}')
            {
                // [ "key" ] = value   |   key = value   |   value (array part)
                if (Peek == '[')
                {
                    _i++;
                    SkipTrivia();
                    var key = ParseString();
                    SkipTrivia();
                    Expect(']');
                    SkipTrivia();
                    Expect('=');
                    t.Hash[key] = ParseValue();
                }
                else
                {
                    var ident = TryReadIdentifierAssign();
                    if (ident != null) t.Hash[ident] = ParseValue();
                    else t.Array.Add(ParseValue());
                }

                SkipTrivia();
                if (!Eof && (Peek == ',' || Peek == ';')) { _i++; SkipTrivia(); }
                else break;
            }
            SkipTrivia();
            Expect('}');
            return t;
        }

        /// <summary>
        /// If the upcoming token is <c>identifier =</c> (but not <c>==</c>), consumes it
        /// and returns the identifier; otherwise leaves the position untouched.
        /// </summary>
        private string? TryReadIdentifierAssign()
        {
            var save = _i;
            if (Eof || !(char.IsLetter(Peek) || Peek == '_')) return null;
            var start = _i;
            while (!Eof && (char.IsLetterOrDigit(Peek) || Peek == '_')) _i++;
            var ident = _s[start.._i];
            SkipTrivia();
            if (!Eof && Peek == '=' && !(_i + 1 < _s.Length && _s[_i + 1] == '='))
            {
                _i++; // consume '='
                return ident;
            }
            _i = save; // not an assignment (e.g. a bareword value) — rewind
            return null;
        }

        private string ParseString()
        {
            var quote = Peek;
            if (quote != '"' && quote != '\'') throw new FormatException("expected string");
            _i++;
            var sb = new System.Text.StringBuilder();
            while (!Eof && Peek != quote)
            {
                var c = Peek;
                if (c == '\\' && _i + 1 < _s.Length)
                {
                    _i++;
                    var e = Peek;
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                    _i++;
                }
                else { sb.Append(c); _i++; }
            }
            Expect(quote);
            return sb.ToString();
        }

        // ── Numeric expression: terms (+/-) of factors (*//) of primaries ──

        private double ParseNumberExpr() => ParseAddSub();

        private double ParseAddSub()
        {
            var v = ParseMulDiv();
            while (true)
            {
                SkipTrivia();
                if (Eof) break;
                var c = Peek;
                if (c == '+') { _i++; v += ParseMulDiv(); }
                else if (c == '-') { _i++; v -= ParseMulDiv(); }
                else break;
            }
            return v;
        }

        private double ParseMulDiv()
        {
            var v = ParsePrimary();
            while (true)
            {
                SkipTrivia();
                if (Eof) break;
                var c = Peek;
                if (c == '*') { _i++; v *= ParsePrimary(); }
                else if (c == '/') { _i++; v /= ParsePrimary(); }
                else break;
            }
            return v;
        }

        private double ParsePrimary()
        {
            SkipTrivia();
            if (Eof) throw new FormatException("expected number");
            if (Peek == '(')
            {
                _i++;
                var v = ParseAddSub();
                SkipTrivia();
                Expect(')');
                return v;
            }
            var start = _i;
            if (Peek == '-' || Peek == '+') _i++;
            while (!Eof && (char.IsDigit(Peek) || Peek == '.' || Peek == 'e' || Peek == 'E')) _i++;
            var span = _s[start.._i];
            if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                throw new FormatException($"bad number '{span}' at {start}");
            return num;
        }

        private void Expect(char c)
        {
            if (Eof || Peek != c) throw new FormatException($"expected '{c}' at {_i}");
            _i++;
        }
    }
}
