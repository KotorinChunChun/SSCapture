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

        _ = TakeCaptureAsync(settings.DefaultMode, intent, showPreviousOnEscape: intent == CaptureIntent.Screenshot);
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

    private KeyboardHook NewHook(AppSettings value, bool enabled = true)
    {
        KeyboardHook? source = null;
        try
        {
            source = new(intent => PostCurrent(() => OnCaptureKey(intent)), value, enabled,
                number => PostCurrent(() => _ = TakeInstantAsync(number)),
                id => PostCurrent(() => _ = id == AppSettings.LastRecipeKeyId ? TakeSameSettingsAsync() : TakeRegionAsync(id)),
                key => PostCurrent(() => _ = TakePresetShortcutAsync(key)));
            return source;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException && value.PresetCaptureShortcuts is { Length: > 0 })
        {
            string rows = string.Join("、", value.PresetCaptureShortcuts!.Select((row, index) => row.Label(index)));
            throw new InvalidOperationException($"{ex.Message} 監視対象の割り当て: {rows}", ex);
        }

        void PostCurrent(Action action) => Post(() =>
        {
            if (source is not null && ReferenceEquals(hook, source) && source.Enabled) action();
        });
    }
}
