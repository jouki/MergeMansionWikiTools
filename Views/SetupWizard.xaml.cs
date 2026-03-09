using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class SetupWizard : FluentWindow
{
    private AppSettings _settings;
    private int _currentStep = 1;
    private const int TotalSteps = 4;
    private bool _finished;

    /// <summary>True when wizard completed normally (Finish/Skip). False when closed via X button.</summary>
    public bool CompletedNormally { get; private set; }

    /// <summary>Steps where the user clicked Next (not Skip). Only these get persisted.</summary>
    private readonly HashSet<int> _confirmedSteps = new();

    private List<ApkDownloadService.ApkVersionInfo>? _apkVersions;
    private bool _suppressVersionSave;
    private bool _closed;

    // Bot verify result kept in memory until FinishWizard decides whether to persist
    private bool _botVerified;
    private string _botVerifiedDisplayName = "";

    private static CancellationTokenSource? _downloadCts;

    // ── Static download tracking (visible to SettingsPage) ──
    internal static string? ActiveDownloadVersion { get; private set; }
    internal static bool ActiveDownloadIsLatest { get; private set; }
    internal static bool ActiveDownloadRunning { get; private set; }
    /// <summary>Fires (statusText, brushResourceKey) on every status update.</summary>
    internal static event Action<string, string?>? DownloadStatusUpdated;

    private readonly ScrollViewer[] _stepPanels;

    public SetupWizard()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        _settings = SettingsService.Load();

        _stepPanels = [panelStep1, panelStep2, panelStep3, panelStep4];

        // Load existing values into controls
        txtWorkspacePath.Text = _settings.ImageExporterBasePath;
        txtChainPath.Text = _settings.ChainItemOddsPath;
        txtAreasPath.Text = _settings.AreasJsonPath;
        txtEventsPath.Text = _settings.EventsJsonPath;
        txtTinifyKey.Text = _settings.TinifyApiKey;
        txtTinifyKey2.Text = _settings.TinifyApiKey2;
        txtWikiBotUsername.Text = _settings.WikiUsername;
        txtWikiBotPassword.Password = _settings.WikiPassword;
        _botVerified = _settings.WikiVerified;
        _botVerifiedDisplayName = _settings.WikiVerifiedDisplayName;
        UpdateBotStatus();

        UpdateStepUI();

        // Fire-and-forget: load APK versions
        _ = LoadVersionsAsync();
    }

    // ── Step Navigation ──

    private void UpdateStepUI()
    {
        // Toggle panels
        for (int i = 0; i < _stepPanels.Length; i++)
            _stepPanels[i].Visibility = i == _currentStep - 1 ? Visibility.Visible : Visibility.Collapsed;

        // Update dots
        var dots = new[] { dot1, dot2, dot3, dot4 };
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = i == _currentStep - 1
                ? (Brush)FindResource("AccentFillColorDefaultBrush")
                : (Brush)FindResource("ControlStrokeColorDefaultBrush");
        }

        // Step text
        txtStepIndicator.Text = $"Step {_currentStep} of {TotalSteps}";

        // Back button
        btnBack.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;

        // Next / Finish
        btnNext.Content = _currentStep == TotalSteps ? "Finish" : "Next";

        UpdateNextButtonState();
    }

    private void UpdateNextButtonState()
    {
        btnNext.IsEnabled = _currentStep switch
        {
            1 => !string.IsNullOrWhiteSpace(txtWorkspacePath.Text),
            2 => !string.IsNullOrWhiteSpace(txtChainPath.Text)
              || !string.IsNullOrWhiteSpace(txtAreasPath.Text)
              || !string.IsNullOrWhiteSpace(txtEventsPath.Text),
            3 => !string.IsNullOrWhiteSpace(txtTinifyKey.Text),
            4 => _botVerified,
            _ => true
        };
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        _confirmedSteps.Add(_currentStep);

        if (_currentStep < TotalSteps)
        {
            _currentStep++;
            UpdateStepUI();
        }
        else
        {
            FinishWizard();
        }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            _currentStep--;
            UpdateStepUI();
        }
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        _confirmedSteps.Remove(_currentStep);

        if (_currentStep < TotalSteps)
        {
            _currentStep++;
            UpdateStepUI();
        }
        else
        {
            FinishWizard();
        }
    }

    private void FinishWizard()
    {
        if (_finished) return;
        _finished = true;
        CompletedNormally = true;

        PersistConfirmedSteps();
        _settings.OobeCompleted = true;
        SettingsService.Save(_settings);
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closed = true;

        // X button = cancel wizard, don't mark OobeCompleted (app will shut down)
        if (!_finished)
        {
            _finished = true;
            CompletedNormally = false;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Copies UI values into _settings only for steps where user clicked Next.
    /// </summary>
    private void PersistConfirmedSteps()
    {
        if (_confirmedSteps.Contains(1))
        {
            _settings.ImageExporterBasePath = txtWorkspacePath.Text;
            var idx = cmbApkVersion.SelectedIndex;
            if (idx >= 0 && _apkVersions != null && idx < _apkVersions.Count)
                _settings.SelectedApkVersion = _apkVersions[idx].Version;
        }

        if (_confirmedSteps.Contains(2))
        {
            _settings.ChainItemOddsPath = txtChainPath.Text;
            _settings.AreasJsonPath = txtAreasPath.Text;
            _settings.EventsJsonPath = txtEventsPath.Text;
        }

        if (_confirmedSteps.Contains(3))
        {
            _settings.TinifyApiKey = txtTinifyKey.Text.Trim();
            _settings.TinifyApiKey2 = txtTinifyKey2.Text.Trim();
        }

        if (_confirmedSteps.Contains(4))
        {
            _settings.WikiUsername = txtWikiBotUsername.Text.Trim();
            _settings.WikiPassword = txtWikiBotPassword.Password;
            _settings.WikiVerified = _botVerified;
            _settings.WikiVerifiedDisplayName = _botVerifiedDisplayName;
        }
    }

    // ── Step 1: Workspace ──

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select workspace folder" };
        var current = txtWorkspacePath.Text;
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            dlg.InitialDirectory = current;

        if (dlg.ShowDialog() == true)
        {
            txtWorkspacePath.Text = dlg.FolderName;
            UpdateNextButtonState();
        }
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            _suppressVersionSave = true;
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
            var saved = _settings.SelectedApkVersion;
            int restoredIdx = 0;
            if (!string.IsNullOrEmpty(saved) && _apkVersions.Count > 0)
            {
                for (int i = 0; i < _apkVersions.Count; i++)
                {
                    if (_apkVersions[i].Version == saved) { restoredIdx = i; break; }
                }
            }
            cmbApkVersion.SelectedIndex = restoredIdx;
        }
        catch
        {
            cmbApkVersion.Items.Clear();
            cmbApkVersion.Items.Add("Latest");
            cmbApkVersion.SelectedIndex = 0;
            _apkVersions = null;
        }
        finally
        {
            cmbApkVersion.IsEnabled = true;
            _suppressVersionSave = false;
        }
    }

    private void CmbApkVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // No-op — version is read from UI at persist time
    }

    private async void BtnRefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        btnRefreshVersions.IsEnabled = false;
        try { await LoadVersionsAsync(); }
        finally { btnRefreshVersions.IsEnabled = true; }
    }

    private async void BtnDownloadApk_Click(object sender, RoutedEventArgs e)
    {
        // Block if another download is already in progress
        if (ActiveDownloadRunning)
        {
            txtApkDownloadStatus.Text = $"Already downloading v{ActiveDownloadVersion} — wait or cancel first.";
            txtApkDownloadStatus.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
            return;
        }

        var basePath = txtWorkspacePath.Text;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            txtApkDownloadStatus.Text = "Set your Workspace folder first.";
            txtApkDownloadStatus.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
            return;
        }

        if (!Directory.Exists(basePath))
        {
            txtApkDownloadStatus.Text = "Workspace folder does not exist.";
            txtApkDownloadStatus.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
            return;
        }

        var selectedIdx = cmbApkVersion.SelectedIndex;
        var useSpecificVersion = selectedIdx > 0 && _apkVersions != null && selectedIdx < _apkVersions.Count;
        var selectedVersion = useSpecificVersion ? _apkVersions![selectedIdx] : null;

        // Resolve actual version string for tracking
        var resolvedVersion = selectedVersion?.Version ?? (_apkVersions?.Count > 0 ? _apkVersions[0].Version : null);

        // Set static tracking state
        _downloadCts = new CancellationTokenSource();
        ActiveDownloadVersion = resolvedVersion;
        ActiveDownloadIsLatest = !useSpecificVersion;
        ActiveDownloadRunning = true;

        btnDownloadApk.IsEnabled = false;
        btnCancelDownload.Visibility = Visibility.Visible;

        // Status update helper — updates local UI + fires static event for SettingsPage
        void SetStatus(string text, string? brushKey = null)
        {
            // Always notify SettingsPage (even if wizard is closed)
            DownloadStatusUpdated?.Invoke(text, brushKey);

            if (_closed) return;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_closed) return;
                    txtApkDownloadStatus.Text = text;
                    if (brushKey != null)
                        txtApkDownloadStatus.Foreground = (Brush)FindResource(brushKey);
                });
            }
            catch { }
        }

        var ct = _downloadCts.Token;

        try
        {
            (string version, string filePath) result;

            if (selectedVersion != null)
            {
                result = await Task.Run(() =>
                    ApkDownloadService.DownloadVersionAsync(
                        basePath, selectedVersion,
                        status => SetStatus(status), ct), ct);
            }
            else
            {
                result = await Task.Run(() =>
                    ApkDownloadService.DownloadLatestAsync(
                        basePath,
                        status => SetStatus(status), ct), ct);
            }

            var sizeMb = new FileInfo(result.filePath).Length / 1024.0 / 1024.0;
            SetStatus($"Done! v{result.version} ({sizeMb:F1} MB)", "SystemFillColorSuccessBrush");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Download cancelled.", "TextFillColorSecondaryBrush");
        }
        catch (Exception ex)
        {
            SetStatus($"Download failed: {ex.Message}", "SystemFillColorCautionBrush");
        }
        finally
        {
            ActiveDownloadRunning = false;
            _downloadCts?.Dispose();
            _downloadCts = null;

            if (!_closed)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_closed) return;
                    btnDownloadApk.IsEnabled = true;
                    btnCancelDownload.Visibility = Visibility.Collapsed;
                });
            }
        }
    }

    private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    // ── Step 2: Dump Files ──

    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void DumpFilesCardDrop(object sender, DragEventArgs e)
    {
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        foreach (var file in files.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var type = DetectJsonFileType(file);
            switch (type)
            {
                case "chain":  txtChainPath.Text = file; break;
                case "areas":  txtAreasPath.Text = file; break;
                case "events": txtEventsPath.Text = file; break;
            }
        }

        UpdateNextButtonState();
        e.Handled = true;
    }

    private void BrowseChainFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select chain_item_odds.json");
        if (path != null) { txtChainPath.Text = path; UpdateNextButtonState(); }
    }

    private void BrowseAreasFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select areas.json");
        if (path != null) { txtAreasPath.Text = path; UpdateNextButtonState(); }
    }

    private void BrowseEventsFile_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseJsonFile("Select events.json");
        if (path != null) { txtEventsPath.Text = path; UpdateNextButtonState(); }
    }

    // ── Step 3: TinyPNG API Keys ──

    private void TinifyKey_TextChanged(object sender, TextChangedEventArgs e) { UpdateNextButtonState(); }
    private void TinifyKey2_TextChanged(object sender, TextChangedEventArgs e) { }

    // ── Step 4: Wiki Bot ──

    private void WikiBotCredentials_TextChanged(object sender, TextChangedEventArgs e)
    {
        _botVerified = false;
        _botVerifiedDisplayName = "";
        UpdateBotStatus();
    }

    private void WikiBotPassword_Changed(object sender, RoutedEventArgs e)
    {
        _botVerified = false;
        _botVerifiedDisplayName = "";
        UpdateBotStatus();
    }

    private void UpdateBotStatus()
    {
        if (_botVerified)
        {
            txtWikiBotStatus.Text = $"Verified as {_botVerifiedDisplayName}";
            txtWikiBotStatus.Foreground = (Brush)FindResource("SystemFillColorSuccessBrush");
        }
        else if (!string.IsNullOrEmpty(txtWikiBotUsername.Text.Trim()))
        {
            txtWikiBotStatus.Text = "Not verified";
            txtWikiBotStatus.Foreground = (Brush)FindResource("TextFillColorTertiaryBrush");
        }
        else
        {
            txtWikiBotStatus.Text = "";
        }

        UpdateNextButtonState();
    }

    private async void BtnVerifyBot_Click(object sender, RoutedEventArgs e)
    {
        var username = txtWikiBotUsername.Text.Trim();
        var password = txtWikiBotPassword.Password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return;

        btnVerifyBot.IsEnabled = false;
        txtWikiBotStatus.Text = "Verifying...";
        txtWikiBotStatus.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");

        try
        {
            var confirmedName = await WikiMappingService.VerifyLoginAsync(username, password);

            if (confirmedName != null)
            {
                _botVerified = true;
                _botVerifiedDisplayName = confirmedName;
            }
            else
            {
                _botVerified = false;
                _botVerifiedDisplayName = "";
                txtWikiBotStatus.Text = "Login failed — check username and password.";
                txtWikiBotStatus.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
            }

            if (_botVerified) UpdateBotStatus();
        }
        catch (Exception ex)
        {
            _botVerified = false;
            _botVerifiedDisplayName = "";
            txtWikiBotStatus.Text = $"Failed: {ex.Message}";
            txtWikiBotStatus.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
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
        _guideDialog.Owner = this;
        _guideDialog.Closed += (_, _) => _guideDialog = null;
        _guideDialog.Show();
    }

    // ── Hyperlink ──

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ── Helpers ──

    private static string? DetectJsonFileType(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
