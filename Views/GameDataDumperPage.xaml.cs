using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using merge_mansion_dumper.Dumper;
using MergeMansionWikiTools.Services;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

public partial class GameDataDumperPage : UserControl
{
    private readonly MainWindow _main;
    private bool _isDumping;
    private bool _isExtracting;
    private bool _suppressSave;
    private readonly SolidColorBrush _splitSepBrush;

    private string? _lastDumpLogPath;
    private DumperService.DumpResult? _lastDumpResult;
    private string? _lastDumpOutputDir;

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

        // Load checkbox states from settings
        _suppressSave = true;
        chkChains.IsChecked = _main.Settings.DumpChains;
        chkAreas.IsChecked = _main.Settings.DumpAreas;
        chkEvents.IsChecked = _main.Settings.DumpEvents;
        chkDialogues.IsChecked = _main.Settings.DumpDialogues;
        chkCards.IsChecked = _main.Settings.DumpCards;
        // Event filters
        chkEvLuckyCatch.IsChecked = _main.Settings.EventLuckyCatch;
        chkEvLuckySnap.IsChecked = _main.Settings.EventLuckySnap;
        chkEvSeasonal.IsChecked = _main.Settings.EventSeasonal;
        chkEvReArchaeology.IsChecked = _main.Settings.EventReArchaeology;
        chkEvHorizonsCup.IsChecked = _main.Settings.EventHorizonsCup;
        chkEvRollTheDice.IsChecked = _main.Settings.EventRollTheDice;
        chkEvGarageCleanup.IsChecked = _main.Settings.EventGarageCleanup;
        chkEvMysteries.IsChecked = _main.Settings.EventMysteries;
        chkEvBoultonLeague.IsChecked = _main.Settings.EventBoultonLeague;
        chkEvBakeOff.IsChecked = _main.Settings.EventBakeOff;
        chkEvBonanza.IsChecked = _main.Settings.EventBonanza;
        chkEvLegacy.IsChecked = _main.Settings.EventLegacy;
        chkEvOthers.IsChecked = _main.Settings.EventOthers;
        chkEvUncategorised.IsChecked = _main.Settings.EventUncategorised;
        expEventCategories.IsExpanded = _main.Settings.EventSubExpanded;
        _suppressSave = false;

        // Initial visibility of event sub-checkboxes
        expEventCategories.Visibility = chkEvents.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        // Split Dump button styling
        _splitSepBrush = GetSplitSeparatorBrush();
        btnDumpSep.Background = _splitSepBrush;
        ApplicationThemeManager.Changed += (_, _) => Dispatcher.InvokeAsync(RefreshSplitSeparatorColor);
        ApplySplitButtonStyle(btnDump, btnDumpMenu);

        // Show Open Dump Folder if dump dir exists
        UpdateDumpFolderVisibility();

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

