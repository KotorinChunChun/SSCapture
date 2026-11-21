using SSCapture.Services;

namespace SSCapture.Capture;

internal static class IsolatedWindowCapture
{
    internal static async Task<(Bitmap Image, Rectangle Bounds, CaptureMetadata? Metadata)> TakeAsync(
        CaptureWindow target, AppSettings settings, ElementMetadata? focusAtSelectionStart = null)
    {
        _ = CaptureNative.RequireCaptureWindow(target);
        bool observe = settings.DefaultDestination.HasFlag(OutputDestination.File) && settings.MetadataMode != CaptureMetadataMode.None;
        using var cursor = observe ? CursorSnapshot.Capture() : null;
        Point point = observe ? cursor?.ScreenPosition ?? CaptureNative.CursorPosition() : Point.Empty;
        DateTimeOffset observedAt = DateTimeOffset.Now;
        Bitmap image = await WindowStillCapture.CaptureAsync(target.Handle, settings.IncludeCursor);
        try
        {
            Rectangle bounds = new(CaptureNative.RequireCaptureWindow(target).Location, image.Size);
            if (!observe) return (image, bounds, null);
            var pointer = new PointerObservation(observedAt, PixelPoint.From(point), cursor is not null, cursor is not null && settings.IncludeCursor);
            var context = await CaptureContextClient.Shared.ObserveAsync(point);
            var selected = context.PointerApplication.ProcessId == target.ProcessId && context.PointerApplication.Bounds == PixelBounds.From(bounds)
                ? context.PointerApplication : null;
            return (image, bounds, CaptureMetadata.Create(bounds, settings.ScreenshotPaddingPixels, DateTimeOffset.Now, pointer,
                context with { FocusAtSelectionStart = focusAtSelectionStart }, selected));
        }
        catch { image.Dispose(); throw; }
    }
}
