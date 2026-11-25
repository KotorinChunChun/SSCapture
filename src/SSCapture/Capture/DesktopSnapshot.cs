using System.Diagnostics;
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
    internal CursorSnapshot? StartCursor { get; }
    internal FrozenCaptureTargets? FrozenTargets { get; }
    internal Task FrozenControlsReady { get; private set; } = Task.CompletedTask;

    /// <summary>Takes ownership of image. Also used with synthetic pixels for isolated overlay checks.</summary>
    internal DesktopSnapshot(Bitmap image, Rectangle bounds, IReadOnlyList<Rectangle> monitors,
        FrozenCaptureTargets? frozenTargets = null, CursorSnapshot? startCursor = null)
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
        StartCursor = startCursor;
    }

    // Two bounded UIA passes bracket a single immutable pixel capture.
    private const int LayoutPassMilliseconds = 450;

    internal static async Task<DesktopSnapshot> TakeFrozenSelectionAsync(bool includeCursor, bool observePointer)
    {
        var monitors = CaptureNative.MonitorBounds();
        Rectangle desktop = monitors.Aggregate(Rectangle.Union);
        if (CaptureNative.ReadFrozenWindows(desktop, includeControls: false) is not { } windows)
            return Take(includeCursor, observePointer, captureTargets: true, keepCursor: true);

        Point pointer = CaptureNative.CursorPosition();
        nint foreground = CaptureNative.GetForegroundWindow();
        var candidates = FrozenCaptureTargets.VisibleBrowsers(windows, monitors)
            .OrderByDescending(window => window.Target.Handle == foreground)
            .ThenByDescending(window => window.Target.Bounds.Contains(pointer)).ToArray();
        var before = await ObserveLayoutsAsync(candidates);
        var snapshot = Take(includeCursor, observePointer, captureTargets: true, keepCursor: true);
        if (snapshot.FrozenTargets is { } targets && before.Count != 0)
            snapshot.FrozenControlsReady = Task.Run(() => VerifyLayoutsAsync(targets, before));
        return snapshot;
    }

    private static async Task VerifyLayoutsAsync(FrozenCaptureTargets targets, Dictionary<FrozenWindow, ControlLayout> before)
    {
        var after = await ObserveLayoutsAsync(before.Keys);
        foreach (var pair in before)
        {
            var window = pair.Key;
            if (after.TryGetValue(window, out var layout))
                targets.SetBrowserControls(window.Target, window.FullBounds, pair.Value, layout);
        }
    }

    private static async Task<Dictionary<FrozenWindow, ControlLayout>> ObserveLayoutsAsync(IEnumerable<FrozenWindow> windows)
    {
        var observed = new Dictionary<FrozenWindow, ControlLayout>();
        var clock = Stopwatch.StartNew();
        foreach (var window in windows)
        {
            int remaining = LayoutPassMilliseconds - (int)clock.ElapsedMilliseconds;
            if (remaining < 75) break;
            var target = window.Target;
            try
            {
                if (CaptureNative.RequireCaptureWindow(target) != window.FullBounds) continue;
                // Leave another target a chance to respond when a provider stalls.
                int timeout = Math.Min(remaining, CaptureContextClient.Shared.IsStarted ? 250 : 425);
                var layout = await CaptureContextClient.Shared.FindFrozenControlLayoutAsync(
                    new(target.Handle.ToInt64(), target.ProcessId, PixelBounds.From(window.FullBounds), null), timeout);
                if (layout is { Incomplete: false, Entries.Length: > 0 } && CaptureNative.RequireCaptureWindow(target) == window.FullBounds)
                    observed.Add(window, layout);
            }
            catch (InvalidOperationException) { }
        }
        return observed;
    }

    internal static DesktopSnapshot Take(bool includeCursor = false, bool observePointer = false, bool captureTargets = false,
        bool keepCursor = false)
    {
        IReadOnlyList<Rectangle> monitors = CaptureNative.MonitorBounds();
        Rectangle desktop = monitors.Aggregate(Rectangle.Union);
        var before = captureTargets ? CaptureNative.ReadFrozenWindows(desktop) : null;
        DateTimeOffset pointerAt = DateTimeOffset.Now;
        using CursorSnapshot? cursor = includeCursor || observePointer || keepCursor ? CursorSnapshot.Capture() : null;
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
            return new DesktopSnapshot(image, desktop, monitors, frozenTargets, keepCursor ? cursor?.Copy() : null)
                { CapturedAt = pointerAt, Pointer = pointer };
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Image.Dispose();
        StartCursor?.Dispose();
    }
}
