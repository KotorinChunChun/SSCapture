namespace SSCapture.Capture;

internal enum CaptureAspectRatio { Free, Wide, Standard, Square }

internal static class AspectRatios
{
    internal static Point ConstrainDrag(Point start, Point target, double ratio, Rectangle? bounds)
    {
        long dx = (long)target.X - start.X, dy = (long)target.Y - start.Y;
        int signX = dx < 0 ? -1 : 1, signY = dy < 0 ? -1 : 1;
        long maxX = signX > 0 ? (long)(bounds?.Right ?? int.MaxValue) - start.X : (long)start.X - (bounds?.Left ?? int.MinValue);
        long maxY = signY > 0 ? (long)(bounds?.Bottom ?? int.MaxValue) - start.Y : (long)start.Y - (bounds?.Top ?? int.MinValue);
        double height = Math.Max(Math.Abs(dx) / ratio, Math.Abs(dy));
        height = Math.Max(0, Math.Min(height, Math.Min(maxX / ratio, maxY)));
        return new((int)(start.X + signX * (long)Math.Round(height * ratio, MidpointRounding.AwayFromZero)),
            (int)(start.Y + signY * (long)Math.Round(height, MidpointRounding.AwayFromZero)));
    }

    internal static string Label(CaptureAspectRatio ratio) => ratio switch
    {
        CaptureAspectRatio.Free => "自由", CaptureAspectRatio.Wide => "16:9",
        CaptureAspectRatio.Standard => "4:3", CaptureAspectRatio.Square => "1:1",
        _ => throw new ArgumentOutOfRangeException(nameof(ratio))
    };

    internal static Point Constrain(Point start, Point target, CaptureAspectRatio ratio, Rectangle? bounds,
        bool lockWidth, bool lockHeight)
    {
        if (ratio == CaptureAspectRatio.Free) return target;
        (int width, int height) = ratio switch
        {
            CaptureAspectRatio.Wide => (16, 9), CaptureAspectRatio.Standard => (4, 3),
            CaptureAspectRatio.Square => (1, 1), _ => throw new ArgumentOutOfRangeException(nameof(ratio))
        };
        long dx = (long)target.X - start.X, dy = (long)target.Y - start.Y;
        int sx = dx < 0 ? -1 : 1, sy = dy < 0 ? -1 : 1;
        double units = lockWidth ? Math.Abs(dx) / (double)width : lockHeight ? Math.Abs(dy) / (double)height
            : Math.Max(Math.Abs(dx) / (double)width, Math.Abs(dy) / (double)height);
        long count = (long)Math.Round(units, MidpointRounding.AwayFromZero);
        Rectangle desktop = bounds ?? Rectangle.FromLTRB(int.MinValue / 2, int.MinValue / 2, int.MaxValue / 2, int.MaxValue / 2);
        long maxX = sx > 0 ? (long)desktop.Right - start.X : (long)start.X - desktop.Left;
        long maxY = sy > 0 ? (long)desktop.Bottom - start.Y : (long)start.Y - desktop.Top;
        count = Math.Max(0, Math.Min(count, Math.Min(maxX / width, maxY / height)));
        return new Point((int)(start.X + sx * count * width), (int)(start.Y + sy * count * height));
    }
}
