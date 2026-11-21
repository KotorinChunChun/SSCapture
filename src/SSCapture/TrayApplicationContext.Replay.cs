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
            Rectangle bounds = recipe.IsolatedWindow is null ? recipe.Resolve(RegionCoordinates.ReadDisplays()) : Rectangle.Empty;
            PrepareCaptureUi();
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            if (recipe.IsolatedWindow is CaptureWindow target)
            {
                var window = await IsolatedWindowCapture.TakeAsync(target, captureSettings);
                using var windowImage = window.Image;
                StoreStatus(await InstantCapture.DeliverAsync(windowImage, captureSettings, capturedAt, output, regionName: "再撮影",
                    captureBounds: window.Bounds, metadata: window.Metadata, preserveOutputs: true,
                    windowTitle: target.Title, isolatedWindow: target));
                return;
            }
            var capture = await InstantCapture.TakeWithContextAsync(bounds, captureSettings);
            using var raw = capture.Image;
            using var image = recipe.Shape(raw);
            StoreStatus(await InstantCapture.DeliverAsync(image, captureSettings, capturedAt, output, regionName: "再撮影",
                captureBounds: bounds, metadata: capture.Metadata, preserveOutputs: true, capturePolygon: recipe.PolygonFor(bounds)));
        }
        catch (Exception ex) { StoreStatus("直前の設定で再撮影できませんでした: " + ex.Message, true); }
        finally
        {
            busy = false;
            if (!exiting) { RefreshMenu(); FlushFeedback(); }
        }
    }
}
