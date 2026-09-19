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

    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer settingsSaveTimer;
    private Popup? settingsPopup;
    private TargetOverlayWindow? targetOverlay;
    private ClickThroughGuardWindow? clickThroughGuard;
    private bool overlaySectionOpen;

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
        PinButton.Opacity = settings.TopMost ? 1.0 : 0.45;
        ThroughButton.Opacity = settings.ClickThrough ? 1.0 : 0.45;
        LockButton.Opacity = settings.LockTargetEnabled ? 1.0 : 0.45;
        RootBrush.Opacity = settings.BackgroundOpacity;

        ApplyStrings();

        PinButton.Click += (_, _) =>
        {
            settings.TopMost = !settings.TopMost;
            Topmost = settings.TopMost;
            PinButton.Opacity = settings.TopMost ? 1.0 : 0.45;
            RadarSettings.Save();
        };
        ThroughButton.Click += (_, _) => ToggleClickThrough();
        LockButton.Click += (_, _) =>
        {
            settings.LockTargetEnabled = !settings.LockTargetEnabled;
            LockButton.Opacity = settings.LockTargetEnabled ? 1.0 : 0.45;
            SyncTargetOverlay();
            RadarSettings.Save();
        };
        SettingsButton.Click += (_, _) => ToggleSettingsPopup();
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

        Loaded += (_, _) => SyncTargetOverlay();
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
                targetOverlay?.EnsureVisible();
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
        ThroughButton.Opacity = settings.ClickThrough ? 1.0 : 0.45;
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
        SyncClickThroughGuard();
    }

    private void SyncClickThroughGuard()
    {
        if (RadarSettings.Instance.ClickThrough)
        {
            if (settingsPopup != null)
            {
                settingsPopup.IsOpen = false;
            }
            clickThroughGuard ??= new ClickThroughGuardWindow { Owner = this };
            clickThroughGuard.Clicked -= OnClickThroughGuardClicked;
            clickThroughGuard.Clicked += OnClickThroughGuardClicked;
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

    private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var settings = RadarSettings.Instance;
        settings.Zoom = Math.Clamp(settings.Zoom + (e.Delta > 0 ? 0.25f : -0.25f), 0.5f, 4f);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        var settings = RadarSettings.Instance;
        settings.Zoom = Math.Clamp(settings.Zoom - 0.25f, 0.5f, 4f);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        var settings = RadarSettings.Instance;
        settings.Zoom = Math.Clamp(settings.Zoom + 0.25f, 0.5f, 4f);
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        RadarSettings.Instance.Zoom = 1f;
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
        targetOverlay?.SetText(LockTargetService.StatusText, LockTargetService.TargetIsStrong);
        MapCanvas.LockTargetUuid = LockTargetService.CurrentTargetUuid;
        MapCanvas.InvalidateVisual();
    }

    private void Refresh()
    {
        var settings = RadarSettings.Instance;
        var snapshot = RadarTracker.GetSnapshot(settings);
        MapCanvas.Snapshot = snapshot;
        MapCanvas.LockTargetUuid = LockTargetService.CurrentTargetUuid;
        MapCanvas.InvalidateVisual();
        targetOverlay?.SetText(LockTargetService.StatusText, LockTargetService.TargetIsStrong);
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
        StatusText.Text = $"{S.T("Scene", "シーン")} {scene} · {entityCount} · x{settings.Zoom:0.##}{target}{capture}";
    }

    private void ToggleSettingsPopup()
    {
        if (settingsPopup?.IsOpen == true)
        {
            settingsPopup.IsOpen = false;
            return;
        }

        var settings = RadarSettings.Instance;
        var panel = new StackPanel { Margin = new Thickness(10) };

        panel.Children.Add(MakeCheck(S.T("Self", "自分"), settings.ShowSelf, v => settings.ShowSelf = v));
        panel.Children.Add(MakeCheck(S.T("Party", "パーティー"), settings.ShowParty, v => settings.ShowParty = v));
        panel.Children.Add(MakeCheck(S.T("Other players", "他プレイヤー"), settings.ShowPlayers, v => settings.ShowPlayers = v));
        panel.Children.Add(MakeCheck(S.T("Normal monsters", "通常モンスター"), settings.ShowNormalMonsters, v => settings.ShowNormalMonsters = v));
        panel.Children.Add(MakeCheck(S.T("Elite", "エリート"), settings.ShowEliteMonsters, v => settings.ShowEliteMonsters = v));
        panel.Children.Add(MakeCheck(S.T("Boss", "ボス"), settings.ShowBosses, v => settings.ShowBosses = v));
        panel.Children.Add(MakeCheck(S.T("Unknown monsters", "種別不明"), settings.ShowUnknownMonsters, v => settings.ShowUnknownMonsters = v));
        panel.Children.Add(MakeCheck(S.T("Gimmicks", "ギミック"), settings.ShowGimmicks, v => settings.ShowGimmicks = v));
        panel.Children.Add(MakeCheck(S.T("Self direction", "自分の向き"), settings.ShowSelfDirection, v => settings.ShowSelfDirection = v));
        panel.Children.Add(MakeCheck(S.T("Distance rings", "距離リング"), settings.ShowDistanceRings, v => settings.ShowDistanceRings = v));
        panel.Children.Add(MakeCheck(S.T("Ring labels", "リング距離表示"), settings.ShowDistanceRingLabels, v => settings.ShowDistanceRingLabels = v));
        panel.Children.Add(MakeCheck(S.T("Lock target", "ロック対象"), settings.LockTargetEnabled, v => { settings.LockTargetEnabled = v; LockButton.Opacity = v ? 1.0 : 0.45; SyncTargetOverlay(); }));

        var overlayPanel = new StackPanel { Margin = new Thickness(10, 4, 0, 0) };
        overlayPanel.Children.Add(MakeCheck(S.T("Show overlay", "オーバーレイ表示"), settings.TargetOverlayEnabled, v =>
        {
            settings.TargetOverlayEnabled = v;
            SyncTargetOverlay();
        }));
        overlayPanel.Children.Add(MakeCheck(S.T("Always on top", "常に最前面"), settings.TargetOverlayTopMost, v => { settings.TargetOverlayTopMost = v; targetOverlay?.ApplySettings(); }));
        overlayPanel.Children.Add(MakeSliderRow(S.T("Opacity", "透明度"), (float)settings.TargetOverlayOpacity, 0.15f, 1f, v => { settings.TargetOverlayOpacity = v; targetOverlay?.ApplySettings(); }));
        overlayPanel.Children.Add(MakeSliderRow(S.T("Text size", "文字サイズ"), (float)settings.TargetOverlayFontSize, 9f, 32f, v => { settings.TargetOverlayFontSize = v; targetOverlay?.ApplySettings(); }));
        overlayPanel.Children.Add(MakeSliderRow(S.T("Frame width", "枠の太さ"), (float)settings.TargetOverlayBorderThickness, 0f, 4f, v => { settings.TargetOverlayBorderThickness = v; targetOverlay?.ApplySettings(); }));
        overlayPanel.Children.Add(MakeColorRow(S.T("Frame", "枠色"), () => settings.TargetOverlayBorderArgb, v => settings.TargetOverlayBorderArgb = v));
        overlayPanel.Children.Add(MakeColorRow(S.T("Background", "背景色"), () => settings.TargetOverlayBackgroundArgb, v => settings.TargetOverlayBackgroundArgb = v));
        overlayPanel.Children.Add(MakeColorRow(S.T("Text", "文字色"), () => settings.TargetOverlayTextArgb, v => settings.TargetOverlayTextArgb = v));
        var overlayExpander = new Expander
        {
            Header = S.T("Target overlay", "ターゲットオーバーレイ"),
            Foreground = System.Windows.Media.Brushes.White,
            IsExpanded = overlaySectionOpen,
            Content = overlayPanel,
        };
        overlayExpander.Expanded += (_, _) => overlaySectionOpen = true;
        overlayExpander.Collapsed += (_, _) => overlaySectionOpen = false;
        panel.Children.Add(overlayExpander);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });

        var modeRow = new StackPanel { Orientation = Orientation.Horizontal };
        modeRow.Children.Add(new TextBlock { Text = S.T("View:", "表示:"), VerticalAlignment = VerticalAlignment.Center, Width = 50 });
        var modeCombo = new ComboBox { Width = 110 };
        AddComboItem(modeCombo, RadarViewMode.MiniMap, S.T("Mini map", "ミニマップ"));
        AddComboItem(modeCombo, RadarViewMode.ScanOverview, S.T("Scan overview", "全体スキャン"));
        SelectComboItem(modeCombo, settings.ViewMode);
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (modeCombo.SelectedItem is ComboBoxItem item)
            {
                settings.ViewMode = (RadarViewMode)item.Tag;
                RadarSettings.Save();
            }
        };
        modeRow.Children.Add(modeCombo);
        panel.Children.Add(modeRow);

        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        labelRow.Children.Add(new TextBlock { Text = S.T("Labels:", "ラベル:"), VerticalAlignment = VerticalAlignment.Center, Width = 50 });
        var labelCombo = new ComboBox { Width = 110 };
        AddComboItem(labelCombo, RadarLabelMode.None, S.T("None", "なし"));
        AddComboItem(labelCombo, RadarLabelMode.PartyAndBoss, S.T("Party + Boss", "PT+ボス"));
        AddComboItem(labelCombo, RadarLabelMode.All, S.T("All", "すべて"));
        SelectComboItem(labelCombo, settings.LabelMode);
        labelCombo.SelectionChanged += (_, _) =>
        {
            if (labelCombo.SelectedItem is ComboBoxItem item)
            {
                settings.LabelMode = (RadarLabelMode)item.Tag;
                RadarSettings.Save();
            }
        };
        labelRow.Children.Add(labelCombo);
        panel.Children.Add(labelRow);

        panel.Children.Add(MakeSliderRow(S.T("Enemy range", "敵の表示距離"), settings.EnemyMaxDistanceMeters, 5, 500, v => settings.EnemyMaxDistanceMeters = v));
        panel.Children.Add(MakeSliderRow(S.T("Map range", "マップ範囲"), settings.BaseDisplayRangeMeters, 20, 500, v => settings.BaseDisplayRangeMeters = v));
        panel.Children.Add(MakeSliderRow(S.T("Background", "背景の濃さ"), settings.BackgroundOpacity, 0.1f, 1f, v => { settings.BackgroundOpacity = v; RootBrush.Opacity = v; }));

        panel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });

        var langRow = new StackPanel { Orientation = Orientation.Horizontal };
        langRow.Children.Add(new TextBlock { Text = S.T("Language:", "言語:"), VerticalAlignment = VerticalAlignment.Center, Width = 50 });
        var langCombo = new ComboBox { Width = 110 };
        AddComboItem(langCombo, "en", "English");
        AddComboItem(langCombo, "ja", "日本語");
        SelectComboItem(langCombo, settings.Language);
        langCombo.SelectionChanged += (_, _) =>
        {
            if (langCombo.SelectedItem is ComboBoxItem item)
            {
                settings.Language = (string)item.Tag;
                RadarSettings.Save();
                ApplyStrings();
                settingsPopup!.IsOpen = false;
                ToggleSettingsPopup();
            }
        };
        langRow.Children.Add(langCombo);
        panel.Children.Add(langRow);

        settingsPopup = new Popup
        {
            Child = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 28, 32)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 95, 100)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new ScrollViewer
                {
                    MaxHeight = 460,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel,
                },
            },
            PlacementTarget = SettingsButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        settingsPopup.Closed += (_, _) => RadarSettings.Save();
        settingsPopup.IsOpen = true;
    }

    private static void AddComboItem(ComboBox combo, object tag, string text)
    {
        combo.Items.Add(new ComboBoxItem { Tag = tag, Content = text });
    }

    private static void SelectComboItem(ComboBox combo, object tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (Equals(item.Tag, tag))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static CheckBox MakeCheck(string label, bool value, Action<bool> onChange)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Foreground = System.Windows.Media.Brushes.White };
        check.Checked += (_, _) => { onChange(true); RadarSettings.Save(); };
        check.Unchecked += (_, _) => { onChange(false); RadarSettings.Save(); };
        return check;
    }

    private static readonly uint[] OverlayPalette =
    [
        0xFFFFFFFF, 0xFF55E0E6, 0xFF7BE37B, 0xFFE3D07B, 0xFFE39A5B,
        0xFFE35B5B, 0xFFD07BE3, 0xFF7BA8E3, 0xFF9AA5A8, 0xFF20262A, 0x00000000,
    ];

    private FrameworkElement MakeColorRow(string label, Func<uint> get, Action<uint> set)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center });
        foreach (var argb in OverlayPalette)
        {
            var color = System.Windows.Media.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            var swatch = new Border
            {
                Width = 15, Height = 15, Margin = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    argb == get()
                        ? System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)
                        : System.Windows.Media.Color.FromRgb(0x55, 0x5F, 0x62)),
                Background = new System.Windows.Media.SolidColorBrush(color),
                ToolTip = argb == 0 ? "Transparent" : $"#{argb:X8}",
                Cursor = Cursors.Hand,
            };
            ToolTipService.SetInitialShowDelay(swatch, 0);
            var captured = argb;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                set(captured);
                RadarSettings.Save();
                targetOverlay?.ApplySettings();
                settingsPopup!.IsOpen = false;
                ToggleSettingsPopup();
                e.Handled = true;
            };
            row.Children.Add(swatch);
        }
        return row;
    }

    private static FrameworkElement MakeSliderRow(string label, float value, float min, float max, Action<float> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center });
        var slider = new Slider
        {
            Width = 130,
            Minimum = min,
            Maximum = max,
            Value = value,
            VerticalAlignment = VerticalAlignment.Center,
            AutoToolTipPlacement = AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = (max - min) <= 4 ? 2 : 0,
        };
        slider.ValueChanged += (_, e) => { onChange((float)e.NewValue); };
        row.Children.Add(slider);
        return row;
    }

    protected override void OnClosed(EventArgs e)
    {
        refreshTimer.Stop();
        // The service is static and outlives the window during shutdown, so
        // an event left attached would keep dispatching into a closed one.
        LockTargetService.TargetChanged -= OnTargetChanged;
        targetOverlay?.Close();
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
