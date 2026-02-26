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
        txtFlowchartPath.Text = _main.Settings.FlowchartOutputPath;

        // Show reset button if "don't ask again" is active
        UpdateResetFolderPrefVisibility();

        // Debug mode
        toggleDebugMode.IsChecked = _main.Settings.DebugMode;

        // Clipboard settings
        toggleClipboardAutoAdd.IsChecked = _main.Settings.ClipboardAutoAdd;
        toggleClipboardGlobal.IsChecked = _main.Settings.ClipboardMonitorGlobal;
        gridClipboardGlobal.Visibility = _main.Settings.ClipboardAutoAdd ? Visibility.Visible : Visibility.Collapsed;

        // Build chunk size rows
        BuildChunkRows();

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
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtChainPath.Text = path;
            SaveChainPath(path);
            await _main.LoadDataAsync(path);
        }
    }

    private void AreasFileDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtAreasPath.Text = path;
            _main.SetAreasPath(path);
        }
    }

    private void EventsFileDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path != null)
        {
            txtEventsPath.Text = path;
            _main.SetEventsPath(path);
        }
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
    }

    private void TinifyKey2_TextChanged(object sender, TextChangedEventArgs e)
    {
        _main.Settings.TinifyApiKey2 = txtTinifyKey2.Text.Trim();
        _main.SaveSettings();
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
    public void HighlightChunkSizes()      { SwitchToTab(3); HighlightBorder(expertChunkHighlight); }

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
        // Clear the overlay background — parent card's DynamicResource background is untouched
        anim.Completed += (_, _) => border.ClearValue(Border.BackgroundProperty);

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

    private void BrowseFlowchartPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select flowchart output folder" };
        if (!string.IsNullOrEmpty(_main.Settings.FlowchartOutputPath) &&
            System.IO.Directory.Exists(_main.Settings.FlowchartOutputPath))
            dlg.InitialDirectory = _main.Settings.FlowchartOutputPath;

        if (dlg.ShowDialog() == true)
        {
            txtFlowchartPath.Text = dlg.FolderName;
            _main.Settings.FlowchartOutputPath = dlg.FolderName;
            _main.SaveSettings();
        }
    }

    private void ClearFlowchartPath_Click(object sender, RoutedEventArgs e)
    {
        txtFlowchartPath.Text = "";
        _main.Settings.FlowchartOutputPath = "";
        _main.SaveSettings();
    }

    public void HighlightFlowchartSection()    { SwitchToTab(1); HighlightBorder(flowchartSectionBorder); }
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
            txtWikiBotStatus.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
        }
        else if (!string.IsNullOrEmpty(_main.Settings.WikiUsername))
        {
            txtWikiBotStatus.Text = "Not verified";
            txtWikiBotStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush");
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
        txtWikiBotStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");

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
        }
        catch (Exception ex)
        {
            _main.Settings.WikiVerified = false;
            _main.Settings.WikiVerifiedDisplayName = "";
            _main.SaveSettings();
            UpdateBotStatus();
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

    private void ResetFolderPreference_Click(object sender, RoutedEventArgs e)
    {
        _main.Settings.FlowchartRememberFolderChoice = false;
        _main.Settings.FlowchartAutoUpdateFolder = false;
        _main.SaveSettings();
        UpdateResetFolderPrefVisibility();
        _main.ShowStatus("'Save to' preference reset.", Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    public void UpdateResetFolderPrefVisibility()
    {
        btnResetFolderPref.Visibility = _main.Settings.FlowchartRememberFolderChoice
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Debug Mode ──

    private void ToggleDebugMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _main.Settings.DebugMode = toggleDebugMode.IsChecked == true;
        _main.SaveSettings();
    }

    // ── Expert — chunk sizes ──

    private void BuildChunkRows()
    {
        chunksPanel.Children.Clear();
        var sizes = _main.Settings.AreaChunkSizes;
        if (sizes == null || sizes.Count == 0) sizes = new List<int> { 40 };

        foreach (var size in sizes)
            AddChunkRow(size);

        UpdateRemoveButtons();
    }

    private void AddChunkRow(int value = 40)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = new Wpf.Ui.Controls.TextBox
        {
            Text = value.ToString(),
            PlaceholderText = "40",
            Height = 34
        };
        tb.TextChanged += (_, _) => SaveChunkSizesAuto();
        Grid.SetColumn(tb, 0);

        var removeBtn = new Wpf.Ui.Controls.Button
        {
            Content = "×",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Height = 34,
            Width = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        removeBtn.Click += (_, _) =>
        {
            chunksPanel.Children.Remove(row);
            UpdateRemoveButtons();
            SaveChunkSizesAuto();
        };
        Grid.SetColumn(removeBtn, 1);

        row.Children.Add(tb);
        row.Children.Add(removeBtn);
        chunksPanel.Children.Add(row);
    }

    private void BtnAddChunk_Click(object sender, RoutedEventArgs e)
    {
        AddChunkRow();
        UpdateRemoveButtons();
        SaveChunkSizesAuto();
    }

    private void UpdateRemoveButtons()
    {
        for (int i = 0; i < chunksPanel.Children.Count; i++)
        {
            if (chunksPanel.Children[i] is Grid row)
            {
                var btn = row.Children.OfType<Wpf.Ui.Controls.Button>().FirstOrDefault();
                if (btn != null)
                    btn.Visibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void SaveChunkSizesAuto()
    {
        var sizes = new List<int>();
        foreach (Grid row in chunksPanel.Children)
        {
            var tb = row.Children.OfType<Wpf.Ui.Controls.TextBox>().FirstOrDefault();
            if (tb != null && int.TryParse(tb.Text.Trim(), out var n) && n > 0)
                sizes.Add(n);
        }

        if (sizes.Count == 0) sizes.Add(40);

        _main.Settings.AreaChunkSizes = sizes;
        _main.SaveSettings();
    }

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
