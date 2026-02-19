using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class AssetRipperPage : UserControl
{
    private readonly MainWindow _main;
    private CancellationTokenSource? _cts;

    public AssetRipperPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        txtExePath.Text = _main.Settings.AssetRipperPath;
    }

    // ── Browse ───────────────────────────────────────────────────────

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select AssetRipper.CLI.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrEmpty(_main.Settings.AssetRipperPath))
        {
            var dir = System.IO.Path.GetDirectoryName(_main.Settings.AssetRipperPath);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() == true)
        {
            txtExePath.Text = dlg.FileName;
            _main.Settings.AssetRipperPath = dlg.FileName;
            _main.SaveSettings();
        }
    }

    private void BrowseInputFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select bundle file",
            Filter = "All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            txtInputPath.Text = dlg.FileName;
    }

    private void BrowseInputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select folder containing bundle files" };
        if (dlg.ShowDialog() == true)
            txtInputPath.Text = dlg.FolderName;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select output folder" };

        if (!string.IsNullOrEmpty(_main.Settings.ImageExporterBasePath) &&
            System.IO.Directory.Exists(_main.Settings.ImageExporterBasePath))
            dlg.InitialDirectory = _main.Settings.ImageExporterBasePath;

        if (dlg.ShowDialog() == true)
            txtOutputPath.Text = dlg.FolderName;
    }

    // ── Extract ──────────────────────────────────────────────────────

    private async void BtnExtract_Click(object sender, RoutedEventArgs e)
    {
        var exe = txtExePath.Text.Trim();
        var input = txtInputPath.Text.Trim();
        var output = txtOutputPath.Text.Trim();

        if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
        {
            ShowInfo("Asset Ripper executable not found. Browse to AssetRipper.CLI.exe first.", InfoBarSeverity.Error);
            return;
        }
        if (string.IsNullOrEmpty(input))
        {
            ShowInfo("Select an input file or folder.", InfoBarSeverity.Error);
            return;
        }
        if (string.IsNullOrEmpty(output))
        {
            ShowInfo("Select an output folder.", InfoBarSeverity.Error);
            return;
        }

        btnExtract.IsEnabled = false;
        txtLog.Text = "";
        ShowInfo("Starting extraction...", InfoBarSeverity.Informational);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var extraArgs = txtExtraArgs.Text.Trim();
        var args = $"--input \"{input}\" --output \"{output}\"";
        if (!string.IsNullOrEmpty(extraArgs))
            args += " " + extraArgs;

        AppendLog($"Running: {System.IO.Path.GetFileName(exe)} {args}");
        AppendLog(new string('─', 60));

        try
        {
            var sb = new StringBuilder();
            var exitCode = await Task.Run(() => RunProcess(exe, args, sb, _cts.Token));

            AppendLog(new string('─', 60));
            AppendLog($"Exit code: {exitCode}");

            if (exitCode == 0)
                ShowInfo($"Extraction complete. Output: {output}", InfoBarSeverity.Success);
            else
                ShowInfo($"Process exited with code {exitCode}. See log for details.", InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            AppendLog("--- Cancelled ---");
            ShowInfo("Extraction cancelled.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ShowInfo($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            btnExtract.IsEnabled = true;
        }
    }

    private int RunProcess(string exe, string args, StringBuilder sb, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) Dispatcher.Invoke(() => AppendLog(e.Data));
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) Dispatcher.Invoke(() => AppendLog("[ERR] " + e.Data));
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        while (!proc.WaitForExit(200))
        {
            ct.ThrowIfCancellationRequested();
        }

        return proc.ExitCode;
    }

    // ── Log helpers ──────────────────────────────────────────────────

    private void AppendLog(string line)
    {
        txtLog.AppendText(line + "\n");
        txtLog.ScrollToEnd();
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        txtLog.Text = "";
    }

    // ── InfoBar ──────────────────────────────────────────────────────

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
