using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SSCapture.Capture;

internal sealed class DesktopSnapshot : IDisposable
{
    // Graphics.CopyFromScreen rejects bitwise combinations, even though the
    // underlying Win32 BitBlt API accepts SRCCOPY | CAPTUREBLT.
    internal const CopyPixelOperation CopyOperation = CopyPixelOperation.SourceCopy;
    internal Bitmap Image { get; }
    internal Rectangle Bounds { get; }
    internal IReadOnlyList<Rectangle> Monitors { get; }

    /// <summary>Takes ownership of image. Also used with synthetic pixels for isolated overlay checks.</summary>
    internal DesktopSnapshot(Bitmap image, Rectangle bounds, IReadOnlyList<Rectangle> monitors)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(monitors);
        if (image.Size != bounds.Size || monitors.Count == 0 ||
            monitors.Any(monitor => monitor.Width <= 0 || monitor.Height <= 0 || !bounds.Contains(monitor)))
            throw new ArgumentException("画像とモニタ領域の物理ピクセル寸法が一致しません。");
        Image = image;
        Bounds = bounds;
        Monitors = monitors.ToArray();
    }

    internal static DesktopSnapshot Take(bool includeCursor = false)
    {
        using CursorSnapshot? cursor = includeCursor ? CursorSnapshot.Capture() : null;
        IReadOnlyList<Rectangle> monitors = CaptureNative.MonitorBounds();
        Rectangle desktop = monitors.Aggregate(Rectangle.Union);
        Bitmap image = new(desktop.Width, desktop.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (Graphics target = Graphics.FromImage(image))
            {
                target.Clear(Color.Transparent);
                target.CompositingMode = CompositingMode.SourceCopy;
                foreach (Rectangle monitor in monitors)
                {
                    // GDI screenshot alpha is undefined. A 32bpp RGB intermediate produces opaque
                    // monitor pixels while preserving transparent areas between physical monitors.
                    using Bitmap monitorImage = new(monitor.Width, monitor.Height, PixelFormat.Format32bppRgb);
                    using (Graphics graphics = Graphics.FromImage(monitorImage))
                        graphics.CopyFromScreen(monitor.Location, Point.Empty, monitor.Size,
                            CopyOperation);
                    target.DrawImageUnscaled(monitorImage, monitor.X - desktop.X, monitor.Y - desktop.Y);
                }
            }
            if (cursor is not null)
            {
                using Region monitorClip = new();
                monitorClip.MakeEmpty();
                foreach (Rectangle monitor in monitors)
                    monitorClip.Union(new Rectangle(monitor.X - desktop.X, monitor.Y - desktop.Y, monitor.Width, monitor.Height));
                cursor.Draw(image, desktop.Location, monitorClip);
            }
            return new DesktopSnapshot(image, desktop, monitors);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public void Dispose() => Image.Dispose();
}
