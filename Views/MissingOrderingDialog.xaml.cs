using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class MissingOrderingDialog : FluentWindow
{
    private const string ModuleTitle = "Module:Datatable/Areas/Mapping";
    private const string ModuleUrl = "https://merge-mansion.fandom.com/wiki/Module:Datatable/Areas/Mapping";

    private readonly IReadOnlyList<DeducedEntry> _adds;
    private readonly IReadOnlyList<RemovedCommentedEntry> _removes;
    private readonly string _wikiUsername;
    private readonly string _wikiPassword;
    private readonly string _additionsLuaForCopy;

    public MissingOrderingDialog(IReadOnlyList<DeducedEntry> adds,
                                 IReadOnlyList<RemovedCommentedEntry> removes,
                                 string wikiUsername, string wikiPassword)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        _adds = adds;
        _removes = removes;
        _wikiUsername = wikiUsername;
        _wikiPassword = wikiPassword;
        _additionsLuaForCopy = AreaOrderingService.GeneratePreviewLua(adds);

        UpdateHeader();
        RenderDiff();

        if (string.IsNullOrEmpty(wikiUsername) || string.IsNullOrEmpty(wikiPassword))
        {
            btnUpdate.IsEnabled = false;
            btnUpdate.ToolTip = "Wiki bot is not configured. Set up credentials in Settings.";
        }

        if (adds.Count == 0 && removes.Count == 0)
        {
            btnUpdate.IsEnabled = false;
            btnUpdate.ToolTip = "No changes needed.";
        }
    }

    private void UpdateHeader()
    {
        var legitAdds = _adds.Count(e => !e.IsCommented);
        var commentedAdds = _adds.Count - legitAdds;
        var parts = new List<string>();
        if (legitAdds > 0) parts.Add($"{legitAdds} new");
        if (commentedAdds > 0) parts.Add($"{commentedAdds} commented (in-prep)");
        if (_removes.Count > 0) parts.Add($"{_removes.Count} commented entries to remove");

        if (parts.Count == 0)
            txtHeader.Text = "No pending changes for Module:Datatable/Areas/Mapping.";
        else
            txtHeader.Text = $"Changes for Module:Datatable/Areas/Mapping: {string.Join(", ", parts)}.";
    }

    private void RenderDiff()
    {
        txtPreview.Inlines.Clear();

        var addedBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F));   // greenish
        var addedCommentedBrush = (Brush)Application.Current.FindResource("TextFillColorTertiaryBrush");
        var removedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x6A)); // reddish
        var sigilBrush = (Brush)Application.Current.FindResource("TextFillColorSecondaryBrush");

        // Compute alignment column ('=' position) for visual cleanliness across diff
        int maxKey = 0;
        foreach (var e in _adds)
        {
            int len = (e.IsCommented ? 2 : 0) + 4 + e.Name.Length; // [" + name + "]
            if (len > maxKey) maxKey = len;
        }
        foreach (var r in _removes)
        {
            int len = 2 + 4 + r.Name.Length; // --[" + name + "]
            if (len > maxKey) maxKey = len;
        }
        if (maxKey == 0) maxKey = 24;

        bool first = true;
        // Adds first (green)
        foreach (var e in _adds)
        {
            if (!first) txtPreview.Inlines.Add(new LineBreak());
            first = false;

            txtPreview.Inlines.Add(new Run("+ ") { Foreground = sigilBrush });
            var keyPart = (e.IsCommented ? "--" : "") + $"[\"{e.Name}\"]";
            txtPreview.Inlines.Add(new Run(keyPart)
            {
                Foreground = e.IsCommented ? addedCommentedBrush : addedBrush
            });
            int pad = Math.Max(1, maxKey - keyPart.Length + 1);
            txtPreview.Inlines.Add(new Run(new string(' ', pad) + $"= {{orderingIndex = {e.OrderingIndex}}},")
            {
                Foreground = e.IsCommented ? addedCommentedBrush : addedBrush
            });
        }

        // Removes (red, strikethrough)
        foreach (var r in _removes)
        {
            if (!first) txtPreview.Inlines.Add(new LineBreak());
            first = false;

            txtPreview.Inlines.Add(new Run("- ") { Foreground = sigilBrush });
            var keyPart = $"--[\"{r.Name}\"]";
            int pad = Math.Max(1, maxKey - keyPart.Length + 1);
            var idxText = r.OrderingIndex == Math.Floor(r.OrderingIndex)
                ? ((int)r.OrderingIndex).ToString()
                : r.OrderingIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var run = new Run(keyPart + new string(' ', pad) + $"= {{orderingIndex = {idxText}}},")
            {
                Foreground = removedBrush,
                TextDecorations = TextDecorations.Strikethrough
            };
            txtPreview.Inlines.Add(run);
        }

        if (_adds.Count == 0 && _removes.Count == 0)
            txtPreview.Inlines.Add(new Run("(No changes)") { Foreground = sigilBrush });
    }

    private void PreviewBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CopyToClipboard();
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard();
    }

    private void CopyToClipboard()
    {
        try
        {
            if (string.IsNullOrEmpty(_additionsLuaForCopy))
            {
                ShowInfo("Nothing to copy.", InfoBarSeverity.Informational);
                return;
            }
            Clipboard.SetText(_additionsLuaForCopy);
            ShowInfo("Additions copied to clipboard.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"Copy failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void BtnOpenModule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ModuleUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowInfo($"Failed to open module URL: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        btnUpdate.IsEnabled = false;
        btnCopy.IsEnabled = false;
        try
        {
            ShowInfo("Fetching current module...", InfoBarSeverity.Informational);
            var current = await WikiMappingService.FetchModuleContentAsync(ModuleTitle);
            if (current == null)
            {
                ShowInfo($"Failed to fetch {ModuleTitle}.", InfoBarSeverity.Error);
                btnUpdate.IsEnabled = true;
                btnCopy.IsEnabled = true;
                return;
            }

            var patched = AreaOrderingService.PatchModuleContent(current, _adds);
            if (patched == current)
            {
                ShowInfo("No changes needed — module already matches preview.", InfoBarSeverity.Informational);
                btnCopy.IsEnabled = true;
                return;
            }

            ShowInfo("Logging in to wiki...", InfoBarSeverity.Informational);
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(_wikiUsername, _wikiPassword);
            var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

            var legitCount = _adds.Count(e2 => !e2.IsCommented);
            var commentedCount = _adds.Count - legitCount;
            var summaryParts = new List<string>();
            if (legitCount > 0) summaryParts.Add($"{legitCount} new ordering indices");
            if (commentedCount > 0) summaryParts.Add($"{commentedCount} commented in-prep");
            if (_removes.Count > 0) summaryParts.Add($"clear {_removes.Count} stale commented");
            var summary = (summaryParts.Count > 0 ? string.Join(", ", summaryParts) : "Update area ordering")
                        + " (via MergeMansionWikiTools)";

            ShowInfo("Posting update...", InfoBarSeverity.Informational);
            await WikiMappingService.EditModuleAsync(client, csrfToken, ModuleTitle, patched, summary);

            ShowInfo($"Updated {ModuleTitle} — {string.Join(", ", summaryParts)}.", InfoBarSeverity.Success);
            btnCopy.IsEnabled = true;
            // keep btnUpdate disabled — already updated, prevent double-post
        }
        catch (Exception ex)
        {
            ShowInfo($"Update failed: {ex.Message}", InfoBarSeverity.Error);
            btnUpdate.IsEnabled = true;
            btnCopy.IsEnabled = true;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        infoBar.Title = "";
        infoBar.Message = message;
        infoBar.Severity = severity;
        infoBar.IsOpen = true;
    }
}
