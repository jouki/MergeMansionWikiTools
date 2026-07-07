using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MergeMansionWikiTools.Helpers;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Experimental Daily Trade Predictor (Phase 1). The board is a 7×9 grid of tiles the user clicks to
/// place items (chain/item search → level, all with icons) and can drag to rearrange (drag = swap,
/// Ctrl+drag = copy). Progress = current area + the active hotspot tasks the player selects.
/// Deterministic requirement/reward candidate lists come from <see cref="DailyTradePredictorEngine"/>;
/// both daily queues are shown at once. State persists to predictor_state.json.
/// </summary>
public partial class PredictorPage : UserControl
{
    private const int Cols = 7;
    private const int Rows = 9;
    /// <summary>Inventory slot bounds from the game's InventorySlots config library:
    /// 7 free slots + 43 purchasable with coins = 50 max permanent.</summary>
    private const int InvSlotsMin = 7;
    private const int InvSlotsMax = 50;

    private readonly MainWindow _main;
    private readonly string _statePath = PredictorStateStore.DefaultPath;
    private PredictorState _state;
    private readonly ObservableCollection<PredictorCellVm> _cells = new();
    private readonly ObservableCollection<PredictorCellVm> _invCells = new();
    // Season Pass bonus slots — rendered in their own grid so they read as separate from the base ones.
    private readonly ObservableCollection<PredictorCellVm> _invSpCells = new();
    private ChainDisplayResolver _resolver = new();

    // Recently picked chains (newest first) → shown in the picker's "Recent" column for fast
    // re-selection of the same chain at a different level. Backed by PredictorState so it persists
    // across restarts (saved via PersistAndRecompute after each pick).
    private List<string> RecentChainKeys => _state.RecentChainKeys;

    private bool _initialized;
    private bool _suppressInput;
    private PredictorResult? _lastResult;

    // Drag state
    private Point _dragStart;
    private PredictorCellVm? _dragSource;
    private DragAdorner? _dragAdorner;
    private bool _dragHappened;
    private bool _isDragging;
    private bool _dropHandled;

