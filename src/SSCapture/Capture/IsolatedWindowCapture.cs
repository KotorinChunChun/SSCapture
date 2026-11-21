using SSCapture.Services;

namespace SSCapture.Capture;

internal static class IsolatedWindowCapture
{
    internal static async Task<(Bitmap Image, Rectangle Bounds, CaptureMetadata? Metadata)> TakeAsync(
        CaptureWindow target, AppSettings settings, ElementMetadata? focusAtSelectionStart = null, WindowCapturePreview? preview = null,
        IReadOnlyList<CaptureWindow>? foregroundFirst = null)
    {
        _ = CaptureNative.RequireCaptureWindow(target);
        bool observe = settings.DefaultDestination.HasFlag(OutputDestination.File) && settings.MetadataMode != CaptureMetadataMode.None;
        using var cursor = observe ? CursorSnapshot.Capture() : null;
        Point point = observe ? cursor?.ScreenPosition ?? CaptureNative.CursorPosition() : Point.Empty;
        DateTimeOffset observedAt = DateTimeOffset.Now;
        WindowCaptureFrame frame = preview is null ? await TakeGroupAsync(foregroundFirst ?? [target], settings.IncludeCursor) : await preview.TakeAsync();
        Bitmap image = frame.Image;
        try
        {
            Rectangle bounds = frame.Bounds;
            if (!observe) return (image, bounds, null);
            var pointer = new PointerObservation(observedAt, PixelPoint.From(point), cursor is not null, cursor is not null && settings.IncludeCursor);
            var context = await CaptureContextClient.Shared.ObserveAsync(point);
            var selected = context.PointerApplication.ProcessId == target.ProcessId && context.PointerApplication.Bounds == PixelBounds.From(bounds)
                ? context.PointerApplication : null;
            return (image, bounds, CaptureMetadata.Create(bounds, settings.ScreenshotPaddingPixels, frame.CapturedAt, pointer,
                context with { FocusAtSelectionStart = focusAtSelectionStart }, selected));
        }
        catch { image.Dispose(); throw; }
    }

    internal static async Task<WindowCaptureFrame> TakeGroupAsync(IReadOnlyList<CaptureWindow> foregroundFirst, bool includeCursor, CancellationToken token = default)
    {
        if (foregroundFirst.Count == 1) return await TakeFrameAsync(foregroundFirst[0], includeCursor, token).ConfigureAwait(false);

        var frames = new List<WindowCaptureFrame>(foregroundFirst.Count);
        try
        {
            foreach (CaptureWindow window in foregroundFirst)
                frames.Add(await TakeFrameAsync(window, includeCursor, token).ConfigureAwait(false));
            Rectangle bounds = frames.Select(frame => frame.Bounds).Aggregate(Rectangle.Union);
            return new(CaptureGeometry.ComposeWindows(bounds, frames), bounds, frames[0].CapturedAt);
        }
        finally { foreach (var frame in frames) frame.Image.Dispose(); }
    }

    internal static async Task<WindowCaptureFrame> TakeFrameAsync(CaptureWindow target, bool includeCursor, CancellationToken cancellationToken = default)
    {
        _ = CaptureNative.RequireCaptureWindow(target);
        Bitmap image = await WindowStillCapture.CaptureAsync(target.Handle, includeCursor, cancellationToken);
        try
        {
            return new(image, new(CaptureNative.RequireCaptureWindow(target).Location, image.Size), DateTimeOffset.Now);
        }
        catch { image.Dispose(); throw; }
    }
}

internal sealed record WindowCaptureFrame(Bitmap Image, Rectangle Bounds, DateTimeOffset CapturedAt);

// Each hover owns one frame until the pointer leaves the target or the user confirms it.
internal sealed class WindowCapturePreview(CaptureWindow target, bool includeCursor, IReadOnlyList<CaptureWindow> foregroundFirst) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Task<WindowCaptureFrame>? frame;
    private bool transferred;
    internal CaptureWindow Target { get; } = target;
    internal bool IncludeCursor { get; } = includeCursor;
    internal Task<WindowCaptureFrame> Frame => frame ??= LoadAsync(cancellation.Token);

    private async Task<WindowCaptureFrame> LoadAsync(CancellationToken token)
    {
        // Skip GPU setup for windows the pointer only passes over.
        await Task.Delay(80, token).ConfigureAwait(false);
        return await IsolatedWindowCapture.TakeGroupAsync(foregroundFirst, IncludeCursor, token).ConfigureAwait(false);
    }

    internal async Task<WindowCaptureFrame> TakeAsync()
    {
        WindowCaptureFrame result = await Frame;
        transferred = true;
        return result;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        try
        {
            if (!transferred && frame is not null) (await frame.ConfigureAwait(false)).Image.Dispose();
        }
        catch (Exception) when (frame is { IsFaulted: true } or { IsCanceled: true }) { }
        finally { cancellation.Dispose(); }
    }
}
