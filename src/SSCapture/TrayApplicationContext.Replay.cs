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
            Rectangle bounds = recipe.Resolve(RegionCoordinates.ReadDisplays());
            AppSettings captureSettings = recipe.Settings with { KeepHistory = settings.KeepHistory, OneOcrDirectory = settings.OneOcrDirectory,
                NotificationDurationSeconds = settings.NotificationDurationSeconds, PlayCaptureSound = settings.PlayCaptureSound,
                NotificationApplicationId = settings.NotificationApplicationId, NotificationCorner = settings.NotificationCorner,
                NotificationDisplay = settings.NotificationDisplay, HoldNotificationWhileApproaching = settings.HoldNotificationWhileApproaching };
            PrepareCaptureUi();
            DateTimeOffset capturedAt = DateTimeOffset.Now;
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
