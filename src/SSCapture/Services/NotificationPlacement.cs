using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal sealed record NotificationMonitor(string DeviceName, Rectangle Bounds, Rectangle WorkArea, bool IsPrimary);

internal static class NotificationPlacement
{
    internal static NotificationMonitor Resolve(NotificationDisplay target, Rectangle? captureBounds, IEnumerable<NotificationMonitor> monitors)
    {
        if (!Enum.IsDefined(target)) throw new ArgumentException("通知先が正しくありません。");
        var available = monitors.Where(m => m.Bounds.Width > 0 && m.Bounds.Height > 0 && m.WorkArea.Width > 0 && m.WorkArea.Height > 0)
            .OrderByDescending(m => m.IsPrimary).ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (available.Length == 0) throw new InvalidOperationException("通知を表示できる画面がありません。");
        NotificationMonitor primary = available[0];
        if (target >= NotificationDisplay.Display1)
        {
            string device = @"\\.\DISPLAY" + ((int)target - (int)NotificationDisplay.Display1 + 1);
            return available.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase)) ?? primary;
        }
        if (target != NotificationDisplay.Captured || captureBounds is not { Width: > 0, Height: > 0 } capture) return primary;
        // Stable ties prefer the main display; use physical bounds, never the current cursor.
        return available.OrderByDescending(m => IntersectionArea(m.Bounds, capture)).First();
    }

    internal static (Rectangle WorkArea, int Dpi) Current(NotificationDisplay target, Rectangle? captureBounds)
    {
        var display = Resolve(target, captureBounds, Screen.AllScreens.Select(s => new NotificationMonitor(s.DeviceName, s.Bounds, s.WorkingArea, s.Primary)));
        var point = new Point(display.Bounds.Left + display.Bounds.Width / 2, display.Bounds.Top + display.Bounds.Height / 2);
        nint monitor = MonitorFromPoint(point, 2);
        int error = GetDpiForMonitor(monitor, 0, out uint dpi, out _);
        if (error != 0) Marshal.ThrowExceptionForHR(error);
        return (display.WorkArea, checked((int)dpi));
    }

    private static long IntersectionArea(Rectangle left, Rectangle right)
    {
        long width = Math.Max(0, Math.Min((long)left.X + left.Width, (long)right.X + right.Width) - Math.Max(left.X, right.X));
        long height = Math.Max(0, Math.Min((long)left.Y + left.Height, (long)right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return width * height;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}
