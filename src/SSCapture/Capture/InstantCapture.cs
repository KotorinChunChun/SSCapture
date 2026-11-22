using System.Drawing.Imaging;
using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed record DisplayTarget(string DeviceName, Rectangle Bounds, bool IsPrimary = false, string? MonitorName = null);
internal sealed record WindowReplayFrame(Bitmap Image, Rectangle Bounds, CaptureMetadata? Metadata, string? Title, CaptureReplaySource Source);

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

    internal static DisplayTarget ResolveDisplay(DisplayTarget selected, IEnumerable<DisplayTarget> displays) =>
        OrderDisplays(displays).FirstOrDefault(display =>
            StringComparer.OrdinalIgnoreCase.Equals(display.DeviceName, selected.DeviceName))
        ?? throw new InvalidOperationException("選択したモニターの接続を確認できませんでした。");

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

    internal static async Task<WindowReplayFrame> TakeReplayWindowsAsync(LastCaptureRecipe recipe, AppSettings settings, bool requireOriginalWindow = false)
    {
        var monitors = CaptureNative.MonitorBounds();
        var windows = CaptureNative.CaptureWindows(monitors.Aggregate(Rectangle.Union));
        CaptureWindow[] roots = recipe.ResolveWindows(windows, requireOriginalWindow: requireOriginalWindow);
        CaptureWindow target = roots[0];
        if (recipe.ControlGroup is { } controlGroup)
            return await TakeReplayControlsAsync(controlGroup, roots, monitors, recipe.UseIsolatedWindow, settings);
        if (recipe.TracksControl)
        {
            const string unavailable = "再撮影するコントロールを特定できません。選び直してください。";
            if (recipe.ControlIdentity is not { } identity) throw new InvalidOperationException(unavailable);
            Rectangle windowBounds = CaptureNative.RequireCaptureWindow(target);
            var hit = await CaptureContextClient.Shared.FindControlAsync(new(target.Handle.ToInt64(), target.ProcessId,
                PixelBounds.From(windowBounds), null, identity));
            if (hit is not { Bounds: var pixels }) throw new InvalidOperationException(unavailable);
            Rectangle controlBounds = new(pixels.X, pixels.Y, pixels.Width, pixels.Height);
            if (!windowBounds.Contains(controlBounds)
                || !recipe.UseIsolatedWindow && !CaptureGeometry.IsFullyCovered(controlBounds, monitors)
                || CaptureNative.RequireCaptureWindow(target) != windowBounds)
                throw new InvalidOperationException(unavailable);
            var controlSource = new CaptureReplaySource.Control(target, identity, recipe.UseIsolatedWindow) { TargetMargin = recipe.TargetMargin };
            if (!recipe.UseIsolatedWindow)
            {
                Rectangle edited = ResolveDesktopMargin(controlBounds, recipe.TargetMargin ?? 0, monitors, 1);
                var controlCapture = await TakeWithContextAsync(edited, settings);
                return new(controlCapture.Image, edited, controlCapture.Metadata, null, controlSource);
            }
            var isolated = await IsolatedWindowCapture.TakeAsync(target, settings);
            return CropIsolatedReplay(isolated, controlBounds, recipe.TargetMargin ?? 0, 1, settings.ScreenshotPaddingPixels, null, controlSource);
        }
        var group = recipe.IncludeOwnedWindows
            ? roots.SelectMany(root => CaptureWindowGroups.ForegroundGroup(root, windows))
                .DistinctBy(window => window.Handle).OrderBy(window => window.ZOrder).ToArray()
            : roots;
        string title = roots.Length == 1 ? target.Title : $"ウィンドウ {roots.Length} 件";
        var source = new CaptureReplaySource.Windows(roots, recipe.UseIsolatedWindow, recipe.IncludeOwnedWindows, recipe.IsSingleWindowSelection)
            { TargetMargin = recipe.TargetMargin };
        if (recipe.UseIsolatedWindow)
        {
            var window = await IsolatedWindowCapture.TakeAsync(target, settings, foregroundFirst: group, requiredWindows: roots);
            return recipe.TargetMargin is int margin
                ? CropIsolatedReplay(window, window.Bounds, margin, CaptureGeometry.MinimumSize, settings.ScreenshotPaddingPixels, title, source)
                : new(window.Image, window.Bounds, window.Metadata, title, source);
        }

        if (recipe.TargetMargin is int offset)
        {
            Rectangle edited = ResolveDesktopMargin(target.Bounds, offset, monitors, CaptureGeometry.MinimumSize);
            var adjustedCapture = await TakeWithContextAsync(edited, settings);
            return new(adjustedCapture.Image, edited, adjustedCapture.Metadata, title, source);
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

    private static async Task<WindowReplayFrame> TakeReplayControlsAsync(
        IReadOnlyList<(int WindowIndex, ControlIdentity? Identity)> group, CaptureWindow[] windows,
        IReadOnlyList<Rectangle> monitors, bool isolated, AppSettings settings)
    {
        const string unavailable = "再撮影するコントロールを特定できません。選び直してください。";
        if (group.Count == 0 || group.Any(target => target.Identity is null) || group.Distinct().Count() != group.Count)
            throw new InvalidOperationException(unavailable);

        var observed = windows.Select(window => window with { Bounds = CaptureNative.RequireCaptureWindow(window) }).ToArray();
        var targets = new List<CaptureControlTarget>(group.Count);
        foreach (var target in group)
        {
            var window = observed[target.WindowIndex];
            var hit = await CaptureContextClient.Shared.FindControlAsync(new(window.Handle.ToInt64(), window.ProcessId,
                PixelBounds.From(window.Bounds), null, target.Identity));
            if (hit is not { Bounds: var pixels }) throw new InvalidOperationException(unavailable);
            var bounds = new Rectangle(pixels.X, pixels.Y, pixels.Width, pixels.Height);
            if (!window.Bounds.Contains(bounds) || !CaptureGeometry.IsFullyCovered(bounds, monitors))
                throw new InvalidOperationException(unavailable);
            targets.Add(new(window, bounds, target.Identity));
        }
        if (observed.Any(window => CaptureNative.RequireCaptureWindow(window) != window.Bounds))
            throw new InvalidOperationException(unavailable);

        var regions = targets.Select(target => target.Bounds).Distinct().ToArray();
        Rectangle canvas = regions.Aggregate(Rectangle.Union);
        var source = new CaptureReplaySource.ControlGroup(targets, isolated);
        string title = $"コントロール {targets.Count} 件";
        if (isolated)
        {
            var capture = await IsolatedWindowCapture.TakeControlsAsync(targets, settings);
            return new(capture.Image, canvas, capture.Metadata, title, source);
        }
        var desktop = await TakeWithContextAsync(canvas, settings);
        using var raw = desktop.Image;
        return new(CaptureGeometry.ComposeRegions(raw, canvas, canvas, regions), canvas, desktop.Metadata, title, source);
    }

    internal static Rectangle ResolveDesktopMargin(Rectangle original, int margin, IReadOnlyList<Rectangle> monitors, int minimumSize) =>
        CaptureTargetMargin.Apply(original, margin, monitors.Aggregate(Rectangle.Union), minimumSize, monitors).Bounds;

    internal static WindowReplayFrame CropIsolatedReplay((Bitmap Image, Rectangle Bounds, CaptureMetadata? Metadata) frame,
        Rectangle original, int margin, int minimumSize, int padding, string? title, CaptureReplaySource source)
    {
        using var raw = frame.Image;
        if (!frame.Bounds.Contains(original))
            throw new InvalidOperationException("再撮影する対象がウィンドウ画像の外にあります。選び直してください。");
        Rectangle bounds = CaptureTargetMargin.Apply(original, margin, frame.Bounds, minimumSize).Bounds;
        var metadata = frame.Metadata?.WithCrop(bounds).WithPadding(padding);
        return new(CaptureTargetMargin.CropFrame(raw, frame.Bounds, bounds, minimumSize), bounds, metadata, title, source);
    }

    internal static async Task<string> DeliverAsync(Bitmap image, AppSettings settings, DateTimeOffset capturedAt, OutputRouter output,
        Func<Bitmap, string, CancellationToken, Task<OcrResult>>? recognize = null, string regionName = "撮影", Rectangle? captureBounds = null,
        CaptureMetadata? metadata = null, bool preserveOutputs = false, Action? onDelivered = null, IReadOnlyList<Point>? capturePolygon = null,
        string? windowTitle = null, CaptureReplaySource? replaySource = null)
    {
        OutputDestination destination = preserveOutputs ? settings.DefaultDestination : settings.DefaultDestination & (OutputDestination.Clipboard | OutputDestination.File);
        if (destination == OutputDestination.None) return "即時撮影の出力先がOFFです。クリップボードまたはファイル保存を有効にしてください。";
        string warning = "";
        var plan = new FileNamePlan(settings.FileNamePattern, capturedAt, CaptureIntent.Screenshot, Region: regionName);
        bool openFolder = preserveOutputs && settings.OpenFolderAfterSave;
        Func<Bitmap, Task<ScreenshotFileName?>>? requestName = null;
        if (destination.HasFlag(OutputDestination.File) && FileNamePattern.UsesOcr(settings.FileNamePattern))
        {
            requestName = async namingImage =>
            {
                OcrResult result;
                try { result = await Task.Run(() => (recognize ?? OneOcr.RecognizeAsync)(namingImage, settings.OneOcrDirectory, CancellationToken.None)); }
                catch (Exception ex) { result = new OcrResult([], ex.Message); }
                string ocr = FileNamePattern.CleanOcr(result.Lines.FirstOrDefault() ?? "");
                if (string.IsNullOrWhiteSpace(ocr) || result.Error is not null)
                {
                    ocr = "OCRなし";
                    warning = "OCRを取得できなかったためファイル名に「OCRなし」を使用しました。" + result.Error + Environment.NewLine;
                }
                return new(null, plan with { Ocr = ocr }, openFolder);
            };
        }
        string message = await output.ScreenshotAsync(image, destination, settings with { OpenFolderAfterSave = openFolder },
            namePlan: plan, captureBounds: captureBounds, metadata: metadata, onDelivered: onDelivered,
            capturePolygon: capturePolygon, capturedAt: capturedAt, windowTitle: windowTitle,
            replaySource: replaySource, requestFileNameForImage: requestName, useSourceForNaming: true);
        return warning + message;
    }
}
