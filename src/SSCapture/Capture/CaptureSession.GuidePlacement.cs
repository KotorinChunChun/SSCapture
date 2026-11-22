using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private Rectangle? guideCaptureMonitor;

    internal Rectangle VisibleGuideBounds => guide is { Visible: true, IsDisposed: false } ? guide.Bounds : Rectangle.Empty;

    internal static Rectangle ResolveGuideMonitor(SelectionGuideDisplay display, Rectangle? mainMonitor, Rectangle captureMonitor) =>
        display == SelectionGuideDisplay.Main && mainMonitor is { Width: > 0, Height: > 0 } main ? main : captureMonitor;

    private Rectangle CaptureMonitorForGuide => guideCaptureMonitor ??=
        Snapshot.Monitors.FirstOrDefault(monitor => monitor.Contains(initialPointer)) is { Width: > 0 } monitor
            ? monitor : Snapshot.Monitors[0];

    private void TrackGuideMonitor(Point pointer, bool reposition)
    {
        if (Snapshot.Monitors.FirstOrDefault(monitor => monitor.Contains(pointer)) is not { Width: > 0 } monitor
            || CaptureMonitorForGuide == monitor) return;
        guideCaptureMonitor = monitor;
        if (reposition && OutputSettings.SelectionGuideDisplay == SelectionGuideDisplay.Captured)
            ReplaceGuide(overlays.FirstOrDefault(overlay => overlay.Visible && overlay.MonitorBounds == monitor));
    }
}
