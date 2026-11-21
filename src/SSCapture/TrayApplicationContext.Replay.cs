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
            CaptureWindow? target = recipe.IsolatedWindow ?? recipe.WindowRoots?.FirstOrDefault();
            if (target is not null)
            {
                var windows = CaptureNative.CaptureWindows(CaptureNative.MonitorBounds().Aggregate(Rectangle.Union));
                var group = (recipe.WindowRoots ?? [target]).SelectMany(root => CaptureWindowGroups.ForegroundGroup(root, windows))
                    .DistinctBy(window => window.Handle).OrderBy(window => window.ZOrder).ToArray();
                var window = await IsolatedWindowCapture.TakeAsync(target, captureSettings, foregroundFirst: group);
                using var windowImage = window.Image;
                StoreStatus(await InstantCapture.DeliverAsync(windowImage, captureSettings, capturedAt, output, regionName: "再撮影",
                    captureBounds: window.Bounds, metadata: window.Metadata, preserveOutputs: true,
                    windowTitle: recipe.WindowRoots is { Count: > 1 } roots ? $"ウィンドウ {roots.Count} 件" : target.Title,
                    isolatedWindow: recipe.IsolatedWindow, composite: recipe.WindowRoots is { } selected ? new(WindowRoots: selected) : null));
                return;
            }
            var displays = RegionCoordinates.ReadDisplays();
            var regions = recipe.RegionsFor(displays);
            Rectangle bounds = regions?.Aggregate(Rectangle.Union) ?? recipe.Resolve(displays);
            var capture = await InstantCapture.TakeWithContextAsync(bounds, captureSettings);
            using var raw = capture.Image;
            using var image = regions is null ? recipe.Shape(raw) : CaptureGeometry.ComposeRegions(raw, bounds, bounds, regions);
            StoreStatus(await InstantCapture.DeliverAsync(image, captureSettings, capturedAt, output, regionName: "再撮影",
                captureBounds: bounds, metadata: capture.Metadata, preserveOutputs: true, capturePolygon: recipe.PolygonFor(bounds),
                composite: regions is null ? null : new(Regions: regions)));
        }
        catch (Exception ex) { StoreStatus("直前の設定で再撮影できませんでした: " + ex.Message, true); }
        finally
        {
            busy = false;
            RestoreCaptureSettings();
            if (!exiting) { RefreshMenu(); FlushFeedback(); }
        }
    }
}
