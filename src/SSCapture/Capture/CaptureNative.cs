using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SSCapture.Capture;

internal static class CaptureNative
{
    internal const int WsExToolWindow = 0x00000080;
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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
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
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
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

    internal static Keys Modifiers(bool queuedKeyState = false)
    {
        bool Down(int key) => ((queuedKeyState ? GetKeyState(key) : GetAsyncKeyState(key)) & 0x8000) != 0;
        return (Down(0x10) ? Keys.Shift : Keys.None) | (Down(0x11) ? Keys.Control : Keys.None) | (Down(0x12) ? Keys.Alt : Keys.None);
    }

    internal static bool IsModifierTransition(int message, nint key) =>
        (message is 0x0100 or 0x0101 or 0x0104 or 0x0105) && (key.ToInt32() is 0x10 or 0x11 or 0x12 or >= 0xa0 and <= 0xa5);

    internal static bool IsAltTransition(int message, nint key) =>
        (message is 0x0100 or 0x0101 or 0x0104 or 0x0105) && (key.ToInt32() is 0x12 or 0xa4 or 0xa5);

    internal static Size DragThreshold(int dpi) => new(
        Math.Max(2, GetSystemMetricsForDpi(68 /* SM_CXDRAG */, (uint)Math.Max(96, dpi))),
        Math.Max(2, GetSystemMetricsForDpi(69 /* SM_CYDRAG */, (uint)Math.Max(96, dpi))));

    internal static DesktopSnapshot TakeAfterOverlaysClosed(bool includeCursor = false)
    {
        // Drain compositor work after hiding/disposing our HWNDs before reading current pixels.
        SynchronizeDesktop();
        return DesktopSnapshot.Take(includeCursor);
    }

    internal static void DisableTransitions(Form form)
    {
        int disabled = 1;
        int result = DwmSetWindowAttribute(form.Handle, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref disabled, sizeof(int));
        if (result < 0) throw new IOException("撮影前の画面アニメーションを抑止できません。", Marshal.GetExceptionForHR(result));
    }

    internal static void SynchronizeDesktop()
    {
        int result = DwmFlush();
        if (result < 0) throw new IOException("撮影前の画面描画を同期できません。", Marshal.GetExceptionForHR(result));
    }

    internal static bool OwnsForegroundWindow()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out uint processId);
        return processId == Environment.ProcessId;
    }

    internal static bool ForegroundTransition => GetForegroundWindow() == 0;

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
            long style = GetWindowLongPtr(window, -20 /* GWL_EXSTYLE */).ToInt64();
            // 入力透過の補助画面や完全透明な画面の矩形で、背後の撮影対象を遮らない。
            if ((style & 0x20 /* WS_EX_TRANSPARENT */) != 0) return true;
            if ((style & 0x80000 /* WS_EX_LAYERED */) != 0 && GetLayeredWindowAttributes(window, out _, out byte alpha, out uint flags)
                && (flags & 2 /* LWA_ALPHA */) != 0 && alpha == 0) return true;
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
