using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed class DesktopSnapshot : IDisposable
{
    // Graphics.CopyFromScreen rejects bitwise combinations, even though the
    // underlying Win32 BitBlt API accepts SRCCOPY | CAPTUREBLT.
    internal const CopyPixelOperation CopyOperation = CopyPixelOperation.SourceCopy;
    internal Bitmap Image { get; }
    internal Rectangle Bounds { get; }
    internal IReadOnlyList<Rectangle> Monitors { get; }
    internal DateTimeOffset CapturedAt { get; private init; } = DateTimeOffset.Now;
    internal Services.PointerObservation? Pointer { get; private init; }
    internal Services.CaptureContext? Context { get; set; }
    internal FrozenCaptureTargets? FrozenTargets { get; }

    /// <summary>Takes ownership of image. Also used with synthetic pixels for isolated overlay checks.</summary>
    internal DesktopSnapshot(Bitmap image, Rectangle bounds, IReadOnlyList<Rectangle> monitors,
        FrozenCaptureTargets? frozenTargets = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(monitors);
        if (image.Size != bounds.Size || monitors.Count == 0 ||
            monitors.Any(monitor => monitor.Width <= 0 || monitor.Height <= 0 || !bounds.Contains(monitor)))
            throw new ArgumentException("画像とモニター領域の物理ピクセル寸法が一致しません。");
        Image = image;
        Bounds = bounds;
        Monitors = monitors.ToArray();
        FrozenTargets = frozenTargets;
    }

    internal static async Task<DesktopSnapshot> TakeFrozenSelectionAsync(bool includeCursor, bool observePointer)
    {
        Rectangle desktop = CaptureNative.MonitorBounds().Aggregate(Rectangle.Union);
        Point pointer = CaptureNative.CursorPosition();
        CaptureWindow? candidate = CaptureNative.CaptureWindows(desktop).FirstOrDefault(window => window.Bounds.Contains(pointer));
        if (candidate is null || !FrozenCaptureTargets.IsBrowser(candidate))
            return Take(includeCursor, observePointer, captureTargets: true);

        Rectangle bounds;
        try { bounds = CaptureNative.RequireCaptureWindow(candidate); }
        catch (InvalidOperationException) { return Take(includeCursor, observePointer, captureTargets: true); }

        ControlLayout? before = await ReadLayoutAsync(candidate, bounds);
        var snapshot = Take(includeCursor, observePointer, captureTargets: true);
        try
        {
            if (before is { Incomplete: false, Entries.Length: > 0 })
                snapshot.FrozenTargets?.SetBrowserControls(candidate, bounds, before, await ReadLayoutAsync(candidate, bounds));
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }

        static async Task<ControlLayout?> ReadLayoutAsync(CaptureWindow target, Rectangle bounds)
        {
            try
            {
                if (CaptureNative.RequireCaptureWindow(target) != bounds) return null;
                var layout = await CaptureContextClient.Shared.FindFrozenControlLayoutAsync(
                    new(target.Handle.ToInt64(), target.ProcessId, PixelBounds.From(bounds), null));
                return CaptureNative.RequireCaptureWindow(target) == bounds ? layout : null;
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    internal static DesktopSnapshot Take(bool includeCursor = false, bool observePointer = false, bool captureTargets = false)
    {
        IReadOnlyList<Rectangle> monitors = CaptureNative.MonitorBounds();
        Rectangle desktop = monitors.Aggregate(Rectangle.Union);
        var before = captureTargets ? CaptureNative.ReadFrozenWindows(desktop) : null;
        DateTimeOffset pointerAt = DateTimeOffset.Now;
        using CursorSnapshot? cursor = includeCursor || observePointer ? CursorSnapshot.Capture() : null;
        Services.PointerObservation? pointer = observePointer ? new(pointerAt,
            Services.PixelPoint.From(cursor?.ScreenPosition ?? CaptureNative.CursorPosition()), cursor is not null, includeCursor && cursor is not null) : null;
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
            var frozenTargets = before is not null && CaptureNative.ReadFrozenWindows(desktop) is { } after
                ? FrozenCaptureTargets.Compare(before, after) : null;
            if (includeCursor && cursor is not null)
            {
                using Region monitorClip = new();
                monitorClip.MakeEmpty();
                foreach (Rectangle monitor in monitors)
                    monitorClip.Union(new Rectangle(monitor.X - desktop.X, monitor.Y - desktop.Y, monitor.Width, monitor.Height));
                cursor.Draw(image, desktop.Location, monitorClip);
            }
            return new DesktopSnapshot(image, desktop, monitors, frozenTargets) { CapturedAt = pointerAt, Pointer = pointer };
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public void Dispose() => Image.Dispose();
}
