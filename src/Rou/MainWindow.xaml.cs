using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Rou.Interop;
using Rou.Models;
using Rou.Services;

namespace Rou;

public partial class MainWindow : Window
{
    const int HotkeyId = 0x5042;
    const int WM_HOTKEY = 0x0312;
    const double ExpandedHeight = 540;
    const double CompactHeight = 160;

    readonly Store _store;
    readonly ObservableCollection<TaskRow> _rows = new();
    readonly ListCollectionView _view;
    readonly HashSet<string> _entered = new();
    readonly DispatcherTimer _toastTimer;
    readonly DispatcherTimer _clockTimer;
    readonly DispatcherTimer _posTimer;
    readonly DispatcherTimer _heightTimer;

    HwndSource? _source;
    IntPtr _hwnd;
    TrayIcon? _tray;
    FileSystemWatcher? _inboxWatch;
    Action? _undo;
    bool _compact;
    bool _reallyClose;
    bool _hotkeyOk;
    bool _firstActivate = true;
    readonly DispatcherTimer _trimTimer;
    string _hotkeyLabel = "";

    // 依次尝试注册，取第一个成功的热键
    static readonly (uint Mods, uint Vk, string Label)[] HotkeyCandidates =
    {
        (Native.MOD_CONTROL | Native.MOD_ALT, 0x50, "Ctrl+Alt+P"),
        (Native.MOD_CONTROL | Native.MOD_SHIFT, 0x52, "Ctrl+Shift+R"),
        (Native.MOD_CONTROL | Native.MOD_ALT, 0x52, "Ctrl+Alt+R"),
        (Native.MOD_WIN | Native.MOD_SHIFT, 0x52, "Win+Shift+R")
    };

    bool _docked;
    string _dockEdge = "none";
    Rect _restoreBounds;

