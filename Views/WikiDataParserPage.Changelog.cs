using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WpfMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

// Changelog: local-vs-wiki diff computation (incl. CBE rename detection) + diff rendering UI.
public partial class WikiDataParserPage
{
    private ChangelogData ComputeItemsChangelog(string wikiContent, string localLua)
    {
        var wikiEntries = WikiMappingService.ExtractLuaTableEntries(wikiContent, "items");
        var localEntries = WikiMappingService.ExtractLuaTableEntries(localLua, "items");
        return ComputeItemsChangelog(wikiEntries, localEntries);
    }

    private ChangelogData ComputeItemsChangelog(string wikiContent, Dictionary<string, string> localEntries)
    {
        var wikiEntries = WikiMappingService.ExtractLuaTableEntries(wikiContent, "items");
        return ComputeItemsChangelog(wikiEntries, localEntries);
    }

    private static ChangelogData ComputeItemsChangelog(
        Dictionary<string, string> wikiEntries, Dictionary<string, string> localEntries)
    {
        AppLogger.Debug($"[ComputeItemsChangelog] wikiEntries={wikiEntries.Count}, localEntries={localEntries.Count}");

        var added = localEntries.Keys.Except(wikiEntries.Keys).OrderBy(k => k).ToList();
        var removed = wikiEntries.Keys.Except(localEntries.Keys).OrderBy(k => k).ToList();
        var modified = localEntries.Keys.Intersect(wikiEntries.Keys)
            .Where(k => localEntries[k] != wikiEntries[k])
            .OrderBy(k => k)
            .Select(k => new ModifiedEntry(k, wikiEntries[k], localEntries[k]))
            .ToList();

        AppLogger.Debug($"[ComputeItemsChangelog] initial: +{added.Count} added, -{removed.Count} removed, ~{modified.Count} modified");

        // Rename detection: pair Removed items with their counterparts in CURRENT LOCAL DATA.
        // Match heuristic: both ids match `^CBE_<event>_(.+)$` and the (.+) part is identical, but the
        // <event> segments differ. Example: `CBE_Easter2025_Assembly_01` (Removed — old event ended) ↔
        // `CBE_SweetMess_Assembly_01` (still in local — current event) → game devs renamed the event.
        //
        // IMPORTANT: counterparts are searched in the FULL set of localEntries (not just `added`) because
        // the new event's items typically already exist on the wiki (Modified or unchanged), they're not
        // freshly added. Looking only at `added` misses them entirely.
        var renamed = new List<RenamedEntry>();
        var rxEvent = new System.Text.RegularExpressions.Regex(
            @"^CBE_([A-Za-z0-9]+)_(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Build local index: rest → list of (id, eventPrefix)
        var localByRest = new Dictionary<string, List<(string Id, string Event)>>(StringComparer.Ordinal);
        foreach (var lid in localEntries.Keys)
        {
            var m = rxEvent.Match(lid);
            if (!m.Success) continue;
            var ev = m.Groups[1].Value;
            var rest = m.Groups[2].Value;
            if (!localByRest.TryGetValue(rest, out var list))
                localByRest[rest] = list = new List<(string, string)>();
            list.Add((lid, ev));
        }
        AppLogger.Debug($"[ComputeItemsChangelog] localByRest entries: {localByRest.Count} unique rests across local items");

        var pairedRemoved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rid in removed)
        {
            var m = rxEvent.Match(rid);
            if (!m.Success) continue;
            var oldEvent = m.Groups[1].Value;
            var rest = m.Groups[2].Value;
            if (!localByRest.TryGetValue(rest, out var candidates) || candidates.Count == 0) continue;
            // Match candidates with a DIFFERENT event prefix (otherwise it's a no-op pairing).
            (string Id, string Event)? match = null;
            foreach (var c in candidates)
            {
                if (!string.Equals(c.Event, oldEvent, StringComparison.Ordinal))
                {
                    match = c;
                    break;
                }
            }
            if (match == null) continue;

            pairedRemoved.Add(rid);
            var oldChain = ExtractChainNameFromEntry(wikiEntries.GetValueOrDefault(rid, ""));
            var newChain = ExtractChainNameFromEntry(localEntries.GetValueOrDefault(match.Value.Id, ""));
            renamed.Add(new RenamedEntry(rid, match.Value.Id, oldChain, newChain));
        }
        if (renamed.Count > 0)
        {
            removed = removed.Where(r => !pairedRemoved.Contains(r)).ToList();
            renamed = renamed.OrderBy(r => r.OldId, StringComparer.Ordinal).ToList();
        }

        AppLogger.Debug($"[ComputeItemsChangelog] final: +{added.Count} added, -{removed.Count} removed (was {removed.Count + pairedRemoved.Count}), ~{modified.Count} modified, ↻{renamed.Count} renamed");
        if (renamed.Count > 0)
        {
            // Log first 5 rename pairs for sanity check
            foreach (var r in renamed.Take(5))
                AppLogger.Debug($"[ComputeItemsChangelog] rename sample: {r.OldId} → {r.NewId}  (chain {r.OldChain} → {r.NewChain})");
        }

        return new ChangelogData(added, removed, modified, renamed.Count > 0 ? renamed : null);
    }

