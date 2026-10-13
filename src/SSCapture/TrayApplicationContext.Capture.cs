using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private async Task TakeInstantAsync(int number)
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
        feedback.HideForCapture();
        RefreshMenu();
        try
        {
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            Rectangle bounds = InstantCapture.ResolveDisplay(number, InstantCapture.ReadDisplays());
            using Bitmap image = InstantCapture.Take(bounds, settings.IncludeCursor);
            // Keep capture exclusive until output completes. The optional protected
            // completion toast is shown only after pixels have been acquired.
            StoreStatus(await InstantCapture.DeliverAsync(image, settings, capturedAt, output, regionName: $"画面{number}", captureBounds: bounds));
        }
        catch (Exception ex)
        {
            StoreStatus("即時撮影できませんでした: " + ex.Message, true);
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

    private async Task TakeCaptureAsync(CaptureMode mode, CaptureIntent intent)
    {
        if (busy || exiting || pendingRecording is not null)
            return;
        busy = true;
        feedback.HideForCapture();
        RefreshMenu();
        form?.Hide();
        regionForm?.Hide();
        historyForm?.Hide();
        menu.Close();
        bool openSettings = false;
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
            using CaptureOutcome outcome = await CaptureSession.SelectAsync(new CaptureOptions(mode, intent, settings.DefaultDestination, settings.FreezeFrame, IncludeRecordingImage: intent == CaptureIntent.Recording && FileNamePattern.UsesOcr(settings.FileNamePattern), IncludeCursor: settings.IncludeCursor, FixedSize: new Size(settings.FixedWidth, settings.FixedHeight), FixedAnchor: settings.FixedCaptureAnchor));
            openSettings = outcome.OpenSettings;
            if (outcome.Selection is null)
            {
                StoreStatus("選択をキャンセルしました。");
                return;
            }

            if (outcome.Selection.Destination == OutputDestination.None)
            {
                Report("出力先がすべてOFFです。", false);
                return;
            }

            string? fileName = null;
            FileNamePlan? namePlan = null;
            AppSettings captureSettings = settings;
            if (outcome.Selection.Destination.HasFlag(OutputDestination.File))
            {
                if (FileNamePattern.UsesOcr(settings.FileNamePattern) && intent == CaptureIntent.Recording)
                {
                    using var naming = new OcrNameForm(outcome.Image ?? throw new InvalidOperationException("OCR用画像がありません。"), settings, intent, capturedAt);
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
                else if (!FileNamePattern.UsesOcr(settings.FileNamePattern))
                    namePlan = new FileNamePlan(settings.FileNamePattern, capturedAt, intent);
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
                if (mode is CaptureMode.Rectangle or CaptureMode.FixedRegion && outcome.Selection.Polygon is null && captureDisplays is not null)
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
                Func<Task<ScreenshotFileName?>>? requestFileName = outcome.Selection.Destination.HasFlag(OutputDestination.File) && FileNamePattern.UsesOcr(captureSettings.FileNamePattern) ? async () =>
                {
                    try
                    {
                        return await RequestScreenshotFileNameAsync(outcome.Image, captureSettings, capturedAt);
                    }
                    finally
                    {
                        namingFinished.TrySetResult();
                    }
                }
                : null;
                var delivery = DeliverAndRememberAsync(outcome.Image, outcome.Selection.Destination, captureSettings, fileName, namePlan, remembered, sequence, capturedAt, outcome.Selection.Bounds, requestFileName);
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
            if (!exiting)
            {
                RefreshMenu();
                FlushFeedback();
                if (openSettings)
                    ShowSettings();
            }
        }
    }

    private Task<ScreenshotFileName?> RequestScreenshotFileNameAsync(Bitmap image, AppSettings captureSettings, DateTimeOffset capturedAt)
    {
        using var naming = new OcrNameForm(image, captureSettings, CaptureIntent.Screenshot, capturedAt);
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

    private async Task<string> DeliverAndRememberAsync(Bitmap image, OutputDestination destination, AppSettings captureSettings, string? fileName, FileNamePlan? namePlan, SavedRegion? remembered, long sequence, DateTimeOffset capturedAt, Rectangle captureBounds, Func<Task<ScreenshotFileName?>>? requestFileName = null)
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
        }, captureBounds: captureBounds, requestFileName: requestFileName);
        return result + warning;
    }
}
