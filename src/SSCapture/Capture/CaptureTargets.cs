namespace SSCapture.Capture;

internal sealed record CaptureComposite(IReadOnlyList<Rectangle>? Regions = null, IReadOnlyList<CaptureWindow>? WindowRoots = null);

internal static class CaptureWindowGroups
{
    internal static IReadOnlyList<CaptureWindow> ForegroundGroup(CaptureWindow root, IReadOnlyList<CaptureWindow> windows)
    {
        var byHandle = windows.ToDictionary(window => window.Handle);
        if (!byHandle.TryGetValue(root.Handle, out var current) || current.ProcessId != root.ProcessId || current.ThreadId != root.ThreadId)
            return [root];

        var owned = windows.Where(window => window.ZOrder < current.ZOrder && window.ProcessId == current.ProcessId
            && HasOwner(window, current.Handle, byHandle)
            && !CaptureGeometry.IsFullyCovered(window.Bounds, windows.Where(other => other.ZOrder < window.ZOrder)
                .Select(other => other.Bounds).ToArray()));
        return [.. owned, current];
    }

    private static bool HasOwner(CaptureWindow window, nint root, Dictionary<nint, CaptureWindow> byHandle)
    {
        nint owner = window.Owner;
        for (int remaining = byHandle.Count; owner != 0 && remaining > 0; remaining--)
        {
            if (owner == root) return true;
            if (!byHandle.TryGetValue(owner, out var parent)) break;
            owner = parent.Owner;
        }
        return false;
    }
}

internal sealed class CaptureTargetDrag
{
    private readonly List<Point> path = [];
    private readonly HashSet<Rectangle> monitors = [];
    private readonly Dictionary<nint, CaptureWindow> windows = [];
    internal IReadOnlyList<Point> Path => path;
    internal IReadOnlySet<Rectangle> Monitors => monitors;
    internal IReadOnlyCollection<CaptureWindow> Windows => windows.Values;
    internal Rectangle TrailBounds { get; private set; }

    internal void Move(Point point, IReadOnlyList<Rectangle> monitorBounds, Func<Point, CaptureWindow?>? hitWindow)
    {
        Point start = path.Count == 0 ? point : path[^1];
        if (path.Count != 0 && start == point) return;
        var pixel = new Rectangle(point, new Size(1, 1));
        TrailBounds = path.Count == 0 ? pixel : Rectangle.Union(TrailBounds, pixel);
        path.Add(point);

        // A fast mouse can skip an entire window or a thin section of a monitor between messages.
        long dx = (long)point.X - start.X, dy = (long)point.Y - start.Y;
        long steps = Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        for (long sample = path.Count == 1 ? 0 : 1; sample <= steps; sample++)
        {
            Point through = new((int)(start.X + Math.Round((double)dx * sample / steps)),
                (int)(start.Y + Math.Round((double)dy * sample / steps)));
            if (hitWindow is not null)
            {
                if (hitWindow(through) is { } window) windows.TryAdd(window.Handle, window);
            }
            else
                foreach (Rectangle monitor in monitorBounds)
                    if (monitor.Contains(through)) monitors.Add(monitor);
        }
    }
}
