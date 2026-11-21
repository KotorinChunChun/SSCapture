using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private Form[] windowsToRestore = [];

    private void PrepareCaptureUi(bool syncClosedMenu = false)
    {
        windowsToRestore = Application.OpenForms.Cast<Form>()
            .Where(f => f.Visible && !f.IsDisposed && f != namingForm && f != recordingForm).ToArray();
        CaptureUiPreparation.Prepare(windowsToRestore,
            () => { feedback.HideForCapture(); menu.Close(); }, syncClosedMenu || menu.Visible);
    }

    private void RestoreCaptureSettings()
    {
        if (pendingRecording is not null || recorder is not null) return;
        Form[] previous = windowsToRestore;
        windowsToRestore = [];
        if (!exiting) CaptureUiPreparation.RestoreAll(previous);
    }

    private async Task TakeInstantAsync(int number, DisplayTarget? menuTarget = null)
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
            PrepareCaptureUi(syncClosedMenu: menuTarget is not null);
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            var displays = InstantCapture.ReadDisplays();
            Rectangle bounds = menuTarget is null
                ? InstantCapture.ResolveDisplay(number, displays)
                : InstantCapture.ResolveDisplay(menuTarget, displays).Bounds;
            var capture = await InstantCapture.TakeWithContextAsync(bounds, settings);
            using Bitmap image = capture.Image;
            // Keep capture exclusive until output completes. The optional protected
            // completion toast is shown only after pixels have been acquired.
            StoreStatus(await InstantCapture.DeliverAsync(image, settings, capturedAt, output, regionName: $"画面{number}",
                captureBounds: bounds, metadata: capture.Metadata, replaySource: new CaptureReplaySource.Monitors([bounds])));
        }
        catch (Exception ex)
        {
            StoreStatus("即時撮影できませんでした: " + ex.Message, true);
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

    private async Task TakeCaptureAsync(CaptureMode mode, CaptureIntent intent, SavedRegion? inputPreset = null, bool showPreviousOnEscape = false)
    {
        if (busy || exiting || pendingRecording is not null)
            return;
        if (intent == CaptureIntent.Recording &&
            !CheckRecordingTools())
            return;
        busy = true;
        RefreshMenu();
        CaptureSettingsLink? settingsLink = null;
        bool showPrevious = false;
        DateTimeOffset capturedAt = DateTimeOffset.Now;
        long sequence = ++captureSerial;
        IReadOnlyList<DisplayLayout>? captureDisplays = null;
        try
        {
            captureDisplays = RegionCoordinates.ReadDisplays();
        }
        catch (Exception)
        { /* Capture remains available without reusable coordinates. */
        }

        try
        {
            PrepareCaptureUi();
            AppSettings startSettings = inputPreset?.Apply(settings) ?? settings;
            using CaptureOutcome outcome = await CaptureSession.SelectAsync(new CaptureOptions(mode, intent, startSettings.DefaultDestination, startSettings.FreezeFrame, IncludeRecordingImage: intent == CaptureIntent.Recording && (startSettings.DefaultDestination.HasFlag(OutputDestination.File) && FileNamePattern.UsesOcr(startSettings.FileNamePattern) || startSettings.CapturePresets.Any(p => p.Destination.HasFlag(OutputDestination.File) && FileNamePattern.UsesOcr(p.FileNamePattern))), IncludeCursor: startSettings.IncludeCursor, FixedSize: new Size(startSettings.FixedWidth, startSettings.FixedHeight), FixedAnchor: startSettings.FixedCaptureAnchor, LimitToStartingMonitor: startSettings.LimitToStartingMonitor, Grid20: startSettings.Grid20, ShowSelectionGuide: startSettings.ShowSelectionGuide, OutputSettings: startSettings, DimCaptureBackground: startSettings.DimCaptureBackground, InitialInputPreset: inputPreset ?? settings.SavedRegions.FirstOrDefault(p => p.IsDefault)), SaveGrid20, SaveGuideVisibility);
            if (outcome.OpenSettings)
                settingsLink = outcome.SettingsLink ?? new(CaptureSettingsDestination.General);
            if (outcome.Selection is null)
            {
                showPrevious = showPreviousOnEscape && outcome.EscapedBeforeSelectionInput;
                StoreStatus("選択をキャンセルしました。");
                return;
            }

            if (outcome.Selection.Destination == OutputDestination.None)
            {
                Report("出力先がすべてOFFです。", false);
                return;
            }

            string? fileName = null;
            AppSettings captureSettings = outcome.OutputSettings ?? settings;
            string regionName = outcome.Selection.RegionName ?? "撮影";
            FileNamePlan? namePlan = new(captureSettings.FileNamePattern, capturedAt, intent,
                intent == CaptureIntent.Recording && FileNamePattern.UsesOcr(captureSettings.FileNamePattern) ? "OCRなし" : "",
                Region: regionName);
            if (intent == CaptureIntent.Recording && outcome.Selection.Destination.HasFlag(OutputDestination.File)
                && FileNamePattern.UsesOcr(captureSettings.FileNamePattern))
            {
                using var naming = new OcrNameForm(outcome.Image ?? throw new InvalidOperationException("OCR用画像がありません。"),
                    captureSettings, intent, capturedAt, regionName: regionName);
                namingForm = naming;
                DialogResult result;
                try
                {
                    result = naming.ShowDialog();
                }
                finally
                {
                    namingForm = null;
                }

                if (result != DialogResult.OK)
                {
                    StoreStatus("OCR命名をキャンセルしました。");
                    return;
                }

                fileName = naming.FileName;
                namePlan = naming.NamePlan;
                captureSettings = naming.OutputSettings;
            }

            if (intent == CaptureIntent.Recording)
            {
                recordingFileName = fileName;
                recordingNamePlan = namePlan;
                PrepareRecording(outcome.Selection, captureSettings);
            }
            else if (outcome.Image is not null)
            {
                SavedRegion? remembered = null;
                if (mode is CaptureMode.Rectangle or CaptureMode.FixedRegion && outcome.Selection.Polygon is null
                    && outcome.Selection.ReplaySource is null && captureDisplays is not null)
                {
                    try
                    {
                        remembered = RegionCoordinates.FromBounds("前回範囲", outcome.Selection.Bounds, captureDisplays);
                        _ = RegionCoordinates.Resolve(remembered, RegionCoordinates.ReadDisplays());
                    }
                    catch (Exception)
                    {
                        remembered = null;
                    }
                }

                var namingFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<Bitmap, Task<ScreenshotFileName?>>? requestFileName = outcome.Selection.Destination.HasFlag(OutputDestination.File) && FileNamePattern.UsesOcr(captureSettings.FileNamePattern) ? async namingImage =>
                {
                    try
                    {
                        return await RequestScreenshotFileNameAsync(namingImage, captureSettings, capturedAt, regionName);
                    }
                    finally
                    {
                        namingFinished.TrySetResult();
                    }
                }
                : null;
                var delivery = DeliverAndRememberAsync(outcome.Image, outcome.Selection.Destination, captureSettings, fileName, namePlan, remembered, sequence, capturedAt, outcome.Selection, requestFileName, outcome.Metadata);
                Report(await AwaitDeliveryAsync(delivery, requestFileName is null ? null : namingFinished.Task), false);
            }
        }
        catch (Exception ex)
        {
            Report("撮影できませんでした: " + ex.Message, true);
        }
        finally
        {
            busy = false;
            RestoreCaptureSettings();
            if (!exiting)
            {
                RefreshMenu();
                FlushFeedback();
                if (showPrevious) feedback.ShowPreviousScreenshot(settings);
                if (settingsLink is not null)
                    ShowSettings(settingsLink);
            }
        }
    }

    private Task<ScreenshotFileName?> RequestScreenshotFileNameAsync(Bitmap image, AppSettings captureSettings, DateTimeOffset capturedAt, string regionName)
    {
        using var naming = new OcrNameForm(image, captureSettings, CaptureIntent.Screenshot, capturedAt, regionName: regionName);
        namingForm = naming;
        try
        {
            return Task.FromResult<ScreenshotFileName?>(naming.ShowDialog() == DialogResult.OK ? new(naming.FileName, naming.NamePlan, naming.OutputSettings.OpenFolderAfterSave) : null);
        }
        finally
        {
            namingForm = null;
        }
    }

    private async Task<string> DeliverAndRememberAsync(Bitmap image, OutputDestination destination, AppSettings captureSettings, string? fileName, FileNamePlan? namePlan, SavedRegion? remembered, long sequence, DateTimeOffset capturedAt, CaptureSelection selection, Func<Bitmap, Task<ScreenshotFileName?>>? requestFileName, CaptureMetadata? metadata)
    {
        string warning = "";
        string result = await output.ScreenshotAsync(image, destination, captureSettings, fileName, namePlan, capturedAt, () =>
        {
            if (remembered is null || sequence <= lastRememberedSerial)
                return;
            try
            {
                regionStore.Save(remembered);
                lastRegion = remembered;
                lastRememberedSerial = sequence;
                if (!exiting)
                    RefreshMenu();
            }
            catch (Exception ex)
            {
                warning = Environment.NewLine + "前回範囲を保存できませんでした: " + ex.Message;
            }
        }, captureBounds: selection.Bounds, requestFileNameForImage: requestFileName, metadata: metadata, capturePolygon: selection.Polygon,
            windowTitle: selection.WindowTitle, replaySource: selection.ReplaySource);
        return result + warning;
    }
}
