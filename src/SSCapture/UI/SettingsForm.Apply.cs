using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed partial class SettingsForm
{
    private Action? refreshSettingControls;

    private Func<AppSettings>? PageDraft(TabPage page) => page.Text switch
    {
        "基本" => CurrentSelectionSettings,
        "通知" => ReadNotificationSettings,
        "履歴" => () => committed with { KeepHistory = keepHistory.Checked },
        "外部アプリ" => ReadExternalApplicationSettings,
        "即時撮影キー" => ReadInstantShortcuts,
        FirstPointTab => ReadBeforeShortcuts,
        SecondPointTab => ReadDragShortcuts,
        _ => null
    };

    private void AddApplyButton(FlowLayoutPanel actions, Control editArea, Func<AppSettings> readDraft, Action restoreDraft, bool isBasic = false)
    {
        var apply = MakeButton("適用", "ApplyPageSettings", ButtonRole.Primary);
        apply.Margin = new Padding(0, 4, 8, 4);
        actions.Controls.Add(apply);
        Button? restart = null;
        if (isBasic && restartAsAdministrator is { } restartApp)
        {
            restart = MakeButton("再起動", "RestartAsAdministrator");
            restart.Margin = new Padding(0, 4, 8, 4);
            restart.Click += (_, _) =>
            {
                if (restart.Enabled && restartApp() is { } error) SetStatus(error, true);
            };
            actions.Controls.Add(restart);
            settingTips.SetToolTip(restart, "保存した設定を反映するため、Windowsの承認後にSSCaptureを管理者として再起動します。");
        }
        var revert = AddRevertButton(actions, restoreDraft);

        bool applying = false;
        void Refresh()
        {
            bool changed = !applying && (readDraft() != committed || isBasic && startupSettings.HasDraft);
            apply.Enabled = revert.Enabled = changed;
            if (restart is not null)
            {
                bool canRestart = committed.RunAsAdministrator && readDraft().RunAsAdministrator;
                restart.Visible = canRestart;
                restart.Enabled = canRestart && !applying && !changed;
            }
        }
        if (isBasic) startupSettings.DraftChanged += (_, _) => Refresh();
        apply.Click += async (_, _) =>
        {
            if (applying) return;
            var next = readDraft();
            bool settingsChanged = next != committed;
            bool startupChanged = isBasic && startupSettings.HasDraft;
            bool elevationChanged = isBasic && next.RunAsAdministrator != committed.RunAsAdministrator;
            if (!settingsChanged && !startupChanged) return;

            applying = true;
            Refresh();
            try
            {
                if (CommitEditorSettings(next) is { } saveError)
                {
                    SetStatus(saveError, true);
                    return;
                }
                string? startupError = startupChanged ? await startupSettings.ApplyDraftAsync() : null;
                if (IsDisposed) return;
                if (startupError is null)
                    SetStatus(elevationChanged && restart is not null && next.RunAsAdministrator
                        ? "設定を適用しました。「再起動」で管理者権限に切り替えられます。"
                        : elevationChanged
                            ? "設定を適用しました。起動権限は、トレイから終了した後の手動起動で反映されます。"
                            : "設定を適用しました。");
                else
                    SetStatus((settingsChanged ? "基本設定を保存しました。 " : "") + "自動起動を変更できませんでした。 " + startupError, true);
            }
            finally
            {
                applying = false;
                if (!IsDisposed) Refresh();
            }
        };

        refreshSettingControls += Refresh;
        Watch(editArea);
        Refresh();

        void Watch(Control control)
        {
            if (control is HotkeyEditor key)
            {
                key.ValueChanged += (_, _) => Refresh();
                return;
            }

            switch (control)
            {
                case CheckBox check: check.CheckedChanged += (_, _) => Refresh(); break;
                case ComboBox combo: combo.SelectedIndexChanged += (_, _) => Refresh(); break;
                case NumericUpDown number:
                    number.ValueChanged += (_, _) => Refresh();
                    number.TextChanged += (_, _) => Refresh();
                    return;
                case TextBox text when !text.ReadOnly: text.TextChanged += (_, _) => Refresh(); break;
            }

            foreach (Control child in control.Controls) Watch(child);
        }
    }

    private Button AddRevertButton(FlowLayoutPanel actions, Action restoreDraft)
    {
        var revert = MakeButton("戻す", "RevertPageSettings", ButtonRole.Neutral);
        revert.Margin = new Padding(0, 4, 8, 4);
        revert.CausesValidation = false;
        revert.Click += (_, _) =>
        {
            restoreDraft();
            refreshSettingControls?.Invoke();
            SetStatus("保存済みの設定に戻しました。");
        };
        actions.Controls.Add(revert);
        return revert;
    }

    private AppSettings ReadNotificationSettings() => committed with
    {
        NotificationDurationSeconds = SettingNumber(notificationDuration),
        PlayCaptureSound = playCaptureSound.Checked,
        HoldNotificationWhileApproaching = holdNotification.Checked,
        NotificationApplicationId = NotificationAppId(),
        NotificationDisplay = (notificationDisplay.SelectedItem as NotificationDisplayItem)?.Value ?? committed.NotificationDisplay,
        NotificationCorner = (notificationCorner.SelectedItem as CornerItem)?.Value ?? committed.NotificationCorner
    };

    private AppSettings ReadInstantShortcuts()
    {
        var keys = instantKeys.Select(key => key.Value).ToArray();
        return committed with
        {
            InstantShortcuts = committed.InstantShortcuts.SequenceEqual(keys) ? committed.InstantShortcuts : keys,
            LastRecipeShortcut = lastRecipeKey.Value
        };
    }

    private AppSettings ReadBeforeShortcuts() => committed with
    {
        CaptureBeforeKeys = captureBeforeKeys.Value,
        CaptureMouse = committed.CaptureMouse with
        {
            BeforeLeftAction = captureBeforeKeys.ClickAt(MouseButtons.Left),
            BeforeRightAction = captureBeforeKeys.ClickAt(MouseButtons.Right),
            BeforeMiddleAction = captureBeforeKeys.ClickAt(MouseButtons.Middle)
        }
    };

    private AppSettings ReadDragShortcuts() => committed with
    {
        CaptureKeys = captureKeys.Value,
        CaptureMouse = committed.CaptureMouse with
        {
            DragRightAction = captureKeys.ClickAt(MouseButtons.Right),
            DragMiddleAction = captureKeys.ClickAt(MouseButtons.Middle)
        }
    };

    private AppSettings ReadExternalApplicationSettings()
    {
        var next = WithExternalApplications(committed, CaptureIntent.Screenshot, imageApps.Applications, imageApps.SelectedId);
        return WithExternalApplications(next, CaptureIntent.Recording, videoApps.Applications, videoApps.SelectedId);
    }

    private static AppSettings WithExternalApplications(AppSettings current, CaptureIntent intent, ExternalApplication[] apps, string id)
    {
        bool image = intent == CaptureIntent.Screenshot;
        var previousApps = image ? current.ImageEditors : current.VideoEditors;
        string previousId = image ? current.SelectedImageEditorId : current.SelectedVideoEditorId;
        if (previousId == id && previousApps.SequenceEqual(apps)) return current;

        var presets = previousId == id ? current.CapturePresets : current.CapturePresets.Select(preset => !preset.IsDefault ? preset :
            image ? preset with { ImageEditorId = id } : preset with { VideoEditorId = id }).ToArray();
        var selected = apps.FirstOrDefault(app => app.Id == id);
        string executable = selected?.Executable ?? "";
        string arguments = selected?.Arguments ?? EditorArguments.Default;
        return image
            ? current with { ImageEditors = apps, SelectedImageEditorId = id, ImageEditorPath = executable, ImageEditorArguments = arguments, CapturePresets = presets }
            : current with { VideoEditors = apps, SelectedVideoEditorId = id, VideoEditorPath = executable, VideoEditorArguments = arguments, CapturePresets = presets };
    }

    private void BindPresetShortcutLinks()
    {
        Bind(inputPresetEditor, "SavedRegions", "OpenInputPresetShortcuts", input: true);
        Bind(presetEditor, "CapturePresetList", "OpenOutputPresetShortcuts", input: false);

        void Bind(Control editor, string listName, string buttonName, bool input)
        {
            var list = (ListBox)editor.Controls.Find(listName, true).Single();
            var button = (Button)editor.Controls.Find(buttonName, true).Single();
            button.AutoEllipsis = true;
            button.UseMnemonic = false;

            void Refresh()
            {
                string? id = list.SelectedItem switch { SavedRegion region => region.Id, CapturePreset preset => preset.Id, _ => null };
                bool isDefault = input
                    ? committed.SavedRegions.Any(preset => preset.Id == id && preset.IsDefault)
                    : committed.CapturePresets.Any(preset => preset.Id == id && preset.IsDefault);
                var keys = (committed.PresetCaptureShortcuts ?? []).Where(row =>
                {
                    string? reference = input ? row.InputPresetId : row.OutputPresetId;
                    return reference is null ? isDefault : reference == id;
                }).Select(row => KeyDisplay.Binding(row.Key)).ToArray();

                button.Text = keys.Length == 0 ? "割り当てなし" : string.Join("、", keys);
                settingTips.SetToolTip(button, keys.Length == 0 ? "ショートカットを設定します。" :
                    string.Join("\n", keys) + "\nショートカット設定を開きます。");
            }

            list.SelectedIndexChanged += (_, _) => Refresh();
            refreshSettingControls += Refresh;
            Refresh();
        }
    }

    private static int SettingNumber(NumericUpDown control) =>
        int.TryParse(control.Text, out int value) ? Math.Clamp(value, (int)control.Minimum, (int)control.Maximum) : (int)control.Value;
}
