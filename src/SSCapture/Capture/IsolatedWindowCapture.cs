using System.Drawing.Imaging;
using SSCapture.Services;

namespace SSCapture.Capture;

internal static class IsolatedWindowCapture
{
    internal static async Task<(Bitmap Image, Rectangle Bounds, CaptureMetadata? Metadata)> TakeAsync(
        CaptureWindow target, AppSettings settings, ElementMetadata? focusAtSelectionStart = null, WindowCapturePreview? preview = null,
        IReadOnlyList<CaptureWindow>? foregroundFirst = null, IReadOnlyList<CaptureWindow>? requiredWindows = null,
        bool clientAreaOnly = false)
    {
        _ = CaptureNative.RequireCaptureWindow(target);
        bool observe = settings.DefaultDestination.HasFlag(OutputDestination.File) && settings.MetadataMode != CaptureMetadataMode.None;
        using var cursor = observe ? CursorSnapshot.Capture() : null;
        Point point = observe ? cursor?.ScreenPosition ?? CaptureNative.CursorPosition() : Point.Empty;
        DateTimeOffset observedAt = DateTimeOffset.Now;
        WindowCaptureFrame frame = await CaptureOrReuseAsync();
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

        async Task<WindowCaptureFrame> CaptureOrReuseAsync()
        {
            if (clientAreaOnly) return await TakeClientFrameAsync(target, settings.IncludeCursor);
            if (preview is not null)
            {
                try { return await preview.TakeAsync(); }
                catch (Exception ex) when (IsCaptureFailure(ex)) { /* Try a fresh capture after a failed hover. */ }
            }
            return await TakeGroupAsync(foregroundFirst ?? [target], settings.IncludeCursor,
                required: (requiredWindows ?? [target]).Select(window => window.Handle).ToHashSet());
        }
    }

    internal static async Task<WindowCaptureFrame> TakeGroupAsync(IReadOnlyList<CaptureWindow> foregroundFirst, bool includeCursor,
        CancellationToken token = default, IReadOnlySet<nint>? required = null,
        Func<CaptureWindow, bool, CancellationToken, Task<WindowCaptureFrame>>? takeFrame = null)
    {
        takeFrame ??= TakeFrameAsync;
        if (foregroundFirst.Count == 1) return await takeFrame(foregroundFirst[0], includeCursor, token).ConfigureAwait(false);

        var frames = new List<WindowCaptureFrame>(foregroundFirst.Count);
        try
        {
            foreach (CaptureWindow window in foregroundFirst)
            {
                try { frames.Add(await takeFrame(window, includeCursor, token).ConfigureAwait(false)); }
                catch (Exception ex) when (required is not null && !required.Contains(window.Handle) && IsCaptureFailure(ex)) { }
            }
            Rectangle bounds = frames.Select(frame => frame.Bounds).Aggregate(Rectangle.Union);
            return new(CaptureGeometry.ComposeWindows(bounds, frames), bounds, frames[0].CapturedAt);
        }
        finally { foreach (var frame in frames) frame.Image.Dispose(); }
    }

