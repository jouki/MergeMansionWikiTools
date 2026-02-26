using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using TinifyAPI;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public class FileInfoItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long OriginalSize { get; set; }

    private long _newSize;
    public long NewSize
    {
        get => _newSize;
        set
        {
            _newSize = value;
            OnPropertyChanged(nameof(NewSizeText));
            OnPropertyChanged(nameof(SavingsText));
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public bool IsOptimized { get; set; }

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    public string OriginalSizeText => $"{(OriginalSize / 1024.0):F1} KB";
    public string NewSizeText => NewSize > 0 ? $"{(NewSize / 1024.0):F1} KB" : "";
    public string SavingsText => NewSize > 0 ? $"-{100 - (NewSize * 100 / OriginalSize)}%" : "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

public partial class OptimizationWindow : FluentWindow
{
    private readonly string _apiKey;
    private readonly string _apiKey2;
    private bool _startedFired;
    public ObservableCollection<FileInfoItem> Files { get; set; } = new();
    public event Action? OptimizationStarted;
    public bool AllOptimized => Files.All(f => !f.IsSelected || f.IsOptimized);

    public OptimizationWindow(List<string> filePaths, string apiKey, string apiKey2)
    {
        _apiKey = apiKey;
        _apiKey2 = apiKey2;
        InitializeComponent();

        var sorted = filePaths.OrderBy(p =>
        {
            var m = Regex.Match(System.IO.Path.GetFileNameWithoutExtension(p), @"\d+$");
            return m.Success ? int.Parse(m.Value) : 999;
        }).ToList();

        foreach (var p in sorted)
        {
            var fi = new FileInfo(p);
            byte[] bytes = File.ReadAllBytes(p);
            var thumb = LoadBitmapNoLock(bytes);

            Files.Add(new FileInfoItem
            {
                Name = fi.Name,
                Path = p,
                OriginalSize = fi.Length,
                IsSelected = true,
                Thumbnail = thumb
            });
        }

        lvFiles.ItemsSource = Files;

        bool hasKey = !string.IsNullOrWhiteSpace(_apiKey);
        if (!hasKey)
        {
            statusInfo.Title = "No API Key";
            statusInfo.Message = "Set your TinyPNG API key in Settings to enable optimisation.";
            statusInfo.Severity = InfoBarSeverity.Warning;
            btnRun.IsEnabled = false;
        }
        else
        {
            var fallbackNote = !string.IsNullOrWhiteSpace(_apiKey2) ? " (fallback key configured)" : "";
            statusInfo.Title = "Ready";
            statusInfo.Message = $"Select files to optimise their filesize.{fallbackNote}";
            statusInfo.Severity = InfoBarSeverity.Informational;
        }

        statusInfo.IsOpen = true;
    }

    private static BitmapImage LoadBitmapNoLock(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private async Task<byte[]> OptimizeWithFallback(byte[] data)
    {
        try
        {
            Tinify.Key = _apiKey;
            return await Tinify.FromBuffer(data).ToBuffer();
        }
        catch (AccountException) when (!string.IsNullOrWhiteSpace(_apiKey2))
        {
            Tinify.Key = _apiKey2;
            return await Tinify.FromBuffer(data).ToBuffer();
        }
    }

    private void Grid_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is FileInfoItem item)
            item.IsSelected = !item.IsSelected;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private async void BtnOptimize_Click(object sender, RoutedEventArgs e)
    {
        var toProcess = Files.Where(f => f.IsSelected && !f.IsOptimized).ToList();
        if (!toProcess.Any()) return;

        if (!_startedFired)
        {
            _startedFired = true;
            OptimizationStarted?.Invoke();
        }

        btnRun.IsEnabled = false;
        progressBar.Visibility = Visibility.Visible;
        progressBar.Maximum = toProcess.Count;
        progressBar.Value = 0;

        int count = 0;

        foreach (var item in toProcess)
        {
            try
            {
                statusInfo.Message = $"Processing: {item.Name}...";

                byte[] sourceData = await File.ReadAllBytesAsync(item.Path);
                byte[] optimizedData = await OptimizeWithFallback(sourceData);
                await File.WriteAllBytesAsync(item.Path, optimizedData);

                item.NewSize = new FileInfo(item.Path).Length;
                item.Thumbnail = LoadBitmapNoLock(optimizedData);
                item.IsOptimized = true;

                count++;
                progressBar.Value = count;
            }
            catch (Exception ex)
            {
                statusInfo.Title = "Error";
                statusInfo.Message = $"Error at {item.Name}: {ex.Message}";
                statusInfo.Severity = InfoBarSeverity.Error;
            }
        }

        progressBar.Visibility = Visibility.Collapsed;
        btnRun.IsEnabled = true;

        statusInfo.Title = "Optimisation completed";
        statusInfo.Message = $"All {count} files were successfully optimised.";
        statusInfo.Severity = InfoBarSeverity.Success;
        statusInfo.IsOpen = true;
    }
}