    public PredictorPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        _state = PredictorStateStore.Load(_statePath);
        BuildCells();
        BuildInventoryCells();
        boardGrid.ItemsSource = _cells;
        invGrid.ItemsSource = _invCells;
        invSpGrid.ItemsSource = _invSpCells;
    }

    public async void OnPageShown()
    {
        if (_initialized) { Recompute(); return; }
        _initialized = true;
        _suppressInput = true;

        await BuildResolverAsync();
        RefreshCellIcons();

        cmbArea.ItemsSource = _resolver.AreaOptions;
        cmbArea.SelectedValue = _state.AreaInternalName;
        BuildTaskUi();

        sldStreak.Value = _state.Streak;
        txtStreakVal.Text = _state.Streak.ToString();
        txtInvSlots.Text = _state.InventorySlotsOwned.ToString();
        cmbSpBonus.SelectedIndex = _state.InventorySpBonus switch { 3 => 1, 6 => 2, _ => 0 };
        txtRefresh1.Text = _state.Queue1Refreshes.ToString();
        txtRefresh2.Text = _state.Queue2Refreshes.ToString();
        await UpdateStepRangeAsync();
        sldStep1.Value = _state.Queue1StepIndex;
        sldStep2.Value = _state.Queue2StepIndex;
        RenderTradeButtons();
        UpdateStepLabel();

        _main.AutocompleteDataRefreshed += OnAutocompleteRefreshed;

        _suppressInput = false;
        Recompute();
    }

    // ── Resolver / icons ──

    private async System.Threading.Tasks.Task BuildResolverAsync()
    {
        var ds = _main.DataService;
        if (ds == null) return;
        if (_main.CachedAutocomplete == null && _main.AutocompletePrewarmTask != null)
        {
            try { await _main.AutocompletePrewarmTask; } catch { /* icons stay empty */ }
        }
        var areasSvc = await _main.GetAreasServiceAsync();

        var imgBase = _main.Settings.ImageExporterBasePath;
        var apkVer = _main.Settings.SelectedApkVersion;
        var exportDir = AutocompleteDataService.ResolveImageDir(imgBase, apkVer);
        var processedDir = string.IsNullOrEmpty(imgBase) ? null : System.IO.Path.Combine(imgBase, "Processed Images");

        _resolver = new ChainDisplayResolver(ds, _main.WikiMapping, _main.CachedAutocomplete,
            areasSvc?.Areas, exportDir, processedDir);
    }

    private void OnAutocompleteRefreshed()
    {
        Dispatcher.InvokeAsync(async () =>
        {
            bool prev = _suppressInput;
            _suppressInput = true;
            await BuildResolverAsync();
            RefreshCellIcons();
            var sel = cmbArea.SelectedValue;
            cmbArea.ItemsSource = _resolver.AreaOptions;
            cmbArea.SelectedValue = sel;
            BuildTaskUi();
            _suppressInput = prev;
            Recompute();
        });
    }

    private void RefreshCellIcons()
    {
        foreach (var c in _cells.Concat(_invCells).Concat(_invSpCells))
            c.SetIcon(c.HasItem ? _resolver.GetLevelBrush(c.ChainKey, c.Level) : null);
    }

    // ── Board grid ──

    private void BuildCells()
    {
        _cells.Clear();
        var byPos = _state.Cells.ToDictionary(c => (c.Row, c.Col));
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                var vm = new PredictorCellVm { Row = r, Col = c };
                if (byPos.TryGetValue((r, c), out var cell))
                    vm.Set(cell.ChainKey, cell.Level, null);
                _cells.Add(vm);
            }
    }

    private void SyncCellsToState()
    {
        _state.Cells = _cells
            .Where(c => c.HasItem)
            .Select(c => new PredictorBoardCell { Row = c.Row, Col = c.Col, ChainKey = c.ChainKey!, Level = c.Level })
            .ToList();
        _state.InventoryCells = _invCells.Concat(_invSpCells)
            .Where(c => c.HasItem)
            .Select(c => new PredictorBoardCell { Row = c.Row, Col = c.Col, ChainKey = c.ChainKey!, Level = c.Level })
            .ToList();
    }

    // ── Inventory grid (capacity = slots owned + Season Pass bonus) ──

    private int InventoryCapacity =>
        System.Math.Clamp(_state.InventorySlotsOwned, InvSlotsMin, InvSlotsMax) + _state.InventorySpBonus;

    /// <summary>(Re)builds the inventory tiles for the current capacity. Items in slots beyond a
    /// shrunk capacity are dropped (mirrors losing the temporary Season Pass slots).</summary>
    private void BuildInventoryCells()
    {
        _invCells.Clear();
        _invSpCells.Clear();
        var byPos = _state.InventoryCells.ToDictionary(c => (c.Row, c.Col));
        int baseCount = System.Math.Clamp(_state.InventorySlotsOwned, InvSlotsMin, InvSlotsMax);
        int capacity = baseCount + _state.InventorySpBonus;
        for (int i = 0; i < capacity; i++)
        {
            var vm = new PredictorCellVm { Row = i / Cols, Col = i % Cols };
            if (byPos.TryGetValue((vm.Row, vm.Col), out var cell))
                vm.Set(cell.ChainKey, cell.Level, _resolver.GetLevelBrush(cell.ChainKey, cell.Level));
            (i < baseCount ? _invCells : _invSpCells).Add(vm);
        }
        spSection.Visibility = _state.InventorySpBonus > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Shift+click jumps by 10 (ChangeInvSlots clamps to [min, max]).
    private void InvSlots_Minus(object sender, RoutedEventArgs e) => ChangeInvSlots(ShiftHeld ? -10 : -1);
    private void InvSlots_Plus(object sender, RoutedEventArgs e) => ChangeInvSlots(ShiftHeld ? +10 : +1);

    private static bool ShiftHeld =>
        (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

    private void ChangeInvSlots(int delta)
    {
        if (!_initialized) return;
        int v = System.Math.Clamp(_state.InventorySlotsOwned + delta, InvSlotsMin, InvSlotsMax);
        if (v == _state.InventorySlotsOwned) return;
        _state.InventorySlotsOwned = v;
        txtInvSlots.Text = v.ToString();
        BuildInventoryCells();
        PersistAndRecompute();
    }

    private void SpBonus_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInput || !_initialized) return;
        _state.InventorySpBonus = cmbSpBonus.SelectedIndex switch { 1 => 3, 2 => 6, _ => 0 };
        BuildInventoryCells();
        PersistAndRecompute();
    }

    // ── Refresh (reroll) counters per queue ──

    private void Refresh1_Minus(object sender, RoutedEventArgs e) => ChangeRefreshes(1, -1);
    private void Refresh1_Plus(object sender, RoutedEventArgs e) => ChangeRefreshes(1, +1);
    private void Refresh2_Minus(object sender, RoutedEventArgs e) => ChangeRefreshes(2, -1);
    private void Refresh2_Plus(object sender, RoutedEventArgs e) => ChangeRefreshes(2, +1);

    private void ChangeRefreshes(int queue, int delta)
    {
        if (!_initialized) return;
        if (queue == 1)
        {
            _state.Queue1Refreshes = System.Math.Max(0, _state.Queue1Refreshes + delta);
            txtRefresh1.Text = _state.Queue1Refreshes.ToString();
        }
        else
        {
            _state.Queue2Refreshes = System.Math.Max(0, _state.Queue2Refreshes + delta);
            txtRefresh2.Text = _state.Queue2Refreshes.ToString();
        }
        PersistAndRecompute();
    }

    private void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (_dragHappened) { _dragHappened = false; return; }
        if (sender is not FrameworkElement { Tag: PredictorCellVm vm }) return;

        // Opening the modal picker ends this gesture. The picker closes on the level's mouse-DOWN
        // (SelectionChanged), so the button may still be pressed when control returns — clear the
        // drag source now so the leftover press doesn't start a spurious drag of the OLD item.
        _dragSource = null;

        var dlg = new BoardCellPickerDialog(_resolver, allowClear: vm.HasItem,
            RecentChainKeys, vm.HasItem ? vm.ChainKey : null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        if (dlg.ClearRequested)
            vm.Clear();
        else if (dlg.PickedItem is { } pick)
        {
            vm.Set(pick.ChainKey, pick.Level, _resolver.GetLevelBrush(pick.ChainKey, pick.Level));
            PushRecent(pick.ChainKey);
        }

        PersistAndRecompute();
    }

    /// <summary>Moves a chain to the front of the recent list (deduped, capped).</summary>
    private void PushRecent(string chainKey)
    {
        if (string.IsNullOrEmpty(chainKey)) return;
        RecentChainKeys.RemoveAll(k => string.Equals(k, chainKey, System.StringComparison.OrdinalIgnoreCase));
        RecentChainKeys.Insert(0, chainKey);
        if (RecentChainKeys.Count > 12) RecentChainKeys.RemoveRange(12, RecentChainKeys.Count - 12);
    }

    private void Cell_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PredictorCellVm vm } && vm.HasItem)
        {
            vm.Clear();
            PersistAndRecompute();
            e.Handled = true;
        }
    }

    // ── Drag & drop (swap / copy) ──

    private void Cell_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragHappened = false;
        _dragStart = e.GetPosition(this);
        _dragSource = (sender as FrameworkElement)?.Tag as PredictorCellVm;
        if (_dragSource is { HasItem: false }) _dragSource = null;
    }

    private void Cell_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || e.LeftButton != MouseButtonState.Pressed || _dragSource == null) return;
        var pos = e.GetPosition(this);
        if (System.Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        StartDrag(_dragSource);
    }

    private void StartDrag(PredictorCellVm source)
    {
        // DoDragDrop pumps messages → PreviewMouseMove can re-enter here and start a NESTED drag,
        // which lands a SECOND drop (copy-then-overwrite / swap-then-swap-back). Guard against it.
        if (_isDragging) return;
        _isDragging = true;
        _dragHappened = true;
        _dropHandled = false;

        // The adorner lives on the shared left panel so the ghost follows the cursor across BOTH
        // grids (board ↔ inventory drags are how the player "hides" an item into the inventory).
        var ghost = new Border
        {
            Width = 42, Height = 42, Opacity = 0.75, CornerRadius = new CornerRadius(3),
            Background = (Brush?)source.Icon ?? Brushes.Gray, IsHitTestVisible = false,
        };
        var layer = AdornerLayer.GetAdornerLayer(leftPanel);
        if (layer != null)
        {
            _dragAdorner = new DragAdorner(leftPanel, ghost);
            layer.Add(_dragAdorner);
        }

        // Auto-scroll the left panel while the cursor sits near its top/bottom edge, so items can be
        // dragged between off-screen rows (e.g. top board row → bottom inventory row). The timer keeps
        // scrolling even when the cursor is held still at the edge (DragOver stops firing then).
        StartDragScroll();

        try
        {
            DragDrop.DoDragDrop(leftPanel, new DataObject("predCell", source),
                DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            StopDragScroll();
            if (_dragAdorner != null)
            {
                AdornerLayer.GetAdornerLayer(leftPanel)?.Remove(_dragAdorner);
                _dragAdorner = null;
            }
            _dragSource = null;
            _isDragging = false;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _dragScrollTimer;
    private Point _lastDragPointInScroll;

    private void StartDragScroll()
    {
        _dragScrollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(40),
        };
        _dragScrollTimer.Tick += DragScroll_Tick;
        _dragScrollTimer.Start();
    }

    private void StopDragScroll()
    {
        if (_dragScrollTimer == null) return;
        _dragScrollTimer.Stop();
        _dragScrollTimer.Tick -= DragScroll_Tick;
        _dragScrollTimer = null;
    }

    private void DragScroll_Tick(object? sender, System.EventArgs e)
    {
        const double edge = 44;   // px zone at top/bottom that triggers scrolling
        const double step = 22;   // px scrolled per tick
        double y = _lastDragPointInScroll.Y;
        double h = leftScroll.ViewportHeight;
        if (y < edge)
            leftScroll.ScrollToVerticalOffset(leftScroll.VerticalOffset - step);
        else if (y > h - edge)
            leftScroll.ScrollToVerticalOffset(leftScroll.VerticalOffset + step);
    }

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("predCell")) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        e.Effects = ctrl ? DragDropEffects.Copy : DragDropEffects.Move;
        _dragAdorner?.SetPosition(e.GetPosition(leftPanel));
        _lastDragPointInScroll = e.GetPosition(leftScroll); // for edge auto-scroll (viewport coords)
        e.Handled = true;
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dropHandled) return; // ignore any duplicate drop for the same gesture
        _dropHandled = true;
        if (e.Data.GetData("predCell") is not PredictorCellVm source) return;
        var grid = (ItemsControl)sender;
        var target = HitTestCell(grid, e.GetPosition(grid));
        if (target == null || ReferenceEquals(source, target)) return;

        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        if (ctrl)
        {
            // Copy: leave the source, place a duplicate on the target. Confirm before overwriting.
            if (target.HasItem)
            {
                var ans = System.Windows.MessageBox.Show(
                    $"Tile already holds {_resolver.GetName(target.ChainKey)} L{target.Level}. Overwrite it?",
                    "Overwrite tile?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (ans != MessageBoxResult.OK) return;
            }
            target.Set(source.ChainKey!, source.Level, _resolver.GetLevelBrush(source.ChainKey, source.Level));
        }
        else
        {
            // Move / swap positions.
            var sCk = source.ChainKey; var sLvl = source.Level;
            var tCk = target.ChainKey; var tLvl = target.Level;
            target.Set(sCk!, sLvl, _resolver.GetLevelBrush(sCk, sLvl));
            if (!string.IsNullOrEmpty(tCk)) source.Set(tCk!, tLvl, _resolver.GetLevelBrush(tCk, tLvl));
            else source.Clear();
        }
        PersistAndRecompute();
    }

    /// <summary>Finds the cell VM under a point in the given grid's coordinates (walks up from the
    /// hit visual to the tile Button whose Tag is the cell). Works for the board AND inventory grid.</summary>
    private static PredictorCellVm? HitTestCell(ItemsControl grid, Point ptInGrid)
    {
        if (grid.InputHitTest(ptInGrid) is not DependencyObject d) return null;
        while (d != null && d != grid)
        {
            if (d is FrameworkElement { Tag: PredictorCellVm vm }) return vm;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ── Progress: area + active tasks ──

    private void Area_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInput || !_initialized) return;
        _suppressInput = true;
        _state.ActiveTaskIds = new List<string>(); // area change clears the (now-stale) task selection
        BuildTaskUi();
        _suppressInput = false;
        PersistAndRecompute();
    }

    /// <summary>A mouse wheel over a CLOSED combobox must scroll the page, not cycle the selection
    /// (cycling silently adds tasks / switches area). Swallow the wheel and re-raise it on the parent
    /// so the surrounding ScrollViewer still scrolls; an open dropdown keeps native wheel behavior.</summary>
    private void Combo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false } combo) return;
        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = combo,
        };
        (combo.Parent as UIElement)?.RaiseEvent(args);
    }

    private void AddTask_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInput || !_initialized) return;
        if (cmbAddTask.SelectedItem is not PredictorTaskVm t) return;
        if (!_state.ActiveTaskIds.Contains(t.Id)) _state.ActiveTaskIds.Add(t.Id);
        _suppressInput = true;
        BuildTaskUi();          // moves it from the dropdown into the selected chips
        _suppressInput = false;
        PersistAndRecompute();
    }

    private void RemoveActiveTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PredictorTaskVm t }) return;
        _state.ActiveTaskIds.Remove(t.Id);
        _suppressInput = true;
        BuildTaskUi();
        _suppressInput = false;
        PersistAndRecompute();
    }

    /// <summary>Rebuilds the selected-task chips + the add-a-task dropdown for the current area.</summary>
    private async void BuildTaskUi()
    {
        var ds = _main.DataService;
        var areasSvc = await _main.GetAreasServiceAsync();
        var internalName = cmbArea.SelectedValue as string;
        var area = areasSvc?.Areas.FirstOrDefault(a =>
            string.Equals(a.InternalName, internalName, System.StringComparison.OrdinalIgnoreCase));
        if (ds == null || area == null)
        {
            lstActiveTasks.ItemsSource = null;
            cmbAddTask.ItemsSource = null;
            return;
        }

        var all = area.Tasks.OrderBy(t => t.Index).Select(t => ToTaskVm(ds, t)).ToList();
        var active = new HashSet<string>(_state.ActiveTaskIds, System.StringComparer.OrdinalIgnoreCase);

        lstActiveTasks.ItemsSource = all.Where(v => active.Contains(v.Id)).ToList();

        bool prev = _suppressInput;
        _suppressInput = true; // reset the dropdown selection without triggering AddTask_Changed
        cmbAddTask.ItemsSource = all.Where(v => !active.Contains(v.Id)).ToList();
        cmbAddTask.SelectedIndex = -1;
        _suppressInput = prev;
    }

    private PredictorTaskVm ToTaskVm(DataService ds, LuaTask t) => new()
    {
        Id = t.Id,
        Header = $"#{t.Index} — {(string.IsNullOrWhiteSpace(t.Title) ? t.Id : t.Title)}",
        // ItemTypeToConfigKey, NOT ItemToChainName — the resolver keys everything by chain ConfigKey
        // ("Thread"), while ItemToChainName returns the display name ("Yarn") and the icon lookup
        // would silently miss every chain whose name differs from its ConfigKey.
        ReqIcons = t.Requirements.Keys
            .Select(it => _resolver.GetLevelBrush(
                ds.ItemTypeToConfigKey.TryGetValue(it, out var ch) ? ch : null,
                DataService.GetLevelFromItemType(it)))
            .Where(b => b != null)
            .Cast<ImageBrush>()
            .ToList(),
    };

    // ── Trade slot sliders ──

    private async System.Threading.Tasks.Task UpdateStepRangeAsync()
    {
        var dt = await _main.GetDailyTradeServiceAsync();
        int streak = (int)sldStreak.Value;
        SetQueueRange(sldStep1, dt, streak, uiOrder: 0);
        SetQueueRange(sldStep2, dt, streak, uiOrder: 1);
    }

    private static void SetQueueRange(Slider slider, DailyTradeService? dt, int streak, int uiOrder)
    {
        var t = dt != null && dt.HasData ? DailyTradePredictorEngine.SelectTask(dt.Tasks, streak, uiOrder) : null;
        int maxStep = t != null && t.Steps.Count > 0 ? t.Steps.Count - 1 : 0;
        slider.Maximum = maxStep;
        if (slider.Value > maxStep) slider.Value = maxStep;
    }

    private void UpdateStepLabel()
    {
        txtStep1Val.Text = $"{(int)sldStep1.Value + 1} / {(int)sldStep1.Maximum + 1}";
        txtStep2Val.Text = $"{(int)sldStep2.Value + 1} / {(int)sldStep2.Maximum + 1}";
    }

    private async void Slider_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressInput || !_initialized) return;
        if (ReferenceEquals(sender, sldStreak))
        {
            txtStreakVal.Text = ((int)sldStreak.Value).ToString();
            _suppressInput = true;
            await UpdateStepRangeAsync();
            _suppressInput = false;
        }
        UpdateStepLabel();
        PersistAndRecompute();
    }

    private void PersistAndRecompute()
    {
        if (_suppressInput) return;
        SyncCellsToState();
        _state.AreaInternalName = cmbArea.SelectedValue as string;
        _state.Streak = (int)sldStreak.Value;
        _state.Queue1StepIndex = (int)sldStep1.Value;
        _state.Queue2StepIndex = (int)sldStep2.Value;
        PredictorStateStore.Save(_statePath, _state);
        Recompute();
    }

    // ── Known current trades (their items exclude themselves from the next roll) ──

    private void TradeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot }) return;
        var cur = GetTradeItem(slot);
        var dlg = new BoardCellPickerDialog(_resolver, allowClear: cur != null,
            RecentChainKeys, cur?.ChainKey)
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        if (dlg.ClearRequested) SetTradeItem(slot, null);
        else if (dlg.PickedItem is { } pick)
        {
            SetTradeItem(slot, new PredictorTradeItem { ChainKey = pick.ChainKey, Level = pick.Level });
            PushRecent(pick.ChainKey);
        }

        RenderTradeButtons();
        PersistAndRecompute();
    }

    private void TradeItem_Clear(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: string slot } || GetTradeItem(slot) == null) return;
        SetTradeItem(slot, null);
        RenderTradeButtons();
        PersistAndRecompute();
        e.Handled = true;
    }

    private PredictorTradeItem? GetTradeItem(string slot) => slot switch
    {
        "q1req" => _state.Queue1Trade.Requirement,
        "q1rwd" => _state.Queue1Trade.Reward,
        "q2req" => _state.Queue2Trade.Requirement,
        "q2rwd" => _state.Queue2Trade.Reward,
        _ => null,
    };

    private void SetTradeItem(string slot, PredictorTradeItem? item)
    {
        switch (slot)
        {
            case "q1req": _state.Queue1Trade.Requirement = item; break;
            case "q1rwd": _state.Queue1Trade.Reward = item; break;
            case "q2req": _state.Queue2Trade.Requirement = item; break;
            case "q2rwd": _state.Queue2Trade.Reward = item; break;
        }
    }

    private void RenderTradeButtons()
    {
        RenderTradeButton(btnQ1Req, _state.Queue1Trade.Requirement, "Req: ?");
        RenderTradeButton(btnQ1Rwd, _state.Queue1Trade.Reward, "Rwd: ?");
        RenderTradeButton(btnQ2Req, _state.Queue2Trade.Requirement, "Req: ?");
        RenderTradeButton(btnQ2Rwd, _state.Queue2Trade.Reward, "Rwd: ?");
    }

    private void RenderTradeButton(Button b, PredictorTradeItem? item, string placeholder)
    {
        if (item == null) { b.Content = placeholder; return; }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Width = 22, Height = 22, ClipToBounds = true,
            Background = _resolver.GetLevelBrush(item.ChainKey, item.Level),
            Margin = new Thickness(0, 0, 6, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{_resolver.GetName(item.ChainKey)} L{item.Level}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
        });
        b.Content = panel;
    }

    // ── Prediction ──

    private async void Recompute()
    {
        var ds = _main.DataService;
        var dt = await _main.GetDailyTradeServiceAsync();
        var areasSvc = await _main.GetAreasServiceAsync();

        if (ds == null || dt == null || !dt.HasData)
        {
            txtQueue1.Text = "Daily Trade data not loaded — you need a dump made with the \"Daily Trades\" filter (Game Data Dumper).";
            txtQueue2.Text = "";
            txtNote.Text = "";
            ClearLists();
            return;
        }

        var areas = areasSvc?.Areas ?? new List<LuaArea>();

        // ConfigKey mapping — the engine matches hotspot requirement items against board chain keys
        // and ChainRules, both of which are ConfigKeys (display names would never match).
        System.Func<string, string?> chainOf = it => ds.ItemTypeToConfigKey.TryGetValue(it, out var c) ? c : null;
        System.Func<string, int> levelOf = DataService.GetLevelFromItemType;
        System.Func<string, DailyTradeChainRule?> ruleOf = k => dt.ChainRules.TryGetValue(k, out var r) ? r : null;
        int? ReqVal(string chainKey, int lvl) => FindItem(ds, chainKey, lvl)?.RequiredItemValue;
        int? RwdVal(string chainKey, int lvl) => FindItem(ds, chainKey, lvl)?.RewardItemValue;

        var producers = new HashSet<string>(
            _state.Board.Select(b => b.ChainKey).Where(k => ChainIsProducerTyped(ds, k)),
            System.StringComparer.OrdinalIgnoreCase);

        var res = DailyTradePredictorEngine.Predict(
            dt.Settings ?? new DailyTradeSettings(), _state, areas,
            chainOf, levelOf, ruleOf, ReqVal, RwdVal, producers);

        _lastResult = res;
        RenderQueues(dt);
        RenderCandidates(res);
        EnsureVisibleIcons();
    }

    /// <summary>Flood-fill-crops the per-level icons for everything currently on screen (board tiles,
    /// candidate lists, area tasks) in the background, then re-renders once they're ready. Chains are
    /// extracted at most once (cached to Processed Images), so repeated calls are cheap.</summary>
    private async void EnsureVisibleIcons()
    {
        var ds = _main.DataService;
        if (ds == null) return;

        var chains = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var c in _state.Cells) chains.Add(c.ChainKey);
        foreach (var c in _state.InventoryCells) chains.Add(c.ChainKey);
        if (_lastResult != null)
            foreach (var c in _lastResult.ReqHigh.Concat(_lastResult.ReqNormal).Concat(_lastResult.ReqLow)
                         .Concat(_lastResult.RwdHigh).Concat(_lastResult.RwdNormal).Concat(_lastResult.RwdLow))
                chains.Add(c.ChainKey);

        var areasSvc = await _main.GetAreasServiceAsync();
        var area = areasSvc?.Areas.FirstOrDefault(a =>
            string.Equals(a.InternalName, _state.AreaInternalName, System.StringComparison.OrdinalIgnoreCase));
        if (area != null)
            foreach (var t in area.Tasks)
                foreach (var it in t.Requirements.Keys)
                    if (ds.ItemTypeToConfigKey.TryGetValue(it, out var ch)) chains.Add(ch);

        var list = chains.Where(c => !string.IsNullOrEmpty(c)).ToList();
        if (list.Count == 0) return;

        await System.Threading.Tasks.Task.Run(() =>
        {
            if (!_resolver.EnsureLevelImages(list)) return;
            Dispatcher.Invoke(() =>
            {
                RefreshCellIcons();
                if (_lastResult != null) RenderCandidates(_lastResult);
                BuildTaskUi();
            });
        });
    }

    private void RenderQueues(DailyTradeService dt)
    {
        txtQueue1.Text = QueueLine(dt, 1, 0, _state.Queue1StepIndex, _state.Queue1Refreshes);
        txtQueue2.Text = QueueLine(dt, 2, 1, _state.Queue2StepIndex, _state.Queue2Refreshes);
        txtNote.Text = "Candidates are for the NEXT rolled trade — items already used by the current trades you entered are excluded (both queues never ask for / reward the same item). "
            + "Refreshes and repeated requirements accumulate a diminishing penalty that inflates the desired reward value (up to +100%) — the more you reroll, the worse the offered ratio gets.";
    }

    private string QueueLine(DailyTradeService dt, int label, int uiOrder, int stepIndex, int refreshes)
    {
        var task = DailyTradePredictorEngine.SelectTask(dt.Tasks, _state.Streak, uiOrder);
        if (task == null || task.Steps.Count == 0) return $"Queue {label}: —";
        int step = System.Math.Min(System.Math.Max(0, stepIndex), task.Steps.Count - 1);
        var s = task.Steps[step];
        var line = $"Queue {label} ({task.TaskId}) · trade {step + 1}/{task.Steps.Count}: "
                 + $"desired req {s.RequirementItemValue} → reward {s.RewardItemValue}";
        if (!s.RefreshEnabled) return line + " · refresh disabled";
        if (s.RefreshCosts.Count > 0)
        {
            // RefreshCount indexes into the step's RefreshCosts. Past the end the price CAPS (it does
            // NOT keep escalating) at 2× the last defined cost — a per-step cap, so later steps (with
            // higher last values) cap higher. Confirmed in-game: step [0,1,5,10] caps at 20 gems.
            int lastIdx = s.RefreshCosts.Count - 1;
            long next = refreshes <= lastIdx ? s.RefreshCosts[refreshes] : (long)s.RefreshCosts[lastIdx] * 2;
            line += $" · next refresh: {(next == 0 ? "free" : $"{next} gems")} ({refreshes} used)";
        }
        return line;
    }

    private void RenderCandidates(PredictorResult res)
    {
        lstReqHigh.ItemsSource = ToRows(res.ReqHigh);
        lstReqNormal.ItemsSource = ToRows(res.ReqNormal);
        lstRwdHigh.ItemsSource = ToRows(res.RwdHigh);
        lstRwdNormal.ItemsSource = ToRows(res.RwdNormal);
        lstRejReq.ItemsSource = ToRejRows(res.RejectedRequirements);
        lstRejRwd.ItemsSource = ToRejRows(res.RejectedRewards);
    }

    private List<PredictorRowVm> ToRows(IEnumerable<PredictorCandidate> cands) =>
        cands.Select(c => new PredictorRowVm
        {
            Name = _resolver.GetName(c.ChainKey),
            Level = c.Level,
            Value = c.Value,
            Icon = _resolver.GetLevelBrush(c.ChainKey, c.Level),
        }).ToList();

    private List<PredictorRowVm> ToRejRows(IEnumerable<PredictorRejection> rejs) =>
        rejs.Select(r => new PredictorRowVm
        {
            Name = _resolver.GetName(r.ChainKey),
            Level = r.Level,
            LevelReason = $"L{r.Level}: {r.Reason}",
            Icon = _resolver.GetLevelBrush(r.ChainKey, r.Level),
        }).ToList();

    private void ClearLists()
    {
        lstReqHigh.ItemsSource = null;
        lstReqNormal.ItemsSource = null;
        lstRwdHigh.ItemsSource = null;
        lstRwdNormal.ItemsSource = null;
        lstRejReq.ItemsSource = null;
        lstRejRwd.ItemsSource = null;
    }

    private static ParsedItem? FindItem(DataService ds, string chainKey, int level) =>
        ds.Chains.FirstOrDefault(c =>
            string.Equals(c.ConfigKey, chainKey, System.StringComparison.OrdinalIgnoreCase))
          ?.Items.FirstOrDefault(i => i.Level == level);

    private static bool ChainIsProducerTyped(DataService ds, string chainKey) =>
        ds.Chains.FirstOrDefault(c =>
            string.Equals(c.ConfigKey, chainKey, System.StringComparison.OrdinalIgnoreCase))
          ?.Items.Any(i => i.IsGenerator || i.IsSpawner) ?? false;
}

