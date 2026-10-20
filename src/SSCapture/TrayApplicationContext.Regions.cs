using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private async Task TakeRegionAsync(string id)
    {
        if (exiting || WindowVisibility.RestorePending(namingForm))
            return;
        if (pendingRecording is not null)
        {
            WindowVisibility.RestorePending(recordingForm);
            return;
        }

        if (busy)
            return;
        busy = true;
        RefreshMenu();
        try
        {
            PrepareCaptureUi();
            var region = id == AppSettings.LastRegionKeyId ? lastRegion : settings.SavedRegions.FirstOrDefault(r => r.Id == id);
            if (region is null)
                throw new InvalidOperationException("撮影範囲がありません。先に矩形で撮影するか、範囲を登録してください。");
            Rectangle bounds = RegionCoordinates.Resolve(region, RegionCoordinates.ReadDisplays());
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            using var image = InstantCapture.Take(bounds, settings.IncludeCursor);
            StoreStatus(await InstantCapture.DeliverAsync(image, settings, capturedAt, output, regionName: region.Name, captureBounds: bounds));
        }
        catch (Exception ex)
        {
            StoreStatus("範囲を再撮影できませんでした: " + ex.Message, true);
        }
        finally
        {
            busy = false;
            if (!exiting)
            {
                RefreshMenu();
                FlushFeedback();
            }
        }
    }

    private void ShowRegions()
    {
        if (exiting || busy)
            return;
        if (regionForm is null || regionForm.IsDisposed)
        {
            regionForm = new RegionManagerForm(settings.SavedRegions, lastRegion, size => PickRegionAsync(size, settings.FixedCaptureAnchor), regions => SaveSettings(settings with { SavedRegions = regions }), selectAnchoredRegion: PickRegionAsync);
            regionForm.FormClosed += (_, _) => regionForm = null;
        }

        regionForm.UpdateLastRegion(lastRegion);
        WindowVisibility.Show(regionForm);
    }

    private async Task<Rectangle?> PickRegionAsync(Size? size, CaptureAnchor anchor)
    {
        if (busy || pendingRecording is not null)
            throw new InvalidOperationException("撮影・録画を終了してから範囲を選択してください。");
        busy = true;
        RefreshMenu();
        try
        {
            PrepareCaptureUi();
            using var outcome = await CaptureSession.SelectAsync(new CaptureOptions(size is null ? CaptureMode.Rectangle : CaptureMode.FixedRegion, CaptureIntent.Recording, OutputDestination.None, FixedSize: size, FixedAnchor: anchor, LimitToStartingMonitor: settings.LimitToStartingMonitor, Grid20: settings.Grid20, ShowSelectionGuide: settings.ShowSelectionGuide), SaveGrid20, SaveGuideVisibility);
            return outcome.Selection?.Bounds;
        }
        finally
        {
            busy = false;
            if (!exiting)
            {
                RefreshMenu();
                FlushFeedback();
            }
        }
    }
}
