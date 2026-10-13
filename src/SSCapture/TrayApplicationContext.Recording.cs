using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;
internal sealed partial class TrayApplicationContext
{
    private void PrepareRecording(CaptureSelection selection, AppSettings captureSettings)
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            throw new FileNotFoundException("録画エンジンがありません。setup-media.ps1 と build.ps1 を実行してください。");
        pendingRecording = selection;
        recordingSettings = captureSettings;
        recordingForm = new RecordingControlForm(selection.Bounds, captureSettings);
        recordingForm.StartRequested += () => _ = StartRecordingAsync();
        recordingForm.StopRequested += () => _ = StopRecordingAsync();
        recordingForm.CancelRequested += CancelRecording;
        recordingForm.PauseRequested += () => _ = ChangeRecordingPauseAsync(true);
        recordingForm.ResumeRequested += () => _ = ChangeRecordingPauseAsync(false);
        recordingForm.SystemAudioChangeRequested += enabled => _ = ChangeRecordingAudioAsync(true, enabled);
        recordingForm.MicrophoneChangeRequested += enabled => _ = ChangeRecordingAudioAsync(false, enabled);
        WindowVisibility.Show(recordingForm);
    }

    private async Task StartRecordingAsync()
    {
        if (pendingRecording is null || recordingTransition || recorder is not null)
            return;
        feedback.HideForCapture();
        recordingTransition = true;
        recordingFailure = null;
        try
        {
            recordingForm?.SetProcessing("録画を開始しています…");
            RefreshMenu();
            var current = new RecordingService(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe"), workRoot);
            recorder = current;
            current.Failed += message => Post(() =>
            {
                if (!ReferenceEquals(recorder, current))
                    return;
                recordingFailure ??= message;
                Report("録画エラー: " + message, true);
                if (!recordingTransition)
                    _ = FinishFailedRecordingAsync();
            });
            await recorder.StartAsync(pendingRecording, recordingSettings!);
            if (recordingFailure is null)
            {
                recordingForm?.SetRecording();
                StoreStatus("録画中");
            }
        }
        catch (Exception ex)
        {
            await ReleaseRecordingAsync();
            Report("録画を開始できませんでした: " + ex.Message, true);
        }
        finally
        {
            recordingTransition = false;
            RefreshMenu();
            FlushFeedback();
        }

        if (recordingFailure is not null && recorder is not null)
            await FinishFailedRecordingAsync();
    }

    private async Task ChangeRecordingPauseAsync(bool paused)
    {
        if (recorder is null || recordingTransition || recorder.IsPaused == paused)
            return;
        recordingTransition = true;
        try
        {
            recordingForm?.SetProcessing(paused ? "一時停止しています…" : "録画を再開しています…");
            RefreshMenu();
            if (paused)
                await recorder.PauseAsync();
            else
                await recorder.ResumeAsync();
            if (recordingFailure is null)
            {
                if (paused)
                    recordingForm?.SetPaused();
                else
                    recordingForm?.SetRecording();
                StoreStatus(paused ? "録画を一時停止しました。" : "録画を再開しました。");
            }
        }
        catch (Exception ex)
        {
            await ReleaseRecordingAsync();
            Report((paused ? "録画を一時停止できませんでした: " : "録画を再開できませんでした: ") + ex.Message, true);
        }
        finally
        {
            recordingTransition = false;
            RefreshMenu();
            FlushFeedback();
        }

        if (recordingFailure is not null && recorder is not null)
            await FinishFailedRecordingAsync();
    }

    private async Task ChangeRecordingAudioAsync(bool systemAudio, bool enabled)
    {
        if (pendingRecording is null || recordingSettings is null || recordingTransition)
            return;
        recordingTransition = true;
        try
        {
            if (recorder is not null)
            {
                recordingForm?.SetProcessing((systemAudio ? "PC音声" : "マイク") + "を切り替えています…", pauseElapsed: false);
                RefreshMenu();
                if (systemAudio)
                    await recorder.SetSystemAudioAsync(enabled);
                else
                    await recorder.SetMicrophoneAsync(enabled);
            }

            recordingSettings = systemAudio ? recordingSettings with
            {
                RecordSystemAudio = enabled
            }

            : recordingSettings with
            {
                RecordMicrophone = enabled
            };
        }
        catch (Exception ex)
        {
            Report((systemAudio ? "PC音声" : "マイク") + "を切り替えられませんでした: " + ex.Message, true);
        }
        finally
        {
            if (recordingSettings is { } currentSettings)
            {
                recordingForm?.SetAudioState(currentSettings.RecordSystemAudio, currentSettings.RecordMicrophone);
                recordingForm?.SetControlsReady();
            }

            recordingTransition = false;
            RefreshMenu();
            FlushFeedback();
        }

        if (recordingFailure is not null && recorder is not null)
            await FinishFailedRecordingAsync();
    }

    private async Task StopRecordingAsync()
    {
        if (recorder is null || recordingTransition)
            return;
        recordingTransition = true;
        try
        {
            recordingForm?.SetProcessing("動画を仕上げています…");
            RefreshMenu();
            string path = await recorder.StopAsync();
            try
            {
                Report(await AwaitDeliveryAsync(output.RecordingAsync(path, pendingRecording!.Destination, recordingSettings!, recordingFileName, recordingNamePlan, captureBounds: pendingRecording.Bounds)), false);
            }
            catch (Exception ex)
            {
                Report($"動画の送信に失敗しました: {ex.Message}\n動画: {path}", true);
            }
        }
        catch (Exception ex)
        {
            Report("録画を終了できませんでした: " + ex.Message, true);
        }
        finally
        {
            await ReleaseRecordingAsync();
            recordingTransition = false;
            RefreshMenu();
            FlushFeedback();
        }
    }

    private async Task FinishFailedRecordingAsync()
    {
        if (recorder is null || recordingTransition)
            return;
        recordingTransition = true;
        try
        {
            await ReleaseRecordingAsync();
        }
        finally
        {
            recordingTransition = false;
            RefreshMenu();
            FlushFeedback();
        }
    }

    private void CancelRecording()
    {
        if (recordingTransition || recorder is not null)
            return;
        recordingForm?.Finish();
        recordingForm = null;
        pendingRecording = null;
        recordingSettings = null;
        StoreStatus("録画準備をキャンセルしました。");
        RefreshMenu();
        FlushFeedback();
    }

    private async Task<string> AwaitDeliveryAsync(Task<string> delivery, Task? interactionFinished = null)
    {
        if (await DeliveryWait.CompletedWithinAsync(delivery, TimeSpan.FromSeconds(10), interactionFinished))
            return await delivery;
        _ = ObserveDeliveryAsync(delivery);
        return "送信先が応答しません。処理はバックグラウンドで継続しています。";
    }

    private async Task ObserveDeliveryAsync(Task<string> delivery)
    {
        try
        {
            string result = await delivery;
            Post(() => Report(result, false));
        }
        catch (Exception ex)
        {
            Post(() => Report("バックグラウンド送信に失敗しました: " + ex.Message, true));
        }
    }

    private async Task ReleaseRecordingAsync()
    {
        var previous = recorder;
        recorder = null;
        try
        {
            if (previous is not null)
                await previous.DisposeAsync();
        }
        catch (Exception ex)
        {
            Report("録画の後処理に失敗しました: " + ex.Message, true);
        }
        finally
        {
            recordingForm?.Finish();
            recordingForm = null;
            pendingRecording = null;
            recordingSettings = null;
        }
    }
}
