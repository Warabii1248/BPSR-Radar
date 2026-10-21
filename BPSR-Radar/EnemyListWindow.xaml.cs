using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace BpsrRadar;

public partial class EnemyListWindow : Window
{
    private readonly DispatcherTimer settingsSaveTimer;
    private readonly Dictionary<long, EnemyRow> rows = new();
    private RadarSnapshot? lastSnapshot;
    private long targetUuid;
    private bool placementReady;
    private bool fittingHeight;
    // Where the player put the top edge. Auto-fit may lift the window above
    // it to clear the taskbar; it returns there as the list shrinks, and it is
    // what gets saved.
    private double anchorTop = double.NaN;
    private (double Width, double FontSize, int Count, Visibility Empty, string Text, string Header, Visibility Overflow)? fittedContents;

    public EnemyListWindow()
    {
        InitializeComponent();

        var s = RadarSettings.Instance;
        Width = ValidSize(s.EnemyListWidth, 300, MinWidth);
        bool restorePosition = s.EnemyListHasPosition &&
            double.IsFinite(s.EnemyListLeft) && double.IsFinite(s.EnemyListTop);
        if (restorePosition)
        {
            Left = s.EnemyListLeft;
            Top = s.EnemyListTop;
            anchorTop = s.EnemyListTop;
        }

        settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        settingsSaveTimer.Tick += (_, _) =>
        {
            settingsSaveTimer.Stop();
            RadarSettings.Save();
        };
        Loaded += (_, _) =>
        {
            if (placementReady) return;
            if (!restorePosition && Owner != null &&
                double.IsFinite(Owner.Left) && double.IsFinite(Owner.Top))
            {
                Left = Owner.Left + Owner.ActualWidth + 8;
                Top = Owner.Top;
            }
            anchorTop = Top;
            EnsureVisible();
            FitHeightToContents(force: true);
            placementReady = true;
            SavePlacement();
        };
        LocationChanged += (_, _) =>
        {
            // A move this window did not make itself (drag, EnsureOnScreen)
            // is the player's new position. During it only the height is
            // capped; the top is fitted once the drag ends.
            if (fittingHeight) return;
            anchorTop = Top;
            // A keyboard or snap move (button up) has no drag end to wait for.
            bool dragging = Mouse.LeftButton == MouseButtonState.Pressed;
            FitHeightToContents(force: true, moveTop: !dragging);
            SavePlacement();
        };
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) FitHeightToContents();
            SavePlacement();
        };
        ApplySettings();
    }

    private static double ValidSize(double value, double fallback, double minimum) =>
        double.IsFinite(value) && value > 0 ? Math.Max(minimum, value) : fallback;

    private void SavePlacement()
    {
        if (!IsLoaded || !placementReady) return;
        var s = RadarSettings.Instance;
        s.EnemyListLeft = Left;
        s.EnemyListTop = double.IsFinite(anchorTop) ? anchorTop : Top;
        s.EnemyListWidth = Width;
        s.EnemyListHeight = Height;
        s.EnemyListHasPosition = true;
        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    public void EnsureVisible()
    {
        WindowPlacement.EnsureOnScreen(this);
        FitHeightToContents(force: true);
    }

    // Measure the same WPF content that is displayed, including wrapping in
    // the empty state. Height follows rows/font/width instead of a saved size.
    private void FitHeightToContents(bool force = false, bool moveTop = true)
    {
        if (fittingHeight) return;
        var contents = (Width, FontSize, EnemyRows.Children.Count, EmptyText.Visibility, EmptyText.Text, HeaderDetails.Text, OverflowText.Visibility);
        if (!force && fittedContents == contents) return;
        fittingHeight = true;
        try
        {
            // Child visibility and collection changes invalidate ancestors on
            // the layout queue. Flush that queue before reading desired size.
            Frame.UpdateLayout();
            Frame.Measure(new Size(Width, double.PositiveInfinity));
            double desiredHeight = Math.Max(1, Math.Ceiling(Frame.DesiredSize.Height));
            double height = WindowPlacement.FitOverlayHeight(this, desiredHeight, anchorTop, moveTop);
            if (Math.Abs(Height - height) > 0.1) Height = height;
            fittedContents = contents;
        }
        finally { fittingHeight = false; }
    }

    public void SyncClickThrough() =>
        WindowPlacement.SetClickThrough(this, RadarSettings.Instance.ClickThrough);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SyncClickThrough();
        FitHeightToContents(force: true);
    }

    public void ApplySettings()
    {
        var s = RadarSettings.Instance;
        Topmost = s.EnemyListTopMost;
        double opacity = double.IsFinite(s.EnemyListOpacity)
            ? Math.Clamp(s.EnemyListOpacity, 0.15, 1) : 0.9;
        byte alpha = (byte)Math.Round(opacity * 255);
        Frame.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x0A, 0x14, 0x18));
        Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(alpha, 0x3F, 0xA7, 0xAD));
        // Share the target name's RGB while keeping enemy-list text fully opaque.
        uint textColor = s.TargetOverlayTextArgb;
        Foreground = new SolidColorBrush(Color.FromRgb(
            (byte)(textColor >> 16), (byte)(textColor >> 8), (byte)textColor));
        FontSize = double.IsFinite(s.EnemyListFontSize)
            ? Math.Clamp(s.EnemyListFontSize, 8, 48) : 14;
        HeaderDetails.FontSize = FontSize * 0.85;
        OverflowText.FontSize = FontSize * 0.85;
        foreach (var row in rows.Values) row.ApplyFontSize(FontSize);
        if (lastSnapshot != null)
            SetSnapshot(lastSnapshot, targetUuid);
        else
            UpdateLabels(0, false);
        FitHeightToContents();
    }

    internal void SetSnapshot(RadarSnapshot snapshot, long targetUuid)
    {
        lastSnapshot = snapshot;
        this.targetUuid = targetUuid;
        var s = RadarSettings.Instance;
        var nearby = snapshot.Self == null ? Array.Empty<RadarEntitySnapshot>() : snapshot.NearbyEnemies;
        int maxRows = s.EffectiveEnemyListMaxRows;
        var enemies = nearby.Length > maxRows ? nearby[..maxRows] : nearby;
        var currentIds = new HashSet<long>(enemies.Select(enemy => enemy.Uuid));
        foreach (long uuid in rows.Keys.Where(uuid => !currentIds.Contains(uuid)).ToArray())
        {
            EnemyRows.Children.Remove(rows[uuid].Root);
            rows.Remove(uuid);
        }

        float units = s.DistanceCalibrationComplete && float.IsFinite(s.WorldUnitsPerMeter) &&
            s.WorldUnitsPerMeter > 0 ? s.WorldUnitsPerMeter : 1f;
        for (int index = 0; index < enemies.Length; index++)
        {
            var enemy = enemies[index];
            if (!rows.TryGetValue(enemy.Uuid, out var row))
            {
                row = new EnemyRow();
                row.ApplyFontSize(FontSize);
                rows.Add(enemy.Uuid, row);
            }
            int oldIndex = EnemyRows.Children.IndexOf(row.Root);
            if (oldIndex != index)
            {
                if (oldIndex >= 0) EnemyRows.Children.RemoveAt(oldIndex);
                EnemyRows.Children.Insert(index, row.Root);
            }
            row.Update(enemy, enemy.DistanceFromSelf / units);
            row.SetTarget(targetUuid != 0 && enemy.Uuid == targetUuid);
        }
        UpdateLabels(nearby.Length, snapshot.Self != null, nearby.Length - enemies.Length);
        FitHeightToContents();
    }

    internal void SetTarget(long uuid)
    {
        if (uuid == targetUuid) return;
        targetUuid = uuid;
        foreach (var (rowUuid, row) in rows)
            row.SetTarget(uuid != 0 && rowUuid == uuid);
    }

    // count: every enemy in the radius; hidden: those past the row limit.
    private void UpdateLabels(int count, bool hasPosition, int hidden = 0)
    {
        float radius = RadarSettings.Instance.EffectiveEnemyListRadiusMeters;
        SetText(HeaderDetails, S.T($"Radius {radius:0.#} m · {count} enemies", $"半径 {radius:0.#} m · {count}体"));
        SetText(OverflowText, S.T($"+{hidden} more", $"+{hidden}体"));
        OverflowText.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetText(EmptyText, hasPosition
            ? S.T("No enemies in range", "範囲内に敵はいません")
            : S.T("Waiting for your position", "自分の位置を取得中"));
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnemyScroll.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch (InvalidOperationException) { }
        // DragMove returns when the button is released.
        FitHeightToContents(force: true);
        SavePlacement();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        FitHeightToContents();
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        settingsSaveTimer.Stop();
        RadarSettings.Save();
        base.OnClosed(e);
    }

    private sealed class EnemyRow
    {
        private static readonly Brush LockBrush = FrozenBrush(0xF1, 0xC7, 0x5B);
        private static readonly Brush HpBrush = FrozenBrush(0xD8, 0x5B, 0x64);
        private static readonly Brush HpTrackBrush = FrozenBrush(0x40, 0x4D, 0x52);
        // The target overlay's name colours, fully opaque like all list text.
        private static readonly Brush EliteBrush = KindBrush(RadarEntityKind.Elite);
        private static readonly Brush BossBrush = KindBrush(RadarEntityKind.Boss);
        private static readonly Geometry ReticleGeometry = CreateReticleGeometry();
        private readonly TextBlock name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock distance = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        private readonly ShapePath reticle = new()
        {
            Data = ReticleGeometry,
            Width = 16,
            Height = 16,
            Stroke = LockBrush,
            StrokeThickness = 1.3,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden,
        };
        private readonly Border hpFill = new() { Background = HpBrush, CornerRadius = new CornerRadius(1.5) };
        private readonly ColumnDefinition hpFilledColumn = new();
        private readonly ColumnDefinition hpRemainingColumn = new();
        private bool isTarget;
        private double lastRatio = double.NaN;
        private bool? lastHasHp;
        private RadarEntityKind? lastKind;

        public Border Root { get; } = new()
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0, 4, 0),
        };

        public EnemyRow()
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(reticle);
            Grid.SetColumn(name, 1);
            content.Children.Add(name);
            Grid.SetColumn(distance, 2);
            content.Children.Add(distance);

            var bar = new Grid { Height = 3, Margin = new Thickness(0, 1, 0, 0) };
            bar.ColumnDefinitions.Add(hpFilledColumn);
            bar.ColumnDefinitions.Add(hpRemainingColumn);
            var track = new Border { Background = HpTrackBrush, CornerRadius = new CornerRadius(1.5) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            bar.Children.Add(hpFill);
            Grid.SetColumn(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            Grid.SetRow(bar, 1);
            content.Children.Add(bar);
            Root.Child = content;
        }

        public void ApplyFontSize(double fontSize)
        {
            name.FontSize = fontSize;
            distance.FontSize = fontSize * 0.85;
        }

        public void Update(RadarEntitySnapshot entity, float distanceMeters)
        {
            string displayName = string.IsNullOrWhiteSpace(entity.Name)
                ? S.T("Unknown enemy", "不明な敵") : entity.Name;
            SetText(name, displayName);
            if (entity.Kind != lastKind)
            {
                // Other names inherit the window's text colour.
                if (entity.Kind == RadarEntityKind.Elite) name.Foreground = EliteBrush;
                else if (entity.Kind == RadarEntityKind.Boss) name.Foreground = BossBrush;
                else name.ClearValue(TextBlock.ForegroundProperty);
                name.FontWeight = entity.Kind is RadarEntityKind.Elite or RadarEntityKind.Boss
                    ? FontWeights.Bold : FontWeights.Normal;
                lastKind = entity.Kind;
            }
            bool hasHp = entity.HasHp && entity.MaxHp > 0;
            SetText(distance, hasHp ? $"{distanceMeters:0.0} m" : $"HP — · {distanceMeters:0.0} m");
            double ratio = hasHp ? Math.Clamp((double)entity.Hp / entity.MaxHp, 0, 1) : 0;
            if (ratio != lastRatio)
            {
                hpFilledColumn.Width = new GridLength(ratio, GridUnitType.Star);
                hpRemainingColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
                lastRatio = ratio;
            }
            if (hasHp != lastHasHp)
            {
                hpFill.Visibility = hasHp ? Visibility.Visible : Visibility.Collapsed;
                lastHasHp = hasHp;
            }
            string tooltip = hasHp
                ? S.T($"{displayName}\nHP: {entity.Hp:N0}\nMax HP: {entity.MaxHp:N0}",
                    $"{displayName}\n現在HP: {entity.Hp:N0}\n最大HP: {entity.MaxHp:N0}")
                : S.T($"{displayName}\nHP: unknown\nMax HP: unknown", $"{displayName}\n現在HP: 不明\n最大HP: 不明");
            if (!Equals(Root.ToolTip, tooltip)) Root.ToolTip = tooltip;
        }

        public void SetTarget(bool value)
        {
            if (value == isTarget) return;
            isTarget = value;
            reticle.Visibility = value ? Visibility.Visible : Visibility.Hidden;
            Root.BorderBrush = value ? LockBrush : Brushes.Transparent;
        }

        private static Brush FrozenBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static Brush KindBrush(RadarEntityKind kind)
        {
            uint argb = TargetOverlayWindow.NameArgb(kind, 0xFF000000);
            return FrozenBrush((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        private static Geometry CreateReticleGeometry()
        {
            var geometry = Geometry.Parse("M8,3 A5,5 0 1 0 8,13 A5,5 0 1 0 8,3 M8,0 L8,4 M8,12 L8,16 M0,8 L4,8 M12,8 L16,8");
            geometry.Freeze();
            return geometry;
        }
    }
}
