using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SSCapture.Capture;

/// <summary>All coordinates in this class are physical virtual-desktop pixels.</summary>
internal static class CaptureGeometry
{
    internal const int MinimumSize = 2;

    internal static Rectangle FromDrag(Point start, Point end) => Rectangle.FromLTRB(
        Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
        Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));

    internal static Rectangle Clip(Rectangle selection, Rectangle desktop) =>
        Rectangle.Intersect(selection, desktop);

    internal static bool IsUsable(Rectangle selection) =>
        selection.Width >= MinimumSize && selection.Height >= MinimumSize;

    internal static Size ValidateFixedSize(Size? size)
    {
        if (size is not { Width: > 0 and <= 32768, Height: > 0 and <= 32768 } value
            || (long)value.Width * value.Height > 100_000_000)
            throw new ArgumentException("固定サイズは各辺1～32768ピクセル、合計100,000,000ピクセル以下で指定してください。", nameof(size));
        return value;
    }

    internal static bool IsFullyCovered(Rectangle bounds, IReadOnlyList<Rectangle> monitors)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.X + bounds.Width > int.MaxValue
            || (long)bounds.Y + bounds.Height > int.MaxValue) return false;
        List<Rectangle> remaining = [bounds];
        foreach (Rectangle monitor in monitors)
        {
            List<Rectangle> next = [];
            foreach (Rectangle piece in remaining)
            {
                Rectangle overlap = Rectangle.Intersect(piece, monitor);
                if (overlap.Width <= 0 || overlap.Height <= 0) { next.Add(piece); continue; }
                if (overlap.Top > piece.Top) next.Add(Rectangle.FromLTRB(piece.Left, piece.Top, piece.Right, overlap.Top));
                if (overlap.Bottom < piece.Bottom) next.Add(Rectangle.FromLTRB(piece.Left, overlap.Bottom, piece.Right, piece.Bottom));
                if (overlap.Left > piece.Left) next.Add(Rectangle.FromLTRB(piece.Left, overlap.Top, overlap.Left, overlap.Bottom));
                if (overlap.Right < piece.Right) next.Add(Rectangle.FromLTRB(overlap.Right, overlap.Top, piece.Right, overlap.Bottom));
            }
            remaining = next;
            if (remaining.Count == 0) return true;
        }
        return false;
    }

    internal static Rectangle PolygonBounds(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return Rectangle.Empty;
        return Rectangle.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y),
            points.Max(p => p.X), points.Max(p => p.Y));
    }

    internal static bool HasPolygonArea(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return false;
        Point first = points[0];
        int next = 1;
        while (next < points.Count && points[next] == first) next++;
        if (next == points.Count) return false;
        long dx = (long)points[next].X - first.X;
        long dy = (long)points[next].Y - first.Y;
        // Do not use signed polygon area: a self-intersecting lasso can have zero signed area.
        return points.Skip(next + 1).Any(p =>
            dx * ((long)p.Y - first.Y) != dy * ((long)p.X - first.X));
    }

    internal static Bitmap? Crop(Bitmap desktopImage, Rectangle desktop, Rectangle selection, int minimumSize = MinimumSize)
    {
        if (minimumSize < 1) throw new ArgumentOutOfRangeException(nameof(minimumSize));
        Rectangle clipped = Clip(selection, desktop);
        if (clipped.Width < minimumSize || clipped.Height < minimumSize) return null;
        return desktopImage.Clone(new Rectangle(clipped.X - desktop.X, clipped.Y - desktop.Y,
            clipped.Width, clipped.Height), PixelFormat.Format32bppArgb);
    }

    internal static Bitmap? Freeform(Bitmap desktopImage, Rectangle desktop, IReadOnlyList<Point> points)
    {
        if (!HasPolygonArea(points)) return null;
        Rectangle clipped = Clip(PolygonBounds(points), desktop);
        if (!IsUsable(clipped)) return null;
        Bitmap result = new(clipped.Width, clipped.Height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.SmoothingMode = SmoothingMode.None;
            using GraphicsPath path = new(FillMode.Alternate);
            path.AddPolygon(points.Select(p => new Point(p.X - clipped.X, p.Y - clipped.Y)).ToArray());
            graphics.SetClip(path);
            graphics.DrawImage(desktopImage, new Rectangle(0, 0, result.Width, result.Height),
                new Rectangle(clipped.X - desktop.X, clipped.Y - desktop.Y, result.Width, result.Height),
                GraphicsUnit.Pixel);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
