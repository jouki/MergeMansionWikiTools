using System.ComponentModel;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MergeMansionWikiTools.Views;

public partial class MoveItemsDialog : FluentWindow
{
    private readonly MainWindow _main;
    private readonly ParsedChain _sourceChain;
    private readonly List<ParsedItem> _selectedItems;
    private readonly bool _singleItemMode;

    // ── Pre-built data for instant autocomplete ──
    private readonly string[] _allNames;           // sorted display names
    private readonly string[] _allNamesLower;      // lowercase for fast search
    private readonly Dictionary<string, ParsedChain> _chainByName; // O(1) conflict lookup

    private ParsedChain? _conflictChain;
    private CancellationTokenSource? _searchCts;
    private bool _hasConflict;
    private bool _hasLevelCollision;
    private bool _mappingOnly;
    private bool _suppressTextChanged; // prevent re-entry when setting text from suggestion
    private const int MaxSuggestions = 30;

    // ── Page move state ──
    private bool _sourcePageExists;

    // ── Backlinks state ──
    private CancellationTokenSource? _backlinksCts;
    private List<string> _allBacklinks = new();
    private List<BacklinkItem> _currentPageItems = new();
    private bool _backlinksReady; // true when Loaded or Empty
    private int _backlinksPage;
    private const int BacklinksPerPage = 15;

    public MoveItemsDialog(
        MainWindow main,
        ParsedChain sourceChain,
        List<ParsedItem> selectedItems,
        bool singleItemMode)
    {
        _main = main;
        _sourceChain = sourceChain;
        _selectedItems = selectedItems;
        _singleItemMode = singleItemMode;

        // ── Pre-build lookup structures (fast, runs once) ──
        var chains = main.DataService?.Chains ?? new List<ParsedChain>();

        _chainByName = new Dictionary<string, ParsedChain>(StringComparer.OrdinalIgnoreCase);
        var nameSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chains)
        {
            nameSet.Add(c.DisplayName);
            _chainByName.TryAdd(c.DisplayName, c);
        }
        _allNames = nameSet.ToArray();
        _allNamesLower = _allNames.Select(n => n.ToLowerInvariant()).ToArray();

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        // Cap height at 75% of the screen the window is on
        Loaded += (_, _) =>
        {
            MaxHeight = GetScreenWorkAreaHeight() * 0.75;
            txtChainName.Focus();
        };

        // ESC key — confirmation before closing
        PreviewKeyDown += Window_PreviewKeyDown;

        // Header
        if (singleItemMode)
            txtHeader.Text = $"Set level for item from \"{sourceChain.DisplayName}\"";
        else
            txtHeader.Text = $"Moving {selectedItems.Count} item{(selectedItems.Count > 1 ? "s" : "")} from \"{sourceChain.DisplayName}\"";

        // Item list
        itemsList.ItemsSource = selectedItems;

        // Move page checkbox — label set once, check existence immediately
        chkMovePage.Content = $"Move wiki page \"{sourceChain.DisplayName}\"";
        _ = CheckSourcePageExistsAsync(sourceChain.DisplayName);

        // Load backlinks once (source chain never changes)
        _ = LoadBacklinksAsync(sourceChain.DisplayName);

        // Level field (single-item mode)
        if (singleItemMode)
        {
            levelPanel.Visibility = Visibility.Visible;
            nbLevel.Value = selectedItems[0].Level;
        }

