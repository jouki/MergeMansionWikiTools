using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Microsoft.Win32;

namespace MergeMansionWikiTools.Views;

public partial class SettingsPage : UserControl
{
    private readonly MainWindow _main;
    private ScrollViewer[] _tabPanels = null!;
    private int _currentTabIndex;
    private List<ApkDownloadService.ApkVersionInfo>? _apkVersions;
    private string? _lastApkDir;
    private CancellationTokenSource? _tinifyCheck1Cts;
    private CancellationTokenSource? _tinifyCheck2Cts;

    public SettingsPage(MainWindow main)
    {
        AppLogger.Info("[Settings] Constructor START");
        _main = main;
        InitializeComponent();
        AppLogger.Info("[Settings] InitializeComponent done");

        _tabPanels = [panelGeneral, panelTools, panelWiki, panelAdvanced];

        // Load saved paths
        txtChainPath.Text = _main.Settings.ChainItemOddsPath;
        txtAreasPath.Text = _main.Settings.AreasJsonPath;
        txtEventsPath.Text = _main.Settings.EventsJsonPath;
        txtDialoguesPath.Text = _main.Settings.DialoguesJsonPath;
        txtPetsPath.Text = _main.Settings.PetsJsonPath;
        txtCardCollectionPath.Text = _main.Settings.CardCollectionJsonPath;
        txtTinifyKey.Text = _main.Settings.TinifyApiKey;
        txtTinifyKey2.Text = _main.Settings.TinifyApiKey2;
        txtImageBasePath.Text = _main.Settings.ImageExporterBasePath;
        // Debug mode
        toggleDebugMode.IsChecked = _main.Settings.DebugMode;
        toggleDetectionIndices.IsChecked = _main.Settings.ShowDetectionIndices;

        // Image Extractor advanced
        toggleExtractBuiltIn.IsChecked = _main.Settings.ExtractIncludeBuiltIn;

        // Clipboard settings
        toggleClipboardAutoAdd.IsChecked = _main.Settings.ClipboardAutoAdd;
        toggleClipboardGlobal.IsChecked = _main.Settings.ClipboardMonitorGlobal;
        gridClipboardGlobal.Visibility = _main.Settings.ClipboardAutoAdd ? Visibility.Visible : Visibility.Collapsed;
        toggleClearOptimiserOnChainEntry.IsChecked = _main.Settings.ClearOptimiserOnChainEntry;

        // Dumper settings
        toggleDumpAutoNewFolder.IsChecked = _main.Settings.DumpAutoNewFolder;

        AppLogger.Info("[Settings] Checkboxes done");
        // Wiki mapping status + bot credentials
        UpdateWikiMappingStatus();
        _suppressBotCredentialReset = true;
        txtWikiBotUsername.Text = _main.Settings.WikiUsername;
        txtWikiBotPassword.Password = _main.Settings.WikiPassword;
        _suppressBotCredentialReset = false;
        UpdateBotStatus();

        // Set theme ComboBox to match saved preference
        cmbTheme.SelectedIndex = _main.Settings.ThemePreference switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0 // System
        };

        // Position tab indicator after layout
        Loaded += (_, _) => UpdateTabIndicator(animate: false);

        // Subscribe to wizard download status (if a download was started during OOBE)
        SetupWizard.DownloadStatusUpdated += OnWizardDownloadStatus;
        Unloaded += (_, _) => SetupWizard.DownloadStatusUpdated -= OnWizardDownloadStatus;

        AppLogger.Info("[Settings] Wiki/Bot status done");
        // Apply pre-loaded data when available (non-blocking, no re-fetch)
        ApplyPreloadedDataWhenReady();