    public MainWindow(Store store)
    {
        _store = store;
        InitializeComponent();

        _view = new ListCollectionView(_rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskRow.Group)));
        TaskList.ItemsSource = _view;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clockTimer.Tick += (_, _) => UpdateChrome();

        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _posTimer.Tick += (_, _) => { _posTimer.Stop(); SavePosition(); };

        // 空闲时定期归还未用内存页，降低任务管理器中的工作集
        _heightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        _heightTimer.Tick += (_, _) => { _heightTimer.Stop(); UpdateHeight(false); };

        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _trimTimer.Tick += (_, _) => { if (!IsActive) TrimMemory(); };

        Loaded += OnLoaded;
        Activated += OnFirstActivated;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Deactivated += OnDeactivated;
        LocationChanged += (_, _) => { if (IsLoaded) { _posTimer.Stop(); _posTimer.Start(); } };
    }

    // ================= 生命周期 =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW));

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        foreach (var (mods, vk, label) in HotkeyCandidates)
        {
            if (Native.RegisterHotKey(_hwnd, HotkeyId, mods, vk))
            {
                _hotkeyOk = true;
                _hotkeyLabel = label;
                break;
            }
        }
        if (!_hotkeyOk)
        {
            _hotkeyLabel = "未注册（可点托盘图标）";
            Store.Log("hotkey: all candidates failed");
        }

        _tray = new TrayIcon(_hwnd, "Rou 日程 · 右键菜单");
        _tray.Click += ToggleVisibility;
        _tray.RightClick += ShowTrayMenu;
        _tray.DoubleClick += () => { WakeUp(); ShowOverlay(SettingsPanel); };
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();
        ApplySettingsToUi();
        Rebuild();
        _clockTimer.Start();
        StartInboxWatch();
        DrainInbox(silent: true);
        if (_store.Settings.Compact) SetCompact(true, animate: false);
        if (_store.Settings.DockEdge is "left" or "right" or "top") DockTo(_store.Settings.DockEdge, animate: false);

        _trimTimer.Start();
        var kick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        kick.Tick += (_, _) => { kick.Stop(); TrimMemory(); };
        kick.Start();
    }

    // 首次显示不抢占输入焦点，保证输入框的语法提示可见
    void OnFirstActivated(object? sender, EventArgs e)
    {
        if (!_firstActivate) return;
        _firstActivate = false;
        Dispatcher.BeginInvoke(new Action(Keyboard.ClearFocus), DispatcherPriority.Input);
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePosition();
        if (_reallyClose) return;
        e.Cancel = true;
        Hide();
    }

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (_store.Settings.DesktopLayer && !_store.Settings.Topmost)
            Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleVisibility();
            handled = true;
        }
        else if (msg == TrayIcon.CallbackMessage)
        {
            _tray?.Handle(lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void ToggleVisibility()
    {
        if (_docked) { Undock(); Activate(); return; }
        if (IsVisible) Hide();
        else WakeUp();
    }

    public void WakeUp()
    {
        if (_docked) Undock();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = _store.Settings.Topmost;
        Activate();
        AddBox.Focus();
    }

    public void Shutdown()
    {
        if (_reallyClose) return;
        _reallyClose = true;
        _toastTimer.Stop();
        _clockTimer.Stop();
        _posTimer.Stop();
        if (_hotkeyOk) Native.UnregisterHotKey(_hwnd, HotkeyId);
        _inboxWatch?.Dispose();
        _tray?.Dispose();
        _tray = null;
        _source?.RemoveHook(WndProc);
        Close();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && OverlayHost.Visibility == Visibility.Visible)
        {
            OnCloseOverlay(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            AddBox.Focus();
            e.Handled = true;
        }
    }

    // ================= 列表 =================

    void Rebuild()
    {
        var rows = _store.Tasks
            .Select(t => new TaskRow(t))
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Model.Due ?? DateTime.MaxValue)
            .ThenByDescending(r => r.Priority)
            .ToList();

        _rows.Clear();
        foreach (var row in rows) _rows.Add(row);
        UpdateChrome();
        ScheduleHeight();
    }

    void UpdateChrome()
    {
        var now = DateTime.Now;
        DayNum.Text = now.Day.ToString();
        MonthLine.Text = MonthCn(now.Month) + "月 · " + Nlp.WeekCn[(int)now.DayOfWeek];

        var today = now.Date;
        int remain = _store.Tasks.Count(t => t.Due.HasValue && t.Due.Value.Date == today);
        int done = _store.Archive.Count(t => t.CompletedAt?.Date == today);
        int total = remain + done;
        double ratio = total == 0 ? 0 : (double)done / total;

        SubLine.Text = remain == 0
            ? (done > 0 ? $"今天已完成 {done} 项" : "今天没有安排")
            : $"今天还有 {remain} 项";

        ProgDone.Width = new GridLength(Math.Max(0.0001, ratio), GridUnitType.Star);
        ProgRest.Width = new GridLength(Math.Max(0.0001, 1 - ratio), GridUnitType.Star);
        FooterText.Text = $"今天 {done}/{total}";
        FooterHotkey.Text = _hotkeyOk ? _hotkeyLabel : "托盘";
        HotkeyHint.Text = _hotkeyOk ? _hotkeyLabel + " 显示 / 隐藏" : "全局热键被占用，可点托盘图标";

        bool empty = _rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            EmptyTitle.Text = done > 0 ? $"今天的 {done} 项都完成了" : "今天没有待办";
            EmptyTip.Text = done > 0 ? "享受片刻清静，或输入一句话继续添加" : "输入一句话即可添加，例如「明天9点 晨跑」";
        }
    }

    static string MonthCn(int m) => m switch
    {
        1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五", 6 => "六",
        7 => "七", 8 => "八", 9 => "九", 10 => "十", 11 => "十一", _ => "十二"
    };

    void OnRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not TaskRow row) return;
        if (!_entered.Add(row.Id)) return;
        border.Opacity = 0;
        var shift = new TranslateTransform(0, 5);
        border.RenderTransform = shift;
        border.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(240))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ================= 添加 =================

    void OnAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitAdd();
        }
        else if (e.Key == Key.Escape)
        {
            AddBox.Clear();
            Keyboard.ClearFocus();
        }
    }

    void CommitAdd()
    {
        var text = AddBox.Text.Trim();
        if (text.Length == 0) return;

        var created = new List<TaskItem>();
        foreach (var part in Cli.SplitTasks(text))
        {
            var task = Nlp.Parse(part).ToTask("manual");
            _store.Tasks.Add(task);
            _entered.Remove(task.Id);
            created.Add(task);
        }
        if (created.Count == 0) return;

        _store.SaveTasks();
        AddBox.Clear();
        Rebuild();
        Toast(created.Count == 1 ? "已添加 · " + Nlp.Describe(created[0]) : $"已添加 {created.Count} 项");
    }

    // ================= 完成 / 删除 / 编辑 =================

    void OnToggleDone(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TaskRow row }) Complete(row);
    }

    void Complete(TaskRow row)
    {
        var task = row.Model;
        int index = _store.Tasks.IndexOf(task);
        _store.Tasks.Remove(task);
        task.CompletedAt = DateTime.Now;
        _store.Archive.Insert(0, task);
        TrimArchive();

        TaskItem? next = null;
        if (!string.IsNullOrEmpty(task.Repeat))
        {
            next = Recurrence.Next(task);
            if (next != null) _store.Tasks.Add(next);
        }
        _store.SaveTasks();
        _store.SaveArchive();
        Rebuild();

        var message = next != null ? "已完成 · 下次 " + Nlp.Describe(next) : "已完成并归档";
        Toast(message, "撤销", () =>
        {
            _store.Archive.Remove(task);
            task.CompletedAt = null;
            if (next != null) _store.Tasks.Remove(next);
            _store.Tasks.Insert(Math.Clamp(index, 0, _store.Tasks.Count), task);
            _store.SaveTasks();
            _store.SaveArchive();
            Rebuild();
            Toast("已恢复");
        });
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskRow row }) return;
        var task = row.Model;
        int index = _store.Tasks.IndexOf(task);
        _store.Tasks.Remove(task);
        _store.SaveTasks();
        Rebuild();
        Toast("已删除", "撤销", () =>
        {
            _store.Tasks.Insert(Math.Clamp(index, 0, _store.Tasks.Count), task);
            _store.SaveTasks();
            Rebuild();
            Toast("已恢复");
        });
    }

    void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (sender is not Border border || border.DataContext is not TaskRow row) return;
        row.IsEditing = true;
        if (border.FindName("EditBox") is TextBox box)
            Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }), DispatcherPriority.Input);
    }

    void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not TaskRow row) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitEdit(box, row);
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            row.IsEditing = false;
            Keyboard.ClearFocus();
        }
    }

    void OnEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not TaskRow row) return;
        CommitEdit(box, row);
    }

    void CommitEdit(TextBox box, TaskRow row)
    {
        if (!row.IsEditing) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        row.IsEditing = false;
        if (string.IsNullOrWhiteSpace(row.Model.Title)) row.Model.Title = "新任务";
        row.Refresh();
        _store.SaveTasks();
        UpdateChrome();
    }

    void TrimArchive()
    {
        if (_store.Archive.Count > 3000)
            _store.Archive.RemoveRange(3000, _store.Archive.Count - 3000);
    }

    // ================= 提示条 =================

    void Toast(string text, string? actionText = null, Action? action = null)
    {
        _undo = action;
        ToastText.Text = text;
        ToastAction.Visibility = actionText == null ? Visibility.Collapsed : Visibility.Visible;
        ToastBox.IsHitTestVisible = action != null;
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(230))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void HideToast()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240));
        fade.Completed += (_, _) => { ToastBox.IsHitTestVisible = false; _undo = null; };
        ToastBox.BeginAnimation(OpacityProperty, fade);
    }

    void OnToastAction(object sender, MouseButtonEventArgs e)
    {
        var action = _undo;
        _undo = null;
        _toastTimer.Stop();
        HideToast();
        action?.Invoke();
    }

    // ================= 面板 =================

    void OnOpenArchive(object sender, RoutedEventArgs e)
    {
        RefreshArchive();
        ShowOverlay(ArchivePanel);
    }

    void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        ApplySettingsToUi();
        ShowOverlay(SettingsPanel);
    }

    void ShowOverlay(Grid panel)
    {
        OverlayHost.Visibility = Visibility.Visible;
        ArchivePanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        OverlayHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
    }

    void OnCloseOverlay(object sender, RoutedEventArgs e)
    {
        OverlayHost.Visibility = Visibility.Collapsed;
        AddBox.Focus();
    }

    void RefreshArchive()
    {
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        ArcTotal.Text = _store.Archive.Count.ToString();
        ArcWeek.Text = _store.Archive.Count(t => t.CompletedAt >= monday).ToString();
        ArcStreak.Text = Streak().ToString();

        var rows = _store.Archive
            .Where(t => t.CompletedAt != null)
            .OrderByDescending(t => t.CompletedAt)
            .Select(t => new ArchiveRow
            {
                Title = t.Title,
                Detail = t.CompletedAt!.Value.ToString("HH:mm"),
                Day = ArchiveDay(t.CompletedAt!.Value.Date)
            })
            .ToList();

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ArchiveRow.Day)));
        ArcList.ItemsSource = view;
        ArcEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string ArchiveDay(DateTime d)
    {
        var today = DateTime.Today;
        if (d == today) return "今天";
        if (d == today.AddDays(-1)) return "昨天";
        if (d == today.AddDays(-2)) return "前天";
        return d.ToString("M月d日") + " · " + Nlp.WeekCn[(int)d.DayOfWeek];
    }

    int Streak()
    {
        var days = _store.Archive
            .Where(t => t.CompletedAt != null)
            .Select(t => t.CompletedAt!.Value.Date)
            .Distinct()
            .ToHashSet();
        int n = 0;
        var cursor = DateTime.Today;
        if (!days.Contains(cursor)) cursor = cursor.AddDays(-1);
        while (days.Contains(cursor)) { n++; cursor = cursor.AddDays(-1); }
        return n;
    }

    void OnClearArchive(object sender, RoutedEventArgs e)
    {
        if (_store.Archive.Count == 0) return;
        var backup = new List<TaskItem>(_store.Archive);
        _store.Archive.Clear();
        _store.SaveArchive();
        RefreshArchive();
        Toast("归档已清空", "撤销", () =>
        {
            _store.Archive.AddRange(backup);
            _store.SaveArchive();
            RefreshArchive();
            Toast("已恢复");
        });
    }

    // ================= 设置 =================

    void ApplySettingsToUi()
    {
        var s = _store.Settings;
        SwDark.IsChecked = s.Theme == "dark";
        SwTopmost.IsChecked = s.Topmost;
        SwDesktop.IsChecked = s.DesktopLayer;
        SwAutostart.IsChecked = AutoStart.IsEnabled();
        Topmost = s.Topmost;
        VersionText.Text = "Rou 1.0 · " + Store.Root;
        HotkeyHint.Text = _hotkeyOk ? _hotkeyLabel + " 显示 / 隐藏" : "全局热键被占用，可点托盘图标";
    }

    void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _store.Settings.Theme = SwDark.IsChecked == true ? "dark" : "light";
        App.ApplyTheme(_store.Settings.Theme);
        _store.SaveSettings();
    }

    void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        bool on = SwTopmost.IsChecked == true;
        _store.Settings.Topmost = on;
        Topmost = on;
        if (on)
        {
            _store.Settings.DesktopLayer = false;
            SwDesktop.IsChecked = false;
        }
        _store.SaveSettings();
    }

    void OnToggleDesktopLayer(object sender, RoutedEventArgs e)
    {
        bool on = SwDesktop.IsChecked == true;
        _store.Settings.DesktopLayer = on;
        if (on)
        {
            _store.Settings.Topmost = false;
            SwTopmost.IsChecked = false;
            Topmost = false;
        }
        _store.SaveSettings();
    }

    void OnToggleAutostart(object sender, RoutedEventArgs e)
    {
        bool on = SwAutostart.IsChecked == true;
        _store.Settings.Autostart = on;
        AutoStart.Apply(on);
        _store.SaveSettings();
    }

    void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Store.InboxDir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Store.InboxDir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Store.Log("open dir: " + ex.Message); }
    }

    void OnExitApp(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ================= 高度自适应 =================

    void ScheduleHeight()
    {
        Dispatcher.BeginInvoke(new Action(() => UpdateHeight(true)), DispatcherPriority.Loaded);
        // 批量增删时首次布局可能还没完成，稍后再校准一次
        _heightTimer.Stop();
        _heightTimer.Start();
    }

    // 固定部分高度 = 外框留白 + 头部 + 输入条 + 底部进度（含各自 Margin）
    double ChromeHeight()
    {
        static double Outer(FrameworkElement e) => e.ActualHeight + e.Margin.Top + e.Margin.Bottom;
        // 卡片外边距在 CardHost 上（20 + 20），窗口根节点无外边距
        double root = CardHost.Margin.Top + CardHost.Margin.Bottom;
        return root + 2 + Outer(Header) + Outer(AddBar) + Outer(Footer);
    }

    void UpdateHeight(bool animate)
    {
        if (_compact || _docked || !IsLoaded || !IsVisible) return;
        double content = _rows.Count == 0
            ? 132
            : TaskList.ActualHeight + ListScroll.Padding.Top + ListScroll.Padding.Bottom;
        double target = Math.Clamp(ChromeHeight() + content + 3, 214, ExpandedHeight);
        if (Math.Abs(target - Height) < 1.5) return;
        // 直接落值：动画在连续增删时容易与基值打架，这里优先保证布局正确
        BeginAnimation(HeightProperty, null);
        Height = target;
    }

    double _heightTarget;

    void AnimateHeight(double target)
    {
        // 动画期间 Height 是动画值，直接写基值会被 Stop 行为回滚，
        // 因此统一用 HoldEnd + 完成回调落基值，避免多次调用互相覆盖。
        _heightTarget = target;
        double from = Height;
        if (Math.Abs(from - target) < 0.5)
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
            return;
        }

        var anim = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        anim.Completed += (_, _) =>
        {
            if (Math.Abs(_heightTarget - target) > 0.5) return;   // 已有更新的目标
            BeginAnimation(HeightProperty, null);
            Height = target;
        };
        BeginAnimation(HeightProperty, anim);
    }

    // ================= 折叠 / 位置 =================

    void OnToggleCompact(object sender, RoutedEventArgs e) => SetCompact(!_compact, animate: true);

    void SetCompact(bool on, bool animate)
    {
        _compact = on;
        _store.Settings.Compact = on;
        _store.SaveSettings();

        CollapseIcon.Data = (Geometry)FindResource(on ? "Ico.Down" : "Ico.Up");
        BtnCollapse.ToolTip = on ? "展开" : "收起";

        if (on)
        {
            ListScroll.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Collapsed;
            Footer.Visibility = Visibility.Collapsed;
        }
        else
        {
            ListScroll.Visibility = Visibility.Visible;
            Footer.Visibility = Visibility.Visible;
            UpdateChrome();
        }

        if (on)
        {
            if (animate && IsLoaded) AnimateHeight(CompactHeight);
            else Height = CompactHeight;
        }
        else if (animate && IsLoaded)
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateHeight(true)), DispatcherPriority.Loaded);
        }
        else
        {
            Height = ExpandedHeight;
            Dispatcher.BeginInvoke(new Action(() => UpdateHeight(false)), DispatcherPriority.Loaded);
        }
    }

    void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_compact) SetCompact(false, animate: true);
            return;
        }
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { /* 拖动期间失去捕获 */ }
        CheckDock();
    }

    void RestorePosition()
    {
        // 使用 Win32 物理坐标 + 当前 DPI 换算为 DIP，避免高缩放比例下窗口跑到屏幕外
        var dpi = VisualTreeHelper.GetDpi(this);
        var wx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var wy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        double l = 40, t = 40, r = 1200, b = 800;
        var monitor = Native.MonitorFromWindow(_hwnd, 2);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor != IntPtr.Zero && Native.GetMonitorInfo(monitor, ref mi))
        {
            l = mi.rcWork.Left / wx;
            t = mi.rcWork.Top / wy;
            r = mi.rcWork.Right / wx;
            b = mi.rcWork.Bottom / wy;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            l = wa.Left; t = wa.Top; r = wa.Right; b = wa.Bottom;
        }

        var s = _store.Settings;
        bool saved = s.Left is double sl && s.Top is double st &&
                     sl > l - Width + 120 && sl < r - 120 &&
                     st > t - 20 && st < b - 100;
        if (saved)
        {
            Left = s.Left!.Value;
            Top = s.Top!.Value;
        }
        else
        {
            Left = r - Width - 14;
            Top = t + 40;
        }
    }

    void SavePosition()
    {
        if (_docked || WindowState != WindowState.Normal) return;
        _store.Settings.Left = Left;
        _store.Settings.Top = Top;
        _store.SaveSettings();
    }

    // ================= 托盘菜单 =================

    void ShowTrayMenu()
    {
        Native.SetForegroundWindow(_hwnd);
        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            PlacementTarget = this
        };
        menu.Items.Add(MenuEntry(_docked ? "展开 Rou" : "显示 / 隐藏", _hotkeyLabel, ToggleVisibility));
        menu.Items.Add(MenuEntry("新建任务", "Ctrl+N", () => { WakeUp(); AddBox.Focus(); }));
        menu.Items.Add(MenuEntry("归档记录", "", () => { WakeUp(); RefreshArchive(); ShowOverlay(ArchivePanel); }));
        menu.Items.Add(MenuEntry("设置", "", () => { WakeUp(); ApplySettingsToUi(); ShowOverlay(SettingsPanel); }));
        menu.Items.Add(MenuEntry("退出", "", () => Application.Current.Shutdown()));
        menu.IsOpen = true;
    }

    static MenuItem MenuEntry(string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    // ================= 贴边吸附（Qiu 收纳） =================

    Native.MONITORINFO MonitorInfo()
    {
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        var monitor = Native.MonitorFromWindow(_hwnd, 2);
        if (monitor == IntPtr.Zero) return new Native.MONITORINFO();
        return Native.GetMonitorInfo(monitor, ref mi) ? mi : new Native.MONITORINFO();
    }

    void CheckDock()
    {
        if (_docked) return;
        if (!Native.GetWindowRect(_hwnd, out var wr)) return;
        var mi = MonitorInfo();
        if (mi.cbSize == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        int tol = (int)(18 * dpi.DpiScaleX);
        string edge = "none";
        if (Math.Abs(wr.Right - mi.rcWork.Right) <= tol) edge = "right";
        else if (Math.Abs(wr.Left - mi.rcWork.Left) <= tol) edge = "left";
        else if (Math.Abs(wr.Top - mi.rcWork.Top) <= tol) edge = "top";

        if (edge == "none")
        {
            if (_store.Settings.DockEdge != "none")
            {
                _store.Settings.DockEdge = "none";
                _store.SaveSettings();
            }
            return;
        }
        DockTo(edge, animate: true);
    }

    void DockTo(string edge, bool animate)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var mi = MonitorInfo();
        if (mi.cbSize == 0) return;

        if (!_docked)
        {
            _restoreBounds = new Rect(Left, Top, Width < 120 ? 376 : Width, Height < 120 ? 420 : Height);
            _store.Settings.Left = _restoreBounds.X;
            _store.Settings.Top = _restoreBounds.Y;
        }

        _docked = true;
        _dockEdge = edge;
        _store.Settings.DockEdge = edge;
        _store.SaveSettings();

        CardHost.Visibility = Visibility.Collapsed;
        DockTab.Visibility = Visibility.Visible;

        const double w = 54, h = 30;
        double wl = mi.rcWork.Left / sx, wt = mi.rcWork.Top / sy;
        double wr = mi.rcWork.Right / sx, wb = mi.rcWork.Bottom / sy;
        double cx = Left + Width / 2, cy = Top + Height / 2;

        double x = edge switch
        {
            "left" => wl + 2,
            "right" => wr - w - 2,
            _ => Math.Clamp(cx - w / 2, wl + 2, wr - w - 2)
        };
        double y = edge switch
        {
            "top" => wt + 2,
            _ => Math.Clamp(cy - h / 2, wt + 2, wb - h - 2)
        };

        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Width = w;
        Height = h;
        Left = x;
        Top = y;
    }

    void Undock()
    {
        if (!_docked) return;
        _docked = false;
        _dockEdge = "none";
        _store.Settings.DockEdge = "none";
        _store.SaveSettings();

        CardHost.Visibility = Visibility.Visible;
        DockTab.Visibility = Visibility.Collapsed;

        var target = _restoreBounds.Width > 120 ? _restoreBounds : new Rect(Left, Top, 376, 420);
        Width = target.Width;
        Height = target.Height;
        Left = target.X;
        Top = target.Y;

        var dpi = VisualTreeHelper.GetDpi(this);
        var mi = MonitorInfo();
        if (mi.cbSize != 0)
        {
            double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
            double wl = mi.rcWork.Left / sx, wt = mi.rcWork.Top / sy;
            double wr = mi.rcWork.Right / sx, wb = mi.rcWork.Bottom / sy;
            Left = Math.Clamp(Left, wl, Math.Max(wl, wr - Width));
            Top = Math.Clamp(Top, wt, Math.Max(wt, wb - Height));
        }

        Dispatcher.BeginInvoke(new Action(() => UpdateHeight(false)), DispatcherPriority.Loaded);
    }

    void OnUndockClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Undock();
        Activate();
    }

    // ================= 内存回收 =================

    void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }

    // ================= 外部同步 =================

    public void ReloadFromDisk()
    {
        if (_reallyClose) return;
        int before = _store.Tasks.Count;
        _store.Load();
        Rebuild();
        if (_store.Tasks.Count > before)
            Toast($"已同步 {_store.Tasks.Count - before} 项新任务");
    }

    void StartInboxWatch()
    {
        try
        {
            Directory.CreateDirectory(Store.InboxDir);
            Directory.CreateDirectory(Store.InboxDoneDir);
            _inboxWatch = new FileSystemWatcher(Store.InboxDir, "*.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _inboxWatch.Created += (_, _) => ScheduleInboxDrain();
            _inboxWatch.Renamed += (_, _) => ScheduleInboxDrain();
        }
        catch (Exception ex) { Store.Log("inbox watch: " + ex.Message); }
    }

    void ScheduleInboxDrain()
    {
        Task.Delay(400).ContinueWith(_ =>
            Dispatcher.BeginInvoke(new Action(() => DrainInbox(silent: false))));
    }

    void DrainInbox(bool silent)
    {
        var added = AgentInbox.Drain();
        if (added.Count == 0) return;
        _store.Load();
        foreach (var item in added) _entered.Remove(item.Id);
        Rebuild();
        if (!silent)
            Toast(added.Count == 1 ? "Agent 添加 · " + added[0].Title : $"Agent 添加 {added.Count} 项");
    }
}
