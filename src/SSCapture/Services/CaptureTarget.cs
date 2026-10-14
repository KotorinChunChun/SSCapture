using SSCapture.Capture;

namespace SSCapture.Services;

internal static class CaptureTarget
{
    internal static string? Describe(Rectangle capture, IEnumerable<DisplayTarget> displays)
    {
        if (capture.Width <= 0 || capture.Height <= 0) return null;
        var ordered = InstantCapture.OrderDisplays(displays);
        for (int i = 0; i < ordered.Count; i++)
        {
            Rectangle monitor = ordered[i].Bounds;
            if (!monitor.Contains(capture)) continue;
            if (monitor == capture) return $"デスクトップ【{i + 1}】全体";
            long x = (long)capture.X - monitor.X, y = (long)capture.Y - monitor.Y;
            return $"デスクトップ【{i + 1}】の 【{x},{y}】 ～ 【{x + capture.Width},{y + capture.Height}】";
        }
        var touched = ordered.Select((display, index) => (display.Bounds, Number: index + 1))
            .Where(display => display.Bounds.IntersectsWith(capture)).ToArray();
        if (touched.Length == 0) return null;
        string numbers = string.Join("・", touched.Select(d => d.Number));
        if (ordered.Count > 1 && capture == ordered.Select(d => d.Bounds).Aggregate(Rectangle.Union))
            return $"デスクトップ【{numbers}】全体";
        string relation = touched.Length > 1 ? "にまたがる" : "を含む";
        return $"デスクトップ【{numbers}】{relation}範囲（全体座標 【{capture.X},{capture.Y}】 ～ 【{(long)capture.X + capture.Width},{(long)capture.Y + capture.Height}】）";
    }
}
