using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfBorder = System.Windows.Controls.Border;
using WpfStackPanel = System.Windows.Controls.StackPanel;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Modal dialog shown when an event re-airs ambiguously: the user decides whether the new
/// airing is a separate run (Variant A) or an update of the existing run (Variant B).
/// Each option is a clickable card showing a green/red diff preview built by <see cref="EventDriftPreview"/>.
/// </summary>
public partial class EventDriftDialog : Wpf.Ui.Controls.FluentWindow
{
    // Same diff brushes as MysteryDiffDialog — reuse the same visual language.
    private static readonly Brush BrushAddedBg   = new SolidColorBrush(Color.FromArgb(0x25, 0x30, 0xC0, 0x30));
    private static readonly Brush BrushRemovedBg = new SolidColorBrush(Color.FromArgb(0x25, 0xD0, 0x40, 0x40));
    private static readonly Brush BrushAddedFg   = new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0x40));
    private static readonly Brush BrushRemovedFg = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));

    // Accent brushes for the selected-card state (resolved at construction time after theme is applied).
    private Brush? _accentBorder;
    private Brush? _accentCardBg;
    private Brush? _neutralBorder;
    private Brush? _hoverBg;       // subtle fill shown on hover over a non-selected card
    private Brush? _defaultCardBg; // resting card background

    /// <summary>
    /// <c>true</c> when the user cancelled (clicked Cancel or closed via X).
    /// When <c>true</c>, <see cref="IsUpdate"/> is meaningless.
    /// </summary>
    public bool Cancelled { get; private set; } = true; // default: treat X-close as cancel

    /// <summary>
    /// <c>false</c> = user chose Variant A (separate run);
    /// <c>true</c>  = user chose Variant B (update existing run).
    /// Only meaningful when <see cref="Cancelled"/> is <c>false</c>.
    /// </summary>
    public bool IsUpdate { get; private set; }

    // Tracks which card (if any) is currently selected: null=none, false=A, true=B
    private bool? _selected;

    public EventDriftDialog(EventDriftDecision decision)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        // Resolve accent brushes from theme resources.
        _accentBorder  = TryFindResource("AccentFillColorDefaultBrush") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        // Selected-card fill: NEUTRAL subtle tint (the accent lives only on the border + checkmark);
        // a strong accent fill washed out the green/red diff text underneath.
        _accentCardBg  = TryFindResource("SubtleFillColorSecondaryBrush") as Brush
                         ?? new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));
        _neutralBorder = TryFindResource("ControlElevationBorderBrush") as Brush
                         ?? new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));
        _defaultCardBg = TryFindResource("CardBackgroundFillColorDefaultBrush") as Brush;
        _hoverBg       = TryFindResource("ControlFillColorSecondaryBrush") as Brush
                         ?? new SolidColorBrush(Color.FromArgb(0x0E, 0x80, 0x80, 0x80));

        // Header
        txtEventName.Text = decision.EventName;

        int daysAfter = (int)Math.Round((decision.NewStart - decision.ExistingStart).TotalDays);
        int dur        = (int)Math.Round(decision.ExistingDurationDays);
        txtSummary.Text =
            $"New start {decision.NewStart:dd.MM.yyyy} is {daysAfter} days after " +
            $"the {decision.ExistingStart:dd.MM.yyyy} run (beyond its {dur}-day duration).";

        // Build diff panels
        var (previewA, previewB) = EventDriftPreview.BuildPreview(decision);
        PopulatePanel(pnlA, previewA);
        PopulatePanel(pnlB, previewB);
    }

    // ── Card selection ──────────────────────────────────────────────

    private void CardA_Click(object sender, MouseButtonEventArgs e) => SelectCard(false);
    private void CardB_Click(object sender, MouseButtonEventArgs e) => SelectCard(true);

    private void SelectCard(bool isUpdate)
    {
        _selected = isUpdate;

        // Apply selected state to the chosen card, neutral to the other.
        ApplyCardState(cardA, iconA, selected: !isUpdate);
        ApplyCardState(cardB, iconB, selected: isUpdate);

        btnConfirm.IsEnabled = true;
    }

    private void ApplyCardState(System.Windows.Controls.Border card, SymbolIcon icon, bool selected)
    {
        if (selected)
        {
            card.BorderBrush  = _accentBorder;
            card.Background   = _accentCardBg;
            icon.Symbol       = SymbolRegular.CheckmarkCircle20;
            icon.Foreground   = _accentBorder;
        }
        else
        {
            card.BorderBrush  = _neutralBorder;
            card.Background   = _defaultCardBg;
            icon.Symbol       = SymbolRegular.RadioButton20;
            icon.SetResourceReference(SymbolIcon.ForegroundProperty, "TextFillColorTertiaryBrush");
        }
    }

    // ── Hover (only on the NOT-currently-selected card) ─────────────
    private void CardA_Enter(object sender, MouseEventArgs e) { if (_selected != false) cardA.Background = _hoverBg; }
    private void CardA_Leave(object sender, MouseEventArgs e) { if (_selected != false) cardA.Background = _defaultCardBg; }
    private void CardB_Enter(object sender, MouseEventArgs e) { if (_selected != true)  cardB.Background = _hoverBg; }
    private void CardB_Leave(object sender, MouseEventArgs e) { if (_selected != true)  cardB.Background = _defaultCardBg; }

    // ── Button handlers ─────────────────────────────────────────────

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return; // shouldn't happen — button is disabled until a card is picked

        Cancelled    = false;
        IsUpdate     = _selected.Value;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Cancelled = true;
        Close();
    }

    /// <summary>
    /// Handles the window X button (Closing event). Treats it as Cancel/abort
    /// so <see cref="Cancelled"/> is always <c>true</c> when the dialog is dismissed without Confirm.
    /// </summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // If DialogResult was already set (by BtnConfirm_Click), Cancelled was set to false there.
        // Any other close path (X, Cancel button) leaves Cancelled = true (the default).
    }

    // ── Diff panel builder ──────────────────────────────────────────

    /// <summary>
    /// Splits a preview string on newlines and adds one styled Border per line to
    /// <paramref name="panel"/>. Lines starting with "+ " are green, "- " red, else default.
    /// Text wraps so long lines are never clipped horizontally.
    /// </summary>
    private static void PopulatePanel(WpfStackPanel panel, string preview)
    {
        foreach (var rawLine in preview.Split('\n'))
        {
            string line = rawLine;

            var border = new System.Windows.Controls.Border
            {
                Padding      = new Thickness(4, 1, 4, 1),
                Margin       = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(2)
            };

            var tb = new WpfTextBlock
            {
                Text         = string.IsNullOrEmpty(line) ? " " : line,
                FontFamily   = new FontFamily("Consolas"),
                FontSize     = 12,
                TextWrapping = TextWrapping.Wrap   // Fix: wrap instead of clip
            };

            if (line.StartsWith("+ ", StringComparison.Ordinal))
            {
                border.Background = BrushAddedBg;
                tb.Foreground     = BrushAddedFg;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                border.Background = BrushRemovedBg;
                tb.Foreground     = BrushRemovedFg;
            }
            else
            {
                tb.SetResourceReference(WpfTextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            }

            border.Child = tb;
            panel.Children.Add(border);
        }
    }
}
