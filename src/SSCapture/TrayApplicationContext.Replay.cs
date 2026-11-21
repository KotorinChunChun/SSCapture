using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private async Task TakeSameSettingsAsync(bool useCurrentOutputs = false)
    {
        if (exiting || busy || recorder is not null || pendingRecording is not null || WindowVisibility.RestorePending(namingForm)) return;
        busy = true; RefreshMenu();
        try
        {
            var recipe = recipes.Current ?? throw new InvalidOperationException("成功した静止画の記録がありません。先に撮影してください。");
            if (!useCurrentOutputs) recipe.ValidateApplications(settings);
            AppSettings captureSettings = useCurrentOutputs ? settings : recipe.SettingsForReplay(settings);
            PrepareCaptureUi();
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            if (recipe.TracksWindows)
            {
                var window = await InstantCapture.TakeReplayWindowsAsync(recipe, captureSettings);
                using var windowImage = window.Image;
                StoreStatus(await InstantCapture.DeliverAsync(windowImage, captureSettings, capturedAt, output, regionName: "再撮影",
                    captureBounds: window.Bounds, metadata: window.Metadata, preserveOutputs: !useCurrentOutputs, windowTitle: window.Title,
                    replaySource: window.Source));
                return;
            }
            var displays = RegionCoordinates.ReadDisplays();
            var regions = recipe.RegionsFor(displays);
            Rectangle bounds = regions?.Aggregate(Rectangle.Union) ?? recipe.Resolve(displays);
            var capture = await InstantCapture.TakeWithContextAsync(bounds, captureSettings);
            using var raw = capture.Image;
            using var image = regions is null ? recipe.Shape(raw) : CaptureGeometry.ComposeRegions(raw, bounds, bounds, regions);
            StoreStatus(await InstantCapture.DeliverAsync(image, captureSettings, capturedAt, output, regionName: "再撮影",
                captureBounds: bounds, metadata: capture.Metadata, preserveOutputs: !useCurrentOutputs, capturePolygon: recipe.PolygonFor(bounds),
                replaySource: regions is null ? null : new CaptureReplaySource.Monitors(regions)));
        }
        catch (Exception ex)
        {
            string action = useCurrentOutputs ? "前回の範囲を再撮影" : "直前の設定で再撮影";
            StoreStatus(action + "できませんでした: " + ex.Message, true);
        }
        finally
        {
            busy = false;
            RestoreCaptureSettings();
            if (!exiting) { RefreshMenu(); FlushFeedback(); }
        }
    }

}
