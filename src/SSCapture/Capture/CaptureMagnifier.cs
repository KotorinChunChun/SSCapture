using System.Drawing.Drawing2D;

namespace SSCapture.Capture;

internal static class CaptureMagnifier
{
    private const int Radius = 4;
    private const int Scale = 10;
    private const int LensSize = (Radius * 2 + 1) * Scale;
    private const int Offset = 24;
    private const int Clearance = 8;

    internal readonly record struct Placement(Rectangle Bounds, int Quadrant = -1);

    internal static Rectangle Place(Rectangle viewport, Point pointer) => Place(viewport, pointer, [], -1).Bounds;

    internal static Placement Place(Rectangle viewport, Point pointer, IReadOnlyList<Rectangle> reserved, int previousQuadrant)
    {
        if (viewport.Width < LensSize || viewport.Height < LensSize) return new(Rectangle.Empty);
        var obstacles = reserved.Where(rect => rect.Width > 0 && rect.Height > 0 && rect.IntersectsWith(viewport))
            .Append(new Rectangle(pointer, new Size(1, 1)))
            .Select(rect => Rectangle.Inflate(rect, Clearance, Clearance)).ToArray();
        Point[] candidates =
        [
            new(pointer.X + Offset, pointer.Y - Offset - LensSize),
            new(pointer.X - Offset - LensSize, pointer.Y - Offset - LensSize),
            new(pointer.X + Offset, pointer.Y + Offset),
            new(pointer.X - Offset - LensSize, pointer.Y + Offset)
        ];

        if (previousQuadrant is >= 0 and < 4 && Fits(candidates[previousQuadrant]))
            return At(candidates[previousQuadrant], previousQuadrant);
        for (int index = 0; index < candidates.Length; index++)
            if (Fits(candidates[index])) return At(candidates[index], index);

        // In top-left coordinates, each obstacle excludes a rectangle. The closest free
        // point must lie at the preferred coordinate or at an edge on each axis.
        int right = viewport.Right - LensSize, bottom = viewport.Bottom - LensSize;
        Point preferred = new(pointer.X - LensSize / 2, pointer.Y - LensSize / 2);
        HashSet<int> xs = [viewport.Left, right, Math.Clamp(preferred.X, viewport.Left, right)];
        HashSet<int> ys = [viewport.Top, bottom, Math.Clamp(preferred.Y, viewport.Top, bottom)];
        foreach (Rectangle obstacle in obstacles)
        {
            xs.Add(Math.Clamp(obstacle.Left - LensSize, viewport.Left, right));
            xs.Add(Math.Clamp(obstacle.Right, viewport.Left, right));
            ys.Add(Math.Clamp(obstacle.Top - LensSize, viewport.Top, bottom));
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
            var bounds = new Rectangle(point, new Size(LensSize, LensSize));
            return viewport.Contains(bounds) && !obstacles.Any(bounds.IntersectsWith);
        }
        static Placement At(Point point, int quadrant = -1) => new(new Rectangle(point, new Size(LensSize, LensSize)), quadrant);
    }

    internal static void Draw(Graphics graphics, Rectangle lens, Bitmap frame, Rectangle frameBounds, Point sample,
        Rectangle selection = default, IReadOnlyList<Point>? polygon = null)
    {
        const int pixels = Radius * 2 + 1;
        Rectangle requested = new(sample.X - Radius, sample.Y - Radius, pixels, pixels);
        Rectangle available = Rectangle.Intersect(requested, frameBounds);
        graphics.FillRectangle(Brushes.DimGray, lens);
        if (available.Width > 0 && available.Height > 0)
        {
            Rectangle destination = new(lens.X + (available.X - requested.X) * Scale, lens.Y + (available.Y - requested.Y) * Scale,
                available.Width * Scale, available.Height * Scale);
            available.Offset(-frameBounds.X, -frameBounds.Y);
            GraphicsState state = graphics.Save();
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(frame, destination, available, GraphicsUnit.Pixel);
            graphics.Restore(state);
        }

        if (selection.Width > 0 && selection.Height > 0)
        {
            using var shape = polygon is { Count: >= 3 } ? new GraphicsPath(FillMode.Alternate) : null;
            if (shape is not null) shape.AddPolygon(polygon!.ToArray());
            using var dim = new SolidBrush(Color.FromArgb(72, Color.Black));
            Span<bool> included = stackalloc bool[pixels * pixels];
            for (int y = 0; y < pixels; y++)
                for (int x = 0; x < pixels; x++)
                {
                    int sx = requested.X + x, sy = requested.Y + y;
                    bool inside = selection.Contains(sx, sy) && (shape?.IsVisible(sx + 0.5f, sy + 0.5f) ?? true);
                    included[y * pixels + x] = inside;
                    if (!inside) graphics.FillRectangle(dim, lens.X + x * Scale, lens.Y + y * Scale, Scale, Scale);
                }
            for (int y = 0; y < pixels; y++)
                for (int x = 0; x < pixels; x++)
                {
                    int i = y * pixels + x, left = lens.X + x * Scale, top = lens.Y + y * Scale;
                    if (x > 0 && included[i - 1] != included[i])
                        graphics.FillRectangle(Brushes.Red, left, top, 1, Scale);
                    if (y > 0 && included[i - pixels] != included[i])
                        graphics.FillRectangle(Brushes.Red, left, top, Scale, 1);
                }
        }
        graphics.DrawRectangle(Pens.Black, lens.X, lens.Y, lens.Width - 1, lens.Height - 1);
        int centerX = lens.X + Radius * Scale, centerY = lens.Y + Radius * Scale;
        graphics.DrawRectangle(Pens.White, centerX + 1, centerY + 1, Scale - 3, Scale - 3);
    }
}
