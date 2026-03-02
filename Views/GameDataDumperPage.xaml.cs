using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MergeMansionWikiTools.Services;
using Microsoft.Win32;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public partial class GameDataDumperPage : UserControl
{
    private readonly MainWindow _main;
    private bool _isDumping;
    private bool _isExtracting;

    public GameDataDumperPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        // Load saved paths (clear if file no longer exists)
        if (!string.IsNullOrEmpty(_main.Settings.DumperConfigPath))
        {
            if (File.Exists(_main.Settings.DumperConfigPath))
                SetPathText(txtConfigPath, _main.Settings.DumperConfigPath);
            else
                _main.Settings.DumperConfigPath = "";
        }
        if (!string.IsNullOrEmpty(_main.Settings.DumperPatchPath))
        {
            if (File.Exists(_main.Settings.DumperPatchPath))
                SetPathText(txtPatchPath, _main.Settings.DumperPatchPath);
            else
                _main.Settings.DumperPatchPath = "";
        }
        if (!string.IsNullOrEmpty(_main.Settings.DumperLanguagePath))
        {
            if (File.Exists(_main.Settings.DumperLanguagePath))
                SetPathText(txtLanguagePath, _main.Settings.DumperLanguagePath);
            else
                _main.Settings.DumperLanguagePath = "";
        }
        if (!string.IsNullOrEmpty(_main.Settings.DumperOutputPath))
            SetPathText(txtOutputPath, _main.Settings.DumperOutputPath);

        // Auto-detect from _DATA folder
        TryAutoDetect();
    }

    // ── Auto-detect ──────────────────────────────────────────────

    private void TryAutoDetect()
    {
        var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_DATA");
        if (!Directory.Exists(dataDir))
        {
            txtAutoDetect.Text = "";
            return;
        }

        bool anyDetected = false;

        // Config: _DATA/C/
        if (string.IsNullOrEmpty(GetPathText(txtConfigPath)))
        {
            var cDir = Path.Combine(dataDir, "C");
            if (Directory.Exists(cDir))
            {
                var files = Directory.GetFiles(cDir);
                if (files.Length == 1)
                {
                    SetPathText(txtConfigPath, files[0]);
                    SavePaths();
                    anyDetected = true;
                }
            }
        }

        // Patch: _DATA/P/
        if (string.IsNullOrEmpty(GetPathText(txtPatchPath)))
        {
            var pDir = Path.Combine(dataDir, "P");
            if (Directory.Exists(pDir))
            {
                var files = Directory.GetFiles(pDir);
                if (files.Length == 1)
                {
                    SetPathText(txtPatchPath, files[0]);
                    SavePaths();
                    anyDetected = true;
                }
            }
        }

        // Language: _DATA/L/
        if (string.IsNullOrEmpty(GetPathText(txtLanguagePath)))
        {
            var lDir = Path.Combine(dataDir, "L");
            if (Directory.Exists(lDir))
            {
                var files = Directory.GetFiles(lDir);
                if (files.Length == 1)
                {
                    SetPathText(txtLanguagePath, files[0]);
                    SavePaths();
                    anyDetected = true;
                }
            }
        }

        // Default output: _DATA/dump/
        if (string.IsNullOrEmpty(GetPathText(txtOutputPath)))
        {
            var dumpDir = Path.Combine(dataDir, "dump");
            SetPathText(txtOutputPath, dumpDir);
            SavePaths();
            anyDetected = true;
        }

        txtAutoDetect.Text = anyDetected ? "Some paths were auto-detected from _DATA/ folder." : "";
    }

    // ── Path helpers ──────────────────────────────────────────────

    private static readonly string _placeholderConfig = "Drop config archive file here or browse...";
    private static readonly string _placeholderPatch = "Drop patch config file here or browse...";
    private static readonly string _placeholderLang = "Drop language file here or browse...";
    private static readonly string _placeholderOutput = "Drop folder here or browse...";

    private static void SetPathText(TextBox tb, string path)
    {
        tb.Text = path;
        tb.Foreground = (System.Windows.Media.Brush)tb.FindResource("TextFillColorPrimaryBrush");
    }

    private static string GetPathText(TextBox tb)
    {
        var text = tb.Text;
        if (text == _placeholderConfig || text == _placeholderPatch || text == _placeholderLang || text == _placeholderOutput)
            return "";
        return text;
    }

    private void SavePaths()
    {
        _main.Settings.DumperConfigPath = GetPathText(txtConfigPath);
        _main.Settings.DumperPatchPath = GetPathText(txtPatchPath);
        _main.Settings.DumperLanguagePath = GetPathText(txtLanguagePath);
        _main.Settings.DumperOutputPath = GetPathText(txtOutputPath);
        _main.SaveSettings();
    }

    // ── Drag & Drop ──────────────────────────────────────────────

    private void Path_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ConfigPath_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            SetPathText(txtConfigPath, files[0]);
            SavePaths();
        }
    }

    private void PatchPath_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            SetPathText(txtPatchPath, files[0]);
            SavePaths();
        }
    }

    private void LanguagePath_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            SetPathText(txtLanguagePath, files[0]);
            SavePaths();
        }
    }

    private void OutputPath_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var path = files[0];
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            SetPathText(txtOutputPath, path);
            SavePaths();
        }
    }

    // ── Browse buttons ────────────────────────────────────────────

    private void BtnBrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select config archive", Filter = "All files|*.*" };
        if (dlg.ShowDialog() == true)
        {
            SetPathText(txtConfigPath, dlg.FileName);
            SavePaths();
        }
    }

    private void BtnBrowsePatch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select patch config", Filter = "All files|*.*" };
        if (dlg.ShowDialog() == true)
        {
            SetPathText(txtPatchPath, dlg.FileName);
            SavePaths();
        }
    }

    private void BtnBrowseLanguage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select language file", Filter = "All files|*.*" };
        if (dlg.ShowDialog() == true)
        {
            SetPathText(txtLanguagePath, dlg.FileName);
            SavePaths();
        }
    }

    private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select output directory" };
        if (dlg.ShowDialog() == true)
        {
            SetPathText(txtOutputPath, dlg.FolderName);
            SavePaths();
        }
    }

    // ── Pull from Phone ──────────────────────────────────────────

    private async void BtnPullFromPhone_Click(object sender, RoutedEventArgs e)
    {
        if (_isDumping || _isExtracting) return;

        _isExtracting = true;
        SetButtonsEnabled(false);
        resultInfoBar.IsOpen = false;
        txtLog.Text = "";

        var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_DATA");

        var progress = new Progress<string>(msg =>
        {
            txtLog.Text += msg + "\n";
            logScroller.ScrollToEnd();
        });

        try
        {
            var result = await PhoneDetectionService.ExtractGameDataAsync(dataDir, progress);

            // Show warnings in log
            foreach (var w in result.Warnings)
                txtLog.Text += $"[WARN] {w}\n";
            logScroller.ScrollToEnd();

            if (result.Error != null)
            {
                resultInfoBar.Title = "Pull from Phone failed";
                resultInfoBar.Message = result.Error;
                resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
                resultInfoBar.IsOpen = true;
                AppLogger.Warn($"Phone pull failed: {result.Error}");
                return;
            }

            // Auto-fill paths
            if (result.ConfigFilePath != null)
                SetPathText(txtConfigPath, result.ConfigFilePath);
            if (result.PatchFilePath != null)
                SetPathText(txtPatchPath, result.PatchFilePath);
            if (result.LanguageFilePath != null)
                SetPathText(txtLanguagePath, result.LanguageFilePath);

            // Default output if empty
            if (string.IsNullOrEmpty(GetPathText(txtOutputPath)))
            {
                var dumpDir = Path.Combine(dataDir, "dump");
                SetPathText(txtOutputPath, dumpDir);
            }

            SavePaths();

            txtAutoDetect.Text = $"Files pulled from {result.DeviceName}.";

            // Result info bar
            var parts = new List<string>();
            if (result.ConfigFilePath != null) parts.Add("config");
            if (result.PatchFileCount > 0) parts.Add($"{result.PatchFileCount} patch(es)");
            if (result.LanguageFilePath != null) parts.Add("language");

            resultInfoBar.Title = "Pull from Phone completed";
            resultInfoBar.Message = $"Extracted: {string.Join(", ", parts)} from {result.DeviceName}.";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;
            resultInfoBar.IsOpen = true;

            Increment(s => s.PhonePulls++);
            AppLogger.Info($"Phone pull completed from {result.DeviceName}: config={result.ConfigFilePath != null}, patches={result.PatchFileCount}, lang={result.LanguageFilePath != null}");
        }
        catch (Exception ex)
        {
            txtLog.Text += $"[ERROR] {ex.Message}\n";
            logScroller.ScrollToEnd();

            resultInfoBar.Title = "Pull from Phone failed";
            resultInfoBar.Message = ex.Message;
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;

            AppLogger.Error($"Phone pull failed: {ex.Message}", ex);
        }
        finally
        {
            _isExtracting = false;
            SetButtonsEnabled(true);
        }
    }

    // ── Dump actions ──────────────────────────────────────────────

    private async void BtnDumpAll_Click(object sender, RoutedEventArgs e) => await RunDumpAsync(DumperService.DumpMode.All);
    private async void BtnDumpChains_Click(object sender, RoutedEventArgs e) => await RunDumpAsync(DumperService.DumpMode.Chains);
    private async void BtnDumpAreas_Click(object sender, RoutedEventArgs e) => await RunDumpAsync(DumperService.DumpMode.Areas);
    private async void BtnDumpEvents_Click(object sender, RoutedEventArgs e) => await RunDumpAsync(DumperService.DumpMode.Events);
    private async void BtnDumpCards_Click(object sender, RoutedEventArgs e) => await RunDumpAsync(DumperService.DumpMode.CardCollection);
    private async void BtnDumpExperimental_Click(object sender, RoutedEventArgs e) => await RunDumpAsync(DumperService.DumpMode.Experimental);

    private async Task RunDumpAsync(DumperService.DumpMode mode)
    {
        if (_isDumping || _isExtracting) return;

        var configPath = GetPathText(txtConfigPath);
        var outputPath = GetPathText(txtOutputPath);

        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
        {
            resultInfoBar.Title = "Config archive is required";
            resultInfoBar.Message = "Please select a valid config archive file.";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;
            return;
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            resultInfoBar.Title = "Output directory is required";
            resultInfoBar.Message = "Please select an output directory.";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;
            return;
        }

        _isDumping = true;
        SetButtonsEnabled(false);
        resultInfoBar.IsOpen = false;
        txtLog.Text = "";

        var progress = new Progress<string>(msg =>
        {
            txtLog.Text += msg + "\n";
            logScroller.ScrollToEnd();
        });

        try
        {
            var result = await DumperService.DumpAsync(
                configPath,
                GetPathText(txtPatchPath),
                GetPathText(txtLanguagePath),
                outputPath,
                mode,
                progress);

            // Show warnings (already prefixed from DumperService)
            foreach (var w in result.Warnings)
                txtLog.Text += $"{w}\n";

            // Show errors (already prefixed from DumperService)
            foreach (var err in result.Errors)
                txtLog.Text += $"{err}\n";

            logScroller.ScrollToEnd();

            // Result info bar
            if (result.Errors.Count > 0)
            {
                resultInfoBar.Title = "Dump completed with errors";
                resultInfoBar.Message = string.Join("; ", result.Errors);
                resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            }
            else
            {
                var files = new List<string>();
                if (result.ChainItemOddsPath != null) files.Add("chain_item_odds.json");
                if (result.AreasPath != null) files.Add("areas.json");
                if (result.EventsPath != null) files.Add("events.json");
                if (result.CardCollectionPath != null) files.Add("card_collection.json");
                if (result.ExperimentalPath != null) files.Add("Experimental/");

                resultInfoBar.Title = "Dump completed successfully";
                resultInfoBar.Message = $"Generated: {string.Join(", ", files)}";
                resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;

                // Show open folder button
                btnOpenOutputFolder.Visibility = Visibility.Visible;
            }

            resultInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            txtLog.Text += $"[FATAL] {ex.Message}\n";
            logScroller.ScrollToEnd();

            resultInfoBar.Title = "Dump failed";
            resultInfoBar.Message = ex.Message;
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;
        }
        finally
        {
            _isDumping = false;
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        btnPullFromPhone.IsEnabled = enabled;
        btnDumpAll.IsEnabled = enabled;
        btnDumpChains.IsEnabled = enabled;
        btnDumpAreas.IsEnabled = enabled;
        btnDumpEvents.IsEnabled = enabled;
        btnDumpCards.IsEnabled = enabled;
        btnDumpExperimental.IsEnabled = enabled;
    }

    private void BtnOpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = GetPathText(txtOutputPath);
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