// ── View models ──

/// <summary>One board tile. Notifies so the grid updates in place on pick/clear/drag.</summary>
public class PredictorCellVm : System.ComponentModel.INotifyPropertyChanged
{
    public int Row { get; set; }
    public int Col { get; set; }
    public string? ChainKey { get; private set; }
    public int Level { get; private set; }
    public ImageBrush? Icon { get; private set; }

    public bool HasItem => !string.IsNullOrEmpty(ChainKey);
    public Visibility EmptyVisibility => HasItem ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ItemVisibility => HasItem ? Visibility.Visible : Visibility.Collapsed;
    public string LevelText => HasItem ? Level.ToString() : "";

    public void Set(string chainKey, int level, ImageBrush? icon)
    {
        ChainKey = chainKey; Level = level; Icon = icon; RaiseAll();
    }

    public void SetIcon(ImageBrush? icon) { Icon = icon; OnChanged(nameof(Icon)); }

    public void Clear() { ChainKey = null; Level = 0; Icon = null; RaiseAll(); }

    private void RaiseAll()
    {
        OnChanged(nameof(ChainKey)); OnChanged(nameof(Level)); OnChanged(nameof(Icon));
        OnChanged(nameof(HasItem)); OnChanged(nameof(EmptyVisibility));
        OnChanged(nameof(ItemVisibility)); OnChanged(nameof(LevelText));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
}

/// <summary>A row in the candidate / rejection lists (Name + per-level icon).</summary>
public class PredictorRowVm
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int? Value { get; set; }
    public string? LevelReason { get; set; }
    public ImageBrush? Icon { get; set; }
}

/// <summary>An area task in the "active tasks" multi-select (index + title + required-item icons).</summary>
public class PredictorTaskVm
{
    public string Id { get; set; } = "";
    public string Header { get; set; } = "";
    public List<ImageBrush> ReqIcons { get; set; } = new();
}
