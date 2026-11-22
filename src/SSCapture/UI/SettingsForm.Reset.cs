using SSCapture.Services;

namespace SSCapture.UI;

internal sealed partial class SettingsForm
{
    private void AddPageActions(TabPage page)
    {
        var actions = new FlowLayoutPanel { Name = "PageResetActions", Dock = DockStyle.Bottom,
            Height = SettingsEditorActions.Height, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var reset = MakeButton("初期設定に戻す", "ResetSettingsPage");
        reset.Width = 168;
        reset.Margin = new Padding(0, 4, 3, 4);
        reset.CausesValidation = false;
        reset.Click += (_, _) => ResetSelectedPage();
        actions.Controls.Add(reset);
        if (PageDraft(page) is { } readDraft) AddApplyButton(actions, page, readDraft);
        page.Controls.Add(actions);
    }

    internal void ResetSelectedPage(Func<bool>? confirm = null)
    {
        var defaults = new AppSettings();
        try
        {
            switch (SelectedCategory)
            {
                case "入力プリセット":
                    if (ConfirmPresetReset())
                    {
                        var next = StandardRegistrationReset.RestoreInputs(committed);
                        CompletePresetReset(CommitInputs(next.SavedRegions), () => inputPresetEditor.LoadRestoredRegions(committed.SavedRegions));
                    }
                    return;
                case "出力プリセット":
                    if (ConfirmPresetReset())
                    {
                        var next = StandardRegistrationReset.RestoreOutputs(committed) with { OneOcrDirectory = defaults.OneOcrDirectory };
                        CompletePresetReset(CommitEditorSettings(next), () =>
                        {
                            presetEditor.LoadPresets(committed.CapturePresets, committed.CapturePresets.Single(preset => preset.IsDefault).Id);
                            oneOcrDirectory.Text = defaults.OneOcrDirectory;
                        });
                    }
                    return;
                case "基本":
                    ResetBasicDraft(defaults);
                    break;
                case "通知":
                    notificationDisplay.SelectedIndex = (int)defaults.NotificationDisplay;
                    notificationCorner.SelectedIndex = (int)defaults.NotificationCorner;
                    notificationDuration.Value = defaults.NotificationDurationSeconds;
                    holdNotification.Checked = defaults.HoldNotificationWhileApproaching;
                    playCaptureSound.Checked = defaults.PlayCaptureSound;
                    notificationApplication.SelectedIndex = 0;
                    break;
                case "履歴":
                    keepHistory.Checked = defaults.KeepHistory;
                    break;
                case "外部アプリ":
                    var restored = StandardRegistrationReset.RestoreExternalApplications(committed with { ImageEditors = imageApps.Applications, VideoEditors = videoApps.Applications });
                    imageApps.LoadRestoredApplications(restored.ImageEditors);
                    videoApps.LoadRestoredApplications(restored.VideoEditors);
                    presetEditor.RefreshApplications();
                    break;
                case "ショートカット":
                    ResetShortcutDraft(defaults);
                    break;
                case "コマンド生成":
                    captureCommand.ResetDraft();
                    SetStatus("コマンド生成の入力を初期値に戻しました。");
                    return;
                default:
                    return;
            }
            refreshSettingControls?.Invoke();
            string button = SelectedCategory == "ショートカット" && shortcutTabs.SelectedTab == workShortcutPage ? "保存" : "適用";
            SetStatus($"初期値を設定しました。{button}で確定します。");
        }
        catch (ArgumentException ex)
        {
            SetStatus("初期設定に戻せませんでした。 " + ex.Message, true);
        }

        bool ConfirmPresetReset()
        {
            if (confirm is not null) return confirm();
            using var dialog = new PresetResetConfirmation(SelectedCategory!);
            return dialog.ShowDialog(this) == DialogResult.OK;
        }
    }

    private void CompletePresetReset(string? error, Action reload)
    {
        if (error is not null) { SetStatus(error, true); return; }
        reload();
        RefreshPresetShortcutChoices();
        SetStatus("標準プリセットを初期設定に戻しました。");
    }

    private void ResetBasicDraft(AppSettings defaults)
    {
        limitToStartingMonitor.Checked = defaults.LimitToStartingMonitor;
        dimCaptureBackground.Checked = defaults.DimCaptureBackground;
        shareCapturePresentation.Checked = defaults.ShareCapturePresentation;
        gridEnabled.Checked = defaults.Grid20;
        gridStep.Value = defaults.GridStepPixels;
        snapDistance.Value = defaults.SnapDistancePixels;
        guideEnabled.Checked = defaults.ShowSelectionGuide;
        guideDisplay.SelectedIndex = (int)defaults.SelectionGuideDisplay;
        pointerGuideEnabled.Checked = defaults.ShowPointerGuide;
        magnifierEnabled.Checked = defaults.ShowMagnifier;
        magnifierDotCount.SelectedItem = defaults.MagnifierDotCount;
        magnifierZoom.Value = defaults.MagnifierZoom;
        startupSettings.ResetDraft();
    }

    private void ResetShortcutDraft(AppSettings defaults)
    {
        switch (shortcutTabs.SelectedTab?.Text)
        {
            case "PC作業中":
                var rows = PresetCaptureShortcut.Migrate(defaults).PresetCaptureShortcuts!;
                HotkeyBinding.ValidateSettings(committed with
                {
                    PresetCaptureShortcuts = rows, LastRecipeShortcut = defaults.LastRecipeShortcut,
                    InstantShortcuts = instantKeys.Select(key => key.Value).ToArray()
                });
                lastRecipeKey.Value = defaults.LastRecipeShortcut;
                presetShortcutEditor.LoadRows(rows);
                break;
            case "即時撮影キー":
                HotkeyBinding.ValidateSettings(committed with
                {
                    PresetCaptureShortcuts = presetShortcutEditor.Rows, LastRecipeShortcut = lastRecipeKey.Value,
                    InstantShortcuts = defaults.InstantShortcuts
                });
                for (int index = 0; index < instantKeys.Length; index++) instantKeys[index].Value = defaults.InstantShortcuts[index];
                break;
            case "撮影範囲設定前":
                captureBeforeKeys.ResetDraft(defaults.CaptureBeforeKeys, defaults.CaptureMouse, defaults.ControlActivation, defaults.WindowActivation);
                break;
            case "撮影ドラッグ中":
                captureKeys.ResetDraft(defaults.CaptureKeys, defaults.CaptureMouse, defaults.ControlActivation, defaults.WindowActivation);
                break;
        }
    }

    private sealed class PresetResetConfirmation : BrandedForm
    {
        internal PresetResetConfirmation(string category) : base("初期設定に戻す")
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;

            var content = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16) };
            content.Controls.Add(new Label { AutoSize = true, Text = $"「{category}」の標準プリセットを初期値に戻して保存します。\n自作プリセットは保持します。" });
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
            var cancel = MakeButton("キャンセル", "CancelResetSettingsPage", ButtonRole.Neutral);
            cancel.Width = 120;
            cancel.DialogResult = DialogResult.Cancel;
            var reset = MakeButton("初期設定に戻す", "ConfirmResetSettingsPage", ButtonRole.Primary);
            reset.Width = 168;
            reset.DialogResult = DialogResult.OK;
            buttons.Controls.AddRange([reset, cancel]);
            content.Controls.Add(buttons);
            Controls.Add(content);
            AcceptButton = CancelButton = cancel;
        }
    }
}
