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
}
