using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private void ShowSettings()
    {
        if (exiting || busy)
            return;
        if (lastStatus?.IsExpired(DateTimeOffset.Now) == true)
            lastStatus = null;
        if (form is null || form.IsDisposed)
        {
            form = new SettingsForm(settings, next => SaveSettings(next with { Grid20 = settings.Grid20, ShowSelectionGuide = settings.ShowSelectionGuide }), OpenFolder,
                openHistory: ShowHistory, lastRegion: lastRegion, selectInputRegion: PickRegionAsync);
            form.FormClosed += (_, _) => form = null;
            form.SetStatus(lastStatus);
        }

        WindowVisibility.Show(form);
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    private string? SaveSettings(AppSettings next)
    {
        if (recorder is not null || pendingRecording is not null)
            return "録画を終了またはキャンセルしてから保存してください。";
        KeyboardHook? replacement = null;
        try
        {
            next.Validate();
            replacement = NewHook(next, false);
            store.Save(next);
            hook?.Dispose();
            hook = replacement;
            replacement = null;
            settings = next;
            if (!settings.ShowCaptureNotification)
                feedback.HideForCapture();
            if (!settings.KeepHistory)
            {
                history.Clear();
                historyForm?.RefreshEntries();
            }

            hook.Enabled = settings.HasShortcuts;
            RefreshMenu();
            return null;
        }
        catch (Exception ex)
        {
            replacement?.Dispose();
            return "設定を保存できません: " + ex.Message;
        }
    }

    private void SaveGrid20(bool enabled)
    {
        AppSettings next = settings with { Grid20 = enabled };
        store.Save(next);
        settings = next;
    }

    private void SaveGuideVisibility(bool enabled)
    {
        AppSettings next = settings with { ShowSelectionGuide = enabled };
        store.Save(next);
        settings = next;
    }

    private void ToggleShortcut()
    {
        if (!TryConfigureHook(!(hook?.Enabled ?? false), out var error))
        {
            Report(error!, true);
            return;
        }

        RefreshMenu();
    }

    private void RefreshMenu()
    {
        retryOutputItem.Enabled = output.HasPendingRetry && !busy && pendingRecording is null && recorder is null;
        pause.Enabled = settings.HasShortcuts;
        pause.Checked = hook is null || !hook.Enabled;
        foreach (var (mode, item) in modeItems)
        {
            item.Checked = settings.DefaultMode == mode;
            item.Enabled = !busy && pendingRecording is null;
        }

        recordItem.Text = recorder?.IsPaused == true ? "録画を停止（一時停止中）" : recorder?.IsRecording == true ? "録画を停止" : pendingRecording is not null ? "録画準備" : "録画";
        recordItem.Enabled = !busy && !recordingTransition;
        foreach (ToolStripItem item in savedRegionMenu.DropDownItems.Cast<ToolStripItem>().ToArray())
            item.Dispose();
        savedRegionMenu.DropDownItems.Clear();
        var lastItem = new ToolStripMenuItem("前回の範囲", null, (_, _) => _ = TakeRegionAsync(AppSettings.LastRegionKeyId))
        {
            Enabled = lastRegion is not null
        };
        savedRegionMenu.DropDownItems.Add(lastItem);
        foreach (var region in settings.SavedRegions)
            savedRegionMenu.DropDownItems.Add(new ToolStripMenuItem(region.Name, null, (_, _) => _ = TakeRegionAsync(region.Id)));
        savedRegionMenu.Enabled = !busy && pendingRecording is null;
        tray.Text = recorder?.IsPaused == true ? "SSCapture — 録画一時停止中" : recorder?.IsRecording == true ? "SSCapture — 録画中" : pause.Checked ? "SSCapture — ショートカット停止中" : "SSCapture";
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.SaveDirectory);
            Process.Start(new ProcessStartInfo(settings.SaveDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Report("保存先を開けません: " + ex.Message, true);
        }
    }
}
