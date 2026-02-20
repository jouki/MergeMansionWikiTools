using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace MergeMansionWikiTools.Views;

public partial class SettingsPage : UserControl
{
    private readonly MainWindow _main;

    public SettingsPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        // Load saved paths
        txtChainPath.Text = _main.Settings.ChainItemOddsPath;
        txtAreasPath.Text = _main.Settings.AreasJsonPath;
        txtEventsPath.Text = _main.Settings.EventsJsonPath;
        txtTinifyKey.Text = _main.Settings.TinifyApiKey;
        txtTinifyKey2.Text = _main.Settings.TinifyApiKey2;
        txtImageBasePath.Text = _main.Settings.ImageExporterBasePath;

        // Build chunk size rows
        BuildChunkRows();

        // Set theme ComboBox to match saved preference
        cmbTheme.SelectedIndex = _main.Settings.ThemePreference switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0 // System
        };
    }

    // ── Drag & Drop ──

    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ChainFileDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtChainPath.Text = path;
            SaveChainPath(path);
        }
    }

    private void AreasFileDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtAreasPath.Text = path;
            _main.Settings.AreasJsonPath = path;
            _main.SaveSettings();
        }
    }

    private void EventsFileDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtEventsPath.Text = path;
            _main.Settings.EventsJsonPath = path;
            _main.SaveSettings();
        }
    }

    private static string? GetDroppedFilePath(DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        return files?.Length > 0 ? files[0] : null;
    }

    // ── Browse buttons ──

    private void BrowseChainFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select chain_item_odds.json");
        if (path != null)
        {
            txtChainPath.Text = path;
            SaveChainPath(path);
        }
    }

    private void BrowseAreasFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select areas.json");
        if (path != null)
        {
            txtAreasPath.Text = path;
            _main.Settings.AreasJsonPath = path;
            _main.SaveSettings();
        }
    }

    private void BrowseEventsFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select events.json");
        if (path != null)
        {
            txtEventsPath.Text = path;
            _main.Settings.EventsJsonPath = path;
            _main.SaveSettings();
        }
    }

    // ── Load button ──

    private async void LoadChainFile_Click(object sender, RoutedEventArgs e)
    {
        var path = txtChainPath.Text;
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            await _main.LoadDataAsync(path);
        }
        else
        {
            _main.ShowStatus("File not found. Please select a valid JSON file.", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    // ── TinyPNG API Keys (auto-save on change) ──

    private void TinifyKey_TextChanged(object sender, TextChangedEventArgs e)
    {
        _main.Settings.TinifyApiKey = txtTinifyKey.Text.Trim();
        _main.SaveSettings();
    }

    private void TinifyKey2_TextChanged(object sender, TextChangedEventArgs e)
    {
        _main.Settings.TinifyApiKey2 = txtTinifyKey2.Text.Trim();
        _main.SaveSettings();
    }

    // ── Theme ──

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return; // skip during constructor init

        var pref = cmbTheme.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        };

        _main.Settings.ThemePreference = pref;
        _main.SaveSettings();
        App.ApplyTheme(pref);
    }

    // ── Hyperlink ──

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ── Section highlights (called from WikiDataParserPage links) ──

    public void HighlightChainSection() => HighlightBorder(chainSectionBorder);
    public void HighlightAreasSection() => HighlightBorder(areasSectionBorder);
    public void HighlightChunkSizes() => HighlightBorder(expertChunkHighlight);

    /// <summary>
    /// Animates a yellow highlight fade on a border overlay.
    /// The target border must be an inner overlay (no own background) inside a card —
    /// after animation we simply clear its Background so the parent card shows through.
    /// </summary>
    private static void HighlightBorder(Border border)
    {
        border.BringIntoView();

        var brush = new SolidColorBrush(Color.FromArgb(0, 255, 180, 0));
        border.Background = brush;

        var anim = new ColorAnimation
        {
            From = Color.FromArgb(70, 255, 180, 0),
            To = Color.FromArgb(0, 255, 180, 0),
            Duration = new Duration(TimeSpan.FromSeconds(2)),
            AutoReverse = false,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        // Clear the overlay background — parent card's DynamicResource background is untouched
        anim.Completed += (_, _) => border.ClearValue(Border.BackgroundProperty);

        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    // ── Image Extractor — base path ──

    private void BrowseImageBasePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select PNG source base path" };
        if (!string.IsNullOrEmpty(_main.Settings.ImageExporterBasePath) &&
            System.IO.Directory.Exists(_main.Settings.ImageExporterBasePath))
            dlg.InitialDirectory = _main.Settings.ImageExporterBasePath;

        if (dlg.ShowDialog() == true)
        {
            txtImageBasePath.Text = dlg.FolderName;
            _main.Settings.ImageExporterBasePath = dlg.FolderName;
            _main.SaveSettings();
        }
    }

    private void ClearImageBasePath_Click(object sender, RoutedEventArgs e)
    {
        txtImageBasePath.Text = "";
        _main.Settings.ImageExporterBasePath = "";
        _main.SaveSettings();
    }

    // ── Expert — chunk sizes ──

    private void BuildChunkRows()
    {
        chunksPanel.Children.Clear();
        var sizes = _main.Settings.AreaChunkSizes;
        if (sizes == null || sizes.Count == 0) sizes = new List<int> { 40 };

        foreach (var size in sizes)
            AddChunkRow(size);

        UpdateRemoveButtons();
    }

    private void AddChunkRow(int value = 40)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = new Wpf.Ui.Controls.TextBox
        {
            Text = value.ToString(),
            PlaceholderText = "40",
            Height = 34
        };
        tb.TextChanged += (_, _) => SaveChunkSizesAuto();
        Grid.SetColumn(tb, 0);

        var removeBtn = new Wpf.Ui.Controls.Button
        {
            Content = "×",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Height = 34,
            Width = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        removeBtn.Click += (_, _) =>
        {
            chunksPanel.Children.Remove(row);
            UpdateRemoveButtons();
            SaveChunkSizesAuto();
        };
        Grid.SetColumn(removeBtn, 1);

        row.Children.Add(tb);
        row.Children.Add(removeBtn);
        chunksPanel.Children.Add(row);
    }

    private void BtnAddChunk_Click(object sender, RoutedEventArgs e)
    {
        AddChunkRow();
        UpdateRemoveButtons();
        SaveChunkSizesAuto();
    }

    private void UpdateRemoveButtons()
    {
        for (int i = 0; i < chunksPanel.Children.Count; i++)
        {
            if (chunksPanel.Children[i] is Grid row)
            {
                var btn = row.Children.OfType<Wpf.Ui.Controls.Button>().FirstOrDefault();
                if (btn != null)
                    btn.Visibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void SaveChunkSizesAuto()
    {
        var sizes = new List<int>();
        foreach (Grid row in chunksPanel.Children)
        {
            var tb = row.Children.OfType<Wpf.Ui.Controls.TextBox>().FirstOrDefault();
            if (tb != null && int.TryParse(tb.Text.Trim(), out var n) && n > 0)
                sizes.Add(n);
        }

        if (sizes.Count == 0) sizes.Add(40);

        _main.Settings.AreaChunkSizes = sizes;
        _main.SaveSettings();
    }

    // ── Helpers ──

    private void SaveChainPath(string path)
    {
        _main.Settings.ChainItemOddsPath = path;
        _main.SaveSettings();
    }

    private static string? BrowseJsonFile(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };

        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
