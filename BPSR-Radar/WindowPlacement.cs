using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BpsrRadar;

// Keeps saved window positions safe across display connect/disconnect.
internal static class WindowPlacement
{
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // Toggles full click-through (mouse passes to whatever is underneath).
    public static void SetClickThrough(Window w, bool enabled)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        int style = GetWindowLong(hwnd, GwlExStyle);
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    private static (double x, double y) DpiScale(Window w)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        return (dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX, dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY);
    }

    // True if the DIP rect intersects any currently connected monitor.
    public static bool IntersectsMonitor(Window w, double left, double top, double width, double height)
    {
        var (sx, sy) = DpiScale(w);
        var r = new RECT
        {
            Left = (int)(left * sx),
            Top = (int)(top * sy),
            Right = (int)((left + Math.Max(1, width)) * sx),
            Bottom = (int)((top + Math.Max(1, height)) * sy),
        };
        return MonitorFromRect(ref r, MonitorDefaultToNull) != IntPtr.Zero;
    }

    // If the window is entirely off all monitors, move it onto the nearest
    // monitor's work area.
    public static void EnsureOnScreen(Window w)
    {
        if (double.IsNaN(w.Left) || double.IsNaN(w.Top))
        {
            return;
        }
        if (IntersectsMonitor(w, w.Left, w.Top, w.Width, w.Height))
        {
            return;
        }

        var (sx, sy) = DpiScale(w);
        var center = new POINT
        {
            X = (int)((w.Left + w.Width * 0.5) * sx),
            Y = (int)((w.Top + w.Height * 0.5) * sy),
        };
        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        w.Left = info.rcWork.Left / sx + 24;
        w.Top = info.rcWork.Top / sy + 24;
    }

    // Work area (DIPs) of the monitor nearest to the DIP rect; the primary
    // monitor's if the query fails.
    public static Rect WorkAreaNear(Window w, Rect rect)
    {
        var (sx, sy) = DpiScale(w);
        var r = new RECT
        {
            Left = (int)(rect.Left * sx),
            Top = (int)(rect.Top * sy),
            Right = (int)(rect.Right * sx) + 1,
            Bottom = (int)(rect.Bottom * sy) + 1,
        };
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromRect(ref r, MonitorDefaultToNearest), ref info))
        {
            return SystemParameters.WorkArea;
        }
        return new Rect(info.rcWork.Left / sx, info.rcWork.Top / sy,
            (info.rcWork.Right - info.rcWork.Left) / sx, (info.rcWork.Bottom - info.rcWork.Top) / sy);
    }

    // A growing list may use the work area up to its full height. Its top is
    // computed from anchorTop, where the player put it: moved up only as far
    // as the footer needs to stay above the taskbar, and back down when the
    // list shrinks. moveTop=false (during a drag) only caps the height, so
    // the window can be dragged across a monitor boundary.
    public static double FitOverlayHeight(Window w, double desiredHeight, double anchorTop = double.NaN,
        bool moveTop = true)
    {
        var (sx, sy) = DpiScale(w);
        double left = double.IsFinite(w.Left) ? w.Left : 0;
        double top = double.IsFinite(anchorTop) ? anchorTop : double.IsFinite(w.Top) ? w.Top : 0;
        var monitor = MonitorFromPoint(new POINT
        {
            X = (int)((left + w.Width / 2) * sx),
            Y = (int)((top + 8) * sy),
        }, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return desiredHeight;
        double workTop = info.rcWork.Top / sy;
        double workBottom = info.rcWork.Bottom / sy;
        double height = Math.Clamp(desiredHeight, 1, Math.Max(1, workBottom - workTop));
        if (moveTop && double.IsFinite(w.Top))
        {
            double fittedTop = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - height));
            if (Math.Abs(w.Top - fittedTop) > 0.1) w.Top = fittedTop;
        }
        return height;
    }
}
