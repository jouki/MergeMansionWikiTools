using System.Windows;
using System.Windows.Controls;
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
