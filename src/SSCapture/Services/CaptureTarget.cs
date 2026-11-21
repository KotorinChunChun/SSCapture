using SSCapture.Capture;

namespace SSCapture.Services;

internal static class CaptureTarget
{
    internal static string? Describe(Rectangle capture, IEnumerable<DisplayTarget> displays, IReadOnlyList<Rectangle>? capturedMonitors = null)
    {
        if (capture.Width <= 0 || capture.Height <= 0) return null;
        var ordered = InstantCapture.OrderDisplays(displays);
        if (capturedMonitors is { Count: > 0 })
        {
            var numbers = ordered.Select((display, index) => (display.Bounds, Number: index + 1))
                .Where(display => capturedMonitors.Contains(display.Bounds)).Select(display => display.Number).ToArray();
            return numbers.Length == capturedMonitors.Count ? DesktopName(numbers) + "全体"
                : $"デスクトップ（全体座標 {DescribeRange(capture, Point.Empty)}）";
        }
        for (int i = 0; i < ordered.Count; i++)
        {
            Rectangle monitor = ordered[i].Bounds;
            if (!monitor.Contains(capture)) continue;
            string desktop = DesktopName([i + 1]);
            if (monitor == capture) return desktop + "全体";
            return desktop + "の " + DescribeRange(capture, monitor.Location);
        }
        var touched = ordered.Select((display, index) => (display.Bounds, Number: index + 1))
            .Where(display => display.Bounds.IntersectsWith(capture)).ToArray();
        if (touched.Length == 0) return null;
        string desktops = DesktopName(touched.Select(d => d.Number));
        if (ordered.Count > 1 && capture == ordered.Select(d => d.Bounds).Aggregate(Rectangle.Union))
            return desktops + "全体";
        string relation = touched.Length > 1 ? "にまたがる" : "を含む";
        return $"{desktops}{relation}範囲（全体座標 {DescribeRange(capture, Point.Empty)}）";
    }

    private static string DesktopName(IEnumerable<int> numbers) => $"デスクトップ【{string.Join("・", numbers)}】";

    private static string DescribeRange(Rectangle capture, Point origin)
    {
        long x = (long)capture.X - origin.X, y = (long)capture.Y - origin.Y;
        return $"【{x},{y}】 {capture.Width} × {capture.Height} px";
    }
}
