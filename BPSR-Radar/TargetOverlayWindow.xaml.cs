using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BpsrRadar;

// Movable, resizable overlay that shows the current lock-on target.
public partial class TargetOverlayWindow : Window
{
    private readonly DispatcherTimer settingsSaveTimer;
    private string lastText = "";
    private bool lastStrong;

    public TargetOverlayWindow()
    {
        InitializeComponent();

        var s = RadarSettings.Instance;
        Topmost = s.TargetOverlayTopMost;
        Width = Math.Max(MinWidth, s.TargetOverlayWidth);
        Height = Math.Max(MinHeight, s.TargetOverlayHeight);
        if (!double.IsNaN(s.TargetOverlayLeft) && !double.IsNaN(s.TargetOverlayTop) &&
            WindowPlacement.IntersectsMonitor(this, s.TargetOverlayLeft, s.TargetOverlayTop, Width, Height))
        {
            Left = s.TargetOverlayLeft;
            Top = s.TargetOverlayTop;
        }

        ApplySettings();

        settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        settingsSaveTimer.Tick += (_, _) =>
        {
            settingsSaveTimer.Stop();
            RadarSettings.Save();
        };
        LocationChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            s.TargetOverlayLeft = Left;
            s.TargetOverlayTop = Top;
            QueueSettingsSave();
        };
        SizeChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            s.TargetOverlayWidth = Width;
            s.TargetOverlayHeight = Height;
            QueueSettingsSave();
        };
    }

    private void QueueSettingsSave()
    {
        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    public void EnsureVisible() => WindowPlacement.EnsureOnScreen(this);

    // Follows the main window's click-through setting.
    public void SyncClickThrough() =>
        WindowPlacement.SetClickThrough(this, RadarSettings.Instance.ClickThrough);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SyncClickThrough();
    }

    private static SolidColorBrush Argb(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static uint ScaleAlpha(uint argb, double factor)
    {
        uint alpha = (uint)Math.Round((argb >> 24) * factor);
        return (argb & 0x00FFFFFF) | (alpha << 24);
    }

    public void ApplySettings()
    {
        var s = RadarSettings.Instance;
        Topmost = s.TargetOverlayTopMost;
        double opacity = Math.Clamp(s.TargetOverlayOpacity, 0.15, 1.0);
        Frame.Background = Argb(ScaleAlpha(s.TargetOverlayBackgroundArgb, opacity));
        Frame.BorderBrush = Argb(ScaleAlpha(s.TargetOverlayBorderArgb, opacity));
        Frame.BorderThickness = new Thickness(Math.Clamp(s.TargetOverlayBorderThickness, 0, 6));
        TargetText.Foreground = Argb(s.TargetOverlayTextArgb);
        TargetText.FontSize = Math.Clamp(s.TargetOverlayFontSize, 8, 48);
    }

    public void SetText(string text, bool strong = false)
    {
        var t = string.IsNullOrWhiteSpace(text) ? "—" : text;
        if (t == lastText && strong == lastStrong)
        {
            return;
        }
        lastText = t;
        lastStrong = strong;
        TargetText.Text = t;
        TargetText.FontWeight = strong ? FontWeights.Bold : FontWeights.Normal;
        TargetText.Opacity = string.IsNullOrWhiteSpace(text) ? 0.45 : 1.0;
    }

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource as DependencyObject, ResizeThumb))
        {
            return;
        }
        try { DragMove(); } catch { }
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (node == ancestor) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        RadarSettings.Save();
        base.OnClosed(e);
    }
}
