using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BpsrRadar;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x08000000;

    private const int WmHotkey = 0x0312;
    private const int WmDisplayChange = 0x007E;
    private const int ClickThroughHotkeyId = 0xB522;
    // Harness ground-truth marks, only claimed while recording a session.
    private const int MarkLockHotkeyId = 0xB523;
    private const int MarkClearHotkeyId = 0xB524;
    private const int MarkAutoHotkeyId = 0xB525;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkR = 0x52;
    private const uint VkM = 0x4D;
    private const uint VkN = 0x4E;
    // B, deliberately not next to M and N: one run recorded a lock and a
    // clear nine milliseconds apart from neighbouring keys.
    private const uint VkB = 0x42;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoActivate = 0x10;

    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer settingsSaveTimer;
    private SettingsWindow? settingsWindow;
    private TargetOverlayWindow? targetOverlay;
    private EnemyListWindow? enemyList;
    private ClickThroughGuardWindow? clickThroughGuard;

    public MainWindow()
    {
        InitializeComponent();

        var settings = RadarSettings.Instance;
        if (settings.HasWindowPosition)
        {
            Width = Math.Max(MinWidth, settings.WindowWidth);
            Height = Math.Max(MinHeight, settings.WindowHeight);
            if (WindowPlacement.IntersectsMonitor(this, settings.WindowLeft, settings.WindowTop, Width, Height))
            {
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
            }
        }
        Topmost = settings.TopMost;
        SetPinLook(settings.TopMost);
        SetToggleLook(ThroughButton, settings.ClickThrough);
        SetToggleLook(LockButton, settings.LockTargetEnabled);
        RootBrush.Opacity = settings.BackgroundOpacity;

        ApplyStrings();

        PinButton.Click += (_, _) =>
        {
            settings.TopMost = !settings.TopMost;
            Topmost = settings.TopMost;
            SetPinLook(settings.TopMost);
            RadarSettings.Save();
        };
        ThroughButton.Click += (_, _) => ToggleClickThrough();
        LockButton.Click += (_, _) =>
        {
            settings.LockTargetEnabled = !settings.LockTargetEnabled;
            SetToggleLook(LockButton, settings.LockTargetEnabled);
            SyncTargetOverlay();
            RadarSettings.Save();
            // The open settings window shows the lock switch and the rows that
            // depend on it; showing the overlay also changes the Z order.
            settingsWindow?.Rebuild();
            KeepSettingsWindowOnTop();
        };
        // Without a background the title bar's empty space is not hit-tested,
        // so only the 16px caption text could be grabbed to move the window.
        TitleBar.Background = System.Windows.Media.Brushes.Transparent;
        // The only way to open the settings window.
        SettingsButton.Click += (_, _) => OpenSettingsWindow();
        CloseButton.Click += (_, _) => Close();

        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        refreshTimer.Tick += (_, _) => Refresh();
        refreshTimer.Start();

        // The timer still drives the radar, which is fed by 100ms packet
        // snapshots and has nothing to gain from running faster. The lock
        // target does: it can change several times a second while the player
        // cycles through a pull, and waiting for the next tick was the last
        // sampling delay in the chain. So it arrives as an event instead.
        LockTargetService.TargetChanged += OnTargetChanged;

        settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        settingsSaveTimer.Tick += (_, _) =>
        {
            settingsSaveTimer.Stop();
            RadarSettings.Save();
        };
        LocationChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            var s = RadarSettings.Instance;
            s.WindowLeft = Left;
            s.WindowTop = Top;
            s.HasWindowPosition = true;
            QueueSettingsSave();
        };
        SizeChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            var s = RadarSettings.Instance;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            QueueSettingsSave();
        };

        Loaded += (_, _) => { SyncTargetOverlay(); SyncEnemyList(); };
    }

    private void QueueSettingsSave()
    {
        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    private void ShowTargetOverlay()
    {
        targetOverlay ??= new TargetOverlayWindow { Owner = this };
        targetOverlay.ApplySettings();
        targetOverlay.Show();
    }

    private void SyncTargetOverlay()
    {
        var settings = RadarSettings.Instance;
        if (settings.TargetOverlayEnabled && settings.LockTargetEnabled)
        {
            ShowTargetOverlay();
        }
        else if (targetOverlay != null)
        {
            targetOverlay.Close();
            targetOverlay = null;
        }
    }

    private void SyncEnemyList()
    {
        if (RadarSettings.Instance.EnemyListEnabled)
        {
            enemyList ??= new EnemyListWindow { Owner = this };
            enemyList.ApplySettings();
            enemyList.Show();
            enemyList.SetSnapshot(RadarTracker.GetSnapshot(RadarSettings.Instance), ActiveTargetUuid);
        }
        else if (enemyList != null)
        {
            enemyList.Close();
            enemyList = null;
        }
    }

    private static long ActiveTargetUuid => RadarSettings.Instance.LockTargetEnabled
        ? LockTargetService.CurrentTargetUuid : 0;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThrough();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            RegisterHotKey(hwnd, ClickThroughHotkeyId, ModControl | ModAlt | ModNoRepeat, VkR);
            if (MarkLog.Enabled)
            {
                RegisterHotKey(hwnd, MarkLockHotkeyId, ModControl | ModAlt | ModNoRepeat, VkM);
                RegisterHotKey(hwnd, MarkClearHotkeyId, ModControl | ModAlt | ModNoRepeat, VkN);
                RegisterHotKey(hwnd, MarkAutoHotkeyId, ModControl | ModAlt | ModNoRepeat, VkB);
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == ClickThroughHotkeyId)
        {
            ToggleClickThrough();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == MarkLockHotkeyId)
        {
            MarkLog.Mark("lock");
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == MarkClearHotkeyId)
        {
            MarkLog.Mark("clear");
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == MarkAutoHotkeyId)
        {
            MarkLog.Mark("auto");
            handled = true;
        }
        else if (msg == WmDisplayChange)
        {
            Dispatcher.BeginInvoke(() =>
            {
                WindowPlacement.EnsureOnScreen(this);
                settingsWindow?.KeepInWorkArea();
                targetOverlay?.EnsureVisible();
                enemyList?.EnsureVisible();
            });
        }
        return IntPtr.Zero;
    }

    private void ResizeBorder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        if (sender is FrameworkElement el && int.TryParse(el.Tag?.ToString(), out int hitTest))
        {
            ReleaseCapture();
            SendMessage(new WindowInteropHelper(this).Handle, 0x00A1, new IntPtr(hitTest), IntPtr.Zero);
            e.Handled = true;
        }
    }

    // A toggle in the title bar: ON is the accent colour with a line under
    // the button (the template shows it for Tag="on"), OFF a muted grey.
    private static void SetToggleLook(Button button, bool on)
    {
        button.Foreground = new System.Windows.Media.SolidColorBrush(on
            ? System.Windows.Media.Color.FromRgb(0x3F, 0xA7, 0xAD)
            : System.Windows.Media.Color.FromRgb(0x7F, 0x9A, 0x9E));
        button.Tag = on ? "on" : null;
    }

    // Segoe Fluent Icons: Pinned (filled) when on top, Pin (outline) when not.
    private void SetPinLook(bool on)
    {
        PinButton.Content = on ? "" : "";
        SetToggleLook(PinButton, on);
    }

    private void ApplyStrings()
    {
        PinButton.ToolTip = S.T("TopMost", "常に最前面");
        ThroughButton.ToolTip = S.T("Click-through — toggle anytime with Ctrl+Alt+R",
            "クリック透過 — Ctrl+Alt+R でいつでも切替");
        LockButton.ToolTip = S.T("Lock target", "ロック対象");
        SettingsButton.ToolTip = S.T("Settings", "設定");
        CloseButton.ToolTip = S.T("Close", "閉じる");
        FitButton.Content = S.T("Fit", "全体");
    }

    private void ToggleClickThrough()
    {
        var settings = RadarSettings.Instance;
        settings.ClickThrough = !settings.ClickThrough;
        ApplyClickThrough();
        SetToggleLook(ThroughButton, settings.ClickThrough);
        RadarSettings.Save();
    }

    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int style = GetWindowLong(hwnd, GwlExStyle);
            style |= WsExLayered | WsExNoActivate;
            SetWindowLong(hwnd, GwlExStyle, style);
        }
        // AllowsTransparency windows are hit-tested per-pixel by the OS, so
        // WM_NCHITTEST cannot make them selectively transparent. Instead the
        // whole window passes clicks and a small guard window sits on the
        // toggle button so click-through can always be switched back off.
        WindowPlacement.SetClickThrough(this, RadarSettings.Instance.ClickThrough);
        targetOverlay?.SyncClickThrough();
        enemyList?.SyncClickThrough();
        SyncClickThroughGuard();
    }

    private void SyncClickThroughGuard()
    {
        if (RadarSettings.Instance.ClickThrough)
        {
            clickThroughGuard ??= new ClickThroughGuardWindow { Owner = this };
            clickThroughGuard.Clicked -= OnClickThroughGuardClicked;
            clickThroughGuard.Clicked += OnClickThroughGuardClicked;
            clickThroughGuard.ApplyStrings();
            clickThroughGuard.TrackTo(ThroughButton);
            clickThroughGuard.Show();
        }
        else
        {
            clickThroughGuard?.Hide();
        }
    }

    private void OnClickThroughGuardClicked() => ToggleClickThrough();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e) =>
        SetZoom(RadarSettings.Instance.Zoom + (e.Delta > 0 ? 0.25f : -0.25f));

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(RadarSettings.Instance.Zoom - 0.25f);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(RadarSettings.Instance.Zoom + 0.25f);

    private void Fit_Click(object sender, RoutedEventArgs e) => SetZoom(1f);

    // Saved like every other setting, not only when the window closes.
    private void SetZoom(float zoom)
    {
        RadarSettings.Instance.Zoom = float.IsFinite(zoom) ? Math.Clamp(zoom, 0.5f, 4f) : 1f;
        QueueSettingsSave();
    }

    // Raised from the capture thread and from the lock-target service loop,
    // so it has to hop to the UI thread before touching anything. Only the
    // target-dependent parts are redrawn: the rest of Refresh reads a packet
    // snapshot that has not moved.
    private void OnTargetChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(new Action(OnTargetChanged)); } catch { }
            return;
        }
        UpdateTargetOverlay();
        enemyList?.SetTarget(ActiveTargetUuid);
        MapCanvas.LockTargetUuid = LockTargetService.CurrentTargetUuid;
        MapCanvas.InvalidateVisual();
    }

    private void UpdateTargetOverlay()
    {
        if (targetOverlay == null) return;
        targetOverlay.SetText(LockTargetService.StatusText, LockTargetService.TargetKind);
        long uuid = LockTargetService.CurrentTargetUuid;
        bool hasHp = RadarTracker.TryGetHp(uuid, out long hp, out long maxHp);
        targetOverlay.SetHp(uuid != 0, hasHp, hp, maxHp);
    }

    private void Refresh()
    {
        var settings = RadarSettings.Instance;
        var snapshot = RadarTracker.GetSnapshot(settings);
        MapCanvas.Snapshot = snapshot;
        MapCanvas.LockTargetUuid = LockTargetService.CurrentTargetUuid;
        MapCanvas.InvalidateVisual();
        UpdateTargetOverlay();
        enemyList?.SetSnapshot(snapshot, ActiveTargetUuid);
        if (clickThroughGuard?.IsVisible == true)
        {
            clickThroughGuard.TrackTo(ThroughButton);
        }

        int entityCount = (snapshot.Self == null ? 0 : 1) + snapshot.Party.Length + snapshot.Players.Length + snapshot.Enemies.Length;
        string scene = !string.IsNullOrEmpty(snapshot.SceneName) ? snapshot.SceneName : snapshot.SceneId.ToString();
        string target = settings.LockTargetEnabled ? $" · {S.T("Target", "ターゲット")}: {LockTargetService.StatusText}" : "";
        string capture = CaptureService.Running ? "" : (CaptureService.LastError != null
            ? $" · {S.T("capture error", "キャプチャエラー")}"
            : $" · {S.T("capture off", "キャプチャ停止")}");
        FitButton.Visibility = settings.ViewMode == RadarViewMode.ScanOverview
            ? Visibility.Visible : Visibility.Collapsed;
        // A capture problem goes first: trimming cuts the end of this line.
        string head = $"{S.T("Scene", "シーン")} {scene} · {entityCount} · x{settings.Zoom:0.##}";
        StatusText.Text = string.IsNullOrEmpty(capture)
            ? $"{head}{target}"
            : $"{capture.TrimStart(' ', '·')} · {head}{target}";
    }

    // Opens the settings window, or brings the open one to the front.
    private void OpenSettingsWindow()
    {
        if (settingsWindow != null)
        {
            settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(new SettingsCallbacks
        {
            SyncTargetOverlay = SyncTargetOverlay,
            SyncEnemyList = SyncEnemyList,
            ApplyTargetOverlay = () => targetOverlay?.ApplySettings(),
            ApplyEnemyList = () => enemyList?.ApplySettings(),
            BackgroundOpacityChanged = v => RootBrush.Opacity = v,
            LockTargetChanged = v => SetToggleLook(LockButton, v),
            LanguageChanged = OnLanguageChanged,
            AfterWindowChange = KeepSettingsWindowOnTop,
        })
        {
            Owner = this,
        };
        window.Closed += (_, _) =>
        {
            settingsWindow = null;
            // Back to the resting look it has always had, not the muted OFF grey.
            SettingsButton.ClearValue(Control.ForegroundProperty);
            SettingsButton.Tag = null;
        };
        settingsWindow = window;
        SetToggleLook(SettingsButton, true);
        window.Show();
    }

    private void OnLanguageChanged()
    {
        ApplyStrings();
        // HP units (億 / B) and list labels are language-dependent.
        targetOverlay?.ApplySettings();
        enemyList?.ApplySettings();
        settingsWindow?.Rebuild();
    }

    // Showing the overlay or enemy list, or switching one to Always on top,
    // re-inserts it together with its owner (this window) at the top of the
    // topmost band -- above the open settings window, which then sat behind
    // the radar. Measured 2026-10-21 with a minimal repro: ShowActivated=false
    // does not prevent it; raising the window again once idle does. (That repro
    // was a popup; the target is now the settings window's own HWND.)
    private void KeepSettingsWindowOnTop()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (settingsWindow != null && new WindowInteropHelper(settingsWindow).Handle is var handle && handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    protected override void OnClosed(EventArgs e)
    {
        refreshTimer.Stop();
        // The service is static and outlives the window during shutdown, so
        // an event left attached would keep dispatching into a closed one.
        LockTargetService.TargetChanged -= OnTargetChanged;
        // An owned window closes with its owner anyway; closing it here fixes
        // the order of its settings save relative to this window's.
        settingsWindow?.Close();
        targetOverlay?.Close();
        enemyList?.Close();
        clickThroughGuard?.Close();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, ClickThroughHotkeyId);
        }
        var settings = RadarSettings.Instance;
        settings.WindowLeft = Left;
        settings.WindowTop = Top;
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        settings.HasWindowPosition = true;
        RadarSettings.Save();
        base.OnClosed(e);
    }
}
