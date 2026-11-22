using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Capture;

internal static class ScrollCapture
{
    internal static async Task<(Bitmap Image, CaptureMetadata? Metadata)?> CollectAsync(Rectangle viewport, AppSettings settings,
        nint targetWindow = 0, ElementMetadata? initialFocus = null)
    {
        IReadOnlyList<Rectangle> monitors = CaptureNative.MonitorBounds();
        if (!CaptureGeometry.IsFullyCovered(viewport, monitors))
            throw new InvalidOperationException("スクロール撮影の範囲に画面外の座標が含まれています。");
        var stitcher = new ScrollFrameStitcher(viewport.Size, settings.ScreenshotPaddingPixels);

        if (targetWindow != 0) _ = CaptureNative.SetForegroundWindow(targetWindow);
        CaptureNative.SynchronizeDesktop();

        bool recordContext = settings.DefaultDestination.HasFlag(OutputDestination.File) && settings.MetadataMode != CaptureMetadataMode.None;
        DateTimeOffset startedAt = DateTimeOffset.Now;
        using var cursor = settings.IncludeCursor || recordContext ? CursorSnapshot.Capture() : null;
        Point point = cursor?.ScreenPosition ?? CaptureNative.CursorPosition();
        var pointer = new PointerObservation(startedAt, PixelPoint.From(point), cursor is not null, cursor is not null && settings.IncludeCursor);
        Bitmap TakeFrame() => InstantCapture.Take(viewport);
        using (Bitmap first = TakeFrame())
            stitcher.Add(first);

        Task<CaptureContext>? contextTask = recordContext ? CaptureContextClient.Shared.ObserveAsync(point) : null;
        using var form = new ScrollCaptureForm(viewport, stitcher, TakeFrame, monitors);
        form.Start(interceptEscape: true);
        Bitmap? result = await form.Completion;
        if (result is null) return null;
        try
        {
            if (settings.IncludeCursor && cursor is not null)
            {
                using var clip = new Region(new Rectangle(Point.Empty, viewport.Size));
                cursor.Draw(result, viewport.Location, clip);
            }
            CaptureMetadata? metadata = null;
            if (contextTask is not null)
            {
                CaptureContext context = await contextTask;
                if (initialFocus is not null) context = context with { FocusAtSelectionStart = initialFocus };
                metadata = CaptureMetadata.Create(viewport, 0, startedAt, pointer, context) with
                {
                    SourceSize = new(result.Width, result.Height)
                };
                metadata = metadata.WithPadding(settings.ScreenshotPaddingPixels);
            }
            return (result, metadata);
        }
        catch { result.Dispose(); throw; }
    }
}