    private static ChangelogData ComputeAreasChangelog(
        Dictionary<string, string> wikiEntries, Dictionary<string, string> localEntries,
        Dictionary<string, double>? ordering = null)
    {
        // Build sort key: known ordering index first, then unknown areas after max
        double SortKey(string key)
        {
            if (ordering != null && ordering.TryGetValue(key, out var idx)) return idx;
            return double.MaxValue; // unknown areas sort last
        }

        var added = localEntries.Keys.Except(wikiEntries.Keys).OrderBy(SortKey).ThenBy(k => k).ToList();
        var removed = wikiEntries.Keys.Except(localEntries.Keys).OrderBy(SortKey).ThenBy(k => k).ToList();
        var modified = localEntries.Keys.Intersect(wikiEntries.Keys)
            .Where(k => NormalizeAreaBlock(wikiEntries[k]) != NormalizeAreaBlock(localEntries[k]))
            .OrderBy(SortKey).ThenBy(k => k)
            .Select(k => new ModifiedEntry(k, wikiEntries[k], localEntries[k]))
            .ToList();
        return new ChangelogData(added, removed, modified);
    }

    private static string NormalizeAreaBlock(string block) =>
        System.Text.RegularExpressions.Regex.Replace(block.Replace("\r", "").Trim(), @"\s+", " ");

    /// <summary>
    /// Builds a changelog section element for use in confirmation dialogs.
    /// Returns a DockPanel: separator + summary header pinned at top,
    /// detail content in a ScrollViewer that fills remaining dialog space.
    /// The parent Build*Preview DockPanel must place this as the fill child (last, no Dock).
    /// </summary>
    private UIElement BuildChangelogElement(ChangelogData? changelog,
        string entityName, Brush primary, Brush secondary, Brush tertiary)
    {
        var isAreaMode = entityName == "area";
        var panel = new DockPanel();

        // Separator (pinned top)
        var sep = new Border
        {
            Height = 1, Margin = new Thickness(0, 6, 0, 10),
            Background = secondary, Opacity = 0.3
        };
        DockPanel.SetDock(sep, Dock.Top);
        panel.Children.Add(sep);

        if (changelog == null)
        {
            var loading = new WpfTextBlock
            {
                Text = "Changelog: loading...",
                FontSize = 11, Foreground = tertiary
            };
            DockPanel.SetDock(loading, Dock.Top);
            panel.Children.Add(loading);
            return panel;
        }

        if (!changelog.HasChanges())
        {
            var noChanges = new WpfTextBlock
            {
                Text = "No data changes detected vs wiki",
                FontSize = 11, Foreground = tertiary
            };
            DockPanel.SetDock(noChanges, Dock.Top);
            panel.Children.Add(noChanges);
            return panel;
        }

        // Summary header (pinned top)
        var parts = new List<string>();
        if (changelog.Modified.Count > 0) parts.Add($"{changelog.Modified.Count} modified");
        if (changelog.Added.Count > 0) parts.Add($"+{changelog.Added.Count} new");
        if (changelog.Removed.Count > 0) parts.Add($"\u2212{changelog.Removed.Count} removed");
        if ((changelog.Renamed?.Count ?? 0) > 0) parts.Add($"\u21bb{changelog.Renamed!.Count} renamed");
        if ((changelog.Archived?.Count ?? 0) > 0) parts.Add($"\ud83d\udce6{changelog.Archived!.Count} archived");

        var summaryTb = new WpfTextBlock
        {
            Text = $"Data changes: {string.Join(", ", parts)}",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(summaryTb, Dock.Top);
        panel.Children.Add(summaryTb);

        // Detail in ScrollViewer
        var detailContent = new StackPanel();
        var nameMap = BuildItemNameMap();
        BuildChangelogDetail(detailContent, changelog, primary, secondary, tertiary, nameMap, isAreaMode);
        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 300,
            Content = detailContent
        });

