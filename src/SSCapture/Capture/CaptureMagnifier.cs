using System.Drawing.Drawing2D;

namespace SSCapture.Capture;

internal static class CaptureMagnifier
{
    internal const int DefaultDotCount = 20;
    internal const int MinDotCount = 2;
    internal const int MaxDotCount = 32;
    internal const int DefaultZoom = 4;
    internal const int MinZoom = 2;
    internal const int MaxZoom = 20;
    private const int Offset = 24;
    private const int Clearance = 8;

    internal readonly record struct Placement(Rectangle Bounds, int Quadrant = -1);

    internal static Rectangle Place(Rectangle viewport, Point pointer) => Place(viewport, pointer, [], -1).Bounds;

    internal static Placement Place(Rectangle viewport, Point pointer, IReadOnlyList<Rectangle> reserved, int previousQuadrant,
        int lensSize = DefaultDotCount * DefaultZoom)
    {
        if (viewport.Width < lensSize || viewport.Height < lensSize) return new(Rectangle.Empty);
        var obstacles = reserved.Where(rect => rect.Width > 0 && rect.Height > 0 && rect.IntersectsWith(viewport))
            .Append(new Rectangle(pointer, new Size(1, 1)))
            .Select(rect => Rectangle.Inflate(rect, Clearance, Clearance)).ToArray();
        Point[] candidates =
        [
            new(pointer.X + Offset, pointer.Y - Offset - lensSize),
            new(pointer.X - Offset - lensSize, pointer.Y - Offset - lensSize),
            new(pointer.X + Offset, pointer.Y + Offset),
            new(pointer.X - Offset - lensSize, pointer.Y + Offset)
        ];

        if (previousQuadrant is >= 0 and < 4 && Fits(candidates[previousQuadrant]))
            return At(candidates[previousQuadrant], previousQuadrant);
        for (int index = 0; index < candidates.Length; index++)
            if (Fits(candidates[index])) return At(candidates[index], index);

        // In top-left coordinates, each obstacle excludes a rectangle. The closest free
        // point must lie at the preferred coordinate or at an edge on each axis.
        int right = viewport.Right - lensSize, bottom = viewport.Bottom - lensSize;
        Point preferred = new(pointer.X - lensSize / 2, pointer.Y - lensSize / 2);
        HashSet<int> xs = [viewport.Left, right, Math.Clamp(preferred.X, viewport.Left, right)];
        HashSet<int> ys = [viewport.Top, bottom, Math.Clamp(preferred.Y, viewport.Top, bottom)];
        foreach (Rectangle obstacle in obstacles)
        {
            xs.Add(Math.Clamp(obstacle.Left - lensSize, viewport.Left, right));
            xs.Add(Math.Clamp(obstacle.Right, viewport.Left, right));
            ys.Add(Math.Clamp(obstacle.Top - lensSize, viewport.Top, bottom));
            ys.Add(Math.Clamp(obstacle.Bottom, viewport.Top, bottom));
        }

        Point? best = null;
        double distance = double.PositiveInfinity;
        foreach (int y in ys.Order())
            foreach (int x in xs.Order())
            {
                var point = new Point(x, y);
                if (!Fits(point)) continue;
                double dx = (double)x - preferred.X, dy = (double)y - preferred.Y;
                double squared = dx * dx + dy * dy;
                if (squared >= distance) continue;
                best = point;
                distance = squared;
            }
        return best is Point position ? At(position) : new(Rectangle.Empty);

        bool Fits(Point point)
        {
            var bounds = new Rectangle(point, new Size(lensSize, lensSize));
            return viewport.Contains(bounds) && !obstacles.Any(bounds.IntersectsWith);
        }
        Placement At(Point point, int quadrant = -1) => new(new Rectangle(point, new Size(lensSize, lensSize)), quadrant);
    }

    internal static void Draw(Graphics graphics, Rectangle lens, Bitmap frame, Rectangle frameBounds, Point boundary,
        Rectangle selection = default, IReadOnlyList<Point>? polygon = null,
        int dotCount = DefaultDotCount, int zoom = DefaultZoom, IReadOnlyList<Rectangle>? regions = null)
    {
        int half = dotCount / 2;
        Rectangle requested = new(boundary.X - half, boundary.Y - half, dotCount, dotCount);
        Rectangle available = Rectangle.Intersect(requested, frameBounds);
        graphics.FillRectangle(Brushes.DimGray, lens);
        if (available.Width > 0 && available.Height > 0)
        {
            Rectangle destination = InLens(available, requested, lens, zoom);
            available.Offset(-frameBounds.X, -frameBounds.Y);
            GraphicsState state = graphics.Save();
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(frame, destination, available, GraphicsUnit.Pixel);
            graphics.Restore(state);
        }

        if (regions is { Count: > 0 })
        {
            using var dim = new SolidBrush(Color.FromArgb(72, Color.Black));
            DrawShapedBoundary(graphics, lens, requested, zoom, dim,
                (x, y) => IsInRegions(regions, x, y));
        }
        else if (selection.Width > 0 && selection.Height > 0)
        {
            using var dim = new SolidBrush(Color.FromArgb(72, Color.Black));
            if (polygon is { Count: >= 3 })
                DrawPolygonBoundary(graphics, lens, requested, selection, polygon, zoom, dim);
            else
                DrawRectangleBoundary(graphics, lens, requested, selection, zoom, dim);
        }
        else DrawPointer(graphics, lens);

        graphics.DrawRectangle(Pens.Black, lens.Left, lens.Top, lens.Width - 1, lens.Height - 1);
    }