        txtAutoDetect.Text = anyDetected ? "Some paths were auto-detected from _DATA/ folder." : "";
    }

    // ── Path helpers ──────────────────────────────────────────────

    private static readonly string _placeholderConfig = "Drop config archive file here or browse...";
    private static readonly string _placeholderPatch = "Drop patch config file here or browse...";
    private static readonly string _placeholderLang = "Drop language file here or browse...";

    private static void SetPathText(TextBox tb, string path)
    {
        tb.Text = path;
        tb.SetResourceReference(TextBox.ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    private static string GetPathText(TextBox tb)
    {
        var text = tb.Text;
        if (text == _placeholderConfig || text == _placeholderPatch || text == _placeholderLang)
            return "";
        return text;
    }

    private void SavePaths()
    {
        _main.Settings.DumperConfigPath = GetPathText(txtConfigPath);
        _main.Settings.DumperPatchPath = GetPathText(txtPatchPath);
        _main.Settings.DumperLanguagePath = GetPathText(txtLanguagePath);
        _main.SaveSettings();
    }

    // ── Output path derivation ────────────────────────────────────

    private string? GetDumpOutputDir()
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
            return null;
        return Path.Combine(basePath, version, "Dump");
    }

    private void UpdateDumpFolderVisibility()
    {
        var dir = _lastDumpOutputDir ?? GetDumpOutputDir();
        dumpFolderRow.Visibility = dir != null && Directory.Exists(dir) ? Visibility.Visible : Visibility.Collapsed;
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

    // ── Checkbox persistence ──────────────────────────────────────

    private void DumpCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSave) return;
        _main.Settings.DumpChains = chkChains.IsChecked == true;
        _main.Settings.DumpAreas = chkAreas.IsChecked == true;
        _main.Settings.DumpEvents = chkEvents.IsChecked == true;
        _main.Settings.DumpDialogues = chkDialogues.IsChecked == true;
        _main.Settings.DumpCards = chkCards.IsChecked == true;
        _main.SaveSettings();
    }

    private void chkEvents_Changed(object sender, RoutedEventArgs e)
    {
        DumpCheckbox_Changed(sender, e);
        if (_suppressSave) return;
        expEventCategories.Visibility = chkEvents.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EventSubCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSave) return;
        _main.Settings.EventLuckyCatch = chkEvLuckyCatch.IsChecked == true;
        _main.Settings.EventLuckySnap = chkEvLuckySnap.IsChecked == true;
        _main.Settings.EventSeasonal = chkEvSeasonal.IsChecked == true;
        _main.Settings.EventReArchaeology = chkEvReArchaeology.IsChecked == true;
        _main.Settings.EventHorizonsCup = chkEvHorizonsCup.IsChecked == true;
        _main.Settings.EventRollTheDice = chkEvRollTheDice.IsChecked == true;
        _main.Settings.EventGarageCleanup = chkEvGarageCleanup.IsChecked == true;
        _main.Settings.EventMysteries = chkEvMysteries.IsChecked == true;
        _main.Settings.EventBoultonLeague = chkEvBoultonLeague.IsChecked == true;
        _main.Settings.EventBakeOff = chkEvBakeOff.IsChecked == true;
        _main.Settings.EventBonanza = chkEvBonanza.IsChecked == true;
        _main.Settings.EventLegacy = chkEvLegacy.IsChecked == true;
        _main.Settings.EventOthers = chkEvOthers.IsChecked == true;
        _main.Settings.EventUncategorised = chkEvUncategorised.IsChecked == true;
        _main.SaveSettings();
    }

    private void SetAllEventCheckboxes(bool value)
    {
        _suppressSave = true;
        chkEvSeasonal.IsChecked = value;
        chkEvGarageCleanup.IsChecked = value;
        chkEvMysteries.IsChecked = value;
        chkEvBakeOff.IsChecked = value;
        chkEvBonanza.IsChecked = value;
        chkEvLuckyCatch.IsChecked = value;
        chkEvLuckySnap.IsChecked = value;
        chkEvReArchaeology.IsChecked = value;
        chkEvBoultonLeague.IsChecked = value;
        chkEvHorizonsCup.IsChecked = value;
        chkEvRollTheDice.IsChecked = value;
        chkEvLegacy.IsChecked = value;
        chkEvOthers.IsChecked = value;
        chkEvUncategorised.IsChecked = value;
        _suppressSave = false;
        EventSubCheckbox_Changed(this, new RoutedEventArgs());
    }

    private void BtnSelectAllEvents_Click(object sender, RoutedEventArgs e) => SetAllEventCheckboxes(true);
    private void BtnDeselectAllEvents_Click(object sender, RoutedEventArgs e) => SetAllEventCheckboxes(false);

    private void BtnRecommendedEvents_Click(object sender, RoutedEventArgs e)
    {
        _suppressSave = true;
        chkEvSeasonal.IsChecked = true;
        chkEvGarageCleanup.IsChecked = true;
        chkEvMysteries.IsChecked = true;
        chkEvBakeOff.IsChecked = true;
        chkEvBonanza.IsChecked = true;
        chkEvLuckyCatch.IsChecked = true;
        chkEvLuckySnap.IsChecked = true;
        chkEvReArchaeology.IsChecked = true;
        chkEvBoultonLeague.IsChecked = false;
        chkEvHorizonsCup.IsChecked = true;
        chkEvRollTheDice.IsChecked = true;
        chkEvLegacy.IsChecked = false;
        chkEvOthers.IsChecked = false;
        chkEvUncategorised.IsChecked = true;
        _suppressSave = false;
        EventSubCheckbox_Changed(this, new RoutedEventArgs());
    }

    private void EventExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSave) return;
        _main.Settings.EventSubExpanded = expEventCategories.IsExpanded;
        _main.SaveSettings();
    }

    private DumperService.DumpMode GetSelectedDumpMode()
    {
        var mode = DumperService.DumpMode.None;
        if (chkChains.IsChecked == true) mode |= DumperService.DumpMode.Chains;
        if (chkAreas.IsChecked == true) mode |= DumperService.DumpMode.Areas;
        if (chkEvents.IsChecked == true) mode |= DumperService.DumpMode.Events;
        if (chkDialogues.IsChecked == true) mode |= DumperService.DumpMode.Dialogues;
        if (chkCards.IsChecked == true) mode |= DumperService.DumpMode.CardCollection;
        return mode;
    }

    private EventFilters GetSelectedEventFilters()
    {
        var f = EventFilters.None;
        if (chkEvLuckyCatch.IsChecked == true) f |= EventFilters.LuckyCatch;
        if (chkEvLuckySnap.IsChecked == true) f |= EventFilters.LuckySnap;
        if (chkEvSeasonal.IsChecked == true) f |= EventFilters.Seasonal;
        if (chkEvReArchaeology.IsChecked == true) f |= EventFilters.ReArchaeology;
        if (chkEvHorizonsCup.IsChecked == true) f |= EventFilters.HorizonsCup;
        if (chkEvRollTheDice.IsChecked == true) f |= EventFilters.RollTheDice;
        if (chkEvGarageCleanup.IsChecked == true) f |= EventFilters.GarageCleanup;
        if (chkEvMysteries.IsChecked == true) f |= EventFilters.Mysteries;
        if (chkEvBoultonLeague.IsChecked == true) f |= EventFilters.BoultonLeague;
        if (chkEvBakeOff.IsChecked == true) f |= EventFilters.BakeOff;
        if (chkEvBonanza.IsChecked == true) f |= EventFilters.Bonanza;
        if (chkEvLegacy.IsChecked == true) f |= EventFilters.Legacy;
        if (chkEvOthers.IsChecked == true) f |= EventFilters.Others;
        if (chkEvUncategorised.IsChecked == true) f |= EventFilters.Uncategorised;
        return f;
    }

    // ── Dump actions ──────────────────────────────────────────────

    private async void BtnDump_Click(object sender, RoutedEventArgs e)
    {
        var mode = GetSelectedDumpMode();
        if (mode == DumperService.DumpMode.None)
        {
            resultInfoBar.Title = "Nothing to dump";
            resultInfoBar.Message = "Please select at least one category to dump.";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            resultInfoBar.IsOpen = true;
            return;
        }
        await RunDumpAsync(mode);
    }

    private void BtnDumpMenu_Click(object sender, RoutedEventArgs e)
    {
        var mode = GetSelectedDumpMode();
        if (mode == DumperService.DumpMode.None)
        {
            resultInfoBar.Title = "Nothing to dump";
            resultInfoBar.Message = "Please select at least one category to dump.";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            resultInfoBar.IsOpen = true;
            return;
        }

        var menu = new ContextMenu();
        var item = new MenuItem { Header = "Dump to custom folder..." };
        item.Click += async (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Select output directory for dump" };
            if (dlg.ShowDialog() == true)
                await RunDumpAsync(mode, dlg.FolderName);
        };
        menu.Items.Add(item);
        OpenSplitMenu(menu, btnDumpMenu);
    }

    private async void BtnDumpExperimental_Click(object sender, RoutedEventArgs e)
        => await RunDumpAsync(DumperService.DumpMode.Experimental);

    private async Task RunDumpAsync(DumperService.DumpMode mode, string? customOutputDir = null)
    {
        if (_isDumping || _isExtracting) return;

        var configPath = GetPathText(txtConfigPath);

        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
        {
            resultInfoBar.Title = "Config archive is required";
            resultInfoBar.Message = "Please select a valid config archive file.";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;
            return;
        }

        var outputPath = customOutputDir ?? GetDumpOutputDir();
        if (string.IsNullOrEmpty(outputPath))
        {
            resultInfoBar.Title = "Output directory cannot be determined";
            resultInfoBar.Message = "Please set a workspace path and APK version in Settings, or use the arrow menu to dump to a custom folder.";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;
            return;
        }

        _isDumping = true;
        SetButtonsEnabled(false);
        resultInfoBar.IsOpen = false;
        txtLog.Text = "";

        // Create dump log file
        var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDir);
        var logFileName = $"dump_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        _lastDumpLogPath = Path.Combine(logsDir, logFileName);
        StreamWriter? logWriter = null;

        try
        {
            logWriter = new StreamWriter(_lastDumpLogPath, false, System.Text.Encoding.UTF8);
            await logWriter.WriteLineAsync($"=== Dump started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            await logWriter.WriteLineAsync($"Mode: {mode}");
            await logWriter.WriteLineAsync($"Output: {outputPath}");
            await logWriter.WriteLineAsync();

            var progress = new Progress<string>(msg =>
            {
                txtLog.Text += msg + "\n";
                logScroller.ScrollToEnd();
                try { logWriter?.WriteLine(msg); } catch { }
            });

            var result = await DumperService.DumpAsync(
                configPath,
                GetPathText(txtPatchPath),
                GetPathText(txtLanguagePath),
                outputPath,
                mode,
                GetSelectedEventFilters(),
                progress);

            _lastDumpResult = result;
            _lastDumpOutputDir = outputPath;

            // Show warnings (already prefixed from DumperService)
            foreach (var w in result.Warnings)
            {
                txtLog.Text += $"{w}\n";
                try { logWriter?.WriteLine(w); } catch { }
            }

            // Show errors (already prefixed from DumperService)
            foreach (var err in result.Errors)
            {
                txtLog.Text += $"{err}\n";
                try { logWriter?.WriteLine(err); } catch { }
            }

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
                if (result.DialoguesPath != null) files.Add("dialogues.json");
                if (result.CardCollectionPath != null) files.Add("card_collection.json");
                if (result.ExperimentalPath != null) files.Add("Experimental/");

                resultInfoBar.Title = "Dump completed successfully";
                resultInfoBar.Message = $"Generated: {string.Join(", ", files)}";
                resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;

                // Show Load Dumped Files button (only for non-experimental dumps)
                if (mode != DumperService.DumpMode.Experimental)
                    btnUseDumpFiles.Visibility = Visibility.Visible;
                else
                    btnUseDumpFiles.Visibility = Visibility.Collapsed;

                Increment(s => s.DataDumps++);
            }

            resultInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            txtLog.Text += $"[FATAL] {ex.Message}\n";
            logScroller.ScrollToEnd();
            try { logWriter?.WriteLine($"[FATAL] {ex.Message}"); } catch { }

            resultInfoBar.Title = "Dump failed";
            resultInfoBar.Message = ex.Message;
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;
        }
        finally
        {
            if (logWriter != null)
            {
                try
                {
                    await logWriter.WriteLineAsync();
                    await logWriter.WriteLineAsync($"=== Dump ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    await logWriter.DisposeAsync();
                }
                catch { }
            }

            logActionButtons.Visibility = Visibility.Visible;
            UpdateDumpFolderVisibility();

            _isDumping = false;
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        btnPullFromPhone.IsEnabled = enabled;
        btnDump.IsEnabled = enabled;
        btnDumpSep.Opacity = enabled ? 1.0 : 0.3;
        btnDumpMenu.IsEnabled = enabled;
        chkChains.IsEnabled = enabled;
        chkAreas.IsEnabled = enabled;
        chkEvents.IsEnabled = enabled;
        chkDialogues.IsEnabled = enabled;
        chkCards.IsEnabled = enabled;
        expEventCategories.IsEnabled = enabled;
        btnDumpExperimental.IsEnabled = enabled;
    }

    // ── Open Dump Folder ──────────────────────────────────────────

    private void BtnOpenDumpFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _lastDumpOutputDir ?? GetDumpOutputDir();
        if (path != null && Directory.Exists(path))
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    // ── Log action buttons ────────────────────────────────────────

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(txtLog.Text))
            Clipboard.SetDataObject(txtLog.Text, false);
    }

    private void BtnOpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDumpLogPath != null && File.Exists(_lastDumpLogPath))
            Process.Start(new ProcessStartInfo { FileName = _lastDumpLogPath, UseShellExecute = true });
    }

    private void BtnOpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        if (Directory.Exists(logsDir))
            Process.Start(new ProcessStartInfo { FileName = logsDir, UseShellExecute = true });
    }

    // ── Load Dumped Files ─────────────────────────────────────────

    private async void BtnUseDumpFiles_Click(object sender, RoutedEventArgs e)
    {
        var dumpDir = _lastDumpOutputDir ?? GetDumpOutputDir();
        if (dumpDir == null || !Directory.Exists(dumpDir)) return;

        var loaded = new List<string>();

        var chainPath = Path.Combine(dumpDir, "chain_item_odds.json");
        if (File.Exists(chainPath))
        {
            _main.Settings.ChainItemOddsPath = chainPath;
            await _main.LoadDataAsync(chainPath);
            _main.SaveSettings();
            loaded.Add("chains");
        }

        var areasPath = Path.Combine(dumpDir, "areas.json");
        if (File.Exists(areasPath))
        {
            _main.SetAreasPath(areasPath);
            loaded.Add("areas");
        }

        var eventsPath = Path.Combine(dumpDir, "events.json");
        if (File.Exists(eventsPath))
        {
            _main.SetEventsPath(eventsPath);
            loaded.Add("events");
        }

        if (loaded.Count > 0)
        {
            resultInfoBar.Title = "Dumped files loaded";
            resultInfoBar.Message = $"Loaded {string.Join(", ", loaded)} from {dumpDir}";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;
        }
        else
        {
            resultInfoBar.Title = "No dump files found";
            resultInfoBar.Message = $"No chain/area/event JSON files found in {dumpDir}";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
        }
        resultInfoBar.IsOpen = true;

        AppLogger.Info($"Loaded dumped files from {dumpDir}: {string.Join(", ", loaded)}");
    }

    // ── Split button helpers ──────────────────────────────────────

    /// <summary>
    /// Opens a split-button context menu with expand-down animation (no slide from above).
    /// </summary>
    private static void OpenSplitMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.Resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;
        menu.RenderTransformOrigin = new Point(0.5, 0);
        menu.RenderTransform = new ScaleTransform(1, 0);
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.Opened += (_, _) =>
        {
            menu.HorizontalOffset = target.ActualWidth - menu.ActualWidth;
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ((ScaleTransform)menu.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };
        menu.IsOpen = true;
    }

    private Color GetSplitSeparatorColor()
    {
        try
        {
            if (FindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
            {
                var c = accent.Color;
                return Color.FromRgb(
                    (byte)Math.Max(0, c.R - 35),
                    (byte)Math.Max(0, c.G - 35),
                    (byte)Math.Max(0, c.B - 35));
            }
        }
        catch { }
        return Color.FromArgb(0x30, 0, 0, 0);
    }

    private SolidColorBrush GetSplitSeparatorBrush() => new(GetSplitSeparatorColor());

    private void RefreshSplitSeparatorColor() => _splitSepBrush.Color = GetSplitSeparatorColor();

    private static void ApplySplitButtonStyle(Wpf.Ui.Controls.Button leftBtn, Wpf.Ui.Controls.Button rightBtn)
    {
        leftBtn.Margin = new Thickness(0);
        rightBtn.Margin = new Thickness(0);

        leftBtn.Loaded += (_, _) => SetInternalCornerRadius(leftBtn, new CornerRadius(4, 0, 0, 4));
        rightBtn.Loaded += (_, _) => SetInternalCornerRadius(rightBtn, new CornerRadius(0, 4, 4, 0));
    }

    private static void SetInternalCornerRadius(Control control, CornerRadius radius)
    {
        var border = FindVisualChild<Border>(control);
        if (border != null)
            border.CornerRadius = radius;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
                return result;
            var found = FindVisualChild<T>(child);
            if (found != null)
                return found;
        }
        return null;
    }
}
