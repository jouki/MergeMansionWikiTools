using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
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
    private bool _wasSkipped;
    public bool WasSkipped
    {
        get => _wasSkipped;
        set { _wasSkipped = value; OnPropertyChanged(nameof(SavingsText)); }
    }

    public string SavingsText => WasSkipped ? "OK" : NewSize > 0 ? $"-{100 - (NewSize * 100 / OriginalSize)}%" : "";

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

                // 1) Check for our optimization marker — skip API call entirely
                if (HasOptMarker(sourceData))
                {
                    item.NewSize = item.OriginalSize;
                    item.WasSkipped = true;
                    item.IsOptimized = true;
                    count++;
                    progressBar.Value = count;
                    continue;
                }

                byte[] optimizedData = await OptimizeWithFallback(sourceData);

                // 2) Fallback: skip overwrite if reduction ≤10% (externally optimized)
                double reductionPct = 100.0 - ((double)optimizedData.Length * 100 / sourceData.Length);
                if (reductionPct <= 10)
                {
                    item.NewSize = item.OriginalSize;
                    item.WasSkipped = true;
                    item.IsOptimized = true;
                }
                else
                {
                    // Insert marker (18 bytes) and write
                    byte[] markedData = InsertOptMarker(optimizedData);
                    await File.WriteAllBytesAsync(item.Path, markedData);
                    item.NewSize = markedData.Length;
                    item.Thumbnail = LoadBitmapNoLock(markedData);
                    item.IsOptimized = true;
                }

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

        int skippedCount = toProcess.Count(f => f.WasSkipped);
        statusInfo.Title = "Optimisation completed";
        statusInfo.Message = skippedCount > 0
            ? $"Optimised {count - skippedCount} files, {skippedCount} already compressed."
            : $"All {count} files were successfully optimised.";
        statusInfo.Severity = InfoBarSeverity.Success;
        statusInfo.IsOpen = true;
    }

    // ══════════════════════════════════════════════════════════════
    //  PNG tEXt CHUNK — optimization marker (18 bytes overhead)
    // ══════════════════════════════════════════════════════════════

    private const string OptMarkerKey = "mmwt"; // MergeMansionWikiTools

    private static readonly uint[] _crcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint PngCrc(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
            c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    /// <summary>Checks if a PNG file contains our optimization marker tEXt chunk.</summary>
    internal static bool HasOptMarker(ReadOnlySpan<byte> png)
    {
        if (png.Length < 20) return false; // 8 sig + 12 min chunk
        int pos = 8; // skip PNG signature
        while (pos + 12 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.Slice(pos));
            if (len < 0 || pos + 12 + len > png.Length) break;

            // tEXt = 0x74 0x45 0x58 0x74
            if (png[pos + 4] == 't' && png[pos + 5] == 'E' &&
                png[pos + 6] == 'X' && png[pos + 7] == 't' && len >= 5)
            {
                var data = png.Slice(pos + 8, len);
                int nullIdx = data.IndexOf((byte)0);
                if (nullIdx > 0)
                {
                    var keyword = Encoding.Latin1.GetString(data.Slice(0, nullIdx));
                    if (keyword == OptMarkerKey) return true;
                }
            }

            // IEND = end of file
            if (png[pos + 4] == 'I' && png[pos + 5] == 'E' &&
                png[pos + 6] == 'N' && png[pos + 7] == 'D')
                break;

            pos += 12 + len;
        }
        return false;
    }

    /// <summary>Inserts our optimization marker tEXt chunk before IEND. Adds exactly 18 bytes.</summary>
    internal static byte[] InsertOptMarker(byte[] png)
    {
        // Build tEXt chunk: keyword "mmwt" + null + "1"
        byte[] keyBytes = Encoding.Latin1.GetBytes(OptMarkerKey);
        int dataLen = keyBytes.Length + 1 + 1; // keyword + null + "1"
        var chunk = new byte[12 + dataLen]; // length(4) + type(4) + data + crc(4)

        BinaryPrimitives.WriteInt32BigEndian(chunk, dataLen);
        chunk[4] = (byte)'t'; chunk[5] = (byte)'E'; chunk[6] = (byte)'X'; chunk[7] = (byte)'t';
        keyBytes.CopyTo(chunk, 8);
        chunk[8 + keyBytes.Length] = 0; // null separator
        chunk[8 + keyBytes.Length + 1] = (byte)'1'; // value

        uint crc = PngCrc(chunk.AsSpan(4, 4 + dataLen));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + dataLen), crc);

        // Insert before IEND (last 12 bytes of valid PNG)
        int insertAt = png.Length - 12;
        var result = new byte[png.Length + chunk.Length];
        png.AsSpan(0, insertAt).CopyTo(result);
        chunk.CopyTo(result, insertAt);
        png.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));
        return result;
    }
}
