using System.Drawing.Imaging;
using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed record DisplayTarget(string DeviceName, Rectangle Bounds, bool IsPrimary = false);
internal sealed record WindowReplayFrame(Bitmap Image, Rectangle Bounds, CaptureMetadata? Metadata, string Title, CaptureReplaySource.Windows Source);

internal static class InstantCapture
{
    internal static IReadOnlyList<DisplayTarget> OrderDisplays(IEnumerable<DisplayTarget> displays) => displays
        .Where(display => display.Bounds.Width > 0 && display.Bounds.Height > 0)
        .OrderByDescending(display => display.IsPrimary)
        .ThenBy(display => display.Bounds.Left).ThenBy(display => display.Bounds.Top)
        .ThenBy(display => display.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IReadOnlyList<DisplayTarget> ReadDisplays() => OrderDisplays(
        Screen.AllScreens.Select(screen => new DisplayTarget(screen.DeviceName, screen.Bounds, screen.Primary)));

    internal static Rectangle ResolveDisplay(int number, IEnumerable<DisplayTarget> displays)
    {
        if (number is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(number));
        var available = OrderDisplays(displays);
        if (number > available.Count)
            throw new InvalidOperationException($"画面{number}は接続されていません（接続中: {available.Count}画面）。");
        return available[number - 1].Bounds;
    }

    // No windows are shown, hidden or activated before reading these physical pixels.
    internal static Bitmap Take(int number, bool includeCursor = false)
    {
        Rectangle bounds = ResolveDisplay(number, ReadDisplays());
        return Take(bounds, includeCursor);
    }

    internal static Bitmap Take(Rectangle bounds, bool includeCursor = false)
    {
        using var cursor = includeCursor ? CursorSnapshot.Capture() : null;
        return Take(bounds, cursor);
    }

    private static Bitmap Take(Rectangle bounds, CursorSnapshot? cursor)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > RegionCoordinates.MaximumDimension
            || bounds.Height > RegionCoordinates.MaximumDimension || (long)bounds.Width * bounds.Height > RegionCoordinates.MaximumPixels
            || (long)bounds.X + bounds.Width > int.MaxValue || (long)bounds.Y + bounds.Height > int.MaxValue)
            throw new ArgumentException("即時撮影の座標または寸法が正しくありません。範囲を確認してください。", nameof(bounds));
        var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, DesktopSnapshot.CopyOperation);
            cursor?.Draw(image, bounds.Location);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static async Task<(Bitmap Image, CaptureMetadata? Metadata)> TakeWithContextAsync(Rectangle bounds, AppSettings settings)
    {
        bool enabled = settings.DefaultDestination.HasFlag(OutputDestination.File) && settings.MetadataMode != CaptureMetadataMode.None;
        if (!enabled) return (Take(bounds, settings.IncludeCursor), null);
        DateTimeOffset at = DateTimeOffset.Now;
        using var cursor = CursorSnapshot.Capture();
        Point point = cursor?.ScreenPosition ?? CaptureNative.CursorPosition();
        var pointer = new PointerObservation(at, PixelPoint.From(point), cursor is not null, cursor is not null && settings.IncludeCursor);
        Bitmap image = Take(bounds, settings.IncludeCursor ? cursor : null);
        try
        {
            var context = await CaptureContextClient.Shared.ObserveAsync(point);
            return (image, CaptureMetadata.Create(bounds, settings.ScreenshotPaddingPixels, at, pointer, context));
        }
        catch { image.Dispose(); throw; }
    }

    internal static async Task<WindowReplayFrame> TakeReplayWindowsAsync(LastCaptureRecipe recipe, AppSettings settings)
    {
        var monitors = CaptureNative.MonitorBounds();
        var windows = CaptureNative.CaptureWindows(monitors.Aggregate(Rectangle.Union));
        CaptureWindow[] roots = recipe.ResolveWindows(windows);
        CaptureWindow target = roots[0];
        var group = recipe.IncludeOwnedWindows
            ? roots.SelectMany(root => CaptureWindowGroups.ForegroundGroup(root, windows))
                .DistinctBy(window => window.Handle).OrderBy(window => window.ZOrder).ToArray()
            : roots;
        string title = roots.Length == 1 ? target.Title : $"ウィンドウ {roots.Length} 件";
        var source = new CaptureReplaySource.Windows(roots, recipe.UseIsolatedWindow, recipe.IncludeOwnedWindows);
        if (recipe.UseIsolatedWindow)
        {
            var window = await IsolatedWindowCapture.TakeAsync(target, settings, foregroundFirst: group, requiredWindows: roots);
            return new(window.Image, window.Bounds, window.Metadata, title, source);
        }

        var regions = group.SelectMany(window => monitors.Select(monitor => Rectangle.Intersect(window.Bounds, monitor)))
            .Where(CaptureGeometry.IsUsable).Distinct().ToArray();
        if (regions.Length == 0) throw new InvalidOperationException("撮影するウィンドウがモニター内にありません。");
        Rectangle bounds = regions.Aggregate(Rectangle.Union);
        var capture = await TakeWithContextAsync(bounds, settings);
        if (regions.Length == 1) return new(capture.Image, bounds, capture.Metadata, title, source);
        using var raw = capture.Image;
        return new(CaptureGeometry.ComposeRegions(raw, bounds, bounds, regions), bounds, capture.Metadata, title, source);
    }

    internal static async Task<string> DeliverAsync(Bitmap image, AppSettings settings, DateTimeOffset capturedAt, OutputRouter output,
        Func<Bitmap, string, CancellationToken, Task<OcrResult>>? recognize = null, string regionName = "撮影", Rectangle? captureBounds = null,
        CaptureMetadata? metadata = null, bool preserveOutputs = false, Action? onDelivered = null, IReadOnlyList<Point>? capturePolygon = null,
        string? windowTitle = null, CaptureReplaySource? replaySource = null)
    {
        OutputDestination destination = preserveOutputs ? settings.DefaultDestination : settings.DefaultDestination & (OutputDestination.Clipboard | OutputDestination.File);
        if (destination == OutputDestination.None) return "即時撮影の出力先がOFFです。クリップボードまたはファイル保存を有効にしてください。";
        string ocr = "", warning = "";
        if (destination.HasFlag(OutputDestination.File) && FileNamePattern.UsesOcr(settings.FileNamePattern))
        {
            OcrResult result;
            try { result = await Task.Run(() => (recognize ?? OneOcr.RecognizeAsync)(image, settings.OneOcrDirectory, CancellationToken.None)); }
            catch (Exception ex) { result = new OcrResult([], ex.Message); }
            ocr = FileNamePattern.CleanOcr(result.Lines.FirstOrDefault() ?? "");
            if (string.IsNullOrWhiteSpace(ocr) || result.Error is not null)
            {
                ocr = "OCRなし";
                warning = "OCRを取得できなかったためファイル名に「OCRなし」を使用しました。" + result.Error + Environment.NewLine;
            }
        }
        var plan = destination.HasFlag(OutputDestination.File) ? new FileNamePlan(settings.FileNamePattern, capturedAt, CaptureIntent.Screenshot, ocr, Region: regionName) : null;
        return warning + await output.ScreenshotAsync(image, destination, settings with { OpenFolderAfterSave = preserveOutputs && settings.OpenFolderAfterSave }, namePlan: plan, captureBounds: captureBounds, metadata: metadata,
            onDelivered: onDelivered, capturePolygon: capturePolygon, capturedAt: capturedAt, windowTitle: windowTitle, replaySource: replaySource);
    }
}