    private static void DrawPointer(Graphics graphics, Rectangle lens)
    {
        graphics.FillRectangle(Brushes.DodgerBlue, lens.Left + lens.Width / 2, lens.Top, 1, lens.Height);
        graphics.FillRectangle(Brushes.DodgerBlue, lens.Left, lens.Top + lens.Height / 2, lens.Width, 1);
    }

    private static void DrawBoundary(Graphics graphics, Rectangle lens, int x, int y, int length, bool vertical)
    {
        int axis = vertical ? x - lens.Left : y - lens.Top;
        int half = (vertical ? lens.Width : lens.Height) / 2;
        if (axis != half)
        {
            graphics.FillRectangle(Brushes.Red, x, y, vertical ? 1 : length, vertical ? length : 1);
            return;
        }

        int offset = vertical ? y - lens.Top : x - lens.Left;
        for (int i = 0; i < length; i++)
            if (((offset + i) & 7) < 4)
                graphics.FillRectangle(Brushes.Red, x + (vertical ? 0 : i), y + (vertical ? i : 0), 1, 1);
    }

    private static Rectangle InLens(Rectangle area, Rectangle requested, Rectangle lens, int zoom) =>
        new(lens.X + (area.X - requested.X) * zoom, lens.Y + (area.Y - requested.Y) * zoom,
            area.Width * zoom, area.Height * zoom);

    private static void DrawRectangleBoundary(Graphics graphics, Rectangle lens, Rectangle requested, Rectangle selection,
        int zoom, Brush dim)
    {
        Rectangle selected = Rectangle.Intersect(requested, selection);
        if (selected.Width <= 0 || selected.Height <= 0)
        {
            graphics.FillRectangle(dim, lens);
            DrawPointer(graphics, lens);
            return;
        }

        Rectangle inside = InLens(selected, requested, lens, zoom);
        graphics.FillRectangle(dim, lens.Left, lens.Top, lens.Width, inside.Top - lens.Top);
        graphics.FillRectangle(dim, lens.Left, inside.Bottom, lens.Width, lens.Bottom - inside.Bottom);
        graphics.FillRectangle(dim, lens.Left, inside.Top, inside.Left - lens.Left, inside.Height);
        graphics.FillRectangle(dim, inside.Right, inside.Top, lens.Right - inside.Right, inside.Height);
        DrawPointer(graphics, lens);

        if (inside.Left > lens.Left)
            DrawBoundary(graphics, lens, inside.Left, inside.Top, inside.Height, vertical: true);
        if (inside.Right < lens.Right)
            DrawBoundary(graphics, lens, inside.Right, inside.Top, inside.Height, vertical: true);
        if (inside.Top > lens.Top)
            DrawBoundary(graphics, lens, inside.Left, inside.Top, inside.Width, vertical: false);
        if (inside.Bottom < lens.Bottom)
            DrawBoundary(graphics, lens, inside.Left, inside.Bottom, inside.Width, vertical: false);
        if (inside.Right < lens.Right && inside.Bottom < lens.Bottom)
            graphics.FillRectangle(Brushes.Red, inside.Right, inside.Bottom, 1, 1);
    }

    private static void DrawPolygonBoundary(Graphics graphics, Rectangle lens, Rectangle requested, Rectangle selection,
        IReadOnlyList<Point> polygon, int zoom, Brush dim)
    {
        using var shape = new GraphicsPath(FillMode.Alternate);
        shape.AddPolygon(polygon.ToArray());
        DrawShapedBoundary(graphics, lens, requested, zoom, dim,
            (x, y) => selection.Contains(x, y) && shape.IsVisible(x + 0.5f, y + 0.5f));
    }

    private static bool IsInRegions(IReadOnlyList<Rectangle> regions, int x, int y)
    {
        foreach (Rectangle region in regions)
            if (region.Contains(x, y)) return true;
        return false;
    }

    private static void DrawShapedBoundary(Graphics graphics, Rectangle lens, Rectangle requested, int zoom, Brush dim,
        Func<int, int, bool> contains)
    {
        int count = requested.Width;
        Span<bool> included = stackalloc bool[count * count];
        for (int y = 0; y < count; y++)
            for (int x = 0; x < count; x++)
            {
                int sx = requested.X + x, sy = requested.Y + y;
                bool inside = contains(sx, sy);
                included[y * count + x] = inside;
                if (!inside) graphics.FillRectangle(dim, lens.X + x * zoom, lens.Y + y * zoom, zoom, zoom);
            }

        DrawPointer(graphics, lens);
        for (int y = 0; y < count; y++)
            for (int x = 0; x < count; x++)
            {
                int index = y * count + x, left = lens.X + x * zoom, top = lens.Y + y * zoom;
                if (x > 0 && included[index - 1] != included[index])
                    DrawBoundary(graphics, lens, left, top, zoom, vertical: true);
                if (y > 0 && included[index - count] != included[index])
                    DrawBoundary(graphics, lens, left, top, zoom, vertical: false);
            }
    }
}
