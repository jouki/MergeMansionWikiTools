using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using merge_mansion_dumper.Dumper;
using MergeMansionWikiTools.Models;
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
        chkPets.IsChecked = _main.Settings.DumpPets;
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
        chkEvSoloMilestone.IsChecked = _main.Settings.EventSoloMilestone;
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

    private async void TryAutoDetect()
    {
        try
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_DATA");

            // Read current UI state on the UI thread before going off-thread
            bool needConfig = string.IsNullOrEmpty(GetPathText(txtConfigPath));
            bool needPatch = string.IsNullOrEmpty(GetPathText(txtPatchPath));
            bool needLanguage = string.IsNullOrEmpty(GetPathText(txtLanguagePath));

            // Directory scanning off the UI thread
            var (dataDirExists, configFile, patchFile, languageFile) = await Task.Run(() =>
            {
                if (!Directory.Exists(dataDir))
                    return (false, (string?)null, (string?)null, (string?)null);

                string? FindSingleFile(string subDir)
                {
                    var dir = Path.Combine(dataDir, subDir);
                    if (!Directory.Exists(dir)) return null;
                    var files = Directory.GetFiles(dir);
                    return files.Length == 1 ? files[0] : null;
                }

                return (true,
                    needConfig ? FindSingleFile("C") : null,      // Config: _DATA/C/
                    needPatch ? FindSingleFile("P") : null,       // Patch: _DATA/P/
                    needLanguage ? FindSingleFile("L") : null);   // Language: _DATA/L/
            });

            if (!dataDirExists)
            {
                txtAutoDetect.Text = "";
                return;
            }

            bool anyDetected = false;
            if (configFile != null)
            {
                SetPathText(txtConfigPath, configFile);
                anyDetected = true;
            }
            if (patchFile != null)
            {
                SetPathText(txtPatchPath, patchFile);
                anyDetected = true;
            }
            if (languageFile != null)
            {
                SetPathText(txtLanguagePath, languageFile);
                anyDetected = true;
            }
            if (anyDetected) SavePaths();

            txtAutoDetect.Text = anyDetected ? "Some paths were auto-detected from _DATA/ folder." : "";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Auto-detect from _DATA failed: {ex.Message}");
        }
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
        var folder = _main.Settings.ActiveDumpFolder;
        if (string.IsNullOrEmpty(folder)) folder = "Dump";
        return Path.Combine(basePath, version, folder);
    }

    /// <summary>
    /// Resolves dump output directory with collision detection.
    /// Returns null if user cancelled, or the resolved path.
    /// </summary>
    private async Task<string?> ResolveDumpOutputDirAsync()
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
            return null;

        var versionDir = Path.Combine(basePath, version);
        var dumpDir = Path.Combine(versionDir, "Dump");

        // No existing Dump folder → use "Dump"
        if (!Directory.Exists(dumpDir))
            return dumpDir;

        // Auto-new-folder mode → always create next available Dump N
        if (_main.Settings.DumpAutoNewFolder)
        {
            var autoFolder = DiscordDumpDownloadService.GetNextDumpFolderName(
                versionDir, null, isUnknownVersion: false);
            return Path.Combine(versionDir, autoFolder);
        }

        // Check if any existing Dump folder has the same CreatedAt (= same data already dumped)
        // We read CreatedAt from the config file that will be used for this dump
        var configCreatedAt = ReadConfigCreatedAt();
        if (configCreatedAt != null)
        {
            var existingFolder = DiscordDumpDownloadService.FindExistingDumpByCreatedAt(
                versionDir,
                DateTimeOffset.Parse(configCreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind));
            if (existingFolder != null)
            {
                // Same timestamp exists → ask: dump into that folder or create new?
                var msgBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Dump Already Exists",
                    Content = $"A dump with the same data timestamp already exists in:\n{existingFolder}\n\nDump into this folder (overwrite) or create a new one?",
                    PrimaryButtonText = "Overwrite",
                    SecondaryButtonText = "New Folder",
                    CloseButtonText = "Cancel",
                    Owner = Window.GetWindow(this)
                };
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
                var result = await msgBox.ShowDialogAsync();
                if (result == Wpf.Ui.Controls.MessageBoxResult.None) return null; // Cancel
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                    return Path.Combine(versionDir, existingFolder);
                // Secondary = new folder
                var collisionFolder = DiscordDumpDownloadService.GetNextDumpFolderName(
                    versionDir, null, isUnknownVersion: false);
                return Path.Combine(versionDir, collisionFolder);
            }
        }

        // Dump/ exists but different timestamp → offer a dropdown of every existing
        // Dump folder (Dump, Dump 2, Dump 3, ...) so the user can pick which to
        // overwrite, plus a "Create new" default. Previously Overwrite always targeted
        // plain "Dump" even when the user cared about the newest one (Dump 4).
        var nextFolder = DiscordDumpDownloadService.GetNextDumpFolderName(
            versionDir, null, isUnknownVersion: false);
        return await ShowDumpFolderSelectDialogAsync(versionDir, nextFolder);
    }

    // Displays a MessageBox with a ComboBox listing existing Dump folders + "Create new".
    // Returns the chosen absolute path, or null if user cancelled.
    private async Task<string?> ShowDumpFolderSelectDialogAsync(string versionDir, string nextFolder)
    {
        // Discover existing Dump folders sorted by index (Dump = 1, Dump 2, Dump 3, ...).
        var existing = new List<string>();
        if (Directory.Exists(Path.Combine(versionDir, "Dump")))
            existing.Add("Dump");
        for (int i = 2; i < 1000; i++)
        {
            var name = $"Dump {i}";
            if (Directory.Exists(Path.Combine(versionDir, name)))
                existing.Add(name);
        }

        // Options: each existing folder (newest first — most likely user intent) + "Create new"
        var options = new List<string>();
        for (int i = existing.Count - 1; i >= 0; i--) options.Add(existing[i]);
        var createNewLabel = $"Create new ({nextFolder})";
        options.Add(createNewLabel);

        // Build content — label + combo + hint
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "A Dump folder already exists for this version. Select which one to overwrite, or create a new one:",
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        });
        var combo = new System.Windows.Controls.ComboBox
        {
            ItemsSource = options,
            SelectedIndex = 0, // newest existing folder by default
            MinWidth = 220,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        panel.Children.Add(combo);

        var msgBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Dump Folder Exists",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            Owner = Window.GetWindow(this)
        };
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
        var result = await msgBox.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return null; // Cancel or close

        var choice = combo.SelectedItem as string ?? options[0];
        if (choice == createNewLabel)
            return Path.Combine(versionDir, nextFolder);
        return Path.Combine(versionDir, choice);
    }

    private string? ReadConfigCreatedAt()
    {
        var configPath = GetPathText(txtConfigPath);
        if (string.IsNullOrEmpty(configPath)) return null;
        // Can't read CreatedAt from binary config before dump — return null
        // (CreatedAt is only available after dump creates the JSON files)
        return null;
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
        _main.Settings.DumpPets = chkPets.IsChecked == true;
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
        _main.Settings.EventSoloMilestone = chkEvSoloMilestone.IsChecked == true;
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
        if (chkPets.IsChecked == true) mode |= DumperService.DumpMode.Pets;
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
        if (chkEvSoloMilestone.IsChecked == true) f |= EventFilters.SoloMilestone;
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
    {
        // Experimental dump goes into existing Dump/Experimental/ — use current active dump path directly
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
        {
            resultInfoBar.Title = "Output directory cannot be determined";
            resultInfoBar.Message = "Please set a workspace path and APK version in Settings.";
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;
            return;
        }
        // Find the highest existing Dump folder (Dump 4 > Dump 3 > Dump 2 > Dump), or create "Dump"
        var versionDir = Path.Combine(basePath, version);
        var outputPath = Path.Combine(versionDir, "Dump");
        if (Directory.Exists(versionDir))
        {
            var dumpDirs = Directory.GetDirectories(versionDir, "Dump*")
                .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^Dump( \d+)?$"))
                .OrderByDescending(d => d.Length).ThenByDescending(d => d)
                .ToList();
            if (dumpDirs.Count > 0)
                outputPath = dumpDirs[0];
        }

        var expDir = Path.Combine(outputPath, "Experimental");
        // Only ask about overwrite if Experimental subfolder already exists
        if (Directory.Exists(expDir))
        {
            var result = await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Experimental Dump Exists",
                Content = $"An Experimental subfolder already exists in:\n{expDir}\n\nOverwrite it?",
                PrimaryButtonText = "Overwrite",
                CloseButtonText = "Cancel",
                Owner = Window.GetWindow(this)
            }.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        }
        await RunDumpAsync(DumperService.DumpMode.Experimental, outputPath);
    }

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

        string? outputPath;
        if (customOutputDir != null)
        {
            outputPath = customOutputDir;
        }
        else
        {
            outputPath = await ResolveDumpOutputDirAsync();
            if (outputPath == null)
            {
                // User cancelled or paths not configured
                var basePath = _main.Settings.ImageExporterBasePath;
                var version = _main.Settings.SelectedApkVersion;
                if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
                {
                    resultInfoBar.Title = "Output directory cannot be determined";
                    resultInfoBar.Message = "Please set a workspace path and APK version in Settings, or use the arrow menu to dump to a custom folder.";
                    resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
                    resultInfoBar.IsOpen = true;
                }
                return;
            }
        }

        // Pre-dump: quick version check from config archive header
        if (customOutputDir == null)
        {
            var mismatchResult = await CheckVersionBeforeDumpAsync(configPath, outputPath);
            if (mismatchResult == null) return; // user cancelled
            if (mismatchResult != outputPath) outputPath = mismatchResult; // user chose to redirect
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

                // Show Load Dumped Files button — keep visible after experimental dump if it was already shown
                if (mode != DumperService.DumpMode.Experimental)
                    btnUseDumpFiles.Visibility = Visibility.Visible;
                // Don't hide button after experimental dump — main dump files are still valid

                Increment(s => s.DataDumps++);

                // Auto-check if dump is newer than last Discord publish
                _ = CheckDiscordPublishAsync(outputPath);
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
        chkPets.IsEnabled = enabled;
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

    private void BtnOpenDumpFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version)) return;

        var versionDir = Path.Combine(basePath, version);
        var folders = DiscordDumpDownloadService.ScanDumpFolders(versionDir);
        if (folders.Count == 0) return;

        var menu = new ContextMenu();
        foreach (var (name, createdAt) in folders)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            if (createdAt != null)
            {
                header.Children.Add(new TextBlock
                {
                    Text = $"  ({createdAt[..Math.Min(10, createdAt.Length)]})",
                    Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var folderPath = Path.Combine(versionDir, name);
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                if (Directory.Exists(folderPath))
                    Process.Start(new ProcessStartInfo { FileName = folderPath, UseShellExecute = true });
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = btnOpenDumpFolderMenu;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ── Log action buttons ────────────────────────────────────────

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(txtLog.Text))
            App.NativeSetClipboardText(txtLog.Text);
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

        var dialoguesPath = Path.Combine(dumpDir, "dialogues.json");
        if (File.Exists(dialoguesPath))
        {
            _main.SetDialoguesPath(dialoguesPath);
            loaded.Add("dialogues");
        }

        var petsPath = Path.Combine(dumpDir, "Pets.json");
        if (!File.Exists(petsPath))
            petsPath = Path.Combine(dumpDir, "Experimental", "Pets.json");
        if (File.Exists(petsPath))
        {
            _main.SetPetsPath(petsPath);
            loaded.Add("Pets");
        }

        var cardCollectionPath = Path.Combine(dumpDir, "card_collection.json");
        if (File.Exists(cardCollectionPath))
        {
            _main.SetCardCollectionPath(cardCollectionPath);
            loaded.Add("card_collection");
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
        _main.RefreshSettingsPaths();
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

    // ── Discord Publish ─────────────────────────────────────────

    private string? _pendingPublishDir;

    private void SetPublishEnabled(bool enabled)
    {
        btnPublishDiscord.IsEnabled = enabled;
        btnPublishDiscord.Opacity = enabled ? 1.0 : 0.4;
    }

    private async Task CheckDiscordPublishAsync(string dumpDir)
    {
        var token = _main.Settings.DiscordBotToken;
        if (string.IsNullOrWhiteSpace(token))
            token = AppSettings.DefaultDiscordBotToken;
        var channelId = _main.Settings.DiscordChannelId;
        if (string.IsNullOrWhiteSpace(channelId))
            channelId = AppSettings.DefaultDiscordChannelId;

        var createdAt = DiscordDumpService.ReadCreatedAtFromDump(dumpDir);
        if (createdAt == null)
        {
            SetPublishEnabled(false);
            btnPublishDiscord.ToolTip = "No CreatedAt timestamp found in dump files";
            return;
        }

        try
        {
            var lastPublished = await DiscordDumpService.GetLastPublishedDateAsync(token, channelId);
            if (DiscordDumpService.IsDumpNewer(createdAt, lastPublished))
            {
                _pendingPublishDir = dumpDir;
                SetPublishEnabled(true);

                if (lastPublished == null)
                    btnPublishDiscord.ToolTip = "New dump available — no previous publish found";
                else
                    btnPublishDiscord.ToolTip = $"Dump is newer than last publish ({lastPublished:yyyy-MM-dd HH:mm})";
            }
            else
            {
                SetPublishEnabled(false);
                btnPublishDiscord.ToolTip = "Dump is not newer than last published version";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Discord publish check failed: {ex.Message}");
            SetPublishEnabled(false);
            btnPublishDiscord.ToolTip = $"Discord check failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Before dump: reads CreatedAt from the binary config archive header (fast, no full import)
    /// and checks if the data matches the currently selected APK version.
    /// Returns: the output path to use (may be redirected), or null if user cancelled.
    /// </summary>
    private async Task<string?> CheckVersionBeforeDumpAsync(string configPath, string currentOutputPath)
    {
        try
        {
            var selectedVersion = _main.Settings.SelectedApkVersion;
            if (string.IsNullOrEmpty(selectedVersion)) return currentOutputPath;

            var createdAt = await Task.Run(() => DumperService.ReadConfigCreatedAt(configPath));
            if (createdAt == null) return currentOutputPath; // can't read — proceed normally

            // Try to match the config's CreatedAt to an APK version
            List<ApkDownloadService.ApkVersionInfo>? versions = null;
            try { versions = await Task.Run(() => ApkDownloadService.FetchAvailableVersionsAsync()); }
            catch { }
            if (versions == null || versions.Count == 0) return currentOutputPath;

            var matched = ApkDownloadService.MatchVersionByDate(versions, createdAt.Value);
            if (matched == null) return currentOutputPath; // can't determine — no warning
            if (matched.Version == selectedVersion) return currentOutputPath; // match — all good

            // Mismatch! Warn before dump
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Version Mismatch",
                Content = $"The game data appears to be from v{matched.Version} " +
                          $"(created {createdAt.Value:yyyy-MM-dd}), but the selected " +
                          $"game version is v{selectedVersion}.\n\n" +
                          $"Save dump to v{matched.Version} instead?",
                PrimaryButtonText = $"Use v{matched.Version}",
                SecondaryButtonText = $"Keep v{selectedVersion}",
                CloseButtonText = "Cancel",
                Owner = Window.GetWindow(this)
            };
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
            var result = await msgBox.ShowDialogAsync();

            if (result == Wpf.Ui.Controls.MessageBoxResult.None)
                return null; // Cancel

            if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                // Redirect to correct version folder
                var basePath = _main.Settings.ImageExporterBasePath;
                var correctVersionDir = Path.Combine(basePath, matched.Version);
                var folder = DiscordDumpDownloadService.GetNextDumpFolderName(
                    correctVersionDir, createdAt, isUnknownVersion: false);
                return Path.Combine(correctVersionDir, folder);
            }

            return currentOutputPath; // Keep — use originally resolved path
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Pre-dump version check failed: {ex.Message}");
            return currentOutputPath;
        }
    }

    private async void BtnPublishDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingPublishDir == null) return;

        var token = _main.Settings.DiscordBotToken;
        if (string.IsNullOrWhiteSpace(token))
            token = AppSettings.DefaultDiscordBotToken;
        var channelId = _main.Settings.DiscordChannelId;
        if (string.IsNullOrWhiteSpace(channelId))
            channelId = AppSettings.DefaultDiscordChannelId;

        var createdAt = DiscordDumpService.ReadCreatedAtFromDump(_pendingPublishDir);
        if (createdAt == null) return;

        // Confirm before publishing
        var msgBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Publish to Discord",
            Content = $"Upload dump ZIP to Discord?\n\nCreated at: {createdAt}\nFolder: {System.IO.Path.GetFileName(_pendingPublishDir)}",
            PrimaryButtonText = "Publish",
            CloseButtonText = "Cancel",
            Owner = Window.GetWindow(this)
        };
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
        var result = await msgBox.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        btnPublishDiscord.IsEnabled = false;
        var originalContent = btnPublishDiscord.ToolTip;

        var progress = new Progress<string>(msg =>
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.Text += $"\n[Discord] {msg}";
                logScroller.ScrollToEnd();
            });
        });

        try
        {
            var success = await DiscordDumpService.PublishDumpAsync(
                token, channelId, _pendingPublishDir, createdAt, progress);

            if (success)
            {
                resultInfoBar.Title = "Published to Discord";
                resultInfoBar.Message = "Dump ZIP uploaded successfully.";
                resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;
                resultInfoBar.IsOpen = true;
                btnPublishDiscord.ToolTip = "Already published";
            }
            else
            {
                SetPublishEnabled(true);
                btnPublishDiscord.ToolTip = originalContent;
            }
        }
        catch (Exception ex)
        {
            resultInfoBar.Title = "Discord publish failed";
            resultInfoBar.Message = ex.Message;
            resultInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            resultInfoBar.IsOpen = true;
            btnPublishDiscord.IsEnabled = true;
            btnPublishDiscord.ToolTip = originalContent;
        }
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