    internal static async Task<(Bitmap Image, CaptureMetadata? Metadata)> TakeControlsAsync(
        IReadOnlyList<CaptureControlTarget> targets, AppSettings settings, ElementMetadata? focusAtSelectionStart = null,
        Func<CaptureWindow, bool, CancellationToken, Task<WindowCaptureFrame>>? takeFrame = null,
        Func<Rectangle, Bitmap>? takeBackground = null)
    {
        bool observe = settings.DefaultDestination.HasFlag(OutputDestination.File) && settings.MetadataMode != CaptureMetadataMode.None;
        using var cursor = observe ? CursorSnapshot.Capture() : null;
        Point point = observe ? cursor?.ScreenPosition ?? CaptureNative.CursorPosition() : Point.Empty;
        DateTimeOffset observedAt = DateTimeOffset.Now;
        takeFrame ??= TakeFrameAsync;
        Rectangle bounds = targets.Select(target => target.Bounds).Aggregate(Rectangle.Union);
        var groups = targets.GroupBy(target => target.Window.Handle).OrderByDescending(group => group.First().Window.ZOrder).ToArray();
        var result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        DateTimeOffset capturedAt = observedAt;
        try
        {
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.Clear(Color.Transparent);
                if (groups.Length > 1)
                {
                    using var background = takeBackground is null ? InstantCapture.Take(bounds, includeCursor: false) : takeBackground(bounds);
                    graphics.DrawImageUnscaled(background, Point.Empty);
                }
                foreach (var group in groups)
                {
                    CaptureWindow window = group.First().Window;
                    WindowCaptureFrame frame = await takeFrame(window, settings.IncludeCursor, CancellationToken.None);
                    using var raw = frame.Image;
                    capturedAt = frame.CapturedAt;
                    if (frame.Bounds.Location != window.Bounds.Location || group.Any(target => !frame.Bounds.Contains(target.Bounds)))
                        throw new InvalidOperationException("対象の位置が変わりました。もう一度選び直してください。");
                    graphics.DrawImageUnscaled(raw, frame.Bounds.X - bounds.X, frame.Bounds.Y - bounds.Y);
                }
            }

            if (!observe) return (result, null);
            var context = await CaptureContextClient.Shared.ObserveAsync(point);
            var pointer = new PointerObservation(observedAt, PixelPoint.From(point), cursor is not null, cursor is not null && settings.IncludeCursor);
            return (result, CaptureMetadata.Create(bounds, settings.ScreenshotPaddingPixels, capturedAt, pointer,
                context with { FocusAtSelectionStart = focusAtSelectionStart }));
        }
        catch { result.Dispose(); throw; }
    }

    internal static bool IsCaptureFailure(Exception ex) => ex is InvalidOperationException or TimeoutException or NotSupportedException or UnauthorizedAccessException;

    internal static async Task<WindowCaptureFrame> TakeClientFrameAsync(CaptureWindow target, bool includeCursor,
        CancellationToken cancellationToken = default)
    {
        Rectangle frameBounds = CaptureNative.RequireCaptureWindow(target);
        Rectangle clientBounds = CaptureNative.RequireClientBounds(target);
        using var image = await WindowStillCapture.CaptureAsync(target, includeCursor, cancellationToken);
        if (CaptureNative.RequireCaptureWindow(target) != frameBounds || CaptureNative.RequireClientBounds(target) != clientBounds)
            throw new InvalidOperationException("撮影中にウィンドウの描画領域が変わりました。もう一度撮影してください。");
        return new(CropClientFrame(image, frameBounds, clientBounds), clientBounds, DateTimeOffset.Now);
    }

    internal static Bitmap CropClientFrame(Bitmap image, Rectangle frameBounds, Rectangle clientBounds)
    {
        if (frameBounds.Size != image.Size || !frameBounds.Contains(clientBounds)
            || CaptureGeometry.Crop(image, frameBounds, clientBounds, minimumSize: 1) is not { } client)
            throw new InvalidOperationException("描画領域とウィンドウ画像の位置や寸法が一致しません。もう一度撮影してください。");
        return client;
    }

    internal static async Task<WindowCaptureFrame> TakeFrameAsync(CaptureWindow target, bool includeCursor, CancellationToken cancellationToken = default)
    {
        _ = CaptureNative.RequireCaptureWindow(target);
        Bitmap image = await WindowStillCapture.CaptureAsync(target, includeCursor, cancellationToken);
        try
        {
            return new(image, new(CaptureNative.RequireCaptureWindow(target).Location, image.Size), DateTimeOffset.Now);
        }
        catch { image.Dispose(); throw; }
    }
}

internal sealed record WindowCaptureFrame(Bitmap Image, Rectangle Bounds, DateTimeOffset CapturedAt);

// Each hover owns one frame until the pointer leaves the target or the user confirms it.
internal sealed class WindowCapturePreview(CaptureWindow target, bool includeCursor, IReadOnlyList<CaptureWindow> foregroundFirst,
    bool clientAreaOnly = false, Func<WindowCaptureFrame>? frozenFrame = null) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Task<WindowCaptureFrame>? frame;
    private bool transferred;
    internal CaptureWindow Target { get; } = target;
    internal IReadOnlyList<CaptureWindow> Windows { get; } = foregroundFirst;
    internal bool IncludeCursor { get; } = includeCursor;
    internal bool ClientAreaOnly { get; } = clientAreaOnly;
    internal Task<WindowCaptureFrame> Frame => frame ??= LoadAsync(cancellation.Token);

    private async Task<WindowCaptureFrame> LoadAsync(CancellationToken token)
    {
        if (frozenFrame is not null) return frozenFrame();
        // Skip GPU setup for windows the pointer only passes over.
        await Task.Delay(80, token).ConfigureAwait(false);
        if (ClientAreaOnly) return await IsolatedWindowCapture.TakeClientFrameAsync(Target, IncludeCursor, token).ConfigureAwait(false);
        return await IsolatedWindowCapture.TakeGroupAsync(Windows, IncludeCursor, token,
            new HashSet<nint> { Target.Handle }).ConfigureAwait(false);
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
