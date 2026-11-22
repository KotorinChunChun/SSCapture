namespace SSCapture.Capture;

internal static class CaptureTargetMargin
{
    internal static (Rectangle Bounds, int Margin) Apply(Rectangle original, int requested, Rectangle source,
        int minimumSize, IReadOnlyList<Rectangle>? monitors = null)
    {
        if (requested <= 0)
        {
            int minimum = -Math.Max(0, (Math.Min(original.Width, original.Height) - minimumSize) / 2);
            int margin = Math.Max(requested, minimum);
            return (Rectangle.Inflate(original, margin, margin), margin);
        }

        int upper = Math.Clamp(Math.Min(Math.Min(original.Left - source.Left, source.Right - original.Right),
            Math.Min(original.Top - source.Top, source.Bottom - original.Bottom)), 0, requested);
        int lower = 0;
        if (monitors is null) lower = upper;
        while (lower < upper)
        {
            int middle = lower + (int)(((long)upper - lower + 1) / 2);
            if (CaptureGeometry.IsFullyCovered(Rectangle.Inflate(original, middle, middle), monitors!))
                lower = middle;
            else
                upper = middle - 1;
        }
        return (Rectangle.Inflate(original, lower, lower), lower);
    }

    internal static (int Steps, int Remainder) WheelSteps(int remainder, int delta)
    {
        long total = (long)remainder + delta;
        return ((int)(total / 120), (int)(total % 120));
    }

    internal static Bitmap CropFrame(Bitmap image, Rectangle source, Rectangle bounds, int minimumSize)
    {
        if (source.Contains(bounds) && CaptureGeometry.Crop(image, source, bounds, minimumSize) is { } cropped)
            return cropped;
        throw new InvalidOperationException("選択した範囲がウィンドウ画像の外にあります。もう一度選び直してください。");
    }
}
