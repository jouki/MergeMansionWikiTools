using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public partial class VariantComparisonDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ParsedChain _chain;
    private readonly string _basePath;
    private readonly List<VariantInfo> _variants;
    private VariantComparisonResult? _result;

    public string DialogTitle => $"Compare AB Test Variants — {_chain.DisplayName}";

    public VariantComparisonDialog(ParsedChain chain, string basePath, List<VariantInfo> variants)
    {
        _chain = chain;
        _basePath = basePath;
        _variants = variants;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        DataContext = this;

        txtChainInfo.Text = chain.DisplayName;
        txtChainDetail.Text = $"{chain.ConfigKey} · {chain.Items.Count} levels";
        txtVariants.Text = "Variants: " + string.Join(", ", variants.Select(v => v.Name));

        _ = LoadComparisonAsync();
    }

    private async Task LoadComparisonAsync()
    {
        try
        {
            _result = await VariantComparisonService.CompareChainAsync(
                _chain.ConfigKey, _chain.DisplayName, _basePath, _variants);

            pnlLoading.Visibility = Visibility.Collapsed;

            if (!_result.HasAnyDiffs)
            {
                pnlNoDiffs.Visibility = Visibility.Visible;
                return;
            }

            BuildResultsUI(_result);
            scrollResults.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            pnlError.Visibility = Visibility.Visible;
            txtError.Text = $"Error: {ex.Message}";
        }
    }

    // ── Column merging ──

    /// <summary>
    /// Groups variants whose diff values are identical for every field in a given level.
    /// Returns display columns: each has a label + list of VariantInfos it represents.
    /// </summary>
    private static List<(string Label, List<VariantInfo> Sources)> MergeIdenticalColumns(
        ItemVariantDiff itemDiff, List<VariantInfo> variants)
    {
        // Build a signature per variant: concatenation of all field values
        var signatures = new Dictionary<string, List<VariantInfo>>(StringComparer.Ordinal);
        var sigOrder = new List<string>();

        foreach (var v in variants)
        {
            var sb = new StringBuilder();
            if (itemDiff.DiffsByVariant.TryGetValue(v.Name, out var diffs))
            {
                foreach (var fd in diffs.OrderBy(f => f.FieldName, StringComparer.Ordinal))
                    sb.Append(fd.FieldName).Append('=').Append(fd.VariantValue).Append('|');
            }
            else
            {
                sb.Append("(same as main)");
            }

            var sig = sb.ToString();
            if (!signatures.TryGetValue(sig, out var list))
            {
                list = new List<VariantInfo>();
                signatures[sig] = list;
                sigOrder.Add(sig);
            }
            list.Add(v);
        }

        var result = new List<(string, List<VariantInfo>)>();
        foreach (var sig in sigOrder)
        {
            var group = signatures[sig];
            var label = group.Count == 1
                ? group[0].Name
                : string.Join(", ", group.Select(g => g.Name));
            result.Add((label, group));
        }

        return result;
    }

    // ── UI building ──

    private void BuildResultsUI(VariantComparisonResult result)
    {
        pnlResults.Children.Clear();

        foreach (var itemDiff in result.Diffs)
        {
            // Level header with copy icon
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 6)
            };

            var headerText = $"Level {itemDiff.Level}";
            if (!string.IsNullOrEmpty(itemDiff.ItemName))
                headerText += $" — {itemDiff.ItemName}";

            var header = new TextBlock
            {
                Text = headerText,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            headerPanel.Children.Add(header);

            // Copy item name icon
            if (!string.IsNullOrEmpty(itemDiff.ItemName))
            {
                var copyIcon = new SymbolIcon
                {
                    Symbol = SymbolRegular.Copy24,
                    FontSize = 14,
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"Copy \"{itemDiff.ItemName}\"",
                    Tag = itemDiff.ItemName,
                    Opacity = 0.5
                };
                copyIcon.SetResourceReference(SymbolIcon.ForegroundProperty, "TextFillColorTertiaryBrush");
                ToolTipService.SetInitialShowDelay(copyIcon, 0);
                copyIcon.MouseEnter += (_, _) => copyIcon.Opacity = 1.0;
                copyIcon.MouseLeave += (_, _) => copyIcon.Opacity = 0.5;
                copyIcon.MouseLeftButtonDown += (s, _) =>
                {
                    if (s is SymbolIcon si && si.Tag is string name)
                        App.NativeSetClipboardText(name);
                };
                headerPanel.Children.Add(copyIcon);
            }

            pnlResults.Children.Add(headerPanel);

            // Build table with merged columns
            var mergedColumns = MergeIdenticalColumns(itemDiff, result.Variants);
            var grid = BuildDiffTable(itemDiff, mergedColumns, itemDiff.ItemType);
            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                Child = grid
            };
            border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            pnlResults.Children.Add(border);
        }
    }

    private Grid BuildDiffTable(ItemVariantDiff itemDiff,
        List<(string Label, List<VariantInfo> Sources)> columns, string itemType)
    {
        var grid = new Grid();

        // Columns: Field | Main Group | MergedCol1 | MergedCol2 | ...
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var _ in columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Header row
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHeaderCell(grid, 0, 0, "Field");
        AddHeaderCell(grid, 0, 1, "Main Group");
        for (int i = 0; i < columns.Count; i++)
            AddVariantHeaderCell(grid, 0, 2 + i, columns[i].Label, columns[i].Sources, itemType);

        // Collect all unique field names
        var allFields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, fieldDiffs) in itemDiff.DiffsByVariant)
            foreach (var fd in fieldDiffs)
                if (seen.Add(fd.FieldName))
                    allFields.Add(fd.FieldName);

        // Data rows
        int row = 1;
        foreach (var fieldName in allFields)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Field name cell
            AddCell(grid, row, 0, fieldName, null, null, true);

            // Main Group value
            string baseValue = "—";
            foreach (var (_, fieldDiffs) in itemDiff.DiffsByVariant)
            {
                var fd = fieldDiffs.FirstOrDefault(f => f.FieldName == fieldName);
                if (fd != null) { baseValue = fd.BaseValue; break; }
            }
            AddCell(grid, row, 1, baseValue, null, null, false);

            // Merged variant columns
            for (int ci = 0; ci < columns.Count; ci++)
            {
                // Use the first variant in the group (they're all identical)
                var repVariant = columns[ci].Sources[0];
                if (itemDiff.DiffsByVariant.TryGetValue(repVariant.Name, out var diffs))
                {
                    var fd = diffs.FirstOrDefault(f => f.FieldName == fieldName);
                    if (fd != null)
                    {
                        var arrow = fd.Direction switch
                        {
                            DiffDirection.Buff => " \u25B2",
                            DiffDirection.Nerf => " \u25BC",
                            _ => ""
                        };
                        AddCell(grid, row, 2 + ci, fd.VariantValue + arrow, fd.Direction, fd.DeltaText, false);
                    }
                    else
                    {
                        AddCell(grid, row, 2 + ci, baseValue, null, null, false);
                    }
                }
                else
                {
                    AddCell(grid, row, 2 + ci, baseValue, null, null, false);
                }
            }

            row++;
        }

        return grid;
    }

    // ── Cell helpers ──

    private static void AddHeaderCell(Grid grid, int row, int col, string text)
    {
        var border = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            BorderThickness = new Thickness(col > 0 ? 1 : 0, 0, 0, 1)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = text
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        border.Child = tb;

        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        grid.Children.Add(border);
    }

    private void AddVariantHeaderCell(Grid grid, int row, int col,
        string label, List<VariantInfo> sources, string itemType)
    {
        var border = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            BorderThickness = new Thickness(1, 0, 0, 1)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

        // Grid layout so TextBlock trims but icon stays visible
        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = string.Join("\n", sources.Select(s => s.Name))
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(tb, 0);
        innerGrid.Children.Add(tb);

        // Open JSON icon — opens the first variant's file, scrolled to the chain
        var icon = new SymbolIcon
        {
            Symbol = SymbolRegular.DocumentSearch24,
            FontSize = 14,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Open JSON at \"{itemType}\"",
            Tag = (Path: sources[0].FilePath, ItemType: itemType),
            Opacity = 0.6
        };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, "TextFillColorSecondaryBrush");
        ToolTipService.SetInitialShowDelay(icon, 0);
        icon.MouseEnter += (_, _) => icon.Opacity = 1.0;
        icon.MouseLeave += (_, _) => icon.Opacity = 0.6;
        icon.MouseLeftButtonDown += OpenVariantJson;
        Grid.SetColumn(icon, 1);
        innerGrid.Children.Add(icon);

        border.Child = innerGrid;

        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        grid.Children.Add(border);
    }

    private void OpenVariantJson(object sender, MouseButtonEventArgs e)
    {
        if (sender is not SymbolIcon si) return;
        var tag = ((string Path, string ItemType))si.Tag;
        var path = tag.Path;

        try
        {
            int line = FindItemTypeLine(path, tag.ItemType);

            if (line > 0)
            {
                // Try VS Code (UseShellExecute resolves code.cmd from PATH)
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "code",
                        Arguments = $"--goto \"{path}\":{line}",
                        UseShellExecute = true
                    });
                    return;
                }
                catch { /* VS Code not available */ }

                // Try Notepad++ (-n = goto line)
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "notepad++",
                        Arguments = $"-n{line} \"{path}\"",
                        UseShellExecute = true
                    });
                    return;
                }
                catch { /* Notepad++ not available */ }
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private static int FindItemTypeLine(string path, string itemType)
    {
        if (string.IsNullOrEmpty(itemType)) return 0;
        try
        {
            var pattern = $"\"ItemType\": \"{itemType}\"";
            int lineNum = 0;
            foreach (var line in File.ReadLines(path, new UTF8Encoding(true)))
            {
                lineNum++;
                if (line.Contains(pattern, StringComparison.Ordinal))
                    return lineNum;
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    private static readonly Brush BrushBuff = new SolidColorBrush(Color.FromRgb(0x2D, 0xB8, 0x4C));
    private static readonly Brush BrushNerf = new SolidColorBrush(Color.FromRgb(0xE8, 0x4C, 0x3D));
    private static readonly Brush BrushChanged = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x30));
    private static readonly Brush BrushDelta = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    private static void AddCell(Grid grid, int row, int col, string text,
        DiffDirection? direction, string? deltaText, bool isFieldName)
    {
        var border = new Border
        {
            Padding = new Thickness(10, 5, 10, 5),
            BorderThickness = new Thickness(col > 0 ? 1 : 0, 0, 0, 0)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        if (row % 2 == 0)
            border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorTertiaryBrush");

        if (isFieldName)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = text
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            border.Child = tb;
        }
        else if (direction.HasValue && !string.IsNullOrEmpty(deltaText))
        {
            // Value with delta in parentheses
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var valueTb = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = direction.Value switch
                {
                    DiffDirection.Buff => BrushBuff,
                    DiffDirection.Nerf => BrushNerf,
                    DiffDirection.Changed => BrushChanged,
                    _ => Brushes.Gray
                }
            };
            panel.Children.Add(valueTb);

            var deltaTb = new TextBlock
            {
                Text = $" ({deltaText})",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushDelta
            };
            panel.Children.Add(deltaTb);

            panel.ToolTip = $"{text} ({deltaText})";
            border.Child = panel;
        }
        else if (direction.HasValue)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = text,
                Foreground = direction.Value switch
                {
                    DiffDirection.Buff => BrushBuff,
                    DiffDirection.Nerf => BrushNerf,
                    DiffDirection.Changed => BrushChanged,
                    _ => Brushes.Gray
                }
            };
            border.Child = tb;
        }
        else
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = text
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            border.Child = tb;
        }

        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        grid.Children.Add(border);
    }



    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
