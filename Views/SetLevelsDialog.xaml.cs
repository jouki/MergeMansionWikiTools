using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using MergeMansionWikiTools.Models;
using Wpf.Ui.Appearance;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Bulk level editor for the Item Chains selection: one level box per selected item plus the
/// whole-selection shortcuts (set all, shift ±1, renumber consecutively). Returns the wanted level
/// per item in <see cref="Result"/>; it writes nothing itself — the caller turns the result into a
/// Module:Datatable/Items/Mapping diff, shows it, and publishes.
/// <para>
/// Levels are NOT required to be unique inside a chain: aliases and variants are parallel forms of
/// the same merge stage and legitimately share one (see <see cref="ParsedItem.IsVariant"/>), so this
/// dialog never blocks on a duplicate — it only points one out.
/// </para>
/// </summary>
public partial class SetLevelsDialog
{
    /// <summary>One editable row: the item, its current level and the level the user wants.</summary>
    public sealed class LevelRow : INotifyPropertyChanged
    {
        private int _level;

        public LevelRow(ParsedItem source)
        {
            Source = source;
            OriginalLevel = source.Level;
            _level = source.Level;
        }

        public ParsedItem Source { get; }
        public int OriginalLevel { get; }
        public string Name => Source.Name;
        public string ItemType => Source.ItemType;

        public int Level
        {
            get => _level;
            set
            {
                var clamped = Math.Clamp(value, 1, 99);
                if (clamped == _level) return;
                _level = clamped;
                Raise(nameof(Level));
                Raise(nameof(LevelValue));
                Raise(nameof(ChangeLabel));
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// What the row's NumberBox binds to. It exists because <c>NumberBox.Value</c> is a
        /// <c>double?</c>: binding it straight to <see cref="Level"/> makes an emptied box push null
        /// into an int, which the binding drops on the floor — leaving the box blank while the row
        /// silently keeps its old value. Here a null (mid-typing, backspaced to empty) is ignored
        /// and the last good level stays.
        /// </summary>
        public double? LevelValue
        {
            get => _level;
            set { if (value.HasValue) Level = (int)Math.Round(value.Value); }
        }

        /// <summary>Empty when untouched, "1 → 2" when the user moved it — the per-row change hint.</summary>
        public string ChangeLabel => Level == OriginalLevel ? "" : $"{OriginalLevel} → {Level}";

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raised on every level edit so the dialog can refresh its summary line.</summary>
        public event Action? Changed;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<LevelRow> _rows = new();

    /// <summary>Items whose level the user actually changed, with the new value. Empty = nothing to do.</summary>
    public IReadOnlyList<(ParsedItem Item, int Level)> Result { get; private set; } =
        Array.Empty<(ParsedItem, int)>();

    public SetLevelsDialog(string chainName, IReadOnlyList<ParsedItem> items)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        // Shown in the chain's own order, which is what "Renumber from" walks — renumbering a list
        // the user sees in a different order than it is applied would be a trap.
        foreach (var item in items.OrderBy(i => i.Level).ThenBy(i => i.ItemType, StringComparer.Ordinal))
        {
            var row = new LevelRow(item);
            row.Changed += UpdateSummary;
            _rows.Add(row);
        }

        txtHeader.Text = items.Count == 1
            ? $"Set level for 1 item from \"{chainName}\""
            : $"Set levels for {items.Count} items from \"{chainName}\"";
        txtSubHeader.Text = "Writes the level override in Module:Datatable/Items/Mapping. "
            + "Setting an item back to the level the game data gives removes the override instead.";

        itemsList.ItemsSource = _rows;
        nbSetAll.Value = _rows.Count > 0 ? _rows[0].Level : 1;
        nbRenumber.Value = _rows.Count > 0 ? _rows[0].Level : 1;
        UpdateSummary();
    }

    // ── Quick actions ─────────────────────────────────────────────────

    private void BtnSetAll_Click(object sender, RoutedEventArgs e)
    {
        var target = Read(nbSetAll, 1);
        foreach (var row in _rows) row.Level = target;
        UpdateSummary();
    }

    private void BtnShiftUp_Click(object sender, RoutedEventArgs e) => Shift(+1);

    private void BtnShiftDown_Click(object sender, RoutedEventArgs e) => Shift(-1);

    /// <summary>
    /// Moves the whole selection by <paramref name="delta"/>, preserving the gaps between items —
    /// the "a new stage was inserted below" case. Refuses to run at all when it would push a row
    /// below level 1, rather than clamping and silently collapsing two rows onto the same level.
    /// </summary>
    private void Shift(int delta)
    {
        if (_rows.Any(r => r.Level + delta < 1 || r.Level + delta > 99))
        {
            txtSummary.Text = delta < 0
                ? "Cannot shift down — an item would end up below level 1."
                : "Cannot shift up — an item would end up above level 99.";
            return;
        }
        // Walk in the direction of travel so intermediate values never fight each other.
        foreach (var row in delta > 0 ? _rows.OrderByDescending(r => r.Level) : _rows.OrderBy(r => r.Level))
            row.Level += delta;
        UpdateSummary();
    }

    private void BtnRenumber_Click(object sender, RoutedEventArgs e)
    {
        var next = Read(nbRenumber, 1);
        foreach (var row in _rows)
        {
            row.Level = Math.Min(next, 99);
            next++;
        }
        UpdateSummary();
    }

    private static int Read(Wpf.Ui.Controls.NumberBox box, int fallback)
    {
        // Text is the live value; Value only commits on focus loss, so a typed number that was never
        // blurred would otherwise be ignored.
        if (int.TryParse(box.Text, out var typed) && typed is >= 1 and <= 99) return typed;
        return box.Value.HasValue ? Math.Clamp((int)box.Value.Value, 1, 99) : fallback;
    }

    // ── Summary + result ──────────────────────────────────────────────

    private void UpdateSummary()
    {
        var changed = _rows.Count(r => r.Level != r.OriginalLevel);
        var dupes = _rows.GroupBy(r => r.Level).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        var text = changed == 0
            ? "No level changes."
            : $"{changed} mapping {(changed == 1 ? "entry" : "entries")} will change.";

        // Not an error: aliases and variants share a level by design. Worth pointing out, because on
        // plain items it usually means a typo.
        if (dupes.Count > 0)
            text += $" Level {string.Join(", ", dupes.OrderBy(d => d))} "
                + $"{(dupes.Count == 1 ? "is" : "are")} used by more than one selected item "
                + "(fine for aliases/variants).";

        txtSummary.Text = text;
        btnApply.IsEnabled = changed > 0;
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        Result = _rows.Where(r => r.Level != r.OriginalLevel)
            .Select(r => (r.Source, r.Level))
            .ToList();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
