using System.IO;
using System.Windows;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Offers updating the tool to a newly detected game version: switches the working
/// version, optionally downloads the APK, and either pulls an existing dump from
/// Discord or clears the dump file paths and points the user to Game Data Dumper.
/// </summary>
public partial class GameUpdateDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly GameVersionUpdateService.GameVersionCheckResult _check;
    private CancellationTokenSource? _cts;
    private bool _running;

    // internal, not public: GameVersionCheckResult is nested in an internal-static service
    // type, so a public constructor referencing it would be less accessible than itself
    // (CS0051). Task 6 instantiates this dialog from MainWindow, same assembly — internal is enough.
    internal GameUpdateDialog(MainWindow main, GameVersionUpdateService.GameVersionCheckResult check)
    {
        _main = main;
        _check = check;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        txtCurrentVersion.Text = string.IsNullOrEmpty(main.Settings.SelectedApkVersion)
            ? "—" : main.Settings.SelectedApkVersion;
        txtNewVersion.Text = check.Latest.Version;
        txtReleaseDate.Text = check.Latest.ReleaseDate is { } rd
            ? $"Released {rd:yyyy-MM-dd}" : "Release date unknown";

        if (!check.Latest.CanDownload)
        {
            chkDownloadApk.IsChecked = false;
            chkDownloadApk.IsEnabled = false;
            txtApkNote.Text = "APK is not on APKPure yet — download it later from Settings.";
            txtApkNote.Visibility = Visibility.Visible;
        }
    }

    // ── Offer buttons ──

    private void BtnLater_Click(object sender, RoutedEventArgs e)
    {
        _main.SessionDeclinedGameVersion = _check.Latest.Version;
        Close();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        _main.Settings.LastDeclinedGameVersion = _check.Latest.Version;
        _main.SaveSettings();
        Close();
    }

    private void BtnOpenDumper_Click(object sender, RoutedEventArgs e)
    {
        _main.NavigateToPage(7); // Game Data Dumper
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_running) _cts?.Cancel(); // cancel the flow; the flow's finally unblocks
        base.OnClosing(e);
    }

    // ── Update flow ──

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();

        btnUpdate.IsEnabled = false;
        btnSkip.IsEnabled = false;
        btnLater.IsEnabled = false;
        chkDownloadApk.IsEnabled = false;
        progressPanel.Visibility = Visibility.Visible;

        var settings = _main.Settings;
        var basePath = settings.ImageExporterBasePath;
        var version = _check.Latest.Version;
        string? apkWarning = null;

        // Shared fallback for "no dump to use" cases: clear the stale paths from the
        // previous version and point the user at Game Data Dumper / Settings. Declared at
        // method scope (not inside the try block) so the outer catch can reuse it too.
        void ShowDumpFallback(string reasonMsg)
        {
            _main.ClearDumpFilePaths();
            _main.RaiseApkVersionChanged();

            var msg = reasonMsg;
            if (apkWarning != null) msg += $"\n⚠ {apkWarning}";
            ShowFinished(msg, offerDumper: true);
        }

        try
        {
            // Phase 1: switch working version
            txtProgress.Text = $"Switching working version to v{version}…";
            settings.SelectedApkVersion = version;
            _main.SaveSettings();

            // Phase 2: APK download (optional, non-fatal)
            if (chkDownloadApk.IsChecked == true && _check.Latest.CanDownload)
            {
                try
                {
                    await ApkDownloadService.DownloadVersionAsync(
                        basePath, _check.Latest,
                        s => Dispatcher.Invoke(() => txtProgress.Text = s),
                        _cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    apkWarning = $"APK download failed: {ex.Message}";
                    AppLogger.Warn($"[GameUpdate] {apkWarning}");
                }
            }

            // Phase 3: Discord dump
            DiscordDumpDownloadService.DiscordDumpInfo? dump = null;
            bool discordFailed = false;
            try
            {
                txtProgress.Text = "Checking Discord for an existing dump…";
                var botToken = settings.DiscordBotToken;
                if (string.IsNullOrWhiteSpace(botToken))
                    botToken = AppSettings.DefaultDiscordBotToken;
                var channelId = settings.DiscordChannelId;
                if (string.IsNullOrWhiteSpace(channelId))
                    channelId = AppSettings.DefaultDiscordChannelId;
                dump = await GameVersionUpdateService.FindDumpForVersionAsync(
                    botToken, channelId,
                    version, _check.AllVersions,
                    new Progress<string>(s => txtProgress.Text = s),
                    _cts.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                discordFailed = true;
                AppLogger.Warn($"[GameUpdate] Discord dump check failed: {ex.Message}");
            }

            if (dump?.AttachmentUrl != null)
            {
                var versionDir = Path.Combine(basePath, version);
                var existing = dump.DataCreatedAt.HasValue
                    ? DiscordDumpDownloadService.FindExistingDumpByCreatedAt(
                        versionDir, dump.DataCreatedAt.Value)
                    : null;

                try
                {
                    string dumpFolder;
                    if (existing != null)
                    {
                        dumpFolder = existing; // same CreatedAt already on disk — reuse it
                    }
                    else
                    {
                        dumpFolder = DiscordDumpDownloadService.GetNextDumpFolderName(
                            versionDir, dump.DataCreatedAt, isUnknownVersion: false);
                        Directory.CreateDirectory(versionDir);
                        var archivePath = Path.Combine(versionDir, dump.AttachmentFilename!);

                        txtProgress.Text = "Downloading dump…";
                        await DiscordDumpDownloadService.DownloadAttachmentAsync(
                            dump.AttachmentUrl, archivePath,
                            new Progress<(long dl, long? total)>(p => Dispatcher.Invoke(() =>
                            {
                                if (p.total is > 0)
                                {
                                    progressBar.IsIndeterminate = false;
                                    progressBar.Maximum = p.total.Value;
                                    progressBar.Value = p.dl;
                                    txtProgress.Text =
                                        $"Downloading dump… {(double)p.dl / p.total.Value * 100:F0}%";
                                }
                            })),
                            _cts.Token);

                        txtProgress.Text = "Extracting dump…";
                        progressBar.IsIndeterminate = true;
                        await DiscordDumpDownloadService.ExtractArchiveAsync(
                            archivePath, Path.Combine(versionDir, dumpFolder));
                    }

                    settings.ActiveDumpFolder = dumpFolder;
                    _main.SaveSettings();
                    await _main.AssignDumpFilePathsAsync(Path.Combine(versionDir, dumpFolder));
                    _main.RefreshSettingsPaths();
                    _main.RaiseApkVersionChanged();

                    var msg = $"Updated to v{version} — dump loaded from Discord ({dumpFolder}).";
                    if (apkWarning != null) msg += $"\n⚠ {apkWarning}";
                    ShowFinished(msg, offerDumper: false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[GameUpdate] Dump download failed: {ex.Message}");
                    ShowDumpFallback(
                        $"Updated to v{version}, but downloading the dump failed: {ex.Message}. " +
                        "Dump file paths were cleared — create a new dump in Game Data Dumper, " +
                        "or retry the Discord download in Settings.");
                }
            }
            else
            {
                ShowDumpFallback(discordFailed
                    ? $"Updated to v{version}, but the Discord dump check failed. Dump file " +
                      "paths were cleared — create a new dump in Game Data Dumper, or retry " +
                      "the Discord download in Settings."
                    : $"Updated to v{version} — no dump for this version on Discord yet. Dump " +
                      "file paths were cleared — create a new dump in Game Data Dumper.");
            }
        }
        catch (OperationCanceledException)
        {
            ShowDumpFallback(
                $"Update cancelled after switching the working version to v{version}. " +
                "Dump file paths were cleared — create a new dump in Game Data Dumper, " +
                "or retry the Discord download in Settings.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[GameUpdate] Update flow failed: {ex}");
            var msg = $"Update failed: {ex.Message}";
            if (apkWarning != null) msg += $"\n⚠ {apkWarning}";
            ShowFinished(msg, offerDumper: false);
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ShowFinished(string message, bool offerDumper)
    {
        progressPanel.Visibility = Visibility.Collapsed;
        txtResult.Text = message;
        txtResult.Visibility = Visibility.Visible;
        btnUpdate.Visibility = Visibility.Collapsed;
        btnSkip.Visibility = Visibility.Collapsed;
        btnLater.Visibility = Visibility.Collapsed;
        btnClose.Visibility = Visibility.Visible;
        if (offerDumper) btnOpenDumper.Visibility = Visibility.Visible;
    }
}
