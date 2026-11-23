using SSCapture.Capture;

namespace SSCapture.Services;

internal static class CaptureTimerOutput
{
    internal static CapturePreset InitialPeriodic(AppSettings settings)
    {
        CapturePreset template = settings.CapturePresets.FirstOrDefault(p => p.Id == "initial-output-file")
            ?? settings.CapturePresets.Single(p => p.IsDefault);
        return template with { Name = "ファイル保存のみ（予約用）", Destination = OutputDestination.File, OpenFolderAfterSave = false,
            ImageEditorId = null, VideoEditorId = null };
    }
}

internal sealed class CaptureTimerShot
{
    private readonly AppSettings settings;
    private readonly string targetName;
    private readonly Func<Task<Frame>> take;

    private sealed record Frame(Bitmap Image, Rectangle Bounds, CaptureMetadata? Metadata,
        IReadOnlyList<Point>? Polygon = null, string? WindowTitle = null, CaptureReplaySource? Source = null);

    private CaptureTimerShot(AppSettings settings, string targetName, Func<AppSettings, Task<Frame>> take)
    {
        this.settings = CaptureSettingsSnapshot.Copy(settings);
        this.targetName = targetName;
        this.take = () => take(this.settings);
    }

    internal static CaptureTimerShot Fixed(Rectangle bounds, ResolvedCaptureCommand command,
        Func<Rectangle, AppSettings, Task<(Bitmap Image, CaptureMetadata? Metadata)>>? take = null)
    {
        CaptureCommandLine.ValidateRegion(bounds);
        return new(command.Settings, command.InputPreset.Name, async settings =>
        {
            if (take is null && !CaptureGeometry.IsFullyCovered(bounds, CaptureNative.MonitorBounds()))
                throw new InvalidOperationException("指定した固定範囲に画面外の座標があります。");
            var frame = await (take ?? InstantCapture.TakeWithContextAsync)(bounds, settings);
            return new(frame.Image, bounds, frame.Metadata);
        });
    }

    internal static CaptureTimerShot Registered(SavedRegion region, AppSettings settings) =>
        new(settings, region.Name, current => TakeRegionAsync(RegionCoordinates.Resolve(region, RegionCoordinates.ReadDisplays()), current));

    internal static CaptureTimerShot Window(ResolvedCaptureCommand command, Func<WindowCaptureCommand, CaptureWindow>? resolveWindow,
        Func<CaptureWindow, AppSettings, bool, bool, Task<WindowReplayFrame>>? takeWindow)
    {
        var request = command.Request.Window!;
        var target = (resolveWindow ?? (window => window.Resolve()))(request);
        return new(command.Settings, command.InputPreset.Name, async settings =>
        {
            var frame = await (takeWindow ?? InstantCapture.TakeWindowAsync)(target, settings, true, request.ClientAreaOnly);
            return new(frame.Image, frame.Bounds, frame.Metadata, WindowTitle: frame.Title, Source: frame.Source);
        });
    }

    internal static CaptureTimerShot Monitor(DisplayLayout display, AppSettings settings, IReadOnlyList<DisplayLayout> displays) =>
        FromSelection(new(display.Bounds, null, settings.DefaultDestination, ReplaySource: new CaptureReplaySource.Monitors([display.Bounds])),
            settings, display.Name, displays);

    internal static CaptureTimerShot FromSelection(CaptureSelection selection, AppSettings settings, string name,
        IReadOnlyList<DisplayLayout>? displays = null)
    {
        var recipe = LastCaptureRecipe.Create(0, selection.Bounds, selection.Polygon, settings,
            displays ?? (selection.ReplaySource is CaptureReplaySource.Windows or CaptureReplaySource.Control or CaptureReplaySource.ControlGroup ? [] : RegionCoordinates.ReadDisplays()), selection.ReplaySource);
        return new(settings, name, current => TakeTrackedAsync(recipe, current));
    }

    private static async Task<Frame> TakeRegionAsync(Rectangle bounds, AppSettings settings)
    {
        var capture = await InstantCapture.TakeWithContextAsync(bounds, settings);
        return new(capture.Image, bounds, capture.Metadata);
    }

    private static async Task<Frame> TakeTrackedAsync(LastCaptureRecipe recipe, AppSettings settings)
    {
        if (recipe.TracksWindows)
        {
            var window = await InstantCapture.TakeReplayWindowsAsync(recipe, settings, requireOriginalWindow: true);
            return new(window.Image, window.Bounds, window.Metadata, WindowTitle: window.Title, Source: window.Source);
        }
        var displays = RegionCoordinates.ReadDisplays();
        var regions = recipe.RegionsFor(displays);
        Rectangle bounds = regions?.Aggregate(Rectangle.Union) ?? recipe.Resolve(displays);
        IReadOnlyList<Point>? polygon = recipe.PolygonFor(bounds);
        var frame = await TakeRegionAsync(bounds, settings);
        if (regions is null && polygon is null) return frame;
        using var raw = frame.Image;
        Bitmap image = regions is not null ? CaptureGeometry.ComposeRegions(raw, bounds, bounds, regions)
            : CaptureGeometry.Freeform(raw, bounds, polygon!) ?? throw new InvalidOperationException("予約した自由形状を撮影できません。");
        return frame with { Image = image, Polygon = polygon, Source = regions is null ? null : new CaptureReplaySource.Monitors(regions) };
    }

    internal async Task<string> DeliverAsync(OutputRouter output, DateTimeOffset capturedAt,
        Func<Bitmap, string, CancellationToken, Task<OcrResult>>? recognize = null)
    {
        Frame frame = await take();
        using var image = frame.Image;
        bool delivered = false;
        string result = await InstantCapture.DeliverAsync(image, settings, capturedAt, output, recognize,
            regionName: targetName, captureBounds: frame.Bounds, metadata: frame.Metadata, preserveOutputs: true,
            onDelivered: () => delivered = true, capturePolygon: frame.Polygon, windowTitle: frame.WindowTitle, replaySource: frame.Source);
        if (!delivered) throw new InvalidOperationException("撮影画像を出力できませんでした。");
        return result;
    }
}