        // Pre-fill with source chain name for single-item mode
        if (singleItemMode)
        {
            _suppressTextChanged = true;
            txtChainName.Text = sourceChain.DisplayName;
            _suppressTextChanged = false;
        }

    }

    // ── Autocomplete ──────────────────────────────────────────────────

    private void TxtChainName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;

        var query = txtChainName.Text?.Trim() ?? "";

        // Cancel previous search
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // Capture pre-built arrays (thread-safe, immutable)
        var names = _allNames;
        var namesLower = _allNamesLower;

        if (query.Length == 0)
        {
            // Show all names (up to limit)
            suggestList.ItemsSource = names.Length <= MaxSuggestions
                ? names
                : names.AsSpan(0, MaxSuggestions).ToArray();
            suggestPopup.IsOpen = true;
            HideConflict();
            CheckLevelCollision();
            UpdateConfirmEnabled();
            return;
        }

        // Run filtering on thread pool — zero UI thread blocking
        var queryLower = query.ToLowerInvariant();
        Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;

            // Two-pass: prefix matches first, then contains matches
            var prefixMatches = new List<string>();
            var containsMatches = new List<string>();

            for (int i = 0; i < namesLower.Length; i++)
            {
                if (token.IsCancellationRequested) return;

                if (namesLower[i].StartsWith(queryLower))
                    prefixMatches.Add(names[i]);
                else if (namesLower[i].Contains(queryLower))
                    containsMatches.Add(names[i]);

                // Early exit once we have enough
                if (prefixMatches.Count + containsMatches.Count >= MaxSuggestions)
                    break;
            }

            if (token.IsCancellationRequested) return;

            // Combine: prefix matches before contains matches
            var results = new List<string>(prefixMatches.Count + containsMatches.Count);
            results.AddRange(prefixMatches);
            var remaining = MaxSuggestions - results.Count;
            if (remaining > 0 && containsMatches.Count > 0)
                results.AddRange(containsMatches.Take(remaining));

            if (token.IsCancellationRequested) return;

            // Post results to UI thread
            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested) return;

                suggestList.ItemsSource = results;
                suggestPopup.IsOpen = results.Count > 0;

                // Run conflict + level checks (fast, O(1) dictionary lookup)
                CheckConflict(query);
                CheckLevelCollision();
                UpdateConfirmEnabled();
            });
        }, token);
    }

    private void TxtChainName_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!suggestPopup.IsOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                if (suggestList.Items.Count > 0)
                {
                    suggestList.SelectedIndex = Math.Min(
                        (suggestList.SelectedIndex + 1),
                        suggestList.Items.Count - 1);
                    suggestList.ScrollIntoView(suggestList.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (suggestList.SelectedIndex > 0)
                {
                    suggestList.SelectedIndex--;
                    suggestList.ScrollIntoView(suggestList.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Tab:
                if (suggestList.SelectedItem is string selected)
                    AcceptSuggestion(selected);
                else if (suggestList.Items.Count == 1 && suggestList.Items[0] is string only)
                    AcceptSuggestion(only);
                suggestPopup.IsOpen = false;
                e.Handled = e.Key == Key.Enter; // don't suppress Tab focus navigation
                break;

            case Key.Escape:
                suggestPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SuggestList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (suggestList.SelectedItem is string selected)
        {
            AcceptSuggestion(selected);
            suggestPopup.IsOpen = false;
        }
    }

    private void AcceptSuggestion(string name)
    {
        _suppressTextChanged = true;
        txtChainName.Text = name;
        txtChainName.CaretIndex = name.Length;
        _suppressTextChanged = false;

        // Run checks immediately for the accepted name
        CheckConflict(name);
        CheckLevelCollision();
        UpdateConfirmEnabled();
    }

    // ── Conflict detection (O(1) dictionary lookup) ───────────────────

    private void CheckConflict(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            HideConflict();
            UpdateBacklinksDimming();
            return;
        }

        // O(1) lookup instead of LINQ scan
        if (!_chainByName.TryGetValue(name, out var match) || match == _sourceChain)
        {
            HideConflict();
            UpdateBacklinksDimming();
            return;
        }

        // Show conflict
        _conflictChain = match;
        _hasConflict = true;

        txtConflictTitle.Text = $"\"{match.DisplayName}\" already exists";
        txtConflictDetail.Text = $"Internal: {match.ConfigKey} · {match.Items.Count} items";
        btnMoveConflictFirst.Content = $"Move \"{match.DisplayName}\" first";

        conflictPanel.Visibility = Visibility.Visible;
        btnConfirm.IsEnabled = false;

        // Conflict → dim backlinks panel
        UpdateBacklinksDimming();

        // Load conflict chain images asynchronously
        _ = LoadConflictImagesAsync(match);
    }

    /// <summary>
    /// Dims/un-dims the backlinks panel based on conflict state and move page checkbox.
    /// Backlinks data stays loaded — only visual state changes.
    /// </summary>
    private void UpdateBacklinksDimming()
    {
        if (!_backlinksReady) return;
        bool enabled = !_hasConflict && chkMovePage.IsChecked == true;
        backlinksPanel.IsEnabled = enabled;
        backlinksPanel.Opacity = enabled ? 1.0 : 0.4;
    }

    private void HideConflict()
    {
        _conflictChain = null;
        _hasConflict = false;
        _mappingOnly = false;
        conflictPanel.Visibility = Visibility.Collapsed;
        chkMappingOnly.IsChecked = false;
        conflictImages.Children.Clear();
        UpdateConfirmEnabled();
    }

    private async Task LoadConflictImagesAsync(ParsedChain chain)
    {
        conflictImages.Children.Clear();

        try
        {
            var levels = chain.Items.Select(i => i.Level).ToArray();
            var images = await WikiMappingService.CheckChainImagesAsync(chain.DisplayName, levels);

            // Check if conflict is still relevant
            if (_conflictChain != chain) return;

            foreach (var item in chain.Items.OrderBy(i => i.Level))
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 4), Width = 56 };

                if (images.TryGetValue(item.Level, out var imageUrl) && imageUrl != null)
                {
                    var img = new System.Windows.Controls.Image
                    {
                        Width = 48, Height = 48,
                        Stretch = Stretch.Uniform,
                    };
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(imageUrl);
                        bi.DecodePixelWidth = 96; // decode at 2x for crisp display, WPF scales to 48px
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        img.Source = bi;
                    }
                    catch
                    {
                        img.Source = null;
                    }
                    sp.Children.Add(img);
                }
                else
                {
                    var placeholder = new Border
                    {
                        Width = 48, Height = 48,
                        Background = new SolidColorBrush(Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
                        CornerRadius = new CornerRadius(4),
                        Child = new SymbolIcon
                        {
                            Symbol = SymbolRegular.Image24,
                            FontSize = 20,
                            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush")
                        }
                    };
                    sp.Children.Add(placeholder);
                }

                var lvlText = new TextBlock
                {
                    Text = $"Lv{item.Level}",
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                sp.Children.Add(lvlText);

                var nameText = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 9,
                    Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 56
                };
                sp.Children.Add(nameText);

                conflictImages.Children.Add(sp);
            }
        }
        catch
        {
            // Image loading failed — conflict panel still shows without images
        }
    }

    // ── Conflict resolution ───────────────────────────────────────────

    private void BtnMoveConflictFirst_Click(object sender, RoutedEventArgs e)
    {
        if (_conflictChain == null) return;

        var conflictDialog = new MoveItemsDialog(
            _main, _conflictChain,
            _conflictChain.Items.ToList(),
            singleItemMode: false);
        conflictDialog.Owner = this;

        if (conflictDialog.ShowDialog() == true)
        {
            // Conflict chain was moved — re-check
            var name = txtChainName.Text?.Trim() ?? "";
            CheckConflict(name);

            // If conflict resolved, load backlinks
        }
    }

    private void BtnChooseOtherName_Click(object sender, RoutedEventArgs e)
    {
        HideConflict();
        _suppressTextChanged = true;
        txtChainName.Text = "";
        _suppressTextChanged = false;
        txtChainName.Focus();
    }

    // ── Level collision detection ─────────────────────────────────────

    private void NbLevel_ValueChanged(object sender, RoutedEventArgs e)
    {
        CheckLevelCollision();
    }

    private void CheckLevelCollision()
    {
        if (!_singleItemMode || nbLevel == null) return;

        _hasLevelCollision = false;
        levelWarning.Visibility = Visibility.Collapsed;

        var targetName = txtChainName.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(targetName)) { UpdateConfirmEnabled(); return; }

        var desiredLevel = (int)(nbLevel.Value ?? _selectedItems[0].Level);

        // O(1) lookup
        if (!_chainByName.TryGetValue(targetName, out var targetChain))
        {
            UpdateConfirmEnabled();
            return;
        }

        // Check for level collision (exclude the item being moved)
        var collision = targetChain.Items.FirstOrDefault(i =>
            i.Level == desiredLevel && i != _selectedItems[0]);

        if (collision != null)
        {
            _hasLevelCollision = true;
            txtLevelWarning.Text = $"Level {desiredLevel} is already taken by \"{collision.Name}\"";
            levelWarning.Visibility = Visibility.Visible;
        }

        UpdateConfirmEnabled();
    }

    private void UpdateConfirmEnabled()
    {
        if (btnConfirm == null) return;
        btnConfirm.IsEnabled = (!_hasConflict || _mappingOnly) && !_hasLevelCollision
            && !string.IsNullOrWhiteSpace(txtChainName.Text)
            && _backlinksReady;
    }

    private void ChkMappingOnly_Changed(object sender, RoutedEventArgs e)
    {
        _mappingOnly = chkMappingOnly.IsChecked == true;
        UpdateConfirmEnabled();
    }

    // ── Backlinks panel ───────────────────────────────────────────────

    private enum BacklinksPanelState { Inactive, Loading, Loaded, Empty }

    private void SetBacklinksState(BacklinksPanelState state)
    {
        switch (state)
        {
            case BacklinksPanelState.Inactive:
                backlinksPanel.IsEnabled = false;
                backlinksPanel.Opacity = 0.4;
                backlinksProgress.Visibility = Visibility.Collapsed;
                txtBacklinksEmpty.Visibility = Visibility.Collapsed;
                backlinksList.Visibility = Visibility.Collapsed;
                backlinksActions.Visibility = Visibility.Collapsed;
                backlinksPagination.Visibility = Visibility.Collapsed;
                _backlinksReady = false;
                break;

            case BacklinksPanelState.Loading:
                backlinksPanel.IsEnabled = true;
                backlinksPanel.Opacity = 1.0;
                backlinksProgress.Visibility = Visibility.Visible;
                txtBacklinksEmpty.Visibility = Visibility.Collapsed;
                backlinksList.Visibility = Visibility.Collapsed;
                backlinksActions.Visibility = Visibility.Collapsed;
                backlinksPagination.Visibility = Visibility.Collapsed;
                _backlinksReady = false;
                break;

            case BacklinksPanelState.Loaded:
                backlinksProgress.Visibility = Visibility.Collapsed;
                txtBacklinksEmpty.Visibility = Visibility.Collapsed;
                backlinksList.Visibility = Visibility.Visible;
                backlinksActions.Visibility = Visibility.Visible;
                backlinksPagination.Visibility = _allBacklinks.Count > BacklinksPerPage
                    ? Visibility.Visible : Visibility.Collapsed;
                _backlinksReady = true;
                UpdateBacklinksDimming();
                break;

            case BacklinksPanelState.Empty:
                backlinksProgress.Visibility = Visibility.Collapsed;
                txtBacklinksEmpty.Visibility = Visibility.Visible;
                backlinksList.Visibility = Visibility.Collapsed;
                backlinksActions.Visibility = Visibility.Collapsed;
                backlinksPagination.Visibility = Visibility.Collapsed;
                _backlinksReady = true;
                UpdateBacklinksDimming();
                break;
        }

        UpdateConfirmEnabled();
    }

    private async Task CheckSourcePageExistsAsync(string sourceChainName)
    {
        try
        {
            _sourcePageExists = await WikiMappingService.CheckPageExistsAsync(sourceChainName);
            chkMovePage.IsEnabled = _sourcePageExists;
            chkMovePage.IsChecked = _sourcePageExists;
            chkMovePage.ToolTip = _sourcePageExists ? null : "Page does not exist on wiki";
        }
        catch
        {
            // API failure — leave disabled
        }
    }

    private async Task LoadBacklinksAsync(string sourceChainName)
    {
        // Cancel previous load
        _backlinksCts?.Cancel();
        _backlinksCts = new CancellationTokenSource();
        var ct = _backlinksCts.Token;

        SetBacklinksState(BacklinksPanelState.Loading);

        try
        {
            var backlinks = await Task.Run(
                () => WikiMappingService.GetBacklinksAsync(sourceChainName, ct), ct);

            if (ct.IsCancellationRequested) return;

            _allBacklinks = backlinks;

            if (backlinks.Count == 0)
            {
                txtBacklinksHeader.Text = "Pages referencing this chain";
                SetBacklinksState(BacklinksPanelState.Empty);
            }
            else
            {
                txtBacklinksHeader.Text = $"Pages referencing \"{sourceChainName}\" ({backlinks.Count})";
                _backlinksPage = 0;
                ShowBacklinksPage(0);
                SetBacklinksState(BacklinksPanelState.Loaded);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled — do nothing
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            txtBacklinksHeader.Text = "Pages referencing this chain";
            txtBacklinksEmpty.Text = $"Failed to load: {ex.Message}";
            SetBacklinksState(BacklinksPanelState.Empty);
        }
    }

    private void ShowBacklinksPage(int page)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling((double)_allBacklinks.Count / BacklinksPerPage));
        page = Math.Clamp(page, 0, totalPages - 1);
        _backlinksPage = page;

        var start = page * BacklinksPerPage;
        var count = Math.Min(BacklinksPerPage, _allBacklinks.Count - start);

        _currentPageItems = _allBacklinks
            .Skip(start).Take(count)
            .Select(t => new BacklinkItem { Title = t, IsChecked = false })
            .ToList();

        backlinksList.ItemsSource = _currentPageItems;

        // Update pagination UI
        txtBacklinksPageInfo.Text = $"Page {page + 1} of {totalPages}";
        btnBacklinksPrev.IsEnabled = page > 0;
        btnBacklinksNext.IsEnabled = page < totalPages - 1;
    }

    private void BtnBacklinksSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _currentPageItems)
            item.IsChecked = true;
    }

    private void BtnBacklinksDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _currentPageItems)
            item.IsChecked = false;
    }

    private void BtnBacklinksPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_backlinksPage > 0)
            ShowBacklinksPage(_backlinksPage - 1);
    }

    private void BtnBacklinksNext_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = (int)Math.Ceiling((double)_allBacklinks.Count / BacklinksPerPage);
        if (_backlinksPage < totalPages - 1)
            ShowBacklinksPage(_backlinksPage + 1);
    }

    /// <summary>
    /// Collects all checked backlink titles from the current page.
    /// Items on non-visited pages are unchecked by default.
    /// </summary>
    private List<string> GetCheckedBacklinks()
    {
        var result = new List<string>();
        foreach (var item in _currentPageItems)
        {
            if (item.IsChecked)
                result.Add(item.Title);
        }
        return result;
    }

    // ── Move page checkbox → backlinks dependency ─────────────────────

    private void ChkMovePage_Changed(object sender, RoutedEventArgs e)
    {
        UpdateBacklinksDimming();
    }

    // ── Confirm / Cancel ──────────────────────────────────────────────

    private static async Task<Dictionary<string, int>> CountReferencesAsync(
        List<string> pages, string oldName)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var client = new HttpClient();

        // Fetch in parallel (max 6 concurrent)
        var semaphore = new SemaphoreSlim(6);
        var tasks = pages.Select(async page =>
        {
            await semaphore.WaitAsync();
            try
            {
                var wikitext = await WikiMappingService.FetchPageWikitextAsync(client, page);
                var count = WikiMappingService.CountChainReferences(wikitext, oldName);
                lock (result)
                    result[page] = count;
            }
            catch
            {
                lock (result)
                    result[page] = -1; // error
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
        return result;
    }

    private UIElement BuildPreviewContent(string source, string target, bool isRename,
        int? level, Dictionary<string, int>? refCounts = null,
        Dictionary<int, string?>? imageUrls = null,
        string? oldImageBase = null, string? newImageBase = null)
    {
        var root = new StackPanel { MaxWidth = 500 };
        var accent = (Brush)FindResource("AccentFillColorDefaultBrush");
        var primary = (Brush)FindResource("TextFillColorPrimaryBrush");
        var secondary = (Brush)FindResource("TextFillColorSecondaryBrush");
        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");
        var subtle = (Brush)FindResource("SubtleFillColorSecondaryBrush");

        // ── Header: "Source → Target" (single line, wraps if needed) ──
        if (isRename)
        {
            var header = new TextBlock
            {
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4)
            };
            header.Inlines.Add(new System.Windows.Documents.Run(source) { Foreground = primary });
            header.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = tertiary });
            header.Inlines.Add(new System.Windows.Documents.Run(target) { Foreground = accent });
            root.Children.Add(header);
        }
        else
        {
            root.Children.Add(new TextBlock
            {
                Text = target, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = primary, Margin = new Thickness(0, 0, 0, 4)
            });
        }

        // Separator
        root.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 6, 0, 10),
            Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
        });

        // ── Helper: add a step row ──
        void AddStep(string icon, string title, string? detail = null, string? detail2 = null)
        {
            var row = new Border
            {
                Background = subtle, CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconTb = new TextBlock
            {
                Text = icon, FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetColumn(iconTb, 0);
            grid.Children.Add(iconTb);

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = primary, TextWrapping = TextWrapping.Wrap
            });
            if (detail != null)
                content.Children.Add(new TextBlock
                {
                    Text = detail, FontSize = 11, Foreground = secondary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });
            if (detail2 != null)
                content.Children.Add(new TextBlock
                {
                    Text = detail2, FontSize = 10, Foreground = tertiary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });

            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            row.Child = grid;
            root.Children.Add(row);
        }

        // ── 1. Lua mapping ──
        if (_singleItemMode && level.HasValue)
            AddStep("\uD83D\uDCDD", "Update Lua mapping",
                $"Set level {level.Value} in \"{target}\"");
        else if (isRename)
            AddStep("\uD83D\uDCDD", "Update Lua mapping",
                $"{_selectedItems.Count} item{(_selectedItems.Count != 1 ? "s" : "")} \u2192 \"{target}\"");
        else
            AddStep("\uD83D\uDCDD", "Update Lua mapping");

        // ── 2. Page move ──
        if (isRename && chkMovePage.IsChecked == true)
        {
            var detail2 = target.LastIndexOf('(') > 0
                ? "DISPLAYTITLE + Infobox title1 will be added" : null;
            AddStep("\uD83D\uDCC4", "Move wiki page",
                $"\"{source}\" \u2192 \"{target}\"", detail2);
        }

        // ── 3. Images ──
        // Only show image move step if at least one image actually exists on wiki
        var imgCount = imageUrls?.Count(kv => kv.Value != null) ?? 0;
        if (isRename && imgCount > 0)
        {
            var levels = _selectedItems.OrderBy(i => i.Level)
                .Where(i => imageUrls != null && imageUrls.TryGetValue(i.Level, out var u) && u != null)
                .Select(i => i.Level).ToList();
            var minLv = levels.First();
            var maxLv = levels.Last();
            var lvRange = minLv == maxLv ? $"{minLv:D2}" : $"{minLv:D2}\u2013{maxLv:D2}";

            // Step card with image thumbnails
            var imgRow = new Border
            {
                Background = subtle, CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
            };
            var imgGrid = new Grid();
            imgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            imgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var imgIcon = new TextBlock
            {
                Text = "\uD83D\uDDBC", FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetColumn(imgIcon, 0);
            imgGrid.Children.Add(imgIcon);

            var imgContent = new StackPanel();
            imgContent.Children.Add(new TextBlock
            {
                Text = "Move chain images", FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = primary
            });
            imgContent.Children.Add(new TextBlock
            {
                Text = $"{imgCount} image{(imgCount != 1 ? "s" : "")} will be renamed",
                FontSize = 11, Foreground = secondary, Margin = new Thickness(0, 2, 0, 0)
            });

            // Filename mapping (e.g. PuzzleBox01–17.png → NewName01–17.png)
            if (oldImageBase != null && newImageBase != null)
            {
                var mappingTb = new TextBlock
                {
                    FontSize = 10, Foreground = tertiary,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                };
                mappingTb.Inlines.Add(new System.Windows.Documents.Run($"{oldImageBase}{lvRange}.png"));
                mappingTb.Inlines.Add(new System.Windows.Documents.Run(" \u2192 ") { Foreground = tertiary });
                mappingTb.Inlines.Add(new System.Windows.Documents.Run($"{newImageBase}{lvRange}.png") { Foreground = accent });
                imgContent.Children.Add(mappingTb);
            }

            // Thumbnail strip (images load asynchronously — dialog appears instantly)
            if (imageUrls != null)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var item in _selectedItems.OrderBy(i => i.Level))
                {
                    var sp = new StackPanel { Width = 44, Margin = new Thickness(0, 0, 6, 4) };

                    if (imageUrls.TryGetValue(item.Level, out var url) && url != null)
                    {
                        var img = new System.Windows.Controls.Image
                        {
                            Width = 36, Height = 36, Stretch = Stretch.Uniform
                        };
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(url);
                        bi.DecodePixelWidth = 72;
                        bi.EndInit();
                        img.Source = bi;
                        sp.Children.Add(img);
                    }
                    else
                    {
                        sp.Children.Add(new Border
                        {
                            Width = 36, Height = 36,
                            Background = new SolidColorBrush(Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
                            CornerRadius = new CornerRadius(3),
                            Child = new TextBlock
                            {
                                Text = "?", FontSize = 14, Foreground = tertiary,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        });
                    }

                    sp.Children.Add(new TextBlock
                    {
                        Text = $"Lv{item.Level}", FontSize = 9, Foreground = tertiary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 1, 0, 0)
                    });
                    wrap.Children.Add(sp);
                }
                imgContent.Children.Add(wrap);
            }

            Grid.SetColumn(imgContent, 1);
            imgGrid.Children.Add(imgContent);
            imgRow.Child = imgGrid;
            root.Children.Add(imgRow);
        }

        // ── 4. Backlinks ──
        if (isRename && refCounts != null && refCounts.Count > 0)
        {
            var totalRefs = refCounts.Values.Where(c => c > 0).Sum();
            var pageCount = refCounts.Count;

            // Step card with custom content
            var row = new Border
            {
                Background = subtle, CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconTb = new TextBlock
            {
                Text = "\uD83D\uDD17", FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetColumn(iconTb, 0);
            grid.Children.Add(iconTb);

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = "Update references", FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = primary
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{pageCount} page{(pageCount != 1 ? "s" : "")} \u2022 {totalRefs} reference{(totalRefs != 1 ? "s" : "")} total",
                FontSize = 11, Foreground = secondary, Margin = new Thickness(0, 2, 0, 0)
            });

            // Scrollable page list
            var listPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (page, count) in refCounts.OrderByDescending(kv => kv.Value))
            {
                var pageRow = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                pageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var pageName = new TextBlock
                {
                    Text = page, FontSize = 11, Foreground = primary,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(pageName, 0);
                pageRow.Children.Add(pageName);

                if (count > 0)
                {
                    var badge = new Border
                    {
                        Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(5, 1, 5, 1),
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    badge.Child = new TextBlock
                    {
                        Text = $"{count} ref{(count != 1 ? "s" : "")}",
                        FontSize = 10, Foreground = tertiary
                    };
                    Grid.SetColumn(badge, 1);
                    pageRow.Children.Add(badge);
                }
                else if (count == 0)
                {
                    var noRefs = new TextBlock
                    {
                        Text = "0 refs", FontSize = 10, Foreground = tertiary,
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(noRefs, 1);
                    pageRow.Children.Add(noRefs);
                }

                listPanel.Children.Add(pageRow);
            }

            var scroll = new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            content.Children.Add(scroll);

            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            row.Child = grid;
            root.Children.Add(row);
        }

        return root;
    }

    private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        var chainName = txtChainName.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(chainName)) return;

        var sourceChainName = _sourceChain.DisplayName;
        var isRename = sourceChainName != chainName;

        int? level = null;
        if (_singleItemMode)
            level = (int)(nbLevel.Value ?? _selectedItems[0].Level);

        // ── Mapping-only shortcut: skip preview/move, just push Lua mapping ──
        if (_mappingOnly)
        {
            btnConfirm.IsEnabled = false;
            statusBar.Message = "Updating mapping...";
            statusBar.Severity = InfoBarSeverity.Informational;
            statusBar.IsOpen = true;

            try
            {
                var settings = _main.Settings;
                await WikiMappingService.PushItemMappingsAsync(
                    settings.WikiUsername, settings.WikiPassword,
                    _selectedItems, chainName, level);

                Increment(s => s.MysteryPagesPublished++);

                statusBar.Message = "Mapping updated successfully.";
                statusBar.Severity = InfoBarSeverity.Success;
                await Task.Delay(800);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                statusBar.Message = $"Error: {ex.Message}";
                statusBar.Severity = InfoBarSeverity.Error;
                btnConfirm.IsEnabled = true;
            }
            return;
        }

        // ── Fetch preview data (images + reference counts + filenames) ──
        // Backlinks only relevant when page is being moved
        var checkedBacklinks = chkMovePage.IsChecked == true ? GetCheckedBacklinks() : new List<string>();
        Dictionary<string, int>? refCounts = null;
        Dictionary<int, string?>? previewImages = null;
        string? oldImageBase = null, newImageBase = null;

        if (isRename)
        {
            btnConfirm.IsEnabled = false;
            statusBar.Message = "Preparing preview...";
            statusBar.Severity = InfoBarSeverity.Informational;
            statusBar.IsOpen = true;

            var allLevels = _selectedItems.Select(i => i.Level).ToArray();
            var imgTask = WikiMappingService.CheckChainImagesAsync(sourceChainName, allLevels);
            var oldNameTask = WikiMappingService.ResolveWikiFilenameAsync(sourceChainName);
            var newNameTask = WikiMappingService.ResolveWikiFilenameAsync(chainName);
            var refTask = checkedBacklinks.Count > 0
                ? CountReferencesAsync(checkedBacklinks, sourceChainName)
                : Task.FromResult<Dictionary<string, int>>(null!);

            await Task.WhenAll(imgTask, oldNameTask, newNameTask, refTask);
            previewImages = imgTask.Result;
            refCounts = checkedBacklinks.Count > 0 ? refTask.Result : null;

            // Extract base names (strip trailing "01.png")
            static string? GetBase(string? full)
            {
                if (string.IsNullOrEmpty(full)) return null;
                if (full.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && full.Length > 6)
                    return full[..^6];
                return full.Length > 2 ? full[..^2] : full;
            }
            oldImageBase = GetBase(oldNameTask.Result);
            newImageBase = GetBase(newNameTask.Result);

            statusBar.IsOpen = false;
            btnConfirm.IsEnabled = true;
        }

        // ── Preview confirmation ──
        var previewBox = new MessageBox
        {
            Title = "Confirm changes",
            Content = BuildPreviewContent(sourceChainName, chainName, isRename, level,
                refCounts, previewImages, oldImageBase, newImageBase),
            PrimaryButtonText = "Confirm",
            CloseButtonText = "Back",
            Owner = this,
            MinWidth = 520,
        };
        ApplicationThemeManager.Apply(previewBox);

        if (await previewBox.ShowDialogAsync() != MessageBoxResult.Primary)
            return;

        btnConfirm.IsEnabled = false;
        suggestPopup.IsOpen = false;

        try
        {
            var settings = _main.Settings;

            // ── Step 0: Image conflict pre-check ──
            Dictionary<int, string?>? sourceImageUrls = null;
            Dictionary<int, string?>? targetImageExistence = null;

            if (isRename)
            {
                statusBar.Message = "Checking images...";
                statusBar.Severity = InfoBarSeverity.Informational;
                statusBar.IsOpen = true;

                var allLevels = _selectedItems.Select(i => i.Level).ToArray();
                sourceImageUrls = await WikiMappingService.CheckChainImagesAsync(sourceChainName, allLevels);
                targetImageExistence = await WikiMappingService.CheckChainImagesAsync(chainName, allLevels);

                // Find conflicts: source image exists AND target image also exists
                var conflicts = allLevels
                    .Where(l => sourceImageUrls.TryGetValue(l, out var src) && src != null
                             && targetImageExistence.TryGetValue(l, out var tgt) && tgt != null)
                    .ToList();

                if (conflicts.Count > 0)
                {
                    var targetFull = await WikiMappingService.ResolveWikiFilenameAsync(chainName);
                    var targetBase = !string.IsNullOrEmpty(targetFull)
                        ? (targetFull.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && targetFull.Length > 6
                            ? targetFull[..^6] : (targetFull.Length > 2 ? targetFull[..^2] : targetFull))
                        : chainName;

                    var conflictList = string.Join("\n",
                        conflicts.Select(l => $"  \u2022 File:{targetBase}{l:D2}.png (Lv{l})"));

                    var msgBox = new MessageBox
                    {
                        Title = "Image conflict",
                        Content = $"The following target images already exist and will be overwritten:\n\n{conflictList}",
                        PrimaryButtonText = "Continue",
                        CloseButtonText = "Cancel",
                        Owner = this
                    };
                    ApplicationThemeManager.Apply(msgBox);

                    var result = await msgBox.ShowDialogAsync();
                    if (result != MessageBoxResult.Primary)
                    {
                        btnConfirm.IsEnabled = true;
                        statusBar.IsOpen = false;
                        return;
                    }
                }
            }

            // ── Step 1: Lua mapping update ──
            statusBar.Message = "Updating mapping...";
            statusBar.Severity = InfoBarSeverity.Informational;
            statusBar.IsOpen = true;

            await WikiMappingService.PushItemMappingsAsync(
                settings.WikiUsername, settings.WikiPassword,
                _selectedItems, chainName, level);

            Increment(s => s.MysteryPagesPublished++);

            // Authenticate for remaining steps
            using var client = await WikiMappingService.CreateAuthenticatedClientAsync(
                settings.WikiUsername, settings.WikiPassword);
            var csrfToken = await WikiMappingService.GetCsrfTokenAsync(client);

            // ── Step 2: Move wiki page ──
            bool pageMoved = false;
            if (isRename && chkMovePage.IsChecked == true)
            {
                statusBar.Message = "Moving wiki page...";

                try
                {
                    var reason = $"Rename: \"{sourceChainName}\" → \"{chainName}\" (via MergeMansionWikiTools)";
                    await WikiMappingService.MoveWikiPageAsync(client, csrfToken,
                        sourceChainName, chainName, reason);
                    pageMoved = true;

                    // Update moved page content if new name has parenthetical suffix
                    if (chainName.LastIndexOf('(') > 0)
                    {
                        var displayName = StripParenthetical(chainName);
                        await WikiMappingService.UpdateMovedPageContentAsync(
                            client, csrfToken, chainName, displayName);
                    }
                }
                catch (WikiMappingService.WikiArticleExistsException)
                {
                    // Target page already exists — skip page move, continue with rest
                }
            }

            // ── Step 3: Move images ──
            int imgMoved = 0, imgOverwritten = 0, imgSkipped = 0;
            if (isRename)
            {
                statusBar.Message = "Moving images...";
                var allLevels = _selectedItems.Select(i => i.Level).ToArray();

                var imgResult = await WikiMappingService.MoveChainImagesAsync(
                    client, csrfToken,
                    sourceChainName, chainName, allLevels,
                    sourceImageUrls,
                    (done, total) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                            statusBar.Message = $"Moving images... ({done}/{total})");
                    });
                imgMoved = imgResult.moved;
                imgOverwritten = imgResult.overwritten;
                imgSkipped = imgResult.skipped;
            }

            // ── Step 4: Update backlinks ──
            int refsUpdated = 0;

            if (isRename && checkedBacklinks.Count > 0)
            {
                int done = 0;
                int total = checkedBacklinks.Count;

                foreach (var pageTitle in checkedBacklinks)
                {
                    done++;
                    statusBar.Message = $"Updating references... ({done}/{total})";

                    try
                    {
                        var didEdit = await WikiMappingService.UpdatePageReferencesAsync(
                            client, csrfToken, pageTitle, sourceChainName, chainName);
                        if (didEdit) refsUpdated++;
                    }
                    catch
                    {
                        // Continue with remaining pages
                    }
                }
            }

            // ── Step 5: Success ──
            var parts = new List<string> { "Mapping updated" };
            if (pageMoved) parts.Add("page moved");
            if (imgMoved + imgOverwritten > 0)
            {
                var imgPart = $"{imgMoved + imgOverwritten} image{(imgMoved + imgOverwritten != 1 ? "s" : "")} moved";
                if (imgOverwritten > 0)
                    imgPart += $" ({imgOverwritten} overwritten)";
                parts.Add(imgPart);
            }
            if (refsUpdated > 0)
                parts.Add($"{refsUpdated} page{(refsUpdated != 1 ? "s" : "")} updated");

            statusBar.Message = $"Done! {string.Join(", ", parts)}.";
            statusBar.Severity = InfoBarSeverity.Success;
            statusBar.IsOpen = true;

            await Task.Delay(800);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            statusBar.Message = $"Error: {ex.Message}";
            statusBar.Severity = InfoBarSeverity.Error;
            statusBar.IsOpen = true;
            btnConfirm.IsEnabled = true;
        }
    }

    private static string StripParenthetical(string name)
    {
        var idx = name.LastIndexOf('(');
        if (idx > 0) return name[..idx].TrimEnd();
        return name;
    }

    private async void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        await ShowCloseConfirmationAsync();
    }

    // ── ESC key confirmation ─────────────────────────────────────────

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        // Let the autocomplete popup handle ESC first
        if (suggestPopup.IsOpen) return;

        e.Handled = true;
        await ShowCloseConfirmationAsync();
    }

    private async Task ShowCloseConfirmationAsync()
    {
        var msgBox = new MessageBox
        {
            Title = "Cancel Move Items",
            Content = "Do you really want to cancel?",
            PrimaryButtonText = "Yes, cancel",
            CloseButtonText = "No, stay",
            Owner = this
        };
        ApplicationThemeManager.Apply(msgBox);

        var result = await msgBox.ShowDialogAsync();
        if (result == MessageBoxResult.Primary)
        {
            DialogResult = false;
            Close();
        }
    }

    // ── BacklinkItem (simple INPC for checkbox binding) ──────────────

    private class BacklinkItem : INotifyPropertyChanged
    {
        public string Title { get; set; } = "";
        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ── Screen work area helper (P/Invoke, no WinForms dependency) ──

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private double GetScreenWorkAreaHeight()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            const uint MONITOR_DEFAULTTONEAREST = 2;
            var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                var heightPx = mi.rcWork.Bottom - mi.rcWork.Top;
                // Convert device pixels → WPF DIPs
                var source = PresentationSource.FromVisual(this);
                var dpiScale = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
                return heightPx * dpiScale;
            }
        }
        return SystemParameters.WorkArea.Height;
    }
}
