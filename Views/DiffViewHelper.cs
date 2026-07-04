using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Models;

using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

/// <summary>Side-by-side diff rendering (red removed / green added, Consolas rows) shared by
/// <see cref="MysteryDiffDialog"/> and <see cref="GcPageUpdateDialog"/> — one source of truth
/// for the diff look. Left panel = current wiki, right panel = generated content.</summary>
internal static class DiffViewHelper
{
    // Diff colors
    private static readonly Brush BrushAddedBg = new SolidColorBrush(Color.FromArgb(0x25, 0x30, 0xC0, 0x30));
    private static readonly Brush BrushRemovedBg = new SolidColorBrush(Color.FromArgb(0x25, 0xD0, 0x40, 0x40));
    private static readonly Brush BrushAddedFg = new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0x40));
    private static readonly Brush BrushRemovedFg = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));

    /// <summary>Fills the two panels with aligned diff rows (placeholders keep the sides in sync).</summary>
    public static void BuildDiffView(Panel left, Panel right, List<DiffLine> diffs)
    {
        left.Children.Clear();
        right.Children.Clear();

        foreach (var diff in diffs)
        {
            switch (diff.Type)
            {
                case DiffLineType.Match:
                    left.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Match));
                    right.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Match));
                    break;

                case DiffLineType.Removed:
                    // Only in wiki (left) — show red on left, empty placeholder on right
                    left.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Removed));
                    right.Children.Add(CreateDiffPlaceholder(DiffLineType.Removed));
                    break;

                case DiffLineType.Added:
                    // Only in generated (right) — empty placeholder on left, green on right
                    left.Children.Add(CreateDiffPlaceholder(DiffLineType.Added));
                    right.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Added));
                    break;

                case DiffLineType.Modified:
                    // Both sides differ — show old on left (red), new on right (green)
                    left.Children.Add(CreateDiffLine(diff.OldText ?? "", DiffLineType.Removed));
                    right.Children.Add(CreateDiffLine(diff.Text, DiffLineType.Added));
                    break;
            }
        }
    }

    public static Border CreateDiffLine(string text, DiffLineType type)
    {
        var border = new Border
        {
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(2)
        };

        var tb = new TextBlock
        {
            Text = string.IsNullOrEmpty(text) ? " " : text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap
        };

        switch (type)
        {
            case DiffLineType.Added:
                border.Background = BrushAddedBg;
                tb.Foreground = BrushAddedFg;
                break;
            case DiffLineType.Removed:
                border.Background = BrushRemovedBg;
                tb.Foreground = BrushRemovedFg;
                break;
            default:
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                break;
        }

        border.Child = tb;
        return border;
    }

    public static Border CreateDiffPlaceholder(DiffLineType type)
    {
        var border = new Border
        {
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(2),
            Opacity = 0.3
        };

        var tb = new TextBlock
        {
            Text = " ",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };

        // Faint background to show the line exists on the other side
        border.Background = type == DiffLineType.Added
            ? BrushAddedBg
            : BrushRemovedBg;

        border.Child = tb;
        return border;
    }
}
