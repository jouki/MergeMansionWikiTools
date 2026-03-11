using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
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

    public SettingsPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        _tabPanels = [panelGeneral, panelTools, panelWiki, panelAdvanced];

        // Load saved paths
        txtChainPath.Text = _main.Settings.ChainItemOddsPath;
        txtAreasPath.Text = _main.Settings.AreasJsonPath;
        txtEventsPath.Text = _main.Settings.EventsJsonPath;
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

        // APK version list (fire-and-forget, non-blocking)
        _ = LoadApkVersionsAsync();
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
    }

    private void TinifyKey2_TextChanged(object sender, TextChangedEventArgs e)
    {
        _main.Settings.TinifyApiKey2 = txtTinifyKey2.Text.Trim();
        _main.SaveSettings();
        _main.RaiseTinifyApiKeyChanged();
    }




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
            cmbApkVersion.Items.Clear();
            cmbApkVersion.Items.Add("Loading...");
            cmbApkVersion.SelectedIndex = 0;
            cmbApkVersion.IsEnabled = false;

            _apkVersions = await Task.Run(() => ApkDownloadService.FetchAvailableVersionsAsync());

            cmbApkVersion.Items.Clear();
            if (_apkVersions.Count > 0)
            {
                cmbApkVersion.Items.Add($"Latest ({_apkVersions[0].Version})");
                for (int i = 1; i < _apkVersions.Count; i++)
                    cmbApkVersion.Items.Add(_apkVersions[i].Version);
            }
            else
            {
                cmbApkVersion.Items.Add("Latest");
            }

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
            cmbApkVersion.Items.Clear();
            cmbApkVersion.Items.Add("Latest");
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

        // Persist selected version
        var idx = cmbApkVersion.SelectedIndex;
        if (idx >= 0 && _apkVersions != null && idx < _apkVersions.Count)
        {
            _main.Settings.SelectedApkVersion = _apkVersions[idx].Version;
            _main.SaveSettings();
            _main.RaiseApkVersionChanged();
        }
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
            txtApkDownloadStatus.Text = $"No APK found for v{ver} — download it first.";
            txtApkDownloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
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
            _main.ShowStatus($"APK downloaded: v{result.version}\n→ {result.filePath}", Wpf.Ui.Controls.InfoBarSeverity.Success);

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
}