        // Check TinyPNG usage on load
        AppLogger.Info("[Settings] ApplyPreloadedData done");
        if (!string.IsNullOrWhiteSpace(_main.Settings.TinifyApiKey))
            TriggerTinifyCheck(1, _main.Settings.TinifyApiKey, delayMs: 0);
        if (!string.IsNullOrWhiteSpace(_main.Settings.TinifyApiKey2))
            TriggerTinifyCheck(2, _main.Settings.TinifyApiKey2, delayMs: 0);
        AppLogger.Info("[Settings] Constructor END");
    }

    /// <summary>
    /// Re-reads file paths from Settings into the TextBoxes.
    /// Called when navigating to the page or after external path changes.
    /// </summary>
    public void RefreshPaths()
    {
        txtChainPath.Text = _main.Settings.ChainItemOddsPath;
        txtAreasPath.Text = _main.Settings.AreasJsonPath;
        txtEventsPath.Text = _main.Settings.EventsJsonPath;
        txtDialoguesPath.Text = _main.Settings.DialoguesJsonPath;
        txtPetsPath.Text = _main.Settings.PetsJsonPath;
        txtCardCollectionPath.Text = _main.Settings.CardCollectionJsonPath;
    }

    // ── Tab bar ──

    private void TabBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var idx = tabBar.SelectedIndex;
        if (idx < 0 || idx == _currentTabIndex) return;

        SwitchTabContent(idx);
        UpdateTabIndicator(animate: true);
    }

    private void SwitchToTab(int tabIndex)
    {
        tabBar.SelectedIndex = tabIndex;
    }

    private void SwitchTabContent(int index)
    {
        // Hide old panel
        _tabPanels[_currentTabIndex].Visibility = Visibility.Collapsed;

        // Show new panel with fade + slide-up animation
        var panel = _tabPanels[index];
        panel.Visibility = Visibility.Visible;
        panel.Opacity = 0;

        var translate = new TranslateTransform(0, 12);
        panel.RenderTransform = translate;

        var dur = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        var slideUp = new DoubleAnimation(12, 0, dur) { EasingFunction = ease };

        panel.BeginAnimation(OpacityProperty, fadeIn);
        translate.BeginAnimation(TranslateTransform.YProperty, slideUp);

        _currentTabIndex = index;
    }

    private void UpdateTabIndicator(bool animate)
    {
        if (tabBar.SelectedIndex < 0 || tabIndicator == null) return;

        if (tabBar.ItemContainerGenerator.ContainerFromIndex(tabBar.SelectedIndex)
            is not ListBoxItem container)
            return;

        var transform = container.TransformToVisual(tabBarContainer);
        var point = transform.Transform(new Point(0, 0));
        double targetX = point.X;
        double targetW = container.ActualWidth;

        if (animate)
        {
            var dur = TimeSpan.FromMilliseconds(250);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            tabIndicatorTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { To = targetX, Duration = dur, EasingFunction = ease });
            tabIndicator.BeginAnimation(WidthProperty,
                new DoubleAnimation { To = targetW, Duration = dur, EasingFunction = ease });
        }
        else
        {
            tabIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            tabIndicatorTransform.X = targetX;
            tabIndicator.BeginAnimation(WidthProperty, null);
            tabIndicator.Width = targetW;
        }
    }

    // ── Drag & Drop ──

    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ChainFileDrop(object sender, DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is { Length: > 1 })
        {
            await AutoAssignDroppedFiles(files);
            e.Handled = true;
            return;
        }
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtChainPath.Text = path;
            SaveChainPath(path);
            await _main.LoadDataAsync(path);
        }
        e.Handled = true;
    }

    private async void AreasFileDrop(object sender, DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is { Length: > 1 })
        {
            await AutoAssignDroppedFiles(files);
            e.Handled = true;
            return;
        }
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtAreasPath.Text = path;
            _main.SetAreasPath(path);
        }
        e.Handled = true;
    }

    private async void EventsFileDrop(object sender, DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is { Length: > 1 })
        {
            await AutoAssignDroppedFiles(files);
            e.Handled = true;
            return;
        }
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtEventsPath.Text = path;
            _main.SetEventsPath(path);
        }
        e.Handled = true;
    }

    private async void DumpFilesCardDrop(object sender, DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        await AutoAssignDroppedFiles(files);
        e.Handled = true;
    }

    private async Task AutoAssignDroppedFiles(string[] paths)
    {
        var jsonFiles = paths.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (jsonFiles.Length == 0)
        {
            _main.ShowStatus("No .json files found in the dropped files.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        var assigned = new List<string>();

        foreach (var file in jsonFiles)
        {
            var type = DetectJsonFileType(file);
            switch (type)
            {
                case "chain":
                    txtChainPath.Text = file;
                    SaveChainPath(file);
                    await _main.LoadDataAsync(file);
                    assigned.Add("chain_item_odds.json");
                    break;
                case "areas":
                    txtAreasPath.Text = file;
                    _main.SetAreasPath(file);
                    assigned.Add("areas.json");
                    break;
                case "events":
                    txtEventsPath.Text = file;
                    _main.SetEventsPath(file);
                    assigned.Add("events.json");
                    break;
                case "dialogues":
                    txtDialoguesPath.Text = file;
                    _main.SetDialoguesPath(file);
                    assigned.Add("dialogues.json");
                    break;
                case "pets":
                    txtPetsPath.Text = file;
                    _main.SetPetsPath(file);
                    assigned.Add("Pets.json");
                    break;
                case "card_collection":
                    txtCardCollectionPath.Text = file;
                    _main.SetCardCollectionPath(file);
                    assigned.Add("card_collection.json");
                    break;
            }
        }

        if (assigned.Count > 0)
            _main.ShowStatus($"Assigned {assigned.Count} file{(assigned.Count > 1 ? "s" : "")}: {string.Join(", ", assigned)}", Wpf.Ui.Controls.InfoBarSeverity.Success);
        else
            _main.ShowStatus("Could not detect any known dump files.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
    }

    private static string? DetectJsonFileType(string path)
    {
        // 1. Filename-based detection (fast, reliable for well-known names)
        var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        if (fileName.Contains("chain_item_odds", StringComparison.OrdinalIgnoreCase))
            return "chain";
        if (fileName.Equals("areas", StringComparison.OrdinalIgnoreCase))
            return "areas";
        if (fileName.Equals("events", StringComparison.OrdinalIgnoreCase))
            return "events";
        if (fileName.Equals("dialogues", StringComparison.OrdinalIgnoreCase))
            return "dialogues";
        if (fileName.Equals("Pets", StringComparison.OrdinalIgnoreCase))
            return "pets";
        if (fileName.Contains("card_collection", StringComparison.OrdinalIgnoreCase))
            return "card_collection";

        // 2. Content-based fallback (for renamed files)
        try
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            var buffer = new byte[4096];
            int read = fs.Read(buffer, 0, buffer.Length);
            var snippet = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

            if (snippet.Contains("\"PrimaryChain\"") || snippet.Contains("\"ConfigKey\""))
                return "chain";
            if (snippet.Contains("\"TaskDependencies\"") || snippet.Contains("\"HotspotsRefs\""))
                return "areas";
            if (snippet.Contains("\"Progressions\"") || snippet.Contains("\"SP_"))
                return "events";
            if (snippet.Contains("\"Dialogues\"") || snippet.Contains("\"DialogItemId\""))
                return "dialogues";
            if (snippet.Contains("\"PetId\"") || snippet.Contains("\"SelectionHeader\""))
                return "pets";
            if (snippet.Contains("\"CardCollection\""))
                return "card_collection";
        }
        catch { }

        return null;
    }

    private static string? GetDroppedFilePath(DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        return files?.Length > 0 ? files[0] : null;
    }

    // ── Browse buttons ──

    private async void BrowseChainFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select chain_item_odds.json");
        if (path != null)
        {
            txtChainPath.Text = path;
            SaveChainPath(path);
            await _main.LoadDataAsync(path);
        }
    }

    private void BrowseAreasFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select areas.json");
        if (path != null)
        {
            txtAreasPath.Text = path;
            _main.SetAreasPath(path);
        }
    }

    private void BrowseEventsFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select events.json");
        if (path != null)
        {
            txtEventsPath.Text = path;
            _main.SetEventsPath(path);
        }
    }

    // ── TinyPNG API Keys (auto-save on change) ──

    private void TinifyKey_TextChanged(object sender, TextChangedEventArgs e)
    {
        _main.Settings.TinifyApiKey = txtTinifyKey.Text.Trim();
        _main.SaveSettings();
        _main.RaiseTinifyApiKeyChanged();
        TriggerTinifyCheck(1, _main.Settings.TinifyApiKey);
    }

    private void TinifyKey2_TextChanged(object sender, TextChangedEventArgs e)
    {
        _main.Settings.TinifyApiKey2 = txtTinifyKey2.Text.Trim();
        _main.SaveSettings();
        _main.RaiseTinifyApiKeyChanged();
        TriggerTinifyCheck(2, _main.Settings.TinifyApiKey2);
    }

    // ── TinyPNG usage bar ──

    private void TriggerTinifyCheck(int idx, string key, int delayMs = 800)
    {
        if (idx == 1)
        {
            _tinifyCheck1Cts?.Cancel();
            if (string.IsNullOrWhiteSpace(key)) { HideTinifyUsage(1); return; }
            _tinifyCheck1Cts = StartTinifyCheck(1, key, delayMs);
        }
        else
        {
            _tinifyCheck2Cts?.Cancel();
            if (string.IsNullOrWhiteSpace(key)) { HideTinifyUsage(2); return; }
            _tinifyCheck2Cts = StartTinifyCheck(2, key, delayMs);
        }
    }

    private CancellationTokenSource StartTinifyCheck(int idx, string key, int delayMs)
    {
        ShowTinifyLoading(idx);
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, token);
                await CheckTinifyUsageAsync(idx, key, token);
            }
            catch (OperationCanceledException) { }
        });
        return cts;
    }

    private async Task CheckTinifyUsageAsync(int idx, string key, CancellationToken token)
    {
        var (count, error) = await TinifyChecker.CheckAsync(key, token);
        if (error != null)
            Dispatcher.Invoke(() => ShowTinifyError(idx, error));
        else
            Dispatcher.Invoke(() => ShowTinifyUsage(idx, count));
    }

    private void ShowTinifyLoading(int idx)
    {
        var (grid, bar, status, count) = GetTinifyControls(idx);
        grid.Visibility = Visibility.Visible;
        status.Text = "Checking...";
        status.ClearValue(TextBlock.ForegroundProperty);
        count.Text = "";
        bar.IsIndeterminate = true;
    }

    private void ShowTinifyUsage(int idx, int used)
    {
        var (grid, bar, status, count) = GetTinifyControls(idx);
        const int limit = 500;
        grid.Visibility = Visibility.Visible;
        status.Text = "Compressions this month:";
        status.ClearValue(TextBlock.ForegroundProperty);
        count.Text = $"{used} / {limit}";
        bar.IsIndeterminate = false;
        bar.Value = used;
        bar.Foreground = TinifyChecker.CreateUsageBrush(used);
    }

    private void ShowTinifyError(int idx, string message)
    {
        var (grid, bar, status, count) = GetTinifyControls(idx);
        grid.Visibility = Visibility.Visible;
        status.Text = message;
        status.Foreground = new SolidColorBrush(Colors.OrangeRed);
        count.Text = "";
        bar.IsIndeterminate = false;
        bar.Value = 0;
    }

    private void HideTinifyUsage(int idx)
    {
        var (grid, _, _, _) = GetTinifyControls(idx);
        grid.Visibility = Visibility.Collapsed;
    }

    private (Grid grid, ProgressBar bar, TextBlock status, TextBlock count) GetTinifyControls(int idx)
        => idx == 1
            ? (gridTinify1Usage, barTinify1, txtTinify1Status, txtTinify1Count)
            : (gridTinify2Usage, barTinify2, txtTinify2Status, txtTinify2Count);




    // ── Clipboard settings ──

    private void ToggleClipboardAutoAdd_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _main.Settings.ClipboardAutoAdd = toggleClipboardAutoAdd.IsChecked == true;
        gridClipboardGlobal.Visibility = _main.Settings.ClipboardAutoAdd ? Visibility.Visible : Visibility.Collapsed;
        if (!_main.Settings.ClipboardAutoAdd)
        {
            _main.Settings.ClipboardMonitorGlobal = false;
            toggleClipboardGlobal.IsChecked = false;
        }
        _main.SaveSettings();
    }

    private void ToggleClipboardGlobal_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _main.Settings.ClipboardMonitorGlobal = toggleClipboardGlobal.IsChecked == true;
        _main.SaveSettings();
    }

    private void ToggleClearOptimiserOnChainEntry_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _main.Settings.ClearOptimiserOnChainEntry = toggleClearOptimiserOnChainEntry.IsChecked == true;
        _main.SaveSettings();
    }

    private void ToggleDumpAutoNewFolder_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _main.Settings.DumpAutoNewFolder = toggleDumpAutoNewFolder.IsChecked == true;
        _main.SaveSettings();
    }

    // ── Updates ──

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        btnCheckUpdates.IsEnabled = false;
        txtUpdateStatus.Text = "Checking...";

        try
        {
            var release = await Services.UpdateService.CheckForUpdateAsync();
            if (release != null)
            {
                txtUpdateStatus.Text = $"New version available: {release.TagName}";
                _main.ShowUpdateDialog(release);
            }
            else
            {
                txtUpdateStatus.Text = $"You're up to date ({Models.AppVersion.Version})";
                txtUpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
            }
        }
        catch (Exception ex)
        {
            txtUpdateStatus.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            btnCheckUpdates.IsEnabled = true;
        }
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

    public void HighlightChainSection()    { SwitchToTab(0); HighlightBorder(chainSectionBorder); }
    public void HighlightAreasSection()    { SwitchToTab(0); HighlightBorder(areasSectionBorder); }
    public void HighlightEventsSection()   { SwitchToTab(0); HighlightBorder(eventsSectionBorder); }
    // HighlightChunkSizes removed — area chunking is now dynamic

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
        // Reset to Transparent — preserves hit-testing (ClearValue sets null → breaks drag-drop)
        anim.Completed += (_, _) => border.Background = Brushes.Transparent;

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

    // ── Area Flowcharts — output folder ──

    public void HighlightApkSection()           { SwitchToTab(0); HighlightBorder(apkSectionBorder); }
    public void HighlightWikiMappingSection()  { SwitchToTab(2); HighlightBorder(wikiMappingSectionBorder); }

    // ── Wiki Mapping ──

    public void UpdateWikiMappingStatus()
    {
        var cache = _main.WikiMapping;
        if (cache != null && cache.Mappings.Count > 0)
        {
            var local = cache.FetchedAt.ToLocalTime();
            txtWikiMappingStatus.Text = $"{cache.Mappings.Count} entries · last updated {local:d. M. yyyy H:mm}";
        }
        else
        {
            txtWikiMappingStatus.Text = "No cache available.";
        }
    }

    private bool _suppressBotCredentialReset;

    private void WikiBotCredentials_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _main.Settings.WikiUsername = txtWikiBotUsername.Text.Trim();
        if (!_suppressBotCredentialReset)
        {
            _main.Settings.WikiVerified = false;
            _main.Settings.WikiVerifiedDisplayName = "";
            UpdateBotStatus();
        }
        _main.SaveSettings();
    }

    private void WikiBotPassword_Changed(object sender, RoutedEventArgs e)
    {
        _main.Settings.WikiPassword = txtWikiBotPassword.Password;
        if (!_suppressBotCredentialReset)
        {
            _main.Settings.WikiVerified = false;
            _main.Settings.WikiVerifiedDisplayName = "";
            UpdateBotStatus();
        }
        _main.SaveSettings();
    }

    private void UpdateBotStatus()
    {
        if (_main.Settings.WikiVerified)
        {
            txtWikiBotStatus.Text = $"Verified as {_main.Settings.WikiVerifiedDisplayName}";
            txtWikiBotStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
        }
        else if (!string.IsNullOrEmpty(_main.Settings.WikiUsername))
        {
            txtWikiBotStatus.Text = "Not verified";
            txtWikiBotStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        }
        else
        {
            txtWikiBotStatus.Text = "";
        }
    }

    private async void BtnVerifyBot_Click(object sender, RoutedEventArgs e)
    {
        var username = txtWikiBotUsername.Text.Trim();
        var password = txtWikiBotPassword.Password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _main.ShowStatus("Enter your Fandom username and password.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        btnVerifyBot.IsEnabled = false;
        txtWikiBotStatus.Text = "Verifying...";
        txtWikiBotStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        try
        {
            var confirmedName = await WikiMappingService.VerifyLoginAsync(username, password);

            if (confirmedName != null)
            {
                _main.Settings.WikiVerified = true;
                _main.Settings.WikiVerifiedDisplayName = confirmedName;
                _main.ShowStatus($"Wiki account verified as {confirmedName}.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            else
            {
                _main.Settings.WikiVerified = false;
                _main.Settings.WikiVerifiedDisplayName = "";
                _main.ShowStatus("Login failed — check username and password.", Wpf.Ui.Controls.InfoBarSeverity.Error);
            }

            _main.SaveSettings();
            UpdateBotStatus();
            _main.RaiseWikiVerifiedChanged();
        }
        catch (Exception ex)
        {
            _main.Settings.WikiVerified = false;
            _main.Settings.WikiVerifiedDisplayName = "";
            _main.SaveSettings();
            UpdateBotStatus();
            _main.RaiseWikiVerifiedChanged();
            _main.ShowStatus($"Verification failed: {ex.Message}", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            btnVerifyBot.IsEnabled = true;
        }
    }

    private BotSetupGuideDialog? _guideDialog;

    private void BtnBotGuide_Click(object sender, RoutedEventArgs e)
    {
        if (_guideDialog != null && _guideDialog.IsLoaded)
        {
            _guideDialog.Activate();
            return;
        }

        _guideDialog = new BotSetupGuideDialog();
        _guideDialog.Owner = Window.GetWindow(this);
        _guideDialog.Closed += (_, _) => _guideDialog = null;
        _guideDialog.Show();
    }

    private async void BtnRefreshMapping_Click(object sender, RoutedEventArgs e)
    {
        btnRefreshMapping.IsEnabled = false;
        txtWikiMappingStatus.Text = "Fetching from wiki...";

        try
        {
            await _main.RefreshWikiMappingAsync();
            UpdateWikiMappingStatus();
            _main.ShowStatus("Wiki mapping cache refreshed.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            txtWikiMappingStatus.Text = "Refresh failed.";
            _main.ShowStatus($"Wiki mapping refresh failed: {ex.Message}", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            btnRefreshMapping.IsEnabled = true;
        }
    }



    // ── APK Download ──

    private CancellationTokenSource? _apkDownloadCts;

    private async Task LoadApkVersionsAsync()
    {
        try
        {
            cmbApkVersion.ItemsSource = new[] { "Loading..." };
            cmbApkVersion.SelectedIndex = 0;
            cmbApkVersion.IsEnabled = false;

            // Use pre-loaded data from splash if available, otherwise fetch fresh
            if (App.PreloadedVersionsTask != null)
            {
                _apkVersions = await App.PreloadedVersionsTask;
                App.PreloadedVersionsTask = null;
            }
            else
            {
                _apkVersions = await Task.Run(() => ApkDownloadService.FetchAvailableVersionsAsync());
            }

            _main.ApkVersions = _apkVersions;
            cmbApkVersion.ItemsSource = BuildVersionList();

            // Restore persisted version selection
            var saved = _main.Settings.SelectedApkVersion;
            int restoredIdx = 0;
            if (!string.IsNullOrEmpty(saved) && _apkVersions.Count > 0)
            {
                for (int i = 0; i < _apkVersions.Count; i++)
                {
                    if (_apkVersions[i].Version == saved) { restoredIdx = i; break; }
                }
            }
            cmbApkVersion.SelectedIndex = restoredIdx;

            // Always persist the resolved version so Image Extractor can use it
            if (_apkVersions.Count > 0)
            {
                _main.Settings.SelectedApkVersion = _apkVersions[restoredIdx].Version;
                _main.SaveSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[APK Versions] Fetch failed: {ex}");
            cmbApkVersion.ItemsSource = new[] { "Latest" };
            cmbApkVersion.SelectedIndex = 0;
            _apkVersions = null;
            txtApkDownloadStatus.Text = $"Version list failed: {ex.Message}";
            txtApkDownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        }
        finally
        {
            cmbApkVersion.IsEnabled = true;
            UpdateApkFolderButton();
            // Show wizard download status if one is in progress
            ShouldShowWizardDownload();
        }
    }

    private void CmbApkVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _lastApkDir = null;
        UpdateApkFolderButton();
        CheckApkExistence();

        // Persist selected version + update download button state
        var idx = cmbApkVersion.SelectedIndex;
        if (idx >= 0 && _apkVersions != null && idx < _apkVersions.Count)
        {
            _main.Settings.SelectedApkVersion = _apkVersions[idx].Version;
            _main.SaveSettings();
            _main.RaiseApkVersionChanged();

            // Disable download for versions not available on APKPure
            var canDownload = _apkVersions[idx].CanDownload;
            btnDownloadApk.IsEnabled = canDownload;
            btnDownloadApk.ToolTip = canDownload
                ? null
                : $"v{_apkVersions[idx].Version} is not available for automatic download.";
        }

        // Refresh available dumps for the new version
        _ = ScanLocalDumpsAsync();
    }

    private void CheckApkExistence()
    {
        // If a wizard-initiated download is active and matches, show its status instead
        if (ShouldShowWizardDownload())
            return;

        var basePath = _main.Settings.ImageExporterBasePath;
        var idx = cmbApkVersion.SelectedIndex;
        if (string.IsNullOrWhiteSpace(basePath) || idx < 0 || _apkVersions == null || idx >= _apkVersions.Count)
        {
            txtApkDownloadStatus.Text = "";
            return;
        }

        var ver = _apkVersions[idx].Version;
        var dir = System.IO.Path.Combine(basePath, ver);

        if (!System.IO.Directory.Exists(dir) ||
            !System.IO.Directory.EnumerateFiles(dir, "*.apk").Any() &&
            !System.IO.Directory.EnumerateFiles(dir, "*.xapk").Any())
        {
            if (_apkVersions[idx].CanDownload)
            {
                txtApkDownloadStatus.Text = $"No APK found for v{ver} — download it first.";
                txtApkDownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            }
            else
            {
                txtApkDownloadStatus.Text = $"No APK found for v{ver}. This version is not available for automatic download.";
                txtApkDownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            }
        }
        else
        {
            txtApkDownloadStatus.Text = "";
        }
    }

    /// <summary>
    /// Returns true and shows wizard download status if the currently selected version matches
    /// an active OOBE download (always shown for "Latest", otherwise only when version matches).
    /// </summary>
    private bool ShouldShowWizardDownload()
    {
        if (SetupWizard.ActiveDownloadVersion == null) return false;
        if (!SetupWizard.ActiveDownloadRunning && string.IsNullOrEmpty(_wizardDownloadLastStatus)) return false;

        var idx = cmbApkVersion.SelectedIndex;
        var selectedVer = idx >= 0 && _apkVersions != null && idx < _apkVersions.Count
            ? _apkVersions[idx].Version : null;

        bool show = SetupWizard.ActiveDownloadIsLatest
            || selectedVer == SetupWizard.ActiveDownloadVersion;

        if (show && !string.IsNullOrEmpty(_wizardDownloadLastStatus))
        {
            txtApkDownloadStatus.Text = _wizardDownloadLastStatus;
            if (_wizardDownloadLastBrush != null)
                txtApkDownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, _wizardDownloadLastBrush);
            return true;
        }

        return false;
    }

    private string? _wizardDownloadLastStatus;
    private string? _wizardDownloadLastBrush;

    private void OnWizardDownloadStatus(string statusText, string? brushKey)
    {
        _wizardDownloadLastStatus = statusText;
        _wizardDownloadLastBrush = brushKey;

        try
        {
            Dispatcher.Invoke(() =>
            {
                if (ShouldShowWizardDownload())
                {
                    // Already updated by ShouldShowWizardDownload
                }

                // After download finishes, also refresh folder button
                if (!SetupWizard.ActiveDownloadRunning)
                    UpdateApkFolderButton();
            });
        }
        catch { }
    }

    private async void BtnRefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        btnRefreshVersions.IsEnabled = false;
        try
        {
            await LoadApkVersionsAsync();
        }
        finally
        {
            btnRefreshVersions.IsEnabled = true;
        }
    }

    private string? GetSelectedVersionDir()
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrWhiteSpace(basePath)) return null;

        // After download, _lastApkDir is set directly
        if (!string.IsNullOrEmpty(_lastApkDir) && System.IO.Directory.Exists(_lastApkDir))
            return _lastApkDir;

        // Check if the selected version's folder exists
        var idx = cmbApkVersion.SelectedIndex;
        if (idx >= 0 && _apkVersions != null && idx < _apkVersions.Count)
        {
            var ver = _apkVersions[idx].Version;
            var dir = System.IO.Path.Combine(basePath, ver);
            if (System.IO.Directory.Exists(dir)) return dir;
        }

        return null;
    }

    private void UpdateApkFolderButton()
    {
        var dir = GetSelectedVersionDir();
        btnOpenApkFolder.Visibility = dir != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnOpenApkFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetSelectedVersionDir();
        if (dir != null)
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private async void BtnDownloadApk_Click(object sender, RoutedEventArgs e)
    {
        var basePath = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            _main.ShowStatus("Set your Workspace folder first (General tab).", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (!System.IO.Directory.Exists(basePath))
        {
            _main.ShowStatus("Workspace folder does not exist.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        // Determine if a specific version is selected
        var selectedIdx = cmbApkVersion.SelectedIndex;
        var useSpecificVersion = selectedIdx > 0 && _apkVersions != null && selectedIdx < _apkVersions.Count;
        var selectedVersion = useSpecificVersion ? _apkVersions![selectedIdx] : null;

        btnDownloadApk.IsEnabled = false;
        btnCancelDownload.Visibility = Visibility.Visible;
        _apkDownloadCts = new CancellationTokenSource();

        try
        {
            (string version, string filePath) result;

            if (selectedVersion != null)
            {
                result = await Task.Run(() =>
                    ApkDownloadService.DownloadVersionAsync(
                        basePath, selectedVersion,
                        status => Dispatcher.Invoke(() => txtApkDownloadStatus.Text = status),
                        _apkDownloadCts.Token),
                    _apkDownloadCts.Token);
            }
            else
            {
                result = await Task.Run(() =>
                    ApkDownloadService.DownloadLatestAsync(
                        basePath,
                        status => Dispatcher.Invoke(() => txtApkDownloadStatus.Text = status),
                        _apkDownloadCts.Token),
                    _apkDownloadCts.Token);
            }

            var sizeMb = new System.IO.FileInfo(result.filePath).Length / 1024.0 / 1024.0;
            txtApkDownloadStatus.Text = $"Done! v{result.version} ({sizeMb:F1} MB)";
            txtApkDownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
            _main.ShowStatus($"APK downloaded: v{result.version}\n→ {result.filePath}",
                Wpf.Ui.Controls.InfoBarSeverity.Success);

            _lastApkDir = System.IO.Path.GetDirectoryName(result.filePath);
            UpdateApkFolderButton();
            _main.RaiseApkVersionChanged();
        }
        catch (OperationCanceledException)
        {
            txtApkDownloadStatus.Text = "Download cancelled.";
            txtApkDownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
        catch (Exception ex)
        {
            txtApkDownloadStatus.Text = "Download failed.";
            txtApkDownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            _main.ShowStatus($"APK download failed: {ex.Message}", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            btnDownloadApk.IsEnabled = true;
            btnCancelDownload.Visibility = Visibility.Collapsed;
            _apkDownloadCts?.Dispose();
            _apkDownloadCts = null;
        }
    }

    private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _apkDownloadCts?.Cancel();
    }

    // ── Image Extractor (Advanced) ──

    private void ToggleExtractBuiltIn_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _main.Settings.ExtractIncludeBuiltIn = toggleExtractBuiltIn.IsChecked == true;
        _main.SaveSettings();
    }

    /// <summary>Throws an unhandled exception on the UI thread to exercise the crash reporter
    /// end-to-end (caught by App's DispatcherUnhandledException hook → GitHub issue). Confirms
    /// first because this terminates the app.</summary>
    private async void BtnTestCrash_Click(object sender, RoutedEventArgs e)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Test crash reporter",
            Content = "This throws a test exception to verify the GitHub crash reporter.\n\n" +
                      "A test issue (label: auto-crash) should appear on the repo within a few seconds, " +
                      "then the app will close. Continue?",
            PrimaryButtonText = "Trigger crash",
            CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        // Defer past the dialog's message pump so the throw is a genuine unhandled UI-thread
        // exception (caught by App.DispatcherUnhandledException → CrashReporterService).
        Dispatcher.BeginInvoke(new Action(() =>
            throw new InvalidOperationException(
                "Synthetic test crash from Settings → Test crash reporter (intentional).")));
    }

    // ── Debug Mode ──

    private void ToggleDebugMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _main.Settings.DebugMode = toggleDebugMode.IsChecked == true;
        _main.SaveSettings();
    }

    private void ToggleDetectionIndices_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _main.Settings.ShowDetectionIndices = toggleDetectionIndices.IsChecked == true;
        _main.SaveSettings();
    }

    // Area chunk sizes removed — chunking is now dynamic based on byte size

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

    // ── Active Dump selector ──

    private List<string> _activeDumpFolders = [];

    private async Task ScanLocalDumpsAsync()
    {
        cmbActiveDump.Items.Clear();
        _activeDumpFolders.Clear();

        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version))
        {
            cmbActiveDump.Items.Add("No version selected");
            cmbActiveDump.SelectedIndex = 0;
            cmbActiveDump.IsEnabled = false;
            return;
        }

        var versionDir = Path.Combine(basePath, version);
        var folders = DiscordDumpDownloadService.ScanDumpFolders(versionDir);

        if (folders.Count == 0)
        {
            cmbActiveDump.Items.Add("No dumps found");
            cmbActiveDump.SelectedIndex = 0;
            cmbActiveDump.IsEnabled = false;
            return;
        }

        foreach (var (name, createdAt) in folders)
        {
            _activeDumpFolders.Add(name);
            var dateStr = createdAt != null ? $" ({createdAt[..Math.Min(10, createdAt.Length)]})" : "";
            cmbActiveDump.Items.Add($"{name}{dateStr}");
        }

        var saved = _main.Settings.ActiveDumpFolder;
        var matchIdx = _activeDumpFolders.IndexOf(saved);
        cmbActiveDump.SelectedIndex = matchIdx >= 0 ? matchIdx : 0;
        cmbActiveDump.IsEnabled = true;
    }

    private async void BtnSetActiveDump_Click(object sender, RoutedEventArgs e)
    {
        if (cmbActiveDump.SelectedIndex < 0 || cmbActiveDump.SelectedIndex >= _activeDumpFolders.Count)
            return;

        var folderName = _activeDumpFolders[cmbActiveDump.SelectedIndex];
        _main.Settings.ActiveDumpFolder = folderName;
        _main.SaveSettings();

        var basePath = _main.Settings.ImageExporterBasePath;
        var version = _main.Settings.SelectedApkVersion;
        if (!string.IsNullOrEmpty(basePath) && !string.IsNullOrEmpty(version))
        {
            var dumpDir = Path.Combine(basePath, version, folderName);
            if (Directory.Exists(dumpDir))
                await AssignDumpFilePathsAsync(dumpDir);
        }
    }

    /// <summary>
    /// Waits for APK versions to load (up to 15s), then refreshes Discord dropdown with version labels.
    /// </summary>
    private async Task RefreshDiscordVersionsWhenReadyAsync()
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            if (_apkVersions != null && _apkVersions.Count > 0) break;
        }

        if (_apkVersions == null || _apkVersions.Count == 0 || _discordDumps == null) return;

        // Rebuild dropdown items with version labels
        var localTimestamps = DiscordDumpDownloadService.ScanLocalCreatedAtTimestamps(
            _main.Settings.ImageExporterBasePath);

        cmbDiscordDumps.Items.Clear();
        foreach (var dump in _discordDumps)
        {
            var effectiveDate = dump.DataCreatedAt ?? dump.MessageTimestamp;
            var dateStr = effectiveDate.ToString("yyyy-MM-dd");
            var resolvedVer = ResolveDumpVersion(dump, _apkVersions);
            var versionStr = resolvedVer != null ? $" → v{resolvedVer}" : "";
            var checkmark = localTimestamps.Contains(effectiveDate) ? " ✔" : "";
            var prefix = dump.DataCreatedAt == null ? "~" : "";
            cmbDiscordDumps.Items.Add($"{prefix}{dateStr}{versionStr}{checkmark}");
        }
        if (_discordDumps.Count > 0)
            cmbDiscordDumps.SelectedIndex = 0;
    }

    /// <summary>
    /// Subscribes to splash pre-loaded tasks. When each completes, applies data to UI on dispatcher.
    /// Never re-fetches, never blocks UI. Each task continuation runs independently.
    /// </summary>
    private void ApplyPreloadedDataWhenReady()
    {
        AppLogger.Info("[Settings] ApplyPreloadedDataWhenReady START");
        // Versions — show "Loading..." until splash task completes
        if (App.PreloadedVersionsTask != null)
        {
            // Always apply via Dispatcher — never block constructor even if task is already complete
            App.PreloadedVersionsTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    Dispatcher.InvokeAsync(() => ApplyVersionData(t.Result));
            }, TaskScheduler.Default);
        }
        else
        {
            // No preload — fetch in background
            _ = LoadApkVersionsAsync();
        }

        // Discord dumps — always apply via Dispatcher
        if (App.PreloadedDiscordDumpsTask != null)
        {
            App.PreloadedDiscordDumpsTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    Dispatcher.InvokeAsync(() => ApplyDiscordDumpData(t.Result));
            }, TaskScheduler.Default);
        }
        else
        {
            _ = LoadDiscordDumpsAsync();
        }

        // Local dump folders (disk scan — fast, run on threadpool)
        Task.Run(() =>
        {
            var basePath = _main.Settings.ImageExporterBasePath;
            var version = _main.Settings.SelectedApkVersion;
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(version)) return;
            var versionDir = System.IO.Path.Combine(basePath, version);
            var folders = DiscordDumpDownloadService.ScanDumpFolders(versionDir);
            Dispatcher.InvokeAsync(() => ApplyDumpFolderData(folders));
        });
    }

    private List<string> BuildVersionList()
    {
        var showDates = chkShowReleaseDates.IsChecked == true;
        var items = new List<string>();
        if (_apkVersions == null || _apkVersions.Count == 0)
        {
            items.Add("Latest");
            return items;
        }
        // The "Latest" entry must append its date too (it was omitted, so the newest version
        // showed no date even when one was available).
        var latestDate = showDates && _apkVersions[0].ReleaseDate.HasValue
            ? $"  ({_apkVersions[0].ReleaseDate.Value:yyyy-MM-dd})" : "";
        items.Add($"Latest ({_apkVersions[0].Version}){latestDate}");
        for (int i = 1; i < _apkVersions.Count; i++)
        {
            var v = _apkVersions[i];
            var dateStr = showDates && v.ReleaseDate.HasValue
                ? $"  ({v.ReleaseDate.Value:yyyy-MM-dd})" : "";
            items.Add($"{v.Version}{dateStr}");
        }
        return items;
    }

    private void ApplyVersionData(List<ApkDownloadService.ApkVersionInfo> versions)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppLogger.Info($"[Settings] ApplyVersionData START ({versions.Count} versions)");
        _apkVersions = versions;
        _main.ApkVersions = versions;
        cmbApkVersion.ItemsSource = BuildVersionList();
        AppLogger.Info($"[Settings] ApplyVersionData ItemsSource set: {sw.ElapsedMilliseconds}ms");

        var saved = _main.Settings.SelectedApkVersion;
        int restoredIdx = 0;
        if (!string.IsNullOrEmpty(saved) && _apkVersions.Count > 0)
            for (int i = 0; i < _apkVersions.Count; i++)
                if (_apkVersions[i].Version == saved) { restoredIdx = i; break; }

        cmbApkVersion.SelectedIndex = restoredIdx;
        cmbApkVersion.IsEnabled = true;

        if (_apkVersions.Count > 0)
        {
            _main.Settings.SelectedApkVersion = _apkVersions[restoredIdx].Version;
            _main.SaveSettings();
        }

        AppLogger.Info($"[Settings] ApplyVersionData SelectedIndex set: {sw.ElapsedMilliseconds}ms");
        UpdateApkFolderButton();
        CheckApkExistence();
        AppLogger.Info($"[Settings] ApplyVersionData END: {sw.ElapsedMilliseconds}ms");
    }

    private async void ApplyDiscordDumpData(List<DiscordDumpDownloadService.DiscordDumpInfo> dumps)
    {
        AppLogger.Info($"[Settings] ApplyDiscordDumpData START ({dumps.Count} dumps)");
        _discordDumps = dumps;

        // Wait for versions if not loaded yet (max 10s)
        if (_apkVersions == null && App.PreloadedVersionsTask != null)
        {
            try { _apkVersions = await App.PreloadedVersionsTask; _main.ApkVersions = _apkVersions; }
            catch { }
        }

        var versions = _apkVersions;
        var basePath = _main.Settings.ImageExporterBasePath;
        var items = await Task.Run(() =>
        {
            var localTs = App.PreloadedLocalTimestampsTask is { IsCompletedSuccessfully: true }
                ? App.PreloadedLocalTimestampsTask.Result
                : DiscordDumpDownloadService.ScanLocalCreatedAtTimestamps(basePath);

            var list = new List<string>();
            foreach (var dump in dumps)
            {
                var effectiveDate = dump.DataCreatedAt ?? dump.MessageTimestamp;
                var dateStr = effectiveDate.ToString("yyyy-MM-dd");
                var resolvedVer = ResolveDumpVersion(dump, versions);
                var versionStr = resolvedVer != null ? $" → v{resolvedVer}" : "";
                var checkmark = localTs.Contains(effectiveDate) ? " ✔" : "";
                var prefix = dump.DataCreatedAt == null ? "~" : "";
                list.Add($"{prefix}{dateStr}{versionStr}{checkmark}");
            }
            if (list.Count == 0) list.Add("No dumps found");
            return list;
        });

        AppLogger.Info($"[Settings] ApplyDiscordDumpData list built ({items.Count} items)");
        cmbDiscordDumps.ItemsSource = items;

        if (_discordDumps.Count > 0)
            cmbDiscordDumps.SelectedIndex = 0;

        cmbDiscordDumps.IsEnabled = true;
        txtDumpDownloadStatus.Text = $"Found {_discordDumps.Count} dumps.";

        if (versions == null || versions.Count == 0)
            _ = RefreshDiscordVersionsWhenReadyAsync();
    }

    private void ApplyDumpFolderData(List<(string folderName, string? createdAt)> folders)
    {
        cmbActiveDump.Items.Clear();
        _activeDumpFolders.Clear();

        if (folders.Count == 0)
        {
            cmbActiveDump.Items.Add("No dumps found");
            cmbActiveDump.SelectedIndex = 0;
            cmbActiveDump.IsEnabled = false;
            return;
        }

        foreach (var (name, createdAt) in folders)
        {
            _activeDumpFolders.Add(name);
            var dateStr = createdAt != null ? $" ({createdAt[..Math.Min(10, createdAt.Length)]})" : "";
            cmbActiveDump.Items.Add($"{name}{dateStr}");
        }

        var saved = _main.Settings.ActiveDumpFolder;
        var matchIdx = _activeDumpFolders.IndexOf(saved);
        cmbActiveDump.SelectedIndex = matchIdx >= 0 ? matchIdx : 0;
        cmbActiveDump.IsEnabled = true;
    }

    private void BtnRefreshDumps_Click(object sender, RoutedEventArgs e) => _ = ScanLocalDumpsAsync();

    private void ChkShowReleaseDates_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _apkVersions == null) return;
        var savedVersion = _main.Settings.SelectedApkVersion;
        cmbApkVersion.ItemsSource = BuildVersionList();

        int restoredIdx = 0;
        if (!string.IsNullOrEmpty(savedVersion))
            for (int i = 0; i < _apkVersions.Count; i++)
                if (_apkVersions[i].Version == savedVersion) { restoredIdx = i; break; }
        cmbApkVersion.SelectedIndex = restoredIdx;
    }

    private async Task AssignDumpFilePathsAsync(string dumpDir)
    {
        var fileMappings = new (string fileName, Action<string> setter)[]
        {
            ("chain_item_odds.json", p => { txtChainPath.Text = p; SaveChainPath(p); }),
            ("areas.json", p => { txtAreasPath.Text = p; _main.SetAreasPath(p); }),
            ("events.json", p => { txtEventsPath.Text = p; _main.SetEventsPath(p); }),
            ("dialogues.json", p => { txtDialoguesPath.Text = p; _main.SetDialoguesPath(p); }),
            ("card_collection.json", p => { txtCardCollectionPath.Text = p; _main.SetCardCollectionPath(p); }),
        };

        foreach (var (fileName, setter) in fileMappings)
        {
            var path = Path.Combine(dumpDir, fileName);
            if (File.Exists(path))
                setter(path);
        }

        // Pets.json: check main Dump/ first, fallback to Experimental/
        var petsPath = Path.Combine(dumpDir, "Pets.json");
        if (!File.Exists(petsPath))
            petsPath = Path.Combine(dumpDir, "Experimental", "Pets.json");
        if (File.Exists(petsPath))
        {
            txtPetsPath.Text = petsPath;
            _main.SetPetsPath(petsPath);
        }

        // Warn if Experimental/ is missing (Discord dump without it)
        if (!Directory.Exists(Path.Combine(dumpDir, "Experimental")))
        {
            if (!File.Exists(Path.Combine(dumpDir, "Pets.json")))
                _main.ShowStatus("This dump does not include Experimental/ folder. Pet names may show as IDs.",
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }

        // Reload chain data
        var chainPath = Path.Combine(dumpDir, "chain_item_odds.json");
        if (File.Exists(chainPath))
            await _main.LoadDataAsync(chainPath);
    }

    // ── Discord dump download ──

    private List<DiscordDumpDownloadService.DiscordDumpInfo>? _discordDumps;
    private CancellationTokenSource? _dumpDownloadCts;

    private async void BtnRefreshDiscordDumps_Click(object sender, RoutedEventArgs e)
    {
        btnRefreshDiscordDumps.IsEnabled = false;
        await LoadDiscordDumpsAsync();
        btnRefreshDiscordDumps.IsEnabled = true;
    }

    private async Task LoadDiscordDumpsAsync()
    {
        cmbDiscordDumps.Items.Clear();
        cmbDiscordDumps.Items.Add("Loading...");
        cmbDiscordDumps.SelectedIndex = 0;
        cmbDiscordDumps.IsEnabled = false;
        txtDumpDownloadStatus.Text = "";

        try
        {
            var token = _main.Settings.DiscordBotToken;
            if (string.IsNullOrWhiteSpace(token))
                token = AppSettings.DefaultDiscordBotToken;

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => txtDumpDownloadStatus.Text = msg));

            // Use pre-loaded data from splash if available
            if (App.PreloadedDiscordDumpsTask != null)
            {
                _discordDumps = await App.PreloadedDiscordDumpsTask;
                App.PreloadedDiscordDumpsTask = null; // consumed
            }
            else
            {
                _discordDumps = await DiscordDumpDownloadService.FetchAllDumpMessagesAsync(
                    token, AppSettings.DefaultDiscordChannelId, progress);
            }

            cmbDiscordDumps.Items.Clear();

            // Detect already-downloaded dumps
            var localTimestamps = DiscordDumpDownloadService.ScanLocalCreatedAtTimestamps(
                _main.Settings.ImageExporterBasePath);

            foreach (var dump in _discordDumps)
            {
                // Use DataCreatedAt (from message text), fallback to message posted date
                var effectiveDate = dump.DataCreatedAt ?? dump.MessageTimestamp;
                var dateStr = effectiveDate.ToString("yyyy-MM-dd");
                var resolvedVer = ResolveDumpVersion(dump, _apkVersions);
                var versionStr = resolvedVer != null ? $" → v{resolvedVer}" : "";
                var checkmark = localTimestamps.Contains(effectiveDate) ? " ✔" : "";
                // Mark entries where DataCreatedAt couldn't be parsed (message date used as fallback)
                var prefix = dump.DataCreatedAt == null ? "~" : "";
                cmbDiscordDumps.Items.Add($"{prefix}{dateStr}{versionStr}{checkmark}");

                if (dump.DataCreatedAt == null)
                    AppLogger.Warn($"Discord dump with no CreatedAt: msg={dump.MessageId}, " +
                                   $"posted={dump.MessageTimestamp:O}, file={dump.AttachmentFilename}");
            }

            if (_discordDumps.Count == 0)
                cmbDiscordDumps.Items.Add("No dumps found");
            else
                cmbDiscordDumps.SelectedIndex = 0;

            txtDumpDownloadStatus.Text = $"Found {_discordDumps.Count} dumps.";

            // If versions weren't loaded yet (race condition), refresh once they arrive
            if (_apkVersions == null || _apkVersions.Count == 0)
                _ = RefreshDiscordVersionsWhenReadyAsync();
        }
        catch (Exception ex)
        {
            cmbDiscordDumps.Items.Clear();
            cmbDiscordDumps.Items.Add("Fetch failed");
            txtDumpDownloadStatus.Text = $"Error: {ex.Message}";
            _main.ShowStatus($"Discord dump list failed: {ex.Message}",
                Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            cmbDiscordDumps.IsEnabled = true;
        }
    }

    /// <summary>
    /// Resolves a Discord dump's game version: prefer the version from the dump's comment
    /// ("Game Version: X", v0.23.38+); fall back to date-matching against APK release dates
    /// for older dumps that don't carry the line.
    /// </summary>
    private static string? ResolveDumpVersion(
        DiscordDumpDownloadService.DiscordDumpInfo dump,
        List<ApkDownloadService.ApkVersionInfo>? versions)
    {
        if (!string.IsNullOrEmpty(dump.GameVersion)) return dump.GameVersion;
        if (versions == null) return null;
        var effectiveDate = dump.DataCreatedAt ?? dump.MessageTimestamp;
        return ApkDownloadService.MatchVersionByDate(versions, effectiveDate)?.Version;
    }

    private async void BtnDownloadDump_Click(object sender, RoutedEventArgs e)
    {
        // 1. Validate workspace
        var basePath = _main.Settings.ImageExporterBasePath;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            _main.ShowStatus("Set your Workspace folder first.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        // 2. Get selected dump
        var idx = cmbDiscordDumps.SelectedIndex;
        if (idx < 0 || _discordDumps == null || idx >= _discordDumps.Count) return;
        var dump = _discordDumps[idx];
        if (dump.AttachmentUrl == null) return;

        // 3. Determine version — prefer the version from the Discord comment ("Game Version: X");
        // fall back to date-matching APK release dates for older dumps without the line.
        string? version = ResolveDumpVersion(dump, _apkVersions);
        var effectiveDate = dump.DataCreatedAt ?? dump.MessageTimestamp;

        if (string.IsNullOrEmpty(version))
        {
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Unknown Version",
                Content = $"Could not determine game version for this dump ({effectiveDate:yyyy-MM-dd}).\n\nThe dump will be saved to 'Unknown Version/Dump {effectiveDate:yyyy-MM-dd}'.",
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                Owner = Window.GetWindow(this)
            };
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
            var result = await msgBox.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
            version = "Unknown Version";
        }

        var isUnknown = version == "Unknown Version";

        // 3b. Version-mismatch dialog — if the dump's assigned (date-matched) version differs from
        // the current working version, let the user choose the extraction target + whether to switch.
        bool mismatchDecisionMade = false;
        bool switchWorkingVersion = false;
        var currentWorkingVersion = _main.Settings.SelectedApkVersion;
        if (!isUnknown && !string.IsNullOrEmpty(currentWorkingVersion) &&
            !string.Equals(version, currentWorkingVersion, StringComparison.OrdinalIgnoreCase))
        {
            var existingVersions = Directory.Exists(basePath)
                ? Directory.GetDirectories(basePath)
                    .Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList()
                : new List<string>();
            var targetDlg = new DumpExtractTargetDialog(version, currentWorkingVersion, existingVersions)
            {
                Owner = Window.GetWindow(this)
            };
            if (targetDlg.ShowDialog() != true) return; // cancelled
            version = targetDlg.TargetVersion;
            switchWorkingVersion = targetDlg.SwitchWorkingVersion;
            mismatchDecisionMade = true;
        }

        var versionDir = Path.Combine(basePath, version);

        // 4. Check collision (same CreatedAt already exists locally)
        if (dump.DataCreatedAt.HasValue)
        {
            var existing = DiscordDumpDownloadService.FindExistingDumpByCreatedAt(
                versionDir, dump.DataCreatedAt.Value);
            if (existing != null)
            {
                var msgBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Dump Already Exists",
                    Content = $"A dump with the same data timestamp already exists in:\n{existing}\n\nOverwrite it, create a new folder, or cancel?",
                    PrimaryButtonText = "Overwrite",
                    SecondaryButtonText = "New Folder",
                    CloseButtonText = "Cancel",
                    Owner = Window.GetWindow(this)
                };
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
                var result = await msgBox.ShowDialogAsync();

                if (result == Wpf.Ui.Controls.MessageBoxResult.None) return; // Cancel
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    // Overwrite: delete existing
                    var existDir = Path.Combine(versionDir, existing);
                    if (Directory.Exists(existDir))
                        Directory.Delete(existDir, true);
                }
                // Secondary = new folder (continue with auto-naming)
            }
        }

        // 5. Determine dump folder name
        var dumpFolder = DiscordDumpDownloadService.GetNextDumpFolderName(
            versionDir, dump.DataCreatedAt, isUnknown);

        // 6. Download + Extract
        btnDownloadDump.IsEnabled = false;
        barDumpDownload.Visibility = Visibility.Visible;
        barDumpDownload.IsIndeterminate = true;
        _dumpDownloadCts = new CancellationTokenSource();

        try
        {
            // Phase 1: Download 7z
            txtDumpDownloadStatus.Text = "Downloading…";
            Directory.CreateDirectory(versionDir);
            var archivePath = Path.Combine(versionDir, dump.AttachmentFilename!);

            await DiscordDumpDownloadService.DownloadAttachmentAsync(
                dump.AttachmentUrl, archivePath,
                new Progress<(long dl, long? total)>(p => Dispatcher.Invoke(() =>
                {
                    if (p.total is > 0)
                    {
                        barDumpDownload.IsIndeterminate = false;
                        barDumpDownload.Maximum = p.total.Value;
                        barDumpDownload.Value = p.dl;
                        var pct = (double)p.dl / p.total.Value * 100;
                        txtDumpDownloadStatus.Text = $"Downloading… {pct:F0}%";
                    }
                })),
                _dumpDownloadCts.Token);

            // Phase 2: Extract 7z (keep archive)
            txtDumpDownloadStatus.Text = "Extracting…";
            barDumpDownload.IsIndeterminate = true;
            var extractDir = Path.Combine(versionDir, dumpFolder);
            await DiscordDumpDownloadService.ExtractArchiveAsync(archivePath, extractDir);

            // Phase 3: Offer to set as active
            barDumpDownload.Visibility = Visibility.Collapsed;
            txtDumpDownloadStatus.Text = $"Done! Extracted to {dumpFolder}";
            txtDumpDownloadStatus.SetResourceReference(
                TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");

            if (mismatchDecisionMade)
            {
                // The mismatch dialog already decided the target + whether to switch.
                if (switchWorkingVersion)
                {
                    _main.Settings.SelectedApkVersion = version;
                    _main.Settings.ActiveDumpFolder = dumpFolder;
                    _main.SaveSettings();

                    if (_apkVersions != null)
                    {
                        var versionIdx = _apkVersions.FindIndex(v => v.Version == version);
                        if (versionIdx >= 0)
                            cmbApkVersion.SelectedIndex = versionIdx;
                    }

                    await AssignDumpFilePathsAsync(extractDir);
                    _main.RaiseApkVersionChanged();
                }
                // else: extracted to the chosen version without switching — leave working version as-is.
            }
            else
            {
                var setActiveBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Download Complete",
                    Content = $"Dump (v{version}) extracted to {Path.GetFileName(basePath)}/{version}/{dumpFolder}/",
                    PrimaryButtonText = "Switch version and load",
                    SecondaryButtonText = "Load files only",
                    CloseButtonText = "Keep files",
                    MinWidth = 450,
                    Owner = Window.GetWindow(this)
                };
                setActiveBox.Loaded += (sender, _) =>
                {
                    try
                    {
                        var box = (Wpf.Ui.Controls.MessageBox)sender;
                        if (box.Template.FindName("PrimaryColumn", box) is ColumnDefinition primaryCol)
                            primaryCol.Width = new GridLength(1.6, GridUnitType.Star);
                    }
                    catch { }
                };
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(setActiveBox);
                var setResult = await setActiveBox.ShowDialogAsync();

                if (setResult == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    // Switch Game Version + load dump files
                    _main.Settings.SelectedApkVersion = version;
                    _main.Settings.ActiveDumpFolder = dumpFolder;
                    _main.SaveSettings();

                    if (_apkVersions != null)
                    {
                        var versionIdx = _apkVersions.FindIndex(v => v.Version == version);
                        if (versionIdx >= 0)
                            cmbApkVersion.SelectedIndex = versionIdx;
                    }

                    await AssignDumpFilePathsAsync(extractDir);
                    _main.RaiseApkVersionChanged();
                }
                else if (setResult == Wpf.Ui.Controls.MessageBoxResult.Secondary)
                {
                    // Load dump files without switching version
                    _main.Settings.ActiveDumpFolder = dumpFolder;
                    _main.SaveSettings();
                    await AssignDumpFilePathsAsync(extractDir);
                }
            }

            await ScanLocalDumpsAsync();

            _main.ShowStatus($"Dump downloaded and extracted to {version}/{dumpFolder}",
                Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            txtDumpDownloadStatus.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            txtDumpDownloadStatus.Text = $"Failed: {ex.Message}";
            _main.ShowStatus($"Dump download failed: {ex.Message}",
                Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            btnDownloadDump.IsEnabled = true;
            barDumpDownload.Visibility = Visibility.Collapsed;
            txtDumpDownloadStatus.SetResourceReference(
                TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _dumpDownloadCts?.Dispose();
            _dumpDownloadCts = null;
        }
    }

    // ── Browse/Drop handlers for new dump files ──

    private void BrowseDialoguesFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select dialogues.json");
        if (path != null) { txtDialoguesPath.Text = path; _main.SetDialoguesPath(path); }
    }

    private void BrowsePetsFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select Pets.json");
        if (path != null) { txtPetsPath.Text = path; _main.SetPetsPath(path); }
    }

    private void BrowseCardCollectionFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select card_collection.json");
        if (path != null) { txtCardCollectionPath.Text = path; _main.SetCardCollectionPath(path); }
    }

    private void DialoguesFileDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path != null) { txtDialoguesPath.Text = path; _main.SetDialoguesPath(path); }
        e.Handled = true;
    }

    private void PetsFileDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path != null) { txtPetsPath.Text = path; _main.SetPetsPath(path); }
        e.Handled = true;
    }

    private void CardCollectionFileDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path != null) { txtCardCollectionPath.Text = path; _main.SetCardCollectionPath(path); }
        e.Handled = true;
    }

    private static void FindAllDescendants<T>(DependencyObject parent, List<T> results) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) results.Add(match);
            FindAllDescendants(child, results);
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match && (predicate == null || predicate(match)))
                return match;
            var found = FindDescendant(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
