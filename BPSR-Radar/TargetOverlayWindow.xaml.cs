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
    private RadarEntityKind? lastKind;
    private (bool HasTarget, bool HasHp, long Hp, long MaxHp)? lastHp;

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

    // Elite yellow, boss orange (the radar's boss marker colour). The alpha
    // of the player's text colour still applies; other names use it as is.
    internal static uint NameArgb(RadarEntityKind? kind, uint textArgb) => kind switch
    {
        RadarEntityKind.Elite => (textArgb & 0xFF000000) | 0xFFD84A,
        RadarEntityKind.Boss => (textArgb & 0xFF000000) | 0xFF941F,
        _ => textArgb,
    };

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
        // Alpha 0 pixels of a layered window pass clicks through, so a fully
        // transparent background could not be grabbed; 1/255 is invisible.
        uint background = ScaleAlpha(s.TargetOverlayBackgroundArgb, opacity);
        if (background >> 24 == 0) background |= 0x01000000;
        Frame.Background = Argb(background);
        Frame.BorderBrush = Argb(ScaleAlpha(s.TargetOverlayBorderArgb, opacity));
        Frame.BorderThickness = new Thickness(Math.Clamp(s.TargetOverlayBorderThickness, 0, 6));
        TargetText.Foreground = Argb(NameArgb(lastKind, s.TargetOverlayTextArgb));
        TargetText.FontSize = Math.Clamp(s.TargetOverlayFontSize, 8, 48);
        HpText.Foreground = Argb(s.TargetOverlayTextArgb);
        var mode = s.EffectiveTargetOverlayHp;
        if (mode == TargetOverlayHpMode.Percent)
        {
            // The percentage is the only HP element then, so it reads as large
            // as the name does.
            HpText.FontSize = Math.Max(13, Math.Round(TargetText.FontSize * 1.15));
            HpText.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            // Below ~12 DIP, kanji such as 億 lose their bottom strokes to the
            // pixel grid and read as clipped.
            HpText.FontSize = Math.Max(12, TargetText.FontSize * 0.85);
            HpText.FontWeight = FontWeights.Normal;
        }
        // Alone in the row, the bar is drawn thicker.
        HpBar.Height = mode == TargetOverlayHpMode.Bar
            ? Math.Max(5, Math.Round(TargetText.FontSize * 0.55))
            : Math.Max(3, Math.Round(TargetText.FontSize * 0.35));
        var hp = lastHp;
        lastHp = null;
        if (hp is { } h) SetHp(h.HasTarget, h.HasHp, h.Hp, h.MaxHp);
        else ApplyHpMode(hasTarget: false);
        FitToContents();
    }

    // The bar or the text, never both: the mode picks one of the two that
    // share the HP row's cell.
    private void ApplyHpMode(bool hasTarget)
    {
        var mode = RadarSettings.Instance.EffectiveTargetOverlayHp;
        HpRow.Visibility = mode == TargetOverlayHpMode.Off ? Visibility.Collapsed
            : hasTarget ? Visibility.Visible : Visibility.Hidden;
        HpBar.Visibility = mode == TargetOverlayHpMode.Bar ? Visibility.Visible : Visibility.Collapsed;
        HpText.Visibility = mode is TargetOverlayHpMode.Percent or TargetOverlayHpMode.Actual
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // The HP row adds a line under the name. A size saved before it existed
    // (or a larger font) would clip it, so grow to the content and keep the
    // resize thumb from shrinking below it. Never shrinks a larger window.
    // Runs again whenever the name or HP text changes.
    private void FitToContents()
    {
        Frame.UpdateLayout();
        Frame.Measure(new Size(Width, double.PositiveInfinity));
        double neededHeight = Math.Max(30, Math.Ceiling(Frame.DesiredSize.Height));
        MinHeight = neededHeight;
        if (Height < neededHeight) Height = neededHeight;

        // Width: the bar stretches to any width, so only the text modes need
        // room: the longest HP text of the current mode, or it is cut off.
        var mode = RadarSettings.Instance.EffectiveTargetOverlayHp;
        double neededWidth = 90;
        if (mode is TargetOverlayHpMode.Percent or TargetOverlayHpMode.Actual)
        {
            string longest = mode == TargetOverlayHpMode.Actual ? "99,999,999 / 99,999,999" : "100.0%";
            var probe = new System.Windows.Controls.TextBlock
            {
                Text = longest, FontSize = HpText.FontSize, FontFamily = HpText.FontFamily,
                FontWeight = HpText.FontWeight,
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double text = Math.Max(probe.DesiredSize.Width, HpText.DesiredSize.Width);
            double chrome = 10 + 16 + Frame.BorderThickness.Left + Frame.BorderThickness.Right;
            neededWidth = Math.Max(neededWidth, Math.Ceiling(chrome + text));
        }
        MinWidth = neededWidth;
        if (Width < neededWidth) Width = neededWidth;
    }

    public void SetHp(bool hasTarget, bool hasHp, long hp, long maxHp)
    {
        hasHp &= maxHp > 0;
        var state = (hasTarget, hasHp, hp, maxHp);
        if (lastHp == state) return;
        lastHp = state;
        var s = RadarSettings.Instance;
        ApplyHpMode(hasTarget);
        double ratio = hasHp ? Math.Clamp((double)hp / maxHp, 0, 1) : 0;
        HpFilledColumn.Width = new GridLength(ratio, GridUnitType.Star);
        HpRemainingColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
        HpFill.Visibility = hasHp ? Visibility.Visible : Visibility.Collapsed;
        HpTrack.Opacity = hasHp ? 1.0 : 0.4;
        // No text while the bar (or nothing) is shown, so a hidden label
        // cannot go stale or be read by accident.
        var mode = s.EffectiveTargetOverlayHp;
        string text = mode is TargetOverlayHpMode.Percent or TargetOverlayHpMode.Actual
            ? FormatHp(hasHp, hp, maxHp, mode == TargetOverlayHpMode.Actual) : "";
        if (HpText.Text != text)
        {
            HpText.Text = text;
            FitToContents();
        }
    }

    internal static string FormatHp(bool hasHp, long hp, long maxHp, bool actual)
    {
        if (!hasHp || maxHp <= 0) return "HP —";
        if (actual) return $"{FormatAmount(Math.Max(0, hp))} / {FormatAmount(maxHp)}";
        // Rounded down so a damaged target never reads 100.0%, and a living
        // one never reads 0.0%.
        double percent = Math.Floor(Math.Clamp((double)hp / maxHp, 0, 1) * 1000) / 10;
        if (hp > 0 && percent == 0) percent = 0.1;
        return $"{percent:0.0}%";
    }

    // Full digits below 100 million. Above that, "7,500,000,000 / 10,000,000,000"
    // squeezes the bar out of a default-width overlay, so four significant
    // digits with a unit: 億/兆 in Japanese, M/B/T in English. Unit and
    // digits are chosen after rounding: 9,999.6億 is 1.000兆, not 10,000億.
    private static string FormatAmount(long value)
    {
        if (value < 100_000_000) return value.ToString("N0");
        (double Unit, string Suffix, double Next)[] units = S.Ja
            ? [(1e8, "億", 1e4), (1e12, "兆", double.MaxValue)]
            : [(1e6, "M", 1e3), (1e9, "B", 1e3), (1e12, "T", double.MaxValue)];
        static int Decimals(double v) => Math.Clamp(3 - (int)Math.Floor(Math.Log10(v)), 0, 3);
        foreach (var (unit, suffix, next) in units)
        {
            double scaled = value / unit;
            double rounded = Math.Round(scaled, Decimals(scaled));
            if (rounded >= next) continue;
            return rounded.ToString("N" + Decimals(rounded)) + suffix;
        }
        return value.ToString("N0");
    }

    internal void SetText(string text, RadarEntityKind? kind = null)
    {
        var t = string.IsNullOrWhiteSpace(text) ? "—" : text;
        if (t == lastText && kind == lastKind)
        {
            return;
        }
        lastText = t;
        lastKind = kind;
        TargetText.Text = t;
        TargetText.FontWeight = kind is RadarEntityKind.Elite or RadarEntityKind.Boss ? FontWeights.Bold : FontWeights.Normal;
        TargetText.Foreground = Argb(NameArgb(kind, RadarSettings.Instance.TargetOverlayTextArgb));
        TargetText.Opacity = string.IsNullOrWhiteSpace(text) ? 0.45 : 1.0;
        FitToContents();
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
