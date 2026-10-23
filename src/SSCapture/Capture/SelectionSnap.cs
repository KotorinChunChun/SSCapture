namespace SSCapture.Capture;

/// <summary>Snaps against finite physical-pixel edges frozen at selection start.</summary>
internal sealed class SelectionSnap
{
    internal const int Distance = 20;
    private readonly int distance;
    private readonly Edge[] edges;

    internal SelectionSnap(IReadOnlyList<Rectangle> monitors, IReadOnlyList<Rectangle> windows, int distance = Distance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, 32768);
        this.distance = distance;
        List<Edge> collected = [];
        // モニタ境界はpx刻み・ウィンドウ境界より優先する。
        foreach (var (bounds, monitor) in monitors.Select(b => (b, true)).Concat(windows.Select(b => (b, false))))
        {
            long right = (long)bounds.Left + bounds.Width, bottom = (long)bounds.Top + bounds.Height;
            if (bounds.Width <= 0 || bounds.Height <= 0 || right > int.MaxValue || bottom > int.MaxValue) continue;
            collected.Add(new(true, bounds.Left, bounds.Top, (int)bottom, monitor));
            collected.Add(new(true, (int)right, bounds.Top, (int)bottom, monitor));
            collected.Add(new(false, bounds.Top, bounds.Left, (int)right, monitor));
            collected.Add(new(false, (int)bottom, bounds.Left, (int)right, monitor));
        }
        edges = collected.ToArray();
    }

    internal Point Apply(Point point) => Evaluate(point).Point;

    internal SnapResult Evaluate(Point point, bool includeWindows = true)
    {
        long bestX = (long)distance * distance + 1, bestY = bestX;
        int x = point.X, y = point.Y;
        bool hitX = false, hitY = false, monitorX = false, monitorY = false;
        foreach (Edge edge in edges)
        {
            if (!includeWindows && !edge.Monitor) continue;
            long across = Math.Abs((long)(edge.Vertical ? point.X : point.Y) - edge.Coordinate);
            int along = edge.Vertical ? point.Y : point.X;
            long beyond = Math.Abs((long)along - Math.Clamp(along, edge.First, edge.Last));
            // Reject before squaring, including extreme negative desktop coordinates.
            if (across > this.distance || beyond > this.distance) continue;
            long distance = across * across + beyond * beyond;
            if (distance > (long)this.distance * this.distance) continue;
            if (edge.Vertical && (!hitX || edge.Monitor && !monitorX || edge.Monitor == monitorX && distance < bestX))
            { bestX = distance; x = edge.Coordinate; hitX = true; monitorX = edge.Monitor; }
            if (!edge.Vertical && (!hitY || edge.Monitor && !monitorY || edge.Monitor == monitorY && distance < bestY))
            { bestY = distance; y = edge.Coordinate; hitY = true; monitorY = edge.Monitor; }
        }
        return new(new Point(x, y), hitX, hitY);
    }

    internal readonly record struct SnapResult(Point Point, bool X, bool Y);
    private readonly record struct Edge(bool Vertical, int Coordinate, int First, int Last, bool Monitor);
}
