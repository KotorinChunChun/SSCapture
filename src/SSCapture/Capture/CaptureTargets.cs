namespace SSCapture.Capture;

internal sealed record CaptureComposite(IReadOnlyList<Rectangle>? Regions = null, IReadOnlyList<CaptureWindow>? WindowRoots = null);

internal abstract record CaptureReplaySource
{
    internal sealed record Windows(IReadOnlyList<CaptureWindow> Targets, bool Isolated, bool IncludeOwned, bool IsSingleWindowSelection = false) : CaptureReplaySource
    {
        internal int? TargetMargin { get; init; }
    }
    internal sealed record Control(CaptureWindow Window, Services.ControlIdentity? Identity, bool Isolated) : CaptureReplaySource
    {
        internal int? TargetMargin { get; init; }
    }
    internal sealed record ControlGroup(IReadOnlyList<CaptureControlTarget> Targets, bool Isolated) : CaptureReplaySource;
    internal sealed record Monitors(IReadOnlyList<Rectangle> Bounds) : CaptureReplaySource;
    internal sealed record Scroll(Rectangle Viewport) : CaptureReplaySource;
}

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

internal sealed class CaptureTargetDrag(Func<CaptureWindow, IReadOnlyList<CaptureWindow>>? findWindowGroup = null)
{
    private readonly List<Point> path = [];
    private readonly HashSet<Rectangle> monitors = [];
    private readonly Dictionary<nint, CaptureWindow> windows = [];
    private readonly Dictionary<nint, CaptureWindow> includedWindows = [];
    internal IReadOnlyList<Point> Path => path;
    internal IReadOnlySet<Rectangle> Monitors => monitors;
    internal IReadOnlyCollection<CaptureWindow> Windows => windows.Values;
    internal IReadOnlyCollection<CaptureWindow> IncludedWindows => includedWindows.Values;
    internal IReadOnlyList<Rectangle> BrightWindows { get; private set; } = [];
    internal Rectangle TrailBounds { get; private set; }

    internal void Move(Point point, IReadOnlyList<Rectangle> monitorBounds, Func<Point, CaptureWindow?>? hitWindow)
    {
        Point start = path.Count == 0 ? point : path[^1];
        if (path.Count != 0 && start == point) return;
        var pixel = new Rectangle(point, new Size(1, 1));
        TrailBounds = path.Count == 0 ? pixel : Rectangle.Union(TrailBounds, pixel);
        path.Add(point);

        // A fast mouse can skip an entire window or a thin section of a monitor between messages.
        foreach (Point through in Trace(start, point, includeStart: path.Count == 1))
        {
            if (hitWindow is not null)
            {
                if (hitWindow(through) is { } window && windows.TryAdd(window.Handle, window))
                {
                    var group = findWindowGroup?.Invoke(window) ?? [window];
                    includedWindows.TryAdd(window.Handle, window);
                    foreach (CaptureWindow member in group) includedWindows.TryAdd(member.Handle, member);
                    BrightWindows = includedWindows.Values.Select(member => member.Bounds).Distinct().ToArray();
                }
            }
            else
                foreach (Rectangle monitor in monitorBounds)
                    if (monitor.Contains(through)) monitors.Add(monitor);
        }
    }

    internal static IEnumerable<Point> Trace(Point start, Point end, bool includeStart)
    {
        long dx = (long)end.X - start.X, dy = (long)end.Y - start.Y;
        long steps = Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        for (long step = includeStart ? 0 : 1; step <= steps; step++)
            yield return new((int)(start.X + Math.Round((double)dx * step / steps)),
                (int)(start.Y + Math.Round((double)dy * step / steps)));
    }
}
