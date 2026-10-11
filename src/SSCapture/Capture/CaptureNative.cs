using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SSCapture.Capture;

internal static class CaptureNative
{
    internal const int WmDisplayChange = 0x007e;
    internal const int WmDpiChanged = 0x02e0;
    internal const int WmSessionChange = 0x02b1;
    internal const int WmActivateApp = 0x001c;
    internal const int WmSysCommand = 0x0112;
    internal const int ScKeyMenu = 0xf100;
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint data);
    private delegate bool WindowCallback(nint window, nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSUnRegisterSessionNotification(nint window);

    internal static Point CursorPosition()
    {
        if (!GetPhysicalCursorPos(out NativePoint point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Point(point.X, point.Y);
    }

    internal static bool AltPressed(bool queuedKeyState = false) =>
        ((queuedKeyState ? GetKeyState(0x12 /* VK_MENU */) : GetAsyncKeyState(0x12)) & 0x8000) != 0;

    internal static bool IsAltTransition(int message, nint key) =>
        (message is 0x0100 or 0x0101 or 0x0104 or 0x0105) && (key.ToInt32() is 0x12 or 0xa4 or 0xa5);

    internal static Size DragThreshold(int dpi) => new(
        Math.Max(2, GetSystemMetricsForDpi(68 /* SM_CXDRAG */, (uint)Math.Max(96, dpi))),
        Math.Max(2, GetSystemMetricsForDpi(69 /* SM_CYDRAG */, (uint)Math.Max(96, dpi))));

    internal static DesktopSnapshot TakeAfterOverlaysClosed(bool includeCursor = false)
    {
        // Drain compositor work after hiding/disposing our HWNDs before reading current pixels.
        DwmFlush();
        return DesktopSnapshot.Take(includeCursor);
    }

    internal static bool OwnsForegroundWindow()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out uint processId);
        return processId == Environment.ProcessId;
    }

    internal static IReadOnlyList<Rectangle> MonitorBounds()
    {
        List<Rectangle> monitors = [];
        MonitorCallback callback = (nint monitor, nint dc, ref NativeRect bounds, nint data) =>
        {
            Rectangle rectangle = bounds.ToRectangle();
            if (rectangle.Width > 0 && rectangle.Height > 0) monitors.Add(rectangle);
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (monitors.Count == 0) throw new InvalidOperationException("撮影可能なディスプレイがありません。");
        return monitors;
    }

    internal static IReadOnlyList<Rectangle> WindowBounds(Rectangle desktop)
    {
        List<Rectangle> windows = [];
        nint shell = GetShellWindow();
        EnumWindows((window, data) =>
        {
            if (window == shell || !IsWindowVisible(window) || IsIconic(window)) return true;
            if (DwmGetWindowAttributeInt(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(window, 9, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) != 0
                && !GetWindowRect(window, out bounds)) return true;
            Rectangle clipped = CaptureGeometry.Clip(bounds.ToRectangle(), desktop);
            if (CaptureGeometry.IsUsable(clipped)) windows.Add(clipped);
            return true;
        }, 0);
        return windows;
    }
}
