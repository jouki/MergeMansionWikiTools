using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using MergeMansionWikiTools.Services;

namespace MergeMansionWikiTools.Views;

public partial class AboutPage : UserControl
{
    private readonly MainWindow _main;

    private string? _cachedAreaPath;
    private int? _cachedAreaCount;

    public AboutPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        txtVersion.Text = Models.AppVersion.Build;
        RefreshStats();
    }

    public async void RefreshStats()
    {
        // Loaded data stats
        var ds = _main.DataService;
        txtStatChains.Text = ds != null ? ds.Chains.Count.ToString("N0") : "—";
        txtStatItems.Text  = ds != null ? ds.ItemNames.Count.ToString("N0") : "—";

        var areasPath = _main.Settings.AreasJsonPath;
        if (!string.IsNullOrEmpty(areasPath) && File.Exists(areasPath))
        {
            if (_cachedAreaPath != areasPath) { _cachedAreaPath = areasPath; _cachedAreaCount = null; }

            if (_cachedAreaCount == null)
            {
                txtStatAreas.Text = "...";
                _cachedAreaCount = await Task.Run(() => TryCountAreas(areasPath));
            }

            txtStatAreas.Text = _cachedAreaCount.HasValue ? _cachedAreaCount.Value.ToString("N0") : "—";
        }
        else
        {
            txtStatAreas.Text = "—";
        }

        // User activity stats
        var stats = UserStatsService.Load();
        txtUserSessions.Text    = stats.SessionCount.ToString("N0");
        txtUserDataLoads.Text   = stats.DataLoads.ToString("N0");
        txtUserTables.Text      = stats.TablesGenerated.ToString("N0");
        txtUserInfoboxes.Text   = stats.InfoboxesGenerated.ToString("N0");
        txtUserLuaChunks.Text   = stats.LuaAreaChunksGenerated.ToString("N0");
        txtUserLuaItems.Text    = stats.LuaItemsGenerated.ToString("N0");
        txtUserDialogues.Text   = stats.DialoguesExtracted.ToString("N0");
        txtUserFirstLaunch.Text = stats.FirstLaunch == default
            ? "—"
            : stats.FirstLaunch.ToLocalTime().ToString("d MMM yyyy");
    }

    private void JoukiAvatar_Click(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/jouki") { UseShellExecute = true });

    private void JoukiName_Click(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/jouki") { UseShellExecute = true });

    private void RepoLink_Click(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/jouki/MergeMansionWikiTools") { UseShellExecute = true });

    private void OnepiecefreakAvatar_Click(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/onepiecefreak3") { UseShellExecute = true });

    private void OnepiecefreakName_Click(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/onepiecefreak3") { UseShellExecute = true });

    private void DumperRepo_Click(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/mm-utils/merge-mansion-dumper") { UseShellExecute = true });

    private static int? TryCountAreas(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("Data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
                return data.GetArrayLength();
        }
        catch { }
        return null;
    }
}
