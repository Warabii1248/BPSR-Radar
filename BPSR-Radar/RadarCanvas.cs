using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Vector = System.Windows.Vector;

namespace BpsrRadar;

// Draws the radar map. Port of TacticalMapWindow's DrawMap/DrawEnemy/DrawLabel.
internal sealed class RadarCanvas : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    // Elites and bosses are what the player is picking out of a pull, so
    // their names carry the weight. The marker already differs in shape and
    // size; at eleven pixels on a transparent overlay that is easy to lose.
    private static readonly Typeface StrongLabelTypeface = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(46, 64, 140, 143)), 1);
    private static readonly Pen RingPen = new(new SolidColorBrush(Color.FromArgb(77, 128, 209, 209)), 1);
    private static readonly Brush RingLabelBrush = new SolidColorBrush(Color.FromArgb(120, 128, 209, 209));
    private static readonly Brush WaitingBrush = new SolidColorBrush(Color.FromArgb(217, 204, 230, 230));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(230, 245, 245));
    private static readonly Brush PartyBack = new SolidColorBrush(Color.FromRgb(3, 20, 23));
    private static readonly Brush PartyFront = new SolidColorBrush(Color.FromRgb(51, 217, 242));
    private static readonly Brush PlayerBack = new SolidColorBrush(Color.FromRgb(26, 16, 46));
    private static readonly Brush PlayerFront = new SolidColorBrush(Color.FromRgb(182, 150, 255));
    private static readonly Brush SelfBack = new SolidColorBrush(Color.FromRgb(5, 20, 13));
    private static readonly Brush SelfFront = new SolidColorBrush(Color.FromRgb(191, 255, 204));
    private static readonly Brush SelfCross = Brushes.White;
    private static readonly Brush EnemyBrush = new SolidColorBrush(Color.FromRgb(242, 56, 46));
    private static readonly Brush EliteBack = new SolidColorBrush(Color.FromRgb(64, 5, 3));
    private static readonly Brush EliteFront = new SolidColorBrush(Color.FromRgb(255, 71, 41));
    private static readonly Brush BossBack = new SolidColorBrush(Color.FromRgb(64, 20, 3));
    private static readonly Brush BossFront = new SolidColorBrush(Color.FromRgb(255, 148, 31));
    private static readonly Brush UnknownBrush = new SolidColorBrush(Color.FromRgb(179, 64, 51));
    private static readonly Brush GimmickBrush = new SolidColorBrush(Color.FromRgb(206, 184, 84));
    private static readonly Pen LockPen = new(new SolidColorBrush(Color.FromRgb(255, 235, 90)), 2);
    private static readonly SolidColorBrush MapBack = new(Color.FromRgb(6, 14, 17));

    public RadarSnapshot? Snapshot { get; set; }
    public long LockTargetUuid { get; set; }

    static RadarCanvas()
    {
        GridPen.Freeze();
        RingPen.Freeze();
        RingLabelBrush.Freeze();
        WaitingBrush.Freeze();
        LabelBrush.Freeze();
        PartyBack.Freeze();
        PartyFront.Freeze();
        PlayerBack.Freeze();
        PlayerFront.Freeze();
        SelfBack.Freeze();
        SelfFront.Freeze();
        EnemyBrush.Freeze();
        EliteBack.Freeze();
        EliteFront.Freeze();
        BossBack.Freeze();
        BossFront.Freeze();
        UnknownBrush.Freeze();
        GimmickBrush.Freeze();
        LockPen.Freeze();
        MapBack.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var settings = RadarSettings.Instance;
        double side = Math.Max(60, Math.Min(ActualWidth, ActualHeight));
        var canvasMin = new Point((ActualWidth - side) * 0.5, (ActualHeight - side) * 0.5);
        var canvasSize = new Size(side, side);
        var canvasRect = new Rect(canvasMin, canvasSize);

        var bgBrush = new SolidColorBrush(MapBack.Color) { Opacity = settings.BackgroundOpacity };
        dc.DrawRoundedRectangle(bgBrush, null, canvasRect, 6, 6);

        dc.PushClip(new RectangleGeometry(canvasRect));
        for (int i = 1; i < 4; i++)
        {
            double ratio = i / 4.0;
            double x = canvasMin.X + side * ratio;
            double y = canvasMin.Y + side * ratio;
            dc.DrawLine(GridPen, new Point(x, canvasMin.Y), new Point(x, canvasMin.Y + side));
            dc.DrawLine(GridPen, new Point(canvasMin.X, y), new Point(canvasMin.X + side, y));
        }

        var center = new Point(canvasMin.X + side * 0.5, canvasMin.Y + side * 0.5);
        double mapRadiusPixels = Math.Max(20, side * 0.5 - 10);
        var snapshot = Snapshot;
        if (snapshot?.Self == null)
        {
            DrawText(dc, S.T("Waiting for position…", "位置を待機中…"), center, WaitingBrush, centered: true);
            dc.Pop();
            return;
        }

        var view = CalculateView(snapshot, settings);
        double pixelsPerUnit = mapRadiusPixels / Math.Max(1.0, view.Range);
        var selfPoint = WorldToScreen(snapshot.Self.Position, view.Origin, center, pixelsPerUnit);
        DrawDistanceRings(dc, selfPoint, mapRadiusPixels, view.Range, settings);

        foreach (var enemy in snapshot.Enemies)
        {
            var point = WorldToScreen(enemy.Position, view.Origin, center, pixelsPerUnit);
            if (!InsideMap(point, center, mapRadiusPixels))
            {
                continue;
            }

            DrawEnemy(dc, point, enemy);
            if (enemy.Uuid == LockTargetUuid && LockTargetUuid != 0)
            {
                dc.DrawEllipse(null, LockPen, point, 11, 11);
            }
            if (settings.LabelMode == RadarLabelMode.All ||
                (settings.LabelMode == RadarLabelMode.PartyAndBoss && enemy.Kind == RadarEntityKind.Boss))
            {
                DrawLabel(dc, point, enemy.Name, enemy.Opacity, IsStrong(enemy.Kind));
            }
        }

        foreach (var player in snapshot.Players)
        {
            DrawMember(dc, player, PlayerBack, PlayerFront, view.Origin, center, pixelsPerUnit, mapRadiusPixels, settings);
        }

        foreach (var member in snapshot.Party)
        {
            DrawMember(dc, member, PartyBack, PartyFront, view.Origin, center, pixelsPerUnit, mapRadiusPixels, settings);
        }

        if (settings.ShowSelf)
        {
            if (InsideMap(selfPoint, center, mapRadiusPixels))
            {
                if (settings.ShowSelfDirection && snapshot.Self.HeadingRadians.HasValue)
                {
                    double heading = snapshot.Self.HeadingRadians.Value;
                    var forward = new Vector(Math.Sin(heading), -Math.Cos(heading));
                    var right = new Vector(-forward.Y, forward.X);
                    var tip = selfPoint + forward * 17;
                    var rear = selfPoint - forward * 5;
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(tip, true, true);
                        ctx.LineTo(rear + right * 5.5, true, false);
                        ctx.LineTo(rear - right * 5.5, true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(WithOpacity(SelfFront, snapshot.Self.Opacity), null, geo);
                }

                dc.DrawEllipse(WithOpacity(SelfBack, snapshot.Self.Opacity), null, selfPoint, 8, 8);
                dc.DrawEllipse(WithOpacity(SelfFront, snapshot.Self.Opacity), null, selfPoint, 5, 5);
                var crossPen = new Pen(WithOpacity(SelfCross, snapshot.Self.Opacity), 1.5);
                dc.DrawLine(crossPen, selfPoint + new Vector(-7, 0), selfPoint + new Vector(7, 0));
                dc.DrawLine(crossPen, selfPoint + new Vector(0, -7), selfPoint + new Vector(0, 7));
            }
        }

        dc.Pop();
    }

    private static (Vector3 Origin, float Range) CalculateView(RadarSnapshot snapshot, RadarSettings settings)
    {
        float worldUnitsPerMeter = GetWorldUnitsPerMeter(settings);
        float miniMapRange = MathF.Max(1f, settings.BaseDisplayRangeMeters * worldUnitsPerMeter / settings.Zoom);
        if (settings.ViewMode == RadarViewMode.MiniMap || snapshot.Self == null)
        {
            return (snapshot.Self?.Position ?? Vector3.Zero, miniMapRange);
        }

        Vector3 self = snapshot.Self.Position;
        float overviewMaxRange = settings.OverviewMaxRangeMeters * worldUnitsPerMeter;
        var points = new List<Vector3> { self };
        points.AddRange(snapshot.Enemies.Where(x => x.DistanceFromSelf <= overviewMaxRange).Select(x => x.Position));
        points.AddRange(snapshot.Party.Where(x => x.DistanceFromSelf <= overviewMaxRange).Select(x => x.Position));
        points.AddRange(snapshot.Players.Where(x => x.DistanceFromSelf <= overviewMaxRange).Select(x => x.Position));
        if (points.Count == 1)
        {
            return (self, miniMapRange);
        }

        float minX = points.Min(x => x.X);
        float maxX = points.Max(x => x.X);
        float minZ = points.Min(x => x.Z);
        float maxZ = points.Max(x => x.Z);
        var origin = new Vector3((minX + maxX) * 0.5f, self.Y, (minZ + maxZ) * 0.5f);
        float fitRange = MathF.Max(maxX - minX, maxZ - minZ) * 0.55f;
        fitRange = Math.Clamp(fitRange, 1f, overviewMaxRange);
        return (origin, fitRange / settings.Zoom);
    }

    private void DrawMember(DrawingContext dc, RadarEntitySnapshot member, Brush back, Brush front,
        Vector3 origin, Point center, double pixelsPerUnit, double mapRadiusPixels, RadarSettings settings)
    {
        var point = WorldToScreen(member.Position, origin, center, pixelsPerUnit);
        bool clamped = false;
        if (!InsideMap(point, center, mapRadiusPixels))
        {
            var direction = new Vector((float)(point.X - center.X), (float)(point.Y - center.Y));
            if (direction.LengthSquared <= double.Epsilon)
            {
                return;
            }
            direction.Normalize();
            point = center + direction * (mapRadiusPixels - 8);
            clamped = true;
        }

        dc.DrawEllipse(WithOpacity(back, member.Opacity), null, point, 8, 8);
        dc.DrawEllipse(WithOpacity(front, member.Opacity), null, point, 5.5, 5.5);
        if (member.Uuid == LockTargetUuid && LockTargetUuid != 0)
        {
            dc.DrawEllipse(null, LockPen, point, 12, 12);
        }
        if (settings.LabelMode != RadarLabelMode.None)
        {
            string label = member.Name;
            if (clamped)
            {
                string distance = FormatDistance(member.DistanceFromSelf, settings);
                label = string.IsNullOrWhiteSpace(label) ? distance : $"{label} {distance}";
            }
            DrawLabel(dc, point, label, member.Opacity);
        }
    }

    private void DrawDistanceRings(DrawingContext dc, Point center, double mapRadiusPixels, float viewRange, RadarSettings settings)
    {
        if (!settings.ShowDistanceRings)
        {
            return;
        }

        float worldUnitsPerMeter = GetWorldUnitsPerMeter(settings);
        DrawDistanceRing(dc, center, mapRadiusPixels, viewRange, 10f * worldUnitsPerMeter, 10f, settings.ShowDistanceRing10m, settings.ShowDistanceRingLabels);
        DrawDistanceRing(dc, center, mapRadiusPixels, viewRange, 20f * worldUnitsPerMeter, 20f, settings.ShowDistanceRing20m, settings.ShowDistanceRingLabels);
        DrawDistanceRing(dc, center, mapRadiusPixels, viewRange, 30f * worldUnitsPerMeter, 30f, settings.ShowDistanceRing30m, settings.ShowDistanceRingLabels);
    }

    private void DrawDistanceRing(DrawingContext dc, Point center, double mapRadiusPixels, float viewRange, float worldDistance, float configuredDistance, bool enabled, bool showLabel)
    {
        double radius = worldDistance / viewRange * mapRadiusPixels;
        if (!enabled || radius > mapRadiusPixels)
        {
            return;
        }

        dc.DrawEllipse(null, RingPen, center, radius, radius);
        if (showLabel)
        {
            string unit = RadarSettings.Instance.DistanceCalibrationComplete ? "m" : "u";
            DrawText(dc, $"{configuredDistance:0}{unit}", new Point(center.X + radius + 3, center.Y - 8), RingLabelBrush);
        }
    }

    private static void DrawEnemy(DrawingContext dc, Point point, RadarEntitySnapshot entity)
    {
        switch (entity.Kind)
        {
            case RadarEntityKind.Gimmick:
                var gimmickPen = new Pen(WithOpacity(GimmickBrush, entity.Opacity), 1.5);
                dc.DrawLine(gimmickPen, new Point(point.X, point.Y - 5), new Point(point.X + 5, point.Y));
                dc.DrawLine(gimmickPen, new Point(point.X + 5, point.Y), new Point(point.X, point.Y + 5));
                dc.DrawLine(gimmickPen, new Point(point.X, point.Y + 5), new Point(point.X - 5, point.Y));
                dc.DrawLine(gimmickPen, new Point(point.X - 5, point.Y), new Point(point.X, point.Y - 5));
                break;
            case RadarEntityKind.Monster:
                dc.DrawEllipse(WithOpacity(EnemyBrush, entity.Opacity), null, point, 3.5, 3.5);
                break;
            case RadarEntityKind.Elite:
                dc.DrawRectangle(WithOpacity(EliteBack, entity.Opacity), null, new Rect(point.X - 5, point.Y - 5, 10, 10));
                dc.DrawRectangle(WithOpacity(EliteFront, entity.Opacity), null, new Rect(point.X - 3.5, point.Y - 3.5, 7, 7));
                break;
            case RadarEntityKind.Boss:
                dc.DrawRectangle(WithOpacity(BossBack, entity.Opacity), null, new Rect(point.X - 7, point.Y - 7, 14, 14));
                dc.DrawRectangle(WithOpacity(BossFront, entity.Opacity), null, new Rect(point.X - 5, point.Y - 5, 10, 10));
                break;
            default:
                dc.DrawEllipse(WithOpacity(UnknownBrush, entity.Opacity), null, point, 4, 4);
                break;
        }
    }

    internal static bool IsStrong(RadarEntityKind kind) =>
        kind is RadarEntityKind.Elite or RadarEntityKind.Boss;

    private void DrawLabel(DrawingContext dc, Point point, string label, float opacity, bool strong = false)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            DrawText(dc, label, new Point(point.X + 9, point.Y - 8), WithOpacity(LabelBrush, opacity), strong: strong);
        }
    }

    private void DrawText(DrawingContext dc, string text, Point point, Brush brush, bool centered = false,
        bool strong = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            strong ? StrongLabelTypeface : LabelTypeface, 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (centered)
        {
            point = new Point(point.X - ft.Width * 0.5, point.Y - ft.Height * 0.5);
        }
        dc.DrawText(ft, point);
    }

    private static Point WorldToScreen(Vector3 world, Vector3 origin, Point center, double pixelsPerUnit)
    {
        return new Point(
            center.X + (world.X - origin.X) * pixelsPerUnit,
            center.Y - (world.Z - origin.Z) * pixelsPerUnit);
    }

    private static bool InsideMap(Point point, Point center, double radius)
    {
        return Math.Abs(point.X - center.X) <= radius && Math.Abs(point.Y - center.Y) <= radius;
    }

    private static Brush WithOpacity(Brush brush, float opacity)
    {
        var clone = brush.Clone();
        clone.Opacity = Math.Clamp(opacity, 0f, 1f);
        return clone;
    }

    private static float GetWorldUnitsPerMeter(RadarSettings settings)
    {
        return settings.DistanceCalibrationComplete ? MathF.Max(0.0001f, settings.WorldUnitsPerMeter) : 1f;
    }

    private static string FormatDistance(float worldDistance, RadarSettings settings)
    {
        if (!settings.DistanceCalibrationComplete)
        {
            return $"{worldDistance:0}u";
        }
        return $"{worldDistance / GetWorldUnitsPerMeter(settings):0.0}m";
    }
}