        return panel;
    }

    // ── Area diff helpers ──────────────────────────────────────────

    private sealed record AreaParsed(string? Name, string? IngameName, Dictionary<string, string> Tasks);

    /// <summary>
    /// Parses a multi-line area block into name, ingameName, and individual task blocks.
    /// </summary>
    private static AreaParsed ParseAreaBlock(string block)
    {
        string? name = null, ingameName = null;
        var tasks = new Dictionary<string, string>();

        // Extract top-level simple fields
        var nameMatch = System.Text.RegularExpressions.Regex.Match(block, @"name\s*=\s*""([^""]*)""");
        if (nameMatch.Success) name = nameMatch.Groups[1].Value;

        var ingameMatch = System.Text.RegularExpressions.Regex.Match(block, @"ingameName\s*=\s*""([^""]*)""");
        if (ingameMatch.Success) ingameName = ingameMatch.Groups[1].Value;

        // Find "tasks = {" section and extract individual task blocks
        var tasksStart = block.IndexOf("tasks = {", StringComparison.Ordinal);
        if (tasksStart < 0) return new AreaParsed(name, ingameName, tasks);

        // Find individual task entries within the tasks section
        var taskPattern = new System.Text.RegularExpressions.Regex(@"\[""([^""]+)""\]\s*=\s*\{");
        int searchFrom = tasksStart + "tasks = {".Length;

        // First, find the end of the tasks section using brace depth
        int tasksBlockStart = block.IndexOf('{', tasksStart);
        if (tasksBlockStart < 0) return new AreaParsed(name, ingameName, tasks);

        int depth = 1;
        bool inStr = false;
        int pos = tasksBlockStart + 1;
        while (pos < block.Length && depth > 0)
        {
            char c = block[pos];
            if (c == '"' && (pos == 0 || block[pos - 1] != '\\')) inStr = !inStr;
            if (!inStr)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            pos++;
        }
        var tasksSection = block[tasksBlockStart..pos];

        // Now extract individual tasks from within the tasks section
        var taskMatches = taskPattern.Matches(tasksSection);
        for (int m = 0; m < taskMatches.Count; m++)
        {
            var taskId = taskMatches[m].Groups[1].Value;
            var bracePos = taskMatches[m].Index + taskMatches[m].Length - 1;

            int d = 1;
            bool inS = false;
            int j = bracePos + 1;
            while (j < tasksSection.Length && d > 0)
            {
                char c = tasksSection[j];
                if (c == '"' && (j == 0 || tasksSection[j - 1] != '\\')) inS = !inS;
                if (!inS)
                {
                    if (c == '{') d++;
                    else if (c == '}') d--;
                }
                j++;
            }

            if (d == 0)
                tasks.TryAdd(taskId, tasksSection[bracePos..j]);
        }

        return new AreaParsed(name, ingameName, tasks);
    }

    /// <summary>
    /// Parses a task block into key-value fields.
    /// Handles nested braces and quoted strings (multi-line aware).
    /// </summary>
    private static Dictionary<string, string> ParseTaskBlock(string block)
    {
        var fields = new Dictionary<string, string>();
        if (block.Length < 2 || block[0] != '{') return fields;

        var inner = block[1..^1];

        // Split on commas at depth 0
        var parts = new List<string>();
        int depth = 0;
        bool inString = false;
        int start = 0;

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '"' && (i == 0 || inner[i - 1] != '\\')) inString = !inString;
            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(inner[start..i].Trim());
                    start = i + 1;
                }
            }
        }
        if (start < inner.Length)
            parts.Add(inner[start..].Trim());

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Match: key = value or ["key"] = value
            var eqIdx = trimmed.IndexOf(" = ", StringComparison.Ordinal);
            if (eqIdx > 0)
            {
                var key = trimmed[..eqIdx].Trim().Trim('[', ']', '"');
                var val = trimmed[(eqIdx + 3)..].Trim();
                fields[key] = val;
            }
        }

        return fields;
    }

    /// <summary>
    /// Builds task-level diff UI for a modified area entry.
    /// Shows added/removed/modified tasks with field-level details.
    /// </summary>
    private void BuildAreaModifiedDetail(StackPanel target, ModifiedEntry mod,
        Brush secondary, Brush tertiary, bool defaultExpanded,
        Dictionary<string, string>? itemNameMap = null)
    {
        var greenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0xB8, 0x4F));
        var redBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0x50, 0x60));
        var orangeBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25));

        var wikiArea = ParseAreaBlock(mod.WikiValue);
        var localArea = ParseAreaBlock(mod.LocalValue);

        var areaName = localArea.Name ?? wikiArea.Name;
        var diffPanel = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };

        // Compare top-level fields (skip whitespace-only diffs)
        if (wikiArea.Name != localArea.Name && localArea.Name != null &&
            (wikiArea.Name ?? "").Trim() != localArea.Name.Trim())
        {
            var tb = new WpfTextBlock { FontSize = 10, Foreground = secondary, Margin = new Thickness(0, 1, 0, 1) };
            tb.Inlines.Add(new System.Windows.Documents.Run("Name: ") { Foreground = tertiary });
            tb.Inlines.Add(new System.Windows.Documents.Run(wikiArea.Name ?? "(none)") { Foreground = redBrush });
            tb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = tertiary });
            tb.Inlines.Add(new System.Windows.Documents.Run(localArea.Name) { Foreground = greenBrush });
            diffPanel.Children.Add(tb);
        }

        if (wikiArea.IngameName != localArea.IngameName && localArea.IngameName != null &&
            (wikiArea.IngameName ?? "").Trim() != localArea.IngameName.Trim())
        {
            var tb = new WpfTextBlock { FontSize = 10, Foreground = secondary, Margin = new Thickness(0, 1, 0, 1) };
            tb.Inlines.Add(new System.Windows.Documents.Run("Ingame Name: ") { Foreground = tertiary });
            tb.Inlines.Add(new System.Windows.Documents.Run(wikiArea.IngameName ?? "(none)") { Foreground = redBrush });
            tb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = tertiary });
            tb.Inlines.Add(new System.Windows.Documents.Run(localArea.IngameName) { Foreground = greenBrush });
            diffPanel.Children.Add(tb);
        }

        // Build task info for sorting (index + parents from local, fallback wiki)
        var allTaskInfo = new Dictionary<string, (int Index, List<string> Parents)>(StringComparer.Ordinal);
        foreach (var kv in localArea.Tasks.Concat(wikiArea.Tasks))
        {
            if (allTaskInfo.ContainsKey(kv.Key)) continue;
            var f = ParseTaskBlock(kv.Value);
            var idx = f.TryGetValue("index", out var iv) && int.TryParse(iv, out var n) ? n : int.MaxValue;
            var parents = new List<string>();
            if (f.TryGetValue("parents", out var pv))
                parents = System.Text.RegularExpressions.Regex.Matches(pv, @"""([^""]+)""")
                    .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
            allTaskInfo[kv.Key] = (idx, parents);
        }

        // Sort by lowest parent index, then own index (Lua sortTasksByLowestParentIndex)
        int MinParentIndex(string tid)
        {
            if (!allTaskInfo.TryGetValue(tid, out var info)) return int.MaxValue;
            if (info.Parents.Count == 0) return int.MinValue;
            var min = int.MaxValue;
            foreach (var pid in info.Parents)
                if (allTaskInfo.TryGetValue(pid, out var p) && p.Index < min) min = p.Index;
            return min;
        }

        List<string> SortTaskIds(IEnumerable<string> ids) =>
            ids.OrderBy(MinParentIndex)
               .ThenBy(id => allTaskInfo.TryGetValue(id, out var info) ? info.Index : int.MaxValue)
               .ToList();

        // Clickable task header — only the task ID part is interactive (hover underline + click to copy)
        WpfTextBlock ClickableTaskHeader(string prefix, string tid, Brush brush, FontWeight? weight = null)
        {
            var indexStr = allTaskInfo.TryGetValue(tid, out var info) && info.Index < int.MaxValue
                ? $"#{info.Index} " : "";

            var tb = new WpfTextBlock
            {
                FontSize = 10, Foreground = brush,
                FontWeight = weight ?? FontWeights.Normal,
                Margin = new Thickness(0, prefix == "~" ? 2 : 1, 0, 1)
            };
            tb.Inlines.Add(new System.Windows.Documents.Run($"{prefix} Task {indexStr}"));

            var idRun = new System.Windows.Documents.Run(tid)
            {
                Cursor = System.Windows.Input.Cursors.Hand
            };
            idRun.MouseEnter += (s, e) => idRun.TextDecorations = TextDecorations.Underline;
            idRun.MouseLeave += (s, e) => idRun.TextDecorations = null;
            System.Windows.Documents.Run? copiedRun = null;
            idRun.MouseLeftButtonDown += (s, e) =>
            {
                App.NativeSetClipboardText(tid);
                if (copiedRun != null) return;
                copiedRun = new System.Windows.Documents.Run("  Copied!")
                {
                    FontWeight = FontWeights.Normal, FontSize = 9,
                    Foreground = tertiary
                };
                tb.Inlines.Add(copiedRun);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (ts, te) =>
                {
                    tb.Inlines.Remove(copiedRun);
                    copiedRun = null;
                    ((DispatcherTimer)ts!).Stop();
                };
                timer.Start();
            };
            tb.Inlines.Add(idRun);
            return tb;
        }

        // Added tasks
        var addedTasks = SortTaskIds(localArea.Tasks.Keys.Except(wikiArea.Tasks.Keys));
        foreach (var taskId in addedTasks)
            diffPanel.Children.Add(ClickableTaskHeader("+", taskId, greenBrush));

        // Removed tasks
        var removedTasks = SortTaskIds(wikiArea.Tasks.Keys.Except(localArea.Tasks.Keys));
        foreach (var taskId in removedTasks)
            diffPanel.Children.Add(ClickableTaskHeader("\u2212", taskId, redBrush));

        // Modified tasks
        int modifiedTaskCount = 0;
        var commonTasks = SortTaskIds(localArea.Tasks.Keys.Intersect(wikiArea.Tasks.Keys));
        foreach (var taskId in commonTasks)
        {
            var wikiBlock = wikiArea.Tasks[taskId];
            var localBlock = localArea.Tasks[taskId];

            if (NormalizeAreaBlock(wikiBlock) == NormalizeAreaBlock(localBlock)) continue;

            var wikiFields = ParseTaskBlock(wikiBlock);
            var localFields = ParseTaskBlock(localBlock);
            var allKeys = new SortedSet<string>(wikiFields.Keys);
            allKeys.UnionWith(localFields.Keys);

            var hasChanges = false;
            var taskPanel = new StackPanel { Margin = new Thickness(14, 0, 0, 2) };

            foreach (var field in allKeys)
            {
                var hasWiki = wikiFields.TryGetValue(field, out var wikiVal);
                var hasLocal = localFields.TryGetValue(field, out var localVal);

                if (hasWiki && hasLocal && wikiVal == localVal) continue;

                var label = FormatAreaFieldName(field);

                if (hasWiki && hasLocal)
                {
                    var fmtWiki = FormatAreaValue(wikiVal!, field, itemNameMap);
                    var fmtLocal = FormatAreaValue(localVal!, field, itemNameMap);

                    // When formatted values look identical, reveal whitespace
                    if (fmtWiki == fmtLocal)
                    {
                        fmtWiki = RevealWhitespace(wikiVal!.Trim('"'));
                        fmtLocal = RevealWhitespace(localVal!.Trim('"'));
                        if (fmtWiki == fmtLocal) continue;
                    }

                    hasChanges = true;
                    var tb = new WpfTextBlock
                    {
                        FontSize = 10, Foreground = secondary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    };
                    tb.Inlines.Add(new System.Windows.Documents.Run($"{label}: ") { Foreground = tertiary });
                    tb.Inlines.Add(new System.Windows.Documents.Run(fmtWiki) { Foreground = redBrush });
                    tb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = tertiary });
                    tb.Inlines.Add(new System.Windows.Documents.Run(fmtLocal) { Foreground = greenBrush });
                    taskPanel.Children.Add(tb);
                }
                else if (hasLocal)
                {
                    hasChanges = true;
                    taskPanel.Children.Add(new WpfTextBlock
                    {
                        Text = $"+ {label}: {FormatAreaValue(localVal!, field, itemNameMap)}",
                        FontSize = 10, Foreground = greenBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
                else
                {
                    hasChanges = true;
                    taskPanel.Children.Add(new WpfTextBlock
                    {
                        Text = $"\u2212 {label}: {FormatAreaValue(wikiVal!, field, itemNameMap)}",
                        FontSize = 10, Foreground = redBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
            }

            if (hasChanges)
            {
                modifiedTaskCount++;
                diffPanel.Children.Add(ClickableTaskHeader("~", taskId, orangeBrush, FontWeights.SemiBold));
                diffPanel.Children.Add(taskPanel);
            }
        }

        // Build area header with task change counts
        var headerParts = new List<string>();
        if (modifiedTaskCount > 0) headerParts.Add($"{modifiedTaskCount} modified");
        if (addedTasks.Count > 0) headerParts.Add($"+{addedTasks.Count} new");
        if (removedTasks.Count > 0) headerParts.Add($"\u2212{removedTasks.Count} removed");
        var countSuffix = headerParts.Count > 0 ? $" — {string.Join(", ", headerParts)}" : "";

        // Header: use mod.Key as primary; append areaName only if different
        var headerLabel = mod.Key;
        if (areaName != null && areaName != mod.Key)
            headerLabel = $"{mod.Key}  ({areaName})";

        if (diffPanel.Children.Count > 0)
            AddCollapsibleSection(target, $"{headerLabel}{countSuffix}",
                orangeBrush, secondary, diffPanel, defaultExpanded);
        else
        {
            target.Children.Add(new WpfTextBlock
            {
                Text = $"~ {headerLabel} — whitespace only",
                FontSize = 11, Foreground = tertiary, Margin = new Thickness(0, 2, 0, 2)
            });
        }
    }

    private static string FormatAreaFieldName(string field) => field switch
    {
        "index" => "Index",
        "id" => "ID",
        "desc" => "Description",
        "rewards" => "Rewards",
        "parents" => "Parents",
        "children" => "Children",
        "requirements" => "Requirements",
        "name" => "Name",
        "ingameName" => "Ingame Name",
        "release" => "Release Date",
        "unlock" => "Unlock Date",
        _ => field
    };

    private static string FormatAreaValue(string val, string? fieldName = null,
        Dictionary<string, string>? itemNameMap = null)
    {
        if (val == "nil") return "(none)";
        if (val.StartsWith('"') && val.EndsWith('"'))
        {
            var s = val[1..^1];
            if (s.Length > 100) s = s[..97] + "...";
            return s;
        }

        // Requirements: {{name = "Item_03", amount = 1}, {name = "Item_04", amount = 2}}
        if (fieldName == "requirements")
        {
            var reqMatches = System.Text.RegularExpressions.Regex.Matches(
                val, @"name\s*=\s*""([^""]+)"",\s*amount\s*=\s*(\d+)");
            if (reqMatches.Count > 0)
            {
                var items = reqMatches.Select(m =>
                {
                    var itemId = m.Groups[1].Value;
                    var amount = m.Groups[2].Value;
                    var display = ResolveItemDisplay(itemId, itemNameMap);
                    return $"{amount}x {display}";
                });
                return string.Join(", ", items);
            }
        }

        // Rewards: {xp = 50, item = "ChestItem_01"}
        if (fieldName == "rewards")
        {
            var parts = new List<string>();
            var xpMatch = System.Text.RegularExpressions.Regex.Match(val, @"xp\s*=\s*(\d+)");
            if (xpMatch.Success) parts.Add($"{xpMatch.Groups[1].Value} XP");
            var itemMatch = System.Text.RegularExpressions.Regex.Match(val, @"item\s*=\s*""([^""]+)""");
            if (itemMatch.Success)
                parts.Add(ResolveItemDisplay(itemMatch.Groups[1].Value, itemNameMap));
            if (parts.Count > 0) return string.Join(", ", parts);
        }

        // Trim nested table display
        var clean = val.TrimStart('{').TrimEnd('}').Trim();
        if (clean.Length > 100) clean = clean[..97] + "...";
        return string.IsNullOrEmpty(clean) ? "(empty)" : clean;
    }

    /// <summary>
    /// Resolves an item ID to "Display Name [L#]" using the item name map from generated Lua.
    /// Falls back to raw itemId if not found.
    /// </summary>
    private static string ResolveItemDisplay(string itemId, Dictionary<string, string>? itemNameMap)
    {
        if (itemNameMap == null) return itemId;

        // itemNameMap is itemType → name (e.g. "GardenGloves_03" → "Garden Gloves")
        if (itemNameMap.TryGetValue(itemId, out var name))
        {
            // Extract level from itemId suffix (e.g. "GardenGloves_03" → 3)
            var lastUnderscore = itemId.LastIndexOf('_');
            if (lastUnderscore >= 0 && int.TryParse(itemId[(lastUnderscore + 1)..], out var level))
                return $"{name} [L{level}]";
            return name;
        }
        return itemId;
    }

    // ── Changelog ────────────────────────────────────────────────────

    /// <summary>
    /// Populates a StackPanel with the full changelog detail (modified with field diffs, added, removed).
    /// Each category is a separate collapsible section. Shared by both inline card and confirmation dialog.
    /// Initial batch of 50 items shown, with "Show all N items..." link to reveal the rest.
    /// </summary>
    private void BuildChangelogDetail(StackPanel root, ChangelogData cl,
        Brush primary, Brush secondary, Brush tertiary,
        Dictionary<string, string>? nameMap = null, bool isAreaMode = false)
    {
        const int initialCount = 50;
        var greenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0xB8, 0x4F));
        var redBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0x50, 0x60));
        var orangeBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25));

        string DisplayName(string key) =>
            nameMap?.GetValueOrDefault(key) is string dn ? $"{dn}  ({key})" : key;

        // Helper: build a single modified entry with field-level diffs
        void AddModifiedEntry(StackPanel target, ModifiedEntry mod)
        {
            target.Children.Add(new WpfTextBlock
            {
                Text = $"~ {DisplayName(mod.Key)}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = orangeBrush, Margin = new Thickness(0, 3, 0, 2)
            });

            var wikiFields = ParseLuaEntryFields(mod.WikiValue);
            var localFields = ParseLuaEntryFields(mod.LocalValue);
            var allKeys = new SortedSet<string>(wikiFields.Keys);
            allKeys.UnionWith(localFields.Keys);

            var diffPanel = new StackPanel { Margin = new Thickness(14, 0, 0, 4) };

            foreach (var field in allKeys)
            {
                var hasWiki = wikiFields.TryGetValue(field, out var wikiVal);
                var hasLocal = localFields.TryGetValue(field, out var localVal);

                if (hasWiki && hasLocal && wikiVal == localVal) continue;

                var label = FormatFieldName(field);

                if (hasWiki && hasLocal)
                {
                    var fmtWiki = FormatLuaValue(wikiVal!, field, nameMap);
                    var fmtLocal = FormatLuaValue(localVal!, field, nameMap);

                    // When formatted values look identical, reveal whitespace from raw values
                    if (fmtWiki == fmtLocal)
                    {
                        fmtWiki = RevealWhitespace(wikiVal!.Trim('"'));
                        fmtLocal = RevealWhitespace(localVal!.Trim('"'));
                    }

                    var tb = new WpfTextBlock
                    {
                        FontSize = 10, Foreground = secondary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    };
                    tb.Inlines.Add(new System.Windows.Documents.Run($"{label}: ")
                        { Foreground = tertiary });
                    tb.Inlines.Add(new System.Windows.Documents.Run(fmtWiki)
                        { Foreground = redBrush });
                    tb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ")
                        { Foreground = tertiary });
                    tb.Inlines.Add(new System.Windows.Documents.Run(fmtLocal)
                        { Foreground = greenBrush });
                    diffPanel.Children.Add(tb);
                }
                else if (hasLocal)
                {
                    diffPanel.Children.Add(new WpfTextBlock
                    {
                        Text = $"+ {label}: {FormatLuaValue(localVal!, field, nameMap)}",
                        FontSize = 10, Foreground = greenBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
                else
                {
                    diffPanel.Children.Add(new WpfTextBlock
                    {
                        Text = $"\u2212 {label}: {FormatLuaValue(wikiVal!, field, nameMap)}",
                        FontSize = 10, Foreground = redBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
            }

            if (diffPanel.Children.Count > 0)
                target.Children.Add(diffPanel);
        }

        // Helper: add simple name entries (for Added / Removed)
        void AddSimpleEntries(StackPanel target, IEnumerable<string> items, string prefix, Brush brush)
        {
            foreach (var name in items)
                target.Children.Add(new WpfTextBlock
                {
                    Text = $"  {prefix} {DisplayName(name)}", FontSize = 11,
                    Foreground = brush, Margin = new Thickness(0, 1, 0, 1)
                });
        }

        // Helper: add a "Show all N items..." link + hidden panel with remaining items
        void AddShowAllLink(StackPanel target, int totalCount, Brush linkBrush, Action<StackPanel> buildRemaining)
        {
            var morePanel = new StackPanel { Visibility = Visibility.Collapsed };
            buildRemaining(morePanel);

            var showAll = new WpfTextBlock
            {
                Text = $"  Show all {totalCount} items...",
                FontSize = 11, Foreground = linkBrush,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 4, 0, 2)
            };
            showAll.TextDecorations = TextDecorations.Underline;
            showAll.MouseLeftButtonDown += (_, _) =>
            {
                morePanel.Visibility = Visibility.Visible;
                showAll.Visibility = Visibility.Collapsed;
            };

            target.Children.Add(showAll);
            target.Children.Add(morePanel);
        }

        if (cl.Modified.Count > 0)
        {
            var modContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            if (isAreaMode)
            {
                var expandAreas = cl.Modified.Count <= 10;
                foreach (var mod in cl.Modified)
                    BuildAreaModifiedDetail(modContent, mod, secondary, tertiary, expandAreas, nameMap);
            }
            else
            {
                foreach (var mod in cl.Modified.Take(initialCount))
                    AddModifiedEntry(modContent, mod);

                if (cl.Modified.Count > initialCount)
                    AddShowAllLink(modContent, cl.Modified.Count, secondary,
                        panel => { foreach (var mod in cl.Modified.Skip(initialCount)) AddModifiedEntry(panel, mod); });
            }

            AddCollapsibleSection(root, $"Modified ({cl.Modified.Count})", orangeBrush, secondary, modContent);
        }

        if (cl.Added.Count > 0)
        {
            var addContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            AddSimpleEntries(addContent, cl.Added.Take(initialCount), "+", greenBrush);

            if (cl.Added.Count > initialCount)
                AddShowAllLink(addContent, cl.Added.Count, secondary,
                    panel => AddSimpleEntries(panel, cl.Added.Skip(initialCount), "+", greenBrush));

            AddCollapsibleSection(root, $"Added ({cl.Added.Count})", greenBrush, secondary, addContent);
        }

        if (cl.Removed.Count > 0)
        {
            var remContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            AddSimpleEntries(remContent, cl.Removed.Take(initialCount), "\u2212", redBrush);

            if (cl.Removed.Count > initialCount)
                AddShowAllLink(remContent, cl.Removed.Count, secondary,
                    panel => AddSimpleEntries(panel, cl.Removed.Skip(initialCount), "\u2212", redBrush));

            AddCollapsibleSection(root, $"Removed ({cl.Removed.Count})", redBrush, secondary, remContent);
        }

        // Renamed (CBE event rename: CBE_Easter2025_Foo_NN \u2194 CBE_SweetMess_Foo_NN). Items are shown
        // here instead of in Removed/Added so the user sees they're not lost \u2014 just relocated.
        // Renamed items are excluded from the archive (they live in the new id).
        var renamedList = cl.Renamed;
        if (renamedList != null && renamedList.Count > 0)
        {
            var blueBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0xA0, 0xE8));
            var renContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            void AddRenamedEntries(StackPanel panel, IEnumerable<RenamedEntry> entries)
            {
                foreach (var r in entries)
                {
                    var line = new WpfTextBlock
                    {
                        FontSize = 11, Foreground = blueBrush,
                        Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap
                    };
                    line.Inlines.Add(new System.Windows.Documents.Run("\u21bb ") { Foreground = blueBrush });
                    line.Inlines.Add(new System.Windows.Documents.Run(r.OldId) { Foreground = redBrush });
                    line.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = secondary });
                    line.Inlines.Add(new System.Windows.Documents.Run(r.NewId) { Foreground = greenBrush });
                    if (!string.IsNullOrEmpty(r.OldChain) && r.OldChain != r.NewChain)
                    {
                        line.Inlines.Add(new System.Windows.Documents.Run($"  ({r.OldChain} \u2192 {r.NewChain ?? "?"})")
                        { Foreground = tertiary, FontSize = 10 });
                    }
                    panel.Children.Add(line);
                }
            }
            AddRenamedEntries(renContent, renamedList.Take(initialCount));
            if (renamedList.Count > initialCount)
                AddShowAllLink(renContent, renamedList.Count, secondary,
                    panel => AddRenamedEntries(panel, renamedList.Skip(initialCount)));

            AddCollapsibleSection(root, $"Renamed ({renamedList.Count})", blueBrush, secondary, renContent);
        }

        // Archived: items preserved either in Module:Datatable/Items/Archive (full data backed up) or
        // in Module:Datatable/Items/Mapping (override + enrichment). Reassures user nothing was silently lost.
        var archivedList = cl.Archived;
        if (archivedList != null && archivedList.Count > 0)
        {
            var goldBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xA8, 0x4A));
            var archContent = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
            void AddArchivedEntries(StackPanel panel, IEnumerable<ArchivedEntry> entries)
            {
                foreach (var a in entries)
                {
                    var line = new WpfTextBlock
                    {
                        FontSize = 11, Foreground = primary,
                        Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap
                    };
                    line.Inlines.Add(new System.Windows.Documents.Run("📦 ") { Foreground = goldBrush });
                    line.Inlines.Add(new System.Windows.Documents.Run(a.Id) { Foreground = primary });
                    var whereLabel = a.Where == "archive" ? "→ Archive" : "→ Mapping";
                    line.Inlines.Add(new System.Windows.Documents.Run($"  {whereLabel}")
                    { Foreground = goldBrush, FontSize = 10 });
                    if (!string.IsNullOrEmpty(a.Chain))
                        line.Inlines.Add(new System.Windows.Documents.Run($"  ({a.Chain})")
                        { Foreground = tertiary, FontSize = 10 });
                    panel.Children.Add(line);
                }
            }
            AddArchivedEntries(archContent, archivedList.Take(initialCount));
            if (archivedList.Count > initialCount)
                AddShowAllLink(archContent, archivedList.Count, secondary,
                    panel => AddArchivedEntries(panel, archivedList.Skip(initialCount)));

            AddCollapsibleSection(root, $"Archived ({archivedList.Count})", goldBrush, secondary, archContent);
        }
    }

    private static void AddCollapsibleSection(StackPanel root, string headerText,
        Brush accentBrush, Brush secondaryBrush, StackPanel content, bool defaultExpanded = false)
    {
        var collapsed = !defaultExpanded;
        var arrow = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = collapsed
                ? Wpf.Ui.Controls.SymbolRegular.ChevronRight24
                : Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = secondaryBrush, Margin = new Thickness(0, 0, 6, 0)
        };
        var headerTb = new WpfTextBlock
        {
            Text = headerText, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = accentBrush, VerticalAlignment = VerticalAlignment.Center
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        header.Children.Add(arrow);
        header.Children.Add(headerTb);

        content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        header.MouseLeftButtonDown += (_, _) =>
        {
            if (content.Visibility == Visibility.Collapsed)
            {
                content.Visibility = Visibility.Visible;
                arrow.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
            }
            else
            {
                content.Visibility = Visibility.Collapsed;
                arrow.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronRight24;
            }
        };

        root.Children.Add(header);
        root.Children.Add(content);
    }

    /// <summary>
    /// Parses top-level key = value fields from a Lua table entry like {k1 = v1, k2 = v2}.
    /// Handles nested braces and quoted strings.
    /// </summary>
    private static Dictionary<string, string> ParseLuaEntryFields(string entry)
    {
        var fields = new Dictionary<string, string>();
        if (entry.Length < 2 || entry[0] != '{') return fields;

        var inner = entry[1..^1].Trim();

        var parts = new List<string>();
        int depth = 0;
        bool inString = false;
        int start = 0;

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '"' && (i == 0 || inner[i - 1] != '\\')) inString = !inString;
            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(inner[start..i].Trim());
                    start = i + 1;
                }
            }
        }
        if (start < inner.Length)
            parts.Add(inner[start..].Trim());

        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf(" = ", StringComparison.Ordinal);
            if (eqIdx > 0)
            {
                var key = part[..eqIdx].Trim().Trim('[', ']', '"');
                var val = part[(eqIdx + 3)..].Trim();
                fields[key] = val;
            }
        }

        return fields;
    }

    /// <summary>
    /// Builds itemType → displayName map. Prefers wiki-mapped names from DataService.Chains
    /// (which has wiki mapping applied). Falls back to parsing generated items Lua (raw names).
    /// </summary>
    private Dictionary<string, string> BuildItemNameMap()
    {
        var map = new Dictionary<string, string>();

        // Prefer wiki-mapped names from loaded DataService
        var ds = _main.DataService;
        if (ds != null && ds.Chains.Count > 0)
        {
            foreach (var chain in ds.Chains)
                foreach (var item in chain.Items)
                    if (!string.IsNullOrEmpty(item.ItemType))
                        map.TryAdd(item.ItemType, chain.DisplayName);
            return map;
        }

        // Fallback: parse raw names from generated items Lua
        var pattern = @"\[""([^""]+)""\] = \{name = ""([^""]+)""";

        if (!string.IsNullOrEmpty(_lastCombined))
        {
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(_lastCombined, pattern))
                map.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }
        else
        {
            foreach (var chunk in _lastItemChunks)
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(chunk.Lua, pattern))
                    map.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }

        return map;
    }

    private static string FormatFieldName(string field) => field switch
    {
        "name" => "Name",
        "level" => "Level",
        "isGen" => "Generator",
        "isTemp" => "Temporary",
        "chainName" => "Chain",
        "bubble" => "Bubble",
        "odds" => "Drop odds",
        "desc" => "Description",
        _ => field
    };

    private static string FormatLuaValue(string val, string fieldName,
        Dictionary<string, string>? nameMap = null)
    {
        if (val == "nil") return "(none)";
        if (val == "true") return "yes";
        if (val == "false") return "no";

        // Quoted string
        if (val.StartsWith('"') && val.EndsWith('"'))
        {
            var s = val[1..^1];
            if (fieldName == "desc" && s.Length > 80)
                s = s[..77] + "...";
            return s;
        }

        // Simple number
        if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return val;

        // Odds array: {{id = "X", value = 0.5}, ...}
        if (fieldName == "odds")
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                val, @"id\s*=\s*""([^""]+)"",\s*value\s*=\s*([0-9.eE+\-]+)");
            if (matches.Count > 0)
            {
                var items = matches.Select(m =>
                {
                    var id = m.Groups[1].Value;
                    var display = nameMap?.GetValueOrDefault(id) ?? id;
                    if (double.TryParse(m.Groups[2].Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        // Values ≤1 are probabilities → show as %, otherwise show raw weight
                        if (v <= 1.0)
                        {
                            var pct = v * 100;
                            return pct == Math.Floor(pct)
                                ? $"{display} {pct:F0}%"
                                : $"{display} {pct:F1}%";
                        }
                        return $"{display} (\u00D7{v:F0})";
                    }
                    return $"{display} {m.Groups[2].Value}";
                });
                return string.Join(", ", items);
            }
        }

        // Bubble: {duration = N, cost = N, spawnOdds = N}
        if (fieldName == "bubble")
        {
            var dur = System.Text.RegularExpressions.Regex.Match(val, @"duration\s*=\s*(\d+)");
            var cost = System.Text.RegularExpressions.Regex.Match(val, @"cost\s*=\s*(\d+)");
            var odds = System.Text.RegularExpressions.Regex.Match(val, @"spawnOdds\s*=\s*(\d+)");
            if (dur.Success)
            {
                var parts = new List<string>();
                var mins = int.Parse(dur.Groups[1].Value);
                parts.Add(mins >= 60
                    ? $"{mins / 60}h{(mins % 60 > 0 ? $"{mins % 60}m" : "")}"
                    : $"{mins}m");
                if (cost.Success) parts.Add($"cost {cost.Groups[1].Value}");
                if (odds.Success) parts.Add($"spawn {odds.Groups[1].Value}%");
                return string.Join(", ", parts);
            }
        }

        // Generic table — strip outer braces and ["..."] syntax
        var clean = val
            .Replace("[\"", "").Replace("\"]", "")
            .TrimStart('{').TrimEnd('}').Trim();
        if (clean.Length > 100) clean = clean[..97] + "...";
        return clean;
    }

    /// <summary>
    /// Makes leading/trailing whitespace visible using · markers.
    /// Used when formatted diff values look identical but raw values differ.
    /// </summary>
    private static string RevealWhitespace(string s)
    {
        var leading = s.Length - s.TrimStart().Length;
        var trailing = s.Length - s.TrimEnd().Length;
        if (leading == 0 && trailing == 0)
            return $"\"{s}\"";
        var trimmed = s[leading..(s.Length - trailing)];
        return $"\"{new string('\u00B7', leading)}{trimmed}{new string('\u00B7', trailing)}\"";
    }
}
