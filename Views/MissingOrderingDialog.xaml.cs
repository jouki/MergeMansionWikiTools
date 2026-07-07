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
    private readonly IReadOnlyList<RenamedEntry> _renames;
    private readonly IReadOnlyList<StaleEntry> _staleDeletes;
    private readonly string? _moduleContent;
    private readonly string _wikiUsername;
    private readonly string _wikiPassword;
    private readonly string _additionsLuaForCopy;

    public MissingOrderingDialog(IReadOnlyList<DeducedEntry> adds,
                                 IReadOnlyList<RemovedCommentedEntry> removes,
                                 IReadOnlyList<RenamedEntry> renames,
                                 IReadOnlyList<StaleEntry> staleDeletes,
                                 string? moduleContent,
                                 string wikiUsername, string wikiPassword)
    {
        _moduleContent = moduleContent;
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        _adds = adds;
        _removes = removes;
        _renames = renames;
        _staleDeletes = staleDeletes;
        _wikiUsername = wikiUsername;
        _wikiPassword = wikiPassword;
        _additionsLuaForCopy = AreaOrderingService.GeneratePreviewLua(adds);

        UpdateHeader();
        RenderDiff();

        // Dynamic height: the preview grows with the diff (window follows via SizeToContent)
        // and only starts scrolling once the window would outgrow the work area.
        previewScroll.MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 380);

        // SizeToContent="Height" + Mica/ExtendsContentIntoTitleBar can leave phantom space
        // below the content on first show — force a re-measure once the window is loaded.
        Loaded += (_, _) => RefreshWindowHeight();

        if (string.IsNullOrEmpty(wikiUsername) || string.IsNullOrEmpty(wikiPassword))
        {
            btnUpdate.IsEnabled = false;
            btnUpdate.ToolTip = "Wiki bot is not configured. Set up credentials in Settings.";
        }

        if (adds.Count == 0 && removes.Count == 0 && renames.Count == 0 && staleDeletes.Count == 0)
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
        if (_renames.Count > 0) parts.Add($"{_renames.Count} renamed");
        if (_staleDeletes.Count > 0) parts.Add($"{_staleDeletes.Count} stale removed");
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

        // Preferred rendering: a real unified diff of the module's affected span — rows in
        // file order (= orderingIndex order), including unchanged in-between rows as context.
        if (_moduleContent != null)
        {
            var diff = AreaOrderingService.BuildDiffPreview(_moduleContent, _adds, _renames, _staleDeletes);
            if (diff.Count > 0)
            {
                bool firstLine = true;
                foreach (var d in diff)
                {
                    if (!firstLine) txtPreview.Inlines.Add(new LineBreak());
                    firstLine = false;

                    switch (d.Type)
                    {
                        case Models.DiffLineType.Added:
                            txtPreview.Inlines.Add(new Run("+ ") { Foreground = sigilBrush });
                            txtPreview.Inlines.Add(new Run(d.Text.TrimStart('\t')) { Foreground = addedBrush });
                            break;
                        case Models.DiffLineType.Removed:
                            txtPreview.Inlines.Add(new Run("- ") { Foreground = sigilBrush });
                            txtPreview.Inlines.Add(new Run(d.Text.TrimStart('\t'))
                            {
                                Foreground = removedBrush,
                                TextDecorations = TextDecorations.Strikethrough
                            });
                            break;
                        default:
                            txtPreview.Inlines.Add(new Run("  ") { Foreground = sigilBrush });
                            txtPreview.Inlines.Add(new Run(d.Text.TrimStart('\t')) { Foreground = sigilBrush });
                            break;
                    }
                }
                return;
            }
        }

        // Fallback (module content unavailable): grouped list rendering.

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

        // Renames (accent, ~ sigil): old name struck through → new name highlighted
        var renamedBrush = (Brush)Application.Current.FindResource("AccentFillColorDefaultBrush");
        foreach (var ren in _renames)
        {
            if (!first) txtPreview.Inlines.Add(new LineBreak());
            first = false;

            txtPreview.Inlines.Add(new Run("~ ") { Foreground = sigilBrush });
            var prefix = ren.IsCommented ? "--" : "";
            txtPreview.Inlines.Add(new Run($"{prefix}[\"{ren.OldName}\"]")
            {
                Foreground = removedBrush,
                TextDecorations = TextDecorations.Strikethrough
            });
            txtPreview.Inlines.Add(new Run(" → ") { Foreground = sigilBrush });
            txtPreview.Inlines.Add(new Run($"{prefix}[\"{ren.NewName}\"]") { Foreground = renamedBrush });
            txtPreview.Inlines.Add(new Run($"  (orderingIndex = {FormatIndex(ren.OrderingIndex)} kept)")
            {
                Foreground = sigilBrush
            });
        }

        // Stale deletes (red, strikethrough) — mapping rows with no counterpart in game data
        foreach (var del in _staleDeletes)
        {
            if (!first) txtPreview.Inlines.Add(new LineBreak());
            first = false;

            txtPreview.Inlines.Add(new Run("- ") { Foreground = sigilBrush });
            var keyPart = $"{(del.IsCommented ? "--" : "")}[\"{del.Name}\"]";
            int pad = Math.Max(1, maxKey - keyPart.Length + 1);
            txtPreview.Inlines.Add(new Run(
                keyPart + new string(' ', pad) + $"= {{orderingIndex = {FormatIndex(del.OrderingIndex)}}},")
            {
                Foreground = removedBrush,
                TextDecorations = TextDecorations.Strikethrough
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

            var run = new Run(keyPart + new string(' ', pad) + $"= {{orderingIndex = {FormatIndex(r.OrderingIndex)}}},")
            {
                Foreground = removedBrush,
                TextDecorations = TextDecorations.Strikethrough
            };
            txtPreview.Inlines.Add(run);
        }

        if (_adds.Count == 0 && _removes.Count == 0 && _renames.Count == 0 && _staleDeletes.Count == 0)
            txtPreview.Inlines.Add(new Run("(No changes)") { Foreground = sigilBrush });
    }

    private static string FormatIndex(double idx)
        => idx == Math.Floor(idx)
            ? ((int)idx).ToString()
            : idx.ToString(System.Globalization.CultureInfo.InvariantCulture);

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
        if (string.IsNullOrEmpty(_additionsLuaForCopy))
        {
            ShowInfo("Nothing to copy.", InfoBarSeverity.Informational);
            return;
        }
        // Native Win32 clipboard — WPF Clipboard.SetText randomly throws CLIPBRD_E_CANT_OPEN
        // (0x800401D0) when another process holds the clipboard; the app-wide helper avoids OLE.
        if (App.NativeSetClipboardText(_additionsLuaForCopy))
            ShowInfo("Additions copied to clipboard.", InfoBarSeverity.Success);
        else
            ShowInfo("Copy failed: clipboard is held by another application — try again.", InfoBarSeverity.Error);
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

            var patched = AreaOrderingService.PatchModuleContent(current, _adds, _renames, _staleDeletes);
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
            if (_renames.Count > 0) summaryParts.Add($"rename {_renames.Count} (localization changed)");
            if (_staleDeletes.Count > 0) summaryParts.Add($"remove {_staleDeletes.Count} stale");
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
        RefreshWindowHeight(); // the InfoBar grows the content — SizeToContent won't follow on its own
    }

    /// <summary>
    /// Forces the SizeToContent="Height" auto-size to recompute. The Mica/ExtendsContentIntoTitleBar
    /// combo breaks it both ways: phantom space on first show AND no growth when content expands
    /// later (e.g. the status InfoBar opening). Deferred so the new layout is measured first.
    /// </summary>
    private void RefreshWindowHeight()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.Height;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
