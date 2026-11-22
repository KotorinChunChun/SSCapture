using SSCapture.Services;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private readonly ToolStripMenuItem presetCaptureMenu = new("プリセット撮影");
    private bool CanStartPresetCapture => !exiting && !busy && pendingRecording is null && recorder is null
        && !recordingTransition && namingForm is null;

    private Task TakePresetShortcutAsync(string key)
    {
        var row = settings.PresetCaptureShortcuts?.FirstOrDefault(p => p.Key == key);
        return row is null ? Task.CompletedTask : TakePresetCaptureAsync(row);
    }

    private async Task TakePresetCaptureAsync(PresetCaptureShortcut row)
    {
        if (HandleActiveCapture(row.Intent) || !CanStartPresetCapture) return;
        try
        {
            var capture = ResolvedPresetCapture.Resolve(settings, row.InputPresetId, row.OutputPresetId, row.LegacyInstantOutput);
            if (row.UsesDirectCapture(capture.Input))
            {
                if (capture.Settings.DefaultDestination == OutputDestination.None)
                    throw new ArgumentException("出力先がすべてOFFです。出力プリセットを確認してください。");
                await TakeRegionAsync(capture.Input, capture);
            }
            else
            {
                await TakeCaptureAsync(capture.Settings.DefaultMode, row.Intent, capture.Input,
                    showPreviousOnEscape: row.UsesNormalSelection,
                    presetCapture: capture);
            }
        }
        catch (Exception ex)
        {
            Report("プリセット撮影を開始できません: " + ex.Message, true);
        }
    }

    private void RefreshPresetCaptureMenu()
    {
        presetCaptureMenu.Enabled = CanStartPresetCapture;
        foreach (ToolStripItem item in presetCaptureMenu.DropDownItems.Cast<ToolStripItem>().ToArray())
            item.Dispose();
        presetCaptureMenu.DropDownItems.Clear();
        foreach (var row in settings.PresetCaptureShortcuts ?? [])
        {
            var preset = ResolvedPresetCapture.Resolve(settings, row.InputPresetId, row.OutputPresetId, row.LegacyInstantOutput);
            presetCaptureMenu.DropDownItems.Add(new ToolStripMenuItem(
                $"{row.IntentLabel}  {preset.Input.Name} → {preset.Output.Name}", null, (_, _) => _ = TakePresetCaptureAsync(row))
            {
                ShortcutKeyDisplayString = KeyDisplay.Binding(row.Key)
            });
        }
        if (presetCaptureMenu.DropDownItems.Count > 0)
            presetCaptureMenu.DropDownItems.Add(new ToolStripSeparator());
        presetCaptureMenu.DropDownItems.Add("ショートカットを設定", null, (_, _) =>
        {
            ShowSettings();
            form?.ShowPresetShortcuts();
        });
    }
}
