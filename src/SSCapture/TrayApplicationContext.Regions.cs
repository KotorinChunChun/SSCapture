using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private async Task TakeRegionAsync(SavedRegion region, ResolvedPresetCapture? presetCapture = null)
    {
        if (!region.UseFixedCoordinates)
        {
            await TakeCaptureAsync(region.Mode, CaptureIntent.Screenshot, region, presetCapture: presetCapture);
            return;
        }
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
            Rectangle bounds = RegionCoordinates.Resolve(region, RegionCoordinates.ReadDisplays());
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            var regionSettings = presetCapture?.Settings ?? (region.ProfileVersion >= 1 ? region.Apply(settings) : settings);
            var capture = await InstantCapture.TakeWithContextAsync(bounds, regionSettings);
            using var image = capture.Image;
            StoreStatus(await InstantCapture.DeliverAsync(image, regionSettings, capturedAt, output, regionName: region.Name, captureBounds: bounds, metadata: capture.Metadata, preserveOutputs: presetCapture is { LegacyInstantOutput: false }));
        }
        catch (Exception ex)
        {
            StoreStatus("範囲を再撮影できませんでした: " + ex.Message, true);
        }
        finally
        {
            busy = false;
            RestoreCaptureSettings();
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
        ShowSettings();
        form?.SelectCategory("入力プリセット");
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
            using var outcome = await CaptureSession.SelectAsync(CaptureOptions.ForInputSelection(settings, size, anchor), SaveGrid20, SaveGuideVisibility);
            return outcome.Selection?.Bounds;
        }
        finally
        {
            busy = false;
            RestoreCaptureSettings();
            if (!exiting)
            {
                RefreshMenu();
                FlushFeedback();
            }
        }
    }
}
