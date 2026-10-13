using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect))
            return;
        Post(() =>
        {
            bool enabled = hook?.Enabled ?? settings.HasShortcuts;
            hook?.Dispose();
            hook = null;
            if (!TryConfigureHook(enabled, out var error))
                Report(error!, true);
            RefreshMenu();
        });
    }

    private void OnCaptureKey(CaptureIntent intent)
    {
        if (WindowVisibility.RestorePending(namingForm))
            return;
        if (intent == CaptureIntent.Recording && recorder is not null)
        {
            if (recorder.IsRecording && !recordingTransition)
                _ = StopRecordingAsync();
            return;
        }

        if (pendingRecording is not null)
        {
            if (recordingForm is not null)
                WindowVisibility.Show(recordingForm);
            return;
        }

        _ = TakeCaptureAsync(settings.DefaultMode, intent);
    }

    private bool TryConfigureHook(bool enabled, out string? error)
    {
        try
        {
            if (enabled && hook is null)
                hook = NewHook(settings);
            if (hook is not null)
                hook.Enabled = enabled;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "ショートカットを開始できません: " + ex.Message;
            return false;
        }
    }

    private KeyboardHook NewHook(AppSettings value, bool enabled = true) => new(intent => Post(() => OnCaptureKey(intent)), value, enabled, number => Post(() => _ = TakeInstantAsync(number)), id => Post(() => _ = TakeRegionAsync(id)));
}
