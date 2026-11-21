using System.Drawing.Drawing2D;

namespace SSCapture.Capture;

internal static class CaptureMagnifier
{
    private const int Radius = 4;
    private const int Scale = 10;
    private const int LensSize = (Radius * 2 + 1) * Scale;
    private const int Offset = 24;

    internal static Rectangle Place(Rectangle viewport, Point pointer)
    {
        if (viewport.Width < LensSize || viewport.Height < LensSize) return Rectangle.Empty;
        int x = pointer.X + Offset;
        int y = pointer.Y - Offset - LensSize;
        if (x + LensSize > viewport.Right) x = pointer.X - Offset - LensSize;
        if (y < viewport.Top) y = pointer.Y + Offset;
        return new(Math.Clamp(x, viewport.Left, viewport.Right - LensSize),
            Math.Clamp(y, viewport.Top, viewport.Bottom - LensSize), LensSize, LensSize);
    }

    internal static void Draw(Graphics graphics, Rectangle lens, Bitmap frame, Rectangle frameBounds, Point pointer)
    {
        Rectangle requested = new(pointer.X - Radius, pointer.Y - Radius, Radius * 2 + 1, Radius * 2 + 1);
        Rectangle available = Rectangle.Intersect(requested, frameBounds);
        graphics.FillRectangle(Brushes.DimGray, lens);
        if (!available.IsEmpty)
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
        graphics.DrawRectangle(Pens.Black, lens.X, lens.Y, lens.Width - 1, lens.Height - 1);
        int centerX = lens.X + Radius * Scale, centerY = lens.Y + Radius * Scale;
        graphics.DrawRectangle(Pens.Black, centerX - 1, centerY - 1, Scale + 1, Scale + 1);
        graphics.DrawRectangle(Pens.White, centerX, centerY, Scale - 1, Scale - 1);
    }
}
