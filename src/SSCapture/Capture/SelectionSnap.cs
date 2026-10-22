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
        // Preserve the frozen window order for equally close candidates.
        foreach (Rectangle bounds in windows.Concat(monitors))
        {
            long right = (long)bounds.Left + bounds.Width, bottom = (long)bounds.Top + bounds.Height;
            if (bounds.Width <= 0 || bounds.Height <= 0 || right > int.MaxValue || bottom > int.MaxValue) continue;
            collected.Add(new(true, bounds.Left, bounds.Top, (int)bottom));
            collected.Add(new(true, (int)right, bounds.Top, (int)bottom));
            collected.Add(new(false, bounds.Top, bounds.Left, (int)right));
            collected.Add(new(false, (int)bottom, bounds.Left, (int)right));
        }
        edges = collected.ToArray();
    }

    internal Point Apply(Point point)
    {
        long bestX = (long)distance * distance + 1, bestY = bestX;
        int x = point.X, y = point.Y;
        foreach (Edge edge in edges)
        {
            long across = Math.Abs((long)(edge.Vertical ? point.X : point.Y) - edge.Coordinate);
            int along = edge.Vertical ? point.Y : point.X;
            long beyond = Math.Abs((long)along - Math.Clamp(along, edge.First, edge.Last));
            // Reject before squaring, including extreme negative desktop coordinates.
            if (across > this.distance || beyond > this.distance) continue;
            long distance = across * across + beyond * beyond;
            if (edge.Vertical && distance < bestX) { bestX = distance; x = edge.Coordinate; }
            if (!edge.Vertical && distance < bestY) { bestY = distance; y = edge.Coordinate; }
        }
        return new Point(x, y);
    }

    private readonly record struct Edge(bool Vertical, int Coordinate, int First, int Last);
}
