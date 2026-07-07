using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MergeMansionWikiTools.Helpers;

/// <summary>
/// Lightweight wikitext autocomplete for a TextBox. Triggers when cursor sits inside {{...}}:
///   * inside template name (e.g. {{Ite|) → list of template names, Item/Group first
///   * inside first argument of an Item-shape template (e.g. {{Item/Group|Cin|) →
///     {{PAGENAME}} + hardcoded current chain name (when prefix matches) + all matching chain names
/// Designed for zero UI hitches:
///   - 30ms debounce coalesces rapid keystrokes into one filter pass
///   - Candidate list pre-sorted once via SetChainNames (caller can do this off-thread)
///   - Filter is a single linear scan with early-exit on `Take(MaxItems)` cap
///   - ListBox uses recycling virtualization → only visible items render even for ~3000 candidates
///   - ListBox is non-focusable so keyboard focus stays in the editor (no costly focus shuffle)
/// </summary>
public class WikitextAutocomplete
{
    /// <summary>Hardcoded template suggestions ordered by likely relevance.</summary>
    private static readonly string[] Templates =
    {
        "Item/Group", "Item", "Item/nolevel", "Item/Icon",
        "Area",
        "PAGENAME",
        "Dash", "Coins", "XPDrop",
    };

    /// <summary>Templates that take at least one positional arg — accepting their name appends
    /// `|` automatically so the user can type the next arg without an extra keystroke.</summary>
    private static readonly HashSet<string> TemplatesTakingArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Item", "Item/Group", "Item/nolevel", "Item/Icon", "Area",
    };

    private const int DebounceMs = 30;

    private readonly TextBox _textBox;
    private readonly Popup _popup;
    private readonly ListBox _listBox;
    private readonly DispatcherTimer _debounce;

    private List<string> _chainNames = new();
    private bool _chainNamesLoaded = false;
    private Dictionary<string, int> _chainMaxLevel = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _chainImagePath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Rect> _chainSpriteRect = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _chainSpriteRotated = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _areaNames = new();
    private Dictionary<string, string> _areaImagePath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Rect> _areaSpriteRect = new(StringComparer.OrdinalIgnoreCase);
    private string _currentTempAreaName = "";
    private string _currentChainName = "";
    private bool _currentChainHasDisplayTitle;
    private bool _suppressTextEvent;

    public WikitextAutocomplete(TextBox textBox)
    {
        _textBox = textBox;

        // Solid opaque background — theme resource brushes can be translucent against the parent
        // dialog's Mica backdrop, which leaves the popup looking glassy/unreadable.
        var popupBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        var popupFg = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

        _listBox = new ListBox
        {
            MaxHeight = 280,
            MinWidth = 300,
            Background = popupBg,
            Foreground = popupFg,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(0),
            Focusable = false,            // keep keyboard in TextBox
            ItemTemplate = BuildItemTemplate(popupFg),
            ItemContainerStyle = BuildItemContainerStyle(),
        };
        VirtualizingPanel.SetIsVirtualizing(_listBox, true);
        VirtualizingPanel.SetVirtualizationMode(_listBox, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_listBox, true);
        // Use MouseDown (not Up) and PREVIEW so we run before any default ListBox handling.
        // Hit-test resolves the clicked ListBoxItem; we accept immediately and restore focus.
        _listBox.PreviewMouseLeftButtonDown += OnListBoxMouseDown;

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = popupBg,
            Child = _listBox,
        };

        _popup = new Popup
        {
            PlacementTarget = _textBox,
            Placement = PlacementMode.Relative,
            // StaysOpen=true so the Popup doesn't swallow mouse events for "inside/outside"
            // detection — we manage close ourselves on Esc / Enter / Tab / accept / focus loss.
            // (StaysOpen=false has a known WPF quirk where it intercepts mouse-down and
            // child controls never see the click.)
            StaysOpen = true,
            AllowsTransparency = true,
            Focusable = false,
            Child = border,
        };

        _debounce = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMs),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            UpdateSuggestions();
        };

        _textBox.PreviewKeyDown += OnPreviewKeyDown;
        _textBox.TextChanged += OnTextChanged;
        _textBox.SelectionChanged += (_, _) =>
        {
            if (_suppressTextEvent) return;
            _debounce.Stop();
            _debounce.Start();
        };
        _textBox.LostFocus += (_, _) =>
        {
            // Defer close so a ListBox mouse-up inside the popup can fire AcceptCompletion first.
            _textBox.Dispatcher.BeginInvoke(new Action(() => _popup.IsOpen = false), DispatcherPriority.Background);
        };
    }

    /// <summary>
    /// Replaces the suggestion pool. Caller can pre-sort/dedupe off-thread; this method is cheap
    /// (assignment only). Safe to call multiple times.
    /// </summary>
    public void SetChainNames(IEnumerable<string> names)
    {
        _chainNames = names
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _chainNamesLoaded = true;
        // Removed eager refresh — caller will explicitly RefreshIfOpen() after batch-setting
        // names + image paths + sprite rects, so the popup re-renders with FULL data instead of
        // partial (names without thumbnails).
    }

    /// <summary>Forces a re-render of the open suggestion popup. Call after batch-updating
    /// multiple data sources (names + image paths + rects) so the popup picks up everything.</summary>
    public void RefreshIfOpen()
    {
        if (_popup.IsOpen) UpdateSuggestions();
    }

    /// <summary>Map chain name → max level. Used for level slot ({{Item/Group|Name|HERE}})
    /// auto-suggestion. Safe to call before/after <see cref="SetChainNames"/>.</summary>
    public void SetChainMaxLevels(IReadOnlyDictionary<string, int> nameToMaxLevel)
    {
        _chainMaxLevel = new Dictionary<string, int>(nameToMaxLevel, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Map chain name → absolute local image path (max-level item PNG). Used to render
    /// a 24×24 thumbnail next to each chain suggestion. Missing entries render text-only.</summary>
    public void SetChainImagePaths(IReadOnlyDictionary<string, string> nameToImagePath)
    {
        _chainImagePath = new Dictionary<string, string>(nameToImagePath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Map chain name → ATLAS sub-rectangle (bottom-left Y origin, pixel coords).
    /// Y-flip happens in the converter at render time so we don't need to load image dims upfront.</summary>
    public void SetChainSpriteRects(IReadOnlyDictionary<string, Rect> nameToAtlasRect)
    {
        _chainSpriteRect = new Dictionary<string, Rect>(nameToAtlasRect, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Set of chain names whose sprite is stored ROTATED 90° in the atlas. Converter
    /// applies inverse rotation when rendering the thumbnail.</summary>
    public void SetChainSpriteRotated(IEnumerable<string> names)
    {
        _chainSpriteRotated = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All known area display names. Suggested after `{{Area|`. Pre-sorted alphabetically.</summary>
    public void SetAreaNames(IEnumerable<string> names)
    {
        _areaNames = names
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>For temporary chains: the area that hosts them on Main Board. Surfaced as the
    /// first {{Area|... suggestion when set, so the most relevant pick is one keystroke away.</summary>
    public void SetCurrentTemporaryAreaName(string? name) => _currentTempAreaName = name ?? "";

    /// <summary>Map area display name → absolute image path (typically Area_InfoPopupBg_XXX.png).
    /// Empty/missing entries render as text-only suggestion rows.</summary>
    public void SetAreaImagePaths(IReadOnlyDictionary<string, string> nameToImagePath)
    {
        _areaImagePath = new Dictionary<string, string>(nameToImagePath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Map area display name → FRACTIONAL crop rect (0..1 coords). Render-time
    /// scales to absolute pixel coords using bitmap dimensions. Used to trim popup-background
    /// borders by taking the centered 70% of the image.</summary>
    public void SetAreaFractionalRects(IReadOnlyDictionary<string, Rect> nameToFractional)
    {
        _areaSpriteRect = new Dictionary<string, Rect>(nameToFractional, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Name of the chain the dialog is currently editing (= candidate for hardcoded shortcut).</summary>
    public void SetCurrentChainName(string name) => _currentChainName = name ?? "";

    /// <summary>Marks the current chain as having a parenthetical disambiguation suffix
    /// (= DisplayTitle is set on the wiki page). When true, accepting `}}` on Item-shape
    /// templates referencing {{PAGENAME}} auto-inserts `|displayName={{#var:DisplayTitle}}`
    /// so the rendered text matches the short title.</summary>
    public void SetCurrentChainHasDisplayTitle(bool value) => _currentChainHasDisplayTitle = value;

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextEvent) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                // Wrap-around: past last → first
                _listBox.SelectedIndex = _listBox.SelectedIndex >= _listBox.Items.Count - 1
                    ? 0
                    : _listBox.SelectedIndex + 1;
                if (_listBox.SelectedItem != null) _listBox.ScrollIntoView(_listBox.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                // Wrap-around: before first → last
                _listBox.SelectedIndex = _listBox.SelectedIndex <= 0
                    ? _listBox.Items.Count - 1
                    : _listBox.SelectedIndex - 1;
                if (_listBox.SelectedItem != null) _listBox.ScrollIntoView(_listBox.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageDown:
                _listBox.SelectedIndex = Math.Min(_listBox.Items.Count - 1, _listBox.SelectedIndex + 8);
                if (_listBox.SelectedItem != null) _listBox.ScrollIntoView(_listBox.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageUp:
                _listBox.SelectedIndex = Math.Max(0, _listBox.SelectedIndex - 8);
                if (_listBox.SelectedItem != null) _listBox.ScrollIntoView(_listBox.SelectedItem);
                e.Handled = true;
                break;
            case Key.Tab:
            case Key.Enter:
                if (_listBox.SelectedItem != null)
                {
                    AcceptCompletion();
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                _popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnListBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_listBox);
        var hit = VisualTreeHelper.HitTest(_listBox, pos);
        if (hit?.VisualHit is not DependencyObject visualHit) return;
        var item = _listBox.ContainerFromElement(visualHit) as ListBoxItem
                   ?? FindAncestor<ListBoxItem>(visualHit);
        if (item?.DataContext is SuggestionItem si)
        {
            _listBox.SelectedItem = si;
            AcceptCompletion();
            _textBox.Focus();
            e.Handled = true;
        }
    }

    private void UpdateSuggestions()
    {
        var ctx = DetectContext();
        if (ctx == null) { _popup.IsOpen = false; return; }

        var suggestions = ComputeSuggestions(ctx);
        if (suggestions.Count == 0) { _popup.IsOpen = false; return; }

        // Reusing ItemsSource is cheaper than rebuilding the visual tree — but ListBox doesn't
        // diff List<string>, so swap is fine. Virtualization keeps cost low.
        _listBox.ItemsSource = suggestions;
        _listBox.SelectedIndex = 0;

        PositionPopupAtCaret();
        _popup.IsOpen = true;
    }

    private void PositionPopupAtCaret()
    {
        try
        {
            int caretIdx = Math.Min(_textBox.CaretIndex, _textBox.Text?.Length ?? 0);
            var rect = _textBox.GetRectFromCharacterIndex(caretIdx);
            if (double.IsInfinity(rect.X) || double.IsInfinity(rect.Y)) return;
            _popup.HorizontalOffset = rect.X;
            _popup.VerticalOffset = rect.Y + rect.Height + 2;
        }
        catch { /* CaretIndex can race during fast input */ }
    }

    /// <summary>
    /// Walks backward from the caret to find the nearest {{ trigger that isn't closed by a }}.
    /// Returns a context describing what kind of suggestion (template name vs first argument)
    /// is appropriate and where the typed prefix lives in the text.
    /// </summary>
    private AutoContext? DetectContext()
    {
        string text = _textBox.Text ?? "";
        int caret = _textBox.CaretIndex;
        if (caret < 2 || caret > text.Length) return null;

        // Stack-based scan from start to caret: push on {{, pop on }}. Top of stack at caret
        // is the enclosing unclosed {{. This correctly handles nested templates like
        // {{Item/Group|{{PAGENAME}}| where the inner }} would have falsely bailed a naive
        // backward scan.
        var stack = new Stack<int>();
        for (int i = 0; i < caret - 1; i++)
        {
            if (text[i] == '{' && text[i + 1] == '{')
            {
                stack.Push(i);
                i++;  // skip second '{'
            }
            else if (text[i] == '}' && text[i + 1] == '}')
            {
                if (stack.Count > 0) stack.Pop();
                i++;  // skip second '}'
            }
        }
        if (stack.Count == 0) return null;
        int braceStart = stack.Peek();

        string fragment = text.Substring(braceStart + 2, caret - braceStart - 2);
        int firstPipe = fragment.IndexOf('|');
        if (firstPipe < 0)
        {
            return new AutoContext(ContextKind.TemplateName, fragment, braceStart + 2, caret, null);
        }

        string template = fragment.Substring(0, firstPipe).Trim();
        int pipeCount = 0;
        foreach (var c in fragment) if (c == '|') pipeCount++;

        int lastPipe = fragment.LastIndexOf('|');
        string currentArg = fragment.Substring(lastPipe + 1);
        int prefixStart = braceStart + 2 + lastPipe + 1;

        if (pipeCount == 1)
        {
            // {{Template|HERE — first arg slot (chain name)
            return new AutoContext(ContextKind.Argument, currentArg, prefixStart, caret, template);
        }
        if (pipeCount == 2)
        {
            // {{Template|Name|HERE — level slot
            int secondPipe = fragment.IndexOf('|', firstPipe + 1);
            string chainName = fragment.Substring(firstPipe + 1, secondPipe - firstPipe - 1).Trim();
            return new AutoContext(ContextKind.LevelArg, currentArg, prefixStart, caret, template, chainName);
        }

        // pipeCount >= 3 → named parameter slot. Two sub-cases:
        // (a) currentArg contains '=' → value side, suggest values for that param
        // (b) no '=' → name side, suggest param NAMES (excluding already-used ones)
        // Extract chain name (between 1st and 2nd pipe) — used for value suggestions like displayName.
        int p2 = fragment.IndexOf('|', firstPipe + 1);
        string chain = p2 > firstPipe ? fragment.Substring(firstPipe + 1, p2 - firstPipe - 1).Trim() : "";

        int eqIdx = currentArg.IndexOf('=');
        if (eqIdx >= 0)
        {
            // Value side: {{...|param=HERE
            string paramName = currentArg.Substring(0, eqIdx).Trim();
            string valuePrefix = currentArg.Substring(eqIdx + 1);
            int valuePrefixStart = prefixStart + eqIdx + 1;
            var usedForValue = ScanUsedParamValues(fragment);
            return new AutoContext(ContextKind.NamedValueArg, valuePrefix, valuePrefixStart, caret,
                template, chain, paramName, usedForValue);
        }

        // Name side: scan args for "<name>=<value>" pairs already present in this invocation.
        var usedDict = ScanUsedParamValues(fragment);
        return new AutoContext(ContextKind.NamedArg, currentArg, prefixStart, caret, template, chain, null, usedDict);
    }

    /// <summary>Parses already-set "name=value" args (positions 3+ in pipe-split fragment) into
    /// a name→value map. Excludes the in-progress (= last) arg.</summary>
    private static Dictionary<string, string> ScanUsedParamValues(string fragment)
    {
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = fragment.Split('|');
        for (int i = 3; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            int eq = part.IndexOf('=');
            if (eq > 0) used[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
        }
        return used;
    }

    private List<SuggestionItem> ComputeSuggestions(AutoContext ctx)
    {
        if (ctx.Kind == ContextKind.TemplateName)
        {
            return Templates
                .Where(t => string.IsNullOrEmpty(ctx.Prefix) ||
                            t.IndexOf(ctx.Prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(t => new SuggestionItem(t, null))
                .ToList();
        }

        if (ctx.Kind == ContextKind.LevelArg)
        {
            if (ctx.Template == null || !IsItemTemplate(ctx.Template)) return new List<SuggestionItem>();
            var levelResult = new List<SuggestionItem>();
            string lvlPrefix = ctx.Prefix ?? "";
            if (!string.IsNullOrEmpty(ctx.ChainName))
            {
                string lookupName = ctx.ChainName == "{{PAGENAME}}" ? _currentChainName : ctx.ChainName;
                if (!string.IsNullOrEmpty(lookupName) && _chainMaxLevel.TryGetValue(lookupName, out var maxLvl))
                {
                    string maxStr = maxLvl.ToString();
                    if (string.IsNullOrEmpty(lvlPrefix) ||
                        maxStr.StartsWith(lvlPrefix, StringComparison.OrdinalIgnoreCase))
                        levelResult.Add(new SuggestionItem(maxStr, null));
                }
            }
            // }} close option — preceding pipe will be stripped on accept (see AcceptCompletion)
            if (string.IsNullOrEmpty(lvlPrefix) ||
                "}}".StartsWith(lvlPrefix, StringComparison.OrdinalIgnoreCase))
                levelResult.Add(new SuggestionItem("}}", null));
            return levelResult;
        }

        if (ctx.Kind == ContextKind.NamedArg)
        {
            if (ctx.Template == null || !IsItemTemplate(ctx.Template)) return new List<SuggestionItem>();
            var all = new[] { "min=", "max=", "fullName=true", "displayName=", "size=", "}}" };
            var usedKeys = ctx.UsedParamValues?.Keys ?? Enumerable.Empty<string>();
            var usedSet = new HashSet<string>(usedKeys, StringComparer.OrdinalIgnoreCase);
            return all
                .Where(p =>
                {
                    if (p == "}}") return string.IsNullOrEmpty(ctx.Prefix) ||
                                          p.StartsWith(ctx.Prefix, StringComparison.OrdinalIgnoreCase);
                    int eq = p.IndexOf('=');
                    string name = eq > 0 ? p.Substring(0, eq) : p;
                    if (usedSet.Contains(name)) return false;
                    return string.IsNullOrEmpty(ctx.Prefix) ||
                           p.StartsWith(ctx.Prefix, StringComparison.OrdinalIgnoreCase);
                })
                .Select(p => new SuggestionItem(p, null))
                .ToList();
        }

        if (ctx.Kind == ContextKind.NamedValueArg)
        {
            if (ctx.Template == null || !IsItemTemplate(ctx.Template)) return new List<SuggestionItem>();
            string valPrefix = ctx.Prefix ?? "";
            switch (ctx.ParamName?.ToLowerInvariant())
            {
                case "size":
                    return new[] { "48px", "64px", "96px", "128px" }
                        .Where(v => string.IsNullOrEmpty(valPrefix) ||
                                    v.StartsWith(valPrefix, StringComparison.OrdinalIgnoreCase))
                        .Select(v => new SuggestionItem(v, null))
                        .ToList();
                case "displayname":
                {
                    // Suggest chain name without parenthetical suffix (e.g.
                    // "Getting Around (Maddie in Japan)" → "Getting Around").
                    string chain = ctx.ChainName ?? "";
                    if (chain == "{{PAGENAME}}") chain = _currentChainName;
                    int paren = chain.IndexOf('(');
                    string baseName = paren > 0 ? chain.Substring(0, paren).TrimEnd() : chain;
                    if (string.IsNullOrEmpty(baseName)) return new List<SuggestionItem>();
                    if (!string.IsNullOrEmpty(valPrefix) &&
                        !baseName.StartsWith(valPrefix, StringComparison.OrdinalIgnoreCase))
                        return new List<SuggestionItem>();
                    return new List<SuggestionItem> { new(baseName, null) };
                }
                case "fullname":
                    return new[] { "true" }
                        .Where(v => string.IsNullOrEmpty(valPrefix) ||
                                    v.StartsWith(valPrefix, StringComparison.OrdinalIgnoreCase))
                        .Select(v => new SuggestionItem(v, null))
                        .ToList();
                case "min":
                case "max":
                {
                    // Level range constrained by the OTHER param if already set: typing `min=`
                    // when `max=5` is present → only suggest 1..5; typing `max=` when `min=5` is
                    // present → only suggest 5..maxLevel.
                    string chain = ctx.ChainName ?? "";
                    if (chain == "{{PAGENAME}}") chain = _currentChainName;
                    if (string.IsNullOrEmpty(chain) || !_chainMaxLevel.TryGetValue(chain, out var maxL))
                        return new List<SuggestionItem>();
                    bool isMin = ctx.ParamName.Equals("min", StringComparison.OrdinalIgnoreCase);
                    int rangeFrom = 1;
                    int rangeTo = maxL;
                    if (ctx.UsedParamValues != null)
                    {
                        if (isMin && ctx.UsedParamValues.TryGetValue("max", out var maxStr)
                            && int.TryParse(maxStr, out var otherMax))
                            rangeTo = Math.Min(rangeTo, otherMax);
                        if (!isMin && ctx.UsedParamValues.TryGetValue("min", out var minStr)
                            && int.TryParse(minStr, out var otherMin))
                            rangeFrom = Math.Max(rangeFrom, otherMin);
                    }
                    if (rangeFrom > rangeTo) return new List<SuggestionItem>();
                    var range = Enumerable.Range(rangeFrom, rangeTo - rangeFrom + 1);
                    if (!isMin) range = range.Reverse();
                    return range
                        .Select(i => i.ToString())
                        .Where(s => string.IsNullOrEmpty(valPrefix) ||
                                    s.StartsWith(valPrefix, StringComparison.OrdinalIgnoreCase))
                        .Select(s => new SuggestionItem(s, null))
                        .ToList();
                }
                default:
                    return new List<SuggestionItem>();
            }
        }

        // ContextKind.Argument = first pipe — chain name for Item templates, area name for {{Area|
        if (ctx.Template != null && ctx.Template.Equals("Area", StringComparison.OrdinalIgnoreCase))
        {
            var areaPrefix = ctx.Prefix ?? "";
            var areaResult = new List<SuggestionItem>();
            Rect? AreaRect(string nm) => _areaSpriteRect.TryGetValue(nm, out var r) ? r : null;
            if (!string.IsNullOrEmpty(_currentTempAreaName) &&
                (string.IsNullOrEmpty(areaPrefix) ||
                 _currentTempAreaName.IndexOf(areaPrefix, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                areaResult.Add(new SuggestionItem(_currentTempAreaName,
                    _areaImagePath.GetValueOrDefault(_currentTempAreaName),
                    FractionalRect: AreaRect(_currentTempAreaName)));
            }
            foreach (var a in _areaNames)
            {
                if (!string.IsNullOrEmpty(areaPrefix) &&
                    a.IndexOf(areaPrefix, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (string.Equals(a, _currentTempAreaName, StringComparison.OrdinalIgnoreCase)) continue;
                areaResult.Add(new SuggestionItem(a, _areaImagePath.GetValueOrDefault(a),
                    FractionalRect: AreaRect(a)));
            }
            return areaResult;
        }

        if (ctx.Template == null || !IsItemTemplate(ctx.Template))
            return new List<SuggestionItem>();

        var result = new List<SuggestionItem>();
        string prefix = ctx.Prefix ?? "";

        bool currentMatches = string.IsNullOrEmpty(prefix) ||
            (!string.IsNullOrEmpty(_currentChainName) &&
             _currentChainName.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0);
        bool pageNameMatches = string.IsNullOrEmpty(prefix) ||
            "{{PAGENAME}}".IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0;
        Rect? CurrentRect() => _chainSpriteRect.TryGetValue(_currentChainName, out var r1) ? r1 : null;
        bool CurrentRot() => _chainSpriteRotated.Contains(_currentChainName);
        if (pageNameMatches && currentMatches)
            result.Add(new SuggestionItem("{{PAGENAME}}",
                _chainImagePath.GetValueOrDefault(_currentChainName),
                AtlasRect: CurrentRect(), IsRotated: CurrentRot()));
        if (currentMatches && !string.IsNullOrEmpty(_currentChainName))
            result.Add(new SuggestionItem(_currentChainName,
                _chainImagePath.GetValueOrDefault(_currentChainName),
                AtlasRect: CurrentRect(), IsRotated: CurrentRot()));

        if (!_chainNamesLoaded)
        {
            result.Add(new SuggestionItem("Loading…", null));
            return result;
        }

        foreach (var n in _chainNames)
        {
            if (!string.IsNullOrEmpty(prefix) &&
                n.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (string.Equals(n, _currentChainName, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new SuggestionItem(n,
                _chainImagePath.GetValueOrDefault(n),
                AtlasRect: _chainSpriteRect.TryGetValue(n, out var r2) ? r2 : (Rect?)null,
                IsRotated: _chainSpriteRotated.Contains(n)));
        }
        return result;
    }

    private static bool IsItemTemplate(string t) =>
        t.Equals("Item", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Item/Group", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Item/nolevel", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Item/Icon", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when accepting `}}` should inject `|displayName={{#var:DisplayTitle}}`:
    /// chain ref must reference the parenthetical chain (either {{PAGENAME}} OR the literal
    /// full chain name including the disambiguation suffix). In both cases the rendered wiki
    /// page name keeps the paren, so displayName is needed to show the short title.</summary>
    private bool ShouldInjectDisplayTitle(AutoContext ctx)
    {
        // Chain ref must point at the parenthetical chain — accept both {{PAGENAME}} (= current
        // page is the paren chain) AND the literal full chain name (= user explicitly named it).
        bool refsPagename = ctx.ChainName == "{{PAGENAME}}";
        bool refsFullName = !string.IsNullOrEmpty(_currentChainName)
            && ctx.ChainName != null
            && ctx.ChainName.Equals(_currentChainName, StringComparison.OrdinalIgnoreCase);
        if (!refsPagename && !refsFullName) return false;
        // Don't double-add — bail if displayName already exists anywhere in this invocation
        if (ctx.UsedParamValues != null && ctx.UsedParamValues.ContainsKey("displayName"))
            return false;
        // For NamedArg/NamedValueArg contexts the fragment is parsed; for LevelArg the fragment
        // only has 2 pipes so UsedParamValues is null — scan the in-progress fragment manually.
        var text = _textBox.Text ?? "";
        int caret = _textBox.CaretIndex;
        int braceStart = FindEnclosingBraceStart(text, caret);
        if (braceStart >= 0)
        {
            string fragment = text.Substring(braceStart + 2, caret - braceStart - 2);
            if (fragment.IndexOf("|displayName=", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }
        return true;
    }

    private static int FindEnclosingBraceStart(string text, int caret)
    {
        var stack = new Stack<int>();
        for (int i = 0; i < caret - 1; i++)
        {
            if (text[i] == '{' && text[i + 1] == '{') { stack.Push(i); i++; }
            else if (text[i] == '}' && text[i + 1] == '}') { if (stack.Count > 0) stack.Pop(); i++; }
        }
        return stack.Count > 0 ? stack.Peek() : -1;
    }

    private void AcceptCompletion()
    {
        var ctx = DetectContext();
        if (ctx == null || _listBox.SelectedItem is not SuggestionItem selected) return;
        // Placeholder row — not a real suggestion
        if (selected.Text == "Loading…") return;

        string insert = selected.Text.Trim();
        string textFull = _textBox.Text;
        int safeEnd = Math.Min(ctx.PrefixEnd, textFull.Length);
        int safeStart = Math.Min(ctx.PrefixStart, safeEnd);

        // Auto-pipe / auto-close logic per context.
        //   * }} accept: strip a preceding `|` (user picked close instead of continuing args)
        //                + auto-insert `|displayName={{#var:DisplayTitle}}` when parenthetical
        //                page AND template uses {{PAGENAME}} AND displayName isn't already set
        //   * Template name accept: auto `|` if template takes args
        //   * Single-arg template (Area): after first-arg accept, auto `}}` (done)
        //   * Multi-arg Item template first-arg accept: auto `|` (continue to level)
        //   * Level / NamedValue accept: auto `|` (might continue with more named params)
        bool isClose = insert == "}}";
        if (isClose)
        {
            // Strip preceding `|` so we don't end up with `|}}`
            if (safeStart > 0 && textFull[safeStart - 1] == '|') safeStart--;

            // Auto-insert displayName for parenthetical pages — see SetCurrentChainHasDisplayTitle
            if (_currentChainHasDisplayTitle && ctx.Template != null && IsItemTemplate(ctx.Template)
                && ShouldInjectDisplayTitle(ctx))
            {
                insert = "|displayName={{#var:DisplayTitle}}}}";
            }
        }
        else if (ctx.Kind == ContextKind.TemplateName && TemplatesTakingArgs.Contains(insert))
        {
            insert += "|";
        }
        else if (ctx.Kind == ContextKind.Argument && ctx.Template != null)
        {
            if (ctx.Template.Equals("Area", StringComparison.OrdinalIgnoreCase))
                insert += "}}";
            else if (IsItemTemplate(ctx.Template))
                insert += "|";
        }
        else if (ctx.Kind == ContextKind.LevelArg || ctx.Kind == ContextKind.NamedValueArg)
        {
            insert += "|";
        }

        // Smart consume for NamedArg accept: when accepting a "name=" suggestion AND the cursor
        // sits INSIDE an existing "<word>=<value>" arg (= user is replacing the name part),
        // scan forward to consume the rest of the existing name + the '='. Value to the right of
        // '=' is preserved. So clicking "min=" with cursor at "m|in=2" → "min=2", not "min=in=2".
        if (ctx.Kind == ContextKind.NamedArg && insert.EndsWith("=") && safeEnd < textFull.Length)
        {
            int scan = safeEnd;
            while (scan < textFull.Length && (char.IsLetterOrDigit(textFull[scan]) || textFull[scan] == '_'))
                scan++;
            if (scan < textFull.Length && textFull[scan] == '=')
                safeEnd = scan + 1;  // include the '='
        }

        // NamedValueArg/LevelArg accept: replace WHOLE value (cursor may be inside existing
        // value). Scan forward to next '|' or '}}' so clicking "12" against existing "|12}}"
        // replaces the 12 in place instead of producing "|12|12}}". CRITICAL: also stop on
        // newline — without this, an unclosed template like `{{Item/Group|Name|` with caret at
        // the trailing `|` would scan across following lines, eating headings + merging unrelated
        // wikitext into the template invocation.
        if ((ctx.Kind == ContextKind.NamedValueArg || ctx.Kind == ContextKind.LevelArg)
            && safeEnd < textFull.Length)
        {
            int scan = safeEnd;
            while (scan < textFull.Length
                   && textFull[scan] != '|'
                   && textFull[scan] != '\n' && textFull[scan] != '\r'
                   && !(textFull[scan] == '}' && scan + 1 < textFull.Length && textFull[scan + 1] == '}'))
                scan++;
            safeEnd = scan;
        }

        // Defensive: avoid producing duplicate "=", "|", or "}" at the splice point.
        while (insert.Length > 0 && safeEnd < textFull.Length
               && insert[insert.Length - 1] == textFull[safeEnd]
               && (insert[insert.Length - 1] == '=' || insert[insert.Length - 1] == '|' || insert[insert.Length - 1] == '}'))
        {
            safeEnd++;
        }

        _suppressTextEvent = true;
        try
        {
            _textBox.Text = textFull.Substring(0, safeStart) + insert + textFull.Substring(safeEnd);
            _textBox.CaretIndex = safeStart + insert.Length;
        }
        finally { _suppressTextEvent = false; }

        _popup.IsOpen = false;
        UpdateSuggestions();
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static Brush TryBrush(string resourceKey, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources[resourceKey] is Brush b) return b;
        }
        catch { /* designer */ }
        return new SolidColorBrush(fallback);
    }

    /// <summary>
    /// Style that strips the wpf-ui default ListBoxItem padding (~11,5,11,6). Fixed Height so
    /// rows with or without images match exactly. Row 46px = 42×42 thumbnail + 2px top + 2px bottom.
    /// </summary>
    private static Style BuildItemContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 46.0));
        return style;
    }

    /// <summary>
    /// Builds the DataTemplate for ListBox items: small (32×32) thumbnail on the left + text on the right.
    /// Image is set via a DecodePixelWidth=24 BitmapImage so memory stays bounded; rendering is lazy
    /// thanks to ListBox virtualization (only visible items materialize their templates).
    /// </summary>
    private static DataTemplate BuildItemTemplate(Brush textBrush)
    {
        var template = new DataTemplate(typeof(SuggestionItem));
        var rootFactory = new FrameworkElementFactory(typeof(Grid));
        // 2-column grid: fixed thumbnail width + flex text. Fixed width keeps text alignment
        // identical for rows with and without thumbnails — no jagged left edges.
        var colThumb = new FrameworkElementFactory(typeof(ColumnDefinition));
        colThumb.SetValue(ColumnDefinition.WidthProperty, new GridLength(46));
        var colText = new FrameworkElementFactory(typeof(ColumnDefinition));
        colText.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        rootFactory.AppendChild(colThumb);
        rootFactory.AppendChild(colText);

        // Thumbnail: 42×42 inside a 46px column (Border centered) → 2px breathing margin per side.
        // Stretch=Uniform on ImageBrush preserves 1:1 aspect (letterbox). When ImagePath is
        // missing OR brush build fails, the Border stays present but transparent — keeps row
        // height consistent.
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(FrameworkElement.WidthProperty, 42.0);
        borderFactory.SetValue(FrameworkElement.HeightProperty, 42.0);
        borderFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        borderFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.SetValue(UIElement.ClipToBoundsProperty, true);
        borderFactory.SetValue(Grid.ColumnProperty, 0);
        // Bind Border.Background to the WHOLE SuggestionItem (not just ImagePath) so the
        // converter can also see AtlasRect (= sub-rectangle for per-level sprite extraction).
        borderFactory.SetBinding(Border.BackgroundProperty,
            new Binding() { Converter = new SuggestionToImageBrushConverter() });
        rootFactory.AppendChild(borderFactory);

        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(SuggestionItem.Text)));
        textFactory.SetValue(TextBlock.ForegroundProperty, textBrush);
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
        textFactory.SetValue(Grid.ColumnProperty, 1);
        rootFactory.AppendChild(textFactory);

        template.VisualTree = rootFactory;
        return template;
    }

    private class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, System.Globalization.CultureInfo c) =>
            (value is string s && !string.IsNullOrEmpty(s) && File.Exists(s)) ? Visibility.Visible : Visibility.Collapsed;
        public object? ConvertBack(object? v, Type t, object? p, System.Globalization.CultureInfo c) => null;
    }

    /// <summary>Builds an ImageBrush. AtlasRect (bottom-left Y) gets Y-flipped using bitmap
    /// PixelHeight at render time, then CroppedBitmap. FractionalRect (0..1) is scaled to
    /// absolute coords using PixelWidth/Height, then CroppedBitmap. No extra zoom — the rect
    /// IS the crop. Without a rect, full image fits to the box.</summary>
    private class SuggestionToImageBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not SuggestionItem si || string.IsNullOrEmpty(si.ImagePath)) return null;

            // All crop/rotate variants share SpriteImageBrushBuilder (single source of truth).
            // FractionalRect = area center-square crop; otherwise atlas-rect / full-image sprite.
            return si.FractionalRect.HasValue
                ? SpriteImageBrushBuilder.BuildFractional(si.ImagePath, si.FractionalRect.Value)
                : SpriteImageBrushBuilder.Build(si.ImagePath, si.AtlasRect, si.IsRotated);
        }
        public object? ConvertBack(object? v, Type t, object? p, System.Globalization.CultureInfo c) => null;
    }

    private enum ContextKind { TemplateName, Argument, LevelArg, NamedArg, NamedValueArg }

    private record AutoContext(
        ContextKind Kind,
        string Prefix,
        int PrefixStart,
        int PrefixEnd,
        string? Template,
        string? ChainName = null,
        string? ParamName = null,
        IReadOnlyDictionary<string, string>? UsedParamValues = null);

    /// <summary>One row in the suggestion popup. Text is what gets inserted on accept;
    /// ImagePath is shown as a thumbnail next to the text (null = text-only row).
    /// AtlasRect = raw atlas coords (bottom-left Y); converter flips at render time.
    /// FractionalRect = 0..1 coords for center crops on non-atlas images.
    /// IsRotated = sprite is stored 90° rotated in atlas; converter un-rotates on display.</summary>
    public record SuggestionItem(
        string Text,
        string? ImagePath,
        Rect? AtlasRect = null,
        Rect? FractionalRect = null,
        bool IsRotated = false);
}
