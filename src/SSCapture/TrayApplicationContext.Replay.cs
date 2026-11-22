using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private async Task TakeSameSettingsAsync()
    {
        if (exiting || busy || recorder is not null || pendingRecording is not null || WindowVisibility.RestorePending(namingForm)) return;
        busy = true; RefreshMenu();
        try
        {
            var recipe = recipes.Current ?? throw new InvalidOperationException("成功した静止画の記録がありません。先に撮影してください。");
            recipe.ValidateApplications(settings);
            AppSettings captureSettings = recipe.SettingsForReplay(settings);
            PrepareCaptureUi();
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            if (recipe.TracksWindows)
            {
                var window = await InstantCapture.TakeReplayWindowsAsync(recipe, captureSettings);
                using var windowImage = window.Image;
                StoreStatus(await InstantCapture.DeliverAsync(windowImage, captureSettings, capturedAt, output, regionName: "再撮影",
                    captureBounds: window.Bounds, metadata: window.Metadata, preserveOutputs: true, windowTitle: window.Title,
                    replaySource: window.Source));
                return;
            }
            var displays = RegionCoordinates.ReadDisplays();
            var regions = recipe.RegionsFor(displays);
            Rectangle bounds = regions?.Aggregate(Rectangle.Union) ?? recipe.Resolve(displays);
            if (recipe.RequiresScroll)
            {
                Point center = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                var target = CaptureNative.CaptureWindows(displays.Select(display => display.Bounds).Aggregate(Rectangle.Union))
                    .FirstOrDefault(window => window.Bounds.Contains(center));
                var scroll = await ScrollCapture.CollectAsync(bounds, captureSettings, target?.Handle ?? 0);
                if (scroll is not { } captureScroll) return;
                using var scrollImage = captureScroll.Image;
                StoreStatus(await InstantCapture.DeliverAsync(scrollImage, captureSettings, capturedAt, output, regionName: "再撮影",
                    captureBounds: bounds, metadata: captureScroll.Metadata, preserveOutputs: true,
                    replaySource: new CaptureReplaySource.Scroll(bounds)));
                return;
            }
            var capture = await InstantCapture.TakeWithContextAsync(bounds, captureSettings);
            using var raw = capture.Image;
            using var image = regions is null ? recipe.Shape(raw) : CaptureGeometry.ComposeRegions(raw, bounds, bounds, regions);
            StoreStatus(await InstantCapture.DeliverAsync(image, captureSettings, capturedAt, output, regionName: "再撮影",
                captureBounds: bounds, metadata: capture.Metadata, preserveOutputs: true, capturePolygon: recipe.PolygonFor(bounds),
                replaySource: regions is null ? null : new CaptureReplaySource.Monitors(regions)));
        }
        catch (Exception ex)
        {
            StoreStatus("直前の設定で再撮影できませんでした: " + ex.Message, true);
        }
        finally
        {
            busy = false;
            RestoreCaptureSettings();
            if (!exiting) { RefreshMenu(); FlushFeedback(); }
        }
    }

}
