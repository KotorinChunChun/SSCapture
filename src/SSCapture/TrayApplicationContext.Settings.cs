using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private void ShowSettings() => ShowSettings(null);

    private void ShowSettings(CaptureSettingsLink? settingsLink)
    {
        if (exiting || busy)
            return;
        if (lastStatus?.IsExpired(DateTimeOffset.Now) == true)
            lastStatus = null;
        if (form is null || form.IsDisposed)
        {
            form = new SettingsForm(settings, SaveSettings, OpenFolder,
                openHistory: ShowHistory, lastRegion: recipes.LastRegion, selectInputRegion: PickRegionAsync,
                lastOutputSettings: recipes.Current?.Settings, exportSettings: path => SettingsTransfer.Export(path, settings),
                importSettings: ImportSettings,
                restartAsAdministrator: LaunchBehavior.IsAdministrator() ? null : RestartAsAdministrator);
            form.FormClosed += (sender, _) => { if (ReferenceEquals(sender, form)) form = null; };
            form.SetStatus(lastStatus);
        }

        form.UpdateLastRegion(recipes.LastRegion);
        form.UpdateLastOutputSettings(recipes.Current?.Settings);
        WindowVisibility.Show(form);
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Activate();
        if (settingsLink is not null) form.ShowCaptureSettings(settingsLink);
    }

    private string? ImportFromManagement(System.Text.Json.JsonElement payload)
    {
        try { return ImportSettings(SettingsTransfer.Parse(payload)); }
        catch (Exception ex) { return "設定を読み込めません: " + ex.Message; }
    }

    private string? ImportSettings(AppSettings next)
    {
        if (exiting || OperationPolicy.MustWaitToStop || pendingRecording is not null || namingForm is not null ||
            Application.OpenForms.OfType<Form>().Any(window => window.Modal))
            return "撮影・録画や開いている確認画面を終えてから取り込んでください。";

        var error = SaveSettings(next, import: true);
        if (error is not null) return error;

        // 確定した設定で再構築し、置換前の入力内容が後から上書きされるのを防ぐ。
        bool reopen = form?.Visible == true;
        string? category = form?.SelectedCategory;
        var previous = form;
        form = null;
        try
        {
            previous?.Dispose();
            StoreStatus("設定を取り込みました。", false);
            if (reopen)
            {
                ShowSettings();
                if (category is not null) form?.SelectCategory(category);
            }
        }
        catch (Exception ex) { StoreStatus("設定を取り込みました。設定画面を開き直してください: " + ex.Message, true); }
        return null;
    }

    private string? SaveSettings(AppSettings next) => SaveSettings(next, import: false);

    private string? SaveSettings(AppSettings next, bool import)
    {
        if (recorder is not null || pendingRecording is not null)
            return "録画を終了またはキャンセルしてから保存してください。";

        KeyboardHook replacement;
        try
        {
            next = next.UpdateDefaultProjections();
            next.Validate();
            replacement = store.PrepareAndSave(next, import, () => NewHook(next, false));
        }
        catch (Exception ex) { return "設定を保存できません: " + ex.Message; }

        var previous = hook;
        bool remainPaused = import && settings.HasShortcuts && previous?.Enabled == false;
        hook = replacement;
        settings = next;
        previous?.Dispose();
        hook.Enabled = settings.HasShortcuts && !remainPaused;
        try
        {
            // 旧通知が削除/変更前のアプリへ送信しないよう、設定確定時に破棄する。
            feedback.HideForCapture();
            ConfigureHistory();
            historyForm?.RefreshEntries();
            RefreshMenu();
        }
        catch (Exception ex) { StoreStatus("設定を保存しましたが画面を更新できません: " + ex.Message, true); }
        return null;
    }

    private void SaveGrid20(bool enabled)
    {
        AppSettings next = settings with { Grid20 = enabled };
        store.Save(next);
        settings = next;
        form?.UpdateCapturePreferences(next);
    }

    private void SaveGuideVisibility(bool enabled)
    {
        AppSettings next = settings with { ShowSelectionGuide = enabled };
        store.Save(next);
        settings = next;
        form?.UpdateCapturePreferences(next);
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
        RefreshTimerAvailability();
        retryOutputItem.Enabled = output.HasPendingRetry && !busy && pendingRecording is null && recorder is null;
        sameSettingsItem.Enabled = recipes.Current is not null && !busy && pendingRecording is null && recorder is null;
        RefreshPresetCaptureMenu();
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
        foreach (var region in settings.SavedRegions)
            savedRegionMenu.DropDownItems.Add(new ToolStripMenuItem(region.Name, null, (_, _) => _ = TakeRegionAsync(region))
            {
                ShortcutKeyDisplayString = TrayMenuShortcuts.Region(settings, region)
            });
        savedRegionMenu.Enabled = !busy && pendingRecording is null && savedRegionMenu.DropDownItems.Count > 0;
        tray.Text = recorder?.IsPaused == true ? "SSCapture — 録画一時停止中" : recorder?.IsRecording == true ? "SSCapture — 録画中" : pause.Checked ? "SSCapture — ショートカット停止中" : "SSCapture";
    }

    private void OpenFolder() => OpenFolder(settings.SaveDirectory);

    private void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Report("保存先を開けません: " + ex.Message, true);
        }
    }
}
