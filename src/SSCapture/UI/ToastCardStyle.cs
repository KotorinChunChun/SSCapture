using System.Drawing.Drawing2D;

namespace SSCapture.UI;

internal static class ToastCardStyle
{
    internal static readonly Color Surface = Color.FromArgb(250, 252, 253);
    internal static readonly Color Ink = Color.FromArgb(28, 38, 46);
    internal static readonly Color Muted = Color.FromArgb(88, 103, 115);
    internal static readonly Color Accent = Color.FromArgb(0, 106, 119);
    internal static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }

    internal static Rectangle PreviewBounds(int dpi) => new(Scale(16, dpi), Scale(40, dpi), Scale(44, dpi), Scale(44, dpi));
    internal static int Scale(int value, int dpi) => Math.Max(1, (int)Math.Round(value * dpi / 96d));
}
