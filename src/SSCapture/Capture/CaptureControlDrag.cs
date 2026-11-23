using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed record CaptureControlTarget(CaptureWindow Window, Rectangle Bounds, ControlIdentity? Identity);

internal sealed class CaptureControlDrag(Point start)
{
    private sealed class WindowPath
    {
        internal HashSet<Point> Points { get; } = [];
        internal (Rectangle Area, CaptureControlTarget Target)[]? Layout { get; set; }
    }

    private readonly List<Point> path = [start];
    private readonly Dictionary<CaptureWindow, WindowPath> windows = [];
    private readonly HashSet<CaptureControlTarget> selected = [];
    internal IReadOnlyList<Point> Path => path;
    internal IReadOnlyCollection<CaptureControlTarget> Selected => selected;
    internal Rectangle SelectionBounds { get; private set; }
    internal Rectangle TrailBounds { get; private set; } = new(start, new Size(1, 1));
    internal CaptureWindow? NextWindow => windows.FirstOrDefault(pair => pair.Value.Layout is null).Key;
    internal bool LayoutUnavailable { get; private set; }

    internal void Move(Point end, Func<Point, CaptureWindow?>? hitWindow, CaptureControlTarget? startingTarget = null)
    {
        Point previous = path[^1];
        if (previous == end) return;
        bool startSegment = path.Count == 1;
        path.Add(end);
        TrailBounds = Rectangle.Union(TrailBounds, new(end, new Size(1, 1)));
        if (startingTarget is not null) Add(startingTarget);
        if (hitWindow is null) return;

        foreach (Point point in CaptureTargetDrag.Trace(previous, end, includeStart: startSegment && startingTarget is null))
        {
            if (hitWindow(point) is not { } window) continue;
            if (!windows.TryGetValue(window, out var found)) windows.Add(window, found = new());
            if (!found.Points.Add(point) || found.Layout is not { } layout) continue;
            SelectAt(point, layout);
        }
    }

    internal void SetLayout(CaptureWindow window, CaptureWindow observed, ControlLayout? layout, IReadOnlyList<Rectangle> monitors)
    {
        if (layout is not { Incomplete: false })
        {
            LayoutUnavailable = true;
            layout = null;
        }
        var found = windows[window];
        found.Layout = (layout?.Entries ?? []).Select(item =>
        {
            var bounds = item.Hit.Bounds;
            var area = item.HitArea;
            return (new Rectangle(area.X, area.Y, area.Width, area.Height),
                new CaptureControlTarget(observed, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height), item.Hit.Identity));
        }).Where(entry => CaptureGeometry.IsFullyCovered(entry.Item2.Bounds, monitors)).ToArray();
        foreach (Point point in found.Points) SelectAt(point, found.Layout);
    }

    private void SelectAt(Point point, (Rectangle Area, CaptureControlTarget Target)[] layout)
    {
        foreach (var element in layout)
        {
            if (!element.Area.Contains(point)) continue;
            Add(element.Target);
            break;
        }
    }

    private void Add(CaptureControlTarget target)
    {
        if (selected.Add(target))
            SelectionBounds = SelectionBounds.IsEmpty ? target.Bounds : Rectangle.Union(SelectionBounds, target.Bounds);
    }
}
